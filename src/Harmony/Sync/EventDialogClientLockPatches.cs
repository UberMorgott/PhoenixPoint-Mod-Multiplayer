using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using UnityEngine;

namespace Multipleer.Harmony.Sync
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
            catch (Exception ex) { Debug.LogError("[Multipleer] EncounterSingleChoiceClientPatch failed: " + ex.Message); return true; }
        }
    }

    /// <summary>
    /// Client choice-button handler interceptor (the core of the two-class split):
    ///   • while paging (<c>_pagingEvent</c>) → let native advance the description text (no outcome/close).
    ///   • CHOICE (Choices &gt;= 2, or unreadable) → swallow the click: modal stays open, no relay, no NRE.
    ///   • INFO (Choices &lt;= 1) → close LOCALLY via <c>FinishEncounter</c> with NO outcome and NO host call.
    /// Always returns false for a resolved (non-paging) client click so the native body (which derefs the null
    /// client <c>ChoiceReward</c> / runs <c>CompleteEvent</c>) never executes. Host unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterChoiceClientPatch
    {
        private static MethodBase _target;
        private static FieldInfo _geoEventField;   // UIModuleSiteEncounters._geoEvent
        private static FieldInfo _pagingField;     // UIModuleSiteEncounters._pagingEvent
        private static MethodInfo _finishEncounter; // UIModuleSiteEncounters.FinishEncounter()

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (t == null || choiceT == null) return false;
            _target = AccessTools.Method(t, "OnChoiceSelected", new[] { choiceT });
            _geoEventField = AccessTools.Field(t, "_geoEvent");
            _pagingField = AccessTools.Field(t, "_pagingEvent");
            _finishEncounter = AccessTools.Method(t, "FinishEncounter");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = UIModuleSiteEncounters; __0 = the clicked GeoEventChoice (OnChoiceSelected's arg).
        public static bool Prefix(object __instance, object __0)
        {
            try
            {
                if (!EventDialogClientGuard.IsClient) return true; // host: native answer/close

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
                    Debug.Log("[Multipleer] EncounterChoiceClientPatch localClose=true (synthetic result page, EventID==\"\")");
                    _finishEncounter?.Invoke(__instance, null);
                    return false;
                }
                // ALL real host events (non-empty EventID), single- OR multi-choice → swallow-and-wait. The host's
                // CompleteEventDismissPatch always broadcasts a result-bearing dismiss (single-choice auto-completes
                // at trigger), so the client modal is closed/replaced via OnEventDismiss → ShowResultInPlace /
                // CloseDialog — host-authoritative, never stranded.
                Debug.Log("[Multipleer] EncounterChoiceClientPatch localClose=false swallow eventId=" + eventId +
                          " (real host event → wait for host dismiss)");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] EncounterChoiceClientPatch failed: " + ex.Message);
                // On any failure fail SAFE: swallow the click (never let the native body NRE / apply locally).
                return false;
            }
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
            catch (Exception ex) { Debug.LogError("[Multipleer] EncounterSelectChoiceClientPatch failed: " + ex.Message); return true; }
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
                object geoEvent = _eventProp?.GetValue(__instance, null);
                // CHOICE (or ambiguous) → block the local close; INFO → allow native local hide.
                return !EventReflection.IsClientChoiceLocked(geoEvent);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] EventCancelClientLockPatch failed: " + ex.Message);
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

                string eventId = EventReflection.GetEventId(geoEvent);
                if (string.IsNullOrEmpty(eventId)) return;
                // Same live GeoscapeEvent instance → its occurrence id. If an authoritative dismiss already went
                // out for it (the usual single-choice auto-complete-at-trigger case), do NOT send a second bare
                // close — that would re-trigger CloseDialog on the client and close the result page (Bug A).
                ushort occId = EventOccurrenceIds.GetOrAssign(geoEvent);
                if (EventOccurrenceIds.WasDismissed(occId))
                {
                    Debug.Log("[Multipleer] FinishEncounterHostDismissPatch occId=" + occId + " eventId=" + eventId +
                              " skip=alreadyDismissed (result-bearing dismiss already broadcast → no double-dismiss)");
                    return;
                }
                Debug.Log("[Multipleer] FinishEncounterHostDismissPatch occId=" + occId + " eventId=" + eventId +
                          " → BroadcastEventDismiss (fallback close-only)");
                engine.Sync?.BroadcastEventDismiss(occId, eventId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] FinishEncounterHostDismissPatch failed: " + ex.Message); }
        }
    }
}
