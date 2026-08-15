using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers;
using PhoenixPoint.Geoscape.View.ViewControllers.Manufacturing;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE POST-MISSION ARRIVAL ARC — window ORDER (S1), window HISTORY across a battle (S3) and the
    /// RESUPPLY screen's two un-captured model writes (S2). One file because all three hang off the same
    /// native moment and two of them share one Harmony prefix.
    ///
    /// ─── S1: ORDER, BY A RANK EVERY PEER COMPUTES ITSELF ───
    /// Measured 2026-08-04: the host queued [event, event, UIStateReplenish] in ONE frame (Player.log:26726-
    /// 26750) while a client queued [UIStateReplenish, event, event] 103 ms apart and had already ENTERED the
    /// resupply screen before either mirrored raise landed. All three at priority 0. There is no host→client
    /// ordering key that could fix that — a peer's own locally-raised windows carry none — so the DEVELOPER
    /// DECISION (2026-08-04) is not "identical to native" but a rule every peer applies to its own queue and
    /// therefore agrees on without talking: THE RESUPPLY SCREEN COMES FIRST, on the host too.
    ///
    /// The mechanism is the game's own and nothing else: <c>QueryStateSwitch</c>:77-82 inserts before the
    /// first STRICTLY lower <c>Priority</c> and <c>GetNextQueriedStateSwitch</c>:111 pops the head, so
    /// priority is the ONLY ordering knob and equal priorities settle by insert order. <c>Priority</c> is
    /// <c>readonly</c> (GeoscapeViewStateSwitchRequest.cs:9), so the prefix hands the game a NEW request
    /// built by its own constructor at the ranked priority — same <c>State</c> instance, <c>PauseGame</c>
    /// carried over, nothing else touched.
    ///
    /// The rank is a DECLARED TABLE keyed by window KIND, never an if-chain of names: anything not named
    /// keeps the game's own priority, which is why this cannot silently re-order the rest of the game.
    ///
    /// ─── S3: HISTORY, BY REUSING THE HELD-RAISE QUEUE ───
    /// A client's post-battle geoscape is rebuilt from the HOST's mid-tactical save (TacticalEntry blocks
    /// client <c>LaunchTacticalGame</c>; <c>PrepareEntryFromBlobCrt</c> lifts the embedded Geoscape section),
    /// so the restored <c>GeoLevelInstanceData.ViewStateSwitchQuery</c> is the HOST's and everything that
    /// peer had queued and not yet read dies with the level it belonged to.
    ///
    /// It is NOT carried as <c>GeoscapeViewStateSwitchRestorableData</c>: those hold a <c>GeoscapeEvent</c>
    /// from the DESTROYED level and <c>RegenerateState</c> would resurrect a stale entity into the new one.
    /// What is carried is the 0xB6 RAISE — the only complete carrier a mirrored window has — through
    /// machinery that already exists: <see cref="EventPopup.RememberUnanswered"/> keeps the last raise per
    /// event id, and the <c>RestoreState</c> postfix below pushes every still-<c>Triggered</c> one back into
    /// <c>EventPopup</c>'s own <c>_held</c> list, which <c>DrainHeldRaises</c> already replays one per frame
    /// onto a live view in the host's arrival order. Zero new restore machinery, and "still Triggered" IS the
    /// unviewed-or-unanswered test — a window another peer answered while we were in the battle is filtered
    /// out by the record, not by a guess.
    ///
    /// ─── S2: THE RESUPPLY SCREEN'S TWO UNCAPTURED WRITES ───
    /// Everything else on that screen already crosses: <c>GeoCharacter.SetItems</c> (EquipSync),
    /// <c>ItemManufacturing.ManufactureItem</c> (ManufactureSync), <c>ItemStorage.RemoveItem</c> (value
    /// rail). These two did not, and REPAIR is the serious one — <c>GeoCharacter.RepairItem</c>:1387 does
    /// <c>Faction.Wallet.Take</c> + <c>RestoreBodyPart</c>, so a client's repair click spent the SHARED
    /// wallet locally and never crossed: a law-3 violation that also silently diverged every peer's money.
    /// RELOAD had the SAME leak through a second door until 2026-08-05 — the capture sat on the row-click
    /// wrapper while the OK button's <c>ReplenishAll</c> called the mutation one level down. It now sits on
    /// that shared choke point (<see cref="ReloadCapturePatch"/>), which is the only place in
    /// <c>UIModuleReplenish</c> that calls <c>Wallet.Take</c> at all — RailCheck L110 keeps that true.
    /// </summary>
    internal static class ReplenishSync
    {
        internal const byte OpRepair = 1;  // [charId:i32][itemDefGuid:string]
        internal const byte OpReload = 2;  // [charId:i32][itemDefGuid:string]

        /// <summary>FIRST — owner decision 2026-08-10 ("Да, первым после миссии"), replacing the 2026-08-04
        /// rank 20. 20 cleared the event family (the raiser gives 0 / 10 / 15,
        /// <c>GeoscapeView.OnGeoscapeEventRaised</c>:2044/:2049/:2057) but deliberately lost to
        /// <c>UIStateGeoCutscene</c> (100, <c>ToCutsceneState</c>) — a cinematic opened ahead of the resupply
        /// screen, which is not the native post-mission flow. <c>int.MaxValue</c> is the ceiling the game
        /// itself uses, and <c>QueryStateSwitch</c>:77-82 inserts before the first STRICTLY lower priority,
        /// so the post-mission outcome modal (<c>UIStateInitial</c>:112, also <c>int.MaxValue</c> and queued
        /// FIRST from the same arrival) still shows first and the resupply screen comes immediately after it,
        /// ahead of every cutscene and every event window. That IS the native order: you-won panel, then
        /// resupply.
        ///
        /// THIS DOES NOT UN-DO THE 2026-08-07 RULING that the resupply screen must not YANK a player out of
        /// a screen he opened. Rank answers "which queued window is next"; the yank is answered by
        /// <c>WindowOrder.HoldsForOpenScreen</c>:294, and <c>UIStateReplenish</c> is named in
        /// <c>WindowOrder.HeldTransitionStates</c> for exactly the reason <c>UIStateRosterDeployment</c>
        /// already was — a transition-band priority that must still wait for the map. L163 arm (d) asserts
        /// that OUTCOME now instead of the rank-below-100 shape it used to assert.</summary>
        internal const int ReplenishRank = int.MaxValue;

        /// <summary>DECLARED display rank per window KIND — the whole S1 rule. A kind that is not named here
        /// keeps the game's own priority untouched, so this table can only ever move what it names.
        ///
        /// <c>UIStateRosterDeployment</c> is deliberately ABSENT: <c>GeoscapeView.ToDeploymentState</c>:596
        /// already queues the squad screen at <c>int.MaxValue</c>, so requirement B ("the preparation window
        /// goes to first place for everyone when someone confirms a start") needs no rank of ours — what it
        /// needed was for the window to EXIST on every peer, which is the shared <c>DeploymentPreparing</c>
        /// occurrence, not a number.</summary>
        private static readonly Dictionary<Type, int> Rank = new Dictionary<Type, int>
        {
            [typeof(UIStateReplenish)] = ReplenishRank,
        };

        /// <summary>The rank this request should ride at, or null to leave the game's own priority alone.
        /// PURE — RailCheck L93 executes it, which is the point: an ordering rule that only ever runs in a
        /// live session is one nobody can falsify.</summary>
        internal static int? RankFor(Type stateType) =>
            stateType != null && Rank.TryGetValue(stateType, out int r) ? (int?)r : null;

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoReplenishIntent, "replenish",
                new Dictionary<byte, IntentRail.OpHandler>
                {
                    [OpRepair] = (engine, peer, nonce, op, r) => HandleRepair(peer, nonce, r),
                    [OpReload] = (engine, peer, nonce, op, r) => HandleReload(peer, nonce, r),
                });
        }

        // ─── S1 + S3 capture: ONE prefix on the game's only queue entry point ───

        /// <summary>Every window in the game is queued through here, so this is where the rank applies.
        /// NEVER blocks — it only re-ranks, and only the kinds <see cref="Rank"/> names.
        ///
        /// It no longer stamps an order key: the client-side ordinal, the settle and the re-sort were
        /// deleted with L522 and the host's journal position (minted in the POSTFIX on this same method,
        /// <c>GeoWindowCoverageGate</c>) is the one ordering authority. The rank still decides ACROSS
        /// priorities (the resupply screen outranks the event family — a product decision); the journal
        /// decides which of two windows the host raised first. Neither can express the other.</summary>
        [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.QueryStateSwitch))]
        internal static class QueueRankPatch
        {
            private static void Prefix(ref GeoscapeViewStateSwitchRequest request)
            {
                var original = request;
                if (original?.State == null) return;
                // Co-op only. The decision is about peers agreeing without talking; a solo player has nobody
                // to agree with, and re-ordering their windows would be an unrequested change to vanilla.
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                int? rank = RankFor(original.State.GetType());
                if (rank != null && rank.Value != original.Priority)
                    // Priority is readonly: hand the game a new request through its OWN constructor rather
                    // than reflecting into the field. Same State instance, so TryGetStateSwitchRequestForState
                    // and every identity check downstream still see the window they expect.
                    request = new GeoscapeViewStateSwitchRequest(original.State, rank.Value)
                    {
                        PauseGame = original.PauseGame,
                    };
            }
        }

        // ─── S3 restore: hand the un-answered raises back to the machinery that already replays them ───

        /// <summary>The geoscape's window queue has just been rebuilt from the save
        /// (<c>GeoscapeView.RestoreState</c>:344, reached from <c>GeoLevelController</c>:691). On a CLIENT
        /// that save is the HOST's, so this peer's own unread windows are not in it — push them back into
        /// <see cref="EventPopup"/>'s held list and let <c>DrainHeldRaises</c> replay them onto the live view.
        /// Host does nothing: its own queue really did persist, and re-raising would double every window.</summary>
        [HarmonyPatch(typeof(GeoscapeView), nameof(GeoscapeView.RestoreState))]
        internal static class CarryUnreadWindowsPatch
        {
            private static void Postfix()
            {
                try
                {
                    var engine = NetworkEngine.Instance;
                    if (engine == null || !engine.IsActiveSession || engine.IsHost) return;
                    int carried = EventPopup.RequeueUnanswered();
                    if (carried > 0)
                        MpLog.Log("[MP][windows] carried " + carried + " unread window(s) across the mission — " +
                                  "this peer's queue came back as the HOST's, so its own unanswered raises are " +
                                  "re-held for replay");
                }
                catch (Exception ex)
                { MpLog.LogError("[MP][windows] carrying unread windows across the mission failed: " + ex); }
            }
        }

        // ─── S4: the resupply GATE is asked again once the returning peer's own state has ARRIVED ───

        /// <summary>Frames left in the post-arrival re-ask window, 0 = disarmed. See
        /// <see cref="ArmArrivalRecheckPatch"/>.</summary>
        private static int _recheckFrames;

        /// <summary>THE CEILING, named — and it is now the ONLY thing that ends an empty re-ask. 3 s at 60 fps:
        /// roughly 7x the slowest post-mission value-rail batch ever measured (445 ms after
        /// <c>UIStateInitial.EnterState</c> on 2026-08-06; 180 ms in the 3-peer session of 2026-08-09), and
        /// short enough that the windows this HOLDS (see <see cref="ResupplyVerdictPending"/>) are held for an
        /// arrival beat rather than for a visible pause. Monotone: it only counts down, from one arming, so
        /// this can never become an unbounded wait and it never reads another peer.
        ///
        /// IT WAS 600 (10 s) WITH AN EARLY DISARM, AND THE DISARM WAS DECORATIVE — deleted 2026-08-09. The
        /// condition was "a rail seq has landed since arming AND 60 frames have passed", on the stated theory
        /// that the seq term told an arrived post-mission batch from unrelated traffic. It does not: the host's
        /// clock ships Timing/Wallet continuously, so in the live 3-peer session BOTH clients disarmed at
        /// exactly frame 60 — the earliest frame the countdown allows — which is only possible if the seq term
        /// was already true and contributed nothing. The effective window was therefore 1 s, not 10, reached
        /// through a test that reads as if it proved something; a batch slower than that second would have lost
        /// the screen for good with nothing but "disarmed early" in the log. One honest number beats a guard
        /// that cannot fail.</summary>
        private const int RecheckFrames = 180;

        /// <summary>HOST: <c>GeoMission.Complete</c> has run and its post-mission writes are in the host's
        /// own model, but not yet on the wire. Cleared by <see cref="HostPostMissionTick"/>.</summary>
        private static bool _commitPending;

        /// <summary>CLIENT: the 0xB2 edge for the mission this peer just came back from has arrived, i.e.
        /// the host's post-mission writes are already applied here. Latched because the edge can beat the
        /// arrival — a client whose geoscape is still being rebuilt receives it before
        /// <c>UIStateInitial.EnterState</c> ever runs. Consumed by whichever of the two gets there second.</summary>
        private static bool _commitArrived;

        /// <summary>Session/level teardown — a live count-down must not survive into the next geoscape.</summary>
        internal static void Reset() { _recheckFrames = 0; _commitPending = false; _commitArrived = false; }

        // ─── S5: THE EDGE — "the host's post-mission writes are committed AND shipped" ───

        /// <summary>HOST ONLY. <c>GeoMission.Complete</c> (GeoMission.cs:267) is the sole writer of the state
        /// the resupply gate reads: <c>ApplyMissionResults</c> reaches
        /// <c>GeoPhoenixFaction.PostmissionReplenish</c> at GeoMission.cs:896. A POSTFIX, because the fact
        /// being announced is "the body has run". Nothing is sent from here — see
        /// <see cref="HostPostMissionTick"/> for why the send has to wait one diff tick.</summary>
        [HarmonyPatch(typeof(GeoMission), nameof(GeoMission.Complete))]
        internal static class HostPostMissionCommitPatch
        {
            private static void Postfix()
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                _commitPending = true;
            }
        }

        /// <summary>HOST ONLY, driven from <c>SyncEngine.Tick</c> IMMEDIATELY AFTER
        /// <c>DiffEngine.HostTick</c> — that placement is the whole mechanism and not a convenience. The
        /// edge must arrive BEHIND the value-rail batch carrying the post-mission writes; the transport is
        /// per-peer ordered, so "sent after" is "arrives after". Sending it from the
        /// <c>GeoMission.Complete</c> postfix instead would put it AHEAD of the very state it announces,
        /// which is the bug it exists to end.</summary>
        internal static void HostPostMissionTick(NetworkEngine engine)
        {
            if (!_commitPending) return;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) { _commitPending = false; return; }
            _commitPending = false;
            try
            {
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoPostMissionCommit, SyncKind.StateDelta, new byte[0])));
                MpLog.Log("[MP][replenish] post-mission writes committed and shipped — 0x" +
                          SurfaceIds.GeoPostMissionCommit.ToString("X2") + " broadcast behind the batch that " +
                          "carries them, so every client can ask GetMissingItems() on a squad that is finally its own");
            }
            catch (Exception ex)
            { MpLog.LogError("[MP][replenish] broadcasting the post-mission commit edge failed — clients fall " +
                             "back to the re-ask ceiling: " + ex); }
        }

        /// <summary>The inbound half. Registered in the geoscape chain so the id is CLAIMED whatever role
        /// this peer has; the host ignores its own echo because its gate was already true at
        /// <c>UIStateInitial</c>:125.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoPostMissionCommit) return false;
            try
            {
                if (engine != null && engine.IsActiveSession && !engine.IsHost) OnPostMissionWritesCommitted();
            }
            catch (Exception ex)
            { MpLog.LogError("[MP][replenish] the post-mission commit edge threw — this peer falls back to the " +
                             "re-ask ceiling: " + ex); }
            return true;
        }

        /// <summary>CLIENT. The host's post-mission writes are applied here now, so ask the game's own
        /// question ONCE and be done — no poll, no ceiling, no guess about the wire. If the geoscape is not
        /// up yet the answer is latched for <see cref="ArmArrivalRecheckPatch"/> to consume on arrival.</summary>
        internal static void OnPostMissionWritesCommitted()
        {
            _commitArrived = true;
            if (TryQueueReplenish("the host's post-mission commit edge")) { _commitArrived = false; _recheckFrames = 0; }
            LogVerdict("the host's post-mission commit edge");
        }

        /// <summary>
        /// THE VERDICT, PER PEER, IN PLAIN TERMS — so "the resupply window did not appear, is that a bug?"
        /// never costs another log dig. It was asked once (2026-08-10 session, three peers) and the answer
        /// had to be reconstructed from a native line in the HOST'S log alone: <c>REPLENISHMENT: restoring
        /// PX_HandGrenade_WeaponDef from storage to …</c>. That line IS the whole rule —
        /// <c>PostmissionReplenishManager.RestoreList</c>:298-318 puts every preferred item back from the
        /// extra pile, then the mission rewards, then the FACTION WAREHOUSE, before anything is allowed to
        /// count as missing (<c>GetMissingItems</c>:336-355 then compares preferred against what the soldier
        /// now holds). A grenade the stores can cover is simply replaced, so nothing is missing and the game
        /// itself raises no window. That is correct vanilla behaviour and not a lost screen.
        ///
        /// Cheap: one <c>GetMissingItems()</c> walk at ONE terminal moment per peer per mission, never in
        /// the per-frame re-ask.
        /// </summary>
        private static void LogVerdict(string because)
        {
            try
            {
                var geo = GenericApplier.GeoLevel();
                if (!(geo?.ViewerFaction is GeoPhoenixFaction px))
                {
                    MpLog.Log("[MP][replenish] VERDICT DEFERRED (" + because + "): this peer's geoscape is not " +
                              "up yet, so there is nobody to ask GetMissingItems(). The answer is latched and " +
                              "reported when this peer arrives.");
                    return;
                }
                int shortCount = px.GetMissingItems().Count;
                MpLog.Log("[MP][replenish] VERDICT (" + because + "): " + shortCount + " soldier(s) short of " +
                          "their preferred loadout — " +
                          (ReplenishQueued()
                              ? "the resupply window IS queued on this peer."
                              : "NO resupply window on this peer, and with 0 short that is the GAME'S OWN " +
                                "answer rather than a lost screen: PostmissionReplenishManager.RestoreList" +
                                ":298-318 refills every preferred item from the extra pile, the mission " +
                                "rewards or the FACTION WAREHOUSE before it can count as missing, so a thrown " +
                                "grenade the stores can cover is simply put back (the game logs its own " +
                                "'REPLENISHMENT: restoring … from storage to …' on the HOST for each one). " +
                                "A non-zero count with no window is the bug shape; 0 is not."));
            }
            catch (Exception ex)
            { MpLog.LogError("[MP][replenish] reporting the resupply verdict failed: " + ex); }
        }

        /// <summary>The game's own gate and the game's own raiser, in one place so the edge, the arrival and
        /// the ceiling all ask exactly the same question. Returns true when the screen was queued.</summary>
        private static bool TryQueueReplenish(string because)
        {
            var geo = GenericApplier.GeoLevel();
            var view = geo?.View;
            if (view == null) return false;
            if (!(geo.ViewerFaction is GeoPhoenixFaction phoenix)) return false;
            if (ReplenishQueued()) return true;
            if (!phoenix.GetMissingItems().Any()) return false;
            view.QueueReplenishState();   // the game's own raiser, at the game's own priority + our rank
            MpLog.Log("[MP][replenish] queued the game's own UIStateReplenish on " + because +
                      " — UIStateInitial:127 could not, because this peer's geoscape was still the " +
                      "pre-battle save when it asked");
            return true;
        }

        /// <summary>IS THE RESUPPLY VERDICT STILL OUT? Read by <c>EventPopup.CanCarryWindow</c>, which is why
        /// it is a property and not a private test: the popup-history drain starts replaying about a second
        /// before this verdict can be reached, and rank 20 cannot preempt a window that is already CURRENT
        /// (<c>GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch</c>:58-63 dequeues only while
        /// <c>_currentStateSwitchRequest == null</c>). So the order has to be kept BEFORE the queue, not in it.
        ///
        /// NOT A QUORUM, and the distinction is the mod's hardest rule. <see cref="_recheckFrames"/> is a
        /// monotone LOCAL countdown with a 180-frame (3 s) ceiling that reads no other peer and waits on no human
        /// action; it HOLDS rather than drops (the drain retries every tick), and it ends by itself whatever
        /// anyone else does. A peer that went AFK changes nothing about when this clears.</summary>
        internal static bool ResupplyVerdictPending => _recheckFrames > 0;

        /// <summary>
        /// THE RESUPPLY SCREEN WAS RESTORED IN 76980f2 AND STILL DID NOT APPEAR, and this is the half that
        /// commit asserted rather than proved. Its gate is the game's own
        /// <c>UIStateInitial.EnterState</c>:125 — <c>ViewerFaction is GeoPhoenixFaction &amp;&amp;
        /// GetMissingItems().Any()</c> — and its verdict there was: "gated on this peer's own
        /// GetMissingItems() over its own aircraft and its own storage, all mirrored, so nothing about it
        /// needs the wire". MIRRORED, YES. ARRIVED, NO.
        ///
        /// MEASURED (2026-08-06 client log, mission return at 21:22:38). A client's post-battle geoscape is
        /// rebuilt from the HOST'S MID-TACTICAL save, so at <c>EnterState</c> every returning soldier still
        /// carries the FULL pre-battle loadout: nothing is missing, nothing is un-full, nothing is damaged,
        /// <c>GetMissingItems()</c> is empty and <c>QueueReplenishState</c>:127 is never called. The writes
        /// that make it non-empty are the host's own <c>PostMissionReplenish</c> / <c>SetItems</c>, and they
        /// arrive on the ordinary 0xAC value rail TWO FRAMES LATER (frame 52446 enters the state; the
        /// post-mission batch applies at 52448). The whole session contains not one
        /// <c>Queuerd state switch … UIStateReplenish</c> line while the SAME branch's custom-mission arm
        /// fired normally — so the branch ran and only this one gate was false.
        ///
        /// THE FIX IS TO ASK THE GAME'S OWN QUESTION AGAIN, not to mirror an answer: arm on arrival, then
        /// re-ask <c>GetMissingItems()</c> each frame until it says yes, and hand the job back to the game's
        /// own <c>QueueReplenishState</c>. Nothing new decides what "missing" means, no wire byte is added,
        /// and the rank/order arm above still places it. HOST DOES NOTHING: its own gate was already true at
        /// <c>EnterState</c>, because its own <c>Complete</c> ran before it.
        /// </summary>
        [HarmonyPatch(typeof(UIStateInitial), "EnterState")]
        internal static class ArmArrivalRecheckPatch
        {
            private static readonly System.Reflection.FieldInfo ParamsField =
                AccessTools.Field(typeof(UIStateInitial), "_params");                 // UIStateInitial.cs:29

            private static void Postfix(UIStateInitial __instance)
            {
                try
                {
                    var engine = NetworkEngine.Instance;
                    // NOT host-gated any more. The ARM below still is (the host's own gate was already true
                    // at EnterState, because its own Complete ran before it), but the VERDICT LINE must
                    // exist on EVERY peer — the 2026-08-10 question "no resupply window, bug or nothing to
                    // replenish?" was unanswerable precisely because the host, the one peer whose gate is
                    // authoritative, logged nothing at all here.
                    if (engine == null || !engine.IsActiveSession) return;
                    if (ParamsField == null)
                    {
                        MpLog.LogError("[MP][replenish] UIStateInitial._params did not resolve — this peer " +
                                       "cannot tell a post-mission arrival from any other, so the resupply " +
                                       "screen is left to the one gate that already misses it.");
                        return;
                    }
                    // THE GAME'S OWN TEST, verbatim (UIStateInitial.cs:102) — the branch whose last statement
                    // is QueueReplenishState. No second opinion about when a mission is over.
                    var prms = ParamsField.GetValue(__instance) as UIStateInitial.Params;
                    var mission = prms == null ? null : prms.LastMission;
                    if (mission == null) return;
                    if (!mission.IsCompleted &&
                        mission.GetMissionOutcomeState() == PhoenixPoint.Tactical.Levels.TacFactionState.Playing)
                        return;

                    var geo = GenericApplier.GeoLevel();
                    var view = geo?.View;
                    if (view == null) return;

                    // THE HOST'S VERDICT AND NOTHING ELSE: its own GeoMission.Complete ran before this
                    // state, so the gate at UIStateInitial:125 already had the true answer and there is
                    // nothing here to arm or re-ask.
                    if (engine.IsHost)
                    { LogVerdict("the game's own gate at UIStateInitial:125, HOST"); return; }

                    // Already queued by the native gate = the state DID arrive in time; re-asking would
                    // double the screen.
                    if (ReplenishQueued()) return;

                    // THE EDGE MAY ALREADY HAVE LANDED. A client that spent longer rebuilding its geoscape
                    // than the host spent shipping the batch receives 0xB2 before it ever enters this state,
                    // and the latch is what stops that from being a lost screen.
                    if (_commitArrived)
                    {
                        _commitArrived = false;
                        if (TryQueueReplenish("the post-mission commit edge that arrived before this peer did"))
                        { _recheckFrames = 0; return; }
                    }
                    _recheckFrames = RecheckFrames;
                    // THE GATE'S OWN ANSWER, at the instant UIStateInitial:125 asked it. Absence of the
                    // screen is not evidence of WHICH clause was false, and a whole session was spent
                    // arguing that from an empty log: 0 here proves the race (the host's post-mission
                    // writes had not landed), non-zero proves the opposite and sends the next reader
                    // somewhere else entirely. Costs one walk, once per mission arrival.
                    int shortNow = geo.ViewerFaction is GeoPhoenixFaction px
                                       ? px.GetMissingItems().Count : 0;
                    MpLog.Log("[MP][replenish] post-mission arrival: the game's own gate saw " + shortNow +
                              " short soldier(s) at UIStateInitial:125" +
                              (shortNow == 0 ? " — i.e. nothing missing YET, because this peer's returning squad " +
                                               "is still the host's pre-battle save" : " — the native raise " +
                                               "should already have happened, so watch which peer queued it") +
                              ". Re-asking the game's own GetMissingItems() for the next " + RecheckFrames +
                              " frames.");
                }
                catch (Exception ex)
                { MpLog.LogError("[MP][replenish] arming the post-mission resupply re-ask failed: " + ex); }
            }
        }

        private static readonly System.Reflection.FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                // GeoscapeView.cs:138 (game typo)

        /// <summary>Is the game's resupply screen already in this peer's window queue? One question, one
        /// place — the gate, the verdict line and the re-ask all have to be asking the same thing.</summary>
        private static bool ReplenishQueued() =>
            SwitchQueryOf(GenericApplier.GeoLevel()?.View)?.TryGetStateSwitchRequestForState<UIStateReplenish>(out _) == true;

        private static GeoscapeViewSwitchQuery SwitchQueryOf(GeoscapeView view) =>
            view == null || SwitchQueryField == null
                ? null
                : SwitchQueryField.GetValue(view) as GeoscapeViewSwitchQuery;

        /// <summary>Driven from <c>SyncEngine.Tick</c>, next to <c>EventPopup.DrainHeldRaises</c> — the other
        /// seam that exists because a client's arrival and its state do not land on the same frame. Disarmed
        /// the moment it queues, and by the ceiling either way.</summary>
        internal static void ClientArrivalTick(NetworkEngine engine)
        {
            if (_recheckFrames <= 0) return;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) { _recheckFrames = 0; return; }
            try
            {
                bool lastFrame = --_recheckFrames <= 0;
                // THE CEILING IS A SAFETY NET NOW, NOT THE MECHANISM (2026-08-10). The terminator is the
                // host's own 0xB2 edge (OnPostMissionWritesCommitted); this poll only covers the case where
                // that message never comes — a host that crashed out of the session mid-arrival, or a peer
                // whose level went live after the broadcast already flew. Without it ResupplyVerdictPending
                // would hold this peer's other windows forever, which is the one thing P13 forbids.
                if (TryQueueReplenish("the re-ask safety net")) { _recheckFrames = 0; return; }
                if (lastFrame)
                {
                    LogVerdict("the re-ask ceiling, before the host's commit edge arrived");
                    MpLog.Log("[MP][replenish] post-mission re-ask expired with nothing missing and no commit " +
                              "edge — either this squad really did come back whole, or 0x" +
                              SurfaceIds.GeoPostMissionCommit.ToString("X2") + " never arrived. Any window " +
                              "this was holding can go through now.");
                }
            }
            catch (Exception ex)
            {
                _recheckFrames = 0;
                MpLog.LogError("[MP][replenish] post-mission re-ask failed — this peer gets no resupply " +
                               "screen for this mission: " + ex);
            }
        }

        // ─── S2 capture: repair (block-first, law 3) ───

        /// <summary>THE seam for repair. <c>GeoCharacter.RepairItem(GeoItem, bool)</c>:1387 is the sole
        /// funnel — the <c>ItemDef</c> overload :1424 is one line delegating into it — and it both SPENDS
        /// (<c>Faction.Wallet.Take</c>:1402) and MUTATES (<c>RestoreBodyPart</c>:1404). Block-first through
        /// the one posture: host and solo run native; a client's click becomes an intent and writes nothing.
        /// The wire carries the ADDRESS only (character + item def); the host re-derives the cost from its
        /// own numbers, which is what stops a client's screen from deciding what a repair costs.</summary>
        [HarmonyPatch(typeof(GeoCharacter), nameof(GeoCharacter.RepairItem), new[] { typeof(GeoItem), typeof(bool) })]
        internal static class RepairCapturePatch
        {
            private static bool Prefix(GeoCharacter __instance, GeoItem item)
            {
                if (IntentRail.ShouldRunNative()) return true;
                string guid = item?.ItemDef?.Guid;
                if (__instance == null || string.IsNullOrEmpty(guid))
                {
                    MpLog.LogWarning("[MP][replenish] client repair DROPPED — no character or no item def to " +
                                     "name in the intent; nothing was written locally either.");
                    return false;
                }
                int charId = (int)__instance.Id;
                IntentRail.Send(SurfaceIds.GeoReplenishIntent, OpRepair,
                    "repair U#" + charId + " " + item.ItemDef.name,
                    w => { w.Write(charId); w.Write(guid); });
                return false;
            }
        }

        /// <summary>THE seam for the reload, and it is the SHARED CHOKE POINT — <c>UIModuleReplenish
        /// .SingleItemReload</c>:234, the ONE method on this screen that calls <c>Wallet.Take</c> (:248) and
        /// <c>CommonItemData.ModifyCharges</c> (:250). It has exactly two callers and BOTH are covered here:
        /// <c>SingleItemReloadAndRefresh</c>:224 (one row's button) and <c>ReplenishAll</c>:288 (the OK
        /// button's batch loop).
        ///
        /// MOVED DOWN 2026-08-05, and the move IS the fix. The capture used to sit on the OUTER
        /// <c>SingleItemReloadAndRefresh</c> because only that one still knew WHICH SOLDIER — and
        /// <c>ReplenishAll</c> calls the inner method DIRECTLY, so a client's OK button ran the mutation
        /// locally: <c>_faction.Wallet.Take(cost, Purchase)</c> out of the SHARED wallet plus a local
        /// <c>ModifyCharges</c>, on every un-full magazine in the returning squad at once. That is the same
        /// law-3 leak <c>GeoCharacter.RepairItem</c> has (the wallet diverges silently and no delta can
        /// correct it, because host state never changed), and patching only the row-click path left every
        /// sibling caller broken — the reason the ceiling was DECLARED rather than fixed was the missing
        /// character, and that turned out to be one lookup away.
        ///
        /// THE SOLDIER, without reflection: the module's own public <c>Items</c> list holds one row per
        /// reloadable item and <c>AddMissingAmmo</c>:576 stamps each row's
        /// <c>ReplenishmentElementController</c> with the character AND the very <c>GeoItem</c> instance the
        /// model list holds (<c>RemoveFromList</c>:365 removes by that same reference, so the identity is the
        /// game's own assumption, not ours). Matching by REFERENCE and not by value is deliberate: GeoItem
        /// overrides <c>Equals</c> by def (GeoItem.cs:124), so two soldiers carrying the same magazine would
        /// otherwise both answer to the first one's row.
        ///
        /// Still NOT captured at <c>CommonItemData.ModifyCharges</c>: that is a general charge write reached
        /// from everywhere, and blocking it would neuter far more than this screen.
        /// ponytail: the OK button now emits one intent per un-full item rather than one batch message. It is
        /// user-gesture rate and bounded by the squad's loadout; give it a batch op only if a live session
        /// shows the burst mattering.</summary>
        [HarmonyPatch(typeof(UIModuleReplenish), "SingleItemReload")]
        internal static class ReloadCapturePatch
        {
            private static bool Prefix(UIModuleReplenish __instance, GeoItem geoItem, ref bool __result)
            {
                if (IntentRail.ShouldRunNative()) return true;
                // FALSE = "this peer reloaded nothing", which is the truth: the row stays in the list and in
                // _missingItems until the host's own reload comes back down the value rail. Reporting true
                // would strike the item off a screen whose model never changed.
                __result = false;
                var character = OwnerOf(__instance, geoItem);
                string guid = geoItem?.ItemDef?.Guid;
                if (character == null || string.IsNullOrEmpty(guid))
                {
                    MpLog.LogWarning("[MP][replenish] client reload DROPPED — this item names no character on " +
                                     "the open resupply screen or has no item def, so no peer could address it; " +
                                     "nothing was written locally either.");
                    return false;
                }
                int charId = (int)character.Id;
                IntentRail.Send(SurfaceIds.GeoReplenishIntent, OpReload,
                    "reload U#" + charId + " " + geoItem.ItemDef.name,
                    w => { w.Write(charId); w.Write(guid); });
                return false;
            }
        }

        /// <summary>Which soldier carries the item the screen is reloading. See the seam's doc: the row
        /// controllers already hold the pairing, by the same reference the model list uses.</summary>
        private static GeoCharacter OwnerOf(UIModuleReplenish module, GeoItem geoItem)
        {
            var rows = module?.Items;
            if (rows == null || geoItem == null) return null;
            foreach (var row in rows)
            {
                var ctrl = row == null ? null : row.GetComponent<ReplenishmentElementController>();
                if (ctrl != null && ReferenceEquals(ctrl.Item, geoItem)) return ctrl.Character;
            }
            return null;
        }

        // ─── S2 host: validate off REPLICATED state, then run the game's own write ───

        private static bool ResolveTarget(ulong peer, BinaryReader r, string what,
                                          out GeoCharacter character, out ItemDef def)
        {
            character = null; def = null;
            int charId = r.ReadInt32();
            string guid = WireString.ReadKey(r);

            var geo = GenericApplier.GeoLevel();
            if (geo == null)
            { Reject(peer, charId, what + ": no geoscape"); return false; }
            if (!(IdentityResolver.Resolve(geo, "U#" + charId, null) is GeoCharacter c))
            { Reject(peer, charId, what + ": unresolved character"); return false; }
            // The money comes out of the character's faction wallet — never let a client intent spend an
            // NPC faction's resources (law 3, same guard as EquipSync's scrap arm).
            if (!ReferenceEquals(c.Faction, geo.PhoenixFaction))
            { Reject(peer, charId, what + ": not a Phoenix soldier"); return false; }
            var d = GeoItemCodec.ResolveDef(guid);
            if (d == null)
            { Reject(peer, charId, what + ": unknown def " + guid); return false; }

            character = c; def = d;
            return true;
        }

        /// <summary>The host runs the GAME'S OWN <c>RepairItem</c> with <c>payCost: true</c> — its own
        /// <c>GetRepairCost</c> over its own equipped-item health, its own wallet. Everything the client
        /// sent was the address. A false return is the game refusing (already whole, or unaffordable), which
        /// is a legitimate outcome and not a protocol error, so it reconverges the peer instead of erroring.</summary>
        private static void HandleRepair(ulong peer, uint nonce, BinaryReader r)
        {
            if (!ResolveTarget(peer, r, "repair", out var character, out var def)) return;
            int charId = (int)character.Id;
            if (!character.RepairItem(new GeoItem(def), payCost: true))
            { Reject(peer, charId, "repair refused by the game — item already whole, or the wallet cannot pay"); return; }
            MpLog.Log("[MP][replenish] HOST repaired " + def.name + " on U#" + charId +
                      " for peer=" + peer + " nonce=" + nonce);
        }

        /// <summary>Reload has NO public model funnel — <c>UIModuleReplenish.SingleItemReload</c>:234 is a
        /// private UI method and the host holds no such module — so these are its six lines re-run against
        /// the host's OWN item, in the same order and off the same predicates (compatible-ammo substitution,
        /// affordability, the Manufacturable tag, the faction knowing how to make it). Kept literal on
        /// purpose: it is the game's arithmetic, not ours.
        /// ponytail: duplicated game logic, the one place in this arc that is. Collapse it the day the game
        /// grows a public reload funnel.</summary>
        private static void HandleReload(ulong peer, uint nonce, BinaryReader r)
        {
            if (!ResolveTarget(peer, r, "reload", out var character, out var def)) return;
            int charId = (int)character.Id;

            var geoItem = character.EquipmentItems.Concat(character.InventoryItems).Concat(character.ArmourItems)
                                   .FirstOrDefault(i => ReferenceEquals(i?.ItemDef, def) &&   // L113: def identity
                                                        i.CommonItemData.CurrentCharges < i.ItemDef.ChargesMax);
            if (geoItem == null)
            { Reject(peer, charId, "reload: U#" + charId + " carries no un-full " + def.name); return; }

            var faction = character.Faction as GeoPhoenixFaction;
            // SingleItemReload:237-240's own substitution. FirstOrDefault rather than the game's IsEmpty()
            // extension so this does not bind to whether the field is an array or a list.
            var ammoDef = def.CompatibleAmmunition?.FirstOrDefault() ?? def;
            float healthPerc = (float)geoItem.CommonItemData.CurrentCharges / geoItem.ItemDef.ChargesMax;
            var cost = GeoCharacter.GetRepairCost(ammoDef, healthPerc);
            var manufacturableTag = GameUtl.GameComponent<SharedData>().SharedGameTags.ManufacturableTag;
            if (faction == null || !faction.Wallet.HasResources(cost) ||
                !ammoDef.Tags.Contains(manufacturableTag) || !faction.Manufacture.Contains(ammoDef))
            { Reject(peer, charId, "reload refused by the game's own test — unaffordable, or " + ammoDef.name +
                                   " is not manufacturable by this faction"); return; }

            faction.Wallet.Take(cost, OperationReason.Purchase);
            geoItem.CommonItemData.ModifyCharges(geoItem.ItemDef.ChargesMax - geoItem.CommonItemData.CurrentCharges,
                                                 canCreateMagazines: true);
            MpLog.Log("[MP][replenish] HOST reloaded " + def.name + " on U#" + charId +
                      " for peer=" + peer + " nonce=" + nonce);
        }

        /// <summary>Reconverge the touched character subtree AND the wallet: a refused repair/reload leaves
        /// the client's screen showing a row it thinks it just fixed, and the wallet is what a wrong repair
        /// would have diverged. "F#" is the faction ROOT PREFIX (IdentityResolver.Roots:245), so it re-emits
        /// every faction subtree rather than one — deliberate: half the reject paths fail BEFORE a character
        /// resolves, so there is no faction to name, and a reject is rare enough that the broader re-emit is
        /// cheaper than carrying two code paths.</summary>
        private static void Reject(ulong peer, int charId, string why) =>
            IntentRail.Reject(SurfaceIds.GeoReplenishIntent, peer,
                              "U#" + charId + " " + why, "U#" + charId, "F#");
    }
}
