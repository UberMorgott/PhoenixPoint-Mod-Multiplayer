using System;
using System.IO;
using System.Text;
using Base.Core;
using Base.Defs;
using Base.UI.VideoPlayback;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// GEOSCAPE CUTSCENE host→all RAISE (surface 0xBA) — the RAISE half
    /// <see cref="GeoWindowCoverage"/> declared owed for <c>UIStateGeoCutscene</c>. A story cinematic fired
    /// on the host and appeared NOWHERE ELSE: not on the clients' screens and not in their window history
    /// (live 2026-08-01), because every raiser reaches the view directly and nothing on the wire named it.
    ///
    /// PRESENTATION, REPLAYED — not shipped state (law 3, and the "native UI first" rule). The wire carries
    /// the <c>VideoPlaybackSourceDef</c>'s GUID and the host's own queue priority, and each peer then runs
    /// its OWN <c>GeoscapeView.ToCutsceneState</c>:672 — the same call the host made, through the same
    /// <c>GeoscapeViewSwitchQuery</c>, so the video plays from that peer's own assets in that peer's own
    /// locale and its queue orders it exactly as the host's did. No video state, no playback position and
    /// no callback crosses.
    ///
    /// THE ONE FUNNEL is <c>ToCutsceneState</c>, and every geoscape cinematic reaches it: a research's
    /// <c>TriggerCutscene</c> (GeoscapeView.cs:2114), a faction reward's <c>Cinematic</c>
    /// (GeoFactionReward.cs:264 — the <c>GeoEventChoiceOutcome</c> field), the marketplace intro/final
    /// (GeoMarketplace.cs:165/:175), the campaign intro (GeoLevelController.cs:743) and the console cheat
    /// (:2186).
    ///
    /// <c>ToGameOverState</c>:662 IS EXCLUDED, and that is why the seam is this method rather than the
    /// <c>UIStateGeoCutscene</c> constructor both share: game-over builds its state with an
    /// <c>EndFromGameOver</c> callback that ENDS THE LEVEL (:666), which is a host outcome and reaches every
    /// peer as the native teardown, not as a video. Patching the funnel the two do NOT share is what keeps
    /// that carve-out mechanical instead of a runtime test.
    ///
    /// Idempotent by seq like the other two raise surfaces (0xB6 events, 0xB7 modals): a re-delivered raise
    /// is a SECOND cinematic, so the strictly-greater guard is the whole of it.
    /// </summary>
    internal static class CutsceneMirror
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>Teardown only, exactly like <see cref="GeoModalMirror.Reset"/>: the seq stream is
        /// per-session on both sides.</summary>
        internal static void Reset() => Seq.Reset();

        private static GeoscapeView View()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>()?.View;
        }

        // ─── HOST: capture at the one funnel (law 4c, presentation) ─────────

        /// <summary>A POSTFIX, not a prefix: the host's own cinematic must run whatever the wire does, and
        /// nothing here blocks or alters it (law 4c — presentation seam). Never throws into game code.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.ToCutsceneState),
                      new[] { typeof(VideoPlaybackSourceDef), typeof(int) })]
        internal static class CutsceneRaiseBroadcast
        {
            private static void Postfix(VideoPlaybackSourceDef cutsceneDef, int priority)
                => HostBroadcast(cutsceneDef, priority);
        }

        internal static void HostBroadcast(VideoPlaybackSourceDef def, int priority)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (SyncApplyScope.Active) return;   // law 8: the apply that replayed a raise never re-broadcasts
            try
            {
                if (def == null || string.IsNullOrEmpty(def.Guid))
                {
                    // A cinematic with no def is unaddressable (law 2) and the peers get nothing — say so,
                    // because "the video played on one screen only" is invisible from the other side.
                    Debug.LogWarning("[MP][cutscene] HOST cinematic NOT mirrored — it carries no " +
                                     "VideoPlaybackSourceDef guid, so no peer can name the same video");
                    return;
                }
                uint seq = Seq.Next(SurfaceIds.GeoCutsceneRaise);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    w.Write(def.Guid);
                    w.Write(priority);
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoCutsceneRaise, SyncKind.StateDelta, inner)));
                Debug.Log("[MP][cutscene] HOST raised '" + def.name + "' seq=" + seq + " priority=" + priority);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][cutscene] HOST raise broadcast FAILED — no peer will see this " +
                               "cinematic: " + ex);
            }
        }

        // ─── CLIENT: replay the cinematic natively ─────────────────────────

        /// <summary>Returns true when the surface was consumed. Client-only: the host already played it.</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoCutsceneRaise) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                uint seq; string guid; int priority;
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    guid = r.ReadString();
                    priority = r.ReadInt32();
                }
                if (!Seq.ShouldApply(SurfaceIds.GeoCutsceneRaise, seq)) return true;
                if (Raise(guid, priority)) Seq.Mark(SurfaceIds.GeoCutsceneRaise, seq);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] CutsceneMirror inbound failed: " + ex); }
            return true;
        }

        /// <summary>Play it through the game's own opener. Returns false when nothing was raised (and always
        /// says why) so the seq stays unmarked and a re-send can still land.</summary>
        private static bool Raise(string guid, int priority)
        {
            var def = string.IsNullOrEmpty(guid) ? null : GameUtl.GameComponent<DefRepository>()?.GetDef(guid);
            var video = def as VideoPlaybackSourceDef;
            if (video == null)
            {
                // Mod parity (law 10) should have blocked the join; a missing video def is not something a
                // later delta heals, so it is an error rather than a wait.
                Debug.LogError("[MP][cutscene] raise DROPPED — def guid " + guid + " is not a live " +
                               "VideoPlaybackSourceDef on this peer (mod/build parity gap)");
                return false;
            }
            var view = View();
            if (view == null)
            {
                // This peer has no geoscape to put a window in (in a battle, mid-load), and windows are not
                // replayed after the fact — same contract as the 0xB6 / 0xB7 raises.
                Debug.LogWarning("[MP][cutscene] raise of '" + video.name + "' DROPPED — this peer has no " +
                                 "live GeoscapeView, and cinematics are not replayed after the fact");
                return false;
            }
            // Law 8: the replay reaches GeoscapeView, whose ToCutsceneState carries OUR host-capture postfix
            // — the scope is what stops a peer from re-broadcasting the raise it just applied.
            using (SyncApplyScope.Enter())
                view.ToCutsceneState(video, priority);
            Debug.Log("[MP][cutscene] played '" + video.name + "' priority=" + priority +
                      " (native replay off this peer's own assets)");
            return true;
        }
    }
}
