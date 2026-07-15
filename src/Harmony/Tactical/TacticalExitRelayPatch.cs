using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// SIMULTANEOUS tactical exit (tac.exit 0xC2/0xC3 — user directive 2026-07-15): every BattleSummary
    /// "back to geoscape" click converges on the ONE private chokepoint <c>TacticalView.GoToGeoscape</c>
    /// (TacticalView.cs:1109-1121, the BattleSummary finished-callback → FinishLevel). Canon suppress+relay:
    ///   • Prefix, CLIENT in session: suppress the local exit, relay an exit-INTENT to the host
    ///     (<see cref="TacticalMissionEndSync.ClientInterceptExit"/>). The GO re-entrancy invocation passes.
    ///   • Prefix, HOST/single-player: native exit runs.
    ///   • Postfix, HOST in session: broadcast exit-GO once (<see cref="TacticalMissionEndSync.HostAfterExit"/>)
    ///     → every client invokes its OWN GoToGeoscape → one trigger, everyone leaves together. No save
    ///     re-transfer anywhere: each instance returns via its mission-entry geoscape section.
    /// ponytail: the FINAL-mission branch (GetLevelFinishedViewState → GoToGameSummary, TacticalView.cs:1097-1099)
    /// is NOT relayed — campaign-end flow is its own rail; cover it when co-op reaches a final mission.
    /// </summary>
    [HarmonyPatch]
    public static class TacticalExitRelayPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.View.TacticalView");
            _target = t != null ? AccessTools.Method(t, "GoToGeoscape") : null;
            if (_target == null)
                Debug.LogWarning("[Multiplayer][tac] TacticalExitRelayPatch NOT bound: TacticalView.GoToGeoscape not found");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // true = run native (host / single-player / GO re-entry); false = suppressed client click (intent relayed).
        public static bool Prefix() => !TacticalMissionEndSync.ClientInterceptExit();

        public static void Postfix() => TacticalMissionEndSync.HostAfterExit();
    }
}
