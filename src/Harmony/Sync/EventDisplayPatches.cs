using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Host-authoritative geoscape EVENT-DIALOG display sync (additive; the answer-relay
    /// <c>CompleteEventPatch</c> / <c>AnswerEventAction</c> are untouched).
    ///
    /// The client never simulates the geoscape, so it never raises the event locally
    /// (<c>GeoscapeView.OnGeoscapeEventRaised</c> GeoscapeView.cs:2109 never runs → no dialog). The host
    /// does, so we:
    ///   • postfix <c>OnGeoscapeEventRaised(GeoscapeEvent)</c> → host broadcasts <c>EventRaised(id, siteId)</c>;
    ///     clients reconstruct + push the dialog (SyncEngine.OnEventRaised, PauseGame=false).
    ///   • postfix <c>GeoscapeEvent.CompleteEvent(GeoEventChoice, GeoFaction)</c> (the authoritative answer
    ///     apply, GeoscapeEvent.cs:86) → host broadcasts <c>EventDismiss(id)</c>; clients close the dialog.
    ///     This one method is the single host chokepoint for BOTH answer origins: host-pick lets the original
    ///     CompleteEvent run (CompleteEventPatch returns true for host), and a client-pick is applied on the
    ///     host via AnswerEventAction.Apply → EventReflection.CompleteEvent → the real CompleteEvent. The
    ///     dialog is closed by the UI module's EncounterFinished, NOT by CompleteEvent, so the explicit
    ///     EventDismiss is required to close a client's open dialog.
    /// All guarded host-only + not-applying; best-effort try/catch — never throws into game code.
    /// </summary>
    [HarmonyPatch]
    public static class EventRaisedDisplayPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.GeoscapeView");
            var evtT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            if (t == null || evtT == null) return false;
            _target = AccessTools.Method(t, "OnGeoscapeEventRaised", new[] { evtT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        /// <summary>
        /// CLIENT guarantee (load-bearing): block any client-LOCAL geoscape-event dialog. The client's
        /// geoscape sim still ticks (the time-sync clock overwrite drives <c>LevelHourlyUpdateCrt</c>),
        /// so the client's own <c>GeoscapeEventSystem</c> can still raise events locally and pop a dialog
        /// that DIFFERS from the host's. Clients must only ever show events the HOST broadcasts
        /// (<c>SyncEngine.OnEventRaised</c> → <c>EventDisplay.Show</c>, which builds the
        /// <c>UIStateGeoscapeEvent</c> directly via <c>QueryStateSwitch</c> and does NOT route through
        /// <c>OnGeoscapeEventRaised</c> — VERIFIED EventDisplay.cs:82-101 / SyncEngine.cs:268-280), so
        /// skipping the native body here never blocks a host-broadcast dialog. Belt to #1's suspenders
        /// (<c>EventSuppressClientGeoscapePatch</c> sets <c>SuppressEvents=true</c> at source): this prefix
        /// survives even if that flag is reset by a full-state reload before the re-assert runs. Returns
        /// false (skip native) only on a CLIENT in an active co-op session; the HOST is untouched.
        /// </summary>
        public static bool Prefix()
        {
            try
            {
                if (SyncApplyScope.IsApplying) return true;   // engine-driven client reconstruction → never block
                var engine = NetworkEngine.Instance;
                if (engine != null && engine.IsActiveSession && !engine.IsHost)
                    return false;                             // client: no local dialog ever
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventRaisedDisplayPatch.Prefix failed: " + ex.Message); }
            return true;                                       // host (and any failure): native runs
        }

        // geoEvent = the raised GeoscapeEvent (the patched method's sole arg).
        public static void Postfix(object geoEvent)
        {
            if (SyncApplyScope.IsApplying) return;   // never re-broadcast a reconstructed/echoed event
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                string eventId = EventReflection.GetEventId(geoEvent);
                if (string.IsNullOrEmpty(eventId)) return;
                // EXCLUDE mission-arrival/deploy prompts (ANY choice starts a tactical mission) from the client
                // mirror. These are transient host-side PRE-DECISION windows (vehicle arrives → "Deploy / Leave"):
                // a host cancel closes only on the host, but the client — having mirrored the raise — would
                // synthesize a phantom result page and get stuck (in-game leak: PROG_AN2_MISS "Второе посвящение",
                // subtitle "МЕСТНОСТЬ РАЗВЕДКИ"). The client = mirror of COMMITTED state only; the deploy decision
                // is host-local and the mission (on Deploy) arrives via the tactical deploy channel, not this rail.
                // Skip the raise at the SOURCE (host never broadcasts it) — verified before occId is assigned, so
                // no orphan occurrence. Fail-open: an unreadable event is broadcast as before (no legit-event regression).
                if (EventReflection.IsMissionDeployEvent(geoEvent))
                {
                    Debug.Log("[Multiplayer] HOST skip BroadcastEventRaised eventId=" + eventId +
                              " (mission-deploy prompt — client never mirrors an arrival deploy/land confirmation)");
                    return;
                }
                int siteId = EventReflection.GetSiteId(geoEvent);
                // Carry the visiting vehicle id so the client rebuilds the SAME 3-arg context. Without it the
                // client's context has Vehicle == null and an [AircraftName]-token description NREs inside the
                // native UIModuleSiteEncounters render → prefab-placeholder text + raw sample choice buttons.
                int vehicleId = EventReflection.GetVehicleId(geoEvent);
                // Per-occurrence id for THIS live GeoscapeEvent instance (no native one exists). GetOrAssign is
                // order-independent: for a single-choice event the host CompleteEvent's (dismiss broadcast) at
                // trigger time BEFORE this raise runs (GeoscapeEventSystem.OnEventTriggered:659→661, same
                // instance), so the dismiss may allocate the id first and this raise REUSES it — keeping the
                // wire-level raise↔dismiss correlation key identical regardless of which fired first.
                ushort occId = EventOccurrenceIds.GetOrAssign(geoEvent);
                // Snapshot the event's GeoSite identity (Owner/Type/State/name/encounter) so a client MISSING
                // this site degrades to graceful siteless text instead of the StartingBase "Точка Феникс". Only
                // built when the event has a real site (siteId >= 0); siteless events carry no identity.
                GeoSiteState? identity = null;
                if (siteId >= 0)
                {
                    try
                    {
                        object liveSite = EventReflection.GetSite(geoEvent);
                        identity = Multiplayer.Network.Sync.State.GeoSiteReflection.BuildIdentity(GeoRuntime.Instance, liveSite);
                    }
                    catch (Exception iex) { Debug.LogError("[Multiplayer] EventRaisedDisplayPatch identity-snapshot failed: " + iex.Message); }
                }
                // HasSingleChoice (Choices.Count <= 1, mirrors GeoscapeEventData.HasSingleChoice): such an event is
                // auto-completed by the host at trigger, so its result-bearing dismiss precedes this raise. Stamp it
                // on the wire so the client MIRRORS the host's native flavor modal instead of jumping to a synthetic
                // result page (a different dialog STAGE than the host shows). Unreadable count (-1) → multi (false),
                // matching the lock-side fail-safe (an ambiguous event is treated as a locked CHOICE, not single).
                int choiceCount = EventReflection.GetChoiceCount(geoEvent);
                bool singleChoice = choiceCount >= 0 && choiceCount <= 1;
                // 1-WINDOW discriminator (mirrors native IsSingleChoiceEncounter(), UIModuleSiteEncounters.cs:256-262):
                // a single choice with EMPTY outcome text → the host shows reward+narrative in ONE combined window. Stamp
                // it so the client skips the phantom reward-less prompt and resolves straight to the result page. A
                // 2-window single-choice-WITH-outcome (oneWindow=false) keeps the prompt-mirror+advance lockstep.
                bool oneWindow = EventReflection.IsOneWindowSingleChoice(geoEvent);
                // Host-resolved WIRE TEXTS (title + raise narrative, the strings the native window actually
                // renders: Title.Localize() / Description.Last().GetText(context)). Runtime-narrative defs
                // (TFTV VoidOmen_{0..19}: empty loc keys, text exists only as a HOST-side def mutation) resolve
                // to "" on the client, so the client prefers these non-empty wire strings over its local def.
                string wireTitle = EventReflection.ResolveLiveTitle(geoEvent);
                string wireNarrative = EventReflection.ResolveLiveNarrative(geoEvent);
                // Batch-3 P4: consume the display-order stamp recorded by ViewSwitchQueryStampPatch when the
                // native OnGeoscapeEventRaised body pushed the UIState(Marketplace)GeoscapeEvent — same call
                // stack, so the wire carries the exact seq + native priority (TriggeredByEvent 10 / plain 0 /
                // completed-upgrade 15) the host's own queue used. Fallback (stamp patch unbound): a fresh seq
                // at broadcast time keeps a consistent FIFO order, priority 0.
                uint displaySeq = 0;
                int nativePriority = 0;
                if (Multiplayer.Network.Sync.DisplaySequencerGate.Enabled
                    && !Multiplayer.Network.Sync.State.DisplayStamp.TryTake("GeoscapeEvent", out displaySeq, out nativePriority))
                    displaySeq = Multiplayer.Network.Sync.State.DisplaySequence.NextSeq();
                Debug.Log("[Multiplayer] HOST BroadcastEventRaised occId=" + occId + " eventId=" + eventId +
                          " siteId=" + siteId + " vehicleId=" + vehicleId + " hasIdentity=" + identity.HasValue +
                          " singleChoice=" + singleChoice + " oneWindow=" + oneWindow +
                          " titleLen=" + (wireTitle?.Length ?? 0) + " narrLen=" + (wireNarrative?.Length ?? 0) +
                          " displaySeq=" + displaySeq + " nativePriority=" + nativePriority);
                engine.Sync?.BroadcastEventRaised(occId, eventId, siteId, vehicleId, identity, singleChoice, oneWindow, wireTitle, wireNarrative, displaySeq, nativePriority);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventRaisedDisplayPatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Host: after the authoritative <c>GeoscapeEvent.CompleteEvent</c> applies, tell clients to close
    /// their open dialog (clients never run CompleteEvent's UI-close path). Host-only so the client's own
    /// CompleteEvent replay (under SyncApplyScope, during AnswerEventAction echo) does not re-broadcast.
    /// </summary>
    [HarmonyPatch]
    public static class CompleteEventDismissPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            var facT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            if (t == null || choiceT == null || facT == null) return false;
            _target = AccessTools.Method(t, "CompleteEvent", new[] { choiceT, facT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the GeoscapeEvent that was just answered.
        public static void Postfix(object __instance)
        {
            if (SyncApplyScope.IsApplying) return;   // engine-driven replay → don't re-broadcast a dismiss
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                string eventId = EventReflection.GetEventId(__instance);
                // SYMMETRIC to the raise-side exclusion: never broadcast a dismiss for a mission-deploy prompt the
                // client was never shown (the raise was skipped) — it would buffer as an orphan pending-dismiss.
                // The arrive→cancel/deploy decision is host-local; on Deploy the mission rides the tactical channel.
                if (EventReflection.IsMissionDeployEvent(__instance))
                {
                    Debug.Log("[Multiplayer] HOST skip BroadcastEventDismiss eventId=" + eventId +
                              " (mission-deploy prompt — not mirrored to client)");
                    return;
                }
                // The picked choice's index (off GeoscapeEvent.SelectedChoice, set inside CompleteEvent). >= 0
                // tells clients to rebuild + show that choice's follow-up RESULT/OUTCOME page natively; a null/
                // decline choice resolves to -1 → close-only. The reward STATE already syncs via the channels.
                int choiceIndex = EventReflection.GetSelectedChoiceIndex(__instance);
                // Snapshot the reward DISPLAY lines (resources / diplomacy / items / units / revealed sites /
                // soldier dmg+tired / skillpoints / …) the native ShowReward draws, so the client mirrors the
                // delta lines on its result card. Read-only — does NOT re-apply (host already applied).
                byte[] rewardBlob = null;
                try
                {
                    var reward = EventReflection.GetChoiceReward(__instance);
                    if (reward != null)
                    {
                        var snap = Multiplayer.Network.Sync.State.RewardDisplayReflection.BuildFromReward(reward);
                        if (snap != null && !snap.IsEmpty)
                            rewardBlob = Multiplayer.Network.Sync.State.RewardDisplaySnapshot.Encode(snap);
                    }
                }
                catch (Exception rex) { Debug.LogError("[Multiplayer] CompleteEventDismissPatch reward-snapshot failed: " + rex.Message); }
                // Occurrence id for THIS instance (order-independent GetOrAssign). For a single-choice event this
                // dismiss fires at trigger time BEFORE the raise, so it may allocate the id first; the raise then
                // reuses it. This is the AUTHORITATIVE dismiss (carries the real picked index + reward), so mark
                // the occurrence dismissed → the FinishEncounter fallback won't send a second bare close for it.
                ushort occId = EventOccurrenceIds.GetOrAssign(__instance);
                EventOccurrenceIds.MarkDismissed(occId);
                // The live event's real site id (GeoSite.SiteId, -1 = none) — the SAME site the raise resolved.
                // Stamped on the dismiss wire so the client result card shows the real site, not StartingBase.
                int siteId = EventReflection.GetSiteId(__instance);
                // Host-resolved RESULT texts, the exact native SetClosingEncounter pair (:332-336): the picked
                // choice's outcome text + the raise narrative it falls back to when empty (one-window events).
                // Shipped so a client whose local def resolves EMPTY (TFTV VoidOmen runtime narrative) still
                // renders the host's text instead of a blank result parchment.
                string wireOutcome = EventReflection.ResolveLiveOutcomeText(__instance);
                string wireNarrative = EventReflection.ResolveLiveNarrative(__instance);
                Debug.Log("[Multiplayer] HOST BroadcastEventDismiss occId=" + occId + " eventId=" + eventId +
                          " selectedChoiceIndex=" + choiceIndex + " siteId=" + siteId +
                          " rewardBytes=" + (rewardBlob?.Length ?? 0) +
                          " outLen=" + (wireOutcome?.Length ?? 0) + " narrLen=" + (wireNarrative?.Length ?? 0));
                engine.Sync?.BroadcastEventDismiss(occId, eventId, choiceIndex, rewardBlob, siteId, wireOutcome, wireNarrative);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CompleteEventDismissPatch failed: " + ex.Message); }
        }
    }
}
