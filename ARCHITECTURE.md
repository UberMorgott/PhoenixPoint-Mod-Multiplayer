# Architecture — the sync rail

How this mod keeps two campaigns identical: the host runs the game, a generic diff of the live
object graph becomes a delta, and every peer applies it. Grounded in the decompiled game assembly;
where a design intention and an observed engine fact diverge, the fact below wins.

## Why the delta is a live-graph walk, not a blob diff

The obvious design — "serialize the whole state, diff the bytes" — is NOT
feasible: serializer ObjectIDs are session-local nondeterministic ints
(`SerializationWriter._object2ID`, hash-collection traversal order); wire format not structurally
diffable; `SerializationReader.ReadObjects` always builds NEW graphs (`Activator.CreateInstance`).
**Implementation of the law:** the diff engine walks the LIVE game object graph, addressed by
stable game IDs, using the save serializer's TYPE METADATA (`[SerializeType]` + AQN field
discovery) to enumerate persistent fields generically — this preserves the design goals
(generalized enumeration kills "forgot the field"; canonical byte-identical deltas via sorted-ID
traversal + fixed field order; TFTV fields ride free) without depending on blob determinism.
Serializer blobs remain ONLY as payloads for structural creates (spawn → blob → deserialize →
attach → fire native added-event).

## Proven building blocks

- Per-entity blob roundtrip: `SerializeGraph`/`DeserializeGraph` → `src/Rail/SerializerRoundtrip.cs`.
- Live hot-apply by field-copy onto running instances: `PersonnelChannel.Apply` →
  `PersonnelReflection.ApplySoldierState`; spawn path `TacticalActorLifecycleSync.HandleActorSpawn`
  → `ActorSpawner.SpawnActor`.
- Stable game-side addressing: `GeoSite.SiteId`, `GeoTacUnitId`, `GeoVehicle.VehicleID`,
  `PPFactionDef` / `ResearchDef` GUIDs.

## Rail design

**Value-delta (universal apply: set fields on live instance + fire native event) covers:**
`Wallet._resources` + `ResourcesChanged`; `ResearchElement` progress + `_researchQueue` order;
`FactionDiplomacy` values; `GeoSite` state/owner/production + `StateChanged`/`OwnerChanged`;
`GeoVehicle` pos (`GeoNavComponent`)/HP/name; ALL of `GeoCharacter` (plain class, no scene
binding — cleanest); `ItemManufacturing` progress; `Timing` Paused/Scale + the "TA" clock anchor
(never raw `Now`/`StartTime` — a raw clock mirror double-counts local accrual); mist/event vars.

**Structural appliers (hand-written, explicit list — the ONLY bespoke sync code allowed;
identity boundary per law 3):** site spawn/destroy (GO lifecycle + `GeoMap.SiteAdded/Removed`);
vehicle add/loss (scene binding + `VehicleAdded/Removed`); soldier hire/death (`_tacUnits`
registry + `CharacterAdded/Died`); base build (facility graph); haven zone built/destroyed;
research-complete reward chain; manufacture-complete item creation; mission start/end deployment.

**Wire:** SyncProtocol envelope over SurfaceRouter, SurfaceSeq + IntentDedup per
surface, CRC per path-subtree backstop (diverged subtree resent alone). Join/reconnect = native
save transfer (SaveTransferCoordinator). Journal (law 9): written post-pipeline,
observational, debug builds OK.

## Rail engine (implemented 2026-07-17) — THE generic value rail (laws 3/6/11)

One generic mechanism covers the whole geoscape VALUE layer; no more per-subsystem hand sync.
Surface `SurfaceIds.GeoRail` (0xAC), ~2 Hz host tick.

**Components (src/Rail/):**
- `RailMeta.cs` — per-type field tables from the game's OWN serializer metadata
  (`Serializer.GetSerializedMembers` on the configured instance; TFTV fields ride free). Two generic
  sources: direct (`[SerializeType]` members) or the `*InstanceData` DTO bridge (GeoFaction/GeoVehicle/
  GeoSite("GeoSiteInstaceData" game typo)/Timing): DTO metadata gives NAMES, resolved onto the live type
  by same-name then unique-same-class-type match; unresolved → EXCLUDED + reported. Field classes:
  Leaf / Descend / EntityCollection / LeafList (canonical one-value list, HashSet sorted) / LeafDict
  (per-subKey entries, e.g. `Wallet._resources`) / Excluded. Canonical leaf codec (bool/ints/floats/
  string/enum/TimeSpan/Vector3/Quaternion/DefRef(GUID)/EntityRef(root key)/Composite(struct via its own
  metadata, e.g. TimeUnit/ResourceUnit)).
- `IdentityResolver.cs` — the ONLY place that names things (law 2). ID-probe table
  SiteId/VehicleID/ResearchID/Id/Def-GUID; root registry `T | TA | F#<defGuid> | S#<siteId> |
  U#<tacUnitId> | V#<vehicleId>@<ownerFactionDefGuid> | MK | ES | MG | M#<name>` (level clock + "TA" anchor + the
  level's actor registries + level singletons + mod-owned roots — the entire hand-written table);
  `RegisterModRoot(key, state)` lets mod state ride the same generic engine under an `"M#<name>"`
  key on both peers (e.g. shared scrap cart "M#cart"; the bespoke 0xB2 channel was deleted).
  Engine fix: tombstone suppression no longer swallows deletes for a root that emits zero entries.
  Per-OWNER id kinds are qualified: `GeoVehicle.VehicleID` is issued by `GeoFaction._lastVehicleIndex`
  (GeoFaction.cs:2008/2025/2041), so the owner faction def GUID rides in the key — without it one
  aircraft per faction survived and the rest were eaten by the walk's first-wins dedup. Site ids
  (`GeoSitesMapper._nextSiteId`) and tac-unit ids (`GeoLevelController.CreateTacUnitId`) are
  level-wide, so they need no qualifier. Duplicate root keys are now a walk INCIDENT
  (`DiffEngine.WalkRoot`, harness law L17) and the coverage report prints the distinct-root-key
  census per kind — `TakeOverVehicle` (GeoFaction.cs:2041) re-issues an id, so a captured vehicle
  changes key and will show up there instead of vanishing quietly.
  Path grammar
  `root.Member.Member#elemKey…`; resolution symmetric on the client (same keys over its own graph).
  No stable key derivable → subtree EXCLUDED from the value rail (never index-addressed) + reported.
  **Root-visibility rule (2026-07-23):** every level singleton is a ROOT even when little or nothing
  rides yet — `GeoMarketplace` "MK" rides via its existing InstanceData bridge
  (MissionID+IsMissionInProgress); `GeoscapeEventSystem` "ES" / `GeoMissionGenerator` "MG" surface as
  VISIBLE no-persistent-members incidents. A singleton off the root list is a silent coverage hole;
  an incident is a to-do line.
- `DiffEngine.cs` (host) — universal walk (visited-set cycle-safe, depth 12 / 50k-entity brakes), flat
  snapshot (path, fieldIdx, subKey)→encoded bytes, diff vs previous, emit only changed pairs. Canonical
  (law 6): sorted roots/children/subkeys, fixed metadata field order. First walk per boundary = BASELINE,
  no emit (join state comes from the native save transfer, law 1). Dict-key removals → null tombstones.
  Perf logged every tick with traffic (walk/diff ms, entity/field/changed counts) + 10 s heartbeat.
  **Time-sliced periodic walk (2026-07-22).** Measurements demanded it (walk=49-138 ms per 0.5 s tick
  on the main thread, 2026-07-19 build = rhythmic stutter): the PERIODIC walk now runs as a CYCLE —
  root list SNAPSHOTTED at cycle start, ~3 ms of whole roots walked per frame, diff+emit as ONE batch
  at cycle completion (receiver cannot tell sliced from monolithic; wire unchanged). Cadence gates
  cycle START (0.5 s); an overrunning cycle just starts the next one immediately — no overlap, no
  carryover. No enumerator over live game state survives a frame (roots materialized once; each root
  finishes inside its slice); a root destroyed mid-cycle is skipped by the Unity fake-null guard,
  deeper mid-walk death rides the existing getter-throw Incident path, a vanished entity's stale dict
  keys stay tombstone-suppressed as before. TEARING ACCEPTED: fields read on different frames land in
  one batch — same consistency class as the old monolithic tick (which also read mid-mutation state),
  coarser grain; the repaint gate + next cycle converge. Forced walks stay SINGLE-SHOT monolithic
  (FlushNow / ForceReemit / full resend — rare, event-driven, one hitch accepted): a forced flush
  ABANDONS any in-progress cycle first (nothing ships before completion → no loss, no double-ship) so
  the forced walk sees its census/re-emit scope from root 0. MpDiag per-cycle line: frames used /
  total walk ms / max slice ms / roots / changed count.
- `GenericApplier.cs` (client) — whole batch in `SyncApplyScope` (law 8); entity located via
  IdentityResolver (path cache, invalidated on miss + reload boundary); set through cached metadata
  accessor; LeafList applied in-place (game exposes lists by reference). Unknown entity/path → log once
  + skip (identity creation = structural layer, law 3). Seq gap → resync request (throttled), host
  resends ALL covered pairs — just a big delta (law 7). **Unresolved sentinel (2026-07-23):** a ref
  that decodes but cannot resolve (unknown def GUID / unspawned entity / partial Composite) returns
  `RailMeta.Unresolved`, NEVER null — every applier arm (leaf/dict/blob/list/create-arg) SKIPS the
  write instead of clobbering a valid live ref the host would never re-ship (`docs/boundary-law.md` L-C).
- **DTO twin resolution (2026-07-21).** `ActorComponent.SerializationData` is a RECORD-on-read seam: its
  getter mints/re-records a DTO on every access (ActorComponent.cs:56-66) and the game applies one back
  only at level-enter (DoEnterPlay:114-124 → ProcessInstanceData) — so client writes into the resolved
  DTO were VOID and the entire GeoSite/GeoVehicle subtree (State/Weather/timers/ItemStorage/Travelling/
  CurrentSite/haven data…) never mirrored. `IdentityResolver.Resolve` now maps `SerializationData` path
  segments onto the LIVE owner via `RailType.GetBridged` (DTO member names resolved by the same
  `ResolveLive` as the GeoFaction bridge; fieldIdx parity by identical member source + ordinal sort);
  nested component InstanceData (GeoHaven/GeoPhoenixBase/VehicleFactionController…) dispatches via
  `GetComponent`, mirroring GeoSite.RecordInstanceData's own dispatch (GeoSite.cs:1501-1525).
  **Twin gap closure (2026-07-24):** `ResolveLive` gained three generic rungs + one data table —
  (a) `_name` backing-field convention (Weapons→`_weapons`, Modules→`_modules`, TacUnits→`_tacUnits`,
  MaintenancePointsToRepair→`_maintenancePointsToRepair`); (b) compatible-type name match: one-field
  wrapper struct (`RangeRemaining` float ⇄ `EarthUnits.Value`, `RailField.WrapFi`), GeoFaction-for-def
  (`OwnerFactionDef`/`Owner`/`DiplomaticObjectiveFaction` def⇄faction, `RailField.FactionRef` —
  write half `RailMeta.FactionByDef` in the applier), same-element mutable collection
  (Addons List⇄`_addons` HashSet — ApplyList already dispatches on the live container);
  (c) `_twinAliases` per-field data table for the game's own Record/Process mapping where no
  convention reaches (`Name`→`_vehicleName`, `HitPoints`→`Stats.HitPoints` via `RailField.HopFi`).
  Twin tables are now IN the RailCheck baseline (GetBridged dump + nested-dispatch chase), and L14
  asserts the coercions stay wired. Remaining members with no live counterpart (SurfacePos/SurfaceRot
  — travel mirror is a later migration, `NextUpdate` schedule bookkeeping…) stay one-time
  "dto-twin gap" logs, never silent drops. Calling
  the owner's `ProcessInstanceData` wholesale as a batch-end apply seam was audited and REFUSED for the
  live mirror: GeoSite's appends without clearing (`_addons`/`_tacUnits` doubling; GeoHaven `_zones` +
  `StockedResources`, the latter self-appending through a live-ref alias), re-registers/reschedules the
  active mission, and GeoVehicle's runs the `InstanceDataVersion < 3` HitPoints migration
  (GeoVehicle.cs:1112-1119) — save-load semantics on a virgin actor, not repeat-apply semantics.
  Per-actor `ActorInstanceData.TimingData` is opted out ship-side: `TimingInstanceData.OwnNow/OwnFixedNow`
  accrue on every actor every walk (~880 of the measured ~890 changed fields per 0.5 s during geo-time —
  the churn), client actor clocks tick locally, and the level clock rides the "TA" anchor.
- `UiEventMap.cs` (law 11) — per entity kind the native repaint: Wallet → raise its own
  `ResourcesChanged` (GeoscapeView relays → info bar/manufacturing/replenish repaint natively) AND
  mark the open screen dirty (`FactionResourcesChanged` has only 3 native subscribers — equip
  quick-produce / base build menu / research cost gating read the wallet pull-model and stayed stale);
  Research/ResearchElement → ResearchSync repaint path (open-screen SetupQueue rebuild, else
  agenda-tracker `_needsRefresh` nudge); Timing → none needed (Paused/Scale applied through native
  property setters which fire OnPausedEvent/EffectiveScaleChangedEvent); unknown kind → logged once.
  `Fire` runs inside `SyncApplyScope` — apply-driven repaints stay suppressed at intent seams +
  EquipStorageGate / SetItemsApplyGate.
- `ClientSimGate.cs` (law 4b) — client sim not frozen (clock ticks); gated local mutators:
  `GeoLevelController.LevelHourlyUpdateCrt` (ONE chokepoint: faction `ResourceIncome.Apply(Wallet)`,
  UpdateHavens, UpdateBasesHourly, UpdateResearch wallet drain, `Manufacture.Update`, GenerateRecruits,
  RepairFactionAircrafts, DailyUpdate — prefix-skipped on client, reschedule preserved) +
  `Research.Update` (kept in ResearchSync — reachable outside the hourly tick).

**Wire (0xAC inner):** delta = `[MsgDelta:u8][seq:u32][kindDefCount:u8]{[kindId:u8][typeFullName]
[fieldCount:u16]}*[entryCount:u16]{[kindId:u8][path][fieldIdx:u16][subKey][valLen:u16][value]}*`,
chunked ≤ ~45 KB per envelope, each chunk its own SurfaceSeq. Field name→index = per-type table sorted
by metadata name — derived identically on both peers; kindDef fieldCount mismatch = parity alarm (kind
skipped loudly). Client→host resync request = `[MsgResyncRequest:u8]`.

**Coverage report (the opt-out guarantee):** first full walk dumps every visited type — covered fields
(class, live alias) vs excluded (reason) + walk incidents (unkeyable collections etc.) — to log +
`persistentDataPath/Multiplayer/rail-coverage.txt`. The AUTHORITATIVE covered-kind/field list is that
runtime report (read it, don't discover by bug). Expected day-one coverage: Wallet resources (per-type
dict entries), ResearchElement `_state`/`ResearchProgress`/`IsInProgress` + requirement data,
Research `Paused`, Timing `Paused`/`Scale`, GeoCharacter values (+ identity/progression/fatigue/health
sub-objects), GeoVehicle scalars + twins (Travelling/CanRedirect/CurrentSite/VehicleID/HitPoints/
RangeRemaining/Name/Owner/Weapons/Modules/TacUnits…), GeoSite scalars + twins (State/Weather/
ExpiringTimerAt/Owner/Addons/GameTags/TacUnits…), faction GameTags/UnlockedAugmentations. Known
excluded-by-design: manufacture queue items (no stable element key — duplicates legal), Timing.OwnNow
(read-only — client clock ticks locally; Paused/Scale mirror), vehicle SurfacePos/SurfaceRot (travel
mirror is a later migration), per-subsystem `*InstanceData`-only scalars (NextUpdate schedule
bookkeeping — host-only sim, client gated anyway). The committed twin tables in
`docs/rail-baseline.txt` are the authoritative gap list.

**Repaint primitive (2026-07-23) — native rebuild table, re-enter demoted to fallback.**
`UiNativeRepaint` (lives with UiEventMap — the presentation-knowledge location) maps screen type →
the game's OWN read-direction refresh methods (model → live widgets, no lifecycle transition; every
method decompile-grounded, never guessed). `OpenUiRepaint` flush tries the table first (inside
`SyncApplyScope`, law 8); a registered screen that throws keeps the screen + logs once (partial
repaint beats ejection); an entry may DECLINE (return false: empty roster / missing module after a
game update) → that one flush falls back. Every reflection chain resolves ALL its MethodInfos before
invoking anything — no partial repaint before a decline. Wired: `UIStateManufacturing` (→
`ManufactureSync.RepaintManufacturingUi`, ex-opt-out branch; in Scrap mode it resyncs the screen's
STALE storage snapshot — scrap mode feeds from copies taken once on mode entry
(`UIModuleManufacturing.Init:363-371`), natively safe (screen pauses the game) but stale the instant
a remote delta lands, so the repaint replays the native snapshot block (Clear+AddItems+
StripPartialMagsFromScrapStorage) against live storage, preserving the staged cart clamped to live
counts, then subtracts the cart so staged items do not double-show), `UIStateResearch` (→
`ResearchSync.RepaintResearchUi`), `UIStateEditSoldier` (`_refreshStorage=true` + `OnDataChanged` +
`DisplaySoldier` + `RefreshStorage` + `SelectCharacterProgression` = `CharacterChangedHandler:358-365`
minus the UI→model write-back; selection preserved by reseeding the CURRENT character),
`UIStateEditVehicle` (same shape, progression via module `SetCharacterProgression`),
`UIStateGeoRoster` (`OnActorStatChanged:364` = module re-`Init` + unit-stats refresh, then
`SetSelectSlot(cur)`), `UIStateVehicleSelected` (`UpdateVehiclesTabs` + `UpdateVehicleActions`
(idempotent, clears via `UnsubscribeVehicleActions:1427`) + `UpdateReachableSitesMarkers` +
`OnFactionObjectivesChanged`), `UIStateNothingSelected` (`OnFactionObjectivesChanged:519`),
`UIStatePhoenixBaseLayout` (module `SetLeftSideInfo` + `SetupBaseLayout` — deliberately NOT
`UIModuleBaseLayout.Init`, which closes an open build menu :291-292; before rebuild it mirrors
Uninit's slot loop (VisualsContainer on + `DetachPrefab`) because `AttachFacilityPrefab` only
assigns (`PhoenixFacilityController.cs:349-352`) — without it repaint duplicated facility prefabs).
Unregistered screens still ride
Exit+Enter (same instance, throwing `Enter` swallowed + screen kept, `GeoscapeViewState.cs:88-94`
re-registers input first) — now flagged `[MP][uirepaint] fallback re-enter: <UIState>` once per type,
the to-do list for the next table entry. Drag/typing defer + one-flush-per-frame coalescing sit in
front of BOTH paths.

**What stays manual (by design):** structural creates/destroys (law 3 identity boundary), intents
(law 4a seams), the UiEventMap table (presentation knowledge), ResearchSync start-blob/complete/queue
messages, sim gates (law 4b).

## Generic client-intent engine (src/Rail/IntentRail.cs, 2026-07-23)

- ONE engine for every client→host intent surface (0xAB research / 0xAE manufacture /
  0xAF personnel / 0xB0 time / 0xB1 base / 0xB3 equip). Families register op tables
  (`RegisterIntents()` in the SyncEngine ctor: op → handler + optional reject-reconverge action);
  domain validators/appliers stay in the family files. The surface byte IS the family
  discriminator — no single shared surface; 0xA2-0xA4 (GeoIntent/GeoOutcome/GeoReject) stay
  retired tombstones.
- **Client-posture gate — BLOCK-FIRST, codified once** at `IntentRail.ShouldRunNative`
  (`c10fabc`): on a client outside `SyncApplyScope`, a native state mutation is BLOCKED and
  converted into an intent; the HOST executes the native method; the result mirrors back via the
  Delta rail. Presentation staging may proceed; model writes may not. All families route through
  this single gate (duplicated per-family copies collapsed).
- Engine owns: envelope `[nonce:u32][op:u8][family body]` (`IntentRail.Send`, one client emit), ONE
  shared nonce allocator (dedup key is (peer, surface, nonce) — no cross-surface collision, no
  two-senders-one-surface counter drift), ONE host-side IntentDedup (idempotence, law 7: a reliable
  double-send must not double-apply a non-idempotent native op like a stat spend), the host dispatch
  (host gate → decode → dedup → table; unknown op / handler throw → uniform reject).
- Reject discipline (law 7 convergence, generalized from EquipSync): `IntentRail.Reject` = log +
  scoped `DiffEngine.ForceReemit` of caller-passed path prefixes + the family's registered
  reconverge (research/manufacture: forced queue-order resend — their queues ride order channels,
  out of ForceReemit's reach). Never log-only. Time registers neither: the client blocked its local
  write and `TimeAnchor.EnforceDrift` is the standing corrector.
- Reject also NUDGES the gesturing client (empty envelope, same surface) → `MarkDirty` in the
  HandleInbound client branch: a byte-equal reconverge (state never changed on the host) would
  otherwise produce no delta, leaving the client's optimistically-staged UI stale — the nudge
  repaints it from the un-mutated model.
- `IntentRail.Send` failure reconverges the same way (`MarkDirty`) instead of log-only: a lost
  block-first gesture repaints from the un-mutated local model rather than showing a phantom apply.

## Walk-time ownership law — DefOwnership (src/Rail/DefOwnership.cs, 2026-07-23)

Full ride/refuse law incl. this one: `docs/boundary-law.md`. The rail writes IN PLACE into the live
graph (the game's own read path builds fresh graphs), so an instance reachable from BOTH a mirrored
entity AND the def graph (`ItemDef.GetDisplayName` returns `ViewElementDef.DisplayName1/2` BY
REFERENCE; `GeoSiteInstaceData.Name`/`.Motto` alias `HavenSettingDbDef` binds) was one classify
change away from clobbering client def state. No static signal carries the law (`IsWritable` and
`IsComplexTypeSerializeable` both falsified; `{ get; } = new X()` is the STRONGEST ownership claim) —
so it is reference identity at walk time:
- ONE lazily-built reference-equality HashSet of every non-def/non-Unity-asset instance reachable
  from `DefRepository.GetAllDefs` (asset + runtime defs, struct fields + collection elements); O(1)
  lookup, zero per-tick allocation; invalidated at reload boundary (a loaded save can mint runtime defs).
- Hooks: DiffEngine refuses at `VisitEntity` entry (DiffEngine.cs:493) AND at the field arm for
  non-Leaf classes (:516 — def-owned containers/descends); `GenericApplier` backstop (:219/:225,
  version skew / per-peer def graphs); `DefOwnership.Warm()` at SendLoadComplete
  (SaveTransferCoordinator.cs:1472) — every peer, once per load boundary, curtain still up, so the
  first build never spikes a walk slice.
- Build-failure latch: a thrown build cools down 30 s (also rate-limits the log); law fails OPEN
  between retries — no rebuild storm. Leaves exempt by construction (ship by value, apply by
  ref-replacement). `IsPresentation` refusal kept (belt + suspenders); RailCheck L11 = static belt
  (no `LocalizedTextBind` rides covered); the runtime law itself is harness-INVISIBLE (needs a live
  `DefRepository`) — documented in the baseline header.

## Time sync — block-first intents + anchor (src/Rail/TimeSync.cs + TimeAnchor.cs, 2026-07-23)

- **Anchor-not-Now law:** `Timing.StartTime`/`StartFixedTime` (clock base) never ride as raw values —
  a raw clock mirror double-counts local accrual. They ride as the dedicated "TA" `TimeAnchor` root;
  `Paused`/`Scale` ride as normal "T" leaves through the native property setters (which fire
  OnPausedEvent/EffectiveScaleChangedEvent → change-driven flush, so anchor + leaves reach every
  peer same-frame).
- **Block-first intent seams (law 4a):** client pause/speed wrote only the LOCAL clock
  (`UIModuleTimeControl.OnPauseTime`/`UpdateSelectedTime` + `GeoscapeView.SetGamePauseState`) and the
  rail diffs HOST state — the fork was never corrected. The seams now BLOCK the native write on the
  client and send `GeoTimeIntent` 0xB0 (pause 0/1 | speed preset index); the host applies through the
  SAME native funnels (`SetGamePauseState` / `SelectTimePreset`).
- **`TimeAnchor.EnforceDrift`** (~1 Hz, TimeSync.ClientTick): retains the host rate in `_clientDto`
  and re-asserts the last APPLIED anchor when the local clock leaves its derivation — the free-run
  backstop for writers the seams do not capture. The enforcement rate uses the host's
  `EffectiveScale` form: parent-aware Paused + `Scale × ParentTime.CumulativeScale` (Timing.cs:65-75).
  Time registers no reject-reconverge: the drift corrector is the standing one.
- Interception's private clock stays native. Per-actor `TimingInstanceData.OwnNow/OwnFixedNow` stay
  opted out ship-side (the churn — see DTO twin resolution above); client actor clocks tick locally.

## Migration #1 — Research (src/Rail/ResearchSync.cs, 2026-07-16; 0xAA retired 2026-07-26)

- **Host→all: the generic rail ONLY (0xAC).** The GeoResearch 0xAA side channel (MsgStart serializer
  blob / MsgQueue order snapshot / MsgComplete id delta; MsgProgress already retired 2026-07-17) is
  DELETED — no sender, no receiver, id tombstoned in SurfaceIds. Coverage that replaced it:
  element state/progress/flags = covered ResearchElement leaves (`_state`, `ResearchProgress`,
  `IsInProgress`, requirement-instance lists — rail-baseline.txt); queue MEMBERSHIP + ORDER = the
  order-vector, which since 2026-07-26 is authoritative for the SET too on ALIAS collections
  (elements owned by a sibling container — queue ⊂ catalog): `RailMeta.SyncMembersByKeys` prunes
  unlisted local elements and adopts missing keys by resolving the LIVE instance from a sibling
  EntityCollection of the same owner (`GenericApplier.ResolveSiblingElement`), then `ReorderByKeys`
  sequences them; structurally-owned elements (facilities) keep membership on the create/destroy
  set-diff and their vector stays order-only (`DiffEngine.IsStructuralElemType` — vector-driven
  removal would delete the client's per-peer-id corridors). Vectors ship for empty/singleton
  sequences too (a 2→1 / 1→0 membership change needs a carrier). `Research.Current` is derived
  (`_researchQueue[0]`, Research.cs:53-61) — mirroring the queue mirrors it.
- **Client presentation (law 4c), fed by rail deltas:** `ResearchSync.PresentFromMirror` (called
  from the UiEventMap research arm) latches viewer-faction transitions — new queue head → raise the
  native `Research.OnResearchStarted` delegate (geoscape log; the exact moment the game itself
  raises it, Research.SetNext :455-473), element entered Completed → native completed modal
  (`GeoscapeView.OnFactionResearchCompleted`) + log line (`GeoscapeLog.Faction_ResearchCompleted`)
  invoked directly on the private handlers. Latch seeds silently on the first fire after a
  reset/reload (EventPopup contract — no backlog spam).
- **Client→host intents (GeoResearchIntent 0xAB):** intent-capture prefixes on
  `Research.AddResearchToQueue/Cancel/PutInFromOfQueue/PutUpInQueue/PutDownInQueue/InsertAtPosition`
  (all UIModuleResearch entry points route there). Client: native call BLOCKED, intent sent. Host:
  validate → run the SAME native method; the rail broadcasts the outcome (IntentRail dispatch
  FlushNow + host-screen MarkDirty). Reject reconverge = scoped `ForceReemit("F#<guid>.Research")`.
  Transport/nonce/dedup/dispatch/reject discipline ride the generic intent engine since 2026-07-23
  (see IntentRail above). Echo loop closed by `SyncApplyScope` (src/Rail/SyncApplyScope.cs):
  every client apply wraps itself in it; the capture seam passes native through inside it.
- **Sim gating (law 4b):** `Research.Update` prefix-skipped on the client (clock not frozen — the
  local hourly tick would double-progress and locally complete research).
- **Reward-chain boundary (law 3):** the rail's leaf apply writes `ResearchElement._state` as a
  FIELD — never the native State setter, whose `Complete()` runs the reward chain
  (ApplyRewards / RewardReputation / Wallet.Give) = host-only logic.
- **Known limitations (accepted, resolved by later subsystems):** reward side-effects (resources,
  reputation, manufacture unlocks) reach the client only via their own subsystems; dependent-research
  reveal/unlock cascades arrive with that research's start blob, not at completion (client pedia/
  stats/GeoscapeEventSystem completion hooks do NOT fire); NPC-faction research is frozen on the
  client (Research.Update gated for ALL factions). RESOLVED 2026-07-17 by the generic rail: research
  reward RESOURCES and start-affordability now correct — the client wallet mirrors the host wallet
  (`Wallet._resources` rides DiffEngine 0xAC, repaint via native ResourcesChanged).

## Net layer hardening (src/Transport + src/SaveTransfer, 2026-07-24)

- **Non-blocking DirectTransport sends:** Send/Broadcast only ENQUEUE into a bounded per-peer queue
  (96 MB — fits a whole save-blob fan-out); ONE writer thread per peer preserves frame order. A
  non-reading peer can no longer freeze the host main thread mid-fan-out: queue overflow or a >30 s
  write stall drops the peer through the existing peer-lost chokepoint. Teardown = flush-then-close
  (1 s grace) so terminal notices (HostDisconnected/ClientLeave/rejects) still reach the peer;
  writers always close their socket on exit. Wire-supplied frame length capped at 64 MB (uncapped
  alloc from port scanners); packet drain dispatches handlers outside the lock. SteamTransport: 1 s
  grace before `CloseP2PSession` after terminal sends — queued terminal notices are not discarded
  with the session.
- **Transfer-stall deadline (client wedge):** host-loss detectors are suspended only while the save
  transfer shows PROGRESS (`SaveTransferCoordinator.LastProgressMs` — bumped on chunks/snapshots/
  flag edges/phase-2 percent); 60 s with no progress re-arms them, so a silently dead host fires the
  normal teardown instead of parking the client at "Waiting for players..." forever.
- **Curtain parked-lift root fix:** `CurtainLiftGatePatch` frame-stamps parked lifts;
  `PerformDeferredLift` skips its direct `LiftCurtain` when a parked native lift resumes (single
  tail — no competing `_currentFadingRoutine.Stop()`). `RepairRevealInputLock` stays as belt; firing
  >2 s after reveal logs LOUD.
- **Membership gates:** chat + rail resync-requests from senders not on the roster are dropped (no
  "Player" injection, no unthrottled full resends).

## Verification

- **Stage 1 = `tools/RailCheck` (BUILT 2026-07-18).** `cd tools/RailCheck && dotnet run -c Debug`
  — exit 0 green, 1 red. Seconds, no game, no save.
  - It runs headless because `Serializer.GetSerializedMembers` (Serializer.cs:296) is pure attribute
    reflection. But `new Serializer(null)` alone is NOT the game's discovery: members are filtered by
    `IsSerializeableType` (Serializer.cs:308), which for a struct resolves through
    `GetTypeSerializeAttribute` → `GetCustomDataForType` (Serializer.cs:160) = REGISTERED custom type
    data. The game registers it in two steps (`SerializationComponent.Initialize:81-83`), so the
    harness makes the same second call — public static `SerializationComponent.InitCustomTypes(ser)`,
    no Unity state. Without it every Vector2/Vector2Int/Vector3/Vector3Int/Bounds-typed member is
    invisible while the live rail classifies it (that hid `ActorInstanceData.Pos`/`.Rot` — the position
    of every site and vehicle — until 2026-07-19). Only VALUE serialization needs the live game.
  - It asserts the rail's own laws over the real `Assembly-CSharp` metadata: **L1** every list-classed
    field has a `RailMeta.ApplyList` strategy (a licensed-but-unapplyable field is the 2026-07-18
    resync storm by construction); **L2** no `[SerializeCustomCreate]` param is unmatched; **L3** no
    Unity object reaches the blob codec; **L4** leaf/list/blob codec round-trip; **L5** if the codec
    starts carrying runtime types, abstract element types' concretions must be classified; **L6** every
    blob-reconstructed element type in the closure survives a real encode→decode (the offline
    SelfCheckEntityList `DiffEngine.cs:420` delegates here — L4 alone only drove a synthetic class);
    **L7** the dict-delete tombstone stays undecodable as a value; **L8** `SurfaceSeq` is monotonic
    per surface, idempotent under redelivery, safe under reordering (law 7); **L9** `GeoItemDict`
    coverage is non-vacuous (it is a re-inclusion — reaching zero silently kills inventory sync);
    **L10** element ORDER survives the wire: an EntityList blob decodes in live order,
    `ReuseLiveElements` maps value-equal elements 1:1 back onto live instances (a reorder moves
    objects, never husks them), `ReorderByKeys` rearranges keyed collections in place by key;
    **L11** no `LocalizedTextBind` field/element rides covered — the STATIC belt for the runtime
    DefOwnership law (the known def-laundering vector; the runtime reference-identity law itself
    needs a live `DefRepository` and is harness-invisible — see the baseline header note);
    **L12** the intent engine's pure halves: `IntentDedup` idempotence, peer+surface keying, bounded
    ring, `ResetPeer` rejoin semantics, plus the [nonce][op] envelope round-trip on all four family
    surfaces (0xAB/0xAE/0xAF/0xB0) and the empty reject-nudge envelope; **L13** field-codec
    CRC(host)==CRC(client) — what the real apply calls wrote re-encodes to the host's exact bytes
    (ordered/unordered leaf lists, EntityList blobs incl. order), hashed with the real `Crc32`.
  - It probes the codec (`ProbePolymorphicCodec`) rather than assuming: declared-type-only vs
    polymorphic decides the type closure, so "the ship side widened" is a detected event.
  - `docs/rail-baseline.txt` is the committed snapshot — full classifier table, per-type blob **husk**
    lists, and today's known violations. **Drift in that file IS the gate**, so a field moving
    Excluded↔covered is a reviewable diff, never a silent side effect. Intended change → re-run with
    `--update` and commit the baseline in the same commit.
- **What stage 1 does NOT cover** (do not read green as "safe"):
  - No simulation, no LIVE-TREE CRC(host)==CRC(client), no seeded command sequences — those need a
    live `GeoLevelController`, so the originally intended SimCluster shape is still unbuilt. L13
    covers only the separable identity underneath: re-encode-after-apply is byte/CRC-identical at
    the field codec.
  - `LeafKind.DefRef` / `EntityRef` round-trip stays UNTESTABLE offline and is deliberately not faked:
    decode is `GameUtl.GameComponent<DefRepository>()` and the values are `BaseDef : ScriptableObject`,
    neither constructible outside the player; the classify side is the one-liner
    `typeof(BaseDef).IsAssignableFrom(t)`, so asserting it would be a tautology. Same for the
    `GeoItem` codec body (needs an `ItemDef`; `CommonItemData.SetOwnerItem` dereferences it at once) —
    L9 checks the dict's REACHABILITY, not its payload, and `GeoItem` is recorded in the baseline as
    `roundtrip=unconstructible`.
  - The diff layer proper and `GenericApplier`'s resolve path are still untested: `DiffEngine.Tick`'s
    snapshot/compare/emit is inline in a method needing a live `GeoLevelController`. Of that layer only
    the separable halves are covered — tombstone (L7) and seq (L8).
  - `IntentDedup` (idempotence, peer/surface keying, bounded ring, rejoin reset) and the intent
    envelope ([nonce][op] on all four family surfaces + the empty reject nudge) are covered by L12.
    Still in-game-only: `IntentRail`'s nonce allocator, host dispatch and reject-reconverge (each
    needs a live `NetworkEngine`), and the family BODY codecs (inline at capture/handler seams).
  - The closure is DECLARED types from the `IdentityResolver.Roots` kinds; runtime subtypes only
    enter it when the codec is polymorphic. Fields the live walk reaches through a subtype are invisible.
  - **It cannot catch a ship-side rule change that makes an already-classified type start riding as a
    blob** (the `7ef0a30` `ResearchElement` husk → NOTEXT). It reports such a change only as baseline
    drift, and only when the change touches the table. The husk lists exist so review catches it.
- Stage 2 in-game 2-instance gate per subsystem.
- Done = stage 1 green + stage 2 passed + legacy counterpart not ported.
- An xunit test project was considered and rejected: it would only `Compile Include` mod sources and
  reference `0Harmony` + `UnityEngine.CoreModule`, never `Assembly-CSharp` — so the game-bound code
  is never actually invoked. RailCheck goes further (loads the game assembly and drives the real
  `Serializer`) and needs no bootstrap at all.
