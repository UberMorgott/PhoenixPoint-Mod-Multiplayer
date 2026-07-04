using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT-ONLY suppression of LOCAL geoscape-event raising AT THE SOURCE (primary, clean reducer).
    ///
    /// ROOT CAUSE: the client geoscape sim is NOT frozen — the time-sync clock overwrite advances
    /// <c>Timing.Now</c>, which drives the client's <c>GeoLevelController.LevelHourlyUpdateCrt</c> →
    /// <c>faction.UpdateResearch()</c> etc. → the client's own <c>GeoscapeEventSystem</c> raises its OWN
    /// geoscape events locally (host and client then show DIFFERENT events). Fix: set the native
    /// <c>GeoscapeEventSystem.SuppressEvents = true</c> on the CLIENT, which the game's own guard honors at
    /// the raise chokepoint (<c>OnGeoscapeEvent</c> early-returns when true — GeoscapeEventSystem.cs:612;
    /// it is the same flag the tutorial uses). This stops the wasted local sim work; the load-bearing
    /// guarantee remains the <c>EventRaisedDisplayPatch</c> client prefix.
    ///
    /// CRITICAL re-assert caveat (RCA-flagged): <c>SuppressEvents</c> is part of the persisted snapshot —
    /// <c>OnLevelStart</c> reapplies <c>SuppressEvents = InstanceData.SupressEvents</c> (the host's value,
    /// false) whenever a geoscape (re)loads (GeoscapeEventSystem.cs:151, driven by
    /// <c>GeoLevelController.LevelCrt</c> :614→:656). So a client receiving the host's full geoscape state
    /// via the initial save-transfer load (or any later full-state reload) would have the flag reset.
    /// We therefore set it in a POSTFIX on <c>GeoscapeEventSystem.OnLevelStart()</c>, which runs AFTER that
    /// reset on EVERY load — handling both the initial set and the re-assert against a full-state reload in
    /// one place. <c>__instance</c> is the live <c>GeoscapeEventSystem</c>, so we set the property directly.
    ///
    /// HOST is left untouched (host must keep raising + broadcasting its events). Gated strictly on
    /// <c>!IsHost</c> + active co-op session. Best-effort try/catch — never throws into game code.
    ///
    /// Verified against the decompile (2026-06-17):
    ///   • <c>GeoscapeEventSystem.SuppressEvents { get; set; }</c> public auto-property (GeoscapeEventSystem.cs:106).
    ///   • <c>GeoscapeEventSystem.OnLevelStart()</c> parameterless (GeoscapeEventSystem.cs:118); reapplies
    ///     <c>SuppressEvents = InstanceData.SupressEvents</c> when an InstanceData snapshot is present (:151).
    ///   • raise guard: <c>OnGeoscapeEvent</c> returns early when <c>SuppressEvents</c> is true (:612).
    ///   • <c>GeoLevelController.EventSystem</c> public field (GeoLevelController.cs:105); level init runs
    ///     <c>EventSystem.InstanceData = ...</c> (:614) then <c>EventSystem.OnLevelStart()</c> (:656).
    /// </summary>
    [HarmonyPatch]
    public static class EventSuppressClientGeoscapePatch
    {
        private static MethodBase _target;
        private static PropertyInfo _suppressEventsProp;   // GeoscapeEventSystem.SuppressEvents { set; }

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventSystem");
            if (t == null) return false;
            _target = AccessTools.Method(t, "OnLevelStart", Type.EmptyTypes);
            _suppressEventsProp = AccessTools.Property(t, "SuppressEvents");
            return _target != null && _suppressEventsProp != null && _suppressEventsProp.CanWrite;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the GeoscapeEventSystem whose OnLevelStart() just (re)applied the persisted
        // SuppressEvents. On a client we force it true again so no local geoscape events are ever raised.
        public static void Postfix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || engine.IsHost) return; // host/non-session: untouched
                if (__instance == null || _suppressEventsProp == null) return;
                _suppressEventsProp.SetValue(__instance, true, null);
                Debug.Log("[Multiplayer] client GeoscapeEventSystem.SuppressEvents = true (no local event raises)");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventSuppressClientGeoscapePatch failed: " + ex.Message); }
        }
    }
}
