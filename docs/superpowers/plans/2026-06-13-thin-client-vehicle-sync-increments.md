# Thin-Client Vehicle Sync — Snapshot-Interpolation Increment Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:executing-plans` (or
> `superpowers:subagent-driven-development`) to run this plan task-by-task. Steps use checkbox
> (`- [ ]`) syntax. TDD where a pure core exists, DRY, YAGNI; commit straight to inner `main`
> (NO branch, NO push); full test suite green before each commit. Design contract:
> `docs/superpowers/specs/2026-06-13-thin-client-vehicle-sync-snapshot-interpolation.md`.

**Goal:** re-architect the CLIENT half of the `0x35 GeoStateDiff` vehicle mirror to a host-authoritative
THIN CLIENT — client vehicle sim FROZEN, host streams the full transform (pos + rot) at ~15 Hz, client
renders by SNAPSHOT INTERPOLATION (render ~150 ms back, lerp pos + slerp rot between two snapshots). No
client prediction. Retires the half-authoritative SWITCH-A native-routine driver + the native-equation
segment render (the second writer that caused jerk + flies-sideways).

**Architecture:** single writer = `ClientVehicleInterpolator` (snapshot buffer). The native motion writer
(`GeoNavComponent.NavigateRoutine`) never runs for a replicated craft. Wire format is UNCHANGED
(rotation already streamed continuously, §2 of the spec); the host change is the SEND RATE; the client
change is consuming pos+rot via a timestamped buffer instead of re-running native motion.

> **PROGRESS (2026-06-13 EOD — deployed DLL `5F7C2F0F`, 193 xUnit green, build 0/0):**
> INC-A `069c1ce`, INC-B `e3f0bcb`, INC-C `8f1aaa8` DONE + deployed (+ TFTV-compat fix `954b61a`).
> 3 in-game-log-RCA'd VISUAL fixes DONE + deployed (commit subjects tagged "INC-D"): P1 jerk
> InterpDelay 0.2→0.35 `0a877b4`, P2 travel line `a118fa4`, P3 nose-along-travel `7636db4`.
> IN-GAME GATE (`5F7C2F0F`): sync both directions ✓, travel line ✓, nose ✓, smoother. **KNOWN
> REMAINING (next session):** (1) client flies SLOWER than host — apparent speed mismatch (client
> progressively behind, not constant delay); (2) still not perfectly as smooth as host — likely interp
> cadence / snapshot inter-arrival vs render-rate; investigate from log. **INC-D cleanup still PENDING:**
> strip DIAG (`b753111`+`fbfb3f9`+DIAG/INC-C lines), remove dead `InterpolationMath.cs` (+ tests), re-tune.

**Tech stack:** C# net472, HarmonyLib (`AccessTools`; mod NEVER hard-references game types — params typed
`object`), xUnit 2.9.2 (`Multiplayer.Tests`, pure cores TDD-first), existing `NetworkEngine`
(`BroadcastUnreliable:207`/`BroadcastToAll`, `RouteMessage` `0x35` case `:625`, `Update` tick `:373`),
existing `GeoStateDiffCodec`/`GeoVehicleStateDiffer` (wire — KEEP), `GeoBridge` (`PlaceGlobeIconAt:534`,
`NowSeconds:551`, `ApplyVehicleStateFull:466`, `FindVehicleByFactionAndId:175`), the slaved clock
(`TimeBridge.GetTiming:37` via `0x34`).

---

## In-game checkpoint (the GATE)

2-instance co-op (`multiplayer-second-instance-setup`: Goldberg-emu 2nd copy + `mklink /J` junctions;
deploy the Release DLL to BOTH copies). HOST + CLIENT, shared campaign, client joined past load (geo-sim
inert). **Precondition:** a Phoenix Manticore AND a non-Phoenix (NJ Thunderbird) craft on the geoscape.
Each increment has its own PASS criteria below; do NOT advance on a fail (`superpowers:systematic-debugging`).

---

## Build / Test / Deploy

**Build:** `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
**Tests:** `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
**In-game (2-instance):** per `multiplayer-second-instance-setup`.

> **Test linking:** `Multiplayer.Tests/Multiplayer.Tests.csproj` has `EnableDefaultCompileItems=false`;
> pure cores are linked individually (`InterpolationMath.cs` link already present). Any new PURE file
> under test needs its own `<Compile Include="..\src\..."><Link>X.cs</Link></Compile>` line.

---

## INC-A — Retire the second writer; client frozen; mirror-only placement — DONE + deployed `069c1ce`

> Removes ALL client-side native-motion re-running. After INC-A the client craft JUMP to the host
> position each push (jerky, maybe wrong nose) but the mirror is the SOLE writer. Proves freeze complete.
>
> **SCOPE: covers SYMPTOM A AND SYMPTOM B** — both are SWITCH-A regressions (Verified RCA in A0 below).
> SYMPTOM A = host-initiated travel freezes on the client after the 1st leg then teleports on arrival.
> SYMPTOM B = client-initiated travel flies on client+host then the CLIENT ship snaps BACK to origin on
> arrival (mirror-only, no snap-back, is the fix).

### Task A0 — GATE ZERO: prove the host→client stream EXISTS before smoothing it  *(diagnostic; precedes A1)* — DONE (verified `fd45252`)

> **Premise check (most important).** This plan + spec assume host→client APPLIES ARRIVE and the only
> defect is jerkiness (2 Hz step-teleport) — a SMOOTHING problem. Interpolation cannot smooth a stream
> that does not exist. If the live symptom is asymmetric ("host acts → client shows NOTHING; client acts →
> host syncs"), no stream exists and INC-B/C/D build on a non-existent foundation. RESOLVE THIS FIRST.

> **Verified RCA (`fd45252`, "SWITCH-A", fresh in-game log evidence):** the `0x35` continuous
> vehicle-state stream EXISTS and is applied by the client — host seq MONOTONIC per
> `(FactionGuid, VehicleID)`, never reset; client applies EVERY `0x35` record; ZERO drops / ZERO
> exceptions. → The break is NOT a missing stream and NOT a seq-drop; it is (A) the SWITCH-A render-side
> carve-out + (B) a client second-writer, BOTH introduced by SWITCH-A. Root-cause file:line:
> - **SYMPTOM A** (host-initiated: 1st leg synced, 2nd+ frozen then teleport-on-arrival) — render feeds
>   disabled + delegated to a client-run native routine that never advances during host travel:
>   - `src/Network/CommandSync/GeoBridge.cs:351-360` `ApplyVehicleState` — when `IsNativeTravelling`, SKIPS
>     the `surface.position` write AND `RouteVehicleRender` → continuous `0x35` pos DISCARDED.
>   - `src/Network/CommandSync/ClientVehicleInterpolator.cs:213-214` `Tick` — `if (IsNativeTravelling)
>     continue;` SKIPS icon placement.
>   - `src/Network/CommandSync/ClientNativeTravelDriver.cs:97-101` `OnRecord` → `GeoBridge.StartVehicleTravel`
>     — driver does NOT advance the client globe icon during host travel → icon only JUMPS at discrete
>     arrival.
>   - *Aggravator (NOT a bug):* host event-popup PAUSE gaps the slaved clock; on pause the craft CORRECTLY
>     stands still once mirror-only.
> - **SYMPTOM B** (client-initiated: flies on client AND host, then on arrival the CLIENT ship snaps BACK
>   to origin while the host stays at dest) — TWO writers on the client for its own ship:
>   - `src/Harmony/StartTravelInterceptPatch.cs:38` `Prefix` — host-approved command RE-EXECUTES the real
>     `StartTravel` on the client via `CommandRelay.IsApplying`, spawning a native routine NOT registered
>     in `ClientNativeTravelDriver` → `IsNativeTravelling=false` → the `0x35` mirror ALSO drives
>     Surface/interpolator → TWO writers → flies on the client.
>   - `src/Harmony/ClientTravelEmitterSuppressPatch.cs:54-60` — unconditionally suppresses `set_Travelling`
>     / `InitiateTravelling` / `OnArrived` on the client → the command-started native routine CAN'T
>     finalize at the destination → on routine end the icon reverts to the stale `CurrentSite` (= origin) →
>     snap-back.
>
> **Hypothesis status (post-RCA):** H1 REFUTED (over-aggressive freeze — craft instantiates + resolves),
> H2 latent-risk (shared monotonic seq across reliable/continuous, §7 caveat — NOT yet observed dropping a
> legitimate msg), H3 REFUTED (systemic apply-tract break — stream applies cleanly), H4 CONFIRMED
> (SWITCH-A second writer + render carve-out is the actual break).

- [x] **[OPEN QUESTION — ANSWERED `fd45252`]** Current in-game symptom = "jerky" (stream EXISTS →
      smoothing only), NOT "nothing". The host→client `0x35` stream is applied on the client with zero
      drops; the break is the SWITCH-A render carve-out (A) + client second-writer (B), not an absent
      stream. Proceed to A1.
- [ ] Instrument + assert the END-TO-END host→client tract, IN ORDER (DIAGB, moving craft):
      (1) host EMITS `0x35` for a moving vehicle (broadcaster send line);
      (2) client RECEIVES the packet (transport recv);
      (3) `NetworkEngine.RouteMessage` ENTERS `case 0x35` (`cs:625`);
      (4) seq-guard does NOT drop it — AND watch for a legitimate DISCRETE (reliable) msg wrongly dropped
          by the single monotonic seq line shared with the CONTINUOUS (unreliable) channel (spec §7 caveat;
          if seen → split to per-channel seq lines). **H2 latent-risk (`fd45252`): NOT yet observed dropping
          a legitimate msg — seq monotonic, zero drops in the log — keep watching, do not yet split;**
      (5) `(factionGuid, VehicleID)` RESOLVES — `GeoBridge.FindVehicleByFactionAndId != null`;
      (6) apply WRITES position (light-apply / placement runs);
      (7) read-back AFTER apply == host position.
- [ ] **If applies don't arrive/resolve at any step → FIX THAT FIRST. Do NOT proceed to A1/INC-B/C/D.**
- [x] **[HYPOTHESIS to check — over-aggressive freeze] — REFUTED (`fd45252`):** the craft IS instantiated
      and resolves (client applies every `0x35` record, zero unresolved) — the freeze does NOT block
      creation/registration. Does the FROZEN client ever INSTANTIATE the host-
      owned vehicle object? If not, step (5) `FindVehicleByFactionAndId` returns null and apply silently
      no-ops — breaks ONLY host→client, leaving client→host "working". *(Serena-confirmed scope:
      `ClientGeoSimSuppressPatch` patches only GeoSimProducer `NextUpdate(Timing)` callbacks→`Never`;
      `ClientTravelEmitterSuppressPatch` only `set_Travelling`/`InitiateTravelling`/`OnArrived`. NEITHER
      patches `PlaceGlobeIconAt`/`SetOrientedGlobeWorldPosition` nor any Instantiate — so the placement
      primitive is unpatched. Open risk = a now-frozen sim PRODUCER was what created/registered the craft.
      Hypothesis, NOT confirmed cause.)*
- [x] **[HYPOTHESIS to check — systemic break] — REFUTED (`fd45252`):** the vehicle stream itself delivers
      + applies cleanly (monotonic seq, zero drops), so the apply tract is NOT systemically dead — the
      defect is SWITCH-A-local, not broadcast/scope-routing. Confirm host→client delivers for at least ONE non-vehicle
      scope (Site / MarketPrice / FactionTraffic / FactionState — the out-of-scope `0x35` slices). If host→
      client is dead for those too, the break is SYSTEMIC (broadcast/apply tract or scope-routing) and
      vehicle re-architecture is NOT the fix — escalate.

### Task A1 — Delete the SWITCH-A native travel driver  *(impure; no unit test)*

- [ ] DELETE `src/Network/CommandSync/ClientNativeTravelDriver.cs` (whole file).
- [ ] In `NetworkEngine.cs` `Shutdown` (`cs:117-120`): remove the
      `ClientNativeTravelDriver.Reset()` call (`:120`); keep `ClientVehicleInterpolator.Reset()` (`:117`).
- [ ] Build will break at the call sites (A2/A3 fix them) — sequence A1→A3 before building.

### Task A2 — Remove the SWITCH-A carve-out in the StartTravel intercept  *(impure; no unit test)*

- [ ] In `src/Harmony/StartTravelInterceptPatch.cs` `Prefix`: DELETE the line
      `if (EntityReplicationScope.IsApplying) return true;` (the carve-out that let the driver run native
      StartTravel on the client). KEEP `CommandRelay.IsApplying` gate, the `IsHost` gate, and the
      client→host relay (encode + `RelayFromClient` + `return false`). Update the SWITCH-A comment.
- [ ] `CommandCodecTests` StartTravel encode cases must still pass (kept relay path unchanged).

### Task A3 — Gut `ClientVehicleInterpolator` to mirror-only placement + strip native-equation math  *(impure for the shell; the scrapped pure math is removed)*

- [ ] In `src/Network/CommandSync/ClientVehicleInterpolator.cs`: REMOVE all SEGMENT-mode state/methods
      (`SegActive`/`SegStart`/`SegEnd`/`SegCenter`/`SegStartSec`/`SegTotalSec`/`SegAngleRad`,
      `StartSegment`, `CorrectionSample`, `EndSegmentSnap`, `IsSegmentActive`, the `ClientNativeTravelDriver.IsNativeTravelling` skip in `Tick`) AND the EASE constant `K`. Reduce to a
      stub: per-identity `{ object Vehicle; Vector3 Pos; Quaternion Rot; bool Has; }`; `SetTarget(vehicle,
      identity, pos, rot, snap)` stores + on snap (always, INC-A) places via
      `GeoBridge.PlaceGlobeIconAt(pos)` and sets `Surface.rotation = rot`; `Tick` re-places the last
      stored pos/rot (no interpolation yet); keep `Remove`/`Reset`.
- [ ] In `src/Network/CommandSync/InterpolationMath.cs`: DELETE the native-equation members
      (`GreatCircleAngleRad`, `SegmentTotalSeconds`, `SegmentNum`, `ArcRatioFromAngles`,
      `CorrectedStartSec`, `EarthRadiusKm`, `SmoothFactor`/`SmoothTowards` if now unused). Leave the file
      (INC-C repopulates it with the buffer/alpha math).
- [ ] REMOVE the now-obsolete native-equation unit tests
      (`SegmentNum`/`SegmentTotalSeconds`/`GreatCircleAngleRad`/`CorrectedStartSec` cases). Keep
      `GeoStateDiffCodec`/`GeoVehicleStateDiffer` tests.

### Task A4 — `ApplyVehicleState`: stop the direct render-transform writes; place via the interpolator  *(impure; no unit test)*

- [ ] In `src/Network/CommandSync/GeoBridge.cs` `ApplyVehicleState` (`cs:323`): REMOVE the
      `nativeTravelling` gate + the direct `surface.position`/`surface.rotation` render writes + the
      `RouteVehicleRender` call (step 3 block). Instead, when the mask carries `SurfacePos`/`SurfaceRot`,
      hand the latest `{pos, rot}` to `ClientVehicleInterpolator.SetTarget(vehicle, identity, pos, rot,
      snap:true)` (INC-A snaps every push). KEEP the direct state-field setters (Travelling/CurrentSite/
      RangeRemaining/DestinationSites/HitPoints — those are STATE, not render).
- [ ] DELETE `RouteVehicleRender` (`cs:412`).
- [ ] In `ClientGeoStateApplier.ApplyVehicleRecord`: remove the `ClientNativeTravelDriver.OnRecord` calls
      (both first-mirror + light branches). First mirror still calls `ApplyVehicleStateFull` then seeds
      the interpolator (snap); light path calls `ApplyVehicleState`.
- [ ] Build 0/0 + full suite green. Commit `refactor(replication): retire SWITCH-A native travel driver + segment render; mirror-only placement (thin-client INC-A)`.

### Task A5 — IN-GAME GATE A  *(user runs)*

- [ ] Deploy Release DLL to BOTH copies. Host moves a Phoenix Manticore AND a NJ Thunderbird.
- [x] **PRECONDITION (Task A0 gate-zero) — MET (`fd45252`):** the host→client stream is PROVEN to arrive
      (client applies every `0x35` record, monotonic seq, zero drops); OPEN QUESTION resolved to "jerky"
      (stream exists), not "nothing".
- [ ] **PASS (covers SYMPTOM A and SYMPTOM B):**
      - **A (host-initiated):** the host-driven craft on the CLIENT follows the host CONTINUOUSLY (NO
        freeze-then-teleport). May be jerky/stepped (interpolation is INC-C) but NEVER frozen-then-teleport
        and NEVER self-drifts. With the host PAUSED → the client craft stands still (full freeze, single
        writer).
      - **B (client-initiated):** the client-initiated craft does NOT snap back to origin on arrival; it
        STAYS at the destination (a mirror of the host).
      - **General:** NO craft ever moves on the client from a local native routine — only from applied host
        state.
      - Departure/arrival land on the correct site. DIAGB: applies arriving with seq>1, post-apply
        read-back pos == host pos. Client→host StartTravel input still relays. **This proves the freeze is
        complete + the mirror is the sole writer.** Any self-motion → a residual native writer exists; find
        + suppress before INC-B.

---

## INC-B — Raise the send rate + consume rotation — DONE + deployed `e3f0bcb`

### Task B1 — Raise the continuous send/walk rate  *(impure; no unit test)*

- [ ] In `src/Network/CommandSync/GeoStateSyncBroadcaster.cs`: `FlushIntervalSeconds` `0.5f`→`0.066f`
      (~15 Hz continuous flush); `SnapshotIntervalSeconds` `0.1f`→`0.066f` (15 Hz walk). KEEP the cheap
      dirty-check (`_lastSig`/`SigChanged`/`TryGetCheapVehicleSignature`) and the `_continuousPending`
      latest-wins coalescing UNCHANGED (the perf fix that prevents the host lag). Update the rate comments.

### Task B2 — Confirm the client writes `Surface.rotation` from the wire each push  *(impure; no unit test)*

- [ ] Verify `ClientVehicleInterpolator.SetTarget`/`Tick` (from A3) sets `Surface.rotation = rot` (the
      wire quaternion) every placement — this is the flies-sideways fix. (No new code if A3 did it; this
      task is the explicit check + a DIAGB rot read-back if needed.)
- [ ] Build 0/0 + suite green. Commit `feat(replication): raise 0x35 continuous send/walk to ~15Hz + consume wire rotation (thin-client INC-B)`.

### Task B3 — IN-GAME GATE B  *(user runs)*

- [ ] **PASS:** craft nose points along travel direction (sideways bug gone); position steps visibly
      smaller/more frequent (~15/s) though still stepped (no interp yet). DIAGB shows ~15 continuous
      sends/sec for a moving craft. **Host shows NO co-op lag** (perf check — dirty-check skips idle
      craft). If lag → roll the rate back toward 10–12 Hz, re-test.

---

## INC-C — Snapshot-interpolation buffer (lerp + slerp) — DONE + deployed `8f1aaa8`

### Task C1 — Pure `SnapshotBuffer` / alpha+bracket math + tests  *(pure → TDD)*

- [ ] FAILING TESTS FIRST in `Multiplayer.Tests/SnapshotBufferTests.cs`:
  - (a) `Alpha(rt, t0, t1)` = `clamp01((rt−t0)/(t1−t0))`; endpoints → 0 / 1; rt outside → clamped;
        `t1==t0` → defined (return 1, no div-by-zero).
  - (b) bracket selection over a ring: given samples at increasing T, find `s0,s1` with `s0.T ≤ rt ≤ s1.T`;
        rt > newest → underrun (return newest index, hold flag); rt < oldest → overrun (return oldest).
  - (c) monotonic insert + ring eviction at capacity (oldest dropped); out-of-order/duplicate-T insert
        handled (drop stale).
  - Run filtered → FAIL.
- [ ] IMPL: repurpose `src/Network/CommandSync/InterpolationMath.cs` (now empty of native-equation math)
      with a PURE Unity-free `SnapshotBuffer` holding `double[] T` + parallel primitive pos/rot arrays
      (or a pure `struct Sample { double T; float Px,Py,Pz, Rx,Ry,Rz,Rw; }` ring) + `Insert`,
      `FindBracket(rt) -> (i0,i1,alpha,mode)` and the `Alpha` clamp. Keep it Unity-free (the `Vector3.Lerp`/
      `Quaternion.Slerp` themselves stay in the interpolator; this core returns indices + alpha).
- [ ] Link `InterpolationMath.cs` already in csproj; add the new test file (auto-globbed). Tests PASS.
- [ ] Commit `feat(replication): pure snapshot-buffer bracket+alpha math (TDD)`.

### Task C2 — Rewrite `ClientVehicleInterpolator` to the snapshot buffer  *(impure; no unit test — covered by C1)*

- [ ] In `ClientVehicleInterpolator.cs`: per-identity `Entry` holds a `SnapshotBuffer` (ring cap ~12) +
      `object Vehicle` + `bool Has`. `SetTarget(vehicle, identity, pos, rot, snap)`:
      snap → clear ring, insert one sample at `NowSeconds()`, place now (first mirror / arrival);
      else → INSERT `{NowSeconds(), pos, rot}` (arrival-time stamp, spec §4.3).
- [ ] `Tick(dt)` (client-only): `rt = GeoBridge.NowSeconds() − InterpDelaySeconds`; if NaN → hold last
      placed. Per entry: `FindBracket(rt)` → `pos = Vector3.Lerp(s0,s1,alpha)`,
      `rot = Quaternion.Slerp(s0,s1,alpha)`; underrun → hold newest. Place:
      `GeoBridge.PlaceGlobeIconAt(vehicle, pos)` + `Surface.rotation = rot`. Drop dead entries
      (destroyed-Object `== null`). Add consts `InterpDelaySeconds = 0.2f` (= 3× sendInterval(0.066) +
      jitter; spec §4.4 — the prior `0.15f` was only ~2.27× and risked underrun; tune DOWN in INC-D, not
      up), `RingCapacity = 12`.
- [ ] **LOCK the double-orientation-write order (spec §4.2 decision).** The placement does TWO
      orientation-touching ops: `PlaceGlobeIconAt`→`SetOrientedGlobeWorldPosition` (orients pivot to the
      globe tangent) AND `Surface.rotation = rot` (slerped wire quat). Order is critical — tangent-derive
      can clobber the wire quat → "flies-sideways". PICK + PIN one (gated by the INC-C in-game heading
      check), do NOT leave both as independent unordered writes:
  - **(A)** wire quat = FULL authoritative rotation → tangent-derive for POSITION only; ensure
        `Surface.rotation = rot` is the LAST/sole orientation writer (neutralize the placement's orientation
        side-effect).
  - **(B)** wire quat = nose heading composed OVER the tangent → derive tangent first, then compose
        (`tangent * rot` or `rot * tangent` — pin which); final `Surface.rotation` = tangent ∘ heading.
- [ ] Keep `Remove`/`Reset`.

### Task C3 — Route applies into the buffer (no direct placement)  *(impure; no unit test)*

- [ ] `GeoBridge.ApplyVehicleState` (from A4): for `SurfacePos`/`SurfaceRot` bits, call
      `ClientVehicleInterpolator.SetTarget(..., snap:false)` (buffer insert) — NOT a snap. Discrete ARRIVAL
      (`Travelling`→false or `CurrentSite` set) → `SetTarget(..., snap:true)` so the craft lands exactly.
- [ ] `ClientGeoStateApplier`: first mirror → `ApplyVehicleStateFull` then `SetTarget(snap:true)`.
- [ ] Build 0/0 + suite green. Commit `feat(replication): client snapshot-interpolation buffer (lerp pos + slerp rot, render ~150ms back) (thin-client INC-C)`.

### Task C4 — IN-GAME GATE C  *(user runs)*

- [ ] **PASS:** craft fly SMOOTHLY along the great-circle path at native-looking speed with correct
      heading (eye-indistinguishable from a host-side native flight) — no jerk, no overshoot; on arrival
      land exactly on the host site, no rubber-band. Client→host StartTravel input works. No console
      errors either instance.

---

## INC-D — Tune + edge cases + perf + DIAG strip — PARTIAL (3 visual fixes deployed; cleanup PENDING)

> **DONE so far (in-game-log RCA, deployed `5F7C2F0F`):** P1 jerk — `ClientVehicleInterpolator`
> InterpDelaySeconds 0.2→0.35 (`0a877b4`); P2 travel line — `ClientTravelEmitterSuppressPatch` carves out
> `set_Travelling` only during `EntityReplicationScope.IsApplying` so native `DrawVehiclePathLinks` draws,
> `DestinationSites` resolved to real `GeoSite` (`a118fa4`); P3 nose — new `GeoBridge.UpdateVehicleHeadingTowards`
> reuses native `GeoNavComponent.GetHeadingTowardsTarget`/`UpdateHeading` toward `DestinationSites[0].WorldPosition`,
> called in `Tick` after `PlaceGlobeIconAt`, dropped per-frame `SetSurfaceRotation` (`7636db4`).
> **STILL PENDING (next session):** D1 re-tune (the KNOWN-REMAINING speed mismatch — client flies slower /
> progressively behind — may be delay-related), D2 edge cases + `ResetState()`, D3 DIAG strip
> (`b753111`+`fbfb3f9`+DIAG/INC-C lines) + remove now-dead `InterpolationMath.cs` (+ its tests), D4 final gate.

### Task D1 — Tune rate + buffer  *(impure)*

- [ ] Try `FlushIntervalSeconds`/`SnapshotIntervalSeconds` at 20–30 Hz (0.05/0.033) + adjust
      `InterpDelaySeconds` (`≈ 3× sendInterval + jitter`) / `RingCapacity` for the smoothest motion at the
      lowest acceptable latency. Re-check host lag at each step (dirty-check must still skip idle craft).

### Task D2 — Edge cases  *(impure)*

- [ ] REROUTE mid-flight (discrete `DestinationSites` change): the buffer just keeps receiving new pos/rot
      → smooth re-target, no special handling needed; verify in-game.
- [ ] REMOVAL (`0x36 VehicleRemoved`): `ClientVehicleInterpolator.Remove(identity)` already wired; confirm
      the craft vanishes.
- [ ] PARKED craft: no continuous pushes (dirty-check skips) → buffer holds one sample → static at parked
      pos; verify.
- [ ] FIRST MIRROR: heavy `ApplyVehicleStateFull` + snap seeds the buffer exactly; verify the craft
      appears at the right spot.
- [ ] Add `ClientGeoStateApplier.ResetState()` (clear `_lastAppliedSeq`/`_firstMirrorDone`) and WIRE it in
      `NetworkEngine.Shutdown` next to `ClientVehicleInterpolator.Reset()` so a 2nd co-op session starts
      clean.

### Task D3 — Strip DIAG instrumentation  *(impure)*

- [ ] Remove ALL `[DIAGB]`/`DIAG3` logging: `GeoStateSyncBroadcaster` (snapshot summary + per-send lines +
      `_diagLogAccum`), `ClientGeoStateApplier` (per-apply read-back + `_diagbApplyNextLogTime` +
      `_diag3Unresolved`), `NetworkEngine.cs` `0x35` boundary log (`cs:626-636`). Keep the all-factions
      `DescribeVehicles` only if a permanent diag is wanted (else strip).
- [ ] Build 0/0 + FULL suite green after the strip.
- [ ] Commit `chore(diag): strip thin-client vehicle-sync DIAG instrumentation (gate passed)`.

### Task D4 — IN-GAME GATE D (final)  *(user runs)*

- [ ] **PASS:** all edge cases correct (reroute smooth; removed craft gone; parked static; first-mirror
      exact); host steady at the chosen rate with no lag over a long session; no console errors either
      instance; clean build + suite green post-strip. Record the outcome to `docs/research/00-current-state.md`
      via SCRIBE.

---

## Out of scope (do NOT build here)

- Client-side PREDICTION (ruled out — not twitch-controlled).
- Host-time-in-packet timestamping (spec §2 fallback) — only if arrival-time jitter proves visible.
- INC-3b/c scopes (Site / MarketPrice / FactionTraffic / FactionState) — separate `0x35` slices.
- CRC divergence backstop (INC-5).
