using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven GEOSCAPE CUTSCENE mirror. Narrative/story video cutscenes play through the single native
    /// chokepoint <c>GeoscapeView.ToCutsceneState(VideoPlaybackSourceDef, int)</c> (GeoscapeView.cs:672) — reached by
    /// exploration/event reward outcomes (<c>GeoFactionReward.Apply</c> → <c>ToCutsceneState(Cinematic, 100)</c>,
    /// GeoFactionReward.cs:264), research-complete (:2114) and the marketplace. Reward application is
    /// host-authoritative and the client's geoscape sim is frozen + events suppressed, so the client NEVER runs the
    /// reward apply and never plays the cutscene (it was host-only video). This action carries the host's chosen
    /// cutscene to every client so the SAME native playback runs there too.
    ///
    /// Wire payload: <c>string cutsceneGuid</c> (<c>VideoPlaybackSourceDef.Guid</c> — stable across peers via the
    /// shared <c>DefRepository</c>) + <c>i32 priority</c> (the native view-switch priority, preserved verbatim).
    ///
    /// NOTE — deliberately NOT <see cref="IHostOnlyApply"/>: unlike reward-bearing completions, the ONLY effect here
    /// is playing a video (a pure UI view-state push, no sim mutation), and the client has no local originator for it
    /// (its mirrored report-modal DialogCallbacks are nulled precisely because they could fire ToCutsceneState — see
    /// <c>GeoModalDisplay</c>). So the client MUST apply this to see the cutscene; there is no double-play (the host
    /// already played natively and only broadcast; the client plays solely from this mirror).
    /// </summary>
    public sealed class PlayCutsceneAction : ISyncedAction
    {
        private readonly string _cutsceneGuid;
        private readonly int _priority;
        private readonly uint _displaySeq;

        public PlayCutsceneAction(string cutsceneGuid, int priority)
            : this(cutsceneGuid, priority, 0)
        {
        }

        // Batch-3 P4: displaySeq = the host's unified display-order stamp (0 = unstamped/legacy → direct play).
        public PlayCutsceneAction(string cutsceneGuid, int priority, uint displaySeq)
        {
            _cutsceneGuid = cutsceneGuid;
            _priority = priority;
            _displaySeq = displaySeq;
        }

        public string CutsceneGuid => _cutsceneGuid;
        public int Priority => _priority;
        public uint DisplaySeq => _displaySeq;

        public ushort ActionId => SyncedActionIds.PlayCutscene;
        // Presentation ride-along; un-gated on the (never-taken) host-inbound path, non-vehicle so it does not
        // trigger the immediate vehicle-emit. This action only ever flows host → client (BroadcastHostAction).
        public ActionCategory Category => ActionCategory.Dialogs;

        public void Write(BinaryWriter w)
        {
            w.Write(_cutsceneGuid ?? "");
            w.Write(_priority);
            // Batch-3 P4 stamp at the END; an older reader simply leaves the trailing 4 bytes unconsumed
            // (the action payload stream is bounded per action), so the wire stays backward-tolerant.
            w.Write(_displaySeq);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            string guid = r.ReadString();
            int priority = r.ReadInt32();
            // Length-guarded optional tail: a legacy payload without the stamp decodes displaySeq 0.
            uint displaySeq = 0;
            if (r.BaseStream.Length - r.BaseStream.Position >= sizeof(uint))
                displaySeq = r.ReadUInt32();
            return new PlayCutsceneAction(guid, priority, displaySeq);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_cutsceneGuid) && rt != null && rt.IsGeoscapeActive;

        // Client: resolve the SAME VideoPlaybackSourceDef by guid and drive its OWN native
        // GeoscapeView.ToCutsceneState(def, priority) — identical playback to the host. A P4-stamped action
        // first rides the unified display queue (ordered vs mirrored events/report modals); the queue release
        // plays it via the same reflection call. Unstamped/gate-off → the pre-Batch-3 direct play.
        public void Apply(GeoRuntime rt)
        {
            if (_displaySeq != 0 && DisplaySequencerGate.Enabled
                && State.CutsceneDisplayRouter.TryEnqueue(_displaySeq, _priority, _cutsceneGuid))
                return;   // queued — the sequencer releases + plays it in host display order
            CutsceneReflection.PlayGeoscapeCutscene(rt, _cutsceneGuid, _priority);
        }
    }
}
