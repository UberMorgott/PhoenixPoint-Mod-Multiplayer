# Multiplayer — project rules

## Multiplayer sync canon  [coding]
> ONE pattern for ALL host↔client sync. No new one-off rails. Full design: `docs/superpowers/specs/2026-06-27-multiplayer-sync-canon-design.md`
- **Host-authoritative outcome:** client never applies host-authoritative OUTCOME (damage/AP/ammo) locally — local action = suppress + send nonce-deduped intent; host is sole outcome authority.
- **One writer per field:** no parallel rails, no split-by-direction. New state rides the ONE generic versioned actor-state record (0x8F-style spine) on the 0x67 rail — adding a buff/field = zero new surface.
- **Statuses = inert display-only by contract:** pre-set `Applied=true`, seed null `[SerializeMember]` fields (atomicity), guard StartTurn/EndTurn/ApplyEffect/OnUnapply, per-status try/catch, HARD-exclude faction-flippers (MindControl/Zombified) + surface-owned. Never live-apply; no allowlist.
- **Presentation (anim/VFX) own channel, symmetric fire/melee/move:** broadcast to ALL; on the ORIGINATING client SHOOT presentation runs via the native ability (camera/animation), damage-neutered + echo-deduped (zero delay); one action = ONE synchronized causal beat (shot→impact→status), outcome applied at impact frame — never stat-before-anim, never serialized.
- **Converge, don't multiply:** existing side-rails fold into the canon incrementally; never add a new mechanism for what the canon already covers.
