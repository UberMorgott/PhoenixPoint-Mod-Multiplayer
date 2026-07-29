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
    ///   • 0xAA GeoResearch — retired 2026-07-26 (constant deleted same day): the bespoke research
    ///     side channel; research rides the 0xAC value rail + the 0xAB intent surface only.
    ///   • 0xB2 GeoScrapCart — retired 2026-07-26 (constant deleted same day): the shared scrap
    ///     cart's bespoke echo; the cart rides the generic value rail as mod root "M#cart".
    /// </remarks>
    public static class SurfaceIds
    {
        // ─── Geoscape action-relay envelope surfaces (spec 2026-07-02) — RETIRED tombstones ───
        // Were reserved for a single-surface generic intent framework (client intent → host outcome →
        // originator reject). The real framework landed 2026-07-23 as the IntentRail ENGINE instead:
        // families keep their own surface ids below (the surface byte IS the family discriminator —
        // SurfaceRouter already routes on it) and register op tables into IntentRail, which owns
        // nonce/dedup/dispatch. GeoOutcome/GeoReject never materialized on the wire: outcomes ride the
        // normal rail diff (0xAC) / the 0xAD order channel, rejects ride scoped DiffEngine
        // re-emits (IntentRail.Reject). Do not reuse these three ids.
        public const byte GeoIntent = 0xA2;   // retired: never emitted
        public const byte GeoOutcome = 0xA3;  // retired: never emitted
        public const byte GeoReject = 0xA4;   // retired: never emitted
        // ─── Old-repo per-kind geoscape mirrors — RETIRED tombstones ───
        // Ids reserved by the OLD repo's hand-sync codecs (GeoVehicleSnapshot / GeoVehicleTravelSnapshot /
        // GeoVehicleExploreSnapshot / HarvestFloatCodec / CrcProbeCodec). Never emitted by this repo — the
        // codecs were never quarried in. Their jobs ride the generic value rail 0xAC (vehicle placement /
        // travel metadata / exploration progress = covered value+order fields; harvest floats = client-side
        // presentation off its own native events). The old comments' "frozen client" premise is FALSE in
        // this repo: the client sim RUNS (clock ticks, law-4b gates skip the mutators). The CRC divergence
        // probe's job = the law-7 CRC-per-subtree backstop, still unbuilt here. Do not reuse these five ids.
        public const byte GeoVehiclePos = 0xA5;      // retired: never emitted
        public const byte GeoVehicleTravel = 0xA6;   // retired: never emitted
        public const byte GeoVehicleExplore = 0xA7;  // retired: never emitted
        public const byte GeoHarvestFloat = 0xA8;    // retired: never emitted
        public const byte GeoCrcProbe = 0xA9;        // retired: never emitted
        public const byte GeoResearchIntent = 0xAB;  // Research rail client→host INTENT ([nonce:u32][op:u8][factionGuid][researchId][pos:i32], op = start/cancel/front/up/down/insertAt); nonce rides the peer-aware IntentDedup; host validates + executes NATIVELY, outcome returns via the 0xAC value rail
        public const byte GeoRail = 0xAC;  // THE generic value rail (laws 5/6): host→all canonical metadata-guided diff deltas (inner = DiffEngine [MsgDelta:u8][seq:u32][kindDefs][entries], seq rides SurfaceSeq); client→host full-resend request on seq gap (inner = [MsgResyncRequest:u8], law 7 resync-on-gap)
        public const byte GeoManufacture = 0xAD;  // Manufacturing queue rail (migration #3) host→all ORDER snapshot: [seq:u32][count:u16][(itemDefGuid, accumulatedPoints:float)×N]; the un-keyable _queue (dup defs → excluded from the generic rail) is carried by explicit order, seq rides SurfaceSeq
        public const byte GeoManufactureIntent = 0xAE;  // Manufacturing rail client→host INTENT ([nonce:u32][op:u8][itemDefGuid][index:i32], op = queue/cancel/front/up/down/scrap/scrapVehicle/quickProduce + cart ops cartAdd/cartRemove/cartScrap ([defGuid][isVehicle], cartScrap body unused); for scrap the index slot carries the item COUNT); nonce rides IntentDedup; host validates (def-at-index still matches, or storage has count) + executes NATIVELY, queue outcome returns via 0xAD, scrap/buy outcome via the storage + wallet 0xAC value rail, cart outcome via mod root "M#cart" on 0xAC. (Equip-family ops moved to 0xB3 GeoEquipIntent 2026-07-26 — the surface byte IS the family.)
        public const byte GeoPersonnelIntent = 0xAF;  // Personnel progression rail client→host INTENT ([nonce:u32][op:u8][charId:i32][body], op = spendStats(str,will,speed INCREMENTS — the module's own numbers are bonus-inflated display values, only their delta is model-level) / buyAbility(trackSource,slotLevel,buttonLevel,abilityGuid) / secondSpec(specGuid) / reassign(dstRef) / skillReset(no body — free respec, host's own _hasSkillReset gates it) / hire(siteRef,vehicleRef — haven recruit, host re-derives cost/recruit) / fire(charId — KillCharacter Dismissed + vehicle scrap tail) / hireNaked(name,templateGuid,level,siteRef — content key, descriptors have no id) / tftvRedeploy(siteRef) / tftvTrainDeploy(early:bool) / tftvPromote(siteRef,specGuid — civilian→operative creation, host runs TFTV's own method)); nonce rides IntentDedup; host validates + re-derives the SP/mutagen cost from its OWN numbers + executes NATIVELY (ModifyBaseStat / LearnAbility / AddAbility / AddSecondaryClass / ResetCharacterProgression / the TFTV statics), outcome returns via the generic value rail 0xAC. Intent-only surface — there is no host→all personnel message.
        public const byte GeoTimeIntent = 0xB0;  // Geoscape time-control client→host INTENT ([nonce:u32][op:u8][val:u8], op = pause(val 0/1) / speed(val = TimeControlModule preset index — mod parity makes the index the stable id)); nonce rides IntentDedup; host applies NATIVELY (GeoscapeView.SetGamePauseState / UIModuleTimeControl.SelectTimePreset), outcome returns to all via root "T" Paused/Scale leaves + the "TA" TimeAnchor on 0xAC (same-frame: the setters raise EffectiveScaleChangedEvent → DiffEngine change-driven flush). Intent-only surface — there is no host→all time message.
        public const byte GeoBaseIntent = 0xB1;  // Base-construction client→host INTENT ([nonce:u32][op:u8][siteId:i32][body], op = build(facilityDefGuid,posX:i32,posY:i32) / demolish(facilityId:u32 — the UI scrap/cancel-construction path, host refunds natively) / repair(facilityId:u32) / togglePower(facilityId:u32 — toggle only, host re-derives the target value from its OWN IsPowered + Stats.RemainingPower)); nonce rides IntentDedup; host validates (CanBuildFacility + CanPlaceFacility / CannotDemolish / CanRepairFacility / PowerCost + RemainingPower) + executes NATIVELY (GeoPhoenixBase.ConstructFacility/RemoveFacility(scrap:true)/RepairFacility, GeoPhoenixFacility.SetPowered), outcome returns via the 0xAC value rail (facility values keyed by FacilityId) + the structural create/destroy applier. Intent-only surface.
        public const byte GeoEquipIntent = 0xB3;  // Soldier-equip rail client→host INTENT ([nonce:u32][op:u8][body], EquipSync owns capture+validation+native replay; op bytes kept from their 0xAE tenure — never renumber a live op: setItems=8([charId][flags][slot triples] — per-gesture loadout commit), augment=10([defGuid][charId] — augment install, bought via Wallet.Take), scrapEquipped=12([charId][list][defGuid][count][charges][amount] — per-INSTANCE scrap of an equipped item)); nonce rides IntentDedup; outcome returns via the 0xAC value rail (GeoCharacter EntityLists + storage GeoItemDict + wallet). Split off 0xAE 2026-07-26 (the surface byte IS the family). Intent-only surface.
    }
}
