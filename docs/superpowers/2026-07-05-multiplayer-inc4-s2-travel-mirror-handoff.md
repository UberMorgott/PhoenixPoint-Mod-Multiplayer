# Multiplayer session handoff — 2026-07-05 (Inc4 S2 travel mirror + in-game gate loop)

> Session 2026-07-04/05. Inner `main` HEAD `9e80b24` (local only, NOT pushed). Tests 1098/1098 (1091 unit + 7 bridge). Living tracker: [`../COOP-SYNC-ROADMAP.md`](../COOP-SYNC-ROADMAP.md).

## Theme

Inc4 S2 travel mirror shipped + long in-game gate loop (17 commits). Project renamed Multipleer -> Multiplayer (assembly, mod ID, logs, deploy folder).

## Commits (inner main, oldest -> newest)

- **`0d38d20` feat(sync): S2 travel mirror v1** — wrote wrong transform (Surface child)
- **`77c47c0` fix(sync): VoidOmen narrative title-fallback**
- **`039f64a` fix(sync): pivot-write fix** — `NavigateRoutine` writes `PivotTransform.localRotation` + `Surface.localEulerAngles`
- **`0d5b77e` chore(sync): DIAG instrumentation** — paired DIAG-H/DIAG-C probes
- **`da06105` fix(sync): composite key fix** — ROOT CAUSE: `GeoVehicle.VehicleID` is PER-FACTION (`++_lastVehicleIndex` per `GeoFaction`) -> 5 vehicles shipped as id=1, client last-writer-wins; key now `(OwnerId=FNV-1a(faction def asset name), VehicleID)`
- **`e8efcc6` feat(sync): client snapshot interpolation** — ring buffer, slerp, clamp-hold, no extrapolation
- **`0b4b81a` feat(sync): MoveVehicle relay** — `SyncedActionIds.MoveVehicle=40`, `MoveVehiclePatch` on `StartTravel`, `IHostOnlyApply`; `0xA6` travel-meta mirror `{travelling,currentSiteId,destSiteIds}` display-only -> fixes yellow route line
- **`67608fe` feat(sync): ExploreSite relay** — `=41`, `StartExploringCurrentSite` choke; latency cut (poll 15->6 ticks ~10 Hz, interp delay derived 0.15 s, immediate-emit on vehicle order via `VehicleEmitScheduler`)
- **`6ab4adb` feat(sync): GeoSiteState.Inspected** — site channel #5 (explore reveal) + DIAG-START probe
- **`470a7e0` chore: project-wide rename Multipleer -> Multiplayer** — assembly `Multiplayer.dll`, mod ID `Morgott.Multiplayer`, logs `LocalLow\..\Phoenix Point\Multiplayer\multiplayer*.log`; outer docs commit `8ae6bb1`; game `Mods\Multiplayer` migrated, instance-2 junction recreated
- **`e237e57` fix(sync): interp gap-guard** — `maxBlendGapSeconds` ~0.8 s + heading/world-fwd DIAG
- **`bdb9496` feat(sync): ReportMirrorGate.Enabled=true** — existing `0x69 ReportModalShow`; research-complete popup + base-outcome/pandoran-reveal/diplomacy-brief mirror to client
- **`7409e19` fix(sync): host explore apply via native ExploreSiteAbility.Activate**
- **`057614f` fix(sync): client research-popup click** — `GeoscapeView.ToResearchState()` (was null `DialogCallback`)
- **`d2aab7b` feat(sync): turbine-tracer fix + explore-button greying** — mirror native `GeoVehicle.Animator` int `State` 0/1 off `0xA6 Travelling` (was stuck parked-oriented; facing faithful, fwd dot=+1.0); Postfix `ExploreSiteAbility.GetDisabledStateInternal` (host native `IsExploringSite`, client `0xA7 IsExploringMirrored`; refresh via native `AbilityStateChangeCheck`)
- **`efa27d0` fix(sync): explore hygiene** — `ClearClientExploring` on client geoscape (re)load; `GeoSite.RefreshVisuals()` after site apply (PropertyChanged repaint was skipped)
- **`9e80b24` fix(sync): exclude mission-arrival deploy prompts from event mirror** — `IsMissionDeployEvent`: any choice `Outcome.StartMission.MissionTypeDef != null`; gates raise+dismiss+single-choice-close broadcast sites; fail-open; likely also fixes phantom-result event duplicates

## Deployed

- `D:\Steam\steamapps\common\Phoenix Point\Mods\Multiplayer\Multiplayer.dll` — SHA256 `DEA2D4C7...838A`, deployed both instances (junction)

## Envelope map (current)

| Envelope | Purpose | Notes |
|---|---|---|
| `0xA5` | Vehicle position | ~10 Hz, signature-skip, immediate-emit |
| `0xA6` | Travel meta + Animator State | `{travelling, currentSiteId, destSiteIds}` + int `State` 0/1 |
| `0xA7` | Explore progress | Whole-percent |
| `0x69` | Report modals | 4-type whitelist, exhaustive enum-exclusion test |
| `0xA2`-`0xA4` | RESERVED | Action-relay envelope cutover |

## Actions relayed

- `MoveVehicle=40`, `ExploreSite=41`

## In-game CONFIRMED this session

- Smooth vehicle mirror (all factions), client-ordered flight + explore, yellow route line
- VoidOmen narrative + yellow title header
- Research popup on client
- Tracers pending recheck

## PENDING in-game verification (FIRST THING next session)

1. `efa27d0` explore context-menu item never stuck greyed
2. `efa27d0` POI inspected icon flips on client
3. `9e80b24` deploy-prompt silence on host cancel
4. Event-duplicate scenario rerun
5. `057614f` research-nav click
6. `d2aab7b` explore-button grey lifecycle

## Next steps

1. User in-game verify list above
2. Inc3 action-relay envelope-cutover spec STILL awaits user review (`0xA2`-`0xA4`, `docs\superpowers\specs\2026-07-02`)
3. Inc4 S3 sim-freeze default-ON — travel-mirror prerequisite NOW DONE, S3 is next natural increment
4. Ability-relay backlog (same `MoveVehiclePatch` shape): Harvest, Excavate, EmergencyRepair, Scan, AncientSiteProbe, ActivateBase, AncientGuardianGuard; mission flows separate
5. Spawn-gap future risk (host spawns new faction craft mid-session -> client id-miss)
6. Host context-menu live-refresh while open = declined (fragile), greys on reopen
7. USER TODO: rename root folder `E:\DEV\PhoenixPoint\Multipleer` -> `Multiplayer` (deploy.ps1 path-agnostic, .gitignore has both); re-enable mod in in-game mod menu BOTH instances (new ID `Morgott.Multiplayer`)
