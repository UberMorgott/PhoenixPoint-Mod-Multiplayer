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

    /// <summary>Rename dirty seam (echo-skip review 2026-07-08): <c>GeoCharacter.Rename</c> writes
    /// <c>Identity.Name</c> (GeoCharacter.cs:826) — mirrored by the #9 blob's <c>_identity</c> value-copy
    /// (PersonnelReflection.ApplySoldierState) but covered by NEITHER the six PS2 mutators above NOR the
    /// membership/pool seams: without this mark a rename (host-local OR a host-applied RenameSoldierAction)
    /// reached clients only on the next hourly bulk. Same doctrine as the six above (IsHost-gated, no
    /// IsApplying skip); typed resolve mirrors RenameEditRelayPatch (exact-param-match canon).</summary>
    [HarmonyPatch]
    public static class GeoCharacterRenameStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            _target = t != null ? AccessTools.Method(t, "Rename", new[] { typeof(string) }) : null;
            Debug.Log("[Multiplayer] PersonnelStatePatches: GeoCharacter.Rename "
                      + (_target != null ? "bound" : "NOT FOUND — rename dirty seam disabled"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix(object __instance) => PersonnelStateDirty.Mark(__instance);
    }

    /// <summary>Progression dirty seams (progression-intents feature): <c>CharacterProgression</c> has no
    /// back-reference to its owning GeoCharacter, so an ability learn (<c>AddAbility</c> — LearnAbility and
    /// the mutoid path both funnel through it, CharacterProgression.cs:152/173) or a stat spend
    /// (<c>ModifyBaseStat</c> :201) marks the WHOLE roster (the hourly-bulk pattern); the per-soldier
    /// blob-hash skip culls the unchanged majority (R3). Covers BOTH origins symmetrically: a relayed
    /// LevelUpAbility/SpendStatPoints host apply AND the host player's own soldier-edit screen (whose raw
    /// <c>SkillPoints</c> field write has no seam of its own but always travels with one of these calls).
    /// Client-side fires are no-ops (MarkAll is _live-gated + IsHost-gated); the mirror's blob apply
    /// value-copies <c>_progression</c> without calling either method.</summary>
    [HarmonyPatch]
    public static class CharacterProgressionAddAbilityStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Characters.CharacterProgression");
            _target = t != null ? AccessTools.Method(t, "AddAbility") : null;
            Debug.Log("[Multiplayer] PersonnelStatePatches: CharacterProgression.AddAbility "
                      + (_target != null ? "bound" : "NOT FOUND — progression dirty seam disabled"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => PersonnelStateDirty.MarkAll();
    }

    [HarmonyPatch]
    public static class CharacterProgressionModifyBaseStatStateDirtyPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Entities.Characters.CharacterProgression");
            _target = t != null ? AccessTools.Method(t, "ModifyBaseStat") : null;
            Debug.Log("[Multiplayer] PersonnelStatePatches: CharacterProgression.ModifyBaseStat "
                      + (_target != null ? "bound" : "NOT FOUND — progression dirty seam disabled"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;
        public static void Postfix() => PersonnelStateDirty.MarkAll();
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
