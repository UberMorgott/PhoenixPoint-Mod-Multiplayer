using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.CommandSync;
using UnityEngine;

namespace Multipleer.Harmony
{
    // SD-AIDR INC-2 (B): on the HOST, broadcast an entity create/destroy op (0x36) whenever the native
    // birth/death seams fire, so clients replicate the entity. Mirrors StartTravelInterceptPatch.Postfix:
    // host-only gate; bail during a relayed/replayed apply so we never echo a replicated op.
    //   * GeoFaction.CreateVehicle(GeoSite, ComponentSetDef)           -> VehicleCreated (site anchor)
    //   * GeoFaction.CreateVehicleAtPosition(Vector3, ComponentSetDef) -> VehicleCreated (position)
    //   * GeoFaction.UnregisterVehicle(GeoVehicle)                     -> VehicleRemoved (single removal choke)
    //   * GeoSite.DestroySite()                                        -> SiteRemoved
    // SiteCreated is NOT emitted in INC-2 (a new site needs full InstanceData -> INC-3 0x35).
    //
    // STRUCTURE: the four seams are split across TWO patch classes, NOT one shared Postfix. The two CREATE
    // methods carry a `ComponentSetDef vehicleDef` 2nd arg (index __1) that must cross the wire as the
    // broadcast def guid; UnregisterVehicle (1 arg) and DestroySite (0 args) have NO such arg. A single
    // Postfix that injects __1 would FAIL to bind on the remove seams (index out of range) -> PatchAll
    // throw -> mod won't load. Each patch class therefore lists in its own TargetMethods() only the targets
    // whose signature its injected params are valid for, so Harmony binds every injected arg on every target.
    public static class HostEntityOpBroadcast
    {
        // Resolved once in Prepare() and reused by both patch classes' TargetMethods().
        internal static MethodBase CreateVehicle;
        internal static MethodBase CreateVehicleAtPos;
        internal static MethodBase UnregisterVehicle;
        internal static MethodBase DestroySite;

        internal static bool Resolve()
        {
            if (CreateVehicle != null || CreateVehicleAtPos != null
                || UnregisterVehicle != null || DestroySite != null)
                return true; // already resolved by the sibling patch's Prepare()

            var faction = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            var geoSiteType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoSite");
            var csdType = AccessTools.TypeByName("Base.Core.ComponentSetDef");
            var vehType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            if (faction == null || geoSiteType == null || csdType == null || vehType == null) return false;

            CreateVehicle = AccessTools.Method(faction, "CreateVehicle", new[] { geoSiteType, csdType });
            CreateVehicleAtPos = AccessTools.Method(faction, "CreateVehicleAtPosition",
                new[] { typeof(Vector3), csdType });
            UnregisterVehicle = AccessTools.Method(faction, "UnregisterVehicle", new[] { vehType });
            DestroySite = AccessTools.Method(geoSiteType, "DestroySite", Type.EmptyTypes);
            return true;
        }

        // Shared host-only / not-replaying gate. True == this seam fired on the authoritative host and is
        // NOT inside a relayed/replayed apply, so it is safe to broadcast.
        internal static bool ShouldBroadcast()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return false; // host emits authoritatively
            if (CommandRelay.IsApplying) return false;            // a relayed command apply already fans out
            if (EntityReplicationScope.IsApplying) return false;  // a client replay (never on host, defensive)
            return true;
        }

        // GeoVehicle.CurrentSite?.SiteId, or -1 if travelling / no site (use position instead).
        internal static int ResolveCurrentSiteId(object vehicle)
        {
            var site = AccessTools.Property(vehicle.GetType(), "CurrentSite")?.GetValue(vehicle);
            if (site == null) return -1;
            var s = GeoBridge.SiteId(site);
            return int.TryParse(s, out var id) ? id : -1;
        }

        // ActorComponent.WorldPosition (base) — Vector3.
        internal static Vector3 WorldPositionOf(object vehicle)
        {
            var v = AccessTools.Property(vehicle.GetType(), "WorldPosition")?.GetValue(vehicle);
            return v is Vector3 p ? p : Vector3.zero;
        }
    }

    // CREATE seams only: GeoFaction.CreateVehicle / CreateVehicleAtPosition. Both have a `ComponentSetDef
    // vehicleDef` 2nd parameter (index __1). We broadcast THAT def's guid (a ComponentSetDef guid) — NOT
    // vehicle.VehicleDef.Guid, which is a GeoVehicleDef (a SIBLING of ComponentSetDef under BaseDef). The
    // client replay calls CreateVehicle(GeoSite, ComponentSetDef) via reflection, so the resolved def MUST
    // be a ComponentSetDef or the Invoke throws ArgumentException and the aircraft never appears.
    [HarmonyPatch]
    public static class HostVehicleCreateBroadcastPatch
    {
        public static bool Prepare() => HostEntityOpBroadcast.Resolve()
            && (HostEntityOpBroadcast.CreateVehicle != null || HostEntityOpBroadcast.CreateVehicleAtPos != null);

        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (HostEntityOpBroadcast.CreateVehicle != null) yield return HostEntityOpBroadcast.CreateVehicle;
            if (HostEntityOpBroadcast.CreateVehicleAtPos != null) yield return HostEntityOpBroadcast.CreateVehicleAtPos;
        }

        // __instance = GeoFaction; __result = the new GeoVehicle (object, so the mod never references the
        // type); __1 = the ComponentSetDef vehicleDef arg (injected as object — both targets share it at
        // index 1). __originalMethod distinguishes the site-anchor vs position overload.
        public static void Postfix(object __instance, object __result, object __1, MethodBase __originalMethod)
        {
            // [DIAG] TEMPORARY boundary log (logging only, no flow change). Entry + gate snapshot, null-guarded.
            try
            {
                var engDiag = NetworkEngine.Instance;
                var createdIdDiag = (__result != null) ? GeoBridge.VehicleId(__result) : "null";
                Debug.Log($"[Multipleer] DIAG CreatePostfix fired: method={__originalMethod?.Name} " +
                    $"IsHost={(engDiag != null ? engDiag.IsHost.ToString() : "noEngine")} " +
                    $"IsActive={(engDiag != null ? engDiag.IsActive.ToString() : "noEngine")} " +
                    $"ShouldBroadcast={HostEntityOpBroadcast.ShouldBroadcast()} " +
                    $"CommandRelay.IsApplying={CommandRelay.IsApplying} " +
                    $"EntityReplicationScope.IsApplying={EntityReplicationScope.IsApplying} " +
                    $"VehicleID={createdIdDiag}");
            }
            catch (Exception diagEx) { Debug.LogWarning($"[Multipleer] DIAG CreatePostfix log failed: {diagEx.Message}"); }

            if (!HostEntityOpBroadcast.ShouldBroadcast())
            {
                Debug.Log("[Multipleer] DIAG CreatePostfix SKIP (ShouldBroadcast false)"); // [DIAG] TEMPORARY
                return;
            }
            try
            {
                var vehicle = __result; // the new GeoVehicle
                if (vehicle == null) return;
                if (!int.TryParse(GeoBridge.VehicleId(vehicle), out var createdId)) return;
                var op = new GeoEntityOp
                {
                    OpType = GeoEntityOpType.VehicleCreated,
                    DefGuid = GeoBridge.DefGuid(__1), // ComponentSetDef guid (the create method's own def arg)
                    OwnerFactionGuid = GeoBridge.FactionGuid(__instance),
                    SiteId = HostEntityOpBroadcast.ResolveCurrentSiteId(vehicle),
                    EntityId = createdId
                };
                // Position fallback when the vehicle is not anchored on a site (CreateVehicleAtPosition).
                if (op.SiteId < 0)
                {
                    var pos = HostEntityOpBroadcast.WorldPositionOf(vehicle);
                    op.PosX = pos.x; op.PosY = pos.y; op.PosZ = pos.z;
                }
                // [DIAG] TEMPORARY: right before the actual broadcast send.
                Debug.Log($"[Multipleer] DIAG CreatePostfix -> BroadcastGeoEntityOp VehicleCreated id={op.EntityId} defGuid={op.DefGuid} siteId={op.SiteId}");
                NetworkEngine.Instance.BroadcastGeoEntityOp(op);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] HostVehicleCreateBroadcastPatch ({__originalMethod?.Name}) failed: {ex}");
            }
        }
    }

    // REMOVE seams only: GeoFaction.UnregisterVehicle(GeoVehicle) and GeoSite.DestroySite(). Neither carries
    // a ComponentSetDef arg, so this Postfix injects NO def param (only __instance / __0 / __originalMethod),
    // keeping it bindable on both differing signatures.
    [HarmonyPatch]
    public static class HostEntityRemoveBroadcastPatch
    {
        public static bool Prepare() => HostEntityOpBroadcast.Resolve()
            && (HostEntityOpBroadcast.UnregisterVehicle != null || HostEntityOpBroadcast.DestroySite != null);

        public static IEnumerable<MethodBase> TargetMethods()
        {
            if (HostEntityOpBroadcast.UnregisterVehicle != null) yield return HostEntityOpBroadcast.UnregisterVehicle;
            if (HostEntityOpBroadcast.DestroySite != null) yield return HostEntityOpBroadcast.DestroySite;
        }

        // __instance = GeoFaction (UnregisterVehicle) or GeoSite (DestroySite); __args = the original args
        // (always bindable at any arity — VERIFIED: a positional __0 injection THROWS "No parameter found at
        // index 0" on the 0-param DestroySite at PatchAll time, so __args is the ONLY load-safe way to share
        // one postfix across the 1-param UnregisterVehicle and the 0-param DestroySite). The removed
        // GeoVehicle is __args[0] on UnregisterVehicle; DestroySite reads only __instance.
        public static void Postfix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!HostEntityOpBroadcast.ShouldBroadcast()) return;
            var name = __originalMethod.Name;
            try
            {
                if (name == "UnregisterVehicle")
                {
                    var vehicle = (__args != null && __args.Length > 0) ? __args[0] : null; // removed GeoVehicle
                    if (vehicle == null) return;
                    if (!int.TryParse(GeoBridge.VehicleId(vehicle), out var removedId)) return;
                    NetworkEngine.Instance.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.VehicleRemoved,
                        SiteId = -1,
                        EntityId = removedId
                    });
                }
                else if (name == "DestroySite")
                {
                    var site = __instance; // the GeoSite being destroyed
                    if (!int.TryParse(GeoBridge.SiteId(site), out var removedSiteId)) return;
                    NetworkEngine.Instance.BroadcastGeoEntityOp(new GeoEntityOp
                    {
                        OpType = GeoEntityOpType.SiteRemoved,
                        SiteId = removedSiteId,
                        EntityId = -1
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] HostEntityRemoveBroadcastPatch ({name}) failed: {ex}");
            }
        }
    }
}
