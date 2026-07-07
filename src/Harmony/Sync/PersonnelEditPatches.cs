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
    ///     filled from the soldier's current list; this SUBSUMES augment, whose new body-parts ride the armour
    ///     list, so <see cref="AugmentSoldierAction"/> is the registered dedicated-augment variant for a future
    ///     distinct augment chokepoint);
    ///   • <c>GeoPhoenixFaction.HireNakedRecruit</c> → <see cref="HireRecruitAction"/>;
    ///   • <c>GeoCharacter.Rename</c> → <see cref="RenameSoldierAction"/>;
    ///   • <c>GeoFaction.KillCharacter(_, Dismissed)</c> → <see cref="DismissSoldierAction"/>;
    ///   • <c>GeoVehicle/GeoSite.AddCharacter</c> → <see cref="TransferSoldierAction"/> (the paired
    ///     <c>RemoveCharacter</c> is suppressed without a second intent — the Add carries the transfer);
    ///   • <c>GeoPhoenixFaction.KillCapturedUnit</c> → <see cref="KillCapturedUnitAction"/> (containment
    ///     kill button; research live-alien costs never reach it on a client — research start is suppressed
    ///     at AddResearchToQueue);
    ///   • <c>GeoPhoenixFaction.HarvestCapturedUnit</c> → <see cref="HarvestCapturedUnitAction"/> (dismantle
    ///     for food/mutagens; suppressing it here also keeps its inner KillCapturedUnit from double-relaying).
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

    [HarmonyPatch]
    public static class SetItemsEditRelayPatch
    {
        private static MethodBase _target;
        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            _target = t != null ? AccessTools.Method(t, "SetItems") : null;
            Debug.Log("[Multiplayer] PersonnelEditPatches: GeoCharacter.SetItems relay " + (_target != null ? "bound" : "NOT FOUND"));
            return _target != null;
        }
        public static MethodBase TargetMethod() => _target;

        // __0/__1/__2 = armour/equipment/inventory IEnumerable<GeoItem> (null = leave that slot unchanged).
        public static bool Prefix(object __instance, object __0, object __1, object __2)
        {
            if (!PersonnelEditRelay.ShouldRelay()) return true;
            try
            {
                long unitId = PersonnelReflection.ReadUnitId(__instance);
                var armour = __0 != null ? PersonnelEditReflection.ReadItemGuids(__0).ToArray()
                                         : PersonnelEditReflection.ReadCurrentItemGuids(__instance, "_armourItems");
                var equip = __1 != null ? PersonnelEditReflection.ReadItemGuids(__1).ToArray()
                                        : PersonnelEditReflection.ReadCurrentItemGuids(__instance, "_equipmentItems");
                var inv = __2 != null ? PersonnelEditReflection.ReadItemGuids(__2).ToArray()
                                      : PersonnelEditReflection.ReadCurrentItemGuids(__instance, "_inventoryItems");
                return PersonnelEditRelay.Relay(ActionCategory.Equip, unitId, true,
                    () => new EquipSoldierAction(unitId, armour, equip, inv));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SetItemsEditRelayPatch failed: " + ex.Message); return true; }
        }
    }

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
