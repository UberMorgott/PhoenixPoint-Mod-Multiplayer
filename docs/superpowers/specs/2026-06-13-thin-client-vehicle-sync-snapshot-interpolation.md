# Thin-Client Vehicle Sync — Snapshot-Interpolation Re-Architecture (Design Spec)

> Re-architects the CLIENT half of the `0x35 GeoStateDiff` vehicle mirror from the current
> HALF-AUTHORITATVE render (client re-runs the native travel motion — SWITCH-A `NavigateRoutine`
> driver + the great-circle `SegmentNum` equation) to a **host-authoritative THIN CLIENT**: the
> client vehicle sim stays FULLY FROZEN, the host streams the authoritative transform at a moderate
> fixed rate, and the client renders by **snapshot interpolation** (render ~150–200 ms in the past,
> lerp position + slerp orientation between two received snapshots). No client prediction.
>
> - **Status:** design spec (decompile + live-code grounded). Architecture decision is LOCKED (see §1);
>   this spec is the implementation contract for the increment plan.
> - **BUILD STATUS (2026-06-13 EOD — deployed DLL `5F7C2F0F`, 193 xUnit green, build 0/0, wire/codec UNCHANGED):**
>   INC-A (`069c1ce`), INC-B (`e3f0bcb`), INC-C (`8f1aaa8`) DONE + deployed. TFTV-compat fix `954b61a`.
>   3 in-game-log-RCA'd VISUAL fixes DONE + deployed (commit subjects tagged "INC-D"): P1 jerk —
>   `ClientVehicleInterpolator` InterpDelaySeconds 0.2→0.35 (`0a877b4`); P2 travel line — un-suppress
>   mirrored `set_Travelling` during apply so native `DrawVehiclePathLinks` draws (`a118fa4`); P3 nose —
>   derive heading via native `GeoNavComponent.GetHeadingTowardsTarget`/`UpdateHeading` toward
>   `DestinationSites[0]`, drop per-frame SetSurfaceRotation (`7636db4`). IN-GAME GATE (deployed
>   `5F7C2F0F`): sync both directions ✓, travel line ✓, nose-along-travel ✓, smoothness improved.
>   **KNOWN REMAINING (next session):** (1) client craft flies SLOWER than host — apparent SPEED
>   mismatch (client progressively behind, not a constant delay); (2) still not perfectly as smooth as
>   host. Likely interp cadence / snapshot inter-arrival vs render-rate — investigate from log next
>   session. **INC-D cleanup still PENDING (next session):** strip temp DIAG (`b753111`+`fbfb3f9`+
>   DIAG/INC-C lines), remove now-dead `InterpolationMath.cs` (+ its tests), re-tune InterpDelay if the
>   speed issue is delay-related.
> - **Parent arc:** SD-AIDR (`docs/superpowers/specs/2026-06-13-coop-state-replication-design.md`),
>   INC-3 (`…-coop-state-replication-inc3-geostatediff.md`), INC-3a (the `0x35` slice already built).
> - **Companion plan:** `docs/superpowers/plans/2026-06-13-thin-client-vehicle-sync-increments.md`.
> - **Why:** 3+ in-game attempts at re-running native motion on the client produced a SECOND writer
>   fighting the mirror → jerk (2 Hz step-teleport) + flies-sideways (heading never slerped from the
>   wire). Internet research: a closed, non-deterministic, Harmony-only engine rules out deterministic
>   lockstep; the aircraft is not twitch-controlled, so client-side prediction is unnecessary. The
>   correct model is **single-writer host-authoritative + entity interpolation**.

---

## 1. Goal + single-writer principle

- **Goal:** every faction's geoscape craft renders on the client as a smooth, native-looking flight
  with the correct nose heading and globe-tangent orientation, driven SOLELY by host-streamed
  transforms — no client motion simulation, no second writer.
- **Single-writer principle (the core rule):** on the client, the vehicle's `Surface` transform
  (position + rotation) and on-globe icon pivot are written by EXACTLY ONE code path — the snapshot
  interpolator (`ClientVehicleInterpolator`). The native movement writer (`GeoNavComponent.NavigateRoutine`)
  must NEVER run for a replicated vehicle, and no mod code writes the transform outside the interpolator.
- **Stream FULL transform, continuously:** world position (`SurfacePos`) AND orientation (`SurfaceRot`
  = the in-plane nose heading; the globe-tangent pivot is re-derived on the client from world position
  via the native primitive). Both already ride the CONTINUOUS channel in the wire format (§2) — the
  gap is the SEND RATE (currently 2 Hz flush) and the CLIENT consuming rotation via slerp instead of
  the native routine.
- **No extrapolation:** the client renders in the past (render-time = slaved-clock-now − interpDelay)
  and interpolates between the two bracketing snapshots; on buffer underrun it HOLDS the last snapshot,
  never predicting forward.

### KEEP (verified in current tree — do NOT touch the wire/foundation)

| Component | File | Commit | Note |
|---|---|---|---|
| Scope enum | `src/Network/CommandSync/GeoStateScope.cs` | `6f8ff76` | stable bytes; unchanged |
| Wire codec + record structs + mask | `src/Network/CommandSync/GeoStateDiffCodec.cs` | `eefea25`,`e0afc01` | **rotation `SurfaceRot` bit1 already present** |
| Diff/seq core | `src/Network/CommandSync/GeoVehicleStateDiffer.cs` | `3033987` | `SurfaceRot` already in `ContinuousMask` |
| Host broadcaster (structure + **cheap dirty-check perf fix**) | `src/Network/CommandSync/GeoStateSyncBroadcaster.cs` | `25f1b64`,`10ee46c` | KEEP; only RETUNE `FlushIntervalSeconds` (§3) |
| Host snapshot / heavy apply / resolver / placement / clock | `src/Network/CommandSync/GeoBridge.cs` `RecordVehicleState:205`, `ApplyVehicleStateFull:466`, `FindVehicleByFactionAndId:175`, `PlaceGlobeIconAt:534`, `NowSeconds:551` | `2bf8633`,`31217ab`,`db48a9b` | KEEP |
| Client freeze — geo producers | `src/Harmony/ClientGeoSimSuppressPatch.cs` | INC-1 | KEEP |
| Client freeze — travel emitters | `src/Harmony/ClientTravelEmitterSuppressPatch.cs` | INC-1 | KEEP |
| Slaved clock (timestamp source) | `src/Network/CommandSync/ClientTimeMirror.cs`, `TimeBridge.cs` (`GetTiming:37`) | `0x34` | KEEP |
| `0x35` packet wiring | `src/Network/NetworkEngine.cs` `BroadcastGeoStateDiff:356`, `RouteMessage case:625`, `Update tick:373` | `341b089` | KEEP |
| Client→host input relay | `src/Harmony/StartTravelInterceptPatch.cs` `Prefix` | C7 | KEEP (Postfix already retired `ea880b0`) |
| Identity `(Def.Guid, VehicleID)` + transport (`DirectTransport`/`CompositeTransport`) | — | INC-3a | KEEP |

### SCRAP / REPLACE

| Component | File / locus | Commit | Action |
|---|---|---|---|
| **SWITCH-A native travel driver** | `src/Network/CommandSync/ClientNativeTravelDriver.cs` (whole file) | `fd45252` | **DELETE.** The client running native `StartTravel`→`NavigateRoutine` is the second writer. |
| **SWITCH-A intercept carve-out** | `StartTravelInterceptPatch.Prefix` line `if (EntityReplicationScope.IsApplying) return true;` | `fd45252` | **REMOVE** that one line (keep the `CommandRelay.IsApplying` host gate + the client→host relay). |
| **Native-equation SEGMENT render + EASE render** | `ClientVehicleInterpolator.cs` (`SegActive`/`StartSegment`/`CorrectionSample`/`EndSegmentSnap`/EASE `K`) | `55b9f16`,`fa058f0` | **REPLACE** the internals with a timestamped snapshot ring buffer (§4). Keep the class name + `Tick`/`Remove`/`Reset` shell + its `NetworkEngine.Update`/`Shutdown` wiring. |
| **Native travel-equation math** | `InterpolationMath.cs` (`GreatCircleAngleRad`/`SegmentTotalSeconds`/`SegmentNum`/`ArcRatioFromAngles`/`CorrectedStartSec`/`EarthRadiusKm`) | `55b9f16` | **SCRAP** those members. Repurpose the file to the pure interpolation alpha-clamp + (new) `SnapshotBuffer` math (§4, §8). `SmoothFactor` not used (snapshot interp is linear-in-time, not exponential ease). |
| **Direct transform write + segment routing in light apply** | `GeoBridge.ApplyVehicleState:323` (`surface.position`/`surface.rotation` writes + `RouteVehicleRender:412` call) and `RouteVehicleRender` itself | `db48a9b`,`fa058f0` | **REPLACE:** light apply STOPS writing `Surface.position/rotation` directly for render; instead it pushes `{pos, rot, t}` into the interpolator buffer. Discrete/state fields (Travelling/CurrentSite/Dest/Range/HP) keep their direct setters. `RouteVehicleRender` deleted. |
| `IsNativeTravelling` gate + the per-identity native skip in `ApplyVehicleState`/`ClientVehicleInterpolator.Tick` | call sites of `ClientNativeTravelDriver.IsNativeTravelling` | `fd45252` | **REMOVE** (the driver is gone; there is no native render to skip). |
| Temp DIAGB instrumentation | `GeoStateSyncBroadcaster` `[DIAGB]`, `ClientGeoStateApplier` `[DIAGB]`/`DIAG3`, `NetworkEngine` `0x35` boundary log, native-travel logs | `ec19a41`,`a94e4c6`,`7131fef` | KEEP until the final gate (INC-D), then strip (§8). |

---

## 2. Wire format — rotation is ALREADY continuous (minimal change)

**Finding (corrects the research premise):** the `0x35` record ALREADY carries `SurfaceRot` (bit1
`GeoStateMask.SurfaceRot`, `GeoStateDiffCodec.cs:14`) as a full quaternion `[float x,y,z,w]`, and the
differ ALREADY classifies it CONTINUOUS (`GeoVehicleStateDiffer.ContinuousMask =
SurfacePos|SurfaceRot|RangeRemaining`, `GeoVehicleStateDiffer.cs`). So orientation is streamed on the
SAME unreliable channel as position whenever it changes beyond `Epsilon=0.01`. The "rotation only on
discrete records" / "position-only streaming" bug was a CLIENT-CONSUME defect (the native routine /
segment render owned the heading and `ApplyVehicleState` SKIPPED the `surface.rotation` write while
native-travelling), not a wire defect.

- **Wire change: NONE required for orientation.** The compact transform is `[float×3 pos][float×4 rot]`
  = 28 bytes per moving vehicle, mask-gated, already optimal (quaternion, not full `InstanceData`).
  Bandwidth at 15 Hz for one moving craft ≈ 28 B × 15 ≈ 420 B/s + ~17 B header (scope/seq/guid/id/mask)
  → ~670 B/s; ten simultaneous movers ≈ 6.7 KB/s. Well under MTU per envelope (records are batched).
- **Timestamp: client arrival-time stamping — NO wire widening** (decision §4.3). The record carries
  no host time today; rather than widen it, the client stamps each ingested continuous snapshot with
  its slaved-clock now (`GeoBridge.NowSeconds()`), justified in §4.3.
- **Quaternion vs heading float:** KEEP the quaternion. `Surface.rotation` is a full `Quaternion`
  (`GeoVehicle.cs:1090`); slerp on the quaternion reproduces both the nose heading and any banking with
  no decode ambiguity, for 8 extra bytes vs a single heading float. Not worth a lossy heading-only
  encoding.

If, after INC-D in-game, arrival-time jitter proves visible (§7), the fallback is to add one
`[double hostNowSec]` field to the continuous record body (a new `GeoStateMask` bit or a format-version
bump) and switch the buffer to host-time stamps — backward-safe within our own host+client (we control
both ends; no external compat).

---

## 3. Host side — raise the continuous send rate, preserve the perf fix

Current cadence (`GeoStateSyncBroadcaster.cs`):

- `SnapshotIntervalSeconds = 0.1f` → the faction×vehicle snapshot/diff WALK runs at **10 Hz** (the
  cheap-dirty-check perf throttle, `10ee46c`, that killed the host co-op lag — must be PRESERVED).
- `FlushIntervalSeconds = 0.5f` → the CONTINUOUS unreliable pos/rot/range envelope flushes at only
  **2 Hz**. **This 2 Hz flush is the jerk / step-teleport cause** — the client receives a new
  position only every 500 ms.

**Change:**

- Set `FlushIntervalSeconds` to **~0.066f (15 Hz)** as the moving-vehicle continuous send rate (target
  15–30 Hz; start at 15, tune to 20–30 in INC-D if bandwidth allows). The discrete RELIABLE channel is
  unchanged (immediate on transition).
- **Keep `SnapshotIntervalSeconds` decoupled and ≤ the flush interval** so every flush has fresh data.
  Set `SnapshotIntervalSeconds = 0.066f` too (15 Hz walk) — the cheap dirty pre-check
  (`GeoBridge.TryGetCheapVehicleSignature` + `_lastSig` + `SigChanged`) means an IDLE steady-state
  vehicle still does ZERO expensive `RecordVehicleState` calls, so raising the walk to 15 Hz does NOT
  reintroduce the lag (the lag came from running the EXPENSIVE native record at 60 Hz for every vehicle;
  the dirty-check skips it entirely when nothing moved). Only ACTIVELY-MOVING craft pay the record cost,
  and there are few of those at once.
- **Coherence rule:** `FlushIntervalSeconds` should be an integer multiple of (or equal to)
  `SnapshotIntervalSeconds` so the two cadences stay phase-locked (the existing comment already notes
  this). At 15/15 Hz they are equal.
- Rotation is already SAMPLED + DIFFED + SENT continuously (§2) — no host change needed beyond the rate.
- **Perf guard to PRESERVE verbatim:** the `_lastSig`/`SigChanged` cheap pre-check (skips the expensive
  record when the cheap managed signature is unchanged within `Epsilon`), the `_continuousPending`
  latest-wins coalescing buffer, the host-only gate, and the `EntityReplicationScope/CommandRelay`
  IsApplying skip. None of these change.

**Bandwidth sanity at the new rate:** §2 — single mover ≈ 0.67 KB/s, ten movers ≈ 6.7 KB/s on the
unreliable channel; acceptable. Cap-per-envelope / split-across-frames stays an open item (§7) only if
many alien craft move at once.

---

## 4. Client side — snapshot-interpolation buffer

The client becomes a pure entity-interpolation renderer. `ClientGeoStateApplier.Apply` no longer writes
the render transform directly; it (a) seq-guards, (b) resolves by `(faction,id)`, (c) applies the
DISCRETE/state fields directly (as today), and (d) pushes the continuous `{pos, rot}` sample into the
per-identity snapshot buffer. Each frame `ClientVehicleInterpolator.Tick` renders every tracked vehicle
from its buffer.

### 4.1 Buffer data structure (per identity `(FactionGuid, VehicleID)`)

- A ring of timestamped samples: `struct Snapshot { double T; Vector3 Pos; Quaternion Rot; }`.
- Per-identity `Entry`: ring buffer (capacity ~12), the latest-applied state, and a "parked" flag.
- `T` = the client slaved-clock seconds (`GeoBridge.NowSeconds()`) AT INGEST (arrival-time stamp, §4.3).
- Stored in `Dictionary<(string,int), Entry>` (reuse the existing `_tracked` dict shell; no per-frame
  alloc — ring slots reused in place).

### 4.2 Per-frame interpolation (`Tick(float dt)`, client-only)

1. `renderTime = NowSeconds() − InterpDelaySeconds` (the slaved clock; if NaN → hold last placed pos).
2. For each tracked `Entry`, find the two bracketing snapshots `s0,s1` with `s0.T ≤ renderTime ≤ s1.T`.
3. `alpha = clamp01((renderTime − s0.T) / (s1.T − s0.T))` (the pure, testable part — §8).
4. `pos = Vector3.Lerp(s0.Pos, s1.Pos, alpha)` ; `rot = Quaternion.Slerp(s0.Rot, s1.Rot, alpha)`.
5. Place the icon: `GeoBridge.PlaceGlobeIconAt(vehicle, pos)` (native
   `GeoActor.SetOrientedGlobeWorldPosition` → globe-tangent pivot from world pos) AND set the in-plane
   nose heading `Surface.rotation = rot` (the heading the native routine used to set; this is the
   "flies-sideways" fix — heading now comes from the slerped wire quaternion).
6. **Underrun (renderTime > newest `T`):** HOLD the newest snapshot (place at newest pos/rot). Never
   extrapolate.
7. **Overrun (renderTime < oldest `T`, only right after first sample):** clamp to oldest.

**DECISION TO LOCK at INC-C — double orientation write (step 5 has TWO orientation-touching ops).**
Step 5 calls `PlaceGlobeIconAt` (→ `SetOrientedGlobeWorldPosition`, which itself orients the pivot to the
globe tangent) AND writes `Surface.rotation = rot` (the slerped wire quaternion). Two writers inside the
"single writer" → call-order-critical: the tangent-derive could clobber the wire quaternion, or vice-versa,
reintroducing the "flies-sideways" bug. The spec's intent (§1) is "wire quat = in-plane nose heading; the
globe-tangent pivot is re-derived from world pos" — i.e. option (B) below — but step 5 writes the two as
independent statements with NO defined composition order. INC-C MUST resolve which of:
- **(A) wire quaternion = the FULL authoritative rotation.** Then the tangent-derive is for POSITION
  (icon placement) ONLY and must NOT write orientation — `SetOrientedGlobeWorldPosition` orientation side-
  effect must be neutralized/overwritten so `Surface.rotation = rot` is the last/sole orientation writer.
- **(B) wire quaternion = nose heading composed OVER the globe tangent.** Then define a STRICT composition
  order: derive the tangent pivot first, then compose the wire heading on top (e.g. `tangent * rot` or
  `rot * tangent` — pin which), so the final `Surface.rotation` is `tangent ∘ heading`, deterministic.
Do NOT silently pick at design time — INC-C in-game gate (heading correct, no sideways) selects + pins it.

### 4.3 Timestamp choice — client arrival-time stamping (justified)

- **Decision:** stamp each continuous snapshot with the client's slaved-clock now at INGEST; render at
  `now − InterpDelay`. **No wire change.**
- **Why this over host-time / seq+interval:**
  - *Host time in the packet* would be exact but widens the wire and adds a clock-rebase; deferred to the
    §7 fallback only if jitter shows.
  - *seq+interval* (reconstruct `T = seq × flushInterval`) breaks if the host coalesces/skips a flush
    (latest-wins drops intermediate seqs) → non-uniform spacing mis-timed.
  - *Arrival time* is the standard Valve entity-interpolation approach. Its robustness needs NO clock
    agreement with the host: ingest is stamped with the client's OWN clock and render is `that-same-clock
    now − InterpDelay`, so the timeline is SELF-CONSISTENT (one clock on both ends of the stamp→render
    pair) — host/client clock drift cannot mistime playback. Uniform playback then needs only that the
    INPUT spacing be roughly uniform, which holds because the host flushes the continuous channel on a
    FIXED interval (§3); `InterpDelay ≈ 3× sendInterval` (§4.4) absorbs the residual network jitter. The
    craft is strategic/slow, so sub-frame timestamp error is imperceptible.
    - *(The host-slaved clock (±0.5 s) is NOT what makes arrival-time work — see above. It matters only
      for the §2/§7 HOST-TIME FALLBACK, where ingest would be stamped with host time and a slaved clock is
      needed to rebase host stamps into the client timeline.)*
- The DISCRETE reliable channel (arrival/departure) does NOT go through the buffer — it applies
  immediately (§4.4), so exact transitions are never delayed by `InterpDelay`.

### 4.4 Buffer values for the tuning

- `InterpDelaySeconds ≈ 0.2` (= 3 × sendInterval(0.066) + jitter; render ~200 ms back). NOTE: the prior
  `0.15` was only ~2.27× sendInterval (`0.15/0.066≈2.27`), inconsistent with the stated 3× rationale and
  at risk of rare buffer underrun (micro-stutter). Start at `0.2` (3×); tune DOWN in INC-D if latency
  proves visible (floor ≈ 1× sendInterval + jitter), NOT up.
- Ring capacity `≈ 12` (≈ 0.8 s of history at 15 Hz — ample bracket coverage + headroom).
- These are CONSTS in `ClientVehicleInterpolator`, tuned in INC-D against the host rate.

### 4.5 Snap / arrival / parked / removal / reset

- **First mirror (heavy):** `ClientGeoStateApplier` still calls `GeoBridge.ApplyVehicleStateFull`
  (`ProcessInstanceData`) once per identity to fill unsynced fields, THEN seeds the buffer with a single
  snapshot at the current pos/rot and places the icon NOW (snap) so the craft appears exactly.
- **Discrete ARRIVAL (`Travelling`→false or `CurrentSite` set):** apply the state fields directly
  (`ApplyVehicleState`), then SNAP the buffer to the arrival pos/rot (clear ring, seed one sample, place
  now) so the craft lands exactly on the host site with zero interpolation lag. No native cancel needed
  (no native routine runs).
- **Parked vehicle:** no continuous pushes arrive (dirty-check skips it host-side) → buffer holds one
  sample → renders static at the parked pos. Correct, alloc-free.
- **Removal (`0x36 VehicleRemoved`):** `ClientVehicleInterpolator.Remove(identity)` (already wired);
  drop the `ClientNativeTravelDriver.OnRemoved` call (driver deleted).
- **Reset (session boundary, `NetworkEngine.Shutdown`):** `ClientVehicleInterpolator.Reset()` (already
  wired at `NetworkEngine.cs:117`); drop the `ClientNativeTravelDriver.Reset()` call (`:120`, driver
  deleted). Also clear `ClientGeoStateApplier._lastAppliedSeq/_firstMirrorDone` on reset (add a
  `ResetState()` and wire it next to the interpolator reset) so a 2nd co-op session starts clean.

---

## 5. Client-freeze completeness (research's #1 unknown — INC-A must VERIFY)

The single-writer guarantee depends on NO native code moving the replicated transform once we delete the
SWITCH-A driver. Audit of writers:

- **`GeoNavComponent.NavigateRoutine`** (per-frame `Surface.position/rotation` + pivot, `GeoNavComponent.cs:117-124`):
  scheduled ONLY by `StartTravel → Navigate → StartNavigation` (`NavigationComponent.cs:177-180`). After
  deleting `ClientNativeTravelDriver`, the client NEVER calls `StartTravel` on a replicated craft (the
  SWITCH-A carve-out in `StartTravelInterceptPatch.Prefix` is removed; the only remaining client
  `StartTravel` is a PLAYER input, which is relayed to the host and blocked locally). → routine never
  scheduled on the client → **not a writer.** Suspicion to confirm in-game: a routine left running from
  BEFORE the freeze engaged.
- **The 13 geo producers** (`ClientGeoSimSuppressPatch`): return `NextUpdate.Never` on the client → no
  stochastic/clock-driven sim. Unchanged.
- **The 3 travel emitters** (`ClientTravelEmitterSuppressPatch`): `set_Travelling`/`InitiateTravelling`/
  `OnArrived` suppressed unconditionally on the client → no authority side-effects. Unchanged.
- **Mod writers:** after the §4 change, only `ClientVehicleInterpolator.Tick` writes
  `Surface.position/rotation` + pivot. `ApplyVehicleState` no longer writes the render transform (only
  state fields).

**INC-A verification (the gate for this unknown):** freeze the client, place the craft at the host's
last pushed pos via mirror-only placement (no interpolation yet), and confirm over several seconds with
the host PAUSED that the client craft does NOT drift, teleport, or re-orient on its own. If it moves → a
residual native writer exists (find + suppress it before proceeding). Single writer proven = mirror is
the sole writer.

**GATE-ZERO — the host→client stream must be proven to EXIST before it can be proven SMOOTH.** This
whole spec premises that host→client APPLIES ARRIVE and the only defect is jerkiness (2 Hz step-teleport,
§3) — i.e. a smoothing problem. If instead the live symptom is asymmetric ("host acts → client shows
NOTHING; client acts → host syncs"), then NO stream exists to smooth and INC-B/C/D build on a non-existent
foundation. INC-A MUST first instrument the END-TO-END tract and assert, IN ORDER:
  1. host EMITS `0x35` for a moving vehicle (broadcaster send line, moving craft);
  2. client RECEIVES the packet (transport recv);
  3. `NetworkEngine.RouteMessage` ENTERS `case 0x35` (`:625`);
  4. the seq-guard does NOT drop it (and does not wrongly drop a discrete vs continuous — see §7 caveat);
  5. `(factionGuid, VehicleID)` RESOLVES to a real client object (`FindVehicleByFactionAndId != null`);
  6. apply WRITES position (light-apply / interpolator placement runs);
  7. read-back AFTER apply == host position.
**If applies don't arrive/resolve at any step, FIX THAT FIRST — do NOT proceed to INC-B/C/D.** Stream-
exists-but-jerky and stream-absent are different bugs; the latter is a build-from-scratch / regression
hunt, the former is smoothing-only.

- **[ANSWERED — verified in-game on deployed build `fd45252` ("SWITCH-A"), fresh log evidence]** The
  stream EXISTS and is jerky-class, NOT absent. The `0x35` continuous vehicle-state stream is emitted by
  the host AND applied by the client: host seq is MONOTONIC per `(FactionGuid, VehicleID)`, never reset;
  the client applies EVERY `0x35` continuous record; ZERO drops / ZERO exceptions. → The break is NOT a
  missing stream and NOT a seq-drop. The break is (A) the SWITCH-A render-side carve-out and (B) a CLIENT
  SECOND-WRITER — both introduced by SWITCH-A (`fd45252`). See the Verified RCA sub-block in §6 INC-A.
- **[HYPOTHESIS to check at gate-zero — over-aggressive freeze] — REFUTED (`fd45252` log).** The INC-1
  freeze (13 producers→
  `NextUpdate.Never` via `ClientGeoSimSuppressPatch`; 3 emitters via `ClientTravelEmitterSuppressPatch`)
  may suppress not only simulation but also client-side vehicle CREATION/init, so the host-owned craft is
  never instantiated on the frozen client → step 5 `FindVehicleByFactionAndId` returns null and apply
  silently no-ops (a bug class that breaks ONLY host→client, leaving client→host "working"). VERIFY: does
  the frozen client EVER instantiate the host-owned vehicle object, and is `FindVehicleByFactionAndId`
  ever non-null for it? **REFUTED:** the craft IS instantiated and resolves — the client applies every
  `0x35` record (zero unresolved), so the freeze does NOT block creation/registration. *(Serena-confirmed scope: the two named freeze patches target ONLY the
  GeoSimProducer `NextUpdate(Timing)` callbacks and `set_Travelling`/`InitiateTravelling`/`OnArrived` —
  they do NOT patch `PlaceGlobeIconAt`/`SetOrientedGlobeWorldPosition` or any Instantiate. So the icon-
  placement primitive itself is unpatched; the open risk is whether a now-frozen sim PRODUCER was what
  used to create/register the craft on the client. Hypothesis, not confirmed cause.)*
- **[HYPOTHESIS to check at gate-zero — systemic break] — REFUTED (`fd45252` log).** Confirm host→client
  delivers for at least ONE
  non-vehicle scope (Site / MarketPrice / FactionTraffic / FactionState — the out-of-scope `0x35` slices
  per the plan). If host→client is dead for those too, the break is SYSTEMIC (broadcast/apply tract or
  scope-routing), and vehicle re-architecture is NOT the fix — escalate. **REFUTED:** the vehicle stream
  itself delivers + applies cleanly (monotonic seq, zero drops), so the apply tract is NOT systemically
  dead — the defect is SWITCH-A-local (render carve-out + second writer), not broadcast/scope-routing.

- **Hypothesis status (post-RCA, `fd45252`):** H1 REFUTED (over-aggressive freeze — craft instantiates +
  resolves), H2 latent-risk (shared monotonic seq across reliable/continuous, §7 caveat — NOT yet
  observed dropping a legitimate msg, keep watching), H3 REFUTED (systemic apply-tract break — stream
  applies cleanly), H4 CONFIRMED (SWITCH-A second writer + render carve-out is the actual break — see §6
  INC-A Verified RCA).

---

## 6. Increments (each build+test+deploy+in-game verifiable; user tests between)

Detailed task breakdown lives in the companion plan. Summary + PASS criteria:

- **INC-A — retire the second writer; client frozen; mirror-only placement. SCOPE: covers SYMPTOM A AND
  SYMPTOM B (both are SWITCH-A regressions — see Verified RCA below). — DONE + deployed `069c1ce`.**
  - **GATE-ZERO — DONE (verified `fd45252`):** the §5 ordered host→client tract is PROVEN — host emits,
    client applies EVERY `0x35` record, monotonic per-identity seq, ZERO drops / ZERO exceptions. §5 OPEN
    QUESTION resolved: stream EXISTS (jerky-class), NOT absent. The break is SWITCH-A-local, not a missing
    stream or seq-drop — proceed to the code edits.
  - **Verified RCA (`fd45252`) — root cause file:line:**
    - **SYMPTOM A** (host-initiated travel: 1st leg synced, 2nd+ leg frozen on the client then
      teleport-on-arrival) — render feeds DISABLED + delegated to a client-run native routine that never
      advances during host travel:
      - `src/Network/CommandSync/GeoBridge.cs:351-360` `ApplyVehicleState` — when `IsNativeTravelling`,
        SKIPS the `surface.position` write AND `RouteVehicleRender` → the continuous `0x35` pos is
        DISCARDED.
      - `src/Network/CommandSync/ClientVehicleInterpolator.cs:213-214` `Tick` — `if (IsNativeTravelling)
        continue;` SKIPS icon placement.
      - `src/Network/CommandSync/ClientNativeTravelDriver.cs:97-101` `OnRecord` → `GeoBridge.StartVehicleTravel`
        — the replacement driver does NOT advance the client globe icon during host travel → the icon only
        JUMPS at discrete arrival.
      - *Aggravator (NOT a bug):* the host event-popup PAUSE gaps the slaved clock; on pause the craft
        CORRECTLY stands still once it is mirror-only.
    - **SYMPTOM B** (client-initiated travel: flies on the client AND host, then on arrival the CLIENT ship
      snaps BACK to origin while the host stays at the destination) — TWO writers on the client for its own
      ship:
      - `src/Harmony/StartTravelInterceptPatch.cs:38` `Prefix` — the host-approved command RE-EXECUTES the
        real `StartTravel` on the client via `CommandRelay.IsApplying`, spawning a native routine NOT
        registered in `ClientNativeTravelDriver` → `IsNativeTravelling=false` → the `0x35` mirror ALSO
        drives Surface/interpolator → TWO writers → flies on the client.
      - `src/Harmony/ClientTravelEmitterSuppressPatch.cs:54-60` — unconditionally suppresses
        `set_Travelling` / `InitiateTravelling` / `OnArrived` on the client → the command-started native
        routine CAN'T finalize at the destination → on routine end the icon reverts to the stale
        `CurrentSite` (= origin) → snap-back.
  - Files: DELETE `ClientNativeTravelDriver.cs`; remove the `EntityReplicationScope.IsApplying` carve-out
    in `StartTravelInterceptPatch.Prefix`; gut `ClientVehicleInterpolator` to a plain "place at latest
    host pos (snap each apply)" stub (no segment/ease); in `GeoBridge.ApplyVehicleState` remove the
    `Surface.position/rotation` direct render writes + `IsNativeTravelling` gate + `RouteVehicleRender`
    call, replacing render with a single `PlaceGlobeIconAt(latest pos)` + `Surface.rotation = latest`;
    delete `RouteVehicleRender`; drop the driver Reset/Remove calls in `NetworkEngine.Shutdown`.
  - **PASS (in-game gate — covers A and B):**
    - **A (host-initiated):** the host-driven craft on the CLIENT follows the host CONTINUOUSLY (NO
      freeze-then-teleport). May be jerky/stepped (interpolation is INC-C) but NEVER frozen-then-teleport
      and NEVER self-drifts. With the host PAUSED → the client craft stands still (full freeze, single
      writer).
    - **B (client-initiated):** the client-initiated craft does NOT snap back to origin on arrival; it
      STAYS at the destination (a mirror of the host).
    - **General:** NO craft ever moves on the client from a local native routine — only from applied host
      state.
    - DIAGB shows applies arriving (seq>1) and the post-apply read-back pos == host pos; departure/arrival
      land on the correct site. (Proves single-writer + freeze complete.)

- **INC-B — raise send rate + consume rotation. — DONE + deployed `e3f0bcb`.**
  - Files: `GeoStateSyncBroadcaster` `FlushIntervalSeconds`→0.066, `SnapshotIntervalSeconds`→0.066;
    confirm `ApplyVehicleState`/interpolator sets `Surface.rotation` from the wire quaternion each push.
  - **PASS:** craft nose points along the travel direction (sideways bug gone) and the position steps are
    visibly smaller/more frequent (~15/s) though still stepped (no interpolation yet). DIAGB shows ~15
    continuous sends/sec for a moving craft; host shows NO co-op lag (perf check — dirty-check still
    skips idle craft).

- **INC-C — snapshot-interpolation buffer (lerp + slerp). — DONE + deployed `8f1aaa8`.**
  - Files: rewrite `ClientVehicleInterpolator` internals to the §4 ring buffer + per-frame
    bracket/lerp/slerp at `now − InterpDelay`; `ClientGeoStateApplier`/`ApplyVehicleState` push
    `{pos,rot,t}` into the buffer instead of placing directly; new pure `SnapshotBuffer` alpha/bracket
    math in `InterpolationMath` (unit-tested); discrete arrival snaps the buffer.
  - **PASS:** craft fly SMOOTHLY along the great-circle path at native-looking speed with correct heading
    (indistinguishable from a host-side native flight to the eye), no jerk, no overshoot; on arrival they
    land exactly on the host site with no rubber-band. Client→host StartTravel input still works.

- **INC-D — tune + edge cases + perf + DIAG strip. — PARTIAL: 3 VISUAL FIXES DONE + deployed
  (`0a877b4` P1 jerk/interp-delay 0.2→0.35, `a118fa4` P2 travel line, `7636db4` P3 nose-along-travel);
  cleanup (DIAG strip + dead-code removal + re-tune) STILL PENDING next session.**
  - Files: tune `FlushIntervalSeconds`/`SnapshotIntervalSeconds` (try 20–30 Hz) + `InterpDelaySeconds`/
    ring capacity; verify arrival / removal / reroute (multi-hop) / parked / first-mirror; add
    `ClientGeoStateApplier.ResetState()` + wire to `Shutdown`; strip ALL DIAGB/DIAG3 instrumentation.
  - **DONE so far (in-game-log RCA, single-writer preserved, native rendering reused):**
    - P1 jerk `0a877b4`: `ClientVehicleInterpolator` InterpDelaySeconds 0.2→0.35 (buffer was Hold-with-
      full-depth: renderTime=now−0.2s overran newest sample; real inter-arrival jitter >0.2s).
    - P2 travel line `a118fa4`: `ClientTravelEmitterSuppressPatch` carves out `set_Travelling` ONLY
      during mirror apply (`EntityReplicationScope.IsApplying`) — was unconditionally suppressed →
      client `Travelling=false` → native `GeoscapeView.DrawVehiclePathLinks` never drew. InitiateTravelling/
      OnArrived stay suppressed. `DestinationSites` resolved to real `GeoSite` in `ApplyVehicleState`.
    - P3 nose `7636db4`: new `GeoBridge.UpdateVehicleHeadingTowards` reuses native
      `GeoNavComponent.GetHeadingTowardsTarget`/`UpdateHeading` (reflection) toward
      `DestinationSites[0].WorldPosition`, called in `ClientVehicleInterpolator.Tick` after
      `PlaceGlobeIconAt`; dropped per-frame `SetSurfaceRotation`. No `NavigateRoutine` (the scrapped
      `fd45252` 2nd writer).
  - **STILL PENDING (next session):** strip temp DIAG (`b753111`+`fbfb3f9`+DIAG/INC-C lines), remove
    now-dead `InterpolationMath.cs` (+ its tests), re-tune `InterpDelaySeconds` if the SPEED-mismatch
    issue (client flies slower/progressively behind) is delay-related; verify edge cases; add
    `ResetState()`.
  - **PASS:** all edge cases correct (reroute mid-flight re-targets smoothly; removed craft vanish;
    parked craft static; first mirror snaps exactly); host steady at the chosen rate with no lag over a
    long session; no console errors either instance; clean build 0/0 + full test suite green after the
    DIAG strip.

---

## 7. Risks / unknowns + mitigations

- **[#1] Client-freeze completeness** (a residual native writer fights the mirror) — *the top unknown.*
  Mitigation: INC-A explicitly verifies no drift/teleport with the host paused before any interpolation
  is added; the freeze patches + driver deletion are audited in §5. If a residual writer is found,
  suppress it (add to the emitter/producer suppression) before INC-B.
- **Broadcaster cost at the higher rate** (reintroducing the host lag). Mitigation: the cheap dirty-check
  (`TryGetCheapVehicleSignature`/`SigChanged`) skips the expensive `RecordVehicleState` for idle craft, so
  cost scales with MOVING craft only, not total; INC-B/D include an explicit host-lag check. Roll the
  rate back toward 10–12 Hz if lag appears.
- **Interpolation latency acceptability** (rendering 150 ms in the past). Mitigation: the craft is
  strategic/slow and both peers see their OWN issued travel relayed; 150 ms is imperceptible at geoscape
  scale. Tune `InterpDelaySeconds` down in INC-D if desired (floor ≈ 1× sendInterval + jitter).
- **Arrival-time timestamp jitter** (non-uniform packet arrival → playback wobble). Mitigation:
  fixed-interval host flush + `InterpDelay ≈ 3× interval` absorbs it; if visible, the §2 fallback adds a
  `[double hostNowSec]` field and switches to host-time stamps (we own both ends).
- **Orientation representation** (quaternion slerp vs a heading float). Decision: KEEP the quaternion
  (full fidelity, no decode ambiguity, +8 B). No mitigation needed; revisit only if bandwidth-bound.
- **Discrete vs continuous ordering** (an arrival reliable packet racing a stale continuous unreliable
  one). Mitigation: the existing single per-identity seq line + seq-guard drops the stale continuous
  packet; arrival SNAPS the buffer (clears the ring), so a late continuous sample for a landed craft is
  seq-dropped.
- **[CAVEAT] ONE monotonic seq line spans TWO delivery classes** (reliable/discrete + unreliable/
  continuous share a single per-identity seq + monotonic guard). With different delivery guarantees a
  LEGITIMATE discrete (reliable) message can carry a lower seq than an already-accepted continuous
  (unreliable) message and be WRONGLY DROPPED by the monotonic guard — and this drop could itself be a
  contributor to the host→client break. HYPOTHESIS to verify in INC-A (gate-zero tract diag): instrument
  whether the seq-guard ever drops legitimate host→client messages. If it does → split to PER-CHANNEL seq
  lines (one monotonic seq per delivery class) rather than one shared line.
- **`EntityReplicationScope` no longer wraps a client `StartTravel`** (the carve-out is removed).
  Mitigation: confirm nothing else relied on that window on the client; the host path
  (`CommandRelay.IsApplying`) and the client→host relay are untouched.

---

## 8. Test / verification strategy

- **Unit tests (pure, Unity-free — TDD-first, linked per-file into `Multiplayer.Tests.csproj` after the
  existing `InterpolationMath.cs` link):**
  - New `SnapshotBuffer`/alpha math in `InterpolationMath` (repurposed): bracket selection (find `s0,s1`
    spanning `renderTime`), `alpha = clamp01((rt−t0)/(t1−t0))`, underrun→hold-newest, overrun→clamp-oldest,
    monotonic-time insert, ring eviction. Mirror the style of the existing `GeoVehicleStateDifferTests`/
    `GeoStateDiffCodecTests`. (Position lerp / quaternion slerp themselves are Unity calls — test the
    scalar `alpha` + bracket logic, which fully determines the motion.)
  - Keep the existing `GeoStateDiffCodec`/`GeoVehicleStateDiffer` tests green (wire unchanged).
  - REMOVE the obsolete native-equation tests for `SegmentNum`/`SegmentTotalSeconds`/`GreatCircleAngleRad`/
    `CorrectedStartSec` when those members are scrapped.
- **In-game gate per increment** (2-instance co-op per `multiplayer-second-instance-setup`: Goldberg-emu
  2nd copy + `mklink /J` junctions; deploy the Release DLL to BOTH copies). Each increment's PASS
  criteria in §6 is the gate; do not advance on a fail (use `superpowers:systematic-debugging`).
- **Temporary DIAGB** stays through INC-A→INC-C (host snapshot summary, per-apply read-back, `0x35`
  boundary log) to prove send/recv/apply flow and single-writer; STRIP in INC-D after the final gate
  (one commit, build 0/0 + suite green after strip).
- **Build:** `dotnet build E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.csproj -c Release`
- **Tests:** `dotnet test E:\DEV\PhoenixPoint\Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj -c Release`
