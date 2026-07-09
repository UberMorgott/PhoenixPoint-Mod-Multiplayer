using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// TFTV parity for the HEADLESS host augment apply (<see cref="PersonnelEditReflection.Augment"/>).
    /// TFTV hooks its augment side-effects on the augmentation UI seams (real src, refs/TFTV-src):
    ///   • <c>UIModuleMutationSection.ApplyMutation</c> Postfix → stamina SetToMin, gated on
    ///     <c>TFTVNewGameOptions.StaminaPenaltyFromInjurySetting</c> (TFTVStamina.cs:218) + the NJ
    ///     broke-promise diplomacy variable (TFTVAugmentations.cs:623);
    ///   • <c>UIModuleBionics.OnAugmentApplied</c> Postfix → Anu broke-promise variable + stamina SetToMin +
    ///     delirium −4 corruption / torso-bionic wolverine removal (TFTVHarmonyTactical.cs:62,
    ///     TFTVDelirium.cs:794, TFTVStamina.cs:248, TFTVAugmentations.cs:655).
    /// A relayed client augment applies on the host WITHOUT those UI methods, so the postfixes never fire —
    /// this bridge replicates the exact value-writes against the target <c>GeoCharacter</c> (TFTV's statics
    /// take live UI modules, so they are not headless-callable). Host-NATIVE augments keep their UI postfixes
    /// (this runs ONLY from the relayed-intent apply) — never double-applied. Everything is null-safe
    /// best-effort per effect: TFTV absent → one resolve miss → permanent no-op; a single failed effect logs
    /// a warning and never blocks the rest of the apply.
    /// </summary>
    public static class TftvAugmentCompat
    {
        private static bool _ensured;
        private static bool _present;                    // TFTV assembly resolved
        private static FieldInfo _staminaSettingField;   // TFTV.TFTVNewGameOptions.StaminaPenaltyFromInjurySetting (public static bool)
        private static FieldInfo _wolverineField;        // TFTV.TFTVDelirium.wolverineDef (static TacticalAbilityDef)
        private static FieldInfo _wolverineCuredField;   // TFTV.TFTVDelirium.wolverineCuredDef (static TacticalAbilityDef)

        // Game members (bound lazily, null-safe) — decompile-verified 2026-07-09: GeoCharacter.cs:131/225/243,
        // CharacterFatigue.cs:22, StatusStat.cs:119, CharacterStats.cs:34, BaseStat.cs:95/133,
        // GeoLevelController.cs:105, GeoscapeEventSystem.cs:251/265.
        private static PropertyInfo _fatigueProp;        // GeoCharacter.Fatigue (CharacterFatigue)
        private static PropertyInfo _staminaProp;        // CharacterFatigue.Stamina (StatusStat)
        private static MethodInfo _setToMin;             // StatusStat.SetToMin()
        private static PropertyInfo _charStatsProp;      // GeoCharacter.CharacterStats
        private static FieldInfo _corruptionField;       // CharacterStats.Corruption (public readonly BaseStat)
        private static MethodInfo _statToFloat;          // BaseStat.op_Implicit(BaseStat) → float
        private static MethodInfo _statSet;              // BaseStat.Set(float, bool)
        private static PropertyInfo _origFactionProp;    // GeoCharacter.OriginalFactionDef (GeoFactionDef)
        private static PropertyInfo _progressionProp;    // GeoCharacter.Progression (CharacterProgression)
        private static FieldInfo _abilitiesField;        // CharacterProgression._abilities (List<TacticalAbilityDef>)
        private static FieldInfo _eventSystemField;      // GeoLevelController.EventSystem (public field)
        private static MethodInfo _getVariable;          // GeoscapeEventSystem.GetVariable(string)
        private static MethodInfo _setVariable;          // GeoscapeEventSystem.SetVariable(string, int)

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;
            try
            {
                var options = AccessTools.TypeByName("TFTV.TFTVNewGameOptions");
                var delirium = AccessTools.TypeByName("TFTV.TFTVDelirium");
                if (options == null && delirium == null) return;   // TFTV not installed → permanent no-op
                _staminaSettingField = options != null ? AccessTools.Field(options, "StaminaPenaltyFromInjurySetting") : null;
                _wolverineField = delirium != null ? AccessTools.Field(delirium, "wolverineDef") : null;
                _wolverineCuredField = delirium != null ? AccessTools.Field(delirium, "wolverineCuredDef") : null;
                _present = true;
                Debug.Log("[Multiplayer] TftvAugmentCompat: TFTV detected — headless host augments apply TFTV side-effects");
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat.Ensure failed: " + ex.Message); }
        }

        /// <summary>HOST, after a relayed augment applied natively-equivalent: run the TFTV augment
        /// side-effects the skipped UI postfixes would have run. No-op without TFTV.</summary>
        public static void OnHostAugmentApplied(GeoRuntime rt, object soldier, object augmentDef, bool isBionic)
        {
            Ensure();
            if (!_present || soldier == null) return;

            // Stamina SetToMin — the ApplyMutation postfix fires for BOTH families (the section is shared);
            // gated on TFTV's StaminaPenaltyFromInjurySetting exactly like TFTVStamina.cs:235.
            try
            {
                if (_staminaSettingField?.GetValue(null) is bool penalty && penalty)
                {
                    if (_fatigueProp == null) _fatigueProp = AccessTools.Property(soldier.GetType(), "Fatigue");
                    object fatigue = _fatigueProp?.GetValue(soldier, null);
                    if (_staminaProp == null && fatigue != null) _staminaProp = AccessTools.Property(fatigue.GetType(), "Stamina");
                    object stamina = fatigue != null ? _staminaProp?.GetValue(fatigue, null) : null;
                    if (_setToMin == null && stamina != null) _setToMin = AccessTools.Method(stamina.GetType(), "SetToMin");
                    if (stamina != null) _setToMin?.Invoke(stamina, null);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat stamina effect failed: " + ex.Message); }

            if (isBionic)
            {
                // Anu broke-promise variable (TFTVAugmentations.CheckAnuPissedBionicsBrokenPromise).
                try
                {
                    object es = EventSystem(rt);
                    if (es != null && GetVar(es, "BG_Anu_Pissed_Made_Promise") == 1)
                        SetVar(es, "BG_Anu_Pissed_Broke_Promise", 1);
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat Anu-promise effect failed: " + ex.Message); }

                // Delirium: each installed bionic reduces corruption by 4, floored at 0 (TFTVDelirium.cs:798).
                try
                {
                    if (_charStatsProp == null) _charStatsProp = AccessTools.Property(soldier.GetType(), "CharacterStats");
                    object stats = _charStatsProp?.GetValue(soldier, null);
                    if (_corruptionField == null && stats != null) _corruptionField = AccessTools.Field(stats.GetType(), "Corruption");
                    object corruption = stats != null ? _corruptionField?.GetValue(stats) : null;
                    if (corruption != null)
                    {
                        if (_statToFloat == null) _statToFloat = AccessTools.Method(_corruptionField.FieldType, "op_Implicit");
                        if (_statSet == null) _statSet = AccessTools.Method(_corruptionField.FieldType, "Set", new[] { typeof(float), typeof(bool) });
                        if (_statToFloat != null && _statSet != null)
                        {
                            float v = (float)_statToFloat.Invoke(null, new[] { corruption });
                            _statSet.Invoke(corruption, new object[] { v >= 4f ? v - 4f : 0f, true });
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat delirium effect failed: " + ex.Message); }

                // Torso bionic removes wolverine (cured first — TFTVDelirium.cs:807-820).
                try
                {
                    if (IsTorsoAugment(augmentDef))
                    {
                        if (_progressionProp == null) _progressionProp = AccessTools.Property(soldier.GetType(), "Progression");
                        object prog = _progressionProp?.GetValue(soldier, null);
                        if (_abilitiesField == null && prog != null) _abilitiesField = AccessTools.Field(prog.GetType(), "_abilities");
                        var abilities = prog != null ? _abilitiesField?.GetValue(prog) as IList : null;
                        object cured = _wolverineCuredField?.GetValue(null);
                        object wolverine = _wolverineField?.GetValue(null);
                        if (abilities != null)
                        {
                            if (cured != null && abilities.Contains(cured)) abilities.Remove(cured);
                            else if (wolverine != null && abilities.Contains(wolverine)) abilities.Remove(wolverine);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat wolverine effect failed: " + ex.Message); }
            }
            else
            {
                // NJ broke-promise variable (UIModuleMutationSection_ApplyMutation_PissedEvents_patch):
                // promise made + the character is ex-New-Jericho + a mutation was applied (the caller
                // already established the family tag).
                try
                {
                    object es = EventSystem(rt);
                    if (es != null && GetVar(es, "BG_NJ_Pissed_Made_Promise") == 1)
                    {
                        if (_origFactionProp == null) _origFactionProp = AccessTools.Property(soldier.GetType(), "OriginalFactionDef");
                        var orig = _origFactionProp?.GetValue(soldier, null) as UnityEngine.Object;
                        if (orig != null && orig.name == "NewJericho_GeoFactionDef")
                            SetVar(es, "BG_NJ_Pissed_Broke_Promise", 1);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[Multiplayer] TftvAugmentCompat NJ-promise effect failed: " + ex.Message); }
            }
        }

        /// <summary>The augment's first required slot is the human torso (the same
        /// <c>RequiredSlotBinds[0].RequiredSlot == Human_Torso_SlotDef</c> compare TFTV does, by def name).</summary>
        private static bool IsTorsoAugment(object augmentDef)
        {
            try
            {
                if (augmentDef == null) return false;
                if (!(AccessTools.Field(augmentDef.GetType(), "RequiredSlotBinds")?.GetValue(augmentDef) is Array binds) || binds.Length == 0) return false;
                object bind = binds.GetValue(0);
                var slot = bind != null ? AccessTools.Field(bind.GetType(), "RequiredSlot")?.GetValue(bind) as UnityEngine.Object : null;
                return slot != null && slot.name == "Human_Torso_SlotDef";
            }
            catch { return false; }
        }

        /// <summary>The live <c>GeoLevelController.EventSystem</c> (GeoscapeEventSystem), or null.</summary>
        private static object EventSystem(GeoRuntime rt)
        {
            var geo = rt?.GeoLevel();
            if (geo == null) return null;
            if (_eventSystemField == null) _eventSystemField = AccessTools.Field(geo.GetType(), "EventSystem");
            return _eventSystemField?.GetValue(geo);
        }

        private static int GetVar(object es, string name)
        {
            if (_getVariable == null) _getVariable = AccessTools.Method(es.GetType(), "GetVariable", new[] { typeof(string) });
            return _getVariable != null ? (int)_getVariable.Invoke(es, new object[] { name }) : 0;
        }

        private static void SetVar(object es, string name, int value)
        {
            if (_setVariable == null) _setVariable = AccessTools.Method(es.GetType(), "SetVariable", new[] { typeof(string), typeof(int) });
            _setVariable?.Invoke(es, new object[] { name, value });
        }
    }
}
