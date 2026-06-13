# SD-AIDR INC-3 — Generic Host→Client Geoscape State Mirror (`0x35 GeoStateDiff`)

> Design spec for SD-AIDR **Increment 3**. Generalizes the proven `0x34` clock mirror
> (`TimeBridge.RecordHostState`→wire→`ApplyTimeState`, `ClientTimeMirror`) from ONE Timing object
> to N geoscape entities over a single scope-keyed packet.

- **Status:** design spec (decompile-grounded; readiness YELLOW — residual unknowns need 2-instance in-game checkpoints).
- **Parent arc:** SD-AIDR (`docs/superpowers/specs/2026-06-13-coop-state-replication-design.md`).
- **Builds on:** INC-1 (client geoscape inert — closed 13-producer set returns `NextUpdate.Never` via `ClientGeoSimSuppressPatch`) + INC-2 (`0x36 GeoEntityOp` entity create/destroy).
- **Companion plan:** `docs/superpowers/plans/2026-06-13-replication-increment3a-vehicle-state-mirror.md` (INC-3a, the first slice).
- **Packet slot:** `PacketType.GeoStateDiff = 0x35` (already RESERVED at `Multipleer/src/Network/MessageLayer/PacketType.cs:50`).

---

## 1. Overview

- INC-3 = a **GENERIC** host→client geoscape state mirror over a single new packet `PacketType.GeoStateDiff = 0x35`.
- The **host is the sole simulator** (INC-1 already made the client geoscape engine inert: the closed 13-producer set returns `NextUpdate.Never` via `ClientGeoSimSuppressPatch`).
- Each frame the host walks **ALL factions × ALL vehicles**, calls the native `RecordInstanceData` to snapshot durable fields, diffs against the last-sent snapshot, and broadcasts only **CHANGED** records.
- The client is a **PURE mirror**: `ClientGeoStateApplier` resolves each record by stable identity and writes the host's values via direct setters (high-frequency) or native `ProcessInstanceData` (initial/large correction), all inside `using (EntityReplicationScope.Enter())` so host re-broadcast postfixes treat it as a replay and do not echo.
- **Identity is the stable `(factionGuid, VehicleID)` key** — this directly fixes the live movement bug (Phoenix-only `FindVehicleById` + faction-less payload).
- The `0x35` envelope is **scope-keyed**: vehicles are `scope 1` = the FIRST scope through a generic record mechanism, NOT a vehicle-special-cased packet. INC-3b/c add Site/Price/Faction scopes to the same codec/applier/broadcaster with **no envelope change**.
- This is the exact proven pattern of the `0x34` clock mirror (`TimeBridge.RecordHostState` native `RecordInstanceData` → wire → `ApplyTimeState` native `ProcessInstanceData`, `ClientTimeMirror` host-skip + try/catch) **generalized from one Timing object to N geoscape entities**.
- **Cadence is change-driven:** coarse-throttled UNRELIABLE pushes for continuous pos/rot/range; immediate RELIABLE pushes for discrete transitions (`Travelling` flip, `CurrentSite`, `DestinationSites`).
- The per-action `StartTravel` host→client command-REPLAY is **RETIRED** (replaced by the state mirror); only the client→host `StartTravel` INPUT relay is kept.

---

## 2. `0x35 GeoStateDiff` payload (generic, scope-keyed)

GENERIC envelope (clone the `GeoEntityOpCodec` `MemoryStream`/`BinaryWriter` style at `Multipleer/src/Network/CommandSync/GeoEntityOpCodec.cs:32-69`; add a new `GeoStateDiffCodec`):

```
[byte formatVersion][int recordCount]    # then recordCount records
```

Each record is **scope-keyed and self-delimited** so the reader can skip unknown scopes:

```
[byte scope][ulong seq][...identity bytes by scope...][int changedMask][...changed-field values in mask order...]
```

### 2.1 `enum GeoStateScope : byte` (STABLE values — never renumber; same discipline as `GeoEntityOpType`)

| Scope | Value | Increment |
|---|---|---|
| Vehicle | 1 | INC-3a |
| Site | 2 | INC-3b |
| MarketPrice | 3 | INC-3b |
| FactionTraffic | 4 | INC-3c |
| FactionState | 5 | INC-3c |
| Checksum | 255 | CRC backstop record |

### 2.2 `seq` (per-(scope,identity) monotonic)

- `seq` = host-monotonic per-`(scope,identity)` sequence; client drops any record with `seq <= last applied` for that identity.
- → a dropped/reordered UNRELIABLE pos packet is harmless (newest wins) — same rationale as `RosterProgress` monotonic-max on `BroadcastUnreliable`.

### 2.3 Identity — scope `Vehicle`

- `[string ownerFactionGuid][int vehicleID]` = `GeoFaction.Def.Guid` + `GeoVehicle.VehicleID`.
- **NOT vehicleID alone** (per-faction counter → collides).

### 2.4 `changedMask` bits — scope `Vehicle` (only set bits write a value; matches native `GeoVehicleInstanceData` fields)

| Bit | Field | Wire | Record / Apply (`GeoVehicle.cs`) |
|---|---|---|---|
| bit0 | SurfacePos | `[float x,y,z]` (`GeoVehicleInstanceData.SurfacePos`) | Record `:1062` / Apply `:1089` |
| bit1 | SurfaceRot | `[float x,y,z,w]` | `:1063` / `:1090` |
| bit2 | RangeRemaining | `[float]` (new `EarthUnits`) | `:1065` / `:1093` |
| bit3 | Travelling | `[bool]` | `:1068` / `:1097` |
| bit4 | CurrentSite | `[int siteId, -1=none]` | `:1066` / `:1094` |
| bit5 | DestinationSites | `[int count][int siteId...]` ordered | `:1067` / `:1095-1096` |
| bit6 | HitPoints | `[float]` (`GeoVehicleInstanceData.cs:26`) | — |

- **Speed/MaximumRange are NEVER sent** (deterministic from `VehicleDef.BaseStats`, re-cloned on apply `GeoVehicle.cs:1092`).

### 2.5 Optional scope `Checksum` record

```
[byte targetScope][string identity][uint crc32]
```

- CRC over the **bespoke binary image** of the same recorded snapshot (NOT the native `Serializer.Write` coroutine).

### 2.6 Value contract

- All values are **Unity-free primitives**; engine types resolve back to live entities only at apply time (same contract as `GeoEntityOp`).
- Two new payload structs (pure, no engine refs): `GeoStateDiff` (envelope = `List<GeoStateRecord>`) and `GeoVehicleStateRecord` (`factionGuid`, `vehicleID`, `seq`, `changedMask`, the 7 nullable/masked fields).

---

## 3. Host side — `GeoStateSyncBroadcaster`

New `GeoStateSyncBroadcaster` (clone `TimeSyncBroadcaster`: ticked once/frame from `NetworkEngine.Update()`, host-only gate `engine.IsActive && engine.IsHost`). Per tick:

- **ENUMERATE all factions × vehicles** authoritatively: `GeoLevelController.Factions` (public field; `GeoBridge.FindFactionByGuid` already reads it) → `GeoFaction.Vehicles` (`GeoFaction.cs:137` → `GeoMap.VehiclesByOwner`). Generalize `GeoBridge.DescribeVehicles`' Phoenix-only walk to the `Factions` field (or iterate flat `GeoMap.Vehicles` once and group by `Owner` to avoid the per-faction LINQ wrapper).
- **SNAPSHOT** each vehicle via NEW `GeoBridge.RecordVehicleState`: call native `GeoVehicle.RecordInstanceData(new GeoVehicleInstanceData)` (`GeoVehicle.cs:1053`) and read `SurfacePos`/`SurfaceRot`/`RangeRemaining`/`Travelling`/`CurrentSite.SiteId`/`DestinationSites[SiteId]`/`HitPoints` — exactly mirroring `TimeBridge.RecordHostState` (`TimeBridge.cs:85-103` native `Timing.RecordInstanceData`).
- **DIFF** against a host-held last-sent snapshot dict keyed by `(factionGuid,vehicleID)`; set `changedMask` bits only for fields that moved (with an epsilon on pos/rot/range to avoid float churn). Emit nothing if `mask==0`.
- **CADENCE + reliability (change-driven, two channels):**
  - **CONTINUOUS** fields (pos/rot/range while Travelling) → throttled ~0.2–0.5s accumulator (start at `TimeSyncBroadcaster`'s 0.5s, tune in-game), sent via `engine.BroadcastUnreliable` (`NetworkEngine.cs:195`) with incrementing `seq`. Loss-tolerant.
  - **DISCRETE** transitions (`Travelling` flips, `CurrentSite` change, `DestinationSites` set, `HitPoints`) → immediate `BroadcastNow()` this frame via reliable `engine.BroadcastToAll` (`NetworkEngine.cs:185`). Cannot be lost (arrival/departure must be exact).
- New `NetworkEngine.BroadcastGeoStateDiff(GeoStateDiff)` (mirror `BroadcastGeoEntityOp` `NetworkEngine.cs:319` / `BroadcastTimingState` `:306`): body = `GeoStateDiffCodec.Encode`, wrap in `NetworkMessage(PacketType.GeoStateDiff, body)`, call `BroadcastToAll` or `BroadcastUnreliable` per channel. Batch many vehicle records into ONE `GeoStateDiff` envelope per tick.
- **AUTHORITY:** host reads ground truth only; never ingests client sim. The host still runs `NavigateRoutine` etc. normally; the client does not. Host broadcast is gated by `HostEntityOpBroadcast.ShouldBroadcast`-style logic (skip when `EntityReplicationScope.IsApplying` / `CommandRelay.IsApplying` — defensive on host).

---

## 4. Client side — `ClientGeoStateApplier`

New `ClientGeoStateApplier` (model on `ClientEntityOpApplier.cs:16-93` + `ClientTimeMirror.cs:9-19`):

- **GATE client-only:** `engine != null && engine.IsActive && !engine.IsHost` (host owns truth, ignores `0x35`). try/catch around the whole apply (`ClientTimeMirror` pattern).
- `GetGeoLevelController` null-guard; then `using (EntityReplicationScope.Enter())` (`EntityReplicationScope.cs:15`) around the record loop so any host re-broadcast postfix the apply trips checks `IsApplying` (`HostEntityOpBroadcast.ShouldBroadcast` line 61) and does **NOT** re-emit.
- For each record, switch on scope; **Vehicle:** resolve faction via `GeoBridge.FindFactionByGuid` (require NON-EMPTY guid match for non-Phoenix — do NOT fall back to Phoenix for alien/diplomatic factions), then resolve vehicle via a NEW generalized `GeoBridge.FindVehicleByFactionAndId(faction, vehicleID)` (scans the RESOLVED faction's `Vehicles`, replacing the Phoenix-only `FindVehicleById`). If vehicle absent → the `0x36 VehicleCreated` hasn't applied yet: skip/queue gracefully and drop (next periodic push self-heals). **seq guard:** ignore record if `seq <= last applied` for that identity.
- **APPLY changed fields — LIGHT path** (new `GeoBridge.ApplyVehicleState`): direct reflected setters `Surface.position` / `Surface.rotation` / `RangeRemaining` (new `EarthUnits`) / `Travelling` / `CurrentSite` / `_destinationSites`. Respect native ordering for the `Travelling` side-effect (`Travelling` setter clears `CurrentSite` via `VehicleLeft`, `GeoVehicle.cs:212-216`) → set `Travelling` FIRST, then `CurrentSite`, mirroring `ProcessInstanceData`'s `:1097`-vs-`:1094` net result. Avoid full `ProcessInstanceData` per tick (it re-clones Stats + re-adds equipment, `GeoVehicle.cs:1092`/`:1101-1123` — too heavy).
- **HEAVY path** (new `GeoBridge.ApplyVehicleStateFull`): build a `GeoVehicleInstanceData` and call native `GeoVehicle.ProcessInstanceData` (`GeoVehicle.cs:1082`) for the FIRST mirror of a vehicle and for CRC-mismatch corrections only. This is the literal mirror of `TimeBridge.ApplyTimeState` (`TimeBridge.cs:107-122` native `ProcessInstanceData`, fires no events, starts no `NavigateRoutine`).
- The client **NEVER calls `StartTravel`** on a mirrored vehicle: travel is represented purely as `Travelling=true` + `DestinationSites` + per-tick pos/rot/range pushes. `NavigateRoutine` is not started by the mirror; if anything started it, the next `0x35` push overwrites its output (mirror wins).
- `RouteMessage` gets `case PacketType.GeoStateDiff:` → `GeoStateDiffCodec.Decode` → `ClientGeoStateApplier.Apply` (mirror the `0x36` case at `NetworkEngine.cs:587-592` and the `0x34` case `:577-585`).

---

## 5. Identity — the live bug fix

Stable cross-instance key = `(ownerFactionGuid = GeoFaction.Def.Guid, VehicleID = GeoVehicle.VehicleID)`.

- `VehicleID` is a PER-FACTION monotonic counter (`GeoFaction._lastVehicleIndex`, `++` at every spawn site) so it is **NOT globally unique** → the key MUST include the owner faction guid.
- **THIS IS THE LIVE BUG FIX:** today `GeoBridge.FindVehicleById` (`GeoBridge.cs:40-48`) scans ONLY `geoLevel.PhoenixFaction.Vehicles` and the `StartTravel`/`GeoEntityOp` payloads carry no owner, so a host-moved New Jericho Thunderbird (or any non-Phoenix craft) can never resolve on the client.

INC-3 replaces the Phoenix-only resolver:

- Every `0x35` vehicle record carries `ownerFactionGuid` (host fills it from `GeoBridge.FactionGuid(faction)` = `Def.Guid`, `GeoBridge.cs:168-173`).
- New `GeoBridge.FindVehicleByFactionAndId(geoLevel, factionGuid, vehicleID)`: `FindFactionByGuid(geoLevel, factionGuid)` (`GeoBridge.cs:156`, scans the `Factions` field; native equiv `GeoLevelController.GetFaction(PPFactionDef)`) then scan THAT faction's `Vehicles` for `VehicleID`. Require a non-empty guid match for non-Phoenix (the existing Phoenix fallback at `GeoBridge.cs:159,164` is correct ONLY for Phoenix aircraft and WRONG for a generic all-faction diff).
- **Keep/retire:** the old Phoenix-only `FindVehicleById` remains usable for the Phoenix path but the `0x35` + `0x36` appliers move to the generalized `(faction,id)` resolver so all factions reconcile. The existing `GeoBridge.ReconcileVehicleId` (`GeoBridge.cs:220`) already keeps `VehicleID` + `_lastVehicleIndex` collision-free at `0x36` birth, so the key stays valid on the client.
- **Reconcile:** when a `0x35` record references a vehicle the client lacks, it is the create-before-state ordering case → defer to the reliable `0x36 VehicleCreated` (which carries `OwnerFactionGuid` already, `HostEntityOpBroadcast` line 133), then the next `0x35` push applies state.

---

## 6. CRC backstop (detector + targeted self-heal)

INC-3 ships the **DETECTOR + targeted self-heal**; the hard save-reload is INC-5 (as `ClientGeoSimSuppressPatch.cs:15` promises *"self-healed by host diff INC-3 + CRC reload INC-5"*).

- **CRC SOURCE:** do NOT use the native `Base.Serialization` `Serializer.Write` — it is an `IEnumerator<NextUpdate>` coroutine (`Serializer.cs:562`) needing Timing pumping and is exposed to `GeoVehicleInstanceData` `SerializeType Version=3` skew. Instead CRC the **BESPOKE deterministic binary image** of the recorded snapshot (the same bytes `GeoStateDiffCodec` writes for that entity) using the existing mod helper `Crc32(byte[])` at `Multipleer/src/Network/SaveTransferCoordinator.cs:978`.
- **WIRE:** host periodically (low cadence, e.g. every few seconds or batched with discrete transitions) emits scope=`Checksum` records: per-vehicle `crc32` over its canonical recorded image. Reliable channel.
- **CLIENT CHECK:** after applying, client recomputes the same CRC over its local recorded snapshot of that entity (call native `RecordInstanceData` on the client vehicle, encode with the same codec, `Crc32`). Allow an in-flight tolerance window (skip if the entity has an unreliable seq newer than the checksum's basis, to avoid false positives from a pos packet in flight).
- **ON MISMATCH (INC-3 action):** log the divergence + request/apply a FULL-FIELD correction for that entity via the HEAVY seam (`ApplyVehicleStateFull` / native `ProcessInstanceData`) on the next host push (host can force `changedMask=ALL` for a flagged id). This targeted re-push is the INC-3 backstop and is enough for single-entity drift. A whole-geoscape divergence (many mismatches) escalates to the INC-5 full save resync (out of INC-3 scope) — INC-3 only flags + counts it.

---

## 7. Composition — slices

### INC-3a — ALL-FACTIONS VEHICLE STATE MIRROR (unblocks the live movement bug)

- Add `PacketType.GeoStateDiff = 0x35` enum member at the reserved slot; new `GeoStateScope` enum (`Vehicle=1`…`Checksum=255`); `GeoStateDiffCodec` (generic scope/seq/mask envelope, `GeoEntityOpCodec` style); `GeoBridge` additions `RecordVehicleState` + `ApplyVehicleState`(light setters) + `ApplyVehicleStateFull`(`ProcessInstanceData`) + generalized `FindVehicleByFactionAndId`; `GeoStateSyncBroadcaster` (host all-faction snapshot+diff+seq, unreliable pos/rot/range + reliable discrete transitions, ticked from `NetworkEngine.Update` like `TimeSyncBroadcaster`); `NetworkEngine.BroadcastGeoStateDiff`; `RouteMessage` case `0x35` → `ClientGeoStateApplier.Apply` under `EntityReplicationScope`.
- RETIRE `StartTravelInterceptPatch.Postfix` + `CommandExecutor.ApplyStartTravel`; KEEP the client→host `StartTravel` relay `Prefix`.
- **IN-GAME VERIFY:** host moves a Phoenix Manticore AND a New Jericho Thunderbird → both move on the client resolved by `(factionGuid,VehicleID)`; departure/arrival mirror exactly. **This is the gate that fixes the locked bug.**

### INC-3b — SITES + MARKETPLACE PRICES on the SAME `0x35` machinery

- Add `GeoStateScope.Site` (identity=`SiteId`; full `GeoSiteInstaceData` blob via native `GeoSite.RecordInstanceData` `GeoSite.cs:1488` / `ProcessInstanceData` `:1540` — this **retires the deferred `0x36 SiteCreated`**, `GeoEntityOpCodec.cs:12`) and `GeoStateScope.MarketPrice`. New `GeoBridge.RecordSiteState`/`ApplySiteState`; broadcaster + applier just gain scope branches, **envelope unchanged**.
- **IN-GAME VERIFY:** host-side site scan/exploration state and marketplace price shifts reflect on the client; a host-created site appears on the client.

### INC-3c — FACTION-LEVEL STATE (traffic, base-attacks, random events, arrival authority)

- Add `GeoStateScope.FactionTraffic`/`FactionState`; `GeoFaction` is NOT an actor so identity = faction `Def.Guid` and snapshot uses the no-arg `GeoFaction.RecordInstanceData()` (`GeoFaction.cs:354`) → `GeoFactionInstanceData`, applied via targeted setters (Wallet/Diplomacy/Objectives) or `LevelStartLoadedGame(GeoFactionInstanceData)` (`GeoFaction.cs:586`). Faction traffic / alien-base expansion / base-attacks / random events become host-authoritative `0x35` state pushes instead of client-run events (the rest of the suppressed INC-1 producer effects are now mirrored).
- **IN-GAME VERIFY:** alien base expansion, faction traffic, and wallet/diplomacy mirror on the client.

---

## 8. Open questions (require in-game 2-instance verification)

- **In-flight banking:** durable snapshot is `Surface` world pos/rot, but live travel heading is `PivotTransform.localRotation` (`GeoNavComponent.cs:119`) / `Surface.localEulerAngles` (`UpdateHeading :258`). Ship Surface-only for INC-3a (cheap; may miss cosmetic banking) or also push heading? Decide after in-game look.
- **Cadence numbers + smoothing:** start at `TimeSyncBroadcaster`'s 0.5s; is 0.2–0.3s needed for smooth vehicle motion, and do we add client-side interpolation between snapshots to hide a coarse cadence vs spend bandwidth?
- **`NavigateRoutine` handling on client:** explicitly prevent it from starting for mirrored vehicles (add to a no-start guard) vs rely on per-tick overwrite (mirror = last writer). Overwrite is simpler but lets one frame of wrong pos slip.
- **CRC tolerance window size:** how many in-flight unreliable seqs to tolerate before flagging divergence, and is the INC-3 targeted full re-push sufficient or must some cases wait for the INC-5 reload?
- **Retiring `StartTravel` command-replay:** confirm only the `StartTravel` branch of `CommandExecutor`/`HostArbiter` is removed and `SetTimeState` (same `ApplyResult` path) is untouched; audit any other `CampaignActionType` riding that path.
- **Vehicle enumeration at scale:** iterate `GeoFaction.Vehicles` per faction (LINQ wrapper over `GeoMap.VehiclesByOwner` cache) vs iterate flat `GeoMap.Vehicles` once and group by `Owner` — pick the cheaper for many alien vehicles per tick.
- **Wire size:** cap records per `0x35` envelope / split across frames if many factions move simultaneously, to stay under MTU on the unreliable channel.

---

## 9. Risks

- **VehicleID is per-faction** (`GeoFaction._lastVehicleIndex`) — the key MUST be `(factionGuid,VehicleID)`; `VehicleID` alone collides across factions. Current `GeoBridge.FindVehicleById` is Phoenix-only (`GeoBridge.cs:42-43`) → **THE LIVE BUG**; must generalize to the resolved owner faction.
- **`GeoBridge.FindFactionByGuid` falls back to `PhoenixFaction`** on empty/unmatched guid (`GeoBridge.cs:159,164`) — correct for Phoenix aircraft, WRONG for alien/diplomatic vehicles in a generic all-faction diff; require a non-empty guid match for non-Phoenix.
- **`GeoVehicle.ProcessInstanceData` is heavy** — re-clones Stats from `VehicleDef.BaseStats` (`GeoVehicle.cs:1092`) and re-adds equipment (`:1101-1123`); calling it per tick is wasteful. Use light reflected setters for high-frequency mirror, reserve `ProcessInstanceData` for init + CRC-heal.
- **`Travelling` setter side-effect:** setting `Travelling=true` clears `CurrentSite` via `CurrentSite.VehicleLeft` (`GeoVehicle.cs:212-216`) — apply ordering matters; set `Travelling` before `CurrentSite` to land the right `CurrentSite` (native net result of `:1094` vs `:1097`).
- **Client `NavigateRoutine` could double-integrate** and fight the mirror (it is on the INC-1 whitelist, not suppressed). Mitigate: client never calls `StartTravel` for mirrored craft + per-tick pos/rot overwrite makes the mirror the last writer; consider a no-start guard.
- **A `0x35` record referencing an unsynced site/vehicle** `SiteId`/`VehicleID` won't resolve (`BuildSitePath` returns null on miss, `GeoBridge.cs:69`; site sync still deferred until INC-3b) → skip/queue gracefully; rely on reliable `0x36` create-before-state ordering.
- **UNRELIABLE pos/rot/range packets can drop/reorder** → require a monotonic per-`(scope,identity)` `seq` and drop stale on the client, else the vehicle jitters backward.
- **CRC via native `Serializer.Write` is a coroutine** (`IEnumerator<NextUpdate>`, `Serializer.cs:562`) and is exposed to `GeoVehicleInstanceData` `SerializeType Version=3` skew → CRC the bespoke deterministic codec image instead (reuse `SaveTransferCoordinator` `Crc32`).
- **Retiring `StartTravel` command-replay must not break** the kept client→host input relay (`StartTravelInterceptPatch.Prefix`) nor the `SetTimeState` replay; verify the kept/retired split precisely.
- **Bandwidth:** all-faction per-frame snapshots could be large with many alien vehicles; change-driven diff + epsilon + throttle + per-envelope record cap keep it bounded.
