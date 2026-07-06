# PhoenixPoint-Mod-Multiplayer — Execution Plan (Agent Handoff)

Actionable task list derived from the code review. Self-contained: an agent can execute
this without the chat history. Repo: `UberMorgott/PhoenixPoint-Mod-Multiplayer`
(~84.5k LOC C#, 460 files).

---

## Context & Environment Constraints (READ FIRST)

- **The game's source code is NOT needed for anything.** Decompiled notes under
  `docs/research/` are reference only; nothing compiles against them.
- **The current working machine has NO Phoenix Point install**, so the game's compiled
  assemblies (`Assembly-CSharp.dll`, `PhoenixPoint.Modding`, `UnityEngine.*`, `HarmonyLib`)
  are unavailable as build references.
- Consequence: the full mod project will NOT build here. Game-facing projects that
  reference those DLLs will show unresolved-reference errors — **this is expected; do not
  try to "fix" it by stubbing game types.**
- Work that is game-free (organization, pure logic, networking transport, tests, CI) is
  fully doable AND verifiable here. Game-facing edits can be *written* here but must be
  *compiled/verified later* on a machine with the game.

### Executability legend (tag every task you touch)
- 🟢 **HERE** — do it and verify it on this machine (no game needed).
- 🟡 **EDIT-HERE / BUILD-LATER** — edits can be made here; compilation/runtime verification
  requires the game machine. Land on a dedicated branch.
- 🔴 **NEEDS-GAME** — design/write only; real verification requires a running game.

---

## Ground Rules (guardrails — do NOT violate)

- **Never commit game DLLs or any copyrighted game assets** to the repo (CI or otherwise).
- **Keep `Multiplayer.Core` (new) strictly Unity-free and game-free** — only BCL types
  (`System.*`). If a type needs `UnityEngine` or a game type, it does NOT belong in Core.
- **Do NOT add any new sync "rail"/mechanism.** The project is converging on ONE unified
  `0x67 SyncEnvelope` + surface-router backbone ("sync canon"). New state must ride the
  existing generic versioned surface, not a new one-off path.
- **Never reuse a retired wire opcode / surface id.** Retired ids are permanent tombstones.
- **Prefer physically deleting dead code over leaving it "just in case."** A dead rail that
  still has a live receiver is a future desync bug.
- Use small, reviewable commits with conventional messages (`chore:`, `refactor:`, `test:`,
  `feat:`, `docs:`). One logical change per commit.
- Work game-free tasks on `main`/short-lived branches; land 🟡/🔴 game-facing work on a
  single long-lived branch `refactor/game-facing` so it can be built on the game machine.

---

## Phase 0 — Organization & Hygiene  (all 🟢, do first, ~hours)

- 🟢 **LICENSE** — DONE (PolyForm Noncommercial 1.0.0 with a `Required Notice:` line).
  - Verify the `Required Notice:` copyright line at the top of `LICENSE` has the real
    author name/handle + repo URL (replace any placeholder).
- 🟢 **README license block** — add a `## License` section: state PolyForm-NC-1.0.0
  (noncommercial, attribution required), link `LICENSE`, and add the DISCLAIMER
  (requires a legally owned copy of Phoenix Point; built on the official Snapshot Games
  modding framework; not affiliated with Snapshot Games).
- 🟢 **License badge** — add a shields.io static badge to README (GitHub will not auto-name
  this license): `![License: PolyForm-NC-1.0.0](https://img.shields.io/badge/license-PolyForm--NC--1.0.0-blue)`.
- 🟢 **Remove Goldberg from the public repo.**
  - `git rm` the following and scrub references: `tools/goldberg/steam_interfaces.txt`,
    `tools/SECOND-INSTANCE-SETUP.md`, `tools/install-goldberg-dll.bat`,
    `tools/launch-second-copy.bat`, `tools/make-second-copy.bat`, `tools/launch-coop-test.ps1`
    (keep `tools/COOP-TESTING.md` only after stripping Goldberg mentions).
  - Replace with a neutral one-liner in dev docs: local two-instance testing requires the
    developer's own second Steam-API session; no tool named or linked.
  - Rationale: repo owns no emulator binary/game code, but the *mention* pattern-matches as
    piracy-adjacent (reputational/publisher risk). The mod license is unaffected.
- 🟢 **Reorder docs in README** — list as-built (`docs/engine/`) FIRST; move the
  SUPERSEDED `docs/specs/01-design.md` into a "design history / lineage" subsection so new
  contributors don't read the stale architecture first.
- 🟢 **Add `CONTRIBUTING.md`** — one page: the sync canon (one rail, surfaces, "converge
  don't multiply"), where to add new synced state, and the branch/build split (Core builds
  anywhere; full mod needs game DLLs). Distill from `CLAUDE.md`.

---

## Phase 1 — `Multiplayer.Core` extraction  (🟢, keystone — unblocks CI + real tests)

> Goal: move all game-free logic into a standalone assembly that builds and tests with zero
> game/Unity dependency. This is what makes CI and honest testing possible here.

- 🟢 Create `Multiplayer.Core/Multiplayer.Core.csproj` targeting the same TFM as the mod's
  pure test assembly (mirror `Multiplayer.Tests.csproj` settings), referencing ONLY BCL.
- 🟢 Move genuinely pure logic into it (verify each moved file compiles with no
  `UnityEngine`/game usings):
  - Transport core that is pure `System.Net`: `src/Transport/DirectTransport.cs`,
    `ITransport.cs`, `TransportType.cs`, and the pure parts of `CompositeTransport.cs`
    (peer-id namespacing/allocator). Keep Steam/STUN-specific bits that need game/Steam
    types out of Core.
  - Wire/protocol + sequencing/dedup pure helpers: `MessageLayer/PacketType.cs`,
    `MessageSerializer.cs` (pure byte work), `SequenceTracker`, `RequestDedup`,
    `IntentDedup`, `SurfaceSeq`, `SurfaceIds`, `SyncProtocol` framing, chunk math
    (`SaveTransferCoordinator` pure predicates: `BarrierReleased`, `TryChunkIndex`).
- 🟢 Replace the current **mirror-test pattern** (logic copied into tests as
  "byte-identical" duplicates — see `Multiplayer.Tests/SaveTransferBarrierTests.cs`) with
  tests that reference the REAL `Multiplayer.Core` types.
  - Delete each mirrored/duplicated helper from the test file once the test points at the
    real Core symbol. ~88 test files currently contain mirror/pure/byte-identical copies;
    convert them.
  - Acceptance: `grep -ri "byte-identical\|mirror of" Multiplayer.Tests` trends toward zero;
    remaining tests import `Multiplayer.Core`.
- 🟢 Have the game-facing mod project reference `Multiplayer.Core` (single source of truth).
- **Acceptance (verifiable HERE):** `dotnet build Multiplayer.Core` and
  `dotnet test Multiplayer.Tests` both succeed on this machine with no game DLLs present.

---

## Phase 2 — CI  (🟢)

- 🟢 Add `.github/workflows/ci.yml`: on push/PR, `dotnet restore` + `dotnet build
  Multiplayer.Core` + `dotnet test` (Core + pure tests only — NOT the full mod, which needs
  game DLLs).
- 🟢 Add a build-matrix comment/skip explaining the full-mod build is out of CI scope
  (copyrighted game refs cannot be uploaded); optionally a self-hosted/game-machine runner
  note for the full build.
- **Acceptance:** the workflow goes green in GitHub Actions (cloud, no game).

---

## Phase 3 — Integration smoke test  (🟢)

- 🟢 Add an in-process test: stand up host + client `DirectTransport` on `127.0.0.1:14242`,
  push a few synced intents through the rail, assert version/sequence convergence and
  dedup-on-double-send (the reliable transport intentionally double-sends).
- 🟢 Keep it Core-only (no game types) so it runs in CI.
- **Acceptance:** test passes locally and in CI; catches sync-layer regressions the pure
  unit tests can't.

---

## Phase 4 — Legacy cleanup ("shlack")  (🟡 mostly; enum/const files are 🟢)

> The team already annotates dead code accurately; now delete it physically. 26
> `RETIRED / no senders / never wired / removed` markers across 10 files.

- 🟢 `MessageLayer/PacketType.cs` — collapse retired opcodes to a single tombstone comment
  block with the reserved ranges and a `do NOT reuse` note; remove scattered mentions.
  Retired: `WalletSync 0x63`, `StateSync 0x64`, tactical `0x21-0x24`, `0x27`.
- 🟡 `SyncEngine.cs` — confirm the dead `0x67` action-relay has **no remaining receiver**
  (not just no sender); delete both sides if present.
- 🟡 `SyncEngine.cs` — once `GeoActionRelay.UseEnvelope` is effectively always-on across
  paths, delete the legacy `RequestDedup` + the `UseEnvelope` flag; keep only the shared
  peer-aware `IntentDedup`. (Two dedup mechanisms = double the bug surface.)
- 🟡 `SyncEngine.cs` — finish folding the legacy `0x60/0x61/0x62` geoscape action rail into
  the unified `0x67` surface router per `COOP-SYNC-ROADMAP.md`, then delete the legacy
  branch entirely.
- 🟢 `SurfaceIds.cs` — the action id-space (`StartResearch=1`) overlaps the state-channel
  id-space (`InventoryChannel=1`), currently disambiguated only by "kind". Either move them
  to non-overlapping ranges OR introduce a typed `SurfaceId(kind, id)` wrapper so the
  compiler catches cross-space mistakes.
- **Acceptance:** enum/const edits build in Core context (🟢); `SyncEngine` edits build on
  the game machine (🟡). Marker count (`RETIRED/no senders/never wired`) drops materially.

---

## Phase 5 — Reflection version-guard  (🟡)

> 1581 `AccessTools` binding sites into game internals (579 statically cached). Today a game
> update that changes a signature causes a SILENT desync mid-game instead of a clear error.

- 🟡 In `MultiplayerMain.OnModEnabled` (after `PatchAll`), add a startup self-check: resolve
  a curated list of CRITICAL bindings (the methods/fields sync depends on) via `AccessTools`;
  on the first `null`, show a clear message:
  `"Multiplayer mod: incompatible Phoenix Point version (missing <Type.Member>). Update the mod."`
  and disable networking rather than proceeding.
- 🟡 Centralize the critical-binding list in one file (e.g. `src/Network/ReflectionGuard.cs`)
  so it's auditable and greppable; the names already exist scattered in the reflection code.
- 🟢 Add a Core-side unit test for the guard's pure aggregation logic (given a set of
  resolved/null bindings → correct verdict/message), independent of real reflection.
- **Acceptance:** guard logic unit-tested HERE; actual firing verified on the game machine.

---

## Phase 6 — Decompose god-files  (🟡; pure extracts are 🟢)

> Split behind unchanged public facades — no behavior change.

- 🟡 `SyncEngine.cs` (1829 LOC) → split concerns into partials/collaborators behind the
  `SyncEngine` facade: {wallet-echo, action-relay, state-channels, surface-router,
  vehicle-mirror}. Move any pure sub-logic into `Multiplayer.Core` (🟢, then testable).
- 🟡 `Network/Sync/State/GeoSiteReflection.cs` (2168 LOC) → separate the reflection-binding
  layer from the snapshot/apply logic (two classes/files).
- 🟡 `Sync/Tactical/TacticalDeploySync.cs` (1568) and `Network/Sync/EventReflection.cs`
  (1502) → same treatment: binding layer vs logic.
- **Acceptance:** pure extracts compile+test in Core HERE; full split compiles on the game
  machine; no functional diff.

---

## Phase 7 — Disambiguate the two `Sync` trees  (🟡)

- 🟡 Two directories are indistinguishable by name: `src/Sync/` (game-domain: Geoscape/
  Tactical) and `src/Network/Sync/` (replication/transport-side). Rename one for clarity,
  e.g. `src/Network/Sync/` → `src/Network/Replication/` (update namespaces + usings).
- **Acceptance:** builds on the game machine; pure moved files still build in Core HERE.

---

## Phase 8 — Reconnection / host-failover  (🔴 design + write; verify on game)

> Single-authority means a host crash breaks the session for everyone. Intentional leave is
> handled (`_intentionalDisconnect`, `HostLeaveHandler`); an *unexpected* host drop is not.

- 🔴 Design a host-loss flow: freeze clients + clear "host lost" UI state + allow the host to
  re-establish the session from the latest save (the save-transfer machinery already exists —
  reuse it).
- 🟢 Extract and unit-test in Core any pure decision logic (e.g. "should this drop be treated
  as host-loss vs intentional-leave?", timeout/backoff math).
- **Acceptance:** decision logic tested HERE; full reconnection verified in-game.

---

## Suggested Execution Order

1. Phase 0 (org) — all 🟢, immediate value, zero risk.
2. Phase 1 (`Multiplayer.Core`) — 🟢 keystone; unblocks everything below.
3. Phase 2 (CI) + Phase 3 (smoke test) — 🟢; now regressions in the pure layer are caught
   automatically.
4. Phase 4 (legacy cleanup) — start with the 🟢 enum/const files; queue 🟡 `SyncEngine`
   edits on `refactor/game-facing`.
5. Phase 5 (version-guard) → Phase 6 (decompose) → Phase 7 (rename) — 🟡, land on
   `refactor/game-facing`, build/verify on the game machine.
6. Phase 8 (reconnection) — 🔴, last.

## Definition of Done (per phase)
- 🟢 tasks: merged to `main`, CI green.
- 🟡 tasks: committed to `refactor/game-facing`, compile-verified on the game machine, then
  merged.
- 🔴 tasks: design doc + Core-tested decision logic merged; in-game verification tracked
  separately.

## Do-NOT-do checklist (repeat for the agent)
- ❌ Do not commit game DLLs or assets.
- ❌ Do not add `UnityEngine`/game references to `Multiplayer.Core`.
- ❌ Do not introduce a new sync rail/mechanism (converge onto `0x67`).
- ❌ Do not reuse any retired opcode or surface id.
- ❌ Do not re-add Goldberg / Steam-emulator references to the repo.
- ❌ Do not "fix" unresolved game references on this machine by stubbing game types.
