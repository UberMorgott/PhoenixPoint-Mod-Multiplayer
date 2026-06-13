# EOD handoff — thin-client vehicle sync + snapshot interpolation — 2026-06-13

> Seamless-continuation handoff. Pick up here next session. All decompile-verified seams already
> live in the spec/plan — this doc REFERENCES them, does not re-derive.
> One-glance status: `docs/research/00-current-state.md` (top block). Design: spec below.

## Where we are (1 paragraph)

- The CLIENT half of the `0x35 GeoStateDiff` vehicle mirror was re-architected today from a
  half-authoritative render (client re-ran native travel motion — the scrapped SWITCH-A `fd45252`
  `NavigateRoutine` driver) to a **host-authoritative THIN CLIENT + snapshot interpolation**: the
  client geoscape vehicle engine is FROZEN (runs NO native travel routine — single-writer invariant),
  the host streams the authoritative transform ~15Hz over `0x35`, and the client renders as a pure
  mirror + entity interpolation. INC-A/B/C built + deployed; the 3 in-game-log-RCA'd visual fixes
  (jerk / travel-line / nose) built + deployed. In-game gate: sync both directions ✓, travel line ✓,
  nose along travel ✓, smoother. **The one remaining live issue: the client craft flies SLOWER than
  the host (progressively behind, not a constant delay) and is still not perfectly as smooth** — a
  speed/cadence problem to RCA from the log next session, then finish INC-D cleanup.

## Git state at EOD

- Inner repo `E:\DEV\PhoenixPoint\Multipleer`, branch `main`.
- HEAD = `7636db4` *fix(geosync): INC-D point client craft nose along travel via native heading math*.
- **Deployed DLL `5F7C2F0F`. 193 xUnit green, build 0/0, wire/codec UNCHANGED.**
- **PUSHED to `origin/main` this session (EOD) — user-authorized push, no force.** No feature branches
  with unmerged work — everything is on `main` (3 stale feature branches are fully-merged ancestors).
- Working tree CLEAN.

## Architecture — thin-client + snapshot interpolation

- Spec: `docs/superpowers/specs/2026-06-13-thin-client-vehicle-sync-snapshot-interpolation.md`.
- Plan: `docs/superpowers/plans/2026-06-13-thin-client-vehicle-sync-increments.md`.
- Core: client vehicle sim FROZEN (single writer = `ClientVehicleInterpolator`); host streams full
  transform (pos + rot) ~15Hz; client renders by snapshot interpolation (render ~150ms back, lerp pos;
  heading now DERIVED on the client via native heading math — see P3). Wire format UNCHANGED (rotation
  already streamed continuously); host change = SEND RATE; client change = consume via a timestamped
  buffer instead of re-running native motion. Builds on the SD-AIDR INC-3a `0x35` all-faction mirror
  (`(factionGuid,VehicleID)` resolver).

## Done + deployed today (thin-client)

### INC-A — single-writer mirror-only raw placement — `069c1ce`

- Retired the SWITCH-A native travel driver (`ClientNativeTravelDriver.cs` DELETED); removed the
  `EntityReplicationScope.IsApplying` carve-out in `StartTravelInterceptPatch.Prefix`; gutted
  `ClientVehicleInterpolator` to mirror-only placement; `ApplyVehicleState` stopped the direct render
  writes. Client craft now JUMPS to host pos each push but the mirror is the SOLE writer.

### INC-B — raise host stream to ~15Hz — `e3f0bcb`

- `GeoStateSyncBroadcaster` `FlushIntervalSeconds` 0.5→0.066, `SnapshotIntervalSeconds` 0.1→0.066.
  Cheap dirty-check (`_lastSig`/`SigChanged`/`TryGetCheapVehicleSignature`) + `_continuousPending`
  latest-wins coalescing KEPT (the perf fix that prevents host co-op lag — only moving craft pay cost).

### INC-C — client snapshot interpolation — `8f1aaa8`

- `ClientVehicleInterpolator` rewritten to a ring buffer + per-frame bracket/lerp pos / slerp rot at
  `now − InterpDelay`; pure `SnapshotBuffer` alpha/bracket math (TDD); discrete arrival snaps the buffer.

### TFTV-compat fix — `954b61a`

- Per-instance log redirect installed via `AssemblyLoad` so it applies BEFORE the TFTV logger init.

### 3 in-game-log-RCA'd VISUAL fixes (single-writer preserved, native rendering REUSED)

> Commit subjects are tagged "INC-D", but the INC-D *cleanup* (DIAG strip + dead-code removal + re-tune)
> is still PENDING — see "Exact next steps".

- **P1 jerk** `0a877b4` — `ClientVehicleInterpolator` InterpDelaySeconds 0.2→0.35. The buffer was
  Hold-with-full-depth: renderTime = now − 0.2s overran the newest sample because real inter-arrival
  jitter exceeded 0.2s.
- **P2 travel line** `a118fa4` — `ClientTravelEmitterSuppressPatch` now carves out `set_Travelling`
  ONLY during mirror apply (`EntityReplicationScope.IsApplying`); it was unconditionally suppressed →
  client `Travelling=false` → native `GeoscapeView.DrawVehiclePathLinks` never drew. `InitiateTravelling`
  / `OnArrived` STAY suppressed. `DestinationSites` resolved to a real `GeoSite` in `ApplyVehicleState`.
- **P3 nose** `7636db4` — new `GeoBridge.UpdateVehicleHeadingTowards` reuses native
  `GeoNavComponent.GetHeadingTowardsTarget` / `UpdateHeading` (reflection) toward
  `DestinationSites[0].WorldPosition`, called in `ClientVehicleInterpolator.Tick` after
  `PlaceGlobeIconAt`; dropped the per-frame `SetSurfaceRotation`. No `NavigateRoutine` (that was the
  scrapped `fd45252` 2nd writer).

## IN-GAME GATE RESULT (deployed `5F7C2F0F`)

- Sync BOTH directions ✓; travel line on client ✓; nose along travel ✓; smoothness improved.
- **KNOWN REMAINING (the live issue to fix next):**
  1. **Client craft flies SLOWER than host** — apparent SPEED mismatch; the client is PROGRESSIVELY
     behind, not just a constant delay.
  2. Still not perfectly as smooth as the host.
  - Likely interpolation cadence / snapshot inter-arrival vs render-rate. Investigate from the log
     next session (compare host send timestamps vs client ingest/render timing).

## EXACT NEXT STEPS (numbered)

1. **RCA the speed mismatch from the log** (the #1 live issue). Compare host `0x35` send cadence vs
   client snapshot inter-arrival vs render timeline. If the client renders progressively behind, the
   buffer is draining slower than it fills (render-rate / `InterpDelay` / cadence mismatch) — NOT a
   constant delay. Use `superpowers:systematic-debugging`.
2. **Re-tune** `InterpDelaySeconds` / `RingCapacity` / the host `FlushIntervalSeconds` if the speed
   issue is delay/cadence-related (INC-D Task D1). Re-check host lag at each step (dirty-check must
   still skip idle craft).
3. **INC-D cleanup (Task D3):** strip ALL temp DIAG — commits `b753111` + `fbfb3f9` + the DIAG/INC-C
   instrumentation lines; remove the now-DEAD `InterpolationMath.cs` (the native-equation math is gone;
   the pure interp math lives in `SnapshotBuffer` now) AND its tests; ensure build 0/0 + full suite
   green after the strip.
4. **INC-D edge cases (Task D2):** reroute mid-flight / removal (`0x36`) / parked / first-mirror; add
   `ClientGeoStateApplier.ResetState()` (clear `_lastAppliedSeq`/`_firstMirrorDone`) wired in
   `NetworkEngine.Shutdown` next to `ClientVehicleInterpolator.Reset()` so a 2nd co-op session is clean.
5. **INC-D final gate (Task D4):** all edge cases correct; host steady at the chosen rate with no lag
   over a long session; no console errors either instance. Record the outcome to
   `docs/research/00-current-state.md`.

## Test rig + workflow

- 2nd Goldberg instance (`Multipleer\tools\` — see memory `multipleer-second-instance-setup`;
  copy already provisioned at `D:\PP-Instance2`, launch via `launch-second-copy.bat -mods`). DirectIP
  127.0.0.1 (offline → only DirectIP verifiable). Deploy via `deploy.ps1` to BOTH copies.
- Commit to inner `main`. NO feature branches. (GitHub push was a one-off EOD authorization this
  session — resume the normal no-push-during-dev default next session unless re-authorized.)
- Build: `dotnet build E:\DEV\PhoenixPoint\Multipleer\Multipleer.csproj -c Release`
- Tests: `dotnet test E:\DEV\PhoenixPoint\Multipleer\Multipleer.Tests\Multipleer.Tests.csproj -c Release`

## Key references (don't re-derive)

- Spec: `docs/superpowers/specs/2026-06-13-thin-client-vehicle-sync-snapshot-interpolation.md`.
- Plan: `docs/superpowers/plans/2026-06-13-thin-client-vehicle-sync-increments.md`.
- Status: `docs/research/00-current-state.md` (thin-client block at top).
- Foundation arc: `docs/superpowers/specs/2026-06-13-coop-state-replication-design.md` (SD-AIDR),
  INC-3a `docs/superpowers/specs/2026-06-13-coop-state-replication-inc3-geostatediff.md`,
  prior handoff `docs/superpowers/2026-06-13-EOD-replication-handoff.md`.
