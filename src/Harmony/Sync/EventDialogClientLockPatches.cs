using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Two-class CLIENT-side geoscape-event dialog handling (host-authoritative outcomes, client never NREs
    /// and never relays an answer). The host alone owns the outcome; the client modal is purely a mirror:
    ///
    ///   • INFO  events (<c>Choices.Count &lt;= 1</c>, mirrors <c>GeoscapeEventData.HasSingleChoice</c>):
    ///     each player dismisses the modal LOCALLY (pure UI hide, no outcome, no host call). The host already
    ///     auto-applied the outcome at trigger time (<c>GeoscapeEventSystem.OnEventTriggered</c> completes
    ///     single-choice non-marketplace events BEFORE raising the modal).
    ///   • CHOICE events (<c>Choices.Count &gt;= 2</c>): the client modal is LOCKED — choice buttons inert,
    ///     Esc/back blocked. It closes for everyone only when the HOST picks (host click → CompleteEvent →
    ///     <c>CompleteEventDismissPatch</c> → <c>BroadcastEventDismiss</c> → client <c>EventDisplay.Dismiss</c>).
    ///
    /// Classification is read at click/cancel time off the live UI module's open event
    /// (<c>UIModuleSiteEncounters._geoEvent</c>) / the dialog state's <c>Event</c> via
    /// <see cref="EventReflection.IsClientChoiceLocked"/>; an unreadable choice count fails SAFE to CHOICE
    /// (locked) so a client never locally resolves an ambiguous event.
    ///
    /// All patches are CLIENT-ONLY (<c>!IsHost</c>) and never run for the host (host behavior unchanged).
    /// Marketplace events use a different module/state (<c>UIModuleTheMarketplace</c> /
    /// <c>UIStateMarketplaceGeoscapeEvent</c>) → naturally out of scope here. Every body is try/catch
    /// best-effort and NEVER throws into game code.
    ///
    /// Verified against the decompile (2026-06-17):
    ///   • <c>UIModuleSiteEncounters.OnChoiceSelected(GeoEventChoice)</c> (:548) — the choice-button handler
    ///     (wired :170). Pages while <c>_pagingEvent</c> (:550), else resolves the choice.
    ///   • <c>UIModuleSiteEncounters.SelectChoice(GeoscapeEvent, GeoEventChoice)</c> (:600) — runs
    ///     <c>CompleteEvent</c> (:604) then derefs <c>_geoEvent.ChoiceReward.ApplyResult.StartMission</c> (:606);
    ///     <c>ChoiceReward</c> is null on a client reconstruction → NRE. Client no-op (returns false = no mission).
    ///   • <c>UIModuleSiteEncounters.IsSingleChoiceEncounter()</c> (:258) — when true, show-time
    ///     <c>SetSingleChoiceEncounter</c> (:251) auto-runs <c>SelectChoice</c> (:253) AND
    ///     <c>SetClosingEncounter</c> (:255 → :359 <c>ChoiceReward.ApplyResult</c>) → both NRE on the client.
    ///     Forcing it false routes the client through the normal button render (no show-time deref).
    ///   • <c>UIStateGeoscapeEvent.OnCancel()</c> (:68) — Esc/back → <c>FinishQueriedState</c> (local close).
    ///   • <c>UIModuleSiteEncounters.FinishEncounter()</c> (:620) — the local-close invoker.
    /// </summary>
    internal static class EventDialogClientGuard
    {
        /// <summary>True only when a co-op session is live AND this peer is a CLIENT.</summary>
        public static bool IsClient
        {
            get
            {
                var e = NetworkEngine.Instance;
                return e != null && e.IsActiveSession && !e.IsHost;
            }
        }

        /// <summary>
        /// Should a client OK click on this dialog close LOCALLY (vs swallow-and-wait for the host's dismiss)?
        /// TRUE only for the client's OWN synthetic result/info page (the sole dialog with an EMPTY EventID,
        /// <see cref="EventReflection.IsSyntheticResultPage"/>). Every REAL host event has a non-empty EventID →
        /// FALSE → swallow (the host drives its dismiss).
        /// </summary>
        public static bool ShouldLocalClose(string eventId) => EventReflection.IsSyntheticResultPage(eventId);
    }

    /// <summary>
    /// Client: force the normal button render for single-choice events so the show-time
    /// <c>SetSingleChoiceEncounter</c> path (auto <c>SelectChoice</c> + <c>SetClosingEncounter</c>, both of
    /// which deref the null client <c>ChoiceReward</c>) is never taken. The single choice is shown as a button
    /// and handled by the OnChoiceSelected prefix instead. Host unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterSingleChoiceClientPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            if (t == null) return false;
            _target = AccessTools.Method(t, "IsSingleChoiceEncounter");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref bool __result)
        {
            try
            {
                if (!EventDialogClientGuard.IsClient) return true; // host: native
                __result = false;                                  // client: never the auto-select render path
                return false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterSingleChoiceClientPatch failed: " + ex.Message); return true; }
        }
    }

    /// <summary>
    /// Choice-button handler interceptor. First-click-wins is enforced HOST-SIDE at the CompleteEvent chokepoint
    /// (CompleteEventPatch.Prefix → SyncEngine.Arbiter.Claim), NOT here — the client never resolves locally; it
    /// only relays an AnswerEventAction and shows immediate local feedback (greys/blocks the choice buttons so the
    /// click visibly registers and can't double-fire). NO permission gate (user directive).
    ///   • HOST = PURE NATIVE: the host's click (paging, choice, OR the OK on its result page) runs the untouched
    ///     native OnChoiceSelected → SelectChoice → CompleteEvent → SetClosingEncounter (result + rewards) → OK →
    ///     FinishEncounter → FinishQueriedState (close). CompleteEventPatch/CompleteEventDismissPatch.Postfix
    ///     broadcast the EventDismiss to clients on the native CompleteEvent. We intercept NOTHING on the host (the
    ///     old arbiter lock swallowed a "lost" host OK → frozen modal: ISSUE 2/3 — removed).
    ///   • CLIENT, while paging (<c>_pagingEvent</c>) → let native advance description text (no outcome/close).
    ///   • CLIENT synthetic result/info page (EventID=="") → native local close (the player OKs it).
    ///   • CLIENT real MULTI-choice host event (Choices &gt;= 2) → swallow the native body (return false, no client
    ///     CompleteEvent / null ChoiceReward NRE) and <c>SyncEngine.SendActionRequest(AnswerEventAction(occId,
    ///     eventId, choiceIndex))</c>; the modal stays OPEN until the host's EventDismiss lands → OnEventDismiss
    ///     rebuilds the synthetic result page (ShowResultInPlace). On the host, that relayed answer drives the host's
    ///     OWN native modal (<c>EventReflection.TryHostNativeResolve</c>) → native host result page + broadcast.
    ///   • CLIENT single-choice / info real host event → auto-completed on the host at trigger; swallow-and-wait for
    ///     the host's already-broadcast dismiss. Never re-completed.
    /// Last-write-wins is safe: native CompleteEvent self-guards on IsCompleted, so a repeat is a no-op.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterChoiceClientPatch
    {
        private static MethodBase _target;
        private static FieldInfo _geoEventField;   // UIModuleSiteEncounters._geoEvent
        private static FieldInfo _pagingField;     // UIModuleSiteEncounters._pagingEvent
        private static MethodInfo _finishEncounter; // UIModuleSiteEncounters.FinishEncounter()
        private static FieldInfo _choiceContainerField; // UIModuleSiteEncounters.ChoiceButtonsContainer (GameObject)

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (t == null || choiceT == null) return false;
            _target = AccessTools.Method(t, "OnChoiceSelected", new[] { choiceT });
            _geoEventField = AccessTools.Field(t, "_geoEvent");
            _pagingField = AccessTools.Field(t, "_pagingEvent");
            _finishEncounter = AccessTools.Method(t, "FinishEncounter");
            _choiceContainerField = AccessTools.Field(t, "ChoiceButtonsContainer");
            return _target != null;
        }

        /// <summary>
        /// Client choice-button group toggle via a CanvasGroup on ChoiceButtonsContainer (one toggle dims+disables,
        /// or restores, every choice button at once). Same container lookup for both directions so the grey/restore
        /// is symmetric.
        ///   • <paramref name="enabled"/>=false → grey + block: the click registered and input is inert while the
        ///     modal waits for the host's authoritative result (the 8f9452f EventDismiss path drives the outcome).
        ///   • <paramref name="enabled"/>=true → restore: re-enable for a FRESHLY shown dialog. The module/container
        ///     is POOLED across events, so without this the next event's buttons stay permanently greyed/dead.
        ///     Driven per event render from <see cref="EncounterResetButtonsClientPatch"/> (SetEncounter Postfix).
        /// Reflection-only, best-effort, NEVER throws into game code; does NOT close the modal or apply any outcome.
        /// </summary>
        internal static void SetChoiceButtonsEnabled(object module, bool enabled)
        {
            try
            {
                var container = _choiceContainerField?.GetValue(module) as GameObject;
                if (container == null) return;
                var cg = container.GetComponent<CanvasGroup>() ?? container.AddComponent<CanvasGroup>();
                cg.interactable = enabled;
                cg.blocksRaycasts = enabled;
                cg.alpha = enabled ? 1f : 0.4f;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterChoiceClientPatch SetChoiceButtonsEnabled failed: " + ex.Message); }
        }

        /// <summary>
        /// Client replay mode (<c>EventReplayModeGate</c>): reactively re-arm the LIVE open choice window for
        /// <paramref name="occId"/> — restore the choice-button container (a race-loser greyed the whole CanvasGroup
        /// on its own click, which blocks raycasts) then grey non-winning buttons + highlight the winner via the
        /// native selected state (<see cref="Multiplayer.Network.Sync.State.EventReplayReflection.ApplyReplayButtons"/>).
        /// No-op unless this client's currently-open dialog IS this occurrence's choice page (not paging). Called the
        /// instant the decided signal arrives (reactivity mandate) and again from the SetEncounter re-arm postfix.
        /// Best-effort; never throws into game code.
        /// </summary>
        internal static void ArmReplayOnLiveModule(GeoRuntime rt, ushort occId, int winningIndex)
        {
            try
            {
                if (rt == null || occId == 0) return;
                // Only when THIS client's currently-open dialog is this occurrence (EventDisplay records it on Show).
                if (Multiplayer.Network.Sync.State.EventDisplay.OpenOccurrenceId != occId) return;
                var module = EventReflection.GetLiveSiteEncountersModule(rt);
                if (module == null) return;
                // Still paging the description text → the real choice buttons aren't shown yet; the re-arm lands via
                // the SetEncounter postfix when the choice page renders.
                if (EventReflection.IsSiteEncountersPaging(module)) return;
                SetChoiceButtonsEnabled(module, true);   // restore the container (winner must be clickable)
                Multiplayer.Network.Sync.State.EventReplayReflection.ApplyReplayButtons(module, winningIndex);
                Debug.Log("[Multiplayer] EncounterChoiceClientPatch ArmReplayOnLiveModule occId=" + occId +
                          " winningIndex=" + winningIndex + " → armed (grey losers, highlight winner)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterChoiceClientPatch ArmReplayOnLiveModule failed: " + ex.Message); }
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleSiteEncounters; __0 = the clicked GeoEventChoice (OnChoiceSelected's arg).
        public static bool Prefix(object __instance, object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool inSession = engine != null && engine.IsActiveSession;
                bool isHost = inSession && engine.IsHost;
                if (!inSession) return true;   // single-player: native
                // HOST = PURE NATIVE. The host's own click (choice OR the OK on its result page OR paging) runs the
                // untouched native OnChoiceSelected → SelectChoice → CompleteEvent → SetClosingEncounter (result +
                // rewards) → OK → FinishEncounter → FinishQueriedState (close). CompleteEventPatch/
                // CompleteEventDismissPatch.Postfix already broadcast the EventDismiss to clients on the native
                // CompleteEvent. We intercept NOTHING on the host (no arbiter lock, no permission gate, no swallow) —
                // that lock was what froze the host (lost claim → swallowed OK → modal never closed: ISSUE 2/3).
                if (isHost) return true;
                // else: CLIENT — fall through to the client claim path below.

                // While paging multi-page descriptions the native handler only advances text (no outcome,
                // no ChoiceReward deref) → let it run; the real choice/close click lands once paging ends.
                if (_pagingField != null && (bool)(_pagingField.GetValue(__instance) ?? false)) return true;

                object geoEvent = _geoEventField?.GetValue(__instance);
                // Discriminate by EventID, NOT by choice count: a real single-choice host event and the client's
                // synthetic result page BOTH have Choices.Count == 1, so a count gate can't tell them apart (it
                // would local-close real host events, disconnecting them from the host). The synthetic result/info
                // page is the ONLY client-owned dialog and is built with EventID == "" (never re-broadcast /
                // re-keyed — EventReflection.BuildResultEvent:591). So:
                string eventId = EventReflection.GetEventId(geoEvent);
                if (EventDialogClientGuard.ShouldLocalClose(eventId))
                {
                    // Synthetic CLIENT-owned result/info page (EventID == "") → close LOCALLY (the player OKs it).
                    Debug.Log("[Multiplayer] EncounterChoiceClientPatch localClose=true (synthetic result page, EventID==\"\")");
                    _finishEncounter?.Invoke(__instance, null);
                    return false;
                }
                // Real host event (non-empty EventID). Resolve the clicked choice index + open occurrence ONCE for
                // every window kind. GetChoiceIndex returns ChoiceDecline (-1) for a null/decline choice and
                // ChoiceLookupFailed (int.MinValue) when it can't introspect — normalize the failure to -1 (decline)
                // so the host still resolves the occurrence (close-only) and the modal is never left stuck.
                int choiceCount = EventReflection.GetChoiceCount(geoEvent);
                int claimIndex = EventReflection.GetChoiceIndex(geoEvent, __0);
                if (claimIndex == EventReflection.ChoiceLookupFailed) claimIndex = -1;
                ushort occId = Multiplayer.Network.Sync.State.EventDisplay.OpenOccurrenceId;

                // UNIFIED REPLAY CONSUME — one rule for EVERY window kind (multi-choice, single-OK info, close-only):
                // if this occurrence is already DECIDED and this window is replay-armed, the local click consumes the
                // buffered terminal IN PLACE (winner click → authoritative result page; close-only → local close;
                // stray loser click → suppressed + re-armed). NO new claim/relay — the occurrence is already resolved
                // host-side. Handled entirely by the SyncEngine; swallow the native handler either way.
                if (EventReplayModeGate.Enabled && NetworkEngine.Instance?.Sync != null
                    && NetworkEngine.Instance.Sync.TryReplayDecidedClick(occId, claimIndex))
                {
                    Debug.Log("[Multiplayer] EncounterChoiceClientPatch REPLAY-CLICK occId=" + occId +
                              " choiceIndex=" + claimIndex + " choiceCount=" + choiceCount +
                              " → consumed decided terminal in place (no claim/relay)");
                    return false;
                }

                // A genuine MULTI-choice (Choices >= 2) client click is an ANSWER REQUEST over the research-style
                // action relay (first-click-wins): send an AnswerEventAction(occId, eventId, choiceIndex) to the
                // host and keep the modal OPEN (return false → native body never runs → no local CompleteEvent /
                // null-ChoiceReward NRE). The host arbitrates the first claim and broadcasts the OUTCOME, which
                // closes/replaces this modal via OnEventDismiss → ShowResultInPlace / CloseDialog / ArmReplay.
                if (choiceCount >= 2)
                {
                    Debug.Log("[Multiplayer] EncounterChoiceClientPatch CLAIM eventId=" + eventId +
                              " occId=" + occId + " choiceIndex=" + claimIndex + " → SendActionRequest(AnswerEventAction) (modal stays open)");
                    NetworkEngine.Instance?.Sync?.SendActionRequest(
                        new Multiplayer.Network.Sync.Actions.AnswerEventAction(occId, eventId, claimIndex));
                    // REPLAY MODE: remember THIS peer's picked index so the decided signal can split the WINNER
                    // (picked == winning → auto in-place transition) from a race-loser (→ replay-arm). Additive.
                    if (EventReplayModeGate.Enabled) NetworkEngine.Instance?.Sync?.MarkEventPickedChoice(occId, claimIndex);
                    // Immediate local feedback that the click registered: grey + block the choice buttons so the
                    // player sees it took and can't double-fire. NOT an outcome/close — the authoritative result
                    // still arrives via the host's EventDismiss (8f9452f path) which rebuilds the result page.
                    // The grey is undone per fresh dialog by EncounterResetButtonsClientPatch (SetEncounter Postfix).
                    SetChoiceButtonsEnabled(__instance, false);
                    return false;
                }
                // Confirmed SINGLE-choice real host event (Choices.Count == 1): the host auto-completed it at trigger
                // and ALREADY broadcast its result-bearing dismiss. When the client is MIRRORING the host's window-1
                // prompt (gate ON, known occurrence), the player's OK relays an advance-request AND — FIX C — keeps
                // this modal OPEN (greyed), so the host's EventAdvanceResult transitions it to the result page IN
                // PLACE (matching the host's two-window flow) instead of the old local-close that let that advance
                // re-pop a FRESH result window. When no advance is relayed (gate OFF / occId 0 / the brief race where
                // the player OKs the REAL flavor modal before the synthetic result page replaces it) NO host signal
                // will close this modal → close LOCALLY on the OK (the outcome/reward already applied via the state
                // channels; a pure UI hide, no host call).
                if (choiceCount == 1)
                {
                    // Co-op UX fix (in-game confirmed bug): the local close alone left the HOST's prompt open
                    // until the host clicked. Relay an advance-request so EITHER side's OK advances the host's
                    // native prompt→result (host drives OnChoiceSelected → SetClosingEncounter →
                    // SingleChoiceAdvancePatch broadcasts EventAdvanceResult → both sides show the result).
                    // The event auto-completed on the host at trigger, so AnswerEventAction can't do this
                    // (TryHostNativeResolve no-ops on IsCompleted). First-wins/idempotent host-side; occId 0
                    // (no recorded open occurrence) or gate OFF → no relay, localClose only (legacy behavior).
                    if (Multiplayer.Network.Sync.State.SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
                            isClient: true, gateEnabled: EventMirrorFixGate.Enabled,
                            eventId: eventId, choiceCount: choiceCount, occurrenceId: occId))
                    {
                        Debug.Log("[Multiplayer] EncounterChoiceClientPatch → SendEventAdvanceRequest occId=" + occId +
                                  " eventId=" + eventId + " (advance the host's single-choice prompt)");
                        NetworkEngine.Instance?.Sync?.SendEventAdvanceRequest(occId, eventId);
                        // FIX C: DO NOT local-close. The host WILL broadcast EventAdvanceResult for this occurrence,
                        // so keep the modal OPEN and grey the lone button (matching the multi-choice path above) —
                        // the host's advance then transitions THIS SAME window to the result page IN PLACE
                        // (EventDisplay.ShowResult openIsEventState=True). Local-closing here made that later advance
                        // re-pop a FRESH result window (the RCA re-pop). Belt: record the locally-answered occId so
                        // an in-order raise/advance coalesces into this open modal instead of a fresh Show.
                        NetworkEngine.Instance?.Sync?.MarkEventLocallyAnswered(occId);
                        SetChoiceButtonsEnabled(__instance, false);
                        Debug.Log("[Multiplayer] EncounterChoiceClientPatch keepOpen=true (single-choice prompt mirror answered occId=" +
                                  occId + " eventId=" + eventId + " — awaiting host EventAdvanceResult, in-place transition)");
                        return false;
                    }
                    // No advance relay (gate OFF / occId==0 / late race): NO host EventAdvanceResult will arrive to
                    // transition this modal, so close it LOCALLY as before (legacy behavior — the outcome/reward
                    // already applied via the state channels; this is a pure UI hide).
                    Debug.Log("[Multiplayer] EncounterChoiceClientPatch localClose=true (single-choice host modal mirror, no advance relay, EventID=" + eventId + ")");
                    _finishEncounter?.Invoke(__instance, null);
                    return false;
                }
                // Unreadable / zero-choice (ambiguous) real host event: fail SAFE — swallow-and-wait for a host
                // dismiss rather than locally closing an event we couldn't classify (unchanged behavior).
                Debug.Log("[Multiplayer] EncounterChoiceClientPatch localClose=false swallow eventId=" + eventId +
                          " choiceCount=" + choiceCount + " (ambiguous host event → wait for host dismiss)");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] EncounterChoiceClientPatch failed: " + ex.Message);
                // On any failure fail SAFE: swallow the click (never let the native body NRE / apply locally).
                return false;
            }
        }
    }

    /// <summary>
    /// Client per-event RESET for the choice-button grey applied on a click (<see cref="EncounterChoiceClientPatch"/>
    /// → <c>SetChoiceButtonsEnabled(module, false)</c>). The <c>UIModuleSiteEncounters</c> + its
    /// <c>ChoiceButtonsContainer</c> are POOLED across events, so a CanvasGroup left disabled would leave the NEXT
    /// dialog's buttons permanently greyed/dead. <c>SetEncounter</c> (UIModuleSiteEncounters.cs:267) is the universal
    /// client render entry — <c>ShowEncounter</c> (:194 → :247) routes EVERY freshly shown dialog through it (the
    /// client forces <c>IsSingleChoiceEncounter</c>=false via <see cref="EncounterSingleChoiceClientPatch"/>, so the
    /// auto-select branch :241 is never taken), INCLUDING the synthetic result page
    /// (<c>EventDisplay.ShowResult</c> → <c>Show</c> → <c>ShowEncounter</c> → <c>SetEncounter</c>). A Postfix here
    /// re-enables the group right after the native build, before the player can interact — guaranteeing live buttons
    /// for every event AND a clickable OK on the result page that replaces a just-greyed choice modal. Host unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterResetButtonsClientPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var evtT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            if (t == null || evtT == null) return false;
            // SetEncounter(GeoscapeEvent geoEvent, bool pagingEvent, string overrideText = null)
            _target = AccessTools.Method(t, "SetEncounter", new[] { evtT, typeof(bool), typeof(string) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleSiteEncounters. Runs after the native build (re)populated the choice buttons.
        public static void Postfix(object __instance)
        {
            try
            {
                bool isClient = EventDialogClientGuard.IsClient;
                if (isClient) EncounterChoiceClientPatch.SetChoiceButtonsEnabled(__instance, true);   // client: un-grey the pooled container

                // REPLAY MODE: the module is POOLED across events → a prior replay arm must never leak its highlight
                // onto this freshly rendered event. When THIS render is a still-decided occurrence (client:
                // paging→choice / pooled re-render), RE-APPLY the arm here so the highlight lands the moment the
                // choice page shows; otherwise (host, or a non-armed render) CLEAR any stale selected state to the
                // native default. Byte-for-byte legacy when the gate is OFF (only the client CanvasGroup reset above).
                if (!EventReplayModeGate.Enabled) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                if (isClient)
                {
                    ushort occId = Multiplayer.Network.Sync.State.EventDisplay.OpenOccurrenceId;
                    // A result-bearing arm re-applies its visuals (winner highlight + grey losers; a lone OK button
                    // is its own winner). A CLOSE-ONLY arm (winning < 0) falls through to the Clear below instead:
                    // buttons stay native-live and any click consumes the buffered terminal.
                    if (occId != 0 && engine.Sync != null
                        && engine.Sync.TryGetDecidedWinning(occId, out int winningIndex)
                        && winningIndex >= 0)
                    {
                        Multiplayer.Network.Sync.State.EventReplayReflection.ApplyReplayButtons(__instance, winningIndex);
                        return;
                    }
                }
                Multiplayer.Network.Sync.State.EventReplayReflection.ClearReplayButtons(__instance);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterResetButtonsClientPatch failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Client defense-in-depth: <c>SelectChoice</c> runs the authoritative <c>CompleteEvent</c> then derefs the
    /// null client <c>ChoiceReward</c> (NRE). The client must never run either, so any path that still reaches
    /// it is short-circuited to "no mission launched" (false). Host unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterSelectChoiceClientPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var evtT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (t == null || evtT == null || choiceT == null) return false;
            _target = AccessTools.Method(t, "SelectChoice", new[] { evtT, choiceT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(ref bool __result)
        {
            try
            {
                if (!EventDialogClientGuard.IsClient) return true; // host: native
                __result = false;                                  // client: no CompleteEvent, no mission launch
                return false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterSelectChoiceClientPatch failed: " + ex.Message); return true; }
        }
    }

    /// <summary>
    /// Client + CHOICE: block Esc/back (<c>UIStateGeoscapeEvent.OnCancel</c> → <c>FinishQueriedState</c>) so a
    /// CHOICE modal cannot be closed locally — it closes only on the host's broadcast dismiss. INFO is allowed
    /// (native OnCancel is the local hide). Host unaffected. (Marketplace's OnCancel is on a different state.)
    /// </summary>
    [HarmonyPatch]
    public static class EventCancelClientLockPatch
    {
        private static MethodBase _target;
        private static PropertyInfo _eventProp;   // UIStateBaseGeoscapeEvent<>.Event

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewStates.UIStateGeoscapeEvent");
            if (t == null) return false;
            _target = AccessTools.Method(t, "OnCancel");
            _eventProp = AccessTools.Property(t, "Event"); // inherited from UIStateBaseGeoscapeEvent<>
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIStateGeoscapeEvent.
        public static bool Prefix(object __instance)
        {
            try
            {
                if (!EventDialogClientGuard.IsClient) return true; // host: native
                // REPLAY MODE: Esc/back on a replay-armed window consumes the buffered decided terminal exactly
                // like the OK/winner click (result page in place, or local close for a close-only terminal) — a
                // plain native local-hide would close the window WITHOUT resolving the correlator's open/slot
                // state (no future host signal will ever free it → every later dialog would defer forever).
                // clickedIndex -1 = unconditional consume. Not armed → fall through to the normal lock logic.
                if (EventReplayModeGate.Enabled)
                {
                    ushort occ = Multiplayer.Network.Sync.State.EventDisplay.OpenOccurrenceId;
                    var sync = NetworkEngine.Instance?.Sync;
                    if (occ != 0 && sync != null && sync.TryReplayDecidedClick(occ, -1))
                    {
                        Debug.Log("[Multiplayer] EventCancelClientLockPatch REPLAY-CANCEL occId=" + occ +
                                  " → consumed decided terminal (Esc acts as the consume click)");
                        return false;
                    }
                }
                object geoEvent = _eventProp?.GetValue(__instance, null);
                // CHOICE (or ambiguous) → block the local close; INFO → allow native local hide.
                return !EventReflection.IsClientChoiceLocked(geoEvent);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] EventCancelClientLockPatch failed: " + ex.Message);
                return false; // fail SAFE: block the close on any failure (never locally branch an outcome)
            }
        }
    }

    /// <summary>
    /// Host FALLBACK close-only dismiss: when the host clicks OK on its own modal and that occurrence has NOT
    /// already had an authoritative dismiss broadcast (<c>CompleteEventDismissPatch</c>), tell clients to close.
    /// In practice single-choice events auto-complete at trigger (<c>GeoscapeEventSystem.OnEventTriggered</c>)
    /// so their result-bearing dismiss already went out and this is SKIPPED via
    /// <see cref="EventOccurrenceIds.WasDismissed"/> — preventing a SECOND, bare close-only dismiss that would
    /// re-trigger a client <c>CloseDialog</c> (double-dismiss) on top of the result page. This remains only as a
    /// safety net for any single-choice occurrence that reached FinishEncounter without a prior dismiss.
    /// </summary>
    [HarmonyPatch]
    public static class FinishEncounterHostDismissPatch
    {
        private static MethodBase _target;
        private static FieldInfo _geoEventField;   // UIModuleSiteEncounters._geoEvent

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            if (t == null) return false;
            _target = AccessTools.Method(t, "FinishEncounter");
            _geoEventField = AccessTools.Field(t, "_geoEvent");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleSiteEncounters.
        public static void Postfix(object __instance)
        {
            try
            {
                if (SyncApplyScope.IsApplying) return;
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;

                object geoEvent = _geoEventField?.GetValue(__instance);
                if (geoEvent == null) return;
                // CHOICE (Choices >= 2) host-picks already broadcast a result-bearing dismiss via
                // CompleteEventDismissPatch — never duplicate here.
                if (EventReflection.GetChoiceCount(geoEvent) >= 2) return;
                // PURE mission-deploy prompts are never mirrored (raise/dismiss suppressed at the SOURCE) — a
                // single-choice deploy event must not leak a fallback bare-close for a dialog the client never
                // opened. Symmetric to EventRaisedDisplayPatch / CompleteEventDismissPatch. Fail-open (broadcast as before).
                if (EventReflection.IsMissionDeployEvent(geoEvent)) return;

                string eventId = EventReflection.GetEventId(geoEvent);
                if (string.IsNullOrEmpty(eventId)) return;
                // Same live GeoscapeEvent instance → its occurrence id. If an authoritative dismiss already went
                // out for it (the usual single-choice auto-complete-at-trigger case), do NOT send a second bare
                // close — that would re-trigger CloseDialog on the client and close the result page (Bug A).
                ushort occId = EventOccurrenceIds.GetOrAssign(geoEvent);
                if (EventOccurrenceIds.WasDismissed(occId))
                {
                    Debug.Log("[Multiplayer] FinishEncounterHostDismissPatch occId=" + occId + " eventId=" + eventId +
                              " skip=alreadyDismissed (result-bearing dismiss already broadcast → no double-dismiss)");
                    return;
                }
                Debug.Log("[Multiplayer] FinishEncounterHostDismissPatch occId=" + occId + " eventId=" + eventId +
                          " → BroadcastEventDismiss (fallback close-only)");
                engine.Sync?.BroadcastEventDismiss(occId, eventId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] FinishEncounterHostDismissPatch failed: " + ex.Message); }
        }
    }
}
