using Multiplayer.Network.Sync.Actions;

namespace Multiplayer.Network.Sync
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
            SyncedActionRegistry.Register(SyncedActionIds.ReorderResearch, ReorderResearchAction.Read);

            // Manufacturing
            SyncedActionRegistry.Register(SyncedActionIds.QueueManufacture, QueueManufactureAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ManufactureCompleted, ManufactureCompletedAction.Read);

            // Base construction / repair
            SyncedActionRegistry.Register(SyncedActionIds.ConstructFacility, ConstructFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RepairFacility, RepairFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.FacilityCompleted, FacilityCompletedAction.Read);

            // Events / dialogs
            SyncedActionRegistry.Register(SyncedActionIds.AnswerEvent, AnswerEventAction.Read);

            // Vehicles / geoscape travel
            SyncedActionRegistry.Register(SyncedActionIds.MoveVehicle, MoveVehicleAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ExploreSite, ExploreSiteAction.Read);

            // Presentation / narrative
            SyncedActionRegistry.Register(SyncedActionIds.PlayCutscene, PlayCutsceneAction.Read);
        }
    }
}
