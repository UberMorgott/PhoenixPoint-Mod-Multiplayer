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

        /// <summary>Mark the inventory (#1) storage channel dirty (HOST-only). Folded here from the deleted
        /// StorageDirtyPatches (v2 rebuild): the ONLY equip storage mutation that reaches clients is the native
        /// UIStateEditSoldier.UpdateStorage diff — which runs in the SAME synchronous UpdateState pass as
        /// UpdateSoldierEquipment→SetItems, TWO lines after it (:470/:474). So marking #1 from the SetItems
        /// Postfix has CORRECT timing (the storage store is written before the next SyncEngine.Tick drains #1)
        /// AND is once-per-edit, NOT per-frame (UpdateState gates the whole flush on _uiRefreshNeeded, reset each
        /// flush) — satisfying the ZERO-per-frame-work mandate without a mark inside UpdateStorage itself.</summary>
        internal static void MarkStorage()
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                engine.Sync?.MarkChannelDirty(SurfaceIds.InventoryChannel);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelStatePatches.MarkStorage failed: " + ex.Message); }
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

    /// <summary>Equip/augment dirty seam with CHANGE-DETECTION (RCA 2026-07-08 round 2). GeoCharacter.SetItems
    /// IS per-frame on a TFTV host with the equip screen open: the native flush gate (_uiRefreshNeeded,
    /// UIStateEditSoldier.UpdateState:459-461) is DEFEATED by TFTV — its weight patches (GetPrimaryWeight/
    /// RefreshWeightSlider, TFTVUI\Personnel\Stats.cs:588) run INSIDE the flush (:467) and invoke
    /// UIModuleCharacterProgression.StatChanged, which the state maps to RequestRefreshCharacterData (:108)
    /// → re-arms _uiRefreshNeeded EVERY flush ("constant stat updates", TFTV's own comment :609) → flush →
    /// SetItems every frame. An unconditional Postfix mark therefore stormed #9 (silent full-graph
    /// PersonnelBlob serialize per Tick — the hash-skip culls only the WIRE) + #1 (full storage broadcast per
    /// Tick, NO content skip) → the client applied + re-Init'd its open UI per Tick → the field freeze (logs
    /// 18:09:36: both peers go silent at the first #9 flush after the host opened soldier 5's equip screen).
    ///
    /// Fix: mark ONLY when the item lists actually CHANGE — never by call-site (host real edits flow through
    /// this same flush; suppressing the call-site would blind them). The Prefix compares each non-null arg
    /// against the soldier's CURRENT list BEFORE the clear+refill, by per-element REFERENCE equality: an
    /// unchanged TFTV re-flush re-pushes the SAME GeoItem instances in the SAME order (the UI lists hold the
    /// model's instances) ⇒ equal ⇒ no mark, zero serialization; a genuine edit reorders/adds/removes
    /// instances — and a mirror/relayed apply builds FRESH instances — ⇒ marks. Args are re-enumerable
    /// (List / LINQ OfType/Select over lists at every caller, decompile-swept), so the extra enumeration is
    /// safe. The unchanged path allocates nothing but the boxed enumerators (native's own flush LINQ dwarfs
    /// it). Fail-open: any resolve/compare miss ⇒ treat as changed (a missed mark = stale client forever, a
    /// false mark = one redundant flush). Known gap: an IN-PLACE item mutation with no list change (host
    /// reload's ammo write) doesn't mark — it converges on the hourly bulk sweep (blob-hash diff ships it).</summary>
    [HarmonyPatch]
    public static class GeoCharacterSetItemsStateDirtyPatch
    {
        private static MethodBase _target;
        private static FieldInfo _armourField;   // GeoCharacter._armourItems (List<GeoItem>)
        private static FieldInfo _equipField;    // GeoCharacter._equipmentItems
        private static FieldInfo _invField;      // GeoCharacter._inventoryItems

        public static bool Prepare()
        {
            _target = PersonnelStateDirty.ResolveOnGeoCharacter("SetItems");
            if (_target != null)
            {
                var t = _target.DeclaringType;
                _armourField = AccessTools.Field(t, "_armourItems");
                _equipField = AccessTools.Field(t, "_equipmentItems");
                _invField = AccessTools.Field(t, "_inventoryItems");
            }
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0/__1/__2 = armour/equipment/inventory IEnumerable<GeoItem> args (null = leave that list unchanged).
        // __state = "the lists actually change" — computed BEFORE the native clear+refill.
        public static void Prefix(object __instance, object __0, object __1, object __2, out bool __state)
        {
            __state = false;
            try
            {
                // Cheap host gate first: off-host / no session the marks are no-ops anyway — skip the compare
                // so single-player TFTV (same per-frame loop) pays zero work here.
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                // Augment-screen PREVIEW click (OnAugmentClicked bracket via AugmentPreviewScopePatch): a
                // transient UI-local write, reverted on Escape/exit — never authoritative state. Marking it
                // broadcast the uncommitted preview on #9/#1 and repainted the other peer's open augment
                // screen mid-preview (preview regression RCA 2026-07-09). Reverts keep their marks
                // (hash-culled baseline re-stamp = self-heal) and the COMMIT re-marks via
                // AugmentCommitDirtyPatch (OnAugmentApplied), so real augments still mirror.
                if (AugmentPreviewScope.Active) return;
                __state = ListChanged(__0, _armourField, __instance)
                          || ListChanged(__1, _equipField, __instance)
                          || ListChanged(__2, _invField, __instance);
            }
            catch { __state = true; }   // fail-open: never let a compare error silently kill sync
        }

        public static void Postfix(object __instance, bool __state)
        {
            if (!__state) return;
            // Mark BOTH #9 (soldier blob) AND #1 (storage): a host equip-from-storage's UpdateStorage runs in
            // the same synchronous UpdateState pass (:474), and the host-apply of a relayed EquipSoldierAction
            // reconciles storage right after SetItems — both settle before the next Tick drains the channels.
            PersonnelStateDirty.Mark(__instance);
            PersonnelStateDirty.MarkStorage();
        }

        /// <summary>True when applying <paramref name="arg"/> would change the soldier's current list:
        /// different length or any position holding a DIFFERENT GeoItem instance. Null arg = native
        /// leave-unchanged ⇒ false. Unresolvable current list ⇒ true (fail-open).</summary>
        private static bool ListChanged(object arg, FieldInfo currentField, object soldier)
        {
            if (arg == null) return false;
            if (currentField == null) return true;
            if (!(currentField.GetValue(soldier) is System.Collections.IList current)) return true;
            if (!(arg is System.Collections.IEnumerable items)) return true;
            int n = 0;
            foreach (var item in items)
            {
                if (n >= current.Count || !ReferenceEquals(item, current[n])) return true;
                n++;
            }
            return n != current.Count;
        }
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
