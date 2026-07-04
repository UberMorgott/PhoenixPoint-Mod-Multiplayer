using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// HOST single-choice PROMPT→RESULT advance signal (additive; gated on <see cref="EventMirrorFixGate"/>).
    ///
    /// A single-choice site-exploration encounter whose lone choice HAS outcome text renders its window-1 PROMPT
    /// page on the host — native <c>UIModuleSiteEncounters.IsSingleChoiceEncounter()</c> is FALSE (the lone
    /// choice's <c>Outcome.OutcomeText</c> is non-empty, decompile UIModuleSiteEncounters.cs:256-263), so
    /// <c>ShowEncounter</c> takes the <c>SetEncounter</c> prompt path (:239-245) even though the host AUTO-COMPLETED
    /// the event at trigger (<c>GeoscapeEventSystem.OnEventTriggered</c> :651-655: <c>HasSingleChoice</c> →
    /// <c>CompleteEvent</c>). The host's result-bearing <c>EventDismiss</c> therefore went out BEFORE the raise, so
    /// the client mirrors the PROMPT (<c>EventCorrelator</c> single-choice branch) and waits.
    ///
    /// When the host player clicks that lone prompt button, <c>OnChoiceSelected → SelectChoice</c> runs NO
    /// <c>CompleteEvent</c> (the event is already completed — decompile :600-603) → <c>SetClosingEncounter</c> shows
    /// the window-2 RESULT page (:580-595, :324-359) WITHOUT any <c>CompleteEvent</c>/<c>EventDismiss</c> broadcast.
    /// This Postfix is the missing host→client advance: it fires on <c>SetClosingEncounter</c> for a SINGLE-choice
    /// event and broadcasts <c>EventAdvanceResult(occId)</c> so the client follows the host from prompt to result.
    /// MULTI-choice events are skipped (their click DOES run <c>CompleteEvent</c> → the <c>EventDismiss</c> already
    /// advances the client). The occId is the SAME instance's id (<c>EventOccurrenceIds.GetOrAssign</c>) the raise
    /// and dismiss used, so it correlates exactly.
    ///
    /// Host-only + not-applying + best-effort try/catch — never throws into game code. No-op when the gate is OFF
    /// (the client never enters its prompt-mirror state off-gate either, so a stray advance is harmless).
    /// </summary>
    [HarmonyPatch]
    public static class SingleChoiceAdvancePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var evtT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            if (moduleT == null || evtT == null || choiceT == null) return false;
            // private void SetClosingEncounter(GeoscapeEvent geoEvent, GeoEventChoice closingChoice, bool useEventTexts)
            _target = AccessTools.Method(moduleT, "SetClosingEncounter", new[] { evtT, choiceT, typeof(bool) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // geoEvent = the ORIGINAL event whose result page is being shown (first arg of SetClosingEncounter).
        public static void Postfix(object geoEvent)
        {
            if (SyncApplyScope.IsApplying) return;   // engine-driven client result render → never broadcast
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (!EventMirrorFixGate.Enabled) return;   // additive — inert when the gate is OFF
            try
            {
                if (geoEvent == null) return;
                // Only SINGLE-choice events need this explicit advance: a multi-choice click runs CompleteEvent
                // (→ EventDismiss) which already advances the client. Unreadable count (-1) → treat as multi (skip).
                int choiceCount = EventReflection.GetChoiceCount(geoEvent);
                if (choiceCount < 0 || choiceCount > 1) return;
                string eventId = EventReflection.GetEventId(geoEvent);
                if (string.IsNullOrEmpty(eventId)) return;
                ushort occId = EventOccurrenceIds.GetOrAssign(geoEvent);   // SAME instance → SAME id as raise/dismiss
                int choiceIndex = EventReflection.GetSelectedChoiceIndex(geoEvent);
                int siteId = EventReflection.GetSiteId(geoEvent);
                // Mark the prompt→result advance BEFORE broadcasting: the module keeps the SAME _geoEvent on its
                // result page, so this mark is the only prompt-vs-result discriminator — it turns a later client
                // EventAdvanceRequest (raced click / transport double-send) into a first-wins no-op
                // (SingleChoiceAdvanceGate.ShouldDriveHostAdvance via TryHostNativeAdvanceSingleChoice).
                EventOccurrenceIds.MarkAdvanced(occId);
                Debug.Log("[Multiplayer] HOST BroadcastEventAdvanceResult (single-choice prompt→result) occId=" + occId +
                          " eventId=" + eventId + " choiceIndex=" + choiceIndex + " siteId=" + siteId);
                engine.Sync?.BroadcastEventAdvanceResult(occId, eventId, choiceIndex, siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SingleChoiceAdvancePatch failed: " + ex.Message); }
        }
    }
}
