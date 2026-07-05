using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// PS2 per-soldier LIVE-STATE dirty seams (personnel-sync spec §3, taxonomy §8 minimal set):
    /// HOST-only Postfix hooks on the six <c>GeoCharacter</c> state mutators —
    /// <c>ApllyTacticalResult</c> (post-mission HP/XP/items/corruption/MC — native typo is canonical),
    /// <c>SetItems</c> (equip + AUGMENT: an augment is a body-part item swap through the same method),
    /// <c>Heal</c> / <c>SetInjured</c> / <c>DamageBodyPart</c> / <c>AddProgression</c> — plus the ONE
    /// hourly bulk driver <c>GeoPhoenixBase.BaseHourlyUpdate</c> (heal/stamina/train sweep, taxonomy §5):
    /// it marks the WHOLE roster once per base-hour; the marks coalesce into one flush and the
    /// per-soldier blob-hash skip culls everyone who did not actually change (R3). Fine-grained
    /// stamina/XP mutators (Stamina.AddRestrictedToMax, LevelProgression.AddExperience) are deliberately
    /// NOT hooked — they only fire inside the hourly sweep this driver already covers.
    ///
    /// Mirrored applies never re-mark: the client's <c>ApplySoldierState</c> DOES call native
    /// <c>SetItems</c>, but every mark here is IsHost-gated, so the client-side fire is a no-op (the
    /// membership-patches origin doctrine). A HOST mutation inside a relayed-action apply MUST dirty
    /// (authoritative write → mirror back) — no <c>SyncApplyScope.IsApplying</c> skip. Reflective
    /// targets (Prepare false → PatchAll skips silently — bind evidence logged); best-effort try/catch —
    /// never breaks the native mutator.
    /// </summary>
    internal static class PersonnelStateDirty
    {
        internal static MethodBase ResolveOnGeoCharacter(string methodName)
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            // Name-only resolve: all six targets are single-overload on GeoCharacter (decompile-checked
            // 2026-07-06), so this stays robust if a param type moves namespace between game versions.
            var m = t != null ? AccessTools.Method(t, methodName) : null;
            Debug.Log("[Multiplayer] PersonnelStatePatches: GeoCharacter." + methodName + " "
                      + (m != null ? "bound" : "NOT FOUND — state dirty seam disabled"));
            return m;
        }

        internal static void Mark(object character)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                PersonnelChannel.MarkSoldierStateDirtyExternal(PersonnelReflection.ReadUnitId(character));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelStatePatches.Mark failed: " + ex.Message); }
        }

        internal static void MarkAll()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                PersonnelChannel.MarkAllSoldiersStateDirtyExternal();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelStatePatches.MarkAll failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class GeoCharacterTacResultStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("ApllyTacticalResult");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class GeoCharacterSetItemsStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("SetItems");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class GeoCharacterHealStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("Heal");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class GeoCharacterSetInjuredStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("SetInjured");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class GeoCharacterDamageBodyPartStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("DamageBodyPart");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class GeoCharacterAddProgressionStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("AddProgression");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    [HarmonyPatch]
    public static class PhoenixBaseHourlyBulkStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase");
            _target = t != null ? AccessTools.Method(t, "BaseHourlyUpdate") : null;
            Debug.Log("[Multiplayer] PersonnelStatePatches: GeoPhoenixBase.BaseHourlyUpdate "
                      + (_target != null ? "bound" : "NOT FOUND — hourly bulk state seam disabled"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => PersonnelStateDirty.MarkAll();
    }
}
