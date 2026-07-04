using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using UnityEngine;

namespace Multiplayer.Harmony
{
    /// <summary>
    /// Inc4 S1 — the CLIENT geoscape sim-freeze RE-ASSERT hook (active behind the flag). Design spec:
    /// docs/superpowers/specs/2026-07-02-multiplayer-inc4-client-sim-freeze-design.md §3.1.
    ///
    /// Mirrors the re-assert precedent <see cref="Multiplayer.Harmony.Sync.EventSuppressClientGeoscapePatch"/>
    /// (postfix on <c>GeoscapeEventSystem.OnLevelStart()</c>) — the same load-completion hook that runs AFTER
    /// the level's InstanceData is (re)applied on EVERY (re)load / save-blob apply
    /// (<c>GeoLevelController.LevelCrt</c> drives <c>Timing.ProcessInstanceData(host, Paused=false)</c> :515,
    /// then <c>EventSystem.OnLevelStart()</c> :655, then <c>Timing.Start(LevelHourlyUpdateCrt)</c> :761). It is
    /// therefore the correct point to RE-ASSERT a freeze the per-load reset would otherwise clear: our postfix
    /// runs at :655 — AFTER the host-data reset and BEFORE the hourly producer is Started — so the already-
    /// scheduled producers are Max'd by the setter's reschedule and the yet-to-Start hourly producer auto-Max's
    /// under the now-true <c>_paused</c>.
    ///
    /// Body (S1): on the client, <see cref="Multiplayer.Network.TimeSync.TimeSyncManager.FreezeClientGeoSim"/>
    /// sets the live geoscape <c>Timing.Paused = true</c> via the setter (<c>RescheduleForTiming</c> Max's every
    /// already-Started producer; a paused source ⇒ <c>NextUpdate.ConvertToTiming</c> returns Max). Gated on
    /// <see cref="ClientSimFreeze.ShouldFreeze"/> (flag default-OFF) — when OFF it early-returns before any clock
    /// mutation, so the class is byte-unchanged in-game (the legacy producer-table + event-suppress path stays
    /// as the flag-OFF rollback until S4). Rollback = flip <c>ClientSimFreeze.Enabled=false</c>.
    ///
    /// Verified vs decompile (2026-07-02): <c>GeoscapeEventSystem.OnLevelStart()</c> parameterless
    /// (GeoscapeEventSystem.cs:118) — the same target the precedent resolves. Reflection target so an engine
    /// rename never PatchAll-bombs (Prepare returns false -> class skipped). Best-effort try/catch: never
    /// throws into game code. HOST / single-player left untouched.
    /// </summary>
    [HarmonyPatch]
    public static class ClientGeoSimFreezePatch
    {
        private static MethodBase _target;   // GeoscapeEventSystem.OnLevelStart()

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEventSystem");
            if (t == null) return false; // engine not loaded -> Harmony skips this class
            _target = AccessTools.Method(t, "OnLevelStart", Type.EmptyTypes);
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Runs after every geoscape (re)load. Flag-OFF (default): early-returns before any clock mutation.
        public static void Postfix()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                bool onActiveClient = engine != null && engine.IsActiveSession && !engine.IsHost;
                bool freeze = ClientSimFreeze.ShouldFreeze(
                    ClientSimFreeze.Enabled,
                    engine != null,
                    engine != null && engine.IsActiveSession,
                    engine != null && engine.IsHost);

                // Telemetry (client only) — proves the re-assert hook fires each load. Strip before publish
                // (spec §8). Host / single-player: silent, untouched.
                if (onActiveClient)
                    Debug.Log("[Multiplayer] ClientGeoSimFreezePatch reached (client geoscape load); "
                        + "ClientSimFreeze.Enabled=" + ClientSimFreeze.Enabled + " freeze=" + freeze);

                if (!freeze) return; // flag OFF -> byte-unchanged, no clock pause (legacy suppress path stands)

                // S1 (spec §3.1): re-assert the sim freeze — set the live geoscape Timing.Paused = true via the
                // setter so RescheduleForTiming Max's every already-Started producer, re-asserted on EVERY load.
                engine.TimeSync?.FreezeClientGeoSim();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientGeoSimFreezePatch failed: " + ex.Message); }
        }
    }
}
