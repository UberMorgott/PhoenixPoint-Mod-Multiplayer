using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// TS7 — HOST AoE/explosion VFX trigger (<c>tac.vfx</c> 0x98). Postfix on the native prefab-VFX spawn
    /// chokepoint: <c>ExplosionEffect.SpawnObject(EffectTarget)</c> (ExplosionEffect.cs:47 — grenades, rockets,
    /// explosive deaths) AND its base <c>VolumeEffect.SpawnObject(EffectTarget)</c> (VolumeEffect.cs:34 — any other
    /// volume effect with an <c>ObjectToSpawn</c>). Both instantiate <c>VolumeEffectDef.ObjectToSpawn</c> at
    /// <c>target.Position</c>, and both run HOST-ONLY (the frozen client applies flattened damage via tac.damage but
    /// never runs the effect), so the client never sees the blast. The postfix hands {def guid, pos} to
    /// <see cref="TacticalVfxSync.HostBroadcastVfx"/>, which broadcasts it — skipping a simulation/prediction pass
    /// (SpawnObject early-returns without drawing on simulation).
    ///
    /// ExplosionEffect OVERRIDES SpawnObject (does not call base), so BOTH methods are patched to cover explosions
    /// AND base volume effects with no double-fire (an ExplosionEffect instance runs only its override; a base
    /// VolumeEffect runs only the base). Fire/goo/acid voxel volumes are a DIFFERENT system (SpawnTacticalVoxelEffect
    /// → TS3 0x94), so they never reach this seam. Reflective targets so an engine rename never PatchAll-bombs;
    /// best-effort try/catch — never blocks the native effect. Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class VfxBroadcastPatch
    {
        public static bool Prepare()
        {
            // At least one of the two SpawnObject targets must resolve, else skip the class.
            var vol = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Effects.VolumeEffect");
            var expl = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Effects.ExplosionEffect");
            return (vol != null && AccessTools.Method(vol, "SpawnObject") != null)
                || (expl != null && AccessTools.Method(expl, "SpawnObject") != null);
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            // protected void SpawnObject(EffectTarget target) — AccessTools finds the non-public method. Patch the
            // base AND the ExplosionEffect override (which reimplements the instantiate, not calling base).
            var vol = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Effects.VolumeEffect");
            var expl = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Effects.ExplosionEffect");
            var volM = vol != null ? AccessTools.Method(vol, "SpawnObject") : null;
            var explM = expl != null ? AccessTools.Method(expl, "SpawnObject") : null;
            if (volM != null) yield return volM;
            if (explM != null) yield return explM;
        }

        // __instance = the VolumeEffect/ExplosionEffect; target = the EffectTarget (carries Position). The gate
        // (host / not-simulation / readable def+pos) lives in HostBroadcastVfx so a stray call is a cheap no-op.
        public static void Postfix(object __instance, object target)
        {
            try { TacticalVfxSync.HostBroadcastVfx(__instance, target); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] VfxBroadcastPatch.Postfix failed: " + ex);
            }
        }
    }
}
