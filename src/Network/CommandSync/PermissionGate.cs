using System;
using Multipleer.Network.MessageLayer;
using Multipleer.Validation;

namespace Multipleer.Network.CommandSync
{
    // Real per-playerGUID permission gate. Replaces the GUID-blind CampaignPermissionHelper.Check
    // (host=allow/client=block) with an actual flag lookup against PermissionManager. Pure: no
    // NetworkEngine — the caller (HostArbiter) resolves sender->GUID and passes it in.
    public static class PermissionGate
    {
        public static CampaignPermission RequiredPermission(CampaignActionType type)
        {
            switch (type)
            {
                case CampaignActionType.StartResearch:
                    return CampaignPermission.ManageResearch;
                case CampaignActionType.QueueManufacturing:
                case CampaignActionType.CancelManufacturing:
                    return CampaignPermission.ManageManufacturing;
                case CampaignActionType.ConstructFacility:
                case CampaignActionType.RemoveFacility:
                case CampaignActionType.RepairFacility:
                    return CampaignPermission.ManageBases;
                case CampaignActionType.EquipSoldier:
                case CampaignActionType.EquipVehicle:
                    return CampaignPermission.ManageEquipment;
                case CampaignActionType.HireRecruit:
                case CampaignActionType.DismissSoldier:
                    return CampaignPermission.ManageRecruitment;
                case CampaignActionType.DeployAircraft:
                case CampaignActionType.AssignSoldier:
                case CampaignActionType.RemoveSoldier:
                case CampaignActionType.StartTravel:
                    return CampaignPermission.ManageAircraft;
                default:
                    return CampaignPermission.FullCommander;
            }
        }

        public static bool IsAllowed(Guid playerGuid, CampaignActionType type)
        {
            if (playerGuid == Guid.Empty) return false;
            return PermissionManager.HasCampaignPermission(playerGuid, RequiredPermission(type));
        }
    }
}
