using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// CLIENT research-RATE override: postfix on <c>Research.GetHourlyResearchProduction(ResearchDef)</c>
    /// (PRIVATE, Research.cs:649) replacing the locally-computed rate with the host's synced one
    /// (<see cref="ClientResearchRate"/>, fed by the ch2 snapshot's v3 rate block). This is the ETA fix:
    /// <c>Research.GetTotalTimeLeft</c> (Research.cs:694) divides remaining cost by this rate, and the UI
    /// (ResearchListItem.SetTime:207 / ResearchQueueItem.Init:125) renders it as the completion date —
    /// with the rate computed from client-local facility production the dates diverge from the host's.
    ///
    /// <c>[HarmonyPriority(Priority.Last)]</c> so this runs AFTER any other postfix on the method —
    /// specifically TFTV's Void Omen 6 ×1.5 multiplier (TFTVVoidOmens.cs:1027) — and the synced host rate
    /// (which already includes the host-side TFTV multiplication) wins. Guards (pure table in
    /// <see cref="ClientResearchRate.ShouldOverride"/>): active-session CLIENT only (host/single-player
    /// inert), only after a first synced value exists (fresh join keeps the local rate), and only for the
    /// local Phoenix faction's own <c>Research</c> instance — <c>GetAlliesContribution</c> (Research.cs:705)
    /// calls this same method on ALLY Research instances, which must keep their client-local rate
    /// (accepted edge: the NPC-ally ETA term may diverge slightly; rare and minor, not mirrored).
    /// </summary>
    [HarmonyPatch]
    public static class ClientResearchRatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research.Research");
            if (t == null) return false;
            // Bound by NAME ONLY (single overload) — AccessTools.Method(type, name, Type[]) does EXACT
            // param matching and would return null SILENTLY on a ResearchDef Type mismatch.
            _target = AccessTools.Method(t, "GetHourlyResearchProduction");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(object __instance, ref float __result)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                float? rate = ClientResearchRate.SyncedRate;
                bool engineExists = engine != null;
                bool isActive = engineExists && engine.IsActiveSession;
                bool isHost = engineExists && engine.IsHost;
                // Resolve the instance identity ONLY on a live client with a synced value — GetResearch
                // is a (cached) reflection walk and this postfix runs for every ETA the research UI computes.
                bool isLocalPhoenix = isActive && !isHost && rate.HasValue
                    && ReferenceEquals(__instance, ResearchStateReflection.GetResearch(GeoRuntime.Instance));
                if (!ClientResearchRate.ShouldOverride(engineExists, isActive, isHost, rate.HasValue, isLocalPhoenix))
                    return;
                __result = rate.Value;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ClientResearchRatePatch failed: " + ex.Message); }
        }
    }
}
