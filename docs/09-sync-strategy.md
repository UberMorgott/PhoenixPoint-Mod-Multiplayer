# 09 — Synchronization Strategy

[← Index](../README.md)

## Proposed Pipeline

```
Client Action
  → Network Message
  → Host Validation
  → Host Executes Original Game Logic
  → Host Broadcasts Action Result
  → Clients Reproduce Result
```

## Evaluate

- Assess whether this architecture is practical.
- If not practical, identify a more suitable architecture.

## Notes

- Pairs with authoritative-host model → [01](01-architecture-authoritative-host.md).
- "Host executes original game logic" = let unmodified game code run on host, capture results, broadcast — avoids reimplementing combat math.
- Clients **reproduce** results (apply broadcast outcome), they do **not** recompute — keeps RNG single-sourced → [07](07-randomness.md).
- Full-state snapshot fallback for divergence → [08 — Serialization](08-serialization.md).
