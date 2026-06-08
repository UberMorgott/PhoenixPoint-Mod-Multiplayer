# 07 — Randomness

[← Index](../README.md)

> Most likely desync source. Investigate thoroughly.

## Find

- RNG implementation.
- `Random.Range` usage.
- Custom RNG systems.
- Combat hit calculations.
- Damage calculations.

## Decision

- Determine whether **host-only randomness** is feasible.
- If feasible: all RNG executes on host, results broadcast to clients (consistent with [01 — Authoritative Host](01-architecture-authoritative-host.md)).
- Flag any hidden/implicit RNG (AI decisions, procedural generation, loot, perception rolls) — these are the **prime desync risks** → [10 — Deliverables](10-deliverables.md).
