# 08 — Serialization

[← Index](../README.md)

## Can These Be Serialized?

- Soldier state
- Equipment
- Inventory
- Tactical mission state
- Campaign state

## Reuse Existing Systems

- Identify existing **save/load** systems that could assist synchronization.
- Goal: reuse the game's own serializers for state snapshots + client reconstruction rather than rebuilding from scratch.
- Snapshot use cases: client join / reconnect, full-state resync after divergence.
- **Practical realization:** reconnect-resync reuses the game's own save system to snapshot live state + reload all peers → [21 — Disconnect & Reconnect](21-disconnect-reconnect.md). Custom tactical serialization here is the fallback only if mid-battle save is unavailable.
