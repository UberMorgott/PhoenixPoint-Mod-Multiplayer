# Contributing

This is a co-op multiplayer mod for Phoenix Point. Before you touch sync code, internalize
the **sync canon** below — it is what keeps the project from becoming a pile of one-off
network paths that desync in the field. The canon here is the short version; the full design,
rationale, and status-safety contract live in the sync-canon spec
(`docs/superpowers/specs/2026-06-27-multiplayer-sync-canon-design.md`) and the living
`docs/COOP-SYNC-ROADMAP.md`. (`CLAUDE.md` carries the same canon in compressed form for
automated agents.)

## The sync canon (non-negotiable)

- **ONE unified rail for ALL host↔client sync.** Everything rides the `0x67 SyncEnvelope` +
  surface-router backbone. There are no parallel rails and no split-by-direction paths.
- **Host-authoritative; clients are display-only.** A client NEVER simulates. A local action
  = suppress locally + send a nonce-deduped *intent* to the host. The host decides; clients
  render the result.
- **One writer per field.** New synced state rides the existing generic versioned actor-state
  record (the `0x8F`-style spine) on the `0x67` rail. Adding a buff/field/status = zero new
  surface.
- **Converge, don't multiply.** New synced state uses the mechanism the canon already has.
  Never add a new rail/mechanism for something the canon already covers; fold existing
  side-rails into the canon incrementally instead of extending them.
- **Retired ids are permanent tombstones.** Never reuse a retired wire opcode or surface id.
  A dead rail that still has a live receiver is a future desync bug — physically delete dead
  code rather than leaving it "just in case."

## Where to add new synced state

- Put it on the **existing surface spine**, not a new mechanism.
- New buff / field / status → attach to the generic versioned actor-state record on the
  `0x67` rail. If you find yourself designing a new opcode, surface, or channel for it, stop —
  that is almost always the wrong answer under "converge, don't multiply."
- Statuses are **inert, display-only by contract** — never live-apply. The exact rules
  (`Applied=true` pre-set, atomic field seeding, faction-flipper/surface-owned exclusions,
  per-status isolation) are in the sync-canon spec §5; follow it exactly.

## Branch / build split (where your work lands)

The repo is split into a game-free core and a game-facing mod. This split decides *where you
build* and *which branch you land on*.

- **`Multiplayer.Core` — game-free, builds & tests anywhere.** Strictly BCL only (`System.*`).
  No `UnityEngine`, no game types. This is pure logic, transport, wire/protocol, sequencing/
  dedup. It builds and `dotnet test` passes on any machine with no Phoenix Point install.
  - If a type needs `UnityEngine` or an `Assembly-CSharp` type, it does **not** belong in Core.
- **The full mod — game-facing, needs the game to build.** It references the game DLLs
  (`Assembly-CSharp`, `PhoenixPoint.Modding`, `UnityEngine.*`, `HarmonyLib`), which are only
  available on a machine with a **legally owned** Phoenix Point install. On a machine without
  the game, unresolved-reference errors are **expected** — do not "fix" them by stubbing game
  types.

Branching:

- **Game-free work** (Core, tests, CI, pure logic, transport) → land on `main` or a
  short-lived branch. Verify it here: `dotnet build Multiplayer.Core` + `dotnet test`.
- **Game-facing work** (anything touching game/Unity DLLs) → land on the single long-lived
  branch **`refactor/game-facing`**, and compile-verify it on the game machine before merge.

## Commits

- Small, reviewable, **conventional** commits: `chore:`, `refactor:`, `test:`, `feat:`,
  `docs:`.
- **One logical change per commit.** Keep diffs easy to review.

## Never commit

- **Game DLLs or any copyrighted game assets** — not in CI, not anywhere in the repo.
- No Steam-emulator / Goldberg references. Local two-instance testing uses the developer's
  own second Steam-API session; no such tool is named or linked in the repo.
