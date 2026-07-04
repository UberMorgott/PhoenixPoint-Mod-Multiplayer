# Multiplayer Increment 1 — Rail Unification (Slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` to implement
> this plan task-by-task; steps use checkbox (`- [ ]`) syntax. Each code task is **failing test → run (red)
> → minimal impl → run (green) → commit**. The mod uses **runtime reflection** for game types (game DLLs are
> NOT compile-time refs in the test project); the pure files this slice adds reference NO game type. Verify
> every signature with Serena against the real source before editing — this plan already did so (2026-06-26)
> and cites exact `file:line`. Do NOT delete the working geoscape `0x60`–`0x66` path; this slice is
> ADDITIVE-FIRST + BEHAVIOR-PRESERVING (`dont-replace-working-architecture`).

**Goal:** Begin convergence increment 1 ("Unify the rail", design spec §6.1) by lifting the tactical-only
sequencing + dedup primitives into SHARED domain-agnostic types (`SurfaceSeq`, `IntentDedup`) that BOTH
layers use, generalizing the `SurfaceRouter` chokepoint to also service geoscape, and migrating ONE geoscape
host→all message class (the versioned wallet snapshot) onto the unified `SyncEnvelope` (`0x67`) rail behind a
default-OFF gate — all behavior-preserving (with the gate OFF, geoscape co-op is byte-for-byte unchanged).

**Architecture:** The tactical layer already rides one enveloped rail (`SyncEnvelope` `0x67` →
`SurfaceRouter` chokepoint → tactical fast-path hook) with a per-surface `TacticalLiveSeq` and a
`(surfaceId,nonce)` `TacticalIntentDedup`. The geoscape layer rides raw per-purpose packets `0x60`–`0x66`
(routed in `NetworkEngine.RouteMessage` → `SyncEngine.On*`) with a global `SequenceTracker` + a
`(peerId,nonce)` `RequestDedup`. This slice (a) extracts `TacticalLiveSeq`→`SurfaceSeq` and
`TacticalIntentDedup`→`IntentDedup` as shared base types (tactical types become thin subclasses, staying
green); (b) adds a second instance hook `SurfaceRouter.GeoscapeInbound` consulted after the tactical
fast-path, so geoscape envelope surfaces ride the SAME chokepoint; (c) gives the geoscape wallet echo a
surface id (`0xA0`) in the spec's geoscape partition and has the host ALSO emit it as an envelope when
`GeoRailGate.Enabled` (default `false`). The client routes the envelope through the geoscape hook into the
EXISTING `OnWalletSync` applier (version-guarded + idempotent), so even with both paths live there is no
double-apply. The legacy `0x60`–`0x66` path stays the sole primary path and is retired only later, in-game-verified.

**Tech Stack:** C# (`net472`, PhoenixPoint mod), HarmonyX (`Harmony`/`AccessTools`, reflection-only for game
types), existing `NetworkMessage` + `SyncProtocol` (BinaryWriter/Reader) wire layer, xUnit (`Multiplayer.Tests`,
baseline **801 green**). Pure files are unit-tested; the engine/Harmony wiring (Task 6) is in-game verified.

---

## Background facts (verified via Serena, 2026-06-26)

**The two rails (real `file:line`)**
- `PacketType` (`src/Network/MessageLayer/PacketType.cs`): geoscape group `ActionRequest=0x60`, `ActionApply=0x61`,
  `ActionReject=0x62`, `WalletSync=0x63`, `StateSync=0x64`, `EventRaised=0x65`, `EventDismiss=0x66`,
  `SyncEnvelope=0x67` (unified surface envelope, any direction). `0x68` retired.
- `NetworkEngine.RouteMessage` (`src/Network/NetworkEngine.cs:386-576`): `switch(msg.Type)`; geoscape cases call
  `Sync.OnActionRequest/OnActionApply/OnActionReject/OnWalletSync/OnStateSync/OnEventRaised/OnEventDismiss`; the
  `SyncEnvelope` case (already present) calls `Sync.OnSyncEnvelope(msg.SenderSteamId, msg.Payload)`.
- `SyncEngine` (`src/Network/Sync/SyncEngine.cs`): three inbound geoscape mechanisms — (A) currency echo
  `OnWalletSync` (`:243-251`, guarded by `_tracker.ShouldApplyWallet`), broadcast by `BroadcastFullWallet`
  (`:254-261`) and the `Tick` dirty flush (`:519-526`); (B) action relay `OnActionRequest` (`:109-194`,
  uses `RequestDedup _seenRequests`)/`OnActionApply` (`:198-228`); (C) state-channel echo `OnStateSync`
  (`:288-306`). Field `_router = new SurfaceRouter()` (`:50`); `_tracker = new SequenceTracker()` (`:26`);
  `_walletVersion` (`:27`). `OnSyncEnvelope` (`:546`) = `_router.OnInbound(senderPeerId, data, this)`.
- `SurfaceRouter` (`src/Network/Sync/SurfaceRouter.cs`): `public static Func<byte,byte[],bool> TacticalInbound`
  (`:26`); `OnInbound(ulong, byte[], ISyncSink)` (`:29-37`) decodes via `SyncProtocol.TryDecodeEnvelope`, calls
  the tactical hook, drops anything else. **Does NOT deref the `ISyncSink` param** (so unit tests may pass null).
- `SyncProtocol` (`src/Network/Sync/SyncProtocol.cs`): `EncodeEnvelope(byte surfaceId, SyncKind kind, byte[]
  payload)` (`:412`, throws on payload > `ushort.MaxValue`) / `TryDecodeEnvelope(byte[], out byte, out SyncKind,
  out byte[])` (`:430`); `EncodeWalletSync(ulong version, List<(int,float)>)` (`:105`) / `TryDecodeWalletSync`
  (`:121`). `SyncKind` (`src/Network/Sync/SyncKind.cs`): `ActionRequest=0, ActionApply=1, StateSnapshot=2, StateDelta=3`.
- `SurfaceIds` (`src/Network/Sync/SurfaceIds.cs`): action ids 1–30, state-channel ids 1–5 (legacy plan id-space).
- `SequenceTracker` (`src/Network/Sync/SequenceTracker.cs`): global `ShouldApply/Mark`, `ShouldApplyWallet/MarkWallet`,
  `ShouldApplyChannel/MarkChannel`. `RequestDedup` (`src/Network/Sync/RequestDedup.cs`): keyed `(peerId,nonce)`, floor `< 1 ? 1`.

**The tactical primitives to lift (`src/Sync/Tactical/TacticalLiveCodec.cs`)**
- `TacticalLiveSeq` (`:1079-1125`): `public sealed class`; `Dictionary<ushort,uint> _hostNext`/`_clientLast`;
  `uint Next(ushort)`, `bool ShouldApply(ushort,uint)`, `void Mark(ushort,uint)`, `void Reset()`, plus the
  tactical-only no-op `void BeginDeployCaptureMission()`.
- `TacticalIntentDedup` (`:1132-1158`): `public sealed class`; `HashSet<ulong> _seen`, `Queue<ulong> _order`,
  ctor `(int capacity = 512)` with floor `< 16 ? 16`; `bool IsNew(ushort surfaceId, uint nonce)`,
  `void Reset()`, private static `Key(ushort,uint) => ((ulong)surfaceId<<32)|nonce`.
- Consumers: `TacticalDeploySync.LiveSeq` (typed `TacticalLiveSeq`, `:77`) + `.IntentDedup` (typed
  `TacticalIntentDedup`, `:79`); reassigned at `:349-350,607,642-643`; `.BeginDeployCaptureMission()` at `:349`.
- Existing regression tests: `TacticalLiveSeqTests` + `TacticalIntentDedupTests` in
  `Multiplayer.Tests/TacticalLiveCodecTests.cs:141-237` — **these are the green gate for the reparent (Task 3)**.

**Tactical hook arm + envelope dispatch (the pattern geoscape mirrors)**
- `TacticalDeploySync.ArmInboundHook()` (`src/Sync/Tactical/TacticalDeploySync.cs:660-661`) =
  `SurfaceRouter.TacticalInbound = HandleTacticalEnvelope`; called once in `MultiplayerMain.OnModEnabled`
  (`src/MultiplayerMain.cs:47`). `HandleTacticalEnvelope(byte surfaceId, byte[] payload)` (`:673-804`) is a
  `surfaceId ==` switch returning `true` when consumed. Outbound helper: `TacticalMoveSync.BroadcastToAll(engine,
  surfaceId, payload)` wraps `EncodeEnvelope(surfaceId, StateSnapshot, payload)` → `SyncEnvelope` packet
  (`src/Sync/Tactical/TacticalMoveSync.cs:690-695`).

**Build / test**
- Mod assembly auto-globs `src/**/*.cs` (only `Multiplayer.Tests/**` is `<Compile Remove>`d) → new `src` files are
  auto-included (`Multiplayer/Multiplayer.csproj:17-22`). Build: `dotnet build Multiplayer\Multiplayer.csproj`.
- The test project does **NOT** auto-glob src (`EnableDefaultCompileItems=false`); each pure file is an explicit
  `<Compile Include="..\src\...">` link (`Multiplayer/Multiplayer.Tests/Multiplayer.Tests.csproj`). Already linked:
  `SyncProtocol`, `SyncKind`, `SurfaceIds`, `SurfaceRouter`, `ISyncSink`, `SequenceTracker`, `RequestDedup`,
  `TacticalLiveCodec`. New pure files MUST be added there.
- Test run: `dotnet test Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj` (single class:
  `... --filter FullyQualifiedName~SurfaceSeqTests`).
- Commit (inner repo, `main`, local only — `multiplayer-commit-workflow`):
  `git -C E:/DEV/PhoenixPoint/Multiplayer add -A && git -C E:/DEV/PhoenixPoint/Multiplayer commit -m "<msg>"`.

---

## Scope — what this slice DOES / does NOT do

**Does (independently shippable, behavior-preserving):**
1. Introduce shared `SurfaceSeq` + `IntentDedup` (lifted verbatim from the tactical types) — domain-agnostic, unit-tested.
2. Reparent `TacticalLiveSeq : SurfaceSeq`, `TacticalIntentDedup : IntentDedup` — tactical keeps working through the shared types.
3. Generalize `SurfaceRouter` with a second instance hook `GeoscapeInbound` (consulted after tactical) so geoscape can ride the SAME chokepoint.
4. Add `GeoRailGate.Enabled` (default `false`) + `SurfaceIds.GeoWallet = 0xA0`.
5. Wire the geoscape wallet echo onto the envelope rail behind the gate, into the existing `OnWalletSync` applier (in-game verified).

**Does NOT (deferred to later in-game-verified slices):**
- Retire ANY legacy packet (`0x60`–`0x66`) or the dead router comment — the legacy path stays sole-primary.
- Touch `SequenceTracker` / `RequestDedup` (the global geoscape seq + `(peerId,nonce)` dedup keep running the legacy path).
- Migrate the geoscape ACTION relay / events / state-channels (intents, `0x60`/`0x61`/`0x62`/`0x64`/`0x65`/`0x66`) — those are client→host intents needing the shared authorize stage.
- Add the generic intent surface, the `AuthorizeStage`, the `IStateDeltaSource`/`ISnapshotDomain` contracts, the freeze model, or CRC (spec §6.2–§6.6).

**Follow-up slices (separate plans):**
- **Slice 2** — migrate geoscape `WalletSync` to envelope-ONLY (retire the `0x63` send), in-game verified; then `StateSync` (`0x64`) likewise.
- **Slice 3** — migrate the geoscape host→all `ActionApply` (`0x61`) onto an envelope surface; converge its ordering onto a `SurfaceSeq` stream (begin replacing the global `SequenceTracker`).
- **Slice 4** — migrate the geoscape client→host `ActionRequest` (`0x60`) onto a generic intent surface using the shared `IntentDedup`; retire `RequestDedup`; introduce the shared authorize stage. (= rest of Increment 1.)
- **Slice 5** — collapse `SurfaceRouter`'s two ad-hoc delegates into one per-surface-range handler registry (optional cleanup).

---

## File-structure map

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/Network/Sync/SurfaceSeq.cs` | **Create** | Shared per-surface monotonic seq (host) + last-writer-wins guard (client). Pure. |
| `src/Network/Sync/IntentDedup.cs` | **Create** | Shared `(surfaceId,nonce)` bounded-ring intent de-duplicator (floor 16). Pure. |
| `src/Network/Sync/GeoRailGate.cs` | **Create** | Default-OFF rollout switch for the geoscape envelope rail. Pure. |
| `src/Sync/Tactical/TacticalLiveCodec.cs` | **Modify** | `TacticalLiveSeq`/`TacticalIntentDedup` become thin subclasses of the shared types (keep `BeginDeployCaptureMission`). |
| `src/Network/Sync/SurfaceIds.cs` | **Modify** | Add `GeoWallet = 0xA0` (geoscape partition `0xA0`–`0xBF`). |
| `src/Network/Sync/SurfaceRouter.cs` | **Modify** | Add instance `GeoscapeInbound` hook, consulted after the tactical fast-path. |
| `src/Network/Sync/SyncEngine.cs` | **Modify** (in-game) | Arm `_router.GeoscapeInbound`; add `HandleGeoscapeEnvelope`; gate-mirror wallet onto the envelope in `BroadcastFullWallet` + `Tick`. |
| `Multiplayer.Tests/Multiplayer.Tests.csproj` | **Modify** | Link the three new pure files. |
| `Multiplayer.Tests/SurfaceSeqTests.cs` | **Create** | Unit tests for `SurfaceSeq`. |
| `Multiplayer.Tests/IntentDedupTests.cs` | **Create** | Unit tests for `IntentDedup`. |
| `Multiplayer.Tests/GeoRailGateTests.cs` | **Create** | Pins the shipped default OFF. |
| `Multiplayer.Tests/SurfaceRouterGeoscapeTests.cs` | **Create** | Router geoscape-hook dispatch + tactical precedence + drop. |

---

## Task 1 — Shared `SurfaceSeq` (UNIT-TESTED)

**Files:** Create `src/Network/Sync/SurfaceSeq.cs`; Create `Multiplayer.Tests/SurfaceSeqTests.cs`; Modify
`Multiplayer.Tests/Multiplayer.Tests.csproj`.

- [ ] **Red — write the failing test.** Create `Multiplayer.Tests/SurfaceSeqTests.cs`:

```csharp
using Multiplayer.Network.Sync;
using Xunit;

public class SurfaceSeqTests
{
    [Fact]
    public void Next_IsMonotonicPerSurface_StartingAtOne()
    {
        var s = new SurfaceSeq();
        Assert.Equal(1u, s.Next(10));
        Assert.Equal(2u, s.Next(10));
        Assert.Equal(1u, s.Next(20));   // independent stream per surface
        Assert.Equal(3u, s.Next(10));
    }

    [Fact]
    public void ShouldApply_LastWriterWins()
    {
        var s = new SurfaceSeq();
        Assert.True(s.ShouldApply(10, 1u));
        s.Mark(10, 1u);
        Assert.False(s.ShouldApply(10, 1u));   // duplicate
        Assert.True(s.ShouldApply(10, 2u));     // newer
        s.Mark(10, 2u);
        Assert.False(s.ShouldApply(10, 1u));    // stale arrives late → dropped
    }

    [Fact]
    public void ShouldApply_SurfacesAreIndependent()
    {
        var s = new SurfaceSeq();
        s.Mark(10, 10u);
        Assert.True(s.ShouldApply(20, 1u));     // a fresh seq on another surface is still new
    }

    [Fact]
    public void Mark_IgnoresStale_AndResetClears()
    {
        var s = new SurfaceSeq();
        s.Mark(20, 5u);
        s.Mark(20, 3u);                         // stale → ignored
        Assert.False(s.ShouldApply(20, 5u));
        Assert.True(s.ShouldApply(20, 6u));
        s.Reset();
        Assert.True(s.ShouldApply(20, 1u));     // reset clears the client guard
        Assert.Equal(1u, s.Next(20));            // reset clears the host stream
    }
}
```

- [ ] **Add the link** in `Multiplayer.Tests/Multiplayer.Tests.csproj` immediately after the `RequestDedup.cs`
  link (the `<Compile Include="..\src\Network\Sync\RequestDedup.cs">` line):

```xml
    <Compile Include="..\src\Network\Sync\SurfaceSeq.cs"><Link>Sync\SurfaceSeq.cs</Link></Compile>
```

- [ ] **Run (red):** `dotnet test Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj --filter FullyQualifiedName~SurfaceSeqTests`
  → **build error** `CS0246: The type or namespace name 'SurfaceSeq' could not be found`.

- [ ] **Green — implement.** Create `src/Network/Sync/SurfaceSeq.cs`:

```csharp
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// SHARED per-surface sequencing primitive (unified backbone spec §2.2, "ONE Seq"). HOST: monotonic
    /// per-surface seq source for live outcomes/deltas. CLIENT: last-writer-wins guard. PURE (no engine
    /// types) → unit-tested. One instance per live session per side; reset on teardown.
    ///
    /// Seq is assigned PER SURFACE (each surfaceId has an independent monotonic stream) so an outcome on one
    /// surface never suppresses an outcome on another. The host emits over a reliable, per-peer ORDERED
    /// transport, so a strictly-greater check is a sufficient last-writer-wins guard (a stale duplicate/
    /// re-send is dropped; nothing newer can be overtaken).
    ///
    /// Lifted verbatim from the tactical-only TacticalLiveSeq so BOTH the tactical live rail and the geoscape
    /// envelope surfaces share ONE seq abstraction. TacticalLiveSeq now derives from this and only adds the
    /// tactical-specific BeginDeployCaptureMission hook.
    /// </summary>
    public class SurfaceSeq
    {
        private readonly Dictionary<ushort, uint> _hostNext = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _clientLast = new Dictionary<ushort, uint>();

        /// <summary>HOST: take the next monotonic seq for a surface (starts at 1).</summary>
        public uint Next(ushort surfaceId)
        {
            _hostNext.TryGetValue(surfaceId, out var cur);
            uint next = cur + 1;
            _hostNext[surfaceId] = next;
            return next;
        }

        /// <summary>CLIENT: true if this seq is newer than the last applied for the surface. Does NOT mark
        /// (call <see cref="Mark"/> after a successful apply) so a failed apply can be retried by a re-send.</summary>
        public bool ShouldApply(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return seq > last;
        }

        /// <summary>CLIENT: record the last applied seq for a surface.</summary>
        public void Mark(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            if (seq > last) _clientLast[surfaceId] = seq;
        }

        public void Reset()
        {
            _hostNext.Clear();
            _clientLast.Clear();
        }
    }
}
```

- [ ] **Run (green):** same `--filter SurfaceSeqTests` → **`Passed!  - Failed: 0`** (4 tests).
- [ ] **Commit:** `feat(sync): add shared SurfaceSeq per-surface seq primitive (Inc1 rail unify)`.

---

## Task 2 — Shared `IntentDedup` (UNIT-TESTED)

**Files:** Create `src/Network/Sync/IntentDedup.cs`; Create `Multiplayer.Tests/IntentDedupTests.cs`; Modify
the test csproj.

- [ ] **Red — write the failing test.** Create `Multiplayer.Tests/IntentDedupTests.cs`:

```csharp
using Multiplayer.Network.Sync;
using Xunit;

public class IntentDedupTests
{
    [Fact]
    public void IsNew_FirstTrueRepeatFalse()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        Assert.False(d.IsNew(100, 1u));   // reliable-transport double-send
        Assert.True(d.IsNew(100, 2u));
    }

    [Fact]
    public void IsNew_SurfaceNamespaced()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        Assert.True(d.IsNew(101, 1u));    // same nonce, different surface → distinct
    }

    [Fact]
    public void IsNew_EvictsOldestPastCapacity_FloorIs16()
    {
        var d = new IntentDedup(capacity: 16);
        for (uint n = 1; n <= 16; n++) Assert.True(d.IsNew(100, n));
        Assert.True(d.IsNew(100, 17u));   // overflow evicts nonce 1
        Assert.True(d.IsNew(100, 1u));    // 1 was evicted → seen as new again
        Assert.False(d.IsNew(100, 17u));  // a recent one is still deduped
    }

    [Fact]
    public void Reset_Clears()
    {
        var d = new IntentDedup();
        Assert.True(d.IsNew(100, 1u));
        d.Reset();
        Assert.True(d.IsNew(100, 1u));    // after reset the same key is new again
    }
}
```

- [ ] **Add the link** in the test csproj after the `SurfaceSeq.cs` link:

```xml
    <Compile Include="..\src\Network\Sync\IntentDedup.cs"><Link>Sync\IntentDedup.cs</Link></Compile>
```

- [ ] **Run (red):** `... --filter FullyQualifiedName~IntentDedupTests` → **`CS0246` `IntentDedup` not found**.

- [ ] **Green — implement.** Create `src/Network/Sync/IntentDedup.cs`:

```csharp
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// SHARED intent de-duplicator (unified backbone spec §2.2, "ONE intent Dedup"). The reliable transport
    /// can double-send a client intent envelope; a double-applied intent would mutate twice. Keyed by the
    /// intent's (surfaceId, nonce); a bounded ring drops the oldest so memory stays flat over a long session.
    /// PURE (no engine types) → unit-tested.
    ///
    /// Lifted verbatim from the tactical-only TacticalIntentDedup (capacity floor 16); TacticalIntentDedup now
    /// derives from this. NOTE: the geoscape RequestDedup is keyed by (peerId, nonce) — a DIFFERENT abstraction
    /// tied to the legacy action relay — and is left untouched here; it is retired in a later slice when the
    /// geoscape intent surface adopts this shared (surfaceId, nonce) dedup.
    /// </summary>
    public class IntentDedup
    {
        private readonly int _capacity;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly Queue<ulong> _order = new Queue<ulong>();

        public IntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static ulong Key(ushort surfaceId, uint nonce) => ((ulong)surfaceId << 32) | nonce;

        /// <summary>True the FIRST time a (surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ushort surfaceId, uint nonce)
        {
            ulong k = Key(surfaceId, nonce);
            if (_seen.Contains(k)) return false;
            _seen.Add(k);
            _order.Enqueue(k);
            if (_order.Count > _capacity) _seen.Remove(_order.Dequeue());
            return true;
        }

        public void Reset()
        {
            _seen.Clear();
            _order.Clear();
        }
    }
}
```

- [ ] **Run (green):** `... --filter FullyQualifiedName~IntentDedupTests` → **`Passed!  - Failed: 0`** (4 tests).
- [ ] **Commit:** `feat(sync): add shared IntentDedup (surfaceId,nonce) primitive (Inc1 rail unify)`.

---

## Task 3 — Reparent the tactical seq/dedup onto the shared types (UNIT-TESTED regression)

**Files:** Modify `src/Sync/Tactical/TacticalLiveCodec.cs`. **No new test** — the existing
`TacticalLiveSeqTests` + `TacticalIntentDedupTests` (`Multiplayer.Tests/TacticalLiveCodecTests.cs:141-237`) are
the green gate (they call `Next/ShouldApply/Mark/BeginDeployCaptureMission` and `IsNew(capacity:16)` exactly).

- [ ] **Replace** the `TacticalLiveSeq` class body (`src/Sync/Tactical/TacticalLiveCodec.cs:1079-1125`) — keep the
  existing XML summary above it — with the subclass form (the `Next/ShouldApply/Mark/Reset` bodies + the
  `_hostNext`/`_clientLast` fields are now inherited from `SurfaceSeq`):

```csharp
    public sealed class TacticalLiveSeq : Network.Sync.SurfaceSeq
    {
        /// <summary>HOST: capture-time per-mission seq hook, called from the deploy capture. Intentionally a
        /// NO-OP: the host seq streams must survive a mid-mission deploy capture (never rewind). Recreating/
        /// resetting the stream here (the old `LiveSeq = new TacticalLiveSeq()`) rewound `_hostNext[TacTurn]`
        /// to 0, so the next turn re-emitted seq=1 and the client's strict `seq > last` guard dropped it ⇒
        /// "turn doesn't end". The stream is created exactly once per mission (constructor + OnMissionExit
        /// reset) and must survive the capture monotonically.</summary>
        public void BeginDeployCaptureMission()
        {
            // No-op by design: the host seq streams must survive a mid-mission deploy capture (never rewind).
        }
    }
```

- [ ] **Replace** the `TacticalIntentDedup` class body (`:1132-1158`) — keep its XML summary — with:

```csharp
    public sealed class TacticalIntentDedup : Network.Sync.IntentDedup
    {
        public TacticalIntentDedup(int capacity = 512) : base(capacity) { }
    }
```

  (`IsNew`, `Reset`, the ring + the `(surfaceId,nonce)` key are inherited from `IntentDedup`. The capacity floor
  of 16 lives in the base, preserving `IsNew_EvictsOldestPastCapacity`. `Network.Sync.SurfaceSeq`/`Network.Sync.IntentDedup`
  resolve from `Multiplayer.Sync.Tactical` exactly like the file's existing `Network.Sync.SurfaceRouter` references —
  no new `using` needed.)

- [ ] **Run (full suite, the regression gate):** `dotnet test Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj`
  → **`Passed!  - Failed: 0`**, total **= 801 + 8** (the 4 SurfaceSeq + 4 IntentDedup from Tasks 1–2) = **809**.
  In particular `TacticalLiveSeqTests` (5) + `TacticalIntentDedupTests` (3) stay green.
- [ ] **Build the mod** to confirm the live consumers still compile (they reference `TacticalDeploySync.LiveSeq`/
  `.IntentDedup` which keep their subclass types): `dotnet build Multiplayer\Multiplayer.csproj` → **`Build succeeded`**.
- [ ] **Commit:** `refactor(sync): reparent TacticalLiveSeq/TacticalIntentDedup onto shared SurfaceSeq/IntentDedup`.

---

## Task 4 — `GeoRailGate` default-OFF rollout switch (UNIT-TESTED)

**Files:** Create `src/Network/Sync/GeoRailGate.cs`; Create `Multiplayer.Tests/GeoRailGateTests.cs`; Modify the test csproj.

- [ ] **Red — write the failing test.** Create `Multiplayer.Tests/GeoRailGateTests.cs`:

```csharp
using Multiplayer.Network.Sync;
using Xunit;

public class GeoRailGateTests
{
    // Pins the SHIPPED default OFF: with the gate off the geoscape envelope rail emits nothing, so the
    // legacy 0x60-0x66 path is byte-for-byte unchanged (behavior-preserving additive rollout).
    [Fact]
    public void Enabled_DefaultsOff()
    {
        Assert.False(GeoRailGate.Enabled);
    }
}
```

- [ ] **Add the link** in the test csproj after the `IntentDedup.cs` link:

```xml
    <Compile Include="..\src\Network\Sync\GeoRailGate.cs"><Link>Sync\GeoRailGate.cs</Link></Compile>
```

- [ ] **Run (red):** `... --filter FullyQualifiedName~GeoRailGateTests` → **`CS0246` `GeoRailGate` not found**.

- [ ] **Green — implement.** Create `src/Network/Sync/GeoRailGate.cs`:

```csharp
namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Default-OFF rollout gate for the Increment-1 geoscape ENVELOPE rail (unified backbone spec §6.1,
    /// "Unify the rail", additive-first). While <see cref="Enabled"/> is false (the shipped default) the
    /// geoscape co-op rides ONLY the legacy raw packets (0x60-0x66) exactly as today — the host emits NOTHING
    /// on the new SyncEnvelope (0x67) geoscape surfaces, so behavior is byte-for-byte unchanged. Flip to true
    /// (a one-line dev edit + recompile) to ALSO mirror the migrated geoscape message(s) onto the shared
    /// envelope rail for in-game verification; the migrated wallet message is version-guarded + idempotent, so
    /// running both paths is safe. The legacy raw-packet send is retired only in a LATER, in-game-verified slice.
    /// </summary>
    public static class GeoRailGate
    {
        /// <summary>Master switch for the additive geoscape envelope rail. Shipped OFF.</summary>
        public static bool Enabled = false;
    }
}
```

- [ ] **Run (green):** `... --filter FullyQualifiedName~GeoRailGateTests` → **`Passed!  - Failed: 0`** (1 test).
- [ ] **Commit:** `feat(sync): add GeoRailGate default-OFF rollout switch (Inc1 geoscape envelope rail)`.

---

## Task 5 — `SurfaceIds.GeoWallet` + `SurfaceRouter.GeoscapeInbound` hook (UNIT-TESTED dispatch)

**Files:** Modify `src/Network/Sync/SurfaceIds.cs`; Modify `src/Network/Sync/SurfaceRouter.cs`; Create
`Multiplayer.Tests/SurfaceRouterGeoscapeTests.cs`. (`SurfaceIds.cs` + `SurfaceRouter.cs` are already linked in the test csproj.)

- [ ] **Red — write the failing test.** Create `Multiplayer.Tests/SurfaceRouterGeoscapeTests.cs`. (All router tests
  live in ONE class so the global static `TacticalInbound` they pin is mutated serially; each test sets it
  deterministically and clears it in a `finally`. `OnInbound` never derefs the `ISyncSink`, so `null` is passed.):

```csharp
using System;
using Multiplayer.Network.Sync;
using Xunit;

public class SurfaceRouterGeoscapeTests
{
    private static byte[] Wallet(byte[] inner)
        => SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot, inner);

    [Fact]
    public void GeoscapeHook_ConsumesItsSurface_WithDecodedPayload()
    {
        SurfaceRouter.TacticalInbound = null;   // tactical does not claim the geoscape range
        try
        {
            var router = new SurfaceRouter();
            byte gotSid = 0; byte[] gotPayload = null; int calls = 0;
            router.GeoscapeInbound = (sid, pl) => { gotSid = sid; gotPayload = pl; calls++; return true; };

            router.OnInbound(7UL, Wallet(new byte[] { 1, 2, 3 }), null);

            Assert.Equal(1, calls);
            Assert.Equal(SurfaceIds.GeoWallet, gotSid);
            Assert.Equal(new byte[] { 1, 2, 3 }, gotPayload);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void TacticalHook_TakesPrecedence_GeoscapeNotConsultedWhenTacticalClaims()
    {
        SurfaceRouter.TacticalInbound = (sid, pl) => true;   // tactical claims everything
        try
        {
            var router = new SurfaceRouter();
            int geoCalls = 0;
            router.GeoscapeInbound = (sid, pl) => { geoCalls++; return true; };

            router.OnInbound(7UL, Wallet(new byte[] { 9 }), null);

            Assert.Equal(0, geoCalls);   // tactical consumed it → geoscape never consulted
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void UnclaimedEnvelope_IsDropped_NeverThrows()
    {
        SurfaceRouter.TacticalInbound = (sid, pl) => false;   // tactical declines
        try
        {
            var router = new SurfaceRouter();
            router.GeoscapeInbound = (sid, pl) => false;       // geoscape declines too
            // No throw, no consumer → graceful drop.
            router.OnInbound(7UL, Wallet(new byte[] { 0 }), null);
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }

    [Fact]
    public void GeoscapeHook_NullByDefault_GarbageEnvelopeDropped()
    {
        SurfaceRouter.TacticalInbound = null;
        try
        {
            var router = new SurfaceRouter();   // GeoscapeInbound left null (inert)
            router.OnInbound(7UL, new byte[] { 0x01 }, null);   // too short to decode → dropped
        }
        finally { SurfaceRouter.TacticalInbound = null; }
    }
}
```

- [ ] **Run (red):** `... --filter FullyQualifiedName~SurfaceRouterGeoscapeTests` → **build error**
  `CS0117: 'SurfaceIds' does not contain a definition for 'GeoWallet'` and `CS1061: 'SurfaceRouter' does not
  contain a definition for 'GeoscapeInbound'`.

- [ ] **Green (a) — add the surface id.** In `src/Network/Sync/SurfaceIds.cs`, after the State-channel section
  (the `GeoSiteChannel = 5;` line, before the closing `}`):

```csharp

        // ─── Geoscape envelope surfaces (unified backbone spec §2.1 partition 0xA0-0xBF) — Inc1 rail unify ───
        // Migrated geoscape host→all messages ride the SAME 0x67 SurfaceRouter chokepoint as tactical, on ids
        // in the geoscape partition (non-overlapping with tactical 0x80-0x9F and the legacy action/channel
        // ids 1-30 above). Emitted only behind GeoRailGate; the legacy raw packet stays the primary path.
        public const byte GeoWallet = 0xA0;   // host→all versioned full-wallet snapshot (mirrors legacy WalletSync 0x63)
```

- [ ] **Green (b) — add the geoscape hook.** In `src/Network/Sync/SurfaceRouter.cs`, add the instance field after
  the `TacticalInbound` static field (after its `public static System.Func<byte, byte[], bool> TacticalInbound;`):

```csharp

        /// <summary>
        /// Geoscape replication hook (armed by the owning <c>SyncEngine</c> via <c>_router.GeoscapeInbound</c>).
        /// Geoscape envelope surfaces (spec §2.1 partition 0xA0-0xBF, e.g. <c>GeoWallet</c>) ride the SAME 0x67
        /// chokepoint as tactical. INSTANCE-bound (the geoscape handler is an instance method on SyncEngine that
        /// reaches that engine's applier state). Consulted AFTER the tactical fast-path so a tactical surface
        /// always wins its own id range. NULL by default → inert (additive). Signature:
        /// <c>(surfaceId, payload) -&gt; handled?</c>.
        /// </summary>
        public System.Func<byte, byte[], bool> GeoscapeInbound;
```

  and extend `OnInbound` — after the existing tactical block (`if (tac != null && tac(surfaceId, payload)) return;`):

```csharp
            // Geoscape fast-path (additive, instance-bound): a geoscape envelope surface (0xA0-0xBF) is
            // consumed here. Inert unless the owning SyncEngine armed the hook; consulted AFTER tactical so a
            // tactical surface always wins its own id range.
            var geo = GeoscapeInbound;
            if (geo != null && geo(surfaceId, payload)) return;
```

- [ ] **Run (green):** `... --filter FullyQualifiedName~SurfaceRouterGeoscapeTests` → **`Passed!  - Failed: 0`** (4 tests).
- [ ] **Build the mod:** `dotnet build Multiplayer\Multiplayer.csproj` → **`Build succeeded`**.
- [ ] **Commit:** `feat(sync): generalize SurfaceRouter with geoscape hook + GeoWallet surface id (Inc1)`.

---

## Task 6 — Engine wiring: arm the geoscape hook + gated wallet-on-envelope send (IN-GAME VERIFIED)

**Files:** Modify `src/Network/Sync/SyncEngine.cs`. **Not unit-tested** — `SyncEngine` binds `NetworkEngine` +
`GeoRuntime` (UnityEngine/Harmony) and is not in the test assembly. The routing logic it relies on is already
proven pure in Task 5; this task is verified in-game via the 2-instance DirectIP harness
(`multiplayer-second-instance-setup`).

- [ ] **Arm the geoscape hook.** In the `SyncEngine` constructor (`src/Network/Sync/SyncEngine.cs:69-73`), after
  `SyncRegistration.RegisterAll();`:

```csharp
            // Inc1 rail-unify: arm the SurfaceRouter geoscape fast-path so a geoscape envelope surface (0xA0+)
            // routes to this engine's appliers. Inert for traffic the host never sends (gated by GeoRailGate).
            _router.GeoscapeInbound = HandleGeoscapeEnvelope;
```

- [ ] **Add the instance dispatcher.** Add this method to `SyncEngine` (e.g. directly under `OnSyncEnvelope`, `:546`):

```csharp
        /// <summary>SurfaceRouter geoscape fast-path: returns true if this surface is a geoscape surface it
        /// consumed (so the router stops). Mirrors the tactical HandleTacticalEnvelope switch. The inner payload
        /// is the surface's own bytes (e.g. EncodeWalletSync output), routed to the EXISTING applier.</summary>
        private bool HandleGeoscapeEnvelope(byte surfaceId, byte[] payload)
        {
            if (surfaceId == SurfaceIds.GeoWallet)
            {
                // Behavior-identical to the legacy 0x63 path: OnWalletSync is host-guarded + version-guarded, so
                // applying via the envelope is idempotent (a same-version duplicate from the legacy packet drops).
                try { OnWalletSync(payload); }
                catch (Exception ex) { Debug.LogError("[Multiplayer] geo wallet envelope failed: " + ex.Message); }
                return true;
            }
            return false;
        }
```

- [ ] **Gate-mirror the wallet onto the envelope (broadcast path).** In `BroadcastFullWallet` (`:254-261`), capture
  the version once and additionally emit the envelope when the gate is on:

```csharp
        public void BroadcastFullWallet()
        {
            if (!_engine.IsHost) return;
            var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
            if (slots == null) return;
            ulong ver = ++_walletVersion;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                SyncProtocol.EncodeWalletSync(ver, slots)));
            // Inc1 rail-unify (additive, default OFF): ALSO mirror the SAME versioned snapshot onto the unified
            // 0x67 envelope rail. Same version ⇒ the client applies whichever arrives first and drops the other
            // (ShouldApplyWallet is strict >). Retiring the legacy 0x63 send above is a later, in-game-verified slice.
            if (GeoRailGate.Enabled)
                _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                        SyncProtocol.EncodeWalletSync(ver, slots))));
        }
```

- [ ] **Gate-mirror the wallet onto the envelope (Tick dirty-flush path).** In `Tick` (`:519-526`), replace the
  dirty-flush block with the version-captured + gated form:

```csharp
            if (_walletDirty)
            {
                _walletDirty = false;
                var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (slots != null)
                {
                    ulong ver = ++_walletVersion;
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                        SyncProtocol.EncodeWalletSync(ver, slots)));
                    if (GeoRailGate.Enabled)
                        _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                            SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                                SyncProtocol.EncodeWalletSync(ver, slots))));
                }
            }
```

- [ ] **Build + full test suite (no regression):** `dotnet build Multiplayer\Multiplayer.csproj` → `Build succeeded`;
  `dotnet test Multiplayer\Multiplayer.Tests\Multiplayer.Tests.csproj` → **`Passed!  - Failed: 0`** (809 total).
- [ ] **Commit (gate OFF — shipped behavior unchanged):**
  `feat(sync): wire geoscape wallet echo onto the 0x67 envelope rail behind GeoRailGate (default off)`.
- [ ] **In-game verification A (default OFF = no regression).** Build + deploy the mod; run the 2-instance DirectIP
  co-op session. Spend/gain resources on the host → confirm the client wallet still mirrors (legacy `0x63` path,
  `GeoRailGate.Enabled == false`). **Expected: identical to today, zero behavior change.**
- [ ] **In-game verification B (gate ON = new rail delivers).** Set `GeoRailGate.Enabled = true`, recompile + deploy,
  repeat. Wallet still mirrors (both paths live, idempotent). Add a temporary
  `Debug.Log("[Multiplayer] geo wallet via envelope")` in `HandleGeoscapeEnvelope` to confirm the envelope path is
  actually exercised on the client. **Expected: client wallet correct AND the geoscape-envelope log fires.** Then
  set `Enabled = false` again before merging (shipped default OFF). Record the result in the EOD handoff.

---

## Self-review

**Spec-coverage (Increment 1 "Unify the rail" elements → where handled):**
- ONE enveloped rail (`SyncEnvelope 0x67`): geoscape wallet now rides it (Task 5/6); already the tactical rail.
- ONE `SurfaceRouter` chokepoint: generalized with `GeoscapeInbound` so geoscape rides the same `OnInbound` (Task 5). ✓
- ONE per-surface `Seq`: `SurfaceSeq` extracted; tactical now rides it (Tasks 1, 3). Geoscape wallet is host→all
  version-guarded so needs no seq; geoscape *intent/outcome* surfaces adopt `SurfaceSeq` in Slices 3–4. ✓ (scoped)
- ONE intent `Dedup`: `IntentDedup` extracted; tactical rides it (Tasks 2, 3). Geoscape `RequestDedup` retired in
  Slice 4 (no geoscape intent migrated yet here). ✓ (scoped)
- BEHAVIOR-PRESERVING + ADDITIVE-FIRST + retire ≤1 surface per commit: nothing retired; gate default-OFF makes
  the shipped wire byte-identical; legacy `0x60`–`0x66` + `SequenceTracker` + `RequestDedup` untouched. ✓
- Generalize-don't-duplicate: tactical types become subclasses of the shared types (one implementation). ✓

**Placeholder-scan:** No `TBD`/`...`/"similar to"/"like above" in any code block — every Create block is a complete
file; every Modify block is a complete, locatable snippet with an exact insertion anchor (`file:line` cited).
`dotnet` commands and expected outputs are concrete.

**Type-consistency (checked against the real code read 2026-06-26):**
- `SurfaceSeq`/`IntentDedup` signatures match the lifted `TacticalLiveSeq`/`TacticalIntentDedup` verbatim
  (`Next(ushort)→uint`, `ShouldApply(ushort,uint)→bool`, `Mark(ushort,uint)`, `Reset()`; `IsNew(ushort,uint)→bool`,
  ctor `(int=512)` floor 16). `SurfaceSeq` is `public class` (non-sealed) so `sealed TacticalLiveSeq` can derive.
- `TacticalDeploySync.LiveSeq` (`:77`, type `TacticalLiveSeq`) + `.IntentDedup` (`:79`, type `TacticalIntentDedup`)
  and their reassignments (`:349-350,607,642-643`) + `BeginDeployCaptureMission()` (`:349`) stay valid: subclass
  is-a base, so `new TacticalLiveSeq()`/`new TacticalIntentDedup()` still assign; all members resolve (inherited + own).
- `SurfaceRouter.OnInbound(ulong, byte[], ISyncSink)` ignores the sink (verified `:29-37`) → tests pass `null`. The
  added `GeoscapeInbound` is `public System.Func<byte,byte[],bool>` matching `TacticalInbound`'s shape.
- `SyncEngine.OnWalletSync(byte[])` is `public` + host-guarded + version-guarded (`:243-251`), safe to call from
  `HandleGeoscapeEnvelope`. `_walletVersion` (`:27`), `_router` (`:50`), `_engine.BroadcastToAll`,
  `SyncProtocol.EncodeEnvelope`/`EncodeWalletSync`, `SyncKind.StateSnapshot`, `PacketType.SyncEnvelope`,
  `SurfaceIds.GeoWallet` all exist as cited. `RouteMessage`'s `SyncEnvelope` case already calls `OnSyncEnvelope` →
  `_router.OnInbound` (`:546`), so no `NetworkEngine` edit is needed.
- Test-project linkage: `SurfaceRouter`/`SurfaceIds`/`SyncProtocol`/`SyncKind`/`TacticalLiveCodec` already linked;
  the three new pure files get explicit `<Compile Include>` links (Tasks 1, 2, 4).
