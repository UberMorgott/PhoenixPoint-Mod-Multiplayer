using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;
using Multiplayer.Network.MessageLayer;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// RESEARCH on the two sanctioned channels only (migration step 1; the 0xAA side channel retired
    /// 2026-07-26 — see the coverage note below).
    ///
    /// HOST → ALL: nothing bespoke. Every research value rides the generic rail (0xAC):
    ///   • element state/progress/flags — ResearchElement._state / ResearchProgress / IsInProgress /
    ///     requirement-instance lists are covered leaves (docs/rail-baseline.txt);
    ///   • queue MEMBERSHIP + ORDER — Research._researchQueue is an ordered keyed EntityCollection;
    ///     its order-vector (DiffEngine.AddKeyOrder) is authoritative for set AND sequence, missing
    ///     members resolve live from the sibling catalog (GenericApplier.ResolveSiblingElement —
    ///     the alias-collection law);
    ///   • Research.Current is DERIVED (decompile Research.cs:53-61: `Current => _researchQueue[0]`),
    ///     so mirroring the queue mirrors it; the serialized CurrentResearch slot is a v&lt;2 save
    ///     migration relic (Research.cs:1263-1279), always null live.
    /// RETIRED 0xAA history: MsgProgress 2026-07-17 (progress = covered leaf), MsgStart/MsgQueue/
    /// MsgComplete 2026-07-26 (state above; presentation = the latch below). Never reuse the surface.
    ///
    /// CLIENT (projector, law 3): the rail applies values; this file keeps only PRESENTATION fed by
    /// the mirrored state (law 4c): <see cref="PresentFromMirror"/> latches viewer-faction transitions
    /// (queue head changed → raise the native OnResearchStarted delegate = geoscape log line; element
    /// entered Completed → native completed modal + log handlers, invoked directly so the reward-chain
    /// subscribers of the faction event never run) plus the open-screen repaint below. Native
    /// presentation timing is matched exactly: the game itself raises OnResearchStarted for every new
    /// queue head (Research.SetNext, decompile :455-458).
    ///
    /// CLIENT → HOST (surface GeoResearchIntent 0xAB): the research screen's entry points
    /// (Research.AddResearchToQueue/Cancel/PutInFromOfQueue/PutUpInQueue/PutDownInQueue/
    /// InsertAtPosition — UIModuleResearch.cs:414-480 all route here) are intent-capture Harmony
    /// prefixes (law 4a): on the client they BLOCK the native call and send an intent via
    /// <see cref="IntentRail"/>; the host op handlers validate → execute the SAME native method → the
    /// rail broadcasts the outcome (IntentRail dispatch FlushNow). Rejects reconverge via a scoped
    /// re-emit of the faction's research subtree.
    ///
    /// SIM GATING (law 4b): Research.Update is skipped on the client (its clock is not frozen — the
    /// local hourly tick would double-apply progress and locally complete research). Host deltas are
    /// the only research progression source on a client.
    /// </summary>
    public static class ResearchSync
    {
        // Intent ops (GeoResearchIntent inner payload) — each maps 1:1 onto the native Research method.
        private const byte OpStart = 1;      // AddResearchToQueue
        private const byte OpCancel = 2;     // Cancel
        private const byte OpFront = 3;      // PutInFromOfQueue
        private const byte OpUp = 4;         // PutUpInQueue
        private const byte OpDown = 5;       // PutDownInQueue
        private const byte OpInsertAt = 6;   // InsertAtPosition(pos)

        // ─── Presentation latch state (client, viewer faction) ─────────────
        private static string _presentedCurrentId;          // last queue head a started-line was shown for
        private static HashSet<string> _presentedCompleted; // null = unseeded (SeedLatchFromMirror fills it)
        // Completions latched but not yet SHOWN, because this peer was not on a map surface when they
        // landed. Deferred, never dropped — drained by PumpDeferredCompletions from the sync tick.
        private static readonly List<string> _deferredCompleted = new List<string>();
        // Has this peer EVER stood on a geoscape map surface in this session? Sticky, one-way.
        private static bool _everOnMapSurface;

        // ─── Reflection (private members only; everything else is typed) ──
        private static readonly System.Reflection.FieldInfo OnResearchStartedField =
            AccessTools.Field(typeof(Research), "OnResearchStarted");
        private static readonly System.Reflection.MethodInfo SetupQueueMethod =
            AccessTools.Method(typeof(UIModuleResearch), "SetupQueue");
        // SetupQueue rebuilds ONLY the queue panel (right side). The AVAILABLE list (left) is a separate
        // pull-model snapshot rebuilt only by ShowAvailable — which the native Cancel/StartResearch handlers
        // ALSO call. Mirror that: a host research delta changes availability (a cancelled research returns to
        // the list, a queued one leaves it), so law 11 requires the list to repaint too, not just the queue.
        private static readonly System.Reflection.MethodInfo ShowAvailableMethod =
            AccessTools.Method(typeof(UIModuleResearch), "ShowAvailable");
        // NO MOD-SERVED RAISE (§A.9, L520). The reflective handles onto GeoscapeView.OnFactionResearchCompleted
        // and GeoscapeLog.Faction_ResearchCompleted are DELETED: a client presents a research completion by
        // draining its window-journal cursor like every other family, never by replaying a private native
        // handler whose arguments it reconstructs and whose failures it swallows into a LogWarning.

        // ─── Lifecycle (driven by SyncEngine) ──────────────────────────────

        /// <summary>Full session teardown. (The intent dedup/nonce live in <see cref="IntentRail"/>.)</summary>
        public static void Reset()
        {
            _everOnMapSurface = false; // a fresh session starts BEFORE this peer's first map surface
            ResetForReloadBoundary();
        }

        /// <summary>
        /// LATCH ONE COMPLETION FOR PRESENTATION — or swallow it. The ONE door into the deferral list, so
        /// the gate cannot be walked around: sticky one-way on this peer's OWN first geoscape map surface,
        /// not a quorum, nothing waits on another human. Returns true when the completion was queued.
        ///
        /// THE BUG (live 2026-08-14). A client joining a FRESH campaign sits in the intro cutscene while the
        /// host grants the whole campaign-opening research list (6 grants across 21:22-21:27 in the reference
        /// session). <see cref="SeedLatchFromMirror"/> ran BEFORE those grants arrived, so every one of them
        /// looked like a genuine transition, got deferred, and then flushed as back-to-back "research
        /// completed" modals the instant the peer first reached UIStateVehicleSelected at 21:27:49 — read by
        /// the player as "all researches completed at once".
        ///
        /// Before that first surface the mirror is still ARRIVING, so a completion there is backlog and is
        /// latched silently (the same swallow the unseeded path uses). After it, behaviour is unchanged and
        /// every real completion still presents — including one that lands while this peer is off the map:
        /// that one is queued through <see cref="PumpDeferredCompletions"/> like any other and held by the
        /// game's own drain gate until this peer is back on the map.
        /// </summary>
        internal static bool LatchCompletion(string researchId, bool onMapSurfaceNow)
        {
            StampMapSurface(onMapSurfaceNow);
            if (!_everOnMapSurface) return false; // backlog: latched in _presentedCompleted, never shown
            _deferredCompleted.Add(researchId);
            return true;
        }

        /// <summary>
        /// RAISE THE STICKY SURFACE FLAG FROM THE CLOCK, NOT FROM THE COMPLETION.
        ///
        /// THE REGRESSION (0525e7a, live 2026-08-14). The flag was stamped ONLY inside
        /// <see cref="LatchCompletion"/>, i.e. only when a completion happened to land while this peer was
        /// standing on a map surface. The client's very first completion arrived 0.4 s after
        /// <c>UIStateGeoscapeEvent</c> opened — a state that is deliberately NOT in
        /// <c>DurableWindowRegistry.MapStates</c> — so it was swallowed AND the flag stayed down, which made
        /// every LATER completion look like backlog too. Research announcements died for the session.
        ///
        /// The surface is a property of WHERE THIS PEER IS, so it is measured where the peer is measured:
        /// the sync tick (<see cref="ClientTick"/>), before any early return. The intro still says nothing
        /// (MayPresent is false there), and one geoscape frame on the map is enough to arm it for good.
        /// </summary>
        internal static void StampMapSurface(bool onMapSurfaceNow)
        {
            if (onMapSurfaceNow) _everOnMapSurface = true;
        }

        /// <summary>Test seam for L475: how many completions are waiting to be shown.</summary>
        internal static int DeferredCompletionCount => _deferredCompleted.Count;

        /// <summary>Mid-session reload boundary (rca-3 contract): drop the presentation latches — the
        /// transferred save replaces the mirror, so the next research delta re-seeds them silently
        /// (no modal/log spam for the backlog, same contract as EventPopup.Reset).</summary>
        public static void ResetForReloadBoundary()
        {
            _presentedCurrentId = null;
            _presentedCompleted = null;
            _deferredCompleted.Clear();
        }

        /// <summary>
        /// SEED THE LATCH FROM THE MIRROR AS IT IS *NOW* — called by <see cref="GenericApplier"/> BEFORE a
        /// batch applies, which is the whole point of it existing.
        ///
        /// THE BUG IT FIXES (live 2026-08-11, host + Instance2 + Instance3). The latch was seeded lazily,
        /// inside the first <see cref="PresentFromMirror"/> after a reset, and that call runs AFTER the
        /// batch — so whatever transition arrived in the first post-boundary batch was indistinguishable
        /// from backlog and was dropped on the floor by design ("a transition inside that same first batch
        /// is deliberately swallowed with it"). That is not a rare race: the geoscape clock is PAUSED
        /// across a mission, so the first research delta after a mission return is whatever completes when
        /// somebody presses play. Measured: clients reloaded at t≈478.7 ("raise of 'PROG_SY0_WIN' HELD —
        /// this peer has no geoscape ready to carry a window yet"), peer 1 resumed the clock at host
        /// t=543.067, and the research completed 240 ms later — host `Queuerd state switch UIStateGeoModal
        /// with priority 99` (= GeoResearchComplete, GeoscapeView.cs:1987-1990) and NEITHER client ever
        /// entered UIStateGeoModal.
        ///
        /// Seeded here, the baseline is the state the peer LOADED, and every later delta is a real
        /// transition. Idempotent and free once seeded (one null check).
        /// </summary>
        internal static void SeedLatchFromMirror(GeoLevelController geo)
        {
            if (_presentedCompleted != null) return;
            var research = geo == null || geo.ViewerFaction == null ? null : geo.ViewerFaction.Research;
            if (research == null || research.AllResearchesArray == null) return;
            var seed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in research.AllResearchesArray)
                if (el != null && el.State == ResearchState.Completed) seed.Add(el.ResearchID);
            _presentedCompleted = seed;
            var head = research.Current;
            _presentedCurrentId = head == null ? null : head.ResearchID;
        }

        /// <summary>Arm the intent surface (0xAB) on the generic engine: transport + dedup + reject
        /// discipline live in <see cref="IntentRail"/>; the ops table below keeps ALL research
        /// validation/native execution here. Reject reconverge = scoped re-emit of the faction's
        /// research subtree (the queue rides the value rail now), passed per-reject below.</summary>
        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>();
            for (byte op = OpStart; op <= OpInsertAt; op++) ops[op] = HandleIntentOp;
            IntentRail.Register(SurfaceIds.GeoResearchIntent, "research", ops);
        }

        // ─── CLIENT: intent capture (fed by the Harmony prefixes below) ────

        /// <summary>
        /// The one intent-capture decision (law 4a + law 8): the shared law
        /// (<see cref="IntentRail.ShouldRunNative"/>) plus the family arms. Returns TRUE = run the native
        /// method (host, no session, apply scope, non-viewer faction), FALSE = blocked on the client and
        /// an intent was sent instead.
        /// </summary>
        private static bool CaptureIntent(Research research, ResearchElement element, byte op, int pos)
        {
            if (IntentRail.ShouldRunNative()) return true;
            if (research?.Faction == null || element == null) return false; // law 3: client never runs native logic — block, nothing to send
            if (!research.Faction.IsViewerFaction) return true;  // NPC sim paths stay native (and un-synced)

            string factionGuid = research.Faction.Def.Guid, researchId = element.ResearchID;
            IntentRail.Send(SurfaceIds.GeoResearchIntent, op, "op=" + op + " research=" + researchId,
                w => { w.Write(factionGuid ?? ""); w.Write(researchId ?? ""); w.Write(pos); });
            return false; // client is a projector: local execution blocked, outcome arrives as host deltas
        }

        // ─── HOST: intent apply (validate → execute NATIVELY; dedup/decode/reject = IntentRail) ──────

        private static void HandleIntentOp(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string factionGuid = WireString.ReadKey(r);
            string researchId = WireString.ReadKey(r);
            int pos = r.ReadInt32();
            string reemit = "F#" + factionGuid + ".Research"; // reject reconverge: the faction's research subtree

            var live = LocateLive(factionGuid, researchId, out var research);
            if (live == null || research == null)
            {
                IntentRail.Reject(SurfaceIds.GeoResearchIntent, senderPeerId,
                    "unknown research " + researchId + " op=" + op, reemit);
                return;
            }
            // Ownership check: LocateLive resolves ANY factionGuid — never let a client intent drive
            // NPC-faction (Anu/NJ/Synedrion/alien) research.
            if (!research.Faction.IsViewerFaction)
            {
                IntentRail.Reject(SurfaceIds.GeoResearchIntent, senderPeerId,
                    "non-player faction " + factionGuid + " op=" + op, reemit);
                return;
            }

            // Validate, then execute the SAME native code the host UI would run — the rail broadcasts
            // the outcome (IntentRail dispatch runs DiffEngine.FlushNow + host-screen MarkDirty).
            bool ok;
            switch (op)
            {
                case OpStart:
                    ok = live.State == ResearchState.Unlocked && research.CanAddToQueue(live);
                    if (ok) research.AddResearchToQueue(live);
                    break;
                case OpCancel:
                    ok = research.ResearchQueue.Contains(live);
                    if (ok) research.Cancel(live);
                    break;
                case OpFront:
                    ok = research.ResearchQueue.Contains(live);
                    if (ok) research.PutInFromOfQueue(live);
                    break;
                case OpUp:
                    // Native guard is only element != Current (Research.cs:424-433): an up-click at
                    // index 1 legally displaces the current head. IndexOf > 0 matches it (-1 = absent).
                    ok = research.ResearchQueue.IndexOf(live) > 0;
                    if (ok) research.PutUpInQueue(live);
                    break;
                case OpDown:
                    ok = research.ResearchQueue.Contains(live) && live != research.Last;
                    if (ok) research.PutDownInQueue(live);
                    break;
                default: // OpInsertAt (the op set is table-gated upstream)
                    ok = research.ResearchQueue.Contains(live) && pos > 0; // never displace the current
                    if (ok) research.InsertAtPosition(live, pos);
                    break;
            }
            if (ok)
                MpLog.Log("[Multiplayer][rail] ResearchSync HOST intent APPLIED op=" + op + " research=" +
                          researchId + " peer=" + senderPeerId);
            else
                IntentRail.Reject(SurfaceIds.GeoResearchIntent, senderPeerId,
                    "invalid state op=" + op + " research=" + researchId, reemit);
        }

        private static ResearchElement LocateLive(string factionGuid, string researchId, out Research research)
        {
            research = null;
            var geo = GenericApplier.GeoLevel();
            if (geo == null) { MpLog.LogWarning("[Multiplayer][rail] ResearchSync: no geoscape level — dropping apply"); return null; }
            var faction = geo.Factions.FirstOrDefault(f => f.Def != null && f.Def.Guid == factionGuid);
            research = faction?.Research;
            var live = research?.AllResearchesArray?.FirstOrDefault(r => r.ResearchID == researchId);
            if (live == null)
                MpLog.LogWarning("[Multiplayer][rail] ResearchSync: live element not found faction=" + factionGuid + " research=" + researchId);
            return live;
        }

        // ─── CLIENT: presentation from the mirror (law 4c; fed by rail deltas via UiEventMap) ────────

        /// <summary>
        /// Present viewer-faction research transitions the RAIL just mirrored — the state itself arrived
        /// as ordinary deltas; this only reacts. Latch semantics: the first fire after a reset seeds
        /// silently from the mirror (reload/join backlog must not spam modals — EventPopup contract), a
        /// transition inside that same first batch is deliberately swallowed with it. Runs inside the
        /// caller's SyncApplyScope (law 8: the raised native events reach capture seams).
        ///   • Completed: the native modal + geoscape log handlers, invoked directly on their private
        ///     methods (raising the faction event would also run GeoscapeEventSystem/stats — host logic).
        ///   • New queue head: raise the Research.OnResearchStarted backing delegate — exactly when the
        ///     game itself raises it (Research.SetNext → StartedResearch, decompile :455-473); GeoFaction
        ///     relays it to the geoscape log.
        /// </summary>
        internal static void PresentFromMirror(GeoLevelController geo)
        {
            var research = geo?.ViewerFaction?.Research;
            if (research == null || research.AllResearchesArray == null) return;
            bool seeded = _presentedCompleted != null;
            if (!seeded) _presentedCompleted = new HashSet<string>(StringComparer.Ordinal);
            // Sticky first-map-surface gate: the campaign-opening grant list arrives while this peer is still
            // in the intro and must latch silently, not queue six modals for the first frame of the map.
            bool onMapSurface = DurableWindowRegistry.MayPresent(true,
                geo.View == null || geo.View.CurrentViewState == null ? null : geo.View.CurrentViewState.GetType());

            foreach (var el in research.AllResearchesArray)
            {
                if (el == null || el.State != ResearchState.Completed || !_presentedCompleted.Add(el.ResearchID))
                    continue;
                if (!seeded) continue; // backlog latched silently (SeedLatchFromMirror normally got there first)
                LatchCompletion(el.ResearchID, onMapSurface);
            }
            // The raise itself is NOT made here: UiEventMap.Fire calls PumpDeferredCompletions right after
            // this, so the one apply-driven raise site stays visible to L49's ordering arm.

            var head = research.Current;
            var headId = head == null ? null : head.ResearchID;
            if (!string.Equals(headId, _presentedCurrentId, StringComparison.Ordinal))
            {
                _presentedCurrentId = headId;
                if (seeded && head != null)
                {
                    try { (OnResearchStartedField?.GetValue(research) as Delegate)?.DynamicInvoke(head); }
                    catch (Exception ex) { MpLog.LogError("[Multiplayer][rail] ResearchSync: OnResearchStarted raise failed: " + ex); }
                    MpLog.Log("[Multiplayer][rail] ResearchSync CLIENT presented start " + headId);
                }
            }
        }

        /// <summary>
        /// QUEUE THE LATCHED COMPLETIONS THROUGH THE GAME'S OWN DRAIN — with NO gate of its own.
        ///
        /// THE DEFECT THIS DELETION FIXES (owner, 2026-08-14; RCA 2026-08-15). Two presentation channels
        /// answered "may this window take the screen now": this method's own
        /// <c>DurableWindowRegistry.MayPresent</c> pre-gate, and the universal one at the engine's single
        /// drain (<see cref="WindowOrder.HoldsForOpenScreen"/>). The HOST has only the second — it raises
        /// natively — so a client sitting in <c>UIStateGeoscapeEvent</c> logged
        /// "deferred 2 completed research window(s)" and QUEUED the window at DRAIN time while the host had
        /// queued it at RAISE time. Different queue positions, hence the measured 90 s order gap: contents
        /// identical, order not. Worse, a drain from <see cref="ClientTick"/> runs with
        /// <c>RailOrdinal.Current == 0</c>, so the window is keyed off this peer's OWN counter instead of
        /// the ordinal of the message that caused it.
        ///
        /// ONE GATE, AND IT IS THE GAME'S. The completion is queued from inside the apply that carried it,
        /// so the host mints its journal position at the one capture seam and every peer keys the window
        /// identically BY CONSTRUCTION (L521/L523). The screen the player opened is still protected —
        /// <see cref="WindowOrder.HoldsForOpenScreen"/> holds <c>UIStateGeoModal</c> at priority 99 (P13),
        /// exactly as it holds every other review window, and releases it when this peer walks back onto
        /// the map. NOT A QUORUM: the hold reads THIS peer's own view state; nothing waits on another human.
        ///
        /// Presentation itself is NOT served here (§A.9, L520): the host raised this window natively, the
        /// capture postfix minted its journal position and published it, and this peer presents it by
        /// draining its cursor like every other family. This pump only retires the deferral row.
        /// </summary>
        internal static void PumpDeferredCompletions(GeoLevelController geo)
        {
            if (_deferredCompleted.Count == 0) return;
            var research = geo == null || geo.ViewerFaction == null ? null : geo.ViewerFaction.Research;
            if (research == null || research.AllResearchesArray == null) return;
            var pending = _deferredCompleted.ToArray();
            _deferredCompleted.Clear();
            foreach (var id in pending)
            {
                // NO MOD-SERVED RAISE (§A.9, L520). Nothing here reflects into a private native handler and
                // nothing here reconstructs a native argument list.
                MpLog.Log("[Multiplayer][rail] ResearchSync completion '" + id + "' is journalled; " +
                          "presentation drains from the window journal cursor");
            }
        }

        /// <summary>The sync-tick drain, and the reason the latch list still exists: a completion that
        /// landed while this peer had no geoscape/viewer faction to raise it off (the only way
        /// <see cref="PumpDeferredCompletions"/> now returns with rows still pending) is queued on the next
        /// tick instead of waiting for a rail batch that may never come — the geoscape clock can be paused
        /// by any peer, and the rail then goes quiet (L412 deferred-window-has-no-drain). Client-only: the
        /// host raises its own natively.</summary>
        public static void ClientTick(NetworkEngine engine)
        {
            // FIRST, above every early return: the flag must rise on the frame this peer stands on the map,
            // whether or not anything is waiting to be shown (see StampMapSurface).
            StampMapSurface(DurableWindowRegistry.MayPresent());
            if (engine == null || !engine.IsActiveSession || engine.IsHost || _deferredCompleted.Count == 0) return;
            PumpDeferredCompletions(GenericApplier.GeoLevel());
        }

        /// <summary>Law 11 repaint entry for the generic rail (UiEventMap): research values changed.</summary>
        internal static void RepaintResearchUi()
        {
            // Screen shut → the top-right agenda tracker is the only thing painting the current research,
            // and it belongs to no view state: OpenUiRepaint owns that refresh for EVERY kind now
            // (research, manufacturing, aircraft actions, facility builds), so there is no research-specific
            // nudge here any more — and none of them waits for the paused game clock either.
            if (!RebuildOpenResearchScreen()) OpenUiRepaint.RefreshPersistentHud();
        }

        // Law 11 (RCA 2026-07-16): UIModuleResearch is a pull-model snapshot — SetupQueue() runs only
        // at Init + its own button callbacks, ResearchQueueItem sets ProgressBar.value only in Init.
        // A delta landing while the research screen is OPEN must rebuild it in place; SetupQueue is
        // idempotent (pure re-read of Research.Current/ResearchQueue). Returns true when the screen
        // was open (tracker nudge then unnecessary: it is Uninit'd during UIStateResearch, and every
        // geoscape state re-entry re-Inits it from Research.Current — decompile UIStateVehicleSelected
        // EnterState / UIStateNothingSelected EnterState — so the return path self-heals natively).
        private static bool RebuildOpenResearchScreen()
        {
            try
            {
                var geo = GenericApplier.GeoLevel();
                var view = geo == null ? null : geo.View;
                if (view == null || !(view.CurrentViewState is UIStateResearch)) return false;
                var module = view.GeoscapeModules == null ? null : view.GeoscapeModules.ResearchModule;
                if (module != null)
                {
                    SetupQueueMethod?.Invoke(module, null);   // queue panel (right)
                    // available list (left) — a cancelled research returns here, a queued one leaves; the
                    // queue panel stays visible under any list tab. ONLY when that list is the one on
                    // screen: see ShowingAvailableList.
                    if (ShowingAvailableList(module)) ShowAvailableMethod?.Invoke(module, null);
                }
                return true;
            }
            catch (Exception ex)
            {
                MpLog.LogWarning("[Multiplayer][rail] ResearchSync: research screen rebuild failed: " + ex.Message);
                return true; // screen was open — a tracker nudge would not help here
            }
        }


        private static readonly System.Reflection.FieldInfo SelectedFilterField =
            AccessTools.Field(typeof(UIModuleResearch), "_selectedButton");
        private static bool _selectedFilterBindLogged;

        /// <summary>
        /// A REPAINT MAY NOT PRESS A TAB THE PLAYER DID NOT PRESS.
        ///
        /// THE REPORT (client, 2026-08-09): "the research screen traps the player — switching tabs inside it
        /// cannot get back". Measured in multiplayer-2-prev4.log: the client sat in <c>UIStateResearch</c>
        /// from 15:02:16 to 15:03:52 with <c>[MP][uirepaint] native rebuild UIStateResearch</c> firing every
        /// one to five seconds throughout, because a live geoscape never stops shipping deltas.
        ///
        /// <c>UIModuleResearch.ShowAvailable</c> IS THE TAB, not a refresh: :297-305 does
        /// <c>CompletedScrollRect.SetActive(false)</c>, <c>AvailableScrollRect.SetActive(true)</c> and
        /// <c>SelectButton(AvailableFilterButton)</c> — it is the handler <c>SetupFilters</c>:289-290 wires
        /// to the Available button's own click. Driving it from the mirror therefore RE-PRESSED that button
        /// once per rail batch: a player who clicked "Researched" or "Alien Researches" was thrown back to
        /// "Available" within a second, every second, for as long as the screen was open. Nothing in the log
        /// said so, because a repaint doing exactly what it was told is not an error.
        ///
        /// SO THE LIST IS REPAINTED ONLY WHILE IT IS THE ONE ON SCREEN, asked of the module's own selection
        /// field (<c>_selectedButton</c>, written by <c>SelectButton</c>:254-268 and by nothing else). The
        /// two completed lists are deliberately left alone rather than repainted through their own handlers:
        /// <c>ShowCompleted</c>:329 slams <c>verticalNormalizedPosition = 1f</c>, so re-driving them once a
        /// second would replace a tab that jumps with a scroll bar that jumps. They are also the two lists a
        /// mirror barely moves — a research completing while a peer reads the completed list re-reads itself
        /// the next time that tab is clicked, which is also all vanilla ever does for them.
        ///
        /// The queue panel above is NOT gated: <c>SetupQueue</c> is read-direction only (it re-reads
        /// <c>Research.Current</c> + the queue and rebuilds rows) and the queue stays visible under every
        /// list tab, so it must keep repainting — that half is law 11 and stays whole.
        /// </summary>
        internal static bool ShowingAvailableList(UIModuleResearch module)
        {
            if (SelectedFilterField == null)
            {
                // Never silent, and it fails back to the OLD behaviour on purpose: a stale list is the bug
                // class this repo fights, a jumping tab is merely rude.
                if (!_selectedFilterBindLogged)
                {
                    _selectedFilterBindLogged = true;
                    MpLog.LogError("[Multiplayer][rail] UIModuleResearch._selectedButton did not bind — the " +
                                   "mirror repaint cannot tell which research filter tab the player is on, so " +
                                   "it keeps re-pressing Available on every rail batch (the tab will jump)");
                }
                return true;
            }
            // Before the first click _selectedButton is null and the Available list is what Init:183 left
            // on screen, so null reads as Available.
            var selected = SelectedFilterField.GetValue(module);
            return selected == null || ReferenceEquals(selected, module.AvailableFilterButton);
        }

        // ─── Harmony seams (the ONLY patches this subsystem owns, law 4) ───

        /// <summary>Intent capture (law 4a): every research-screen entry point on Research.</summary>
        [HarmonyPatch(typeof(Research))]
        internal static class IntentCapturePatches
        {
            [HarmonyPrefix, HarmonyPatch(nameof(Research.AddResearchToQueue))]
            private static bool StartPrefix(Research __instance, ResearchElement research)
                => CaptureIntent(__instance, research, OpStart, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.Cancel))]
            private static bool CancelPrefix(Research __instance, ResearchElement research)
                => CaptureIntent(__instance, research, OpCancel, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutInFromOfQueue))]
            private static bool FrontPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpFront, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutUpInQueue))]
            private static bool UpPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpUp, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.PutDownInQueue))]
            private static bool DownPrefix(Research __instance, ResearchElement element)
                => CaptureIntent(__instance, element, OpDown, 0);

            [HarmonyPrefix, HarmonyPatch(nameof(Research.InsertAtPosition))]
            private static bool InsertAtPrefix(Research __instance, ResearchElement element, int position)
                => CaptureIntent(__instance, element, OpInsertAt, position);

            /// <summary>
            /// OBSERVABILITY ONLY — the host's completion seam had NO log line at all, so "two researches
            /// completed back to back" could not be diagnosed from a host log: the client side names every id
            /// it presents (CLIENT presented complete), the authority named none. One line per completion at
            /// the game's own single completion method (Research.CompleteResearch, decompile :576), carrying
            /// the id and the GEOSCAPE timestamp — that is the clock the propagation burst of
            /// AddProgressToCurrentResearch is measured against, wall-clock alone cannot separate two
            /// completions inside one hourly tick. Not per-frame: it fires exactly once per research, ever.
            /// </summary>
            // Typed on purpose: Research also declares a STATIC console command of the same name
            // (Research.cs:1020, CompleteResearch(IConsole,string,string)) — an untyped patch is an
            // AmbiguousMatchException at bind time, which L125 catches as a harness crash.
            [HarmonyPostfix, HarmonyPatch(nameof(Research.CompleteResearch), new[] { typeof(ResearchElement) })]
            private static void CompletePostfix(Research __instance, ResearchElement research)
            {
                if (research == null || __instance?.Faction == null || !__instance.Faction.IsViewerFaction) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return; // client: PresentFromMirror speaks
                var geo = GenericApplier.GeoLevel();
                MpLog.Log("[Multiplayer][rail] ResearchSync HOST completed " + research.ResearchID +
                          " geoT=" + (geo == null ? -1d : geo.Timing.Now.TimeSpan.TotalSeconds));
            }
        }

        // The research SIM GATE used to live here as a prefix on Research.Update. It moved UP one level to
        // the single funnel that calls it — ClientSimGate.ClientResearchGate on GeoFaction.UpdateResearch —
        // because Research.Update is only HALF the tick: the same funnel also drains the research wallet
        // (GeoFaction.cs:1414) and restamps NextResearchUpdate (:1416), and that stamp is NOT mirrored
        // (rail-baseline.txt:544). Gating the inner half left the outer half free to run with a permanently
        // stale stamp, which is how one completed research burned the WHOLE queue in a frame
        // (Research.AddProgressToCurrentResearch propagates leftover points through every queued element,
        // Research.cs:736-748). One gate, one level up, and NOT a second one here: two gates on the same
        // tick can disagree, and the disagreement was the bug.
    }
}
