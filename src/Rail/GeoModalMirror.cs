using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.Modal;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 presentation for the geoscape MODAL family — the second window kind on the rail, after
    /// <see cref="EventPopup"/>'s 0xB6 event picker, and the one <see cref="GeoWindowCoverage"/> carried as
    /// its biggest declared Gap. The host ships ONE PRESENTATION PAYLOAD per raise (surface
    /// <see cref="SurfaceIds.GeoModalRaise"/> 0xB7) and the client rebuilds the REAL native modal from it.
    ///
    /// WHY A PAYLOAD FAMILY AND NOT THE QUEUE REQUEST. <c>GeoscapeViewStateSwitchRequest</c> carries a live
    /// <c>IState</c> and nothing else, and the state behind it — <c>UIStateGeoModal</c> — is built from a
    /// <c>DialogCallback</c> CLOSURE over the raiser's locals plus an <c>object modalData</c> whose class
    /// differs per <c>ModalType</c> (UIStateGeoModal.cs:65, GeoscapeView.cs:848/:867). Neither is in the save
    /// graph, so law 2 addressing cannot reach them; each kind needs its own description, exactly as the
    /// 0xB6 raise needs its own.
    ///
    /// WHAT MAKES A MODAL MIRRORABLE — the ONE rule, and it is the game's own code that decides it, not this
    /// file: a modal mirrors when its host-side <c>DialogCallback</c> does NOTHING AUTHORITATIVE. Read
    /// <c>GeoscapeView.ModalResultCallback</c>:798 and the set falls out: a mission BRIEF resolves to
    /// <c>LaunchMission</c> / <c>mission.Cancel()</c>, an ability confirmation to <c>GeoAbility.Activate</c>,
    /// a soldier join to <c>reward.Apply</c> — those are host-authoritative decisions and their windows are
    /// declared, not shipped (<see cref="GeoWindowCoverage.DeclaredModals"/> holds all 43 <c>ModalType</c>
    /// members with a reviewed reason each, and RailCheck L49 fails the build for a new one). What is LEFT is
    /// the campaign-progress notification: research complete, shared-research diplomacy, base activated —
    /// windows whose only button is an acknowledgement, raised by a HOST-side faction event the client's own
    /// gated sim never fires.
    ///
    /// THE CLIENT'S COPY IS NON-AUTHORITATIVE BY CONSTRUCTION, and that is the whole safety argument:
    /// <see cref="RaiseMirrored"/> passes <c>dialogHandler: null</c>. Every button on a native modal funnels
    /// through <c>UIModal.Invoke</c> → <c>UIStateGeoModal.FinishDialog</c>:82 → <c>_dialogHandler?.Invoke</c>,
    /// and the Esc/close tail at ExitState:121 is guarded the same way — so with a null handler NO button on
    /// a mirrored modal can run game logic, whatever the prefab wires up. It is also never marked
    /// <c>Persistent</c>: a persistent modal is SAVE-RESTORED through
    /// <c>UIStateGeoModal.RestoreContext.RegenerateState</c>:36, which rebuilds it with the game's OWN
    /// <c>level.View.ModalResultCallback</c> closure — i.e. a reload would hand a client the authoritative
    /// buttons this file just took away. RailCheck L49 asserts both from IL.
    ///
    /// NO DISMISS MESSAGE, ON PURPOSE. A mirrored modal is informational and carries no shared resolution, so
    /// there is no race to arbitrate and no reason for one peer's OK to close the other's window: each peer
    /// dismisses its own copy at its own pace. (That is also the 0xB6 lesson "never hard-dismiss an open
    /// window when the peer can still act", applied by not creating the problem.) A future ACTIONABLE modal
    /// would need the missing half — an intent for its buttons and a host→all hide keyed on
    /// <c>GeoscapeView.ModalClosed</c>:793 — and that is exactly why the brief family is still declared.
    /// </summary>
    internal static class GeoModalMirror
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        private static readonly System.Reflection.FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");   // GeoscapeView.cs:138 (game typo)

        /// <summary>FULL session teardown only — the raise seq is a host monotonic stream and a client
        /// last-writer guard, so a mid-session reload must NOT reset it (rca-3 contract, same as
        /// <see cref="EventPopup.Reset"/>).</summary>
        public static void Reset() => Seq.Reset();

        // ─── THE PAYLOAD (host→all, surface 0xB7) ──────────────────────────

        /// <summary>How <c>modalData</c> is described on the wire. The shape is derived from the RUNTIME type
        /// of the host's own object, never from a static ModalType→class table: the game's own mapping is a
        /// fallback chain (<c>GetMissionBriefModal</c>:1723 ends in "anything else ⇒ BehemothAttackBrief"), so
        /// a table keyed on the enum would go quietly wrong for a modded mission, while the object in hand
        /// cannot. Adding a shape is the extension point; an object no shape covers is
        /// <see cref="Unsupported"/> and says so out loud rather than shipping half a window.</summary>
        internal enum DataShape : byte
        {
            /// <summary>The raise carried no data at all (GeoPhoenixBaseOutcome — GeoscapeView.cs:1965 passes
            /// null). Nothing to resolve, so nothing can fail to resolve.</summary>
            None = 0,
            /// <summary><c>GeoResearchCompleteData</c>: Ref = the research's own faction, Keys[0] = its
            /// <c>ResearchID</c>, Num = <c>SwitchToResearchState</c>. The client re-fetches the LIVE element
            /// off its own mirrored research container — the renderer walks
            /// <c>UnlocksResearches</c>/<c>ManufactureRewards</c> off it
            /// (GeoReseatchCompleteDataBind.cs:97-125), which a synthesized element could not answer.</summary>
            ResearchComplete = 1,
            /// <summary><c>DiplomacyResearchRewardData</c>: Ref = <c>Faction</c>, Keys = every
            /// <c>ResearchID</c> in <c>Researches</c>, Num = <c>DiplomacyShareLevel</c>.</summary>
            DiplomacyReward = 2,
            /// <summary>The host holds an object this file has no description for. Never sent — the host
            /// refuses the raise and logs it, because a modal whose data cannot be rebuilt renders as an empty
            /// prefab full of the designers' baked placeholder text (the 0xB6 half-built-window lesson).</summary>
            Unsupported = 255,
        }

        /// <summary>One modal raise, as the wire carries it. Refs are ordinary rail root refs (law 2 —
        /// <see cref="IdentityResolver.RootRef"/>/<see cref="IdentityResolver.Resolve"/>, the same addressing
        /// the value rail uses); Keys are the game's own stable string ids for what hangs off that root.
        /// No TEXT rides here and that is deliberate: every mirrored modal's renderer paints from the data
        /// object alone (verified: GeoReseatchCompleteDataBind:97 and DiplomacyResearchRewardDataBind:89
        /// touch no GeoLevelController), so the client's OWN defs render in the CLIENT's locale and no
        /// host-resolved string is ever stamped onto shared def state — the failure
        /// <see cref="EventPopup.WithWireTexts"/> exists to work around does not arise here at all.</summary>
        internal struct Raise
        {
            /// <summary>The <c>ModalType</c> as its integer value — mod parity (law 10) makes the enum
            /// identical on every peer, and shipping the int keeps a value the local build does not know
            /// decodable and reportable instead of an exception.</summary>
            public int ModalType;
            public DataShape Shape;
            public string Ref;
            public string[] Keys;
            public int Num;
            /// <summary>The host's OWN queue priority, passed verbatim from the native call
            /// (GeoscapeView.cs:848/:867 hand it to <c>GeoscapeViewStateSwitchRequest</c>). Windows are shown
            /// one at a time out of a PRIORITY-ORDERED queue — <c>QueryStateSwitch</c>:77-82 inserts before
            /// the first LOWER-priority entry — and the raisers really do differ (research complete 99,
            /// diplomacy 100, base activated 0), so mirroring at a fixed 0 would put two peers on different
            /// windows the moment two are pending. Same reason <see cref="EventPopup.Raise.Priority"/>
            /// exists.</summary>
            public int Priority;
        }

        /// <summary>[seq:u32][modalType:i32][shape:u8][ref][n:u16][key × n][num:i32][priority:i32]. Pure both
        /// ways so RailCheck L49 can round-trip it headless — a wire that drops a field silently is a modal
        /// rendered against the wrong faction or shown in the wrong order, and neither shows up in a log.</summary>
        internal static byte[] Encode(uint seq, Raise p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(p.ModalType);
                w.Write((byte)p.Shape);
                w.Write(p.Ref ?? "");
                var keys = p.Keys ?? new string[0];
                w.Write((ushort)keys.Length);
                foreach (var k in keys) w.Write(k ?? "");
                w.Write(p.Num);
                w.Write(p.Priority);
                return ms.ToArray();
            }
        }

        internal static Raise Decode(byte[] payload, out uint seq)
        {
            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                seq = r.ReadUInt32();
                var p = new Raise { ModalType = r.ReadInt32(), Shape = (DataShape)r.ReadByte(), Ref = r.ReadString() };
                var keys = new string[r.ReadUInt16()];
                for (int i = 0; i < keys.Length; i++) keys[i] = r.ReadString();
                p.Keys = keys;
                p.Num = r.ReadInt32();
                p.Priority = r.ReadInt32();
                return p;
            }
        }

        // ─── HOST: describe the modal at the one place it is opened ────────

        /// <summary>Describe the host's live <c>modalData</c> for the wire. Returns
        /// <see cref="DataShape.Unsupported"/> for anything no shape covers — the caller then declines the
        /// raise loudly instead of shipping a modal the client would render empty.</summary>
        internal static Raise Describe(object modalData)
        {
            switch (modalData)
            {
                case null:
                    return new Raise { Shape = DataShape.None, Ref = "", Keys = new string[0] };
                case GeoResearchCompleteData d:
                    var el = d.ResearchElement;
                    return new Raise
                    {
                        Shape = DataShape.ResearchComplete,
                        Ref = IdentityResolver.RootRef(el?.Faction) ?? "",
                        Keys = new[] { el?.ResearchID ?? "" },
                        Num = d.SwitchToResearchState ? 1 : 0,
                    };
                case DiplomacyResearchRewardData d:
                    return new Raise
                    {
                        Shape = DataShape.DiplomacyReward,
                        Ref = IdentityResolver.RootRef(d.Faction) ?? "",
                        Keys = (d.Researches ?? Enumerable.Empty<ResearchElement>())
                               .Select(r => r?.ResearchID ?? "").ToArray(),
                        Num = d.DiplomacyShareLevel,
                    };
                default:
                    return new Raise { Shape = DataShape.Unsupported, Ref = "", Keys = new string[0] };
            }
        }

        /// <summary>Host-side broadcast of ONE modal, called from the postfixes on the two native openers.
        /// Never throws into game code: a raise this fails on is a window the client does not get, and it
        /// says so.</summary>
        internal static void HostBroadcast(ModalType modalType, object modalData, int priority)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (SyncApplyScope.Active) return;   // law 8: an apply that reaches the view never re-broadcasts
            var rule = GeoWindowCoverage.RuleForModal(modalType);
            if (rule == null || rule.Sync != WindowSync.Mirrored) return;  // declared: the gate already announced it
            try
            {
                var p = Describe(modalData);
                p.ModalType = (int)modalType;
                p.Priority = priority;
                if (p.Shape == DataShape.Unsupported)
                {
                    Debug.LogError("[MP][modals] '" + modalType + "' is DECLARED Mirrored but its modalData is a " +
                                   (modalData == null ? "null" : modalData.GetType().FullName) + ", which " +
                                   "GeoModalMirror.Describe has no shape for — the client gets NO window. Add the " +
                                   "shape, or move the declaration to LocalOnly/Gap with the reason");
                    return;
                }
                if (p.Shape != DataShape.None && string.IsNullOrEmpty(p.Ref))
                {
                    Debug.LogError("[MP][modals] '" + modalType + "' NOT mirrored — its data has no rail root ref on " +
                                   "the host (shape=" + p.Shape + "), so the client would have nothing to resolve it " +
                                   "against and would render the prefab's placeholder text");
                    return;
                }
                uint seq = Seq.Next(SurfaceIds.GeoModalRaise);
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoModalRaise, SyncKind.StateDelta, Encode(seq, p));
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][modals] HOST raised '" + modalType + "' seq=" + seq + " shape=" + p.Shape +
                          " ref=" + (p.Ref == "" ? "none" : p.Ref) + " keys=" + p.Keys.Length +
                          " num=" + p.Num + " priority=" + p.Priority);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][modals] HOST raise broadcast FAILED for '" + modalType + "' — no peer will see " +
                               "this window: " + ex);
            }
        }

        // ─── CLIENT: rebuild the data and push the NATIVE window ───────────

        /// <summary>THE validity decision, PURE so RailCheck L49 can falsify it headless: may this payload
        /// become a window on this peer? Null = yes. A ref or a key the host SHIPPED but this peer cannot
        /// resolve is the killer: the modal prefab's data-bind casts <c>modal.Data</c> and dereferences it
        /// unguarded (GeoReseatchCompleteDataBind.cs:97 reads <c>ResearchElement.GetLocalizedName()</c>
        /// straight off), the throw lands INSIDE <c>UIStateGeoModal.EnterState</c>, and what stays on screen
        /// is a half-built prefab over the designers' baked placeholder text — the exact 0xB6 failure mode,
        /// with the same "logged as a success" property. <see cref="DataShape.None"/> never refuses: a
        /// data-less modal legitimately has nothing to resolve and the host rendered it that way too.</summary>
        internal static string DataRefusal(DataShape shape, bool rootResolved, int keysWanted, int keysResolved)
        {
            if (shape == DataShape.None) return null;
            if (!rootResolved)
                return "the host raised it for a faction/root this peer cannot resolve — the rebuilt modal data " +
                       "would be null and the prefab's data-bind casts and dereferences it unguarded inside " +
                       "EnterState, leaving a half-built window over placeholder text";
            if (keysWanted <= 0)
                return "the host shipped no ids for a " + shape + " payload, which needs at least one — a modal " +
                       "rendered off an empty element list is an empty prefab";
            if (keysResolved != keysWanted)
                return "only " + keysResolved + " of " + keysWanted + " shipped ids resolve on this peer (mod " +
                       "parity: law 10 should have blocked the join) — a modal missing part of what the host " +
                       "showed is a different window, not the same one";
            return null;
        }

        /// <summary>Returns true when the surface was consumed. Client-only: the host never applies its own
        /// raise (it already showed the window natively).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoModalRaise) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                var p = Decode(payload, out uint seq);
                // A re-delivered raise is a SECOND window, not a stale value — the strictly-greater guard is
                // what makes this surface idempotent (law 7), and it is marked only after the window is up.
                if (!Seq.ShouldApply(SurfaceIds.GeoModalRaise, seq)) return true;
                if (RaiseMirrored(p, seq)) Seq.Mark(SurfaceIds.GeoModalRaise, seq);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] GeoModalMirror inbound failed: " + ex); }
            return true;
        }

        /// <summary>Rebuild the host's modal here and push the NATIVE view state through the game's own switch
        /// query — the same call <c>GeoscapeView.OpenModal</c>:876 makes, with the host's own priority so this
        /// peer's queue orders it exactly as the host's did. Returns false when nothing was raised (and always
        /// says why).</summary>
        private static bool RaiseMirrored(Raise p, uint seq)
        {
            var modalType = (ModalType)p.ModalType;
            var geo = GeoLevel();
            var view = geo?.View;
            if (view == null || !(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q))
            {
                // Not "later": this peer has no geoscape to put a window in (tactical mission, mid-load), and
                // there is no history to replay it from. Dropped, loudly — same contract as a 0xB6 raise.
                Debug.LogWarning("[MP][modals] raise of '" + modalType + "' DROPPED — this peer has no live " +
                                 "GeoscapeView to show it in, and windows are not replayed after the fact");
                return false;
            }
            var rule = GeoWindowCoverage.RuleForModal(modalType);
            if (rule == null || rule.Sync != WindowSync.Mirrored)
            {
                // Both peers run the same DLL, so this means the SENDER's table and ours disagree — a mod/
                // build mismatch law 10 should have caught. Refuse rather than open a window nobody reviewed.
                Debug.LogError("[MP][modals] raise of '" + modalType + "' REFUSED — this peer's " +
                               "GeoWindowCoverage.DeclaredModals does not declare it Mirrored (" +
                               (rule == null ? "undeclared" : rule.Sync.ToString()) + "), so the peers are not " +
                               "running the same coverage table");
                return false;
            }

            var data = BuildData(geo, p, out string refusal);
            if (refusal != null)
            {
                Debug.LogError("[MP][modals] raise of '" + modalType + "' REFUSED — " + refusal);
                return false;
            }

            // dialogHandler: null is THE safety property of this family (see the class doc) — with it, every
            // button funnels into UIStateGeoModal.FinishDialog:82 -> `_dialogHandler?.Invoke` and does nothing
            // but close this peer's own copy. Persistent stays FALSE (never set): a persistent modal is
            // save-restored with the game's own authoritative ModalResultCallback closure (RestoreContext:36).
            var state = new UIStateGeoModal(modalType, null, data);
            q.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(state, p.Priority)
            // The GAME'S OWN flag, true as on the host: a mirrored modal must pause THIS peer, which is what
            // ProcessQueriedStateSwitch:67-70 -> RequestGamePause:1269 does. It used to be false on the theory
            // that pause arrives via the rail — it does not when the host is already paused (change-gated
            // Timing.Paused setter → no delta), which is how a client kept running under an open window.
            // A ONE-SHOT pause, not a hold: any peer resumes unconditionally, first-to-act-wins.
            { PauseGame = true });
            Debug.Log("[MP][modals] raised '" + modalType + "' seq=" + seq + " shape=" + p.Shape +
                      " priority=" + p.Priority + " data=" + (data == null ? "none" : data.GetType().Name));
            return true;
        }

        /// <summary>Resolve the shipped refs against THIS peer's live graph and rebuild the concrete
        /// <c>modalData</c> object the prefab's data-bind expects. Every element comes out of the client's own
        /// mirrored containers, so the window renders this peer's defs in this peer's locale.</summary>
        private static object BuildData(GeoLevelController geo, Raise p, out string refusal)
        {
            refusal = null;
            if (p.Shape == DataShape.None) return null;
            var faction = IdentityResolver.Resolve(geo, p.Ref, null) as GeoFaction;
            var keys = p.Keys ?? new string[0];
            var found = new List<ResearchElement>(keys.Length);
            if (faction?.Research != null)
                foreach (var id in keys)
                {
                    var el = string.IsNullOrEmpty(id) ? null : faction.Research.GetResearchById(id);
                    if (el != null) found.Add(el);
                }
            refusal = DataRefusal(p.Shape, faction != null, keys.Length, found.Count);
            if (refusal != null) return null;
            switch (p.Shape)
            {
                case DataShape.ResearchComplete:
                    return new GeoResearchCompleteData
                    {
                        ResearchElement = found[0],
                        // The host's own flag would send the CLIENT to its research screen when the host asked
                        // for it — a navigation decision belongs to the peer looking at the window, and every
                        // shipped raiser passes false anyway (GeoscapeView.cs:1985).
                        SwitchToResearchState = false,
                    };
                case DataShape.DiplomacyReward:
                    return new DiplomacyResearchRewardData
                    {
                        Faction = faction,
                        Researches = found,
                        DiplomacyShareLevel = p.Num,
                    };
                default:
                    refusal = "shape " + p.Shape + " has no client-side builder — the sending peer knows a shape " +
                              "this build does not, which is a version/mod parity break (law 10)";
                    return null;
            }
        }

        private static GeoLevelController GeoLevel()
        {
            try { return Base.Core.GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// HOST capture seam (law 4a) for the modal family, half one of two: <c>GeoscapeView.OpenModal</c>:867.
    /// A POSTFIX, so the host's own window is queued exactly as it always was and the mirror is a pure
    /// addition.
    ///
    /// The <c>forceOnTop</c>/<c>replaceTop</c> branches are DELIBERATELY not mirrored, and it is the same
    /// partition <see cref="GeoWindowCoverage"/> already draws: those two push straight onto
    /// <c>_statesStack</c> (:871/:876) instead of the queue, which is what the game does for the LOCAL player
    /// navigating their own geoscape — the soldier-edit pickers (UIStateEditSoldier.cs:670/:707) and the demo
    /// end screen. A window that never reaches <c>QueryStateSwitch</c> is not a window the game is
    /// interrupting the player with.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), "OpenModal", new[]
    {
        typeof(ModalType), typeof(DialogCallback), typeof(object), typeof(int), typeof(bool), typeof(bool),
    })]
    internal static class GeoModalOpenMirror
    {
        private static void Postfix(ModalType modalType, object modalData, int priority,
                                    bool forceOnTop, bool replaceTop)
        {
            if (forceOnTop || replaceTop) return;   // local navigation, not a pushed window (see the class doc)
            GeoWindowCoverage.AnnounceModal(modalType);
            GeoModalMirror.HostBroadcast(modalType, modalData, priority);
        }
    }

    /// <summary>
    /// HOST capture seam (law 4a), half two: <c>GeoscapeView.OpenModalPersistent</c>:848 — the other of the
    /// only two methods in the shipped game that construct a <c>UIStateGeoModal</c> (the third construction,
    /// <c>RestoreContext.RegenerateState</c>:36, is the save-restore path and re-raises a window this peer
    /// already had). It always queues, so there is no local-navigation branch to skip here.
    ///
    /// Nothing declared Mirrored comes through this opener today — <see cref="GeoModalMirror.HostBroadcast"/>
    /// declines every LocalOnly/Gap kind — but the ANNOUNCE must run for both, or a new modal kind would be
    /// invisible on whichever opener it happened to pick.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), "OpenModalPersistent", new[] { typeof(ModalType), typeof(object), typeof(int) })]
    internal static class GeoModalOpenPersistentMirror
    {
        private static void Postfix(ModalType modalType, object modalData, int priority)
        {
            GeoWindowCoverage.AnnounceModal(modalType);
            GeoModalMirror.HostBroadcast(modalType, modalData, priority);
        }
    }
}
