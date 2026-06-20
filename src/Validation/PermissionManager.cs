using System;
using System.Collections.Generic;
using Multipleer.Network;

namespace Multipleer.Validation
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

        public static void AssignSoldier(Guid playerGuid, int geoUnitId)
        {
            if (!_assignments.TryGetValue(playerGuid, out var assignment))
            {
                assignment = new PlayerAssignment { PlayerGuid = playerGuid };
                _assignments[playerGuid] = assignment;
            }
            assignment.OwnedSoldierIds.Add(geoUnitId);
        }

        public static void UnassignSoldier(Guid playerGuid, int geoUnitId)
        {
            if (_assignments.TryGetValue(playerGuid, out var assignment))
            {
                assignment.OwnedSoldierIds.Remove(geoUnitId);
            }
        }

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

        /// <summary>
        /// Clear ALL player assignments + permissions. Test-support: lets a test reset this process-global
        /// holder to a clean slate so leaked entries can't bleed across tests. Behavior-neutral for the live
        /// game (single co-op session — the holder is populated fresh from the roster on each session).
        /// </summary>
        public static void Reset() => _assignments.Clear();

        // ─── Combat Permissions ───────────────────────────────────────────

        public static bool CanControlSoldier(Guid playerGuid, int geoUnitId)
        {
            if (!_assignments.TryGetValue(playerGuid, out var assignment))
                return false;

            if (HasFlag(assignment.Permissions, CampaignPermission.FullCommander))
                return true;

            if (!HasFlag(assignment.Permissions, CampaignPermission.ControlSoldiers))
                return false;

            return assignment.OwnedSoldierIds.Contains(geoUnitId);
        }

        // ─── Campaign Permissions ─────────────────────────────────────────

        public static bool HasCampaignPermission(Guid playerGuid, CampaignPermission permission)
        {
            if (!_assignments.TryGetValue(playerGuid, out var assignment))
                return false;

            if (HasFlag(assignment.Permissions, CampaignPermission.FullCommander))
                return true;

            return HasFlag(assignment.Permissions, permission);
        }

        public static int GetPermissions(Guid playerGuid)
        {
            return _assignments.TryGetValue(playerGuid, out var assignment)
                ? assignment.Permissions
                : 0;
        }

        // ─── Player Query ─────────────────────────────────────────────────

        public static Guid? GetOwnerOfSoldier(int geoUnitId)
        {
            foreach (var kvp in _assignments)
            {
                if (kvp.Value.OwnedSoldierIds.Contains(geoUnitId))
                    return kvp.Key;
            }
            return null;
        }

        public static PlayerAssignment GetAssignment(Guid playerGuid)
        {
            return _assignments.TryGetValue(playerGuid, out var assignment)
                ? assignment
                : new PlayerAssignment { PlayerGuid = playerGuid };
        }

        public static IReadOnlyDictionary<Guid, PlayerAssignment> GetAllAssignments()
            => _assignments;

        private static bool HasFlag(int permissions, CampaignPermission flag)
            => (permissions & (int)flag) != 0;
    }

    public class PlayerAssignment
    {
        public Guid PlayerGuid { get; set; }
        public string PlayerName { get; set; } = "";
        public int Permissions { get; set; }
        public HashSet<int> OwnedSoldierIds { get; set; } = new HashSet<int>();
    }
}
