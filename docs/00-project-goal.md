# 00 — Project Goal

[← Index](../README.md)

## Goal

- Design a multiplayer mod for Phoenix Point using the official **SDK** + **Harmony** patches.
- **Not** a traditional turn-based multiplayer where players wait for each other.
- Build a **cooperative campaign**: multiple players share one campaign, each controls different aspects of the same faction.

## Co-op Concept

- Different players control different soldiers.
- Different players granted permissions over distinct subsystems:
  - Soldier management
  - Equipment management
  - Base management
  - Research
  - Manufacturing
  - Recruitment
  - Aircraft management
  - Tactical combat control
- Host assigns permissions + soldier ownership through a management UI.

## Ownership + Permission Model (overview)

- **Ownership** = which soldiers a given player may command (tactical + roster).
- **Permission** = which campaign subsystems a player may operate.
- Both are **dynamically configurable** by the host at runtime.
- Full design of the permission system → [03 — Campaign Layer](03-campaign-layer.md).
