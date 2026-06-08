using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Validation;
using UnityEngine;

namespace Multipleer.Harmony
{
    internal static class CampaignPermissionHelper
    {
        public static bool Check(CampaignPermission required)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (!engine.IsHost) return false;
            return true;
        }
    }

    [HarmonyPatch]
    public static class ResearchPermissionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Research");
            _targetMethod = AccessTools.Method(_targetType, "SetQueued",
                new[] { typeof(ResearchDef), typeof(bool) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix(ResearchDef def, bool manualAdd)
        {
            if (!manualAdd) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive) return true;
            if (!engine.IsHost) return false;
            return true;
        }
    }

    [HarmonyPatch]
    public static class ManufacturingPermissionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.ItemManufacturing");
            _targetMethod = AccessTools.Method(_targetType, "EnqueueItem");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            if (!NetworkEngine.Instance?.IsActive ?? true) return true;
            if (!NetworkEngine.Instance.IsHost) return false;
            return CampaignPermissionHelper.Check(CampaignPermission.ManageManufacturing);
        }
    }

    [HarmonyPatch]
    public static class BaseConstructionPermissionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoPhoenixBase");
            _targetMethod = AccessTools.Method(_targetType, "ConstructFacility");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            if (!NetworkEngine.Instance?.IsActive ?? true) return true;
            if (!NetworkEngine.Instance.IsHost) return false;
            return CampaignPermissionHelper.Check(CampaignPermission.ManageBases);
        }
    }

    [HarmonyPatch]
    public static class VehicleEquipmentPermissionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoVehicle");
            _targetMethod = AccessTools.Method(_targetType, "AddEquipment",
                new[] { typeof(GeoVehicleEquipmentDef) });
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            if (!NetworkEngine.Instance?.IsActive ?? true) return true;
            if (!NetworkEngine.Instance.IsHost) return false;
            return CampaignPermissionHelper.Check(CampaignPermission.ManageEquipment);
        }
    }

    [HarmonyPatch]
    public static class SoldierEquipmentPermissionPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
            _targetMethod = AccessTools.Method(_targetType, "SetItems");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        public static bool Prefix()
        {
            if (!NetworkEngine.Instance?.IsActive ?? true) return true;
            if (!NetworkEngine.Instance.IsHost) return false;
            return CampaignPermissionHelper.Check(CampaignPermission.ManageEquipment);
        }
    }
}

// Runtime stubs — resolved by Harmony at runtime from game assembly
public class ResearchDef { }
public class ItemManufacturing { }
public class GeoPhoenixBase { }
public class GeoVehicleEquipmentDef { }
public class GeoVehicle { }
public class GeoCharacter { }
public class TacticalFaction { }
public class TacticalAbility { public virtual object TacticalActorBase => null; public virtual object Def => null; }
public class ShootAbility : TacticalAbility { }
public class MoveAbility : TacticalAbility { }
public class ReloadAbility : TacticalAbility { }
public class TacticalLevelController { }
public class Research { }
