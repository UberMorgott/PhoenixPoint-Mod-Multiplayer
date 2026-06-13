# SD-AIDR INC-3a — All-Factions Vehicle State Mirror (`0x35 GeoStateDiff`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. TDD, DRY, YAGNI, frequent commits — commit straight to inner `main` (NO branch, NO push); full test suite green before each commit.

**Slice:** INC-3a — all-factions vehicle state mirror (the FIRST scope of INC-3's generic `0x35 GeoStateDiff`).

**Goal:** Make host vehicle motion (pos/rot/range + Travelling/CurrentSite/DestinationSites/HitPoints) for **EVERY faction** mirror on the client, keyed by the stable `(factionGuid, VehicleID)` identity — fixing the live host→client movement bug where the Phoenix-only `GeoBridge.FindVehicleById` (`GeoBridge.cs:40-48`) + faction-less payload mean a host-moved non-Phoenix craft (e.g. a New Jericho Thunderbird) can never resolve on the client.

**Architecture:** Generalize the proven `0x34` clock mirror (`TimeBridge.RecordHostState`→wire→`ApplyTimeState`, `ClientTimeMirror` host-skip+try/catch) from ONE Timing object to N vehicles over a single scope-keyed packet `PacketType.GeoStateDiff = 0x35` (already reserved at `src/Network/MessageLayer/PacketType.cs:50`). Host walks all factions × vehicles each frame, snapshots via native `RecordInstanceData`, diffs vs last-sent, broadcasts only CHANGED records (UNRELIABLE for continuous pos/rot/range, RELIABLE for discrete transitions). Client is a PURE mirror: resolve by `(factionGuid,VehicleID)`, seq-guard stale packets, apply light reflected setters (heavy `ProcessInstanceData` for first mirror + CRC-heal only), all under `EntityReplicationScope.Enter()`. The per-action `StartTravel` host→client command-REPLAY is RETIRED; only the client→host `StartTravel` INPUT relay is kept. See spec `docs/superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md`.

**Tech Stack:** C# (net472), HarmonyLib (`AccessTools` dynamic type/method/field resolution; mod NEVER hard-references game types — injected params typed `object`), xUnit 2.9.2 (`Multipleer.Tests`, pure cores TDD-first like `GeoEntityOpCodecTests`/`TimeStateCodecTests`), existing `NetworkEngine` (reliable `BroadcastToAll` `:185` + `BroadcastUnreliable` `:195`, `RouteMessage`, `PacketType` enum), existing `MessageSerializer`/`CommandCodec`/`GeoEntityOpCodec` `BinaryWriter` layout conventions, existing `GeoBridge` id↔entity resolution, existing `EntityReplicationScope` `[ThreadStatic]` guard, existing `TimeSyncBroadcaster` per-frame tick from `NetworkEngine.Update`, existing `SaveTransferCoordinator.Crc32(byte[])` `:978`.

---

## Two-phase DIAG plan

> The locked movement bug must be PROVEN pre-fix and the fix PROVEN post-fix before temp diagnostics are removed.

### PHASE A — DO NOW, before any INC-3a code (Task 0 + a one-shot nav diag in Task 9)

1. **Fix `GeoBridge.DescribeVehicles` (`GeoBridge.cs:83-109`)** — today it walks ONLY `geoLevel.PhoenixFaction.Vehicles`, so the non-Phoenix Thunderbird never prints and the host/client vehicle-set diff is blind to the real bug. Change it to iterate `geoLevel.Factions` (the public `IList<GeoFaction>` field already read by `FindFactionByGuid` at `GeoBridge.cs:160`) → per faction `GeoFaction.Vehicles`, and prefix each entry with `FactionGuid(faction)` so the printed token is `"factionGuid#id:defname"` (the real `(factionGuid,VehicleID)` identity). Keep fully null-guarded/try-catch (never throws, logging-only). This makes the EXISTING DIAG2 logs in `StartTravelInterceptPatch.Postfix` (`cs:84-91`) + `CommandExecutor.ApplyStartTravel` (`cs:40-46`) immediately show that the host moves a faction-keyed id the client Phoenix-only resolver can never find — confirming the locked bug pre-fix.
2. **Add a one-shot post-apply nav diag in the NEW `ClientGeoStateApplier` (Task 9)** — right AFTER applying a Vehicle record, log once per `(factionGuid,vehicleID)` resolved-vs-not + post-apply `Surface.position`/`Travelling`/`CurrentSite` so we see the mirror actually wrote (vs `NavigateRoutine` fighting it). Tag new lines `"[Multipleer] DIAG3 ..."` to grep distinctly from `DIAG`/`DIAG2`.

### PHASE B — AFTER INC-3a in-game-verified (final Task 13)

- Revert ALL temp DIAG = `git revert --no-commit` of `b753111` (deletes `src/Harmony/DiagDeployLogPatch.cs`; strips `[DIAG]` boundary logs in `HostEntityOpBroadcastPatch.cs`, `ClientEntityOpApplier.cs` `cs:21-26`, `NetworkEngine.cs` `cs:323-334` + `cs:587-589`) and `fbfb3f9` (strips `[DIAG2]` in `CurtainShowPatch.cs`, `StartTravelInterceptPatch.cs` `cs:81-91`, `CommandExecutor.cs` `cs:36-46`, and `DescribeVehicles`/`VehicleDefNameOf` helpers in `GeoBridge.cs`).
- Resolve conflicts where INC-3a code now overlaps the diag hunks (`CommandExecutor.ApplyStartTravel` is RETIRED by then so its DIAG2 hunk vanishes with the method; `StartTravelInterceptPatch.Postfix` likewise).
- Decide whether to keep the all-factions `DescribeVehicles` permanently; delete the DIAG3 nav log. Build 0/0 + full suite green after revert; commit `chore(diag): revert temp replication DIAG instrumentation (INC-3a verified)`.

---

## In-game checkpoint (the GATE)

2-instance co-op verification (per `multipleer-second-instance-setup`: Goldberg-emu second copy + `mklink /J` junctions; deploy the Release DLL to BOTH copies' mod folder). HOST instance + CLIENT instance, shared campaign loaded, client joined and past load (INC-1 makes client geo-sim inert).

**PRECONDITION:** a Phoenix Manticore AND a non-Phoenix (New Jericho Thunderbird) vehicle exist on the geoscape; if no NJ craft is naturally present, spawn/observe one via a host faction-traffic event (or pick any non-Phoenix faction aircraft).

**STEPS:**

1. On HOST, order the Phoenix Manticore to travel to a distant site. EXPECT on CLIENT: the Manticore smoothly moves the same path (per-tick `Surface` pos/rot/range mirrored via `0x35` UNRELIABLE), `Travelling` flips true immediately (reliable discrete), and on arrival `CurrentSite` + `Travelling=false` mirror exactly (reliable discrete) — lands on the SAME site, no backward jitter.
2. On HOST, order/observe the New Jericho Thunderbird to travel. EXPECT on CLIENT: it ALSO moves and arrives, resolved by `(factionGuid,VehicleID)` NOT vehicleID-alone — the core proof the Phoenix-only `FindVehicleById` bug is fixed (pre-fix the NJ craft never resolved/never moved on the client). Confirm via DIAG3 that the client resolved the NJ faction guid by non-empty match, NOT Phoenix fallback.
3. DEPARTURE/ARRIVAL exactness: watch a full depart→fly→land cycle on BOTH craft; client `CurrentSite`/`DestinationSites` match host at every transition.
4. CLIENT→HOST input still works: on CLIENT, order a Phoenix craft to travel; the `StartTravel` Prefix relays input to host (kept path), host simulates, motion mirrors back via `0x35` — proving the retired command-REPLAY did not break the kept input relay.
5. No console errors/exceptions either instance; client craft do not double-integrate (`NavigateRoutine` not fighting the mirror — last-writer-wins per-tick overwrite holds).

**GATE:** all five pass = INC-3a done → proceed to Phase B DIAG revert. Any fail → superpowers:systematic-debugging, do NOT revert DIAG yet.

---

## Build / Test / Deploy

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`
**In-game (2-instance):** per `multipleer-second-instance-setup` (Goldberg-emu second copy + `mklink /J` junctions); see Task 12.

> **Test linking:** `Multipleer.Tests/Multipleer.Tests.csproj` has `EnableDefaultCompileItems=false`; pure cores are linked individually (`EntityReplicationScope` link at `csproj:33`). Each new PURE file under unit test MUST get its own `<Compile Include="..\src\..."><Link>X.cs</Link></Compile>` line.

---

## TDD Tasks

### Task 0 — DIAG-now: all-factions `DescribeVehicles` + faction-keyed token  *(impure; no unit test)*

DO FIRST (no INC-3a code yet) so the existing DIAG2 logs expose the live bug with real `(factionGuid,VehicleID)` data.

- [ ] MODIFY `src/Network/CommandSync/GeoBridge.cs` `DescribeVehicles` (`cs:83-109`): replace the Phoenix-only walk (`cs:87` `PhoenixFaction`) with iteration over the `Factions` field (`AccessTools.Field(geoLevel.GetType(),"Factions")` as `IEnumerable` — same accessor `FindFactionByGuid` uses at `cs:160`), then per faction `GeoFaction.Vehicles`, emit each entry as `FactionGuid(faction)+"#"+VehicleId(v)+":"+VehicleDefNameOf(v)`. Keep every step try/catch, never throw, logging-only, return `(totalCount, joinedList)`. No flow change.
- [ ] NO unit test (reflection-only diag helper). Build 0/0 + full suite green.
- [ ] Commit `chore(diag): all-factions DescribeVehicles with faction-keyed tokens`. Deploy + run the 2-instance repro once to CAPTURE pre-fix evidence (host emits a faction-keyed NJ id the client Phoenix-only resolver misses).

### Task 1 — Pure `GeoStateScope` enum (stable bytes) + test  *(pure → TDD)*

- [ ] FAILING TEST FIRST: create `Multipleer.Tests/GeoStateScopeTests.cs` asserting stable bytes `Vehicle=1`, `Site=2`, `MarketPrice=3`, `FactionTraffic=4`, `FactionState=5`, `Checksum=255` (mirror `GeoEntityOpCodecTests.OpTypeBytes_AreStable` at `cs:48-54`). Run filtered → FAIL.
- [ ] IMPL: create `src/Network/CommandSync/GeoStateScope.cs` — `public enum GeoStateScope : byte { Vehicle=1, Site=2, MarketPrice=3, FactionTraffic=4, FactionState=5, Checksum=255 }` with the STABLE-never-renumber comment discipline of `GeoEntityOpType` (`GeoEntityOpCodec.cs:5-14`). Only `Vehicle` is produced/applied in INC-3a; the rest are reserved INC-3b/c forward-compat (self-delimited records let the reader skip unknown scopes).
- [ ] Link into `Multipleer.Tests.csproj` after the `EntityReplicationScope` link (`csproj:33`). Test PASS.
- [ ] Commit `feat(replication): GeoStateScope enum (stable bytes, INC-3 scopes)`.

### Task 2 — Pure `GeoVehicleStateRecord` + `GeoStateDiff` structs + `changedMask` bit consts + test  *(pure → TDD)*

- [ ] FAILING TEST FIRST in `GeoStateDiffCodecTests.cs`: assert `changedMask` bit consts stable (bit0 SurfacePos=1, bit1 SurfaceRot=2, bit2 RangeRemaining=4, bit3 Travelling=8, bit4 CurrentSite=16, bit5 DestinationSites=32, bit6 HitPoints=64) and a default `GeoVehicleStateRecord` has `ChangedMask==0`. FAIL.
- [ ] IMPL: create `src/Network/CommandSync/GeoStateDiffCodec.cs` (struct portion) — two PURE Unity-free structs in `GeoEntityOpCodec` style: `GeoVehicleStateRecord { string FactionGuid; int VehicleID; ulong Seq; int ChangedMask; float PosX,PosY,PosZ; float RotX,RotY,RotZ,RotW; float RangeRemaining; bool Travelling; int CurrentSiteId; int[] DestinationSiteIds; float HitPoints; }` and `GeoStateDiff { List<GeoVehicleStateRecord> Records; }`. Add static `GeoStateMask` with the 7 bit consts. NO engine refs — primitives only (same contract as `GeoEntityOp` at `cs:20-30`).
- [ ] Link into csproj. Test PASS.
- [ ] Commit `feat(replication): pure GeoStateDiff/GeoVehicleStateRecord structs + changedMask bits`.

### Task 3 — Pure `GeoStateDiffCodec` Encode/Decode (generic scope/seq/mask envelope) + round-trip tests  *(pure → TDD)*

- [ ] FAILING TESTS FIRST in `GeoStateDiffCodecTests.cs` (model on `GeoEntityOpCodecTests.cs`):
  - (a) full-mask Vehicle record round-trips ALL fields incl. ordered `DestinationSiteIds[]` + `Seq`;
  - (b) partial-mask (only bit0 SurfacePos + bit3 Travelling) writes/reads ONLY those, others default;
  - (c) 3-record envelope round-trips in order;
  - (d) empty envelope (count=0) round-trips;
  - (e) null `FactionGuid`/null `DestinationSiteIds` encode `""`/empty no-throw (mirror NullStrings test);
  - (f) `formatVersion` byte present + stable. FAIL.
- [ ] IMPL: add `Encode(GeoStateDiff)`/`Decode(byte[])` using `MemoryStream`+`BinaryWriter` exactly like `GeoEntityOpCodec.cs:34-68`. Envelope: `[byte formatVersion=1][int recordCount]` then per record `[byte scope][ulong Seq][string FactionGuid][int VehicleID][int ChangedMask]` then ONLY mask-set fields in bit order (pos xyz / rot xyzw / range / travelling / currentSiteId / destCount+ids / hitpoints). Self-delimited so an unknown scope can be skipped (INC-3a: only `Vehicle=1` has a body reader — guard others).
- [ ] Tests PASS.
- [ ] Commit `feat(replication): GeoStateDiffCodec generic scope/seq/mask envelope (TDD round-trip)`.

### Task 4 — `GeoBridge.FindVehicleByFactionAndId` — THE BUG FIX (also fixes 0x36 + client→host input)  *(impure; no unit test)*

Early identity task fixing the live bug across BOTH state mirror and existing input/`0x36` paths. NO unit test (`AccessTools` over live types) — build + Task 12 in-game.

- [ ] IMPL in `GeoBridge.cs`: (1) ADD `FindVehicleByFactionAndId(object geoLevel, string factionGuid, int vehicleID)`: `FindFactionByGuid(geoLevel, factionGuid)` (`cs:156`) then scan THAT faction's `GeoFaction.Vehicles` for `VehicleID==vehicleID`; return vehicle or null. CRITICAL: require NON-EMPTY guid match for non-Phoenix — `FindFactionByGuid` falls back to `PhoenixFaction` on empty/unmatched (`cs:159,164`), correct ONLY for Phoenix and WRONG for a generic all-faction diff. Add a strict variant `FindFactionByGuidStrict` (returns null instead of Phoenix fallback when guid non-empty+unmatched) used by the resolver, OR reject a Phoenix-fallback resolution when requested guid is non-empty and `!=` Phoenix guid.
- [ ] (2) Migrate `0x36` `ClientEntityOpApplier` idempotency/remove lookups (`ClientEntityOpApplier.cs:49,80`) to `FindVehicleByFactionAndId(op.OwnerFactionGuid, op.EntityId)` since `0x36` already carries `OwnerFactionGuid` (set at `HostEntityOpBroadcastPatch.cs:133`) — makes non-Phoenix create/remove reconcile too.
- [ ] (3) Keep old Phoenix-only `FindVehicleById` for legacy Phoenix callers.
- [ ] Build 0/0 + full suite green.
- [ ] Commit `fix(replication): (factionGuid,VehicleID) all-factions vehicle resolver (live movement bug fix)`.

### Task 5 — `GeoBridge.RecordVehicleState` (host snapshot via native `RecordInstanceData`)  *(impure; no unit test)*

NO unit test (reflection over live `GeoVehicle`/`GeoVehicleInstanceData`).

- [ ] IMPL in `GeoBridge.cs`: ADD `RecordVehicleState(object vehicle)` → `GeoVehicleStateRecord` — call native `GeoVehicle.RecordInstanceData(new GeoVehicleInstanceData)` (`GeoVehicle.cs:1053`; new the Base type via `AccessTools.TypeByName` `PhoenixPoint.Geoscape.Entities.GeoVehicleInstanceData`) mirroring `TimeBridge.RecordHostState` (`TimeBridge.cs:85-103`). Read off the InstanceData: `SurfacePos` (Vector3, `GeoVehicleInstanceData.cs:17`)→PosX/Y/Z; `SurfaceRot` (Quaternion, `cs:19`)→RotX/Y/Z/W; `RangeRemaining` (float, `cs:23`); `Travelling` (bool, `cs:32`); `CurrentSite`→`CurrentSiteId` via `GeoSite.SiteId` (`cs:28`, `-1` if none); `DestinationSites` (List<GeoSite>, `cs:30`)→ordered `int[]` SiteId; `HitPoints` (float, `cs:26`). Fill `FactionGuid` via `FactionGuid(vehicle.Owner)` + `VehicleID` from the field. `Seq`/`ChangedMask` left to the broadcaster (Task 7).
- [ ] Build 0/0.
- [ ] Commit `feat(replication): GeoBridge.RecordVehicleState (native RecordInstanceData snapshot)`.

### Task 6 — `GeoBridge.ApplyVehicleState` (light reflected setters) + `ApplyVehicleStateFull` (native `ProcessInstanceData`)  *(impure; no unit test)*

NO unit test (reflection over live types).

- [ ] IMPL in `GeoBridge.cs`: (1) `ApplyVehicleState(object vehicle, GeoVehicleStateRecord r)` LIGHT path: for each set bit in `r.ChangedMask` write directly: `Surface.position` (`GeoVehicle.Surface`, `cs:303,89` / Apply `:1089`), `Surface.rotation` (`:1090`), `RangeRemaining` via new `EarthUnits(value)` (`:1093`; build `Base.Utils.EarthUnits` via reflection), `Travelling` setter (`:1097`), `CurrentSite` (`:1094`), `_destinationSites` (`:1095-1096`; resolve each int `SiteId`→`GeoSite` via `FindSiteById`, skip the DestinationSites bit if any id unresolved = site not yet synced), `HitPoints`. ORDERING (matches `ProcessInstanceData` net result + the `Travelling` side-effect at `GeoVehicle.cs:212-216` where `Travelling=true` clears `CurrentSite` via `VehicleLeft`): set `Travelling` FIRST, then `CurrentSite`, then the rest.
- [ ] (2) `ApplyVehicleStateFull(object vehicle, GeoVehicleStateRecord r)` HEAVY path: build `GeoVehicleInstanceData` (`AccessTools.CreateInstance`), set all 7 fields, call native `GeoVehicle.ProcessInstanceData` (`cs:1082`) — literal mirror of `TimeBridge.ApplyTimeState` (`TimeBridge.cs:107-122`). Used for the FIRST mirror of a vehicle + CRC-heal only (re-clones Stats `cs:1092` — too heavy per tick).
- [ ] Build 0/0.
- [ ] Commit `feat(replication): GeoBridge ApplyVehicleState light setters + ApplyVehicleStateFull (ProcessInstanceData)`.

### Task 7 — Pure host diff+seq core: `GeoVehicleStateDiffer` + tests  *(pure → TDD)*

The diff/seq logic is PURE and unit-testable (engine snapshotting stays in `GeoBridge` from Task 5).

- [ ] FAILING TESTS FIRST in `GeoVehicleStateDifferTests.cs`:
  - (a) first record for an identity → full `changedMask`, `seq=1`;
  - (b) unchanged re-submit → `mask==0` (emit nothing);
  - (c) pos moved beyond epsilon → bit0 set + seq increments; within epsilon → bit0 NOT set;
  - (d) Travelling flip / CurrentSite change / DestinationSites change → respective discrete bits with NO epsilon (exact);
  - (e) per-`(factionGuid,VehicleID)` seq independent + monotonic;
  - (f) classify CONTINUOUS bits (pos/rot/range = 0,1,2) vs DISCRETE bits (travelling/currentsite/dest/hp = 3,4,5,6) into two output masks for channel split. FAIL.
- [ ] IMPL: create `src/Network/CommandSync/GeoVehicleStateDiffer.cs` — stateful pure class holding `Dictionary<(string,int),GeoVehicleStateRecord> lastSent` + `Dictionary<(string,int),ulong> seqByIdentity`; `Diff(GeoVehicleStateRecord current)`→record with `ChangedMask` (epsilon on pos/rot/range, exact on discretes) + assigned `Seq`, plus a `ContinuousMask`/`DiscreteMask` split helper. Float epsilon const (~0.01, tune).
- [ ] Link into csproj. Tests PASS.
- [ ] Commit `feat(replication): pure GeoVehicleStateDiffer (epsilon diff + monotonic per-identity seq + channel split)`.

### Task 8 — `NetworkEngine.BroadcastGeoStateDiff` (reliable + unreliable) + `RouteMessage` case `0x35`  *(impure; no unit test)*

NO unit test (live transport wiring; mirrors `BroadcastGeoEntityOp`/`BroadcastTimingState` which have none).

- [ ] (1) `PacketType.cs:50` — replace the reserved comment with `GeoStateDiff = 0x35,` (slot already reserved).
- [ ] (2) `NetworkEngine.cs` after `BroadcastGeoEntityOp` (`cs:336`): add `BroadcastGeoStateDiff(GeoStateDiff diff, bool reliable)` — body=`GeoStateDiffCodec.Encode(diff)`, wrap `NetworkMessage(PacketType.GeoStateDiff, body)`, call `BroadcastToAll` (reliable, `cs:185`) OR `BroadcastUnreliable` (`cs:195`). Batch many records into ONE envelope per call.
- [ ] (3) `RouteMessage` (GeoEntityOp case at `cs:587` is the template): add `case PacketType.GeoStateDiff:` → `GeoStateDiffCodec.Decode(msg.Payload)` → `ClientGeoStateApplier.Apply(diff)`. `ClientGeoStateApplier` lands Task 9 — sequence Task 9 before full build (or temp no-op stub like INC-2 Task 3 did).
- [ ] Build 0/0 (after Task 9).
- [ ] Commit `feat(replication): wire 0x35 GeoStateDiff packet + BroadcastGeoStateDiff(reliable/unreliable) + route`.

### Task 9 — `ClientGeoStateApplier` (client-only, scope, seq-guard, all-faction resolve, light/heavy apply) + DIAG3 nav log  *(impure; no unit test)*

NO unit test (engine reflection; model on `ClientEntityOpApplier.cs:16-93` + `ClientTimeMirror.cs:9-19`).

- [ ] IMPL: create `src/Network/CommandSync/ClientGeoStateApplier.cs`. GATE client-only: `engine != null && engine.IsActive && !engine.IsHost` (`cs:27`) — host owns truth, ignores `0x35`. try/catch around the whole apply (`ClientTimeMirror` pattern). `GetGeoLevelController` null-guard. `using (EntityReplicationScope.Enter())` (`EntityReplicationScope.cs:15`) around the record loop so any host re-broadcast postfix the apply trips sees `IsApplying` (`HostEntityOpBroadcast.ShouldBroadcast cs:61`) and does NOT re-emit.
- [ ] Per record switch on scope; `Vehicle=1`:
  - (a) SEQ GUARD: per-`(factionGuid,VehicleID)` `lastAppliedSeq` dict; if `record.Seq <= lastApplied` DROP (stale UNRELIABLE pos packet, newest-wins).
  - (b) RESOLVE via `FindVehicleByFactionAndId` (Task 4) — non-empty guid for non-Phoenix; if vehicle absent → `0x36 VehicleCreated` not applied yet → skip gracefully (next periodic push self-heals).
  - (c) APPLY: if this identity NEVER mirrored before (first record) OR a flagged full correction → `ApplyVehicleStateFull` (heavy); else `ApplyVehicleState` (light setters). Client NEVER calls `StartTravel` on a mirrored vehicle (travel = `Travelling=true` + `DestinationSites` + per-tick pos/rot/range).
- [ ] DIAG3: one-shot-per-identity post-apply log resolved-or-not + post `Surface.position`/`Travelling`/`CurrentSite`.
- [ ] Other scopes (Site/MarketPrice/Faction/Checksum) → log+skip (forward-compat).
- [ ] Build 0/0.
- [ ] Commit `feat(replication): ClientGeoStateApplier (seq-guarded all-faction mirror under EntityReplicationScope)`.

### Task 10 — `GeoStateSyncBroadcaster` (host all-faction snapshot+diff; unreliable continuous + reliable discrete; ticked from `NetworkEngine.Update`)  *(impure; no unit test)*

NO unit test (host-only frame tick over live geoscape; mirrors `TimeSyncBroadcaster` which has none — the pure diff/seq core is tested in Task 7).

- [ ] IMPL: create `src/Network/CommandSync/GeoStateSyncBroadcaster.cs` cloning `TimeSyncBroadcaster.cs:8-32`. Static `Tick(NetworkEngine engine, float deltaTime)`: host-only gate `engine.IsActive && engine.IsHost` (`cs:16`); defensive skip if `EntityReplicationScope.IsApplying || CommandRelay.IsApplying`. Hold one static `GeoVehicleStateDiffer`.
- [ ] Per tick: ENUMERATE all factions × vehicles via `geoLevel.Factions` field → `GeoFaction.Vehicles` (the generalized walk from Task 0; or iterate flat `GeoMap.Vehicles` once + group by `Owner` to avoid the per-faction LINQ wrapper — open question, pick cheaper). SNAPSHOT each via `GeoBridge.RecordVehicleState` (Task 5). DIFF each via `GeoVehicleStateDiffer` (Task 7) → per-vehicle `ChangedMask` + `Seq`; emit nothing for `mask==0`.
- [ ] SPLIT into two envelopes: (1) CONTINUOUS bits (pos/rot/range) batched into a `GeoStateDiff` on a throttled accumulator (~0.5s start like `TimeSyncBroadcaster.cs:10`, tune to 0.2–0.3 in-game) via `BroadcastGeoStateDiff(reliable:false)`; (2) DISCRETE bits (Travelling flip / CurrentSite / DestinationSites / HitPoints) sent IMMEDIATELY this frame via `BroadcastGeoStateDiff(reliable:true)` — arrival/departure must be exact. Cap records per envelope to stay under MTU on the unreliable channel (open question — split across frames if many).
- [ ] WIRE: `NetworkEngine.Update` (`cs:340-346`) already calls `TimeSyncBroadcaster.Tick` — add `GeoStateSyncBroadcaster.Tick(this, Time.deltaTime)` right after it.
- [ ] Build 0/0.
- [ ] Commit `feat(replication): GeoStateSyncBroadcaster (all-faction host snapshot+diff, unreliable continuous + reliable discrete)`.

### Task 11 — Retire `StartTravel` command-REPLAY; KEEP client→host `StartTravel` INPUT relay  *(impure; no new unit test)*

NO new unit test (behavior-removal over live patch + executor; verified by Task 12 in-game). Precise kept/retired split (audited from live files):

- [ ] RETIRE (a) `StartTravelInterceptPatch.Postfix` (`StartTravelInterceptPatch.cs:61-94`) — host-origin command-result broadcast is superseded by the `0x35` mirror (host motion mirrors as Travelling+DestinationSites+pos/rot/range, no command replay); delete the whole Postfix.
- [ ] RETIRE (b) `CommandExecutor.ApplyStartTravel` (`CommandExecutor.cs:29-63`) AND its switch branch (`cs:17-19`) — client no longer REPLAYS a StartTravel command; the mirror drives the vehicle.
- [ ] KEEP (c) `StartTravelInterceptPatch.Prefix` (`cs:34-57`) — the client→host StartTravel INPUT relay; extend its payload with `ownerFactionGuid` (from Task 4) so the host resolves a client-originated vehicle by `(faction,id)`, not Phoenix-only.
- [ ] KEEP (d) Leave `SetTimeState` UNTOUCHED — it rides the SAME `CommandExecutor` path (`cs:20-22` + `ApplySetTime cs:69-73`); audit confirms ONLY the StartTravel branch is removed. Audit any other `CampaignActionType` on that path before deleting (open question) — check the `CommandExecutor` switch + `HostArbiter` for StartTravel-only assumptions.
- [ ] Build 0/0 + full suite green (`CommandCodecTests` StartTravel cases still cover the kept Prefix encode — confirm pass).
- [ ] Commit `refactor(replication): retire StartTravel command-replay (state mirror supersedes), keep client->host input relay`.

### Task 12 — IN-GAME 2-instance acceptance checkpoint (USER runs this)  *(impure; integration)*

Deploy the Release DLL to BOTH copies (`multipleer-second-instance-setup`).

- [ ] Run the full checkpoint above: host moves a Phoenix Manticore AND a non-Phoenix New Jericho Thunderbird → BOTH mirror on the client resolved by `(factionGuid,VehicleID)`; departure/arrival mirror exactly; client→host StartTravel input still works; no double-integration; no console errors. Use DIAG3 to confirm the NJ faction resolved by non-empty guid (not Phoenix fallback). This is the GATE proving the locked movement bug is fixed.
- [ ] Record the outcome (PASS/FAIL per step) to `docs/research/00-current-state.md` via SCRIBE.
- [ ] PASS → Task 13. FAIL → superpowers:systematic-debugging, do NOT revert DIAG.

### Task 13 — Phase-B: revert all temp DIAG instrumentation (`b753111` + `fbfb3f9`) — ONLY after Task 12 passes  *(impure)*

ONLY after INC-3a in-game-verified (Task 12 PASS).

- [ ] `git revert --no-commit b753111 fbfb3f9` (or revert each oldest-first). `b753111` deletes `src/Harmony/DiagDeployLogPatch.cs` and strips `[DIAG]` boundary logs in `HostEntityOpBroadcastPatch.cs`, `ClientEntityOpApplier.cs` (`cs:21-26`), `NetworkEngine.cs` (`cs:323-334` + `cs:587-589`). `fbfb3f9` strips `[DIAG2]` in `CurtainShowPatch.cs`, `StartTravelInterceptPatch.cs` (`cs:81-91` — partly gone with the retired Postfix in Task 11, resolve conflict), `CommandExecutor.cs` (`cs:36-46` — gone entirely with the retired ApplyStartTravel in Task 11), and `DescribeVehicles`/`VehicleDefNameOf` helpers in `GeoBridge.cs`.
- [ ] DECIDE: keep the all-factions `DescribeVehicles` (Task 0) only if a permanent diag is wanted, else let the revert remove it; delete the DIAG3 one-shot nav log in `ClientGeoStateApplier`. Resolve all conflicts where INC-3a code overlaps the diag hunks.
- [ ] Build 0/0 + FULL suite green after revert.
- [ ] Commit `chore(diag): revert temp replication DIAG instrumentation (INC-3a verified)`. Optional: re-run the 2-instance smoke once post-revert to confirm no diag-dependent behavior leaked.

---

## Out of scope (later INC-3 slices — do NOT build here)

- **INC-3b** — `GeoStateScope.Site` (full `GeoSiteInstaceData` blob; retires the deferred `0x36 SiteCreated`) + `GeoStateScope.MarketPrice` on the SAME `0x35` machinery (broadcaster/applier gain scope branches, envelope unchanged).
- **INC-3c** — `GeoStateScope.FactionTraffic`/`FactionState` (identity = faction `Def.Guid`, no-arg `GeoFaction.RecordInstanceData()` → `GeoFactionInstanceData`); host-authoritative faction traffic / alien-base expansion / base-attacks / random events / wallet+diplomacy.
- **INC-5** — rolling CRC32 whole-geoscape divergence + two-barrier reload backstop. INC-3a ships only the per-vehicle Checksum DETECTOR + targeted single-entity full re-push self-heal (spec §6); a whole-geoscape divergence escalates to INC-5.
