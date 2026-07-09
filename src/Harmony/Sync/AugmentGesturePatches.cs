using System;
using System.Collections;
using System.Collections.Generic;
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
    ///   • PREFIX on <c>UIModuleMutationSection.ApplyMutation</c> — captures the CHOSEN augment
    ///     (<c>_selectedMutationSlot.Mutation</c>, the exact def the native commit would apply) as its
    ///     stable def guid, relays ONE INTENT <see cref="AugmentSoldierAction"/> via
    ///     <see cref="PersonnelEditRelay.Relay"/>, returns false (suppresses the native commit: no wallet
    ///     deduction, no storage mutation, no events on the frozen client). The payload is NEVER read from
    ///     the character model — the preview (<c>OnAugmentClicked</c>) contaminates <c>_armourItems</c>
    ///     with uncommitted picks, the bug the old bodypart-list payload shipped to the host.
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
        private static FieldInfo _selectedSlotField;     // UIModuleMutationSection._selectedMutationSlot
        private static PropertyInfo _slotMutationProp;   // UIModuleMutationsSlot.Mutation (ItemDef, public get)
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
                {
                    _parentModuleField = AccessTools.Field(sectionT, "_parentModule");
                    _selectedSlotField = AccessTools.Field(sectionT, "_selectedMutationSlot");
                }
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
                if (_parentModuleField == null || _selectedSlotField == null) { LogExit("section-unresolved"); return true; }

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

                // The INTENT: the augment the native commit would apply — _selectedMutationSlot.Mutation,
                // the same field ApplyMutation itself reads. Its stable def guid is the whole payload.
                object slot = _selectedSlotField.GetValue(section);
                if (slot == null) { LogExit("no-selected-mutation"); return true; }
                if (_slotMutationProp == null) _slotMutationProp = AccessTools.Property(slot.GetType(), "Mutation");
                string augmentGuid = DefReflection.GetGuid(_slotMutationProp?.GetValue(slot, null));
                if (string.IsNullOrEmpty(augmentGuid)) { LogExit("augment-guid-unresolved"); return true; }

                _lastExit = null;   // reached the relay — re-arm the latch
                PersonnelEditRelay.Relay(ActionCategory.Equip, unitId, true,
                    () => new AugmentSoldierAction(unitId, augmentGuid));
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

    // ─── TFTV client-side postfix guards ───────────────────────────────────────────────────────────────
    // TFTV postfixes UIModuleMutationSection.ApplyMutation (TFTV-src: TFTVStamina.cs:218 stamina SetToMin;
    // TFTVAugmentations.cs:623 NJ broke-promise diplomacy var). A Harmony POSTFIX still runs when our client
    // prefix suppresses the native body — so a relayed (never-applied-locally) augment would write stamina /
    // diplomacy vars into the client mirror model, and the __instance.MutationUsed it reads is stale
    // pre-commit state. Skip TFTV's postfix bodies exactly when the gesture is relayed
    // (PersonnelEditRelay.ShouldRelay: co-op client outside an engine apply); the host applies the real TFTV
    // effects once (native postfixes for host-local augments, TftvAugmentCompat for relayed ones). TFTV's
    // UIModuleBionics.OnAugmentApplied postfix needs no guard: the suppressed client never invokes
    // OnAugmentApplied at all. Without TFTV, Prepare() returns false and the guard never registers.

    [HarmonyPatch]
    public static class TftvApplyMutationStaminaPostfixGuard
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName(
                "TFTV.TFTVStamina+UIModuleMutationSection_ApplyMutation_SetStaminaTo0_patch");
            _target = t != null ? AccessTools.Method(t, "Postfix") : null;
            if (_target != null)
                Debug.Log("[Multiplayer] AugmentGesturePatches: TFTV ApplyMutation stamina postfix guard bound");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => !PersonnelEditRelay.ShouldRelay();
    }

    [HarmonyPatch]
    public static class TftvApplyMutationDiplomacyPostfixGuard
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName(
                "TFTV.TFTVAugmentations+UIModuleMutationSection_ApplyMutation_PissedEvents_patch");
            _target = t != null ? AccessTools.Method(t, "Postfix") : null;
            if (_target != null)
                Debug.Log("[Multiplayer] AugmentGesturePatches: TFTV ApplyMutation diplomacy postfix guard bound");
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        public static bool Prefix() => !PersonnelEditRelay.ShouldRelay();
    }

    // ─── preview-write scope (previews are 100% LOCAL — a preview click never dirties #9/#1) ──────────────
    // The 3D preview is a REAL GeoCharacter.SetItems write (OnAugmentClicked) — a transient UI transaction,
    // not an edit. Bracket ONLY OnAugmentClicked (both modules) with the AugmentPreviewScope latch;
    // GeoCharacterSetItemsStateDirtyPatch skips marking while it is active (preview regression RCA
    // 2026-07-09: a host preview click broadcast the uncommitted pick on #9/#1, stamping the client model
    // and repainting the client's open augment screen mid-preview). The REVERT writes (ClearAugment /
    // RevertUnconfirmedChanges) deliberately KEEP their marks: a revert re-stamps the authoritative
    // baseline, so its blob equals the last-emitted state and the per-soldier hash-skip culls it to zero
    // wire in the common case — and when an unrelated same-soldier mark (hourly bulk sweep) DID ship a
    // preview-contaminated blob mid-transaction, the revert mark ships the corrective baseline (self-heal).
    // The Finalizer runs on every exit path (including native throws), so the latch can never leak armed.

    [HarmonyPatch]
    public static class AugmentPreviewScopePatch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();

        public static bool Prepare()
        {
            _targets.Clear();
            foreach (var typeName in new[]
                     {
                         "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutate",
                         "PhoenixPoint.Geoscape.View.ViewModules.UIModuleBionics",
                     })
            {
                var t = AccessTools.TypeByName(typeName);
                var m = t != null ? AccessTools.Method(t, "OnAugmentClicked") : null;
                if (m != null) _targets.Add(m);
            }
            Debug.Log("[Multiplayer] AugmentGesturePatches: preview scope bound " + _targets.Count
                      + "/2 preview-click methods" + (_targets.Count == 2 ? "" : " — INCOMPLETE, preview writes may leak to #9"));
            return _targets.Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        public static void Prefix() => AugmentPreviewScope.Enter();
        public static void Finalizer() => AugmentPreviewScope.Exit();
    }

    /// <summary>The COMMIT dirty seam that replaces the preview click's accidental one: the native commit
    /// (<c>OnAugmentApplied</c>) does NOT re-run SetItems for the main path — the last preview write already
    /// stamped the final bodyparts — so with previews scope-suppressed the host must mark #9 (soldier blob)
    /// + #1 (displaced parts returned to storage) here. Host-gated inside <see cref="PersonnelStateDirty"/>;
    /// never fires on a co-op client (its ApplyMutation prefix suppresses the native commit entirely), and
    /// the relayed-intent host chain (<see cref="PersonnelEditReflection.Augment"/>) is headless — its own
    /// SetItems marks as before.</summary>
    [HarmonyPatch]
    public static class AugmentCommitDirtyPatch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();
        private static PropertyInfo _currentCharProp;   // UIModuleMutate/UIModuleBionics.CurrentCharacter

        public static bool Prepare()
        {
            _targets.Clear();
            foreach (var typeName in new[]
                     {
                         "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutate",
                         "PhoenixPoint.Geoscape.View.ViewModules.UIModuleBionics",
                     })
            {
                var t = AccessTools.TypeByName(typeName);
                var m = t != null ? AccessTools.Method(t, "OnAugmentApplied") : null;
                if (m != null) _targets.Add(m);
            }
            Debug.Log("[Multiplayer] AugmentGesturePatches: commit dirty seam bound " + _targets.Count + "/2 modules");
            return _targets.Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        public static void Postfix(object __instance)
        {
            try
            {
                if (_currentCharProp == null || !_currentCharProp.DeclaringType.IsInstanceOfType(__instance))
                    _currentCharProp = AccessTools.Property(__instance.GetType(), "CurrentCharacter");
                object character = _currentCharProp?.GetValue(__instance, null);
                if (character == null) return;
                PersonnelStateDirty.Mark(character);
                PersonnelStateDirty.MarkStorage();
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] AugmentCommitDirtyPatch failed: " + ex.Message); }
        }
    }

    // ─── same-group preview switching (co-op sessions only) ───────────────────────────────────────────
    // VANILLA blocks a direct A→B preview switch inside one body-part group: SelectMutation's slot loop
    // disables every sibling (slot2.SetEnable(false), UIModuleMutationSection.cs:219) while a slot is
    // selected, and PhoenixGeneralButton.OnPointerClick (:327) drops clicks on !IsEnabled — so the second
    // click never reaches SelectMutation. The only native unblocks are the toggle-off paths (re-click the
    // selected slot, Escape → HandleCancelInput, a click in another group → ClearMutationSelection) or a
    // full RefreshContainerSlots (commit/repair/character-change). RCA 2026-07-09 round-2: the "dead
    // second click" reported after c65cb4d IS this vanilla deselect-first design — the pre-v2 mod merely
    // masked it by repainting the whole module on every #9 apply (RequestViewRefresh → ResetContainer →
    // RefreshContainerSlots re-enabled all slots), which read as free switching. In a co-op session we
    // restore that free-switching feel PROPERLY: after every SelectMutation that leaves a slot selected,
    // re-enable the section's slots with the native enable rule (CanApplyAugumentation — the exact state
    // RefreshContainerSlots/toggle-off produce, :210/:384), so the next click on B runs SelectMutation(B)
    // natively (switch branch :203 → preview swap from the CharacterOriginalItems baseline). Preview
    // stays local-only (OnAugmentClicked is still latched by AugmentPreviewScope); commit/sync untouched.
    // Toggle-off calls end with _selectedMutationSlot == null → no-op (native re-enable already ran).
    // Gate OFF / single-player / no session → pure native vanilla, matching the rest of the file.

    [HarmonyPatch]
    public static class AugmentSlotSwitchUnlockPatch
    {
        private static MethodBase _target;
        private static FieldInfo _selectedSlotField;     // UIModuleMutationSection._selectedMutationSlot
        private static PropertyInfo _currentContainerProp; // UIModuleMutationSection.CurrentContainer
        private static MethodInfo _canApply;             // UIModuleMutationSection.CanApplyAugumentation(ItemDef)
        private static PropertyInfo _containerSlotsProp; // UIModuleMutationsContainer.Slots
        private static PropertyInfo _slotMutationProp;   // UIModuleMutationsSlot.Mutation
        private static MethodInfo _slotSetEnable;        // UIModuleMutationsSlot.SetEnable(bool)

        public static bool Prepare()
        {
            var sectionT = AccessTools.TypeByName(
                "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationSection");
            var containerT = AccessTools.TypeByName(
                "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationsContainer");
            var slotT = AccessTools.TypeByName(
                "PhoenixPoint.Geoscape.View.ViewModules.UIModuleMutationsSlot");
            if (sectionT != null && containerT != null && slotT != null)
            {
                _target = AccessTools.Method(sectionT, "SelectMutation");
                _selectedSlotField = AccessTools.Field(sectionT, "_selectedMutationSlot");
                _currentContainerProp = AccessTools.Property(sectionT, "CurrentContainer");
                _canApply = AccessTools.Method(sectionT, "CanApplyAugumentation");
                _containerSlotsProp = AccessTools.Property(containerT, "Slots");
                _slotMutationProp = AccessTools.Property(slotT, "Mutation");
                _slotSetEnable = AccessTools.Method(slotT, "SetEnable");
            }
            bool ok = _target != null && _selectedSlotField != null && _currentContainerProp != null
                      && _canApply != null && _containerSlotsProp != null && _slotMutationProp != null
                      && _slotSetEnable != null;
            Debug.Log("[Multiplayer] AugmentGesturePatches: same-group switch unlock "
                      + (ok ? "bound" : "NOT FOUND — vanilla deselect-first stays"));
            return ok;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix(object __instance)
        {
            try
            {
                if (!EquipSyncV2Gate.Enabled) return;
                var eng = NetworkEngine.Instance;
                if (eng == null || !eng.IsActiveSession) return;
                if (_selectedSlotField.GetValue(__instance) == null) return;   // toggle-off → native re-enabled all
                var container = _currentContainerProp.GetValue(__instance, null);
                if (container == null) return;
                if (!(_containerSlotsProp.GetValue(container, null) is IEnumerable slots)) return;
                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    object mutation = _slotMutationProp.GetValue(slot, null);
                    bool canApply = mutation != null
                                    && _canApply.Invoke(__instance, new[] { mutation }) is bool b && b;
                    _slotSetEnable.Invoke(slot, new object[] { canApply });
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] AugmentSlotSwitchUnlockPatch failed: " + ex.Message); }
        }
    }
}
