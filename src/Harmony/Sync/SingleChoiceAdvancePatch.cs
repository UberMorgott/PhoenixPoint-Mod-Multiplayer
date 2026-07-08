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
                // REPLAY MODE consume-click guard: when a client's advance was already applied MODEL-ONLY
                // (SyncEngine.TryHostModelAdvance marked + broadcast without touching this window), the host
                // player's own later click on the prompt renders window-2 natively (SetClosingEncounter) as the
                // unified-rule CONSUME — it must NOT broadcast a second EventAdvanceResult. Gate-OFF flows are
                // byte-identical: every legacy path reaches this postfix with the mark still unset (the native
                // drive's belt-mark lands only AFTER OnChoiceSelected returns, i.e. after this postfix ran).
                if (EventOccurrenceIds.WasAdvanced(occId))
                {
                    Debug.Log("[Multiplayer] SingleChoiceAdvancePatch occId=" + occId +
                              " → skip re-broadcast (already advanced — model-only or earlier click won)");
                    return;
                }
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

    /// <summary>
    /// HOST replay of a CLIENT single-choice advance that arrived BEFORE the host displayed the prompt.
    ///
    /// A client OK on a mirrored single-choice window-1 prompt relays <c>EventAdvanceRequest</c> (0x6B). If the host
    /// is not yet showing that occurrence (its native dialog is queued behind a cutscene / higher-priority display
    /// in the view-switch queue — <c>UIModuleSiteEncounters._geoEvent == null</c>), <c>OnEventAdvanceRequest</c>
    /// cannot drive it and BUFFERS the request (<see cref="Multiplayer.Network.Sync.State.PendingHostAdvance"/>).
    /// This Postfix on <c>SetEncounter</c> (the host's window-1 prompt show, decompile UIModuleSiteEncounters.cs:267)
    /// replays the buffered advance the instant the host reaches that occurrence, so the client's click advances the
    /// flow for everyone — the host player no longer has to click too. Idempotent first-wins: an already-advanced
    /// occurrence (host clicked first) just clears its buffer; a still-paging prompt keeps it buffered for the next
    /// (non-paging) show (<c>TryHostNativeAdvanceSingleChoice</c> re-checks paging itself).
    ///
    /// Host-only + not-applying + gate-coupled; best-effort try/catch — never throws into game code.
    /// </summary>
    [HarmonyPatch]
    public static class EncounterHostAdvanceReplayPatch
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

        // __0 = the GeoscapeEvent whose prompt was just shown on the host.
        public static void Postfix(object __0)
        {
            if (SyncApplyScope.IsApplying) return;   // engine-driven client render → not a host prompt show
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (!EventMirrorFixGate.Enabled) return;
            try
            {
                if (__0 == null) return;
                ushort occId = EventOccurrenceIds.GetOrAssign(__0);   // SAME id as the raise/dismiss/advance wire
                if (!Multiplayer.Network.Sync.State.PendingHostAdvance.TryGet(occId, out var eventId)) return;
                // Already advanced (host click or an earlier driven request won the race) → just drop the buffer.
                if (EventOccurrenceIds.WasAdvanced(occId))
                {
                    Multiplayer.Network.Sync.State.PendingHostAdvance.Remove(occId);
                    return;
                }
                // REPLAY MODE: a buffered advance that becomes model-resolvable at prompt-show is applied
                // MODEL-ONLY (mark + broadcast, host window untouched — the host player reads + consumes at
                // their own pace), exactly like the request-time path. Fallback: legacy native drive.
                if (Multiplayer.Network.Sync.EventReplayModeGate.Enabled && engine.Sync != null
                    && engine.Sync.TryHostModelAdvance(occId, eventId))
                {
                    Multiplayer.Network.Sync.State.PendingHostAdvance.Remove(occId);
                    Debug.Log("[Multiplayer] EncounterHostAdvanceReplayPatch resolved buffered advance occId=" + occId +
                              " eventId=" + eventId + " → MODEL-ONLY broadcast (host window left for the unified consume)");
                    return;
                }
                bool drove = EventReflection.TryHostNativeAdvanceSingleChoice(GeoRuntime.Instance, occId, eventId);
                if (drove)
                {
                    Multiplayer.Network.Sync.State.PendingHostAdvance.Remove(occId);
                    Debug.Log("[Multiplayer] EncounterHostAdvanceReplayPatch replayed buffered advance occId=" + occId +
                              " eventId=" + eventId + " → drove native prompt→result (client click advanced the flow)");
                }
                // Not driven (still paging) → keep buffered; the next non-paging SetEncounter for this occId replays it.
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EncounterHostAdvanceReplayPatch failed: " + ex.Message); }
        }
    }
}
