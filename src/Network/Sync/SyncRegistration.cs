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
        private static readonly object _lock = new object();
        private static bool _done;

        public static void RegisterAll()
        {
            // Idempotent + thread-safe: _done flips only AFTER every reader is in, under the lock —
            // a concurrent caller (xunit parallel test classes; in-game it's a single SyncEngine-ctor
            // call) never observes a half-registered registry.
            lock (_lock)
            {
                if (_done) return;
                RegisterAllCore();
                _done = true;
            }
        }

        private static void RegisterAllCore()
        {

            // Research
            SyncedActionRegistry.Register(SyncedActionIds.StartResearch, StartResearchAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ResearchCompleted, ResearchCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.CancelResearch, CancelResearchAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ReorderResearch, ReorderResearchAction.Read);

            // Manufacturing
            SyncedActionRegistry.Register(SyncedActionIds.QueueManufacture, QueueManufactureAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ManufactureCompleted, ManufactureCompletedAction.Read);

            // Base construction / repair / demolition
            SyncedActionRegistry.Register(SyncedActionIds.ConstructFacility, ConstructFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RepairFacility, RepairFacilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.FacilityCompleted, FacilityCompletedAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RemoveFacility, RemoveFacilityAction.Read);

            // Events / dialogs
            SyncedActionRegistry.Register(SyncedActionIds.AnswerEvent, AnswerEventAction.Read);

            // Vehicles / geoscape travel
            SyncedActionRegistry.Register(SyncedActionIds.MoveVehicle, MoveVehicleAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.ExploreSite, ExploreSiteAction.Read);

            // Presentation / narrative
            SyncedActionRegistry.Register(SyncedActionIds.PlayCutscene, PlayCutsceneAction.Read);

            // Personnel client-edit intents (PS4)
            SyncedActionRegistry.Register(SyncedActionIds.EquipSoldier, EquipSoldierAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.AugmentSoldier, AugmentSoldierAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.HireRecruit, HireRecruitAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.TransferSoldier, TransferSoldierAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.DismissSoldier, DismissSoldierAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.RenameSoldier, RenameSoldierAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.KillCapturedUnit, KillCapturedUnitAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.HarvestCapturedUnit, HarvestCapturedUnitAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.LevelUpAbility, LevelUpAbilityAction.Read);
            SyncedActionRegistry.Register(SyncedActionIds.SpendStatPoints, SpendStatPointsAction.Read);

            // Geoscape sim-mutating abilities (ONE generic GeoAbility.Activate relay)
            SyncedActionRegistry.Register(SyncedActionIds.GeoAbilityActivate, GeoAbilityActivateAction.Read);

            // Kaos "The Marketplace" buy (AB DLC5)
            SyncedActionRegistry.Register(SyncedActionIds.MarketplaceBuy, MarketplaceBuyAction.Read);
        }
    }
}
