# Multiplayer Sync Canon -- single host<->client synchronization pattern

## 1. Problem

- Tactical netcode drifted into ~10 inconsistent mechanisms for the same kinds of state ("spaghetti"), causing whack-a-mole bugs.
- Goal: ONE canonical pattern all sync conforms to, so new buffs/debuffs/fields work with zero new wiring and bugs are prevented by invariants, not patched case-by-case.

## 2. Audit findings (current fragmentation)

All 17 tactical surfaces (0x80--0x91) already ride ONE rail (0x67 SyncEnvelope) -- the rail is fine; **field ownership** is fragmented.

| Area | Fragmentation |
|------|---------------|
| **Position** | 2 parallel rails (0x83/0x86 dedicated + 0x8F POS bit), duplicated walk/teleport thresholds |
| **AP/WP** | 3 writers (tac.damage 0x88, 0x8F flush, ClientApPreserveGate) |
| **Health** | Split by direction: down/death via tac.damage 0x88; up/heal/drift via 0x8F HEALTH |
| **Statuses** | 3 live mechanisms (tac.damage live-apply, 0x8F inert reconcile, 0x8D overwatch) + a DEAD unused allowlist policy |
| **Attack-start anim** | 2 asymmetric surfaces (0x90 fire = full replay; 0x91 melee = STUB), broadcast at different choke points; client also locally predicts fire then de-dupes echo |
| **Ordering** | Per-surface monotonic seq only; NO cross-surface barrier (root of fire-before-damage bug) |

### Precedents in-repo to copy

- Geoscape already unified onto ONE rail (0xA0/0xA1) + a `StateChannelRegistry` of versioned channels; legacy packets (0x63/0x64) retired.
- `tac.damage` (0x88) is a good "single funnel": all damage sources -> one `ApplyDamage` -> one surface -> one client apply.

## 3. The canonical pattern -- host-authoritative, 3 layers, one owner per field

### Layer 1 -- INPUT (client -> host intent)

- Every client action suppressed locally, sent as a nonce-deduped intent.
- ONLY host simulates; client never simulates.
- One common intent shape for all abilities.

### Layer 2 -- STATE (host -> client, ONE generic channel)

- ALL actor/world state rides ONE versioned generic actor-state record (the 0x8F-style spine) on the 0x67 rail.
- Covers: pos, facing, AP/WP, health, all body-part HP, ALL statuses, stance/pose.
- ONE writer per field; no parallel rails, no split-by-direction.
- Client applies as DISPLAY-ONLY mirror (inert + atomic + guarded).
- New field/buff rides automatically -- zero new surface.

### Layer 3 -- PRESENTATION (host -> client visual events)

- Animations/VFX on their own channel, SYMMETRIC across fire/melee/move.
- Broadcast to ALL peers; the initiating client PREDICTS locally for zero perceived delay.
- One action plays as ONE synchronized beat in causal order (shot -> impact -> status-appears).
- Outcome applied at the presentation's impact frame.
- Never stat-before-anim; never artificially serialized.

## 4. Invariants (enforced rules)

1. **Host-authoritative** -- client NEVER simulates; local action = suppress + intent; client is display-only.
2. **ONE writer per field** -- no parallel rails, no split-by-direction.
3. **One generic versioned state record** -- new state rides it automatically; no new ad-hoc surface for something the spine covers.
4. **Statuses/effects = inert display-only mirror BY CONTRACT** (see S5) -- never live-applied; no allowlist.
5. **Presentation separated from state** -- broadcast to all, initiator-predicted, played as one synchronized causal beat; outcome applied at impact frame.
6. **Everything on the one 0x67 SyncEnvelope rail** with versioned channels (geoscape is the precedent); ad-hoc parallel surfaces are retired/converged.

## 5. Status safety contract (why generic mirroring is safe)

**Root cause of past addon-tree corruption** (commit `01c2c30`): a HALF-MUTATION -- inert status `OnApply` dereferenced a null `[SerializeMember]` field (`_slotNames`) causing NRE after it had already subscribed an enemy `AddonsManager` event, leaving an inconsistent addon tree that caused later fire-coroutine hang / HUD lock.

Safety rests on **INERTNESS + ATOMICITY**, not type filtering. The contract:

1. **Pre-set `Status.Applied=true`** before apply (engine's inert/deserialize branch); ABORT mirror if it can't be set -- never live-apply.
2. **ATOMICITY** -- seed every null `[SerializeMember]` field the inert `OnApply` touches (e.g. `_slotNames`, `_damageAccum`) so it runs to completion.
3. **Guard `StartTurn`/`EndTurn`/`ApplyEffect`/`OnUnapply`** for mirror instances -- no DoT tick, no modifier revert.
4. **HARD-EXCLUDE faction-flippers** (`MindControl`, `Zombified`) and surface-owned (`Overwatch`) even when visible -- wrong-faction actor is worse than a missing icon.
5. **Per-status try/catch isolation** -- one bad status never aborts the actor-state apply.
6. **Authority boundaries respected** -- death/DoT-damage, AP/WP, overwatch keep their authoritative owner; the generic mirror drives DISPLAY only.
7. **Only safe while client stays sim-frozen.**

## 6. How this fixes the open bugs

Fixes fall out of invariants, not point fixes.

| Bug | Fix via invariant |
|-----|-------------------|
| **A -- cover pose missing on client** | Invariant 3: stance/pose carried in / re-derived at the generic record's apply (client re-runs engine's own `GetBestIdleCoverPoseAt` at mirror move-completion; sim-freeze otherwise skips it) |
| **B -- fire anim after damage** | Invariant 5: presentation broadcast + ordering barrier so outcome applies at impact; initiator predicts locally |
| **C -- statuses partial / no enemy body-part doll** | Invariants 3+4: ALL statuses mirrored generically as inert (no allowlist); body-part HP already in the record; doll re-derives from `StatChangeEvent` |

## 7. Migration stance

- New code conforms to the canon only.
- Existing side-rails (position dual-rail, AP/WP triple-writer, health split, melee stub, dead allowlist) converge into the canon INCREMENTALLY -- never rewrite working code wholesale.
- Never add a new mechanism for something the canon already covers.

## 8. Alignment

- Realizes the roadmap's "turn-as-pure-state" direction.
- Mirrors the geoscape one-rail precedent.
- Cross-reference: `docs/superpowers/plans/2026-06-25-multiplayer-tactical-fullstate-spine-roadmap.md`.
