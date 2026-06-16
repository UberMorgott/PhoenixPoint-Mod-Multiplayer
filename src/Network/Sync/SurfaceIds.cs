namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Stable wire ids for every synced SURFACE (action or state channel) on the unified
    /// <see cref="SurfaceRouter"/>. Never reuse a retired id. Action surface ids mirror
    /// <see cref="SyncedActionIds"/> exactly so the migration is byte-for-byte stable; state-channel
    /// surface ids reuse the channel's existing <c>ChannelId</c> (registered in a later phase).
    /// </summary>
    public static class SurfaceIds
    {
        // Action surfaces (mirror SyncedActionIds) ─────────────────────────
        public const byte StartResearch = 1;
        public const byte ResearchCompleted = 2;
        public const byte CancelResearch = 3;
        public const byte QueueManufacture = 10;
        public const byte ManufactureCompleted = 11;
        public const byte ConstructFacility = 20;
        public const byte RepairFacility = 21;
        public const byte FacilityCompleted = 22;
        public const byte AnswerEvent = 30;

        // State-channel surfaces (Phase 2 — claimed, not yet registered) ────
        public const byte InventoryChannel = 1;   // distinct id-space from actions (kind disambiguates)
        public const byte ResearchChannel = 2;
    }
}
