using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// NATIVE-UI reflection bridge for co-op event-window "replay mode" (<c>EventReplayModeGate</c>): repaint the
    /// live <c>UIModuleSiteEncounters</c> choice buttons so the winning choice is HIGHLIGHTED (native selected
    /// state) and every other choice is greyed + non-interactable — the winner is the only clickable button.
    ///
    /// Reuses the game's OWN per-button widgets — NO custom overlay (native-UI-first):
    ///   • <c>UIModuleSiteEncounters.ChoicesButtonController</c> (public field, :45) → the choices controller
    ///     (<c>SiteBaseChoicesController</c>).
    ///   • <c>SiteBaseChoicesController.Choices</c> (protected <c>List&lt;SiteBaseChoiceButton&gt;</c>, :23) — index i
    ///     ↔ <c>EventData.Choices[i]</c> (SetChoices populates them in order, :42-45).
    ///   • <c>SiteBaseChoiceButton.Button</c> (public <c>PhoenixGeneralButton</c>, :19).
    ///   • <c>PhoenixGeneralButton.SetInteractable(bool)</c> (:283 — the native grey/disable, same call SetChoice
    ///     uses for failed requirements) and <c>IsSelected</c> + <c>ResetButtonAnimations()</c> (:184-189 — IsSelected
    ///     true renders the pressed/selected highlight). <c>IsNonInteractableWhenSelected</c> is forced false on the
    ///     winner so a selected button STAYS clickable (native default true would swallow the click, :327).
    ///
    /// Best-effort: any missing member disables the whole helper (fail-open — the legacy forced transition still
    /// works when the gate is OFF); every body is try/catch and NEVER throws into game code.
    /// </summary>
    public static class EventReplayReflection
    {
        private static bool _ready;
        private static bool _missing;
        private static FieldInfo _choicesControllerField;          // UIModuleSiteEncounters.ChoicesButtonController
        private static FieldInfo _controllerChoicesField;          // SiteBaseChoicesController.Choices (protected List)
        private static FieldInfo _buttonField;                     // SiteBaseChoiceButton.Button (PhoenixGeneralButton)
        private static MethodInfo _setInteractable;                // PhoenixGeneralButton.SetInteractable(bool)
        private static MethodInfo _resetButtonAnimations;          // PhoenixGeneralButton.ResetButtonAnimations()
        private static FieldInfo _isSelectedField;                 // PhoenixGeneralButton.IsSelected (bool)
        private static FieldInfo _nonInteractableWhenSelectedField; // PhoenixGeneralButton.IsNonInteractableWhenSelected (bool)
        // Requirement-aware restore (ClearReplayButtons): SiteBaseChoiceButton.Choice (public property, :26) →
        // GeoEventChoice.Requirments (public field, sic — game typo, GeoEventChoice.cs:15). OPTIONAL — a miss only
        // skips the defensive interactable restore, never gates _ready (native SetChoice re-applies it anyway).
        private static PropertyInfo _choiceProp;                   // SiteBaseChoiceButton.Choice (GeoEventChoice)
        private static FieldInfo _requirementsField;               // GeoEventChoice.Requirments

        private static void Ensure()
        {
            if (_ready || _missing) return;
            var moduleT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleSiteEncounters");
            var ctrlT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters.SiteBaseChoicesController");
            var btnT = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters.SiteBaseChoiceButton");
            var pgbT = AccessTools.TypeByName("PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton");
            if (moduleT == null || ctrlT == null || btnT == null || pgbT == null) { _missing = true; return; }
            _choicesControllerField = AccessTools.Field(moduleT, "ChoicesButtonController");
            _controllerChoicesField = AccessTools.Field(ctrlT, "Choices");
            _buttonField = AccessTools.Field(btnT, "Button");
            _setInteractable = AccessTools.Method(pgbT, "SetInteractable", new[] { typeof(bool) });
            _resetButtonAnimations = AccessTools.Method(pgbT, "ResetButtonAnimations");
            _isSelectedField = AccessTools.Field(pgbT, "IsSelected");
            _nonInteractableWhenSelectedField = AccessTools.Field(pgbT, "IsNonInteractableWhenSelected");
            _choiceProp = AccessTools.Property(btnT, "Choice");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            _requirementsField = choiceT != null ? AccessTools.Field(choiceT, "Requirments") : null;
            _missing = _choicesControllerField == null || _controllerChoicesField == null || _buttonField == null
                       || _setInteractable == null || _resetButtonAnimations == null || _isSelectedField == null
                       || _nonInteractableWhenSelectedField == null;
            _ready = !_missing;
        }

        // The active choice-button widgets (SiteBaseChoiceButton), index-aligned with EventData.Choices; null if
        // the module/controller is unreadable.
        private static IList GetChoiceButtons(object module)
        {
            var controller = _choicesControllerField.GetValue(module);
            if (controller == null) return null;
            return _controllerChoicesField.GetValue(controller) as IList;
        }

        /// <summary>
        /// Arm replay mode on <paramref name="module"/>: highlight the button at <paramref name="winningIndex"/>
        /// (native selected state, kept clickable) and grey + disable every other shown choice button. Best-effort.
        /// </summary>
        public static void ApplyReplayButtons(object module, int winningIndex)
        {
            try
            {
                Ensure();
                if (!_ready || module == null) return;
                var list = GetChoiceButtons(module);
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var choiceButton = list[i];
                    if (choiceButton == null) continue;
                    // Skip pooled buttons the controller hid for this event (surplus beyond the choice count).
                    var go = (choiceButton as Component)?.gameObject;
                    if (go != null && !go.activeSelf) continue;
                    var pgb = _buttonField.GetValue(choiceButton);
                    if (pgb == null) continue;
                    if (i == winningIndex)
                    {
                        // Winner: keep interactable + render the native selected/pressed highlight. Force
                        // IsNonInteractableWhenSelected=false so the highlighted button STAYS clickable (the local
                        // player clicks it to advance to the authoritative result page).
                        _nonInteractableWhenSelectedField.SetValue(pgb, false);
                        _setInteractable.Invoke(pgb, new object[] { true });
                        _isSelectedField.SetValue(pgb, true);
                        _resetButtonAnimations.Invoke(pgb, null);
                    }
                    else
                    {
                        _isSelectedField.SetValue(pgb, false);
                        _setInteractable.Invoke(pgb, new object[] { false });   // grey + block (native)
                    }
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReplayReflection.ApplyReplayButtons failed: " + ex.Message); }
        }

        /// <summary>
        /// Reset every choice button's native selected/highlight state back to default (IsSelected=false,
        /// IsNonInteractableWhenSelected=true) so a PRIOR replay arm never leaks its highlight onto the next event
        /// rendered by the POOLED module. Also DEFENSIVELY restores interactability for requirement-FREE choices
        /// (Choice.Requirments == null ⇒ PassRequirements is unconditionally true, GeoEventChoice.cs:24-31) in case
        /// a render path ever skips the native re-apply. Requirement-GATED buttons are deliberately left to the
        /// native verdict: SetChoice (SiteBaseChoicesController.cs:60) just re-ran
        /// <c>SetInteractable(PassRequirements(...))</c> in this same render, and force-enabling one would let the
        /// player pick an unaffordable choice (OnChoiceSelected does Wallet.Take unconditionally, :573). Called on
        /// every fresh render (host + client). Best-effort.
        /// </summary>
        public static void ClearReplayButtons(object module)
        {
            try
            {
                Ensure();
                if (!_ready || module == null) return;
                var list = GetChoiceButtons(module);
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var choiceButton = list[i];
                    if (choiceButton == null) continue;
                    var pgb = _buttonField.GetValue(choiceButton);
                    if (pgb == null) continue;
                    _isSelectedField.SetValue(pgb, false);
                    _nonInteractableWhenSelectedField.SetValue(pgb, true);
                    // Defensive un-grey after a prior ArmReplay: only for a requirement-free choice (always passes
                    // → interactable is unconditionally true natively). Optional members; a miss skips it (native
                    // SetChoice already restored interactability in this render).
                    if (_choiceProp != null && _requirementsField != null)
                    {
                        var choice = _choiceProp.GetValue(choiceButton, null);
                        if (choice != null && _requirementsField.GetValue(choice) == null)
                            _setInteractable.Invoke(pgb, new object[] { true });
                    }
                    _resetButtonAnimations.Invoke(pgb, null);
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] EventReplayReflection.ClearReplayButtons failed: " + ex.Message); }
        }
    }
}
