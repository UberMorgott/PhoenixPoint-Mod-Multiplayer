namespace Multipleer.Network.Sync
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

        // Events 30-39
        public const ushort AnswerEvent = 30;

        // Vehicles / geoscape travel 40-49
        public const ushort MoveVehicle = 40;
    }
}
