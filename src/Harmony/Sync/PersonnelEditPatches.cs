using System;
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
    /// PS4 client-edit INTENT RELAY interceptors (personnel-sync spec §5). A co-op CLIENT manages its OWN
    /// soldiers; the sim is frozen so a local roster/equipment/recruit edit can neither take effect nor reach
    /// the host — instead each geoscape edit chokepoint, on a client, SUPPRESSES the local mutation and relays
    /// a permission-gated <see cref="ISyncedAction"/> intent (nonce-deduped by the generic SendActionRequest
    /// path). The host <c>Validate</c>s (category permission via <see cref="PermissionGate"/> + per-soldier
    /// ownership) and runs the NATIVE mutation authoritatively inside <c>SyncApplyScope</c>; the existing
    /// PS1/PS2/PS3 dirty hooks fire → the result mirrors back on #6/#9/#10 to ALL clients (the initiator sees
    /// its own edit only once it round-trips). No host-side broadcast of these actions is needed (unlike
    /// <c>MoveVehiclePatch</c>): the state channels are the sole writer.
    ///
    /// Chokepoints (each single native method, decompile-verified 2026-07-06):
    ///   • <c>GeoCharacter.SetItems</c> → <see cref="EquipSoldierAction"/> (full final loadout — a null arg is
    ///     filled from the soldier's current list); augment has its OWN chokepoint (AugmentGesturePatches.cs
    ///     PREFIX on <c>UIModuleMutationSection.ApplyMutation</c>) → <see cref="AugmentSoldierAction"/>
    ///     carrying the chosen augment's def guid, host-applied via the full native-equivalent chain;
    ///   • <c>GeoPhoenixFaction.HireNakedRecruit</c> → <see cref="HireRecruitAction"/>;
    ///   • <c>GeoCharacter.Rename</c> → <see cref="RenameSoldierAction"/>;
    ///   • <c>GeoFaction.KillCharacter(_, Dismissed)</c> → <see cref="DismissSoldierAction"/>;
    ///   • <c>GeoVehicle/GeoSite.AddCharacter</c> → <see cref="TransferSoldierAction"/> (the paired
    ///     <c>RemoveCharacter</c> is suppressed without a second intent — the Add carries the transfer);
    ///   • <c>GeoPhoenixFaction.KillCapturedUnit</c> → <see cref="KillCapturedUnitAction"/> (containment
    ///     kill button; research live-alien costs never reach it on a client — research start is suppressed
    ///     at AddResearchToQueue);
    ///   • <c>GeoPhoenixFaction.HarvestCapturedUnit</c> → <see cref="HarvestCapturedUnitAction"/> (dismantle
    ///     for food/mutagens; suppressing it here also keeps its inner KillCapturedUnit from double-relaying);
    ///   • <c>UIModuleCharacterProgression.BuyAbility</c> → <see cref="LevelUpAbilityAction"/> (SP ability
    ///     buy — the UI method IS the commit chokepoint: the SP deduction is a raw field write with no
    ///     patchable native beneath it, so the relay hooks the one method that couples cost + LearnAbility);
    ///   • <c>UIModuleCharacterProgression.CommitStatChanges</c> → <see cref="SpendStatPointsAction"/> per
    ///     changed stat (positive deltas only; the host re-derives every point's cost natively). The prefix
    ///     also rolls the module's local current-values back to starting so a repeated commit call in the
    ///     same screen session can never double-relay. Mutoid (mutagen-cost) progression is suppressed
    ///     without relay on a client (wallet-funded path, out of the SP intent family);
    ///   • <c>UIModuleCharacterProgression.ChoseSecondSpecialization</c> → suppressed WITHOUT relay on a
    ///     client (denial notify) so AddSecondaryClass never half-applies locally; the relay intent
    ///     (SecondSpecialization = 70) is a tracked follow-up (COOP-SYNC-ROADMAP.md).
    ///
    /// Composes with the PS1/PS2 dirty Postfixes on the same methods: on a client our Prefix returns false
    /// (suppress) and those Postfixes are IsHost-gated no-ops; on the host our Prefix passes through (IsHost /
    /// IsApplying) so the native runs and marks dirty. Game types are NEVER hard-referenced — targets resolve
    /// via AccessTools; Prepare() false → PatchAll skips silently.
    /// </summary>
    internal static class PersonnelEditRelay
    {
        /// <summary>True only when the LOCAL peer must relay this edit as an intent (co-op client, active
        /// session, not an engine-driven apply/replay). False → the caller returns true (run the native): the
        /// host is authoritative and single-player is untouched.</summary>
        internal static bool ShouldRelay()
        {
            if (SyncApplyScope.IsApplying) return false;          // host apply / client mirror → run native
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return false;  // single-player
            if (engine.IsHost) return false;                       // host authoritative → #6/#9/#10 mirror the result
            return true;                                            // client → suppress + relay
        }

        /// <summary>Permission + ownership gate, then send the built intent. Always returns false (the local
        /// frozen edit is suppressed either way — a denial surfaces via <see cref="PermissionGate.Notify"/>).</summary>
        internal static bool Relay(ActionCategory cat, long unitId, bool checkOwnership, Func<ISyncedAction> build)
        {
            try
            {
                if (!PermissionGate.Check(cat)
                    || (checkOwnership && !PersonnelEditReflection.OwnsSoldier(ClientIdentity.PlayerGuid, unitId)))
                {
                    // Deny visibility (field RCA round 3): Notify is a bare event invoke — without this line a
                    // denied intent left ZERO log signature (indistinguishable from a dead relay path).
                    Debug.Log("[Multiplayer] PersonnelEditRelay: intent DENIED cat=" + cat + " unit=" + unitId
                              + " (permission/ownership) — not sent");
                    PermissionGate.Notify(cat);
                    return false;
                }
                var action = build();
                if (action != null) NetworkEngine.Instance?.Sync?.SendActionRequest(action);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] PersonnelEditRelay.Relay failed: " + ex.Message); }
            return false;
        }

        /// <summary>Destination container key for a transfer: (kind 1, VehicleID) for a GeoVehicle, else
        /// (kind 0, SiteId) for a GeoSite/base.</summary>
        internal static void ContainerKey(object container, out int kind, out int id)
        {
            kind = 0; id = -1;
            try
            {
                var vf = AccessTools.Field(container.GetType(), "VehicleID");
                if (vf != null) { kind = 1; id = Convert.ToInt32(vf.GetValue(container)); return; }
                id = GeoSiteReflection.GetSiteId(container);
            }
            catch { }
        }
    }

    // NOTE (v2 rebuild 2026-07-08): the equip intent no longer rides a GeoCharacter.SetItems flush-diff prefix.
    // The old SetItemsEditRelayPatch + LoadoutRelayDedup are DELETED — they inferred edits by diffing the
    // per-frame SetItems flush and stormed ~60 intents/s (the FPS-collapse layer). Equip now captures at the
    // SOURCE gesture seams in EquipGesturePatches.cs (one intent per user action) with the client flush
    // suppressed. Augment (AugmentSoldierAction) now has its own gesture chokepoint on the augmentation screen
    // in AugmentGesturePatches.cs (PREFIX on UIModuleMutationSection.ApplyMutation).

    [HarmonyPatch]
    public static class HireRecruitEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "HireNakedRecruit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.HireNakedRecruit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor recruit; __1 = IGeoCharacterContainer destination (a base Site).
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                var rt = GeoRuntime.Instance;
                int destSiteId = GeoSiteReflection.GetSiteId(__1);
                if (!PersonnelEditReflection.ResolveRecruitSource(rt, __0, out int kind, out int id))
                {
                    Debug.Log("[Multiplayer] HireRecruitEditRelayPatch: recruit source unresolved — hire suppressed (no relay)");
                    return false;   // frozen client can't hire locally; nothing to relay
                }
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new HireRecruitAction(kind, id, destSiteId));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HireRecruitEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class RenameEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            _target = t != null ? AccessTools.Method(t, "Rename", new[] { typeof(string) }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoCharacter.Rename relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance, string __0)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                long unitId = PersonnelReflection.ReadUnitId(__instance);
                string newName = __0;
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new RenameSoldierAction(unitId, newName));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] RenameEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class DismissEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            // GeoPhoenixFaction OVERRIDES KillCharacter (GeoPhoenixFaction.cs:1377 → base.KillCharacter); the
            // co-op dismiss dispatches to the override, so patch it (not the base GeoFaction MethodInfo).
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "KillCharacter") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.KillCharacter dismiss relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoCharacter unit; __1 = CharacterDeathReason. Only a DISMISS is a client edit; every other
        // death is host-driven (tactical / geoscape sim) and never originates on the frozen client.
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (__1 == null || __1.ToString() != "Dismissed") return true;   // not a dismiss → run native
                long unitId = PersonnelReflection.ReadUnitId(__0);
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new DismissSoldierAction(unitId));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DismissEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class KillCapturedUnitRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "KillCapturedUnit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.KillCapturedUnit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor captive (containment kill button, UIStateRosterAliens.cs:256). On the host
        // (incl. a relayed apply inside SyncApplyScope) this passes through and the existing
        // PhoenixKillCapturedUnitPoolDirtyPatch Postfix mirrors the removal on #10.
        public static bool Prefix(object __0)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (!PersonnelEditReflection.ResolveCapturedSource(GeoRuntime.Instance, __0, out int ordinal, out string guid))
                {
                    Debug.Log("[Multiplayer] KillCapturedUnitRelayPatch: captive unresolved — kill suppressed (no relay)");
                    return false;   // frozen client can't mutate containment locally; nothing to relay
                }
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new KillCapturedUnitAction(ordinal, guid));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] KillCapturedUnitRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class HarvestCapturedUnitRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction");
            _target = t != null ? AccessTools.Method(t, "HarvestCapturedUnit") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoPhoenixFaction.HarvestCapturedUnit relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0 = GeoUnitDescriptor captive; __1 = ResourceType (Supplies = food / Mutagen — dismantle buttons,
        // UIStateRosterAliens.cs:275/296). Suppressing here on a client also keeps the inner
        // KillCapturedUnit (GeoPhoenixFaction.cs:893) from firing a second relay.
        public static bool Prefix(object __0, object __1)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                if (!PersonnelEditReflection.ResolveCapturedSource(GeoRuntime.Instance, __0, out int ordinal, out string guid))
                {
                    Debug.Log("[Multiplayer] HarvestCapturedUnitRelayPatch: captive unresolved — harvest suppressed (no relay)");
                    return false;
                }
                int resourceType = Convert.ToInt32(__1);
                return PersonnelEditRelay.Relay(ActionCategory.Recruitment, 0, false,
                    () => new HarvestCapturedUnitAction(ordinal, guid, resourceType));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] HarvestCapturedUnitRelayPatch failed: " + ex.Message); return true; }
        }
    }

    [HarmonyPatch]
    public static class BuyAbilityProgressionRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "BuyAbility") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.BuyAbility relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // BuyAbility is the ONE native commit chokepoint coupling the SP cost (ConsumeAbilityCost +
        // CommitStatChanges — raw SkillPoints field writes, unpatchable below UI level) with
        // CharacterProgression.LearnAbility (UIModuleCharacterProgression.cs:389-426). The relay keys the
        // slot as (trackSource, index in AbilitiesByLevel) + ability-def guid fingerprint; the host
        // re-validates and re-prices natively. A second confirm click before the #9 round-trip is safe:
        // the host's CanLearnAbility rejects the duplicate (already learned).
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                var t = __instance.GetType();
                object slot = AccessTools.Field(t, "_boughtAbilitySlot")?.GetValue(__instance);
                if (slot == null) return true;   // native no-ops on a null slot too
                if (AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(__instance) is bool pandoran && pandoran)
                {
                    Debug.Log("[Multiplayer] BuyAbilityProgressionRelayPatch: mutoid (mutagen-cost) progression not relayed — buy suppressed");
                    ClearBoughtSlot(t, __instance);
                    return false;
                }
                object character = AccessTools.Field(t, "_character")?.GetValue(__instance);
                long unitId = PersonnelReflection.ReadUnitId(character);
                object track = AccessTools.Property(slot.GetType(), "AbilityTrack")?.GetValue(slot, null);
                var slots = track != null ? AccessTools.Field(track.GetType(), "AbilitiesByLevel")?.GetValue(track) as Array : null;
                int slotIndex = -1;
                if (slots != null)
                    for (int i = 0; i < slots.Length; i++)
                        if (ReferenceEquals(slots.GetValue(i), slot)) { slotIndex = i; break; }
                int trackSource = track != null ? Convert.ToInt32(AccessTools.Field(track.GetType(), "Source")?.GetValue(track) ?? -1) : -1;
                // An empty (personal-pick) slot carries the chosen def in _boughtAbility (BuyAbility :393-396).
                object ability = AccessTools.Field(slot.GetType(), "Ability")?.GetValue(slot)
                                 ?? AccessTools.Field(t, "_boughtAbility")?.GetValue(__instance);
                string guid = DefReflection.GetGuid(ability);
                if (slotIndex < 0 || trackSource < 0 || string.IsNullOrEmpty(guid))
                {
                    Debug.Log("[Multiplayer] BuyAbilityProgressionRelayPatch: slot unresolved — buy suppressed (no relay)");
                    ClearBoughtSlot(t, __instance);
                    return false;
                }
                bool relayed = PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new LevelUpAbilityAction(unitId, (byte)trackSource, slotIndex, guid));
                // Native BuyAbility clears _boughtAbilitySlot after LearnAbility (UIModuleCharacterProgression.cs:418);
                // we suppressed the native call, so mirror that clear here — a still-set slot keeps GeoUiRefresh's
                // progression re-drive permanently skipped ("pending local allocation"), so the host-echoed learn
                // and its SP deduction never repaint on the client (stale SP → the user then over-spends stats the
                // host rejects with sp=0). Cleared on BOTH allow and deny so the pending pick never sticks.
                ClearBoughtSlot(t, __instance);
                return relayed;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BuyAbilityProgressionRelayPatch failed: " + ex.Message); return true; }
        }

        /// <summary>Reset the module's pending-ability pick after a client-suppressed buy — the native cancel idiom
        /// (UIModuleCharacterProgression.ClearBoughtAbility :452 nulls _boughtAbilitySlot + refreshes the tracks).
        /// Best-effort: a miss must never break the suppress path.</summary>
        private static void ClearBoughtSlot(Type t, object module)
        {
            try { AccessTools.Method(t, "ClearBoughtAbility")?.Invoke(module, null); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] BuyAbilityProgressionRelayPatch.ClearBoughtSlot failed: " + ex.Message); }
        }
    }

    [HarmonyPatch]
    public static class CommitStatChangesProgressionRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "CommitStatChanges") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.CommitStatChanges relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // CommitStatChanges is the native stat-spend commit (screen exit / soldier switch / confirm —
        // UIStateEditSoldier.cs:232/363/715, UIModuleCharacterProgression.cs:367-387). Relays ONE
        // SpendStatPointsAction per raised stat carrying only the positive delta; the local current
        // values are rolled back to starting so a repeat commit in the same session never double-relays
        // (native decrease below starting is impossible, :907 — committed spends are non-refundable).
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                var t = __instance.GetType();
                int dStr = Delta(t, __instance, "_currentStrengthStat", "_startingStrengthStat");
                int dWill = Delta(t, __instance, "_currentWillStat", "_startingWillStat");
                int dSpeed = Delta(t, __instance, "_currentSpeedStat", "_startingSpeedStat");
                bool pandoran = AccessTools.Field(t, "_hasPandoranProgression")?.GetValue(__instance) is bool p && p;
                if (!pandoran && (dStr > 0 || dWill > 0 || dSpeed > 0))
                {
                    object character = AccessTools.Field(t, "_character")?.GetValue(__instance);
                    long unitId = PersonnelReflection.ReadUnitId(character);
                    // CharacterBaseAttribute: Strength=0, Will=1, Speed=2 (decompile enum order).
                    if (dStr > 0) PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                        () => new SpendStatPointsAction(unitId, 0, dStr));
                    if (dWill > 0) PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                        () => new SpendStatPointsAction(unitId, 1, dWill));
                    if (dSpeed > 0) PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                        () => new SpendStatPointsAction(unitId, 2, dSpeed));
                }
                else if (pandoran && (dStr > 0 || dWill > 0 || dSpeed > 0))
                    Debug.Log("[Multiplayer] CommitStatChangesProgressionRelayPatch: mutoid (mutagen-cost) stat spend not relayed — commit suppressed");
                // Roll local session state back to starting (idempotent repeat-commit guard).
                Reset(t, __instance, "_currentStrengthStat", "_startingStrengthStat");
                Reset(t, __instance, "_currentWillStat", "_startingWillStat");
                Reset(t, __instance, "_currentSpeedStat", "_startingSpeedStat");
                Reset(t, __instance, "_currentSkillPoints", "_startingSkillPoints");
                Reset(t, __instance, "_currentFactionPoints", "_startingFactionPoints");
                Reset(t, __instance, "_currentMutagens", "_startingMutagens");
                return false;   // suppress the frozen local commit (stat + SP + faction-pool + mutagen writes)
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] CommitStatChangesProgressionRelayPatch failed: " + ex.Message); return true; }
        }

        private static int Delta(Type t, object inst, string currentField, string startingField)
        {
            object cur = AccessTools.Field(t, currentField)?.GetValue(inst);
            object start = AccessTools.Field(t, startingField)?.GetValue(inst);
            return cur is int c && start is int s ? c - s : 0;
        }

        private static void Reset(Type t, object inst, string currentField, string startingField)
        {
            var cf = AccessTools.Field(t, currentField);
            var sf = AccessTools.Field(t, startingField);
            if (cf != null && sf != null) cf.SetValue(inst, sf.GetValue(inst));
        }
    }

    [HarmonyPatch]
    public static class ChoseSecondSpecializationSuppressPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.View.ViewModules.UIModuleCharacterProgression");
            _target = t != null ? AccessTools.Method(t, "ChoseSecondSpecialization") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: UIModuleCharacterProgression.ChoseSecondSpecialization suppress " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // Second-specialization buy (dual-class popup, UIModuleCharacterProgression.cs:813-824) runs
        // AddSecondaryClass + ConsumeAbilityCost + CommitStatChanges in one native call. On a client the
        // inner CommitStatChanges is already suppressed (prefix above), which would leave a PHANTOM local
        // second class with its SP cost silently discarded — so suppress the WHOLE buy without relay
        // (denial notify gives feedback; the popup is closed the way the native entry does, :815). The
        // proper intent (SecondSpecialization = 70) is a tracked follow-up in COOP-SYNC-ROADMAP.md.
        public static bool Prefix(object __instance)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                Debug.Log("[Multiplayer] ChoseSecondSpecializationSuppressPatch: second-specialization buy not relayed yet — suppressed on client");
                PermissionGate.Notify(ActionCategory.ControlSoldiers);
                var popup = AccessTools.Field(__instance.GetType(), "DualClassPopupWindow")?.GetValue(__instance) as GameObject;
                popup?.SetActive(false);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] ChoseSecondSpecializationSuppressPatch failed: " + ex.Message); }
            return false;   // never run the frozen local AddSecondaryClass
        }
    }

    /// <summary>Transfer ADD side: a client soldier added to a Phoenix container = an assign/transfer intent
    /// (hire's internal AddCharacter never reaches here — HireNakedRecruit is suppressed one level up). Relays
    /// the destination; the host re-derives the source (current container) and runs Remove+Add.</summary>
    internal static class TransferAddRelay
    {
        internal static MethodBase Resolve(string typeName)
        {
            var containerT = AccessTools.TypeByName(typeName);
            var charT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            var m = containerT != null && charT != null ? AccessTools.Method(containerT, "AddCharacter", new[] { charT }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: " + typeName + ".AddCharacter transfer relay " + (m != null ? "bound" : "NOT FOUND"));
            return m;
        }
        internal static bool Prefix(object container, object character)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                long unitId = PersonnelReflection.ReadUnitId(character);
                PersonnelEditRelay.ContainerKey(container, out int kind, out int id);
                return PersonnelEditRelay.Relay(ActionCategory.ControlSoldiers, unitId, true,
                    () => new TransferSoldierAction(unitId, kind, id));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] TransferAddRelay failed: " + ex.Message); return true; }
        }
    }

    /// <summary>Transfer REMOVE side: on a client, suppress the local frozen remove (the paired Add relays the
    /// transfer; a standalone remove self-heals via the #9/#6 mirror). Host / apply → pass through.</summary>
    internal static class TransferRemoveRelay
    {
        internal static MethodBase Resolve(string typeName)
        {
            var containerT = AccessTools.TypeByName(typeName);
            var charT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            var m = containerT != null && charT != null ? AccessTools.Method(containerT, "RemoveCharacter", new[] { charT }) : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: " + typeName + ".RemoveCharacter transfer suppress " + (m != null ? "bound" : "NOT FOUND"));
            return m;
        }
        internal static bool Prefix()
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            return false;   // suppress the local frozen remove
        }
    }

    [HarmonyPatch]
    public static class GeoVehicleAddCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferAddRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix(object __instance, object __0) => TransferAddRelay.Prefix(__instance, __0);
    }

    [HarmonyPatch]
    public static class GeoSiteAddCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferAddRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix(object __instance, object __0) => TransferAddRelay.Prefix(__instance, __0);
    }

    [HarmonyPatch]
    public static class GeoVehicleRemoveCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferRemoveRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoVehicle"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix() => TransferRemoveRelay.Prefix();
    }

    [HarmonyPatch]
    public static class GeoSiteRemoveCharacterTransferRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare() { _target = TransferRemoveRelay.Resolve("PhoenixPoint.Geoscape.Entities.GeoSite"); return _target != null; }
        public static MethodBase TargetMethod() => _target;
        public static bool Prefix() => TransferRemoveRelay.Prefix();
    }
}
