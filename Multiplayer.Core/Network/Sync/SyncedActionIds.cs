namespace Multiplayer.Network.Sync
{
    /// <summary>Stable wire ids for every synced action. Never reuse a retired id.</summary>
    public static class SyncedActionIds
    {
        // Research 1-9
        public const ushort StartResearch = 1;
        public const ushort ResearchCompleted = 2;
        public const ushort CancelResearch = 3;
        public const ushort ReorderResearch = 4;

        // Manufacturing 10-19
        public const ushort QueueManufacture = 10;
        public const ushort ManufactureCompleted = 11;

        // Base 20-29
        public const ushort ConstructFacility = 20;
        public const ushort RepairFacility = 21;
        public const ushort FacilityCompleted = 22;
        public const ushort RemoveFacility = 23;   // demolition + cancel-construction (same native chokepoint)

        // Events 30-39
        public const ushort AnswerEvent = 30;

        // Vehicles / geoscape travel 40-49
        public const ushort MoveVehicle = 40;
        public const ushort ExploreSite = 41;

        // Presentation / narrative 50-59
        public const ushort PlayCutscene = 50;

        // Personnel client-edit intents 60-79 (PS4 — client manages OWN soldiers; each is a
        // permission-gated IHostOnlyApply intent whose authoritative result mirrors back on #6/#9/#10)
        public const ushort EquipSoldier = 60;     // GeoCharacter.SetItems full-loadout replace (also carries augment bodyparts)
        public const ushort AugmentSoldier = 61;   // GeoCharacter.SetItems bodypart(armour)-only swap
        public const ushort HireRecruit = 62;      // GeoPhoenixFaction.HireNakedRecruit (haven or naked pool → base)
        public const ushort TransferSoldier = 63;  // native RemoveCharacter(src)+AddCharacter(dst) between Phoenix containers
        public const ushort DismissSoldier = 64;   // GeoFaction.KillCharacter(soldier, Dismissed)
        public const ushort RenameSoldier = 65;    // GeoCharacter.Rename(newName)
        public const ushort KillCapturedUnit = 66; // GeoPhoenixFaction.KillCapturedUnit (containment "kill" button; keyed ordinal+TemplateDef-guid)
        public const ushort HarvestCapturedUnit = 67; // GeoPhoenixFaction.HarvestCapturedUnit (dismantle for food/mutagens; funnels through KillCapturedUnit)
        public const ushort LevelUpAbility = 68;   // buy a progression-track ability with SP (CharacterProgression.LearnAbility, SP split soldier→faction pool)
        public const ushort SpendStatPoints = 69;  // spend SP on a base stat (CharacterProgression.ModifyBaseStat +delta, per-point native cost)
        // 70 RESERVED: SecondSpecialization follow-up intent (ChoseSecondSpecialization is currently
        // suppressed WITHOUT relay on clients — see PersonnelEditPatches + COOP-SYNC-ROADMAP.md).

        // Geoscape sim-mutating abilities 80-89 (ONE generic GeoAbility.Activate relay — client suppress + relay
        // intent, host authoritative Activate; result mirrors on the existing geoscape state channels)
        public const ushort GeoAbilityActivate = 80; // Harvest/Excavate/EmergencyRepair/Scan/AncientSiteProbe/ActivateBase/AncientGuardianGuard

        /// <summary>True for the personnel client-edit intent family (ids 60-79). Every member is
        /// <c>IHostOnlyApply</c> whose authoritative result mirrors back on the #6/#9/#10 state channels
        /// (+ wallet), so the host NEVER needs to echo its GeoOutcome to clients — the client's OnActionApply
        /// would only suppress it. The host uses this to skip that redundant per-edit echo (the EditSoldier
        /// SetItems re-flush made it a per-frame storm). Other IHostOnlyApply families (research/manufacture/
        /// vehicle/answer) KEEP their echo — their subsystems are untouched.</summary>
        public static bool IsPersonnelEditIntent(ushort id) => id >= EquipSoldier && id <= 79;
    }
}
