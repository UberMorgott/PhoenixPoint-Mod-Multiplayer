# EOD handoff — SD-AIDR replication + host→client movement blocker — 2026-06-13

> Seamless-continuation handoff. Pick up here next session. All decompile-verified seams already
> live in the spec/plans — this doc REFERENCES them, does not re-derive.
> One-glance status: `docs/research/00-current-state.md` (top block). Design: spec below.

## Where we are (1 paragraph)

- Co-op geoscape replication was re-architected today. The old "intercept each player action and
  relay it" approach (command-sync / time-sync arc) was REJECTED — it was whack-a-mole and drifted
  into desync. New architecture = **SD-AIDR**: the **host is the sole simulator**, the **client is a
  pure mirror**, and **client inputs are relayed to the host**. INC-1 (client inert) and INC-2
  (entity create/destroy) are BUILT and partly in-game-confirmed. The live bug to fix next is
  **host→client movement of an EXISTING aircraft** — root cause is a client-side vehicle-set SUBSET,
  not id divergence. DIAG2 instrumentation is deployed to pin down WHY the set is a subset.

## Git state at EOD

- Inner repo `E:\DEV\PhoenixPoint\Multiplayer`, branch `main`.
- HEAD = `fbfb3f9` *chore(diag): temporary host/client vehicle-id set logging for movement*.
- **100 commits ahead of `origin/main`. NOT pushed (dev-only). No feature branches — everything on `main`.**
- Working tree CLEAN.

## Architecture pivot — SD-AIDR

- Spec: `docs/superpowers/specs/2026-06-13-coop-state-replication-design.md` (commit `c664128`).
- Designed via judged design-workflow + adversarial verification of every load-bearing seam.
  Readiness **YELLOW**; 4 corrections **C2 / C4 / C8 / C15** folded in.
- Core: host = sole simulator; client = pure mirror; client inputs → host. Replaces per-action
  cherry-picking. Read the spec before adding any new sync surface.

## Done + in repo

### INC-1 — client geoscape INERT — IN-GAME OK

- Plan: `docs/superpowers/plans/2026-06-13-replication-increment1-client-inert.md` (`95d416e`).
- Commits: `d8dd1ac` (auditable 13-producer suppress table, pure, TDD) → `25e2965`
  (`ClientGeoSimSuppressPatch` — 13 producers → `NextUpdate.Never`) → `6ef9348`
  (`ClientTravelEmitterSuppressPatch` — 3 GeoVehicle travel emitters, render-only).
- RESULT in-game: client clock SYNCS host-authoritative (slaved + advancing); client no longer
  self-simulates the geoscape. Correct.

### INC-2 — entity create/destroy (`0x36 GeoEntityOp`) — IN-GAME: SiteRemoved SYNCS ✓

- Plan: `docs/superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md` (`9bf7e57`).
- Commits in order:
  - `b56476e` — pure `GeoEntityOp` codec for `0x36` (4 op-types, TDD).
  - `7d7cba1` — `EntityReplicationScope` `[ThreadStatic]` replay guard (TDD).
  - `f1c218b` — wire `0x36` packet + `BroadcastGeoEntityOp` + route.
  - `030eadb` — `GeoBridge` entity-op resolvers (def / faction / site + native create + id reconcile).
  - `12cc619` — `ClientEntityOpApplier` (native lifecycle replay + VehicleID reconcile, client-only).
  - `786523d` — `HostEntityOpBroadcastPatch` — broadcast `0x36` on create/remove seams (host-only).
  - `53947b4` — critical fix (see load-breaker 1 below).
- RESULT in-game: **SiteRemoved (base destruction) SYNCS ✓.**
- **Two load-breakers caught (independent review + empirical Harmony probe) and fixed:**
  1. Sent `GeoVehicleDef.Guid` where a `ComponentSetDef` guid was required (sibling types) →
     `ArgumentException` on client. Fixed `53947b4`: broadcast the `ComponentSetDef` guid via
     `GeoBridge.DefGuid(__1)` (`GeoBridge.cs:179`).
  2. A single 4-target postfix with `__0` injection on the 0-param `DestroySite` → `PatchAll` throw
     → whole-mod load failure. Fixed by SPLITTING into `HostVehicleCreateBroadcastPatch`
     (2 create targets, uses `__1`; `src/Harmony/HostEntityOpBroadcastPatch.cs:88`) +
     `HostEntityRemoveBroadcastPatch` (uses `object[] __args`; `:158`).

## LIVE BLOCKER — host→client movement of an EXISTING aircraft

- **Symptom:** client→host movement works BOTH directions; host→client = client shows nothing.
  This is NOT a creation bug — creation was a detour and is now fixed.
- **Root cause:** replication keyed on runtime `VehicleID`. The host's `PhoenixFaction.Vehicles`
  is a SUPERSET; the client's is a SUBSET. Host orders e.g. vehicle 3 of 5 → on the client
  `GeoBridge.FindVehicleById` (`GeoBridge.cs:40`, called from `CommandExecutor.cs:48`) cannot
  resolve it → `"[Multiplayer] StartTravel apply: vehicle N not found."` (`CommandExecutor.cs:49`)
  → it aborts BEFORE travel. Client→host works because the client only ever moves vehicles it has;
  the host (superset) always resolves the client's id.
- **NOT id-divergence** (verified): client co-op load uses NATIVE restore
  (`SaveTransferCoordinator.PrepareEntryFromBlobCrt:469` `CreateSceneBinding` →
  `GeoVehicle.ProcessInstanceData`), which PRESERVES the saved `VehicleID`
  (`GeoVehicle.cs:1134`, `GeoVehicleInstanceData.cs:45`). Ids match; the SET is what differs.
- **OPEN QUESTION — why client's set is a SUBSET of the same blob (pick after DIAG2):**
  - (a) Host-created-during-play vehicles never replicated. Deploy/manufacture is a per-machine
    UI action with NO client→host relay — `GeoPhoenixFaction.DeployAsset:714` → `CreateVehicle`;
    a client-origin deploy runs on the client and is never broadcast. **Leading hypothesis.**
  - (b) Client under-restored the blob.
  - (c) Host had extra vehicles at save time.

## DIAG instrumentation deployed — TEMPORARY (remove after root cause confirmed)

- `b753111` — DIAG creation-path boundary logs: `DiagDeployLogPatch.cs:16`
  (`DiagDeployLogPatch`, prefix on `DeployAsset`, logs machine `IsHost`/`IsActive`).
- `fbfb3f9` — DIAG2 vehicle-set logs:
  - host StartTravel broadcast id + host vehicle list — `StartTravelInterceptPatch.cs:88-89`.
  - client apply `requestedVehicleId` + client vehicle list — `CommandExecutor.cs:43-44`.
  - client vehicles **AT LOAD** — `CurtainShowPatch.cs:94`.
- Helpers: `GeoBridge.DescribeVehicles` (`GeoBridge.cs:83`), `GeoBridge.VehicleDefNameOf` (`:113`).
- Temp patch file: `src/Harmony/DiagDeployLogPatch.cs`.
- Deployed DLL SHA256 `B539C82A…F1E6`.

## EXACT NEXT STEPS (numbered)

1. **User runs the 2-instance rig**, moves an aircraft host→client AND client→host, then captures
   `multiplayer.log` + `multiplayer-2.log` IMMEDIATELY. **Do NOT relaunch first** — logs rotate/clobber.
2. **Grep `DIAG2`** in both logs. Compare `DIAG2 host vehicles[N]` vs `DIAG2 client vehicles[N]`;
   is the host's `requestedVehicleId` present in the client set? `DIAG2 ... AT LOAD` vs apply-time
   distinguishes: under-restore (missing AT LOAD) vs post-load-loss (present AT LOAD, gone later)
   vs host-extra (only host ever had it).
3. **Decide the fix from DIAG2 data:**
   - (i) Make the client mirror the host's FULL vehicle set — relay client-origin deploy +
     broadcast ALL host vehicle creates (hook `VehicleAdded` generically, gate load-time); OR
   - (ii) Build **INC-3 generic state-diff `0x35 GeoStateDiff`** (native InstanceData-diff) so the
     client mirrors the host's whole geoscape state. This likely SUBSUMES the movement bug AND
     covers base-attack progress / ownership / prices / faction-traffic / arrival authority + SiteCreated.
   - Prefer (ii) if DIAG2 shows multiple divergent state surfaces, not just vehicles.
4. **After root cause confirmed: REMOVE DIAG/DIAG2 temp logging.** Revert `b753111` + `fbfb3f9`,
   delete `src/Harmony/DiagDeployLogPatch.cs`, strip the inline DIAG/DIAG2 blocks
   (`StartTravelInterceptPatch.cs`, `CommandExecutor.cs`, `CurtainShowPatch.cs`, `GeoBridge.cs`).
5. **Remaining for full sync (all INC-3):** base-attacks, prices, faction traffic, random events,
   arrival / `CurrentSite` authority on the client.

## Test rig + workflow

- 2nd Goldberg instance (`Multiplayer\tools\` — see memory `multiplayer-second-instance-setup`;
  re-run `make-second-copy.bat` if the copy isn't on disk). DirectIP transport (STUN best-effort).
- Deploy via `deploy.ps1`.
- Commit to inner `main`. **NO push to GitHub. NO feature branches.** Tests-green gate before commit.

## Key references (don't re-derive)

- Spec: `docs/superpowers/specs/2026-06-13-coop-state-replication-design.md` (`c664128`).
- Plans: `docs/superpowers/plans/2026-06-13-replication-increment1-client-inert.md` (`95d416e`),
  `docs/superpowers/plans/2026-06-13-replication-increment2-entity-lifecycle.md` (`9bf7e57`).
- Status: `docs/research/00-current-state.md` (SD-AIDR block at top).
- Prior (superseded) arc: `docs/superpowers/specs/2026-06-12-geoscape-command-sync-design.md`.
