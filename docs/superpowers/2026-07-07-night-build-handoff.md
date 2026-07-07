# Night build handoff — 2026-07-06/07

Autonomous night session on inner `main`, commits `aac9ba5`..`2dc9830` (+ this docs commit). NOT pushed.
Green: **1873 unit tests**, Core + Tests + mod builds 0 err / 0 warn (verified 2026-07-07 morning).
Roadmap reconciled: `docs\COOP-SYNC-ROADMAP.md` (STATUS rows + CURRENT POSITION + in-game checklist), `README.md`/`README.ru.md` checkboxes.

## Shipped (task → commit)

- **Envelope cutover** → `7e4076b` (+purge `226b3e8`): 0x67 envelope = SOLE geoscape action rail; `UseEnvelope` flag + legacy `0x60/0x61/0x62` + `RequestDedup` + `_hostSequence` DELETED, opcodes tombstoned; dead packet paths swept.
- **Reflection version-guard (Phase 5)** → `cacc217` + `876f063`: 19 CRITICAL AccessTools bindings checked at `OnModEnabled`; failure = one loud report + `CoopGuardBlocks` soft-gate on host/join.
- **rca-1 TFTV teardown guards** → `677b85f`: +5 role-independent guards on NRE-prone TFTV hooks in the between-levels window (`TFTVLogger.Error` always popups → storm); catalog `ClientTftvTeardownGuardTargets` (7 guards), pure `ShouldBindTftvGuard` tested.
- **rca-2 bind-gate audit** → `6998fb7`: last stale `if (_bound) return;` (GeoVehicleChannel.AttachHost) → GeoMap instance-compare rebind + full per-map reset (kills stale-tombstone craft despawn after reload); all other channels audited clean.
- **rca-3 reload-boundary reset** → `07ced82`: ONE idempotent `SyncEngine.ResetForReloadBoundary()` (pure registry, per-entry exception isolation) from `PrepareEntryFromBlobCrt`; version counters PERSIST both sides (pinned by `ReloadBoundaryVersionContinuityTests`); bonus `IntentDedup.ResetPeer` on rejoin.
- **rca-4 post-reload reseed** → `6bbb39d`: `ReseedOnceGate` once-latch armed at F2 transfer launch, consumed at RevealAll → `BroadcastFullWallet` + `BroadcastAllChannels` + `TimeSyncManager.HostReAnchorNow` + `TacticalDeploySync.HostReseedAfterLoad`.
- **rca-5 / rca-6** → SKIPPED_INVALID (already covered): freeze re-arms per level natively (`OnLevelStart` postfix fires on EVERY level rebuild); tactical reload seed = rca-3 once-guard clear + rca-4 belt.
- **New-campaign co-op bootstrap (P0, core+UI)** → `ac70963`: lobby NEW CAMPAIGN… → native `UIStateNewGeoscapeGameSettings` (host-only, tutorial forced OFF); durable gate at `GameSettings_OnConfirm` (`NewCampaignArmGuard` / existing `HostLoadGuard` for mid-session second campaign; client new-game blocked); autosave at first geoscape Playing frame → existing `LaunchTransfer` + 2-phase barrier, all peers load byte-identical start.
- **Campaign END sync** → `0d5da28`: host postfix on `GeoLevelController.TriggerGameOver` (sole ending funnel incl. TFTV) → `CampaignEnd` variant (=9) on 0x69 `{victory|defeat, victor Def.Guid}`; client suppresses local trigger, replays same native outro; degrade = notify → menu; mid-tactical queue-don't-drop; `CampaignEndFlow` pins ordering.
- **Tac live objectives (0x99 part 1)** → `5770d8e`; **scripted-events closure (part 2)** → `2b1ba18` (D11 spawn-funnel pin, D20 zone unlocks, 5 client TFTV script guards).
- **Containment intents 66/67** → `124875e`; **progression intents 68/69** → `a8f7550` + hardening `133f086` (slot rollback, mutoid guard, second-spec suppress).
- **Evacuation on 0x8E** → `e60d9cc`; **turret/crate/loot** → `56558d2` + `f8e8f29`; **0x8E wave-2** (mount/dismount, MC, frenzy, jet-jump, dash, ram) → `2958fa5`.
- **Intel modal 23 IntelNotice (gap AC)** → `02638a2` (notify-only on 0x69; TFTV verified non-suppressing).
- **Inc5 part 1 CRC divergence detection** → `07a0b35` (hourly `GeoCrcProbe` 0xA9, pure `DivergenceMonitor`, detection only); **part 2 returning-peer rejoin** → `debf271` (rides on-demand join path; `SessionLifecycle.StaleRejoinPeers` prune; others keep playing).
- **Window breaks into TS6 0x96** → `2dc9830` (actor-passage `TriggerWindowBreak` funnel; no new surface/kind).
- **Tests infra** → `559328c` (GameTests InternalsVisibleTo + thread-safe action registry).

## Pending in-game verify (morning, 2 instances)

Full checklist with steps = roadmap CURRENT POSITION → NEXT ACTIONS item 1. Headline order:
1. Envelope cutover regression sweep (legacy rail is gone — any client action failure is this).
2. New campaign from lobby (incl. TFTV extended new-game menu + BACK path + cinematics).
3. F2 mid-session load: no TFTV popup storm, no vehicle ghost-despawn, full reseed, load-into-tactical.
4. Campaign end: shared outro, defeat under blocking brief, host-quit-after-outro, mid-tactical queue.
5. Tac: objectives HUD live, zone unlocks, evac (incl. mounted), turret/crate/loot, wave-2 moves.
6. Rejoin after client kill; CRC probes silent across clean play + F2 + tactical.
7. Window vault breaks both directions; intel notice no-double under TFTV.

## Known concerns

- **rca-1:** guard set derived from static TFTV sweep, not a live popup log — 'load anywhere' soak must confirm storm fully gone. Bool-prefix skips also skip NATIVE original during null-level window (intended, display-only refreshes; documented at each guard).
- **rca-3:** intent-dedup full reset at F2 boundary could theoretically re-apply a straddling double-send — peers are barriered/quiescent there. TimeSync/tactical resets verified by code-path reading, not live run.
- **rca-4:** forced-deadline reveal path can skip one wallet snapshot for a still-loading client — next dirty-flush converges (versioned rails). Time re-anchor geoscape-only by design.
- **campaign-start:** latch consumed on a geoscape Playing frame even if fire guard closed (stale-arm safety) — host retries via button; lobby FSM not CommitStart-locked during bootstrap (nothing reads it besides PLAY).
- **campaign-end:** `_gameOverTriggered` latch read fail-closed (field rename → no broadcast → old F3 exit, logged); client mid-tactical + host immediate quit → queued notice dies with session (F3 path covers).
- **Deliberately NOT done:** belts B2-B4 removal (gated on clean B1 soak #2); P2 ownership isolation (USER DECISION); host-migration/VPN (standing exclusion); god-file decompose (rejected churn); interception LIVE brief (infeasible-by-design WA-3); auto-resync on CRC divergence (next increment); cosmetics 4d/6a/3b/6g/6h/1e (verified-no-code).
- **DLL not redeployed by this docs session** — deploy via `deploy.ps1` before the morning soak.
