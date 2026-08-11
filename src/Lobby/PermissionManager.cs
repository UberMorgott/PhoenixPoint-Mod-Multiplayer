using System;
using System.Collections.Generic;
using Multiplayer.Network;

namespace Multiplayer.Validation
{
    [Flags]
    public enum CampaignPermission
    {
        None                = 0,
        ControlSoldiers     = 1 << 0,   // 0x001
        ManageEquipment     = 1 << 1,   // 0x002
        ManageBases         = 1 << 2,   // 0x004
        ManageResearch      = 1 << 3,   // 0x008
        ManageManufacturing = 1 << 4,   // 0x010
        ManageRecruitment   = 1 << 5,   // 0x020
        ManageAircraft      = 1 << 6,   // 0x040
        ControlTime         = 1 << 7,   // 0x080  (geoscape clock)
        ForceEndTurn        = 1 << 8,   // 0x100  (tactical turn-end)
        FullCommander       = 1 << 9,   // 0x200  (moved from 1<<7)
        ManageDialogs       = 1 << 10   // 0x400  (geoscape event choices)
    }

    public static class PermissionManager
    {
        private static readonly Dictionary<Guid, PlayerAssignment> _assignments =
            new Dictionary<Guid, PlayerAssignment>();

        public static void SetPermission(Guid playerGuid, CampaignPermission permission, bool granted)
        {
            if (!_assignments.TryGetValue(playerGuid, out var assignment))
            {
                assignment = new PlayerAssignment { PlayerGuid = playerGuid };
                _assignments[playerGuid] = assignment;
            }

            if (granted)
                assignment.Permissions |= (int)permission;
            else
                assignment.Permissions &= ~(int)permission;
        }

        public static void SetPermissionsRaw(Guid playerGuid, int permissions)
        {
            if (!_assignments.TryGetValue(playerGuid, out var assignment))
            {
                assignment = new PlayerAssignment { PlayerGuid = playerGuid };
                _assignments[playerGuid] = assignment;
            }
            assignment.Permissions = permissions;
        }

        public static int GetPermissions(Guid playerGuid)
        {
            return _assignments.TryGetValue(playerGuid, out var assignment)
                ? assignment.Permissions
                : 0;
        }
    }

    public class PlayerAssignment
    {
        public Guid PlayerGuid { get; set; }
        public int Permissions { get; set; }
    }
}
