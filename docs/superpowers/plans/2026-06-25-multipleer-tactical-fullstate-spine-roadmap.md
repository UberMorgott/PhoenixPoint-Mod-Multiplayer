# Multipleer — Tactical Full-State Spine: ROADMAP + Inc2 Implementation Plan

> Plan doc. Date 2026-06-25. Direction APPROVED (user re-confirmed) — this is the path to FINISH the
> pivot, not a re-decision. Builds on (and grounds against current src) the two feasibility docs:
> `docs\superpowers\specs\2026-06-20-multipleer-tactical-full-state-replication-feasibility.md` (end-state)
> + `Multipleer\docs\superpowers\specs\2026-06-19-multipleer-tactical-generic-state-spine-design.md` (spine)
> + handoff `docs\superpowers\2026-06-20-multipleer-consolidation-and-tactical-fullstate-handoff.md`.
> Roadmap tracker: `Multipleer\docs\COOP-SYNC-ROADMAP.md`. MEMORY in force: `multipleer-full-state-replication`,
> `dont-replace-working-architecture` (retire ONE surface per commit, revert-to-known-good on 3+ cascading
> fixes), `multipleer-reconcile-existing-first`, `multipleer-commit-workflow` (inner `main`, no push, no branches).
>
> Decompile proxy: `decompiled\AssemblyCSharp\Assembly-CSharp\src` (re-pin vs installed DLL before any patch).
> Tactical src: `Multipleer\src\Sync\Tactical\*` + `Multipleer\src\Harmony\Tactical\*`. Pure cores unit-tested
> (`Multipleer.Tests`, linked per file); engine-glue is reflection-bound → NOT linked → in-game-gated.
> Build/test: `dotnet test Multipleer\Multipleer.Tests\Multipleer.Tests.csproj`; mod build `Multipleer.csproj`;
> deploy `Multipleer\deploy.ps1` → `D:\Steam\...\Mods\Multipleer` (junction covers `D:\PP-Instance2`).

---

## 1. Vision / end-state

ONE native flow replaces the per-action "whack-a-mole" zoo. The host is the sole authority: turn-0 +
reconnect are seeded by the native FULL deploy SNAPSHOT (`tac.deploy`, ★in-game-verified), and during play the
SINGLE host→all state writer is the generic per-actor field-subset DELTA `tac.actorstate` (0x8F) — pos / facing /
HP / AP / WP / statuses / bodypart-HP / (later) equip / overwatch-cone — with per-actor signature-skip on a ~4 Hz
`Timing` heartbeat + flush-on-host-action. The client sim is FULLY FROZEN (AI, NextTurn, PlayTurnCrt sim body,
local damage roll, local move-cost, vision recompute all suppressed); it keeps only the native PRESENTATION
primitives (`Navigate` walk, `ApplyDamage` hit/death, `SetForward`, `SetCone`, `SetShownMode`, `DoCameraChase`)
which it drives FROM STATE/EVENTS, never from a local autonomous sim. Client INPUT flows up as INTENTS
(move/ability/end-turn); results return purely as the delta. **Why this ends the whack-a-mole:** one mechanism
means a fix is class-level (extend the fieldMask / fix the one apply path) instead of N hand-built suppress→
replay→broadcast surfaces, each with its own echo-loop / ordering / double-apply hazards.

---

## 2. Current state inventory (grounded — `Multipleer\src\Sync\Tactical\TacticalSyncSurfaces.cs`)

**17 surfaces defined** (0x80–0x91, 0x8E reserved). **4 are OUTCOME surfaces to RETIRE** (Inc6). The spine
delta (0x8F) already carries AP/WP/Pos/Health/Statuses/BodyPartHp; Inc2 adds Facing.

| id | const | dir | carries | role | status |
|---|---|---|---|---|---|
| 0x80 | `TacDeploy` | host→all | full deploy snapshot (gameParams + `TacLevelInstanceData` + actor table) | **KEEP** turn-0/reconnect seed | ★verified |
| 0x81 | `TacDeployChunk` | host→all | deploy snapshot fragment (over 60 KB cap) | **KEEP** | ★verified |
| 0x82 | `TacIntentMove` | client→host | `{netId,pos,nonce}` | **KEEP** intent relay | live |
| 0x83 | `TacMove` | host→all | move END `{pos,stopReason}` | **(c) RETIRE → delta Pos** | Inc6 #1 |
| 0x84 | `TacIntentEndTurn` | client→host | `{nonce}` | **KEEP** faction input | live |
| 0x85 | `TacTurn` | host→all | faction handoff `{idx,turn#,defGuid}` | **KEEP → pure state** | Inc4 |
| 0x86 | `TacMoveStart` | host→all | move START `{pos}` (walk anim) | **(b) KEEP** presentation bridge | live |
| 0x87 | `TacIntentAbility` | client→host | `{shooter,abilityGuid,target,nonce}` | **KEEP** intent relay | live |
| 0x88 | `TacDamage` | host→all | flattened `DamageResult` (+shooter AP/WP) | **(b) KEEP** death/FX event; delta HP = idempotent backstop | live |
| 0x89 | `TacVision` | host→all | player-faction `KnownActors` reconcile | **(c) RETIRE LAST** → faction-scoped delta (optional) | Inc6 #4 |
| 0x8A | `TacIntentEquip` | client→host | `{actor,equipIndex,nonce}` | **KEEP** intent relay | live |
| 0x8B | `TacEquip` | host→all | equip outcome `{equipIndex}` | **(c) RETIRE → delta Equip bit** | Inc6 #2 |
| 0x8C | `TacIntentOverwatch` | client→host | `{actor,cone8×f32,nonce}` | **KEEP** intent relay | live |
| 0x8D | `TacOverwatchState` | host→all | overwatch cone arm/clear | **(c) RETIRE → delta Overwatch bit** | Inc6 #3 |
| 0x8E | *(reserved)* | — | future generic `tac.intent` (spine §3) | — | not defined |
| 0x8F | `TacActorState` | host→all | per-actor delta: AP/WP/Pos/Health/Statuses/BodyPartHp (**+Facing Inc2**) | **(a) SPINE — KEEP + GROW** | live (Inc1) |
| 0x90 | `TacFireStart` | host→all | fire/throw START (anim only) | **(b) KEEP** presentation | live |
| 0x91 | `TacMeleeStart` | host→all | melee START (anim, stub replay) | **(b) KEEP** presentation | live |

Legend: **(a)** spine state-delta · **(b)** START/event presentation kept for animation · **(c)** per-action
OUTCOME surface to be subsumed by the delta. Snapshot=2, intents=5, turn=1, anim/event presentation=4, spine=1,
**retire=4**.

**Inc1 status (BUILT, in-game-pending):** `ActorFieldPos = 0x0008` is encoded+decoded (`TacticalLiveCodec.cs:850`),
host ships it (`TacticalActorStateSync.HostFlushOnce` sets the bit + `posSig` :169-190), client applies it via
`TacticalMoveSync.ApplyMirrorPosition` → walk/teleport per `TacticalActorStateDiff.DecidePositionApply`
(:216, bands None≤0.05 / Teleport 0.05-1.0 / Walk 1.0-40.0 / Teleport>40 / NaN→Teleport). `PlayTurnCrt` sim body
frozen on the mirror (`MirrorSuppressPatches.PlayTurnCrtMirrorFreezePatch`).

**DELTA from the prior roadmap (verify/correct):**
- The delta is FURTHER along than "Inc1=position only." It already carries **Health** (Feature D, `ActorFieldHealth
  0x0020`, death-safe `ShouldApplyHealthMirror`), **Statuses** (Feature B — `SyncStatuses=true`, visual-only via
  `ShouldMirrorStatus`/`VisibleOnHealthbar`, made inert by `ClientStatusMirrorGuards`; the old default-DENY
  `IsSyncableStatusType` allowlist is SUPERSEDED/unused), and **per-bodypart HP** (`ActorFieldBodyPartHp 0x0200`).
  So Inc3's "delta HP backstop" is ALREADY in. Inc2 (Facing) is the genuine next NEW field.
- **`ActorFieldFacing = 0x0010` already exists as a RESERVED bit** (`TacticalLiveCodec.cs:852`) but is NOT
  encoded/decoded/read/applied yet. Inc2 wires it (insertion point = ascending bit order, BETWEEN Pos 0x0008 and
  Health 0x0020 — currently nothing sits there).
- **Stance/cover is NOT a wire field** (the prompt's "facing + stance/cover bits" is a misnomer to correct):
  crouch/cover/evac/standby/shield are DERIVED — crouch from head-Y vs Pos.Y (`TacticalPerception.IsCrouching`
  :768), cover pose from a geometric query (`GetBestIdleCoverPoseAt` :509), evac/standby/shield from the status
  set. They sync FREE once Pos + Facing (+ statuses) mirror. Inc2 adds ONLY Facing and VERIFIES stance/cover in-game.

---

## 3. Increment roadmap Inc2–Inc6

Each increment ends at its own 2-instance DirectIP 127.0.0.1 in-game gate; commit to inner `main`, tests-green;
ADDITIVE-first, retire only after in-game OK. Diagnostic logs `[Multipleer][tac]`; strip before publish.

### Inc2 — Facing (+ stance/cover free) into the delta  ← CODE-COMPLETE (commit `74b462c`, build 0 err / 0 warn, 888 tests green (+6 new), in-game gate pending)
- **Goal:** mirror actor FACING (forward vector) so a host turn-in-place / post-move heading shows on the client,
  and the soldier adopts the correct crouch/cover idle pose (derived from Pos+Facing).
- **Scope/spine:** grow `tac.actorstate` 0x8F with the `ActorFieldFacing 0x0010` field; host reads `ActorComponent.Rot`
  → forward; client applies via `ActorComponent.SetForward`.
- **Subsumes:** nothing retired (additive). Lays the groundwork to retire `tac.move`(END) later (a moved actor's
  facing + cell both ride the delta).
- **Native APIs/risks:** `ActorComponent.Rot` (`ActorComponent.cs:43`), `SetForward(Vector3)` (:280→`SetRotation`→
  `SetTransform`:299 fires `ActorMovedEvent`). Risk: `SetForward` fires `ActorMovedEvent`→vision recompute — already
  client-suppressed (`MirrorVisionRecomputeSuppressPatches` on `OnActorMoved`); skip-while-navigating avoids fighting
  a mirror walk. No NEW suppress patch needed (AI/turn/vision already frozen).
- **In-game test:** §4 acceptance.

### Inc3 — Combat outcome + explosion VFX from damage-state + enemy-turn camera  ← NEXT
- **Goal:** with the sim frozen, confirm shot/grenade/melee fully replicate from EVENT+delta, and add the missing
  enemy-turn presentation + explosion/destruction VFX.
- **Scope/spine:** `tac.damage` 0x88 (event) + `tac.fire.start`/`tac.melee.start` 0x90/0x91 (anim) + delta Health
  backstop (already built) prove out together; death stays death-safe (`ShouldApplyHealthMirror`); add the
  enemy-turn camera/VFX presentation the client never gets (gap A/B in the handoff).
- **Subsumes:** nothing retired (keeps `tac.damage` as the event).
- **Native APIs/risks:** `CameraDirector.Hint(AbilityActivated)` inside `TacticalAbility.Activate` never runs on the
  client (Activate suppressed) → enemy turn invisible; grenade explosion VFX + terrain destruction not replicated.
  Risk: fire-start vs damage-delta ordering (impact before shot anim) — apply-order guard (anim before stat-set).
- **In-game test:** client sees host shot/grenade/melee damage + projectile/explosion VFX + enemy-turn cinematic;
  no double damage; burst = N hits.

### Inc4 — Turn/phase as PURE state (fully removes host-hang)
- **Goal:** turn handoff becomes pure reflected state; remove the residual host-hang root, not just the Inc1
  mitigation.
- **Scope/spine:** host ships `current faction` + `IsPlayingTurn` as state; client REFLECTS them with `PlayTurnCrt`
  sim body frozen (`MirrorPlayTurnCrt` already does most of this) — no client→host turn barrier, no coroutine to
  dead-spin.
- **Subsumes:** `tac.turn` 0x85 stays as the surface but becomes pure-state (no client sim run).
- **Native APIs/risks:** `TacticalFaction.PlayTurnCrt` (`:388`), `IsPlayingTurn` (`:79`). Risk: freeze regression on
  player INPUT enable — mitigated, input precondition forced by hand independent of `PlayTurnCrt` reaching :442.
- **In-game test:** full multi-turn cycle, both factions, no loss-of-control, no host-hang on monster handoff.

### Inc5 — Terrain/destructibles + reconnect / drift-correction
- **Goal:** replicate live destructible/terrain changes; support reconnect without global reload + a divergence
  backstop.
- **Scope/spine:** live destructible changes live in `TacLevelSavegame.Data` (NOT the level DTO) → a low-rate
  per-actor/destructible `ProcessInstanceData` drift-correction; reconnect = re-send the deploy snapshot to the
  single returning peer + buffered-delta catch-up (others unaffected); CRC32 over the serialized authoritative
  subset for divergence detection (geoscape Inc5 pattern, reuse `SaveTransferCoordinator.Crc32`).
- **Subsumes:** nothing.
- **Native APIs/risks:** `TacLevelSavegame.GetObjectsToWrite` (`:45`, heavy → reconnect-only). Risk: mid-battle
  serialize cost — only on reconnect (one peer); live stream stays the cheap field-subset delta.
- **In-game test:** blow a wall → mirrors; drop+rejoin a client mid-battle → it re-seeds + catches up, others keep
  playing.

### Inc6 — RETIRE the per-action OUTCOME zoo (one surface per commit, in-game-gated)
- **Goal:** delete the redundant outcome surfaces now subsumed by the delta — the actual "whack-a-mole" cleanup.
- **Order + dependency:** `tac.move`(END 0x83) → `tac.equip`(0x8B) → `tac.overwatch.state`(0x8D) → `tac.vision`(0x89,
  last/optional). **Dependency:** equip + overwatch retirement each require FIRST GROWING the delta with their field
  (`ActorFieldEquip 0x0080`, `ActorFieldOverwatch 0x0100` — reserved bits, not yet encoded) and in-game-proving it,
  THEN deleting the outcome surface. `tac.move`(END) needs no new field (delta Pos exists).
- **KEEP:** `tac.move.start`/`tac.fire.start`/`tac.melee.start` (anim), `tac.damage` (event), the intent relay
  (0x82/0x84/0x87/0x8A/0x8C), `tac.turn`, and all sim-freeze patches.
- **In-game test:** §5 — after each retire, the corresponding state still mirrors via the delta.

---

## 4. Inc2 — DETAILED implementation plan (facing + stance/cover-free)

### File structure

| File | Change | Linked in tests? |
|---|---|---|
| `Multipleer\src\Sync\Tactical\TacticalLiveCodec.cs` | **MODIFY** — encode/decode the `ActorFieldFacing 0x0010` field (FacingX/Y/Z) in ascending bit order between Pos(0x0008) and Health(0x0020); add `FacingX/Y/Z` + `HasFacing` to `ActorStateRecord`; update wire-format comment | YES (`:147`) — codec tests run |
| `Multipleer\src\Sync\Tactical\TacticalActorStateDiff.cs` | **MODIFY** — add pure `FacingEpsilon` const + `FacingChanged(...)` decision (client skip + host signature) | YES (`:156`) — diff tests run |
| `Multipleer\Multipleer.Tests\TacticalActorStateCodecTests.cs` | **MODIFY** — add facing round-trip + truncation tests | (test file) |
| `Multipleer\Multipleer.Tests\TacticalActorStateDiffTests.cs` | **MODIFY** — add `FacingChanged` tests | (test file) |
| `Multipleer\src\Sync\Tactical\TacticalActorStateSync.cs` | **MODIFY** — host: read facing (`Rot`→forward), add `facingSig`, set the bit + fill FacingX/Y/Z; client: apply facing after stats | NO (engine glue, in-game) |
| `Multipleer\src\Sync\Tactical\TacticalMoveSync.cs` | **MODIFY** — add client `ApplyMirrorFacing` + `TrySetForward`/`GetForward` helpers | NO (engine glue, in-game) |

No new files; no new Harmony patch; no new surface id. Behavior is ADDITIVE — the delta gains one optional field.

### Tasks (TDD: failing test → run-fail → minimal impl → run-pass → commit). 5 tasks.

---

#### Task 1 — Codec: encode/decode the Facing field (PURE, TDD)

**1a. Write failing tests** in `TacticalActorStateCodecTests.cs` (mirror the existing `PosOnly_RoundTrips` /
`AllFieldsWithPos_RoundTrip_AscendingBitOrder` / `PosTruncated_ReturnsFalse`):

```csharp
private const ushort Fa = TacticalLiveCodec.ActorFieldFacing;   // Inc2: absolute forward vector

private static TacticalLiveCodec.ActorStateRecord RecFac(int netId, ushort mask, float ap, float wp,
    float health, float px, float py, float pz, float fx, float fy, float fz)
{
    return new TacticalLiveCodec.ActorStateRecord
    {
        NetId = netId, FieldMask = mask, Ap = ap, Wp = wp, Health = health,
        PosX = px, PosY = py, PosZ = pz, FacingX = fx, FacingY = fy, FacingZ = fz
    };
}

[Fact]
public void FacingOnly_RoundTrips()
{
    var batch = new TacticalLiveCodec.ActorStateBatch(30u,
        new List<TacticalLiveCodec.ActorStateRecord> { RecFac(42, Fa, 0,0,0, 0,0,0, 0f, 0f, 1f) });
    byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
    Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
    var a = Assert.Single(got.Actors);
    Assert.True(a.HasFacing);
    Assert.False(a.HasPos);
    Assert.Equal(0f, a.FacingX); Assert.Equal(0f, a.FacingY); Assert.Equal(1f, a.FacingZ);
}

[Fact]
public void PosThenFacing_RoundTrips_AscendingBitOrder()
{
    // Pos(0x08) then Facing(0x10) on one record → guards the ascending-bit-order insertion.
    var batch = new TacticalLiveCodec.ActorStateBatch(31u,
        new List<TacticalLiveCodec.ActorStateRecord>
        { RecFac(7, (ushort)(Po | Fa), 0,0,0, 1f,2f,3f, 1f,0f,0f) });
    byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
    Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
    var a = Assert.Single(got.Actors);
    Assert.True(a.HasPos); Assert.True(a.HasFacing);
    Assert.Equal(3f, a.PosZ); Assert.Equal(1f, a.FacingX);
}

[Fact]
public void AllFieldsWithFacing_RoundTrip_AscendingBitOrder()
{
    // AP|WP|STATUSES|POS|FACING|HEALTH|BODYPARTHP → full ascending order with Facing wedged between Pos and Health.
    var r = RecFac(3, (ushort)(Ap | Wp | St | Po | Fa | He | Bp), 2f, 5f, 64.25f, 11f,2f,33f, 0f,0f,1f);
    r.Statuses.Add(Stat("g-bleed", 7, 4f));
    r.BodyParts.Add(Part("Head", 40f));
    var batch = new TacticalLiveCodec.ActorStateBatch(32u,
        new List<TacticalLiveCodec.ActorStateRecord> { r });
    byte[] bytes = TacticalLiveCodec.EncodeActorState(batch);
    Assert.True(TacticalLiveCodec.TryDecodeActorState(bytes, out var got));
    var a = Assert.Single(got.Actors);
    Assert.Equal(33f, a.PosZ);
    Assert.True(a.HasFacing); Assert.Equal(1f, a.FacingZ);
    Assert.Equal(64.25f, a.Health);
    Assert.Equal("Head", a.BodyParts[0].SlotName);
}

[Fact]
public void FacingTruncated_ReturnsFalse()
{
    using (var ms = new System.IO.MemoryStream())
    using (var w = new System.IO.BinaryWriter(ms))
    {
        w.Write(1u); w.Write(1); w.Write(5);
        w.Write(Fa);     // facing bit, but NO 3 floats follow → safe false (12-byte guard)
        Assert.False(TacticalLiveCodec.TryDecodeActorState(ms.ToArray(), out _));
    }
}
```

**1b. Run-fail:** `dotnet test Multipleer\Multipleer.Tests\Multipleer.Tests.csproj` → these 4 fail to COMPILE
(`HasFacing`/`FacingX` don't exist) — that is the red.

**1c. Minimal impl** in `TacticalLiveCodec.cs`:
- `ActorStateRecord` — add fields + accessor:
```csharp
public float FacingX, FacingY, FacingZ;   // Inc2: absolute forward vector (valid only when HasFacing)
...
public bool HasFacing => (FieldMask & ActorFieldFacing) != 0;
```
- In `EncodeActorState`, insert AFTER the `ActorFieldPos` block and BEFORE the `ActorFieldHealth` block:
```csharp
// FACING (0x0010) — Inc2. Encoded AFTER position (0x0008) and BEFORE health (0x0020), i.e. ascending bit
// order. Absolute world forward vector as 3×f32 (client applies via ActorComponent.SetForward).
if ((a.FieldMask & ActorFieldFacing) != 0)
{
    w.Write(a.FacingX);
    w.Write(a.FacingY);
    w.Write(a.FacingZ);
}
```
- In `TryDecodeActorState`, insert AFTER the Pos decode and BEFORE the Health decode (same ascending order),
  with the 12-byte unit guard:
```csharp
// FACING (0x0010) — Inc2. Decoded AFTER position, BEFORE health (ascending bit order), only if its bit
// is set. 3×f32 = 12 bytes, guarded as a unit.
if ((rec.FieldMask & ActorFieldFacing) != 0)
{
    if (ms.Length - ms.Position < 12) return false;
    rec.FacingX = r.ReadSingle();
    rec.FacingY = r.ReadSingle();
    rec.FacingZ = r.ReadSingle();
}
```
- Update the `ActorFieldFacing` const comment (drop "(NOT encoded yet)") + extend the wire-format doc-comment
  line to list facing between pos and health.

**1d. Run-pass:** `dotnet test ...` → all green (existing + 4 new).

**1e. Commit** (commit-on-green workflow — write sentinel, `git -C Multipleer add -A && git -C Multipleer commit`,
delete sentinel, ONE Bash call):
`feat(multipleer-tac): encode actor FACING field (0x0010) in tac.actorstate delta [Inc2]`

---

#### Task 2 — Pure facing-change decision (TDD)

**2a. Write failing tests** in `TacticalActorStateDiffTests.cs`:
```csharp
[Fact]
public void FacingChanged_SubEpsilon_False()
    => Assert.False(TacticalActorStateDiff.FacingChanged(0f,0f,1f, 0.005f,0f,0.999f));

[Fact]
public void FacingChanged_OverEpsilon_True()
    => Assert.True(TacticalActorStateDiff.FacingChanged(0f,0f,1f, 1f,0f,0f));
```

**2b. Run-fail:** compile error (`FacingChanged` missing).

**2c. Minimal impl** in `TacticalActorStateDiff.cs` (next to `PositionEpsilon`):
```csharp
/// <summary>Facing-vector equality tolerance: below this per-component delta the forward is "unchanged" so a
/// sub-epsilon jitter never re-broadcasts (host signature) nor re-applies (client) — avoids re-firing the
/// native ActorMovedEvent. Tactical actors are yaw-only and the forward is unit-length, so 0.01 is far below
/// any real turn.</summary>
public const float FacingEpsilon = 0.01f;

/// <summary>PURE: did the forward vector change beyond <see cref="FacingEpsilon"/> on any component? Unity-free
/// (the glue passes the 3 components) so the host signature and the client skip make the SAME decision.</summary>
public static bool FacingChanged(float ax, float ay, float az, float bx, float by, float bz)
    => System.Math.Abs(ax - bx) > FacingEpsilon
    || System.Math.Abs(ay - by) > FacingEpsilon
    || System.Math.Abs(az - bz) > FacingEpsilon;
```

**2d. Run-pass:** green.

**2e. Commit:** `feat(multipleer-tac): pure FacingChanged decision for actorstate delta [Inc2]`

---

#### Task 3 — HOST: read + ship facing (engine glue; build-green + in-game-gated)

No unit test (reflection glue, NOT linked — matches every other glue file). Verification = build clean + the
in-game gate (Task 5). Implement in `TacticalActorStateSync.cs`:

- Extend `ReadActorState(...)` signature with `out Vector3 forward, out bool hasFacing` (init `forward =
  Vector3.forward; hasFacing = false;`), and after the `TryReadPos` block add:
```csharp
// Inc2: actor facing as a forward vector. ActorComponent.Rot is the world rotation (ActorComponent.cs:43);
// forward = Rot * Vector3.forward. Shipped unconditionally (core presentation state, like Pos), NOT gated by
// SyncStatuses. A NaN/unreadable rotation is dropped (no Facing bit).
if (TryReadForward(actor, out Vector3 fwd)) { forward = fwd; hasFacing = true; }
```
- Add the helper (mirror `TryReadPos`):
```csharp
private static bool TryReadForward(object actor, out Vector3 forward)
{
    forward = Vector3.forward;
    try
    {
        object raw = GetProp(actor, "Rot");          // ActorComponent.Rot : Quaternion
        if (!(raw is Quaternion q)) return false;
        Vector3 f = q * Vector3.forward;
        if (float.IsNaN(f.x) || float.IsNaN(f.y) || float.IsNaN(f.z)) return false;
        forward = f;
        return true;
    }
    catch { return false; }
}
```
- In `HostFlushOnce`, update the `ReadActorState(...)` call to capture `out Vector3 forward, out bool hasFacing`,
  add a `facingSig` (so a turn-in-place — pos unchanged, facing changed — re-broadcasts) and fold it into `sig`,
  set the bit, and fill the record:
```csharp
string facingSig = hasFacing
    ? "$f=" + forward.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
            + forward.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ","
            + forward.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
    : "";
// ... append + facingSig to the existing `sig` (after healthSig + posSig) ...
if (hasFacing) mask |= TacticalLiveCodec.ActorFieldFacing;
// ... on the new ActorStateRecord: FacingX = forward.x, FacingY = forward.y, FacingZ = forward.z ...
```
(`FacingEpsilon`/`FacingChanged` from Task 2 is available if a numeric pre-check is later preferred over the
`F2` string fragment; the string fragment already deduplicates to 0.01 precision.)

**Commit:** `feat(multipleer-tac): host ships actor FACING in tac.actorstate delta [Inc2]` (after `dotnet build
Multipleer\Multipleer.csproj` = 0 err/0 warn).

---

#### Task 4 — CLIENT: apply facing absolute, idempotent, skip-while-navigating (engine glue; build-green + in-game-gated)

Implement in `TacticalMoveSync.cs` (next to `ApplyMirrorPosition`), then call it from `HandleActorState`.

- New client applier + helpers:
```csharp
/// <summary>CLIENT (mirror): apply the host-authoritative ABSOLUTE facing carried by the tac.actorstate delta.
/// Sets the actor's forward via ActorComponent.SetForward(Vector3) (ActorComponent.cs:280 → SetRotation →
/// SetTransform :299). ABSOLUTE/idempotent. SKIPS while the mirror is navigating (a walk owns rotation — the
/// move rail or ApplyMirrorPosition Navigate turns the actor along the path; the next heartbeat converges to the
/// host's final facing). SKIPS a zero vector (SetForward(zero) → invalid LookRotation) and a sub-epsilon change
/// (no churn / no ActorMovedEvent re-fire — vision recompute is already client-suppressed). No-op off-mirror.</summary>
public static bool ApplyMirrorFacing(object actor, Vector3 forward)
{
    try
    {
        if (actor == null) return false;
        var engine = NetworkEngine.Instance;
        if (engine == null || !engine.IsActive || engine.IsHost) return false;   // client-only
        if (!TacticalDeploySync.IsClientMirroring) return false;
        if (forward == Vector3.zero) return false;
        object nav = GetProp(actor, "TacticalNav");
        if (nav != null && ToBool(GetProp(nav, "IsNavigating"))) return false;    // walk owns rotation
        Vector3 cur = GetForward(actor);
        if (!TacticalActorStateDiff.FacingChanged(cur.x, cur.y, cur.z, forward.x, forward.y, forward.z))
            return false;   // already converged
        return TrySetForward(actor, forward);
    }
    catch (Exception ex) { Debug.LogError("[Multipleer][tac] ApplyMirrorFacing failed: " + ex); return false; }
}

private static Vector3 GetForward(object actor)
{
    object r = GetProp(actor, "Rot");
    return r is Quaternion q ? q * Vector3.forward : Vector3.forward;
}

private static bool TrySetForward(object actor, Vector3 forward)
{
    try
    {
        var m = AccessTools.Method(actor.GetType(), "SetForward", new[] { typeof(Vector3) });
        if (m == null) { Debug.LogError("[Multipleer][tac] SetForward(Vector3) not found"); return false; }
        m.Invoke(actor, new object[] { forward });
        return true;
    }
    catch (Exception ex) { Debug.LogError("[Multipleer][tac] TrySetForward failed: " + ex); return false; }
}
```
- In `TacticalActorStateSync.HandleActorState`, add a `facingCnt` counter and, INSIDE the `_applyingRemote` scope,
  apply facing BEFORE the `rec.HasPos` block (so a stationary actor keeps the host facing; a moved actor's Pos may
  start a Walk that overrides it, then converges next heartbeat):
```csharp
if (rec.HasFacing)
{
    if (TacticalMoveSync.ApplyMirrorFacing(
            actor, new Vector3(rec.FacingX, rec.FacingY, rec.FacingZ))) facingCnt++;
}
```
  and include `facing=" + facingCnt` in the existing `CLIENT applied tac.actorstate` diag line.

**Commit:** `feat(multipleer-tac): client mirrors actor FACING from tac.actorstate delta [Inc2]` (after build
0 err/0 warn + `dotnet test` green).

---

#### Task 5 — In-game acceptance gate (2-instance DirectIP 127.0.0.1) + deploy

`deploy.ps1`, restart BOTH instances (client = `D:\PP-Instance2` via `tools\launch-second-copy.bat`), per memory
`multipleer-second-instance-setup`. Watch `[Multipleer][tac]` logs (`HOST broadcast tac.actorstate ... changedActors`,
`CLIENT applied tac.actorstate ... facing=N`).

Pass criteria:
1. **Facing mirrors:** host turns a soldier to face a new direction WITHOUT moving (e.g. rotate toward a target /
   manual facing) → the client soldier rotates to the same heading within ~1 heartbeat (≤~0.5 s). Both directions.
2. **Post-move heading:** after a move, the client soldier's final facing matches the host's (not stuck facing the
   travel direction). No mid-walk facing pop (skip-while-navigating holds; converges after nav stops).
3. **Stance/cover FREE (the no-field claim):** a soldier standing next to cover adopts the correct crouch/cover idle
   pose on the client (derived from Pos+Facing — no wire field). Verify against the host's pose.
4. **No regression:** Inc1 walk still walks (not teleport); AP/WP/HP/status icons still mirror; no loss-of-control;
   no host-hang on end-turn.

If green → update `COOP-SYNC-ROADMAP.md` (Inc2 DONE) + memory `multipleer-mod-status.md`, then proceed to Inc3.

---

## 5. Surface-retirement strategy (Inc6)

Retire ONE outcome surface per commit, in-game-gated, keeping the old path until the delta is in-game-confirmed for
that surface (MEMORY `dont-replace-working-architecture`: revert-to-known-good on 3+ cascading fixes; never break
the working deploy-sync). **Per-surface protocol:**

- **(P1) Prove the delta carries the data.** The subsuming delta field must be encoded+decoded (codec test green)
  AND read+applied by the glue. `tac.move`(END) needs no new field (`ActorFieldPos` exists). `tac.equip` and
  `tac.overwatch.state` FIRST need their delta field grown + in-game-proven (a mini-increment) BEFORE retirement.
- **(P2) Disable, don't delete (1 commit).** Comment out the surface's host BROADCAST (e.g. `HostBroadcastMoveOutcome`)
  + the client APPLY dispatch arm, leaving the surface id + codec intact. Deploy.
- **(P3) 2-instance verify** the state still mirrors via the delta within ~1 heartbeat, both directions, no drift /
  overshoot / missing cosmetic. If it diverges → re-enable, fix the delta, repeat (do NOT stack fixes).
- **(P4) Delete (next commit).** Remove the surface id (`TacticalSyncSurfaces.cs`), its codec
  (`TacticalLiveCodec.cs`), its glue handler, and its suppress/replay Harmony patch. Re-verify build + tests.

**Order + the specific verification each retire needs:**

1. **`tac.move` (END, 0x83) → delta Pos.** Field exists. Verify: with `HostBroadcastMoveOutcome` + `ClientOnMove`
   disabled, a moved soldier still lands on the EXACT host cell via the delta heartbeat + `ApplyMirrorPosition`
   walk/teleport. KEEP `tac.move.start` (0x86 anim). Risk to clear before delete: the delta's skip-if-navigating +
   heartbeat reconcile must fully replace the move-END exact-cell snap (no residual drift on overwatch early-stop).
2. **`tac.equip` (0x8B) → delta `ActorFieldEquip 0x0080`.** GROW FIRST: encode `selectedEquipIndex:i32` (reserved
   bit), host read via `TacticalEquipSync.EquipIndexOf`/`SelectedEquipment`, client apply via
   `InvokeSetSelectedEquipment` under `_applyingRemote`. Verify the active weapon + exposed abilities mirror. THEN
   delete `tac.equip`. KEEP `tac.intent.equip` (0x8A, input).
3. **`tac.overwatch.state` (0x8D) → delta `ActorFieldOverwatch 0x0100`.** GROW FIRST: encode `armed:bool (+8×f32
   cone)` (reserved bit), host read via `TacticalOverwatchSync.FindOverwatchStatus`/`FlattenConeStruct`, client
   cosmetic apply via `ApplyCosmeticArm`/`RemoveCosmeticArm`. Verify the watch cone shows/clears. THEN delete
   `tac.overwatch.state`. KEEP `tac.intent.overwatch` (0x8C).
4. **`tac.vision` (0x89) LAST / OPTIONAL.** Per-FACTION reconcile (not per-actor). Fold into a faction-scoped delta
   sibling ONLY if it folds cleanly; otherwise LEAVE it (it already works in-game — lowest-risk to keep).

**Verification that data is fully carried before any delete:** a surface is safe to delete only when, with its
broadcast disabled (P2), the 2-instance test (P3) shows the state converges via the delta alone — i.e. the delta
field is the proven single source for that data. The delta's ABSOLUTE-value + signature-skip + heartbeat design
makes a dropped delta self-heal (the per-actor signature still differs → re-ships next tick), so loss-tolerance is
already covered.
