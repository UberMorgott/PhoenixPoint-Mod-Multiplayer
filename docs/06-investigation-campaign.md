# 06 — Source Investigation: Campaign Layer

[← Index](../README.md)

> Identify campaign subsystem entry points + where permission checks can be injected.

## System Entry Points

- Research system entry points.
- Manufacturing system entry points.
- Base management entry points.
- Aircraft management entry points.
- Soldier management entry points.

## Permission Injection

- Determine where **permission checks** could be injected for each subsystem.
- Maps to the permission set in [03 — Campaign Layer](03-campaign-layer.md).
- Preferred: Harmony **prefix** that rejects the call when the acting player lacks permission.
