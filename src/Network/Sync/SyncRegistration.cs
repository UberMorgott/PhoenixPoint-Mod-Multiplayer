using Multipleer.Network.Sync.Actions;
using Multipleer.Network.Sync.State;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Registers every <see cref="ISyncedAction"/> reader into <see cref="SyncedActionRegistry"/>.
    /// Called once from the <see cref="SyncEngine"/> ctor. Each reader reconstructs the action from its
    /// wire payload; the id is the stable <see cref="SyncedActionIds"/> constant.
    /// </summary>
    public static class SyncRegistration
    {
        private static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;

            // Research
            SyncedActionRegistry.Register(SyncedActionIds.StartResearch, StartResearchAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ResearchCompleted, ResearchCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.CancelResearch, CancelResearchAction.Read);

            // Manufacturing
            SyncedActionRegistry.Register(SyncedActionIds.QueueManufacture, QueueManufactureAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ManufactureCompleted, ManufactureCompletedAction.Read);

            // Base construction / repair
            SyncedActionRegistry.Register(SyncedActionIds.ConstructFacility, ConstructFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RepairFacility, RepairFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.FacilityCompleted, FacilityCompletedAction.Read);

            // Events / dialogs
            SyncedActionRegistry.Register(SyncedActionIds.AnswerEvent, AnswerEventAction.Read);
        }

        /// <summary>
        /// Register every action surface into the unified <see cref="SurfaceRegistry"/> (Phase 1).
        /// Same readers as <see cref="RegisterAll"/>, each mapped to the geoscape screen its apply
        /// refreshes. State channels are registered in a later phase. Idempotent per registry instance.
        /// </summary>
        public static void RegisterSurfaces(SurfaceRegistry reg)
        {
            // Research → Research screen
            reg.RegisterAction(SurfaceIds.StartResearch, StartResearchAction.Read, GeoUiRefresh.Screen.Research);
            reg.RegisterAction(SurfaceIds.ResearchCompleted, ResearchCompletedAction.Read, GeoUiRefresh.Screen.Research);
            reg.RegisterAction(SurfaceIds.CancelResearch, CancelResearchAction.Read, GeoUiRefresh.Screen.Research);

            // Manufacturing → Manufacturing screen
            reg.RegisterAction(SurfaceIds.QueueManufacture, QueueManufactureAction.Read, GeoUiRefresh.Screen.Manufacturing);
            reg.RegisterAction(SurfaceIds.ManufactureCompleted, ManufactureCompletedAction.Read, GeoUiRefresh.Screen.Manufacturing);

            // Base construction / repair → BaseLayout screen
            reg.RegisterAction(SurfaceIds.ConstructFacility, ConstructFacilityAction.Read, GeoUiRefresh.Screen.BaseLayout);
            reg.RegisterAction(SurfaceIds.RepairFacility, RepairFacilityAction.Read, GeoUiRefresh.Screen.BaseLayout);
            reg.RegisterAction(SurfaceIds.FacilityCompleted, FacilityCompletedAction.Read, GeoUiRefresh.Screen.BaseLayout);

            // Event answer → no single screen (event dialog closes via its own path); null screen.
            reg.RegisterAction(SurfaceIds.AnswerEvent, AnswerEventAction.Read, null);
        }
    }
}
