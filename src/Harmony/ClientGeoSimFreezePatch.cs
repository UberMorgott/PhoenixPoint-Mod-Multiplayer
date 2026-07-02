using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using UnityEngine;

namespace Multipleer.Harmony
{
    /// <summary>
    /// Inc4 S0 — SCAFFOLDING (inert): the CLIENT geoscape sim-freeze re-assert hook. Design spec:
    /// docs/superpowers/specs/2026-07-02-multipleer-inc4-client-sim-freeze-design.md §3.1.
    ///
    /// Mirrors the re-assert precedent <see cref="Multipleer.Harmony.Sync.EventSuppressClientGeoscapePatch"/>
    /// (postfix on <c>GeoscapeEventSystem.OnLevelStart()</c>) — the same load-completion hook that runs AFTER
    /// the level's InstanceData is (re)applied on EVERY (re)load / save-blob apply
    /// (<c>GeoLevelController.LevelCrt</c> drives <c>EventSystem.InstanceData = ...</c> then
    /// <c>EventSystem.OnLevelStart()</c>). It is therefore the correct point to RE-ASSERT a freeze that the
    /// per-load <c>Timing.ProcessInstanceData(host data, Paused=false)</c> would otherwise reset.
    ///
    /// S0 body is INERT: gated on <see cref="ClientSimFreeze.ShouldFreeze"/> (flag default-OFF) it never
    /// mutates the geoscape clock — byte-unchanged in-game. It emits client-only telemetry so the re-assert
    /// hook is OBSERVABLE for the S0 in-game gate. S1 fills the body: on the client, set the live geoscape
    /// <c>Timing.Paused = true</c> via the setter (<c>RescheduleForTiming</c> Max's every already-Started
    /// producer) — the freeze mechanism itself (see spec §3.1-3.3).
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

        // Runs after every geoscape (re)load. S0: INERT — no clock mutation while the flag is OFF.
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

                // S0 telemetry (client only, flag-independent) — proves the re-assert hook fires each load.
                // Strip before publish (spec §8). Host / single-player: silent, untouched.
                if (onActiveClient)
                    Debug.Log("[Multipleer] ClientGeoSimFreezePatch reached (client geoscape load); "
                        + "ClientSimFreeze.Enabled=" + ClientSimFreeze.Enabled + " freeze=" + freeze
                        + " (S0 inert — clock pause lands in S1)");

                if (!freeze) return; // S0: flag default-OFF -> always here -> byte-unchanged, no clock pause

                // S1 TODO (spec §3.1): resolve the live GeoLevelController.Timing and set Timing.Paused = true
                // via the setter so RescheduleForTiming Max's every already-Started geoscape producer
                // (re-asserted on EVERY load). Kept out of S0 to ship inert scaffolding first.
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] ClientGeoSimFreezePatch failed: " + ex.Message); }
        }
    }
}
