# Tactical Concurrency & Conflict Resolution

> How simultaneous player actions coexist in Phoenix Point's turn-based tactical phase, and how conflicts (e.g. two soldiers ordered to the same tile) resolve cheaply. Extends the tactical sync model in [specs/01-design](../specs/01-design.md) and the action pipeline in [01-tactical-action-pipeline](01-tactical-action-pipeline.md).

## Why Simultaneous Play Works in PP

- PP player phase = **free activation**: no fixed per-soldier turn order; any owned soldier may act in any order while AP remains.
- Turn order exists only **between sides** (player side → enemy side); within the player side there is no sequencing constraint.
- Therefore all players may issue actions **in parallel** — no round-robin, no wait-for-your-turn.
- **Benefit:** removes idle waiting; players self-coordinate over voice (Discord) instead of through the game.
- The enemy side is **not** parallel: host runs full AI, then streams the resolved enemy turn to clients (see the sync pipeline in [specs/01-design](../specs/01-design.md)).
- ⚠️ **VERIFY (SDK):** confirm free activation (no enforced soldier order) + AP model in PP source → [open questions](../specs/03-open-questions-sdk.md).

## Conflict Cannot Be Resolved Pre-Host

- A client only sees **its own** intent. Client A has no knowledge of client B's pending click.
- Local pre-check catches only conflicts among a **single player's own** soldiers.
- **Cross-player** conflict (two players → same tile) is observable **only where both packets meet = the host**. No client-side shortcut exists.

## Host Conflict Resolution (cheap, reuses vanilla)

- Host action queue is **single-threaded** → **receipt order = authority**. First action to arrive wins.
- Tile occupancy is validated by the **game's own** pathfind/occupancy logic — we do **not** reimplement it.
- Flow:
  - Action A (move) accepted + executed → tile now occupied by vanilla state.
  - Action B (move, same tile) → vanilla validation rejects (tile occupied) → host relays an action-rejected result to B's owner → B's soldier stays put.
- This **is** the "cancel the later one" rule, achieved with zero new occupancy code.

## Race on Run-to-Empty-Tile (the real gotcha)

- Both players RUN to the **same empty** tile. Neither has arrived → tile is still empty → vanilla check passes for **both** → collision at destination.
- **Fix — destination reservation (~small):**
  - Host keeps a set of **reserved destination tiles** for in-flight moves.
  - Accept move A → add A's destination to the reserved set.
  - Move B targeting a reserved tile → reject (same action-rejected path).
  - **Clear reservation** on arrival **or** on interrupt (e.g. overwatch stops the soldier mid-path).
- Reservation covers the window vanilla occupancy can't (soldier in transit, tile not yet "occupied").

## Local Optimism (optional, keeps responsiveness)

- Not real state prediction — **visual only**, preserves the no-prediction core stance.
- On click: immediately draw path / ghost marker (cosmetic), action **not** committed.
- Host action-result (accept) → confirm + play real movement.
- Host action-reject → snap ghost back, brief "blocked" feedback.
- Cost is tiny; avoids the feel of input lag on every click while the round-trip resolves.

## Rule Summary

```
client click → (optimistic ghost, cosmetic) → ACTION to host
host queue (single-thread): receipt-order = authority
  → vanilla validation + ownership check + destination-reservation check
    → accept  → execute → ACTION_RESULT broadcast → clients apply
    → reject  → ACTION_REJECT to sender → snap back
```

- Authority = **host receipt order**. Loser of a tile race is always the later arrival.
- All heavy lifting reuses **vanilla validation**; the mod adds only the destination-reservation set + reject relay.
- Result application (incl. interrupt / reaction chains and enemy-phase replay) follows the action-result model in [specs/01-design](../specs/01-design.md) (§3 Network Protocol).

## Turn End / Phase Advance

- Problem: one shared player phase, many players → when does the player side end and the enemy side begin?
- **Hybrid model — ready-counter + host authority:**
  - Each player presses "End Turn" → toggles ready → `END_TURN_READY{ peerID }`.
  - Host UI shows the counter **N / total + who** is ready.
  - **Reversible:** a player may un-ready before the phase flips (wants to act again) — same pattern as the lobby ready toggle ([specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md)).
  - Enemy phase starts automatically once **all** owners are ready.
- **Host force-end:** host may end the phase at any time (AFK / stuck player) even if not all ready.
  - Force-end is gated by the **Force End Turn** / Full Commander permission → [03-campaign-layer](03-campaign-layer.md).
  - Regular players only toggle their own ready; they cannot force the whole phase.
- Lightweight per-turn barrier (no save transfer), unlike the one-time start barrier in [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- New message type `END_TURN_READY` → see transport message catalog in [engine/02-transport-layer](../engine/02-transport-layer.md).

## Related

- Action sync pipeline + result protocol → [specs/01-design](../specs/01-design.md).
- Ownership / who may command a soldier → [03-campaign-layer](03-campaign-layer.md), [specs/02-session-lifecycle-and-player-management](../specs/02-session-lifecycle-and-player-management.md).
- Geoscape-side concurrency + shared clock → [08-geoscape-concurrency](08-geoscape-concurrency.md).
