using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// STRUCTURAL-DESTRUCTION mirror host CAPTURE chokepoint (spec TS6). A single HOST postfix on the leaf
    /// <c>DestructableDamageReceiver.ApplyDamage(DamageResult)</c> — the one funnel every combat hit (shot / burst /
    /// grenade / explosion / overwatch) to a wall / floor / prop passes through (each affected TILE of a
    /// <c>Destructable</c> + the single receiver of a <c>Breakable</c>). It hands the receiver + the applied
    /// <c>DamageResult</c> to <see cref="TacticalStructDamageSync.HostCaptureStructDamage"/>, which buffers the hit
    /// into the flush heartbeat (broadcast as 0x96). DISJOINT from TS3's ground-hazard voxels — those ride
    /// <c>TacticalVoxel.SetVoxelType</c>, a DIFFERENT leaf. All gating lives in the sync layer (host + active session +
    /// deploy-captured + not applying-remote + hp&gt;0 + resolvable guid), so a stray call off-host / pre-deploy /
    /// during a client replay is a clean no-op. Postfix so it captures the FINAL applied damage. Auto-registers via
    /// PatchAll; reflection-target lazily like the sibling tactical patches.
    /// </summary>
    [HarmonyPatch]
    public static class StructDamageCapturePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Levels.Destruction.DestructableDamageReceiver");
            if (t == null) return false;
            var dr = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.DamageResult");
            if (dr == null) return false;
            // public void ApplyDamage(DamageResult damageResult) — EXACT param match (DamageResult struct).
            _target = AccessTools.Method(t, "ApplyDamage", new[] { dr });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the DestructableDamageReceiver; __0 = the DamageResult argument (boxed) → reflected by the sync layer.
        public static void Postfix(object __instance, object __0)
        {
            try { TacticalStructDamageSync.HostCaptureStructDamage(__instance, __0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] StructDamageCapturePatch.Postfix failed: " + ex);
            }
        }
    }
}
