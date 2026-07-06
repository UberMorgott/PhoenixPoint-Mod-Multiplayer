using System;
using Multiplayer.Network;
using Multiplayer.Validation;

namespace Multiplayer.Network.Sync
{
    /// <summary>Action categories that map to <see cref="CampaignPermission"/> bits.</summary>
    public enum ActionCategory
    {
        Research,
        Manufacturing,
        BaseConstruction,
        BaseRepair,
        Recruitment,
        Equip,
        Dialogs,
        TimeControl,
        VehicleTravel,
        ControlSoldiers,  // PS4 soldier-owned edits (transfer / dismiss / rename) — capability bit + per-soldier ownership (Validate)
        GeoAbility        // geoscape sim-mutating ability activation (harvest/excavate/repair/scan/probe/activate-base/guard) — default FullCommander, like VehicleTravel
    }

    /// <summary>
    /// Single permission chokepoint every sync interceptor calls FIRST.
    /// Today permissive (every joiner gets <see cref="CampaignPermission.FullCommander"/> by
    /// default), but the wiring is complete: a future per-player permission menu only flips
    /// <see cref="PermissionManager"/> bits — no gate code changes.
    /// </summary>
    public static class PermissionGate
    {
        /// <summary>
        /// Optional UI feedback hook for denied local actions (set by the UI layer; no-op if null).
        /// </summary>
        public static Action<ActionCategory> OnDenied;

        public static CampaignPermission PermissionFor(ActionCategory c)
        {
            switch (c)
            {
                case ActionCategory.Research: return CampaignPermission.ManageResearch;
                case ActionCategory.Manufacturing: return CampaignPermission.ManageManufacturing;
                case ActionCategory.BaseConstruction: return CampaignPermission.ManageBases;
                case ActionCategory.BaseRepair: return CampaignPermission.ManageBases;
                case ActionCategory.Recruitment: return CampaignPermission.ManageRecruitment;
                case ActionCategory.Equip: return CampaignPermission.ManageEquipment;
                case ActionCategory.ControlSoldiers: return CampaignPermission.ControlSoldiers;
                case ActionCategory.Dialogs: return CampaignPermission.ManageDialogs;
                case ActionCategory.TimeControl: return CampaignPermission.ControlTime;
                default: return CampaignPermission.FullCommander;
            }
        }

        /// <summary>Check a specific player (host uses this for incoming requests).</summary>
        public static bool CheckFor(Guid playerGuid, ActionCategory c)
            => PermissionManager.HasCampaignPermission(playerGuid, PermissionFor(c));

        /// <summary>Check the LOCAL player (interceptors use this).</summary>
        public static bool Check(ActionCategory c)
            => CheckFor(ClientIdentity.PlayerGuid, c);

        public static void Notify(ActionCategory c) => OnDenied?.Invoke(c);
    }
}
