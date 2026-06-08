# 03 — Campaign Layer Design

[← Index](../README.md)

## Shared Campaign

- Campaign remains **shared** across all players.
- Host assigns permissions to connected players.

## Permission System

- Example permission set:
  - Control Assigned Soldiers
  - Manage Equipment
  - Manage Bases
  - Manage Research
  - Manage Manufacturing
  - Manage Recruitment
  - Manage Aircraft
  - Control Time (pause / speed the geoscape clock → [19](19-geoscape-concurrency.md))
  - Force End Turn (end the tactical player phase for everyone → [17](17-tactical-concurrency.md))
  - Full Commander Access
- Permissions must be **dynamically configurable** at runtime.
- Host assigns these in a dedicated **Player Management** screen (separate from the Roster) → [20](20-player-management-persistence.md).

## Enforcement

- Permission checks injected at campaign subsystem entry points (Harmony prefix → reject if no permission).
- Entry points to patch → [06 — Investigation: Campaign](06-investigation-campaign.md).
