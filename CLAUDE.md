# Multipleer — project rules

## Multiplayer sync canon  [coding]
> ONE pattern for ALL host↔client sync. No new one-off rails. Full design: `docs/superpowers/specs/2026-06-27-multipleer-sync-canon-design.md`
- **Host-authoritative:** client NEVER simulates — local action = suppress + send nonce-deduped intent; client is display-only.
- **One writer per field:** no parallel rails, no split-by-direction. New state rides the ONE generic versioned actor-state record (0x8F-style spine) on the 0x67 rail — adding a buff/field = zero new surface.
- **Statuses = inert display-only by contract:** pre-set `Applied=true`, seed null `[SerializeMember]` fields (atomicity), guard StartTurn/EndTurn/ApplyEffect/OnUnapply, per-status try/catch, HARD-exclude faction-flippers (MindControl/Zombified) + surface-owned. Never live-apply; no allowlist.
- **Presentation (anim/VFX) own channel, symmetric fire/melee/move:** broadcast to ALL; initiator predicts locally (zero delay); one action = ONE synchronized causal beat (shot→impact→status), outcome applied at impact frame — never stat-before-anim, never serialized.
- **Converge, don't multiply:** existing side-rails fold into the canon incrementally; never add a new mechanism for what the canon already covers.
