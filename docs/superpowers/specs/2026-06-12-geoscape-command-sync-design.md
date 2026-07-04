# Geoscape Command Sync — Design (host-authoritative command-result relay)

**Status:** Approved (2026-06-12). **Mod:** Multiplayer (Phoenix Point co-op). **Milestone:** runtime geoscape state synchronization.

> **Thin spec by design.** The detailed engineering research already lives in `docs/research/03-campaign-layer.md`, `04-serialization.md`, `07-tactical-concurrency.md`, `08-geoscape-concurrency.md` and `docs/specs/01..03`. This document is the ARCHITECTURE INDEX + module map + staging — it references those rather than duplicating them. Each stage gets its OWN implementation plan; do not fold everything into one mega-doc or one mega-file.

## Problem
Co-op currently loads the same save into both instances, then the two geoscape sessions run independently and DIVERGE (a vehicle move / time change on one peer never reaches the other). We need host-authoritative runtime synchronization of geoscape gameplay.

## Decisions (locked)
- **Host-authoritative, NOT lockstep** (ref `docs/research/08` §authority; `specs/01` §1). Host is sole source of truth; clients reproduce results, never recompute (no client-side RNG).
- **Steady-state = replay RESULTS, not raw state** (ref `research/04` "Recommended Sync Approach"): lightweight `(actionType, actorId, resultData)` replication. Routine delta-state push is explicitly NOT the model (the game serializer has no delta tracking).
- **Full-save snapshot = divergence/reconnect/join fallback only** (ref `research/04`): reuse the already-working save-transfer + barrier path; not for routine sync.
- **Broad-intercept, prune-later** (user steer): build the generic relay pipeline ONCE, register the BROAD curated intercept list against it at once, then remove individual intercepts that misbehave — rather than hand-crafting one action end-to-end at a time. There is NO single generic geoscape mutation chokepoint (ref `research/08` §6 open questions) — interception is a curated per-method list plugged into the shared pipeline.
- **Modular, not monolithic** (user steer): small single-responsibility units (below), each independently testable.

## Architecture — command-result relay
```
CLIENT action (e.g. GeoVehicle.StartTravel)
  → Harmony Prefix intercept → encode CampaignActionRequest{type,actor,target,payload} → SendToHost → return false (block local exec)
HOST receives request
  → validate (ownership + legality + permission) → execute the REAL game method → broadcast result
ALL peers (host already applied; clients on receipt)
  → apply result locally (reproduce, no recompute)
HOST-originated action: intercept → execute locally + broadcast result to clients (no request roundtrip).
```
Conflict resolution = last-writer-wins by host receipt order (single-threaded host queue), per `research/08` §time + `research/07`.

## Module decomposition (single-responsibility units)
- **CommandRelay** — the generic pipeline orchestrator (intercept→encode→route→apply). Reusable across every intercepted action. Core seam.
- **CommandCodec** — pure-logic (de)serialization of `CampaignActionMessage{ActionId,ActionType,TargetId,Payload,Timestamp}`. Unit-testable, no UnityEngine. (Reuse/extend existing `MessageSerializer.Serialize/DeserializeCampaignAction`.) NOTE (as-built): the real envelope has **no `ActorId`** field — the actor rides in `TargetId`/`Payload`.
- **HostArbiter** — host-only: validate + execute real game method + broadcast result. Subscribes the existing `OnCampaignActionRequest` event (wired in Stage 1 via `CommandRelay.Wire`).
- **ClientApplier** — client-side: block local exec in Prefix; apply host result on `OnHostCampaignActionResult` (0x31) and log-only on `OnHostCampaignActionRejected` (0x32) — the rejected channel was split out so the originator never re-applies a refused action. (Wired in Stage 1.)
- **InterceptRegistry** — declarative table of intercepts (BROAD, prune-later). Each entry = `{ target method (AccessTools-resolved), encode-args fn, apply-result fn, required permission }`. Adding/removing an intercept = one registry entry — no core change.
- **PermissionGate** — turns the existing stub (`CampaignPermissionHelper.Check` = host=allow/client=block, flag arg ignored — ref `engine/03`) into REAL per-`playerGUID` flag enforcement using the already-defined `CampaignPermission` bits (`ControlSoldiers..FullCommander`, incl. `ControlTime 1<<7`). Keyed by GUID via `SessionManager`.
- **TimeSync** — shared clock (pause/speed) — STAGE 2 (own unit, own plan).
- **EventTransitionSync** — geoscape `EVENT` broadcast + forced `STATE_ENTER` transitions (yank-to-briefing) — STAGE 3 (own unit, own plan).

## Broad-intercept registry — initial list (ref `research/03` §6, `specs/01` §2.4 C1–C7)
Register ALL at once via CommandRelay, prune what misbehaves:
`Research.SetQueued` (+`ResearchElement.AddResearch`), `ItemManufacturing.EnqueueItem` (+`RemoveFromQueue`), `GeoPhoenixBase.ConstructFacility`/`RemoveFacility`/`RepairFacility`, `GeoVehicle.StartTravel`/`AddEquipment`/`AddCharacter`, `GeoCharacter.SetItems`, `GeoPhoenixFaction.HireNakedRecruit`, `GeoFaction.KillCharacter`. Prune criteria: non-deterministic, double-applying, or purely-local (no sync needed).

## Reused existing infrastructure (ref audit)
Packets `CampaignActionRequest 0x30`/`Approved 0x31`/`Rejected 0x32` + serializers + `NetworkEngine.SendCampaignAction`/`ApproveCampaignAction`/`RejectCampaignAction` + `RouteMessage` events `OnCampaignActionRequest`/`OnHostCampaignActionResult`/`OnHostCampaignActionRejected` + `ActionValidator` campaign permission map + `SessionManager` GUID/slot identity + `CampaignPatches` AccessTools intercept pattern. `CampaignActionResult 0x33` / `CampaignStateUpdate 0x34` are stubs — `CampaignStateUpdate` payload/flow is UNDEFINED in docs (open design item); Stage 1 uses result-replay (Approved/Rejected carry the result), not state push.
> **As-built (2026-06-13):** Stage 1 is IMPLEMENTED and wired — `CommandRelay.Wire` (called from `src/UI/MultiplayerUI.cs:189` host / `:310` client) subscribes `HostArbiter.HandleRequest`, `ClientApplier.HandleResult` (0x31), `ClientApplier.HandleRejected` (0x32, log-only). `NetworkEngine.BroadcastCampaignActionResult` (`:288`) fans an approved action out via 0x31. `0x33`/`0x34` receive-cases remain TODO stubs (`NetworkEngine.cs:530-536`). See `docs/research/00-current-state.md`.

## Staging by risk
- **STAGE 1 — command actions + real permissions** (THIS milestone's first plan). Hook points are KNOWN (the curated list), skeleton exists. No SDK unknowns. Deliverable: client geoscape actions replicate host-authoritatively + per-GUID permission enforcement live. First vertical proof: `GeoVehicle.StartTravel` (the reported broken case).
- **STAGE 2 — time/clock sync** (`TIME_STATE`: pause/speed, shared host clock, `ControlTime` gate, auto-pause on decision events). RESEARCH-GATED: PP time-flow API is an open SDK question (`research/08` §6). Own plan after a decompile dive.
- **STAGE 3 — events + forced transitions** (`EVENT` informational/decision; `STATE_ENTER` hard yank-to-briefing). HIGHEST SDK-unknown: event-generation hook points + UI state-stack push/pop API are open (`research/08` §6, `specs/03`). Own plan after a decompile dive.

## Open SDK questions (carry from docs — gate Stages 2–3)
- Time-flow API (pause/speed) — `research/08` §6.
- Geoscape event-generation hook points — `research/08` §6.
- Global state-machine + UI state-stack push/pop API (for `STATE_ENTER`) — `research/08` §6, `specs/03`.
- Each intercept's exact method site/signature confirmed against the runtime assembly before hooking (`specs/03`).

## Testing
- Pure-logic TDD (no UnityEngine): CommandCodec round-trips, HostArbiter validate/dispatch logic, InterceptRegistry lookup, PermissionGate per-GUID flag checks. Linked into `Multiplayer.Tests` (xUnit).
- Engine seams (real Harmony intercepts, real game-method execution, cross-instance apply): manual 2-instance in-game run.

## Out of scope (this milestone)
Tactical sync (separate future milestone, same pattern). Routine delta-state push. Reconnect/divergence-resync beyond reusing the existing save-transfer path. The phase-2 loading-overlay cosmetic bug (tracked separately on `feat/coop-loading-overlay`).
