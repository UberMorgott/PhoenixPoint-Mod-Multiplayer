namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Stable wire ids for every synced SURFACE on the unified <see cref="SurfaceRouter"/> — the
    /// surfaceId byte of the 0x67 envelope. Geoscape partition = 0xA0-0xBF (tactical rides 0x80-0x9F).
    /// Never reuse a retired id: a new sender on one would silently mis-route on an old peer.
    /// </summary>
    /// <remarks>
    /// ─── RETIRED / RESERVED ids — permanent tombstones, do NOT reuse ─────────────────
    ///   • ids 1-30 / 60-79 (action surfaces) + 1-10 (state channels) — the old repo's per-kind
    ///     migration catalog (mirrored its SyncedActionIds/ChannelIds). Never emitted by this repo;
    ///     constants deleted 2026-07-22. The live partition starts at 0xA0.
    ///   • 0xA0 GeoWallet / 0xA1 GeoState — retired: wallet + per-channel state ride the generic
    ///     value rail 0xAC (GeoRail); no sender remains.
    /// </remarks>
    public static class SurfaceIds
    {
        // ─── Geoscape action-relay envelope surfaces (spec 2026-07-02) — RETIRED tombstones ───
        // Were reserved for a single-surface generic intent framework (client intent → host outcome →
        // originator reject). The real framework landed 2026-07-23 as the IntentRail ENGINE instead:
        // families keep their own surface ids below (the surface byte IS the family discriminator —
        // SurfaceRouter already routes on it) and register op tables into IntentRail, which owns
        // nonce/dedup/dispatch. GeoOutcome/GeoReject never materialized on the wire: outcomes ride the
        // normal rail diff (0xAC) / order channels (0xAA/0xAD), rejects ride scoped DiffEngine
        // re-emits (IntentRail.Reject). Do not reuse these three ids.
        public const byte GeoIntent = 0xA2;   // retired: never emitted
        public const byte GeoOutcome = 0xA3;  // retired: never emitted
        public const byte GeoReject = 0xA4;   // retired: never emitted
        public const byte GeoVehiclePos = 0xA5;  // host→all moving-vehicle world placement (Inc4 S2 travel mirror; inner = GeoVehicleSnapshot.Encode(seq, records))
        public const byte GeoVehicleTravel = 0xA6;  // host→all vehicle TRAVEL METADATA (Inc4 S2 route-line mirror: travelling/currentSite/destinationSites; inner = GeoVehicleTravelSnapshot.Encode(seq, records)) — feeds the native yellow route line on the frozen client
        public const byte GeoVehicleExplore = 0xA7;  // host→all vehicle SITE-EXPLORATION PROGRESS (exploring/siteId/progress 0..1; inner = GeoVehicleExploreSnapshot.Encode(seq, records)) — feeds the native site exploration progress bar on the frozen client (whose exploration timer never ticks)
        public const byte GeoHarvestFloat = 0xA8;  // host→all resource-harvest FLOAT mirror (Batch-2 P6: occId/siteId/resourceType/value; inner = HarvestFloatCodec.Encode) — display-only, client replays its own native GeoSite.ShowResourceHarvested; the wallet values on the generic rail 0xAC stay the one silent balance writer
        public const byte GeoCrcProbe = 0xA9;  // host→all rolling CRC divergence probe (Inc5 part 1: once per in-game hour, CRC32 per deterministic state SUBSET; inner = CrcProbeCodec.Encode(round, entries), round rides SurfaceSeq) — detection only: client recomputes+compares (DivergenceMonitor), loud log + toast on divergence, NEVER auto-resyncs
        public const byte GeoResearch = 0xAA;  // Research rail (migration #1) host→all deltas: start (native-serializer blob, value-only fallback) / ≤2 Hz progress value / queue-order snapshot / complete; inner = ResearchSync codec ([msg:u8][seq:u32][factionGuid]…), seq rides SurfaceSeq
        public const byte GeoResearchIntent = 0xAB;  // Research rail client→host INTENT ([nonce:u32][op:u8][factionGuid][researchId][pos:i32], op = start/cancel/front/up/down/insertAt); nonce rides the peer-aware IntentDedup; host validates + executes NATIVELY, outcome returns via 0xAA
        public const byte GeoRail = 0xAC;  // THE generic value rail (laws 5/6): host→all canonical metadata-guided diff deltas (inner = DiffEngine [MsgDelta:u8][seq:u32][kindDefs][entries], seq rides SurfaceSeq); client→host full-resend request on seq gap (inner = [MsgResyncRequest:u8], law 7 resync-on-gap)
        public const byte GeoManufacture = 0xAD;  // Manufacturing queue rail (migration #3) host→all ORDER snapshot: [seq:u32][count:u16][(itemDefGuid, accumulatedPoints:float)×N]; the un-keyable _queue (dup defs → excluded from the generic rail) is carried by explicit order, seq rides SurfaceSeq
        public const byte GeoManufactureIntent = 0xAE;  // Manufacturing rail client→host INTENT ([nonce:u32][op:u8][itemDefGuid][index:i32], op = queue/cancel/front/up/down/scrap/scrapVehicle; for scrap the index slot carries the item COUNT); nonce rides IntentDedup; host validates (def-at-index still matches, or storage has count) + executes NATIVELY, queue outcome returns via 0xAD and scrap outcome via the storage + wallet 0xAC value rail
        public const byte GeoPersonnelIntent = 0xAF;  // Personnel progression rail client→host INTENT ([nonce:u32][op:u8][charId:i32][body], op = spendStats(str,will,speed INCREMENTS — the module's own numbers are bonus-inflated display values, only their delta is model-level) / buyAbility(trackSource,slotLevel,buttonLevel,abilityGuid) / secondSpec(specGuid)); nonce rides IntentDedup; host validates + re-derives the SP/mutagen cost from its OWN numbers + executes NATIVELY (ModifyBaseStat / LearnAbility / AddAbility / AddSecondaryClass), outcome returns via the generic value rail 0xAC. Intent-only surface — there is no host→all personnel message.
        public const byte GeoTimeIntent = 0xB0;  // Geoscape time-control client→host INTENT ([nonce:u32][op:u8][val:u8], op = pause(val 0/1) / speed(val = TimeControlModule preset index — mod parity makes the index the stable id)); nonce rides IntentDedup; host applies NATIVELY (GeoscapeView.SetGamePauseState / UIModuleTimeControl.SelectTimePreset), outcome returns to all via root "T" Paused/Scale leaves + the "TA" TimeAnchor on 0xAC (same-frame: the setters raise EffectiveScaleChangedEvent → DiffEngine change-driven flush). Intent-only surface — there is no host→all time message.
    }
}
