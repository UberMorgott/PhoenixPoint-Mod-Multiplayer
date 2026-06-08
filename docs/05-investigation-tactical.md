# 05 — Source Investigation: Tactical Layer

[← Index](../README.md)

> Analyze Phoenix Point source + SDK. Goal: find entry points + stable IDs + Harmony intercept sites.

## Player Action Entry Points

- Locate **central entry points** for player actions:
  - Move
  - Shoot
  - Ability use
  - Reload
  - Inventory interactions
- Map the **action execution pipeline**:
  - Which methods are called?
  - Where can Harmony intercept them?

## Turn System

- How is the **player phase** implemented?
- How does the **enemy phase** begin?

## Tactical Actor Ownership

- How are soldiers identified?
- Which **stable IDs** are available for networking?

## Mission Initialization

- How are tactical missions loaded?
- How can mission state be **reconstructed** on a client?

## Output

- Class diagrams of tactical systems → feeds [10 — Deliverables](10-deliverables.md).
- Candidate Harmony patch locations.
