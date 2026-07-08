using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Augmentation co-op sync — GESTURE-AT-SOURCE adapter for the bionics/mutation screens (completes
    /// the <see cref="AugmentSoldierAction"/> back-end that was waiting for its UI chokepoint).
    ///
    /// Unlike the equip screen (<see cref="EquipGestureRelay"/>), augmentation has NO per-frame model
    /// flush — all writes are event-driven (<c>OnAugmentClicked</c> preview, <c>ApplyMutation</c> commit)
    /// — so only a gesture relay is needed (no flush suppression, no drag lifecycle).
    ///
    /// Flow (decompile-verified UIModuleMutationSection.cs / UIModuleBionics.cs / UIModuleMutate.cs):
    ///   1. <c>OnAugmentClicked</c> (preview): <c>SetItems(CharacterCurrentItems)</c> writes the new
    ///      bodyparts to the character model for the 3D preview — this runs natively on BOTH host and client.
    ///   2. <c>ApplyMutation</c> (commit): sets section state, calls <c>OnAugmentApplied</c> which does
    ///      displaced-item storage return, hand-loss handling, <c>Wallet.Take(ManufacturePrice)</c>,
    ///      snapshots originals, fires <c>AugmentApplied</c> event.
    ///
    /// CLIENT (gate on, active session, not applying):
    ///   • PREFIX on <c>UIModuleMutationSection.ApplyMutation</c> — reads the preview-written bodyparts
    ///     from the character model (<c>_armourItems</c>), builds ONE <see cref="AugmentSoldierAction"/>,
    ///     relays via <see cref="PersonnelEditRelay.Relay"/>, returns false (suppresses the native commit:
    ///     no wallet deduction, no storage mutation, no events on the frozen client).
    ///
    /// HOST / single-player: every patch is a pass-through (<see cref="PersonnelEditRelay.ShouldRelay"/>
    /// false). The host is fully native; the #9 personnel dirty seam mirrors the authoritative result.
    /// Game types are NEVER hard-referenced — targets resolve via AccessTools; Prepare false → PatchAll
    /// skips silently. Best-effort try/catch — never breaks the native gesture.
    /// </summary>
    internal static class AugmentGestureRelay
    {
        private static bool _ensured;
        private static FieldInfo _parentModuleField;     // UIModuleMutationSection._parentModule (IAugmentationUIModule)
        private static PropertyInfo _currentCharProp;    // UIModuleBionics/UIModuleMutate.CurrentCharacter

        private static void Ensure()
        {
            if (_ensured) return;
            _ensured = true;
            try
            {
                var sectionT = AccessTools.TypeByName(
                    "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection");
                if (sectionT != null)
                    _parentModuleField = AccessTools.Field(sectionT, "_parentModule");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] AugmentGestureRelay.Ensure failed: " + ex.Message);
            }
        }

        // Transition-only guard-exit latch (same pattern as EquipGestureRelay — one log line per
        // exit-reason transition, not per gesture, so human-click rate gestures are cheap to diagnose).
        private static string _lastExit;
        private static void LogExit(string guard)
        {
            if (guard == _lastExit) return;
            _lastExit = guard;
            Debug.Log("[Multiplayer] AugmentGestureRelay: gesture NOT relayed — guard=" + guard);
        }

        /// <summary>PREFIX on <c>UIModuleMutationSection.ApplyMutation</c>. Returns true (run native) on
        /// host/SP, false (suppress + relay) on a co-op client.</summary>
        internal static bool OnApplyMutation(object section)
        {
            try
            {
                if (!EquipSyncV2Gate.Enabled) { LogExit("gate-off"); return true; }
                if (!PersonnelEditRelay.ShouldRelay()) { LogExit("not-client"); return true; }

                Ensure();
                if (_parentModuleField == null) { LogExit("section-unresolved"); return true; }

                object parentModule = _parentModuleField.GetValue(section);
                if (parentModule == null) { LogExit("parent-module-null"); return true; }

                // CurrentCharacter is a public property on UIModuleBionics / UIModuleMutate (both
                // implement IAugmentationUIModule). Cache per concrete type (only two implementors).
                if (_currentCharProp == null || !_currentCharProp.DeclaringType.IsInstanceOfType(parentModule))
                    _currentCharProp = AccessTools.Property(parentModule.GetType(), "CurrentCharacter");
                object character = _currentCharProp?.GetValue(parentModule, null);
                if (character == null) { LogExit("no-current-character"); return true; }

                long unitId = PersonnelReflection.ReadUnitId(character);
                if (unitId == 0) { LogExit("unit-id-unresolved"); return true; }

                // The preview (OnAugmentClicked) already wrote the new bodyparts to the character via
                // SetItems — read them from the model (_armourItems) as the intended final bodypart set.
                string[] bodyparts = PersonnelEditReflection.ReadCurrentItemGuids(character, "_armourItems");

                _lastExit = null;   // reached the relay — re-arm the latch
                PersonnelEditRelay.Relay(ActionCategory.Equip, unitId, true,
                    () => new AugmentSoldierAction(unitId, bodyparts));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] AugmentGestureRelay.OnApplyMutation failed: " + ex.Message);
                return true;   // fail-open: let native run rather than silently eating the click
            }
            return false;   // suppress the native commit on the client
        }
    }

    // ─── gesture relay (one intent per committed augment) ─────────────────────────────────────────────

    [HarmonyPatch]
    public static class AugmentApplyGesturePatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName(
                "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection");
            _target = t != null ? AccessTools.Method(t, "ApplyMutation") : null;
            Debug.Log("[Multiplayer] AugmentGesturePatches: UIModuleMutationSection.ApplyMutation "
                      + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // Prefix: true = run native (host/SP), false = suppress (client relay).
        public static bool Prefix(object __instance)
            => AugmentGestureRelay.OnApplyMutation(__instance);
    }
}
