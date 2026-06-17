using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using UnityEngine;

namespace Multipleer.Harmony.Sync
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
            catch (Exception ex) { Debug.LogError("[Multipleer] EventRaisedDisplayPatch.Prefix failed: " + ex.Message); }
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
                int siteId = EventReflection.GetSiteId(geoEvent);
                engine.Sync?.BroadcastEventRaised(eventId, siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] EventRaisedDisplayPatch failed: " + ex.Message); }
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
                engine.Sync?.BroadcastEventDismiss(eventId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] CompleteEventDismissPatch failed: " + ex.Message); }
        }
    }
}
