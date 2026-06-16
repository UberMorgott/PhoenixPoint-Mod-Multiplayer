using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// Relay interceptor for geoscape event choices:
    /// <c>GeoscapeEvent.CompleteEvent(GeoEventChoice choice, GeoFaction faction)</c> (GeoscapeEvent.cs:86).
    /// The event UI force-pauses the (synced) clock; the host's choice is authoritative. Client picks →
    /// relay + block local; host pick → broadcast + run. If both pick simultaneously, the later
    /// host-sequenced answer wins; the earlier UI simply closes on apply.
    /// </summary>
    [HarmonyPatch]
    public static class CompleteEventPatch
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

        // __instance = GeoscapeEvent; choice = the chosen GeoEventChoice (may be null = decline).
        // __state carries the host action to broadcast AFTER the original completes successfully (Postfix).
        public static bool Prefix(object __instance, object choice, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            if (!PermissionGate.Check(ActionCategory.Dialogs))
            {
                PermissionGate.Notify(ActionCategory.Dialogs);
                return false;
            }

            try
            {
                string eventId = EventReflection.GetEventId(__instance);
                int choiceIndex = EventReflection.GetChoiceIndex(__instance, choice);
                if (string.IsNullOrEmpty(eventId)) return true;
                // Could not resolve a real (non-null) choice → do NOT broadcast a bogus decline (-1).
                // Fail OPEN to local vanilla handling instead of replicating the wrong outcome.
                if (choiceIndex == EventReflection.ChoiceLookupFailed) return true;
                var action = new AnswerEventAction(eventId, choiceIndex);
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CompleteEventPatch failed: " + ex.Message);
                return true;
            }
        }

        // Runs only if the original CompleteEvent returned normally (Harmony skips Postfix on a thrown
        // original) → broadcast the host's confirmed answer. Host-only via __state being set in Prefix.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multipleer] CompleteEventPatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
