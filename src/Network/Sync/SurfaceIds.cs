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
        public const byte ReorderResearch = 4;
        public const byte QueueManufacture = 10;
        public const byte ManufactureCompleted = 11;
        public const byte ConstructFacility = 20;
        public const byte RepairFacility = 21;
        public const byte FacilityCompleted = 22;
        public const byte AnswerEvent = 30;

        // State-channel surfaces (Phase 2 — claimed, not yet registered) ────
        public const byte InventoryChannel = 1;   // distinct id-space from actions (kind disambiguates)
        public const byte ResearchChannel = 2;
        public const byte UnlockChannel = 3;       // research-unlock availability (facilities/manufacture/augmentations)
        public const byte DiplomacyChannel = 4;    // faction diplomacy / reputation (value-only mirror)
        public const byte GeoSiteChannel = 5;       // GeoSite identity mirror (Owner/Type/State/name/EncounterID) — Case A

        // ─── Geoscape envelope surfaces (unified backbone spec §2.1 partition 0xA0-0xBF) — Inc1 rail unify ───
        // Migrated geoscape host→all messages ride the SAME 0x67 SurfaceRouter chokepoint as tactical, on ids
        // in the geoscape partition (non-overlapping with tactical 0x80-0x9F and the legacy action/channel
        // ids 1-30 above). Emitted only behind GeoRailGate; the legacy raw packet stays the primary path.
        public const byte GeoWallet = 0xA0;   // host→all versioned full-wallet snapshot (mirrors legacy WalletSync 0x63)
        public const byte GeoState = 0xA1;    // host→all per-channel versioned state echo (mirrors legacy StateSync 0x64; inner = EncodeStateSync(channelId,version,payload))
    }
}
