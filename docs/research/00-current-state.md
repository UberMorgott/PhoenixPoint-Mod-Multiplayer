# Multipleer — Current State (status note)

> Single-glance "where are we now" for the co-op mod. Updated as the as-built state moves.
> Companion to the design index (`docs/superpowers/specs/2026-06-12-geoscape-command-sync-design.md`)
> and the staging plans (`docs/superpowers/plans/`). Engine as-built detail: `docs/engine/`.

## CURRENT STATE — 2026-06-13

- **Branch:** `feat/geoscape-command-sync`. HEAD = `c5e2b2a` *feat(geo-sync): CommandRelay + StartTravel intercept end-to-end (first vertical proof)*.
- **Uncommitted working-tree changes** (4 src files + 2 new docs + goldberg tooling) — see "Undocumented-but-shipped" below; NOT yet committed.

### Done + in repo (geoscape command sync — Stage 1)

- **CommandSync layer fully present** `src/Network/CommandSync/` (8 files): `CommandCodec`, `PermissionGate`, `InterceptRegistry` (all pure, test-linked) + `HostArbiter`, `ClientApplier`, `CommandRelay`, `CommandExecutor`, `GeoBridge` (engine seams, build+manual).
  - `CommandRelay.Wire(NetworkEngine.Instance)` called from `src/UI/MultiplayerUI.cs:189` (host start) and `:310` (client connect). Idempotent (detaches prior wiring).
  - Wires THREE engine events: `OnCampaignActionRequest → HostArbiter.HandleRequest`, `OnHostCampaignActionResult (0x31) → ClientApplier.HandleResult`, `OnHostCampaignActionRejected (0x32) → ClientApplier.HandleRejected` (log-only, NO apply). The rejected channel was split out (`e665c8c`) so the originator never re-applies a host-refused action. **(Stage-1 plan text predates this split — it described one channel applying every result; the shipped code is the corrected form.)**
  - `CommandRelay.ApplyResult` `CommandRelay.cs:65-83` sets `[ThreadStatic] _applying` and calls `CommandExecutor.Execute`; `IsApplying` guard lets the intercept prefix pass a re-entrant apply through.
  - `CommandExecutor.Execute` switch has **only `StartTravel`** today (`CommandExecutor.cs:17`); `default → LogWarning`. Other curated rows (C1–C7) are registry-dormant or pending.
  - `NetworkEngine.BroadcastCampaignActionResult` `NetworkEngine.cs:288` fans an approved action to all peers via `0x31`.
- **First vertical proof = `GeoVehicle.StartTravel(List<GeoSite>)`** — `src/Harmony/StartTravelInterceptPatch.cs`. Client → encode + relay + block; host → execute + postfix broadcast.
- **`CampaignActionType` enum** `MessageSerializer.cs:533-549` ends at `StartTravel = 13`. No `SetTimeState` yet.
- **Permissions:** real per-GUID gate `PermissionGate` + `PermissionManager` (`ControlTime = 1<<7`, `FullCommander = 1<<9` override). See FullCommander interim below.

### Stubs / NOT implemented

- **`0x33 CampaignActionResult` / `0x34 CampaignStateUpdate` receive-cases are TODO stubs** — `NetworkEngine.cs:530-536`. `BroadcastCampaignState(byte[])` `:295` exists (generic framing) but no peer applies it.
- **Time / clock sync — ZERO code in `src/`.** Only the `ControlTime` permission BIT is defined (`PermissionManager.cs:18`); no Timing patch, no time action, no clock broadcaster exists. → the new time-sync increment-1 plan is **greenfield, not duplicating anything**.
- **Per-player permission MENU** — not built; deferred (vision in `03-campaign-layer.md` §Roadmap + `specs/02` §4). Interim = FullCommander-for-all.

### Undocumented-but-shipped (uncommitted working tree, 2026-06-13)

- **FullCommander default grant** (interim "allow everything", no perm menu yet):
  - `SessionManager.cs:218` — joining client granted `FullCommander` keyed by `PlayerGuid` after JOIN (PlayerGuid only bound post-`AddClient`); mirrors mask onto `ClientInfo.Permissions` for the roster.
  - `SessionManager.cs:356` — host self-entry granted `FullCommander`; host-row `Permissions` now reflects the real mask (was hardcoded `0`).
  - **Fixes:** clients previously had no permission entry → `HasCampaignPermission` returned `false` → host rejected ALL client `StartTravel`. (Documented in `03-campaign-layer.md` §Interim.)
- **Lobby exit guards** `src/Harmony/MainMenuPatches.cs`:
  - `LobbyEscapeLeavePatch` **renamed → `LobbyEscapeSuppressPatch`**: Escape inside the open lobby now SWALLOWED (no Options, no leave/teardown). Previously Escape ran `OnLobbyLeave` (accidental Escape broke the session).
  - **NEW `LobbyCancelSuppressPatch`**: prefixes `HomeScreenViewState.OnInputEventInternal` to swallow the "Cancel" (RMB/back) gesture inside the lobby so back-navigation can't tear down the menu under the overlay. Gated by `IsLobbyOpen` → inert everywhere else. **Leaving the lobby is ONLY via the LEAVE button.**
- **DirectTransport connect-fail diagnostic** `src/Transport/DirectTransport.cs`: the previously-silent `catch` on DirectIP connect now logs the real failure (`SocketException` → includes `SocketErrorCode`) via a reflection-based `LogError` (file stays Unity-free for the test link; no-op/Console fallback under tests).

### Active next step

- **Time Sync — Stage 2 Increment 1 (host-authoritative time)** — plan `docs/superpowers/plans/2026-06-13-time-sync-stage2-increment1.md`, grounded in `docs/research/12-time-flow-and-sync-seams.md`. Adds `SetTimeState = 14`, client pause/speed intercepts, client hourly-sim suppression, and a continuous `0x34` clock mirror (`RecordInstanceData`/`ProcessInstanceData`). Verified to NOT overlap any existing code.

### Deferred / out of scope (now)

- Time-sync **Increments 2 (world-state delta broadcast) + 3 (route base/research/manufacture/squad inputs)**.
- **Per-player permission menu** (granular per-resource rights, aircraft/soldier ownership) — `03-campaign-layer.md` §Roadmap, `specs/02` §4.
- Broaden the curated intercept registry beyond `StartTravel` (Stage-1 plan Task 7 — most rows are `SignatureConfirmed = false` dormant or absent in this build).
- Tactical sync; reconnect/divergence-resync beyond the existing save-transfer path.

### In-game verification status (2-instance)

- **Pending / to re-test** (per `multipleer-second-instance-setup`):
  - host→client `StartTravel` mirror ✓ (first proof committed); client→host `StartTravel` after FullCommander grant — **re-test pending** (the grant is uncommitted).
  - Lobby `Escape` / RMB-`Cancel` guard — in-game pending.
  - DirectTransport connect-fail logging — surfaces the real socket error in `Player.log`, pending live confirm.
