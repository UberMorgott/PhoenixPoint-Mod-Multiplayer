using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// CLIENT: objective-XP rows in the BattleSummary render the HOST's numbers. ObjectiveResultElement
    /// (ObjectiveResultElement.cs:22) shows <c>FactionObjective.GetActualExperienceReward()</c> =
    /// BaseExperienceReward × the VIRTUAL GetCompletion() × faction multiplier — and the completion counters
    /// (soldiers evacuated / crates collected / …) live host-only, so a mirroring client always rendered 0
    /// (and ВСЕГО, the sum of the rows, stayed 0). The 0x95 conclusion now carries the host-rendered value
    /// per objective ordinal; this prefix returns it for the mirrored live objective instance. Nothing
    /// mirrored (host / single-player / pre-conclusion) → native runs untouched — the ONE non-virtual
    /// chokepoint covers every objective subclass.
    /// </summary>
    [HarmonyPatch]
    public static class ObjectiveRewardMirrorPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.FactionObjectives.FactionObjective");
            _target = t != null ? AccessTools.Method(t, "GetActualExperienceReward") : null;
            if (_target == null)
                Debug.LogWarning("[Multiplayer][tac] ObjectiveRewardMirrorPatch NOT bound: FactionObjective.GetActualExperienceReward not found");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, ref int __result)
        {
            if (!TacticalMissionEndSync.TryGetMirroredObjectiveXp(__instance, out int xp)) return true;
            __result = xp;
            return false;
        }
    }
}
