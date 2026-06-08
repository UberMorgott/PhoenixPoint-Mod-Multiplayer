using System;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;

namespace Multipleer.Validation
{
    public static class ActionValidator
    {
        // Resolve a per-session transport peerID (steamId) to the sender's persistent playerGUID
        // via the live session roster. Permission/ownership is keyed by GUID, not by peerID.
        private static Guid ResolveGuid(ulong clientSteamId)
        {
            var session = NetworkEngine.Instance?.Session;
            if (session != null && session.Clients.TryGetValue(clientSteamId, out var client))
                return client.PlayerGuid;
            return Guid.Empty;
        }

        // ─── Tactical Action Validation ───────────────────────────────────

        public static ValidationResult ValidateTacticalAction(
            ulong clientSteamId, TacticalActionMessage action)
        {
            var playerGuid = ResolveGuid(clientSteamId);

            // HARDENING: an unresolved / empty GUID can never be a valid permission key.
            if (playerGuid == Guid.Empty)
                return ValidationResult.Fail("Unknown player identity");

            // Permission check: does client control this soldier?
            if (!PermissionManager.CanControlSoldier(playerGuid, action.ActorGeoId))
            {
                return ValidationResult.Fail("You do not control this soldier");
            }

            // TODO: Game-state validation
            // - Does the actor exist and is alive?
            // - Does the actor have enough AP/WP?
            // - Is the ability available (not on cooldown)?
            // - Is the target valid (in range, line of sight)?
            // These require access to the live TacticalActor state
            // and will be implemented when Harmony patches are active.

            return ValidationResult.Ok();
        }

        // ─── Campaign Action Validation ───────────────────────────────────

        public static ValidationResult ValidateCampaignAction(
            ulong clientSteamId, CampaignActionMessage action)
        {
            var playerGuid = ResolveGuid(clientSteamId);

            // HARDENING: an unresolved / empty GUID can never be a valid permission key.
            if (playerGuid == Guid.Empty)
                return ValidationResult.Fail("Unknown player identity");

            var requiredPermission = GetRequiredPermission(action.ActionType);

            if (!PermissionManager.HasCampaignPermission(playerGuid, requiredPermission))
            {
                return ValidationResult.Fail(
                    $"You don't have permission: {requiredPermission}");
            }

            return ValidationResult.Ok();
        }

        private static CampaignPermission GetRequiredPermission(CampaignActionType type)
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
    }

    public struct ValidationResult
    {
        public bool IsValid { get; private set; }
        public string ErrorMessage { get; private set; }

        public static ValidationResult Ok()
            => new ValidationResult { IsValid = true, ErrorMessage = null };

        public static ValidationResult Fail(string reason)
            => new ValidationResult { IsValid = false, ErrorMessage = reason };
    }
}
