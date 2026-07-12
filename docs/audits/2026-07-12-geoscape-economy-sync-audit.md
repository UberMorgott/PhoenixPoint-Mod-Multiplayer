# Geoscape Economy / Research Sync Audit — 2026-07-12

Verdict: all subsystems follow the sync canon (host-authoritative, one writer, reward-free client echo). The
haven-resource-trade gap (§1/§2) is **RESOLVED** (commit `89b52c7`); the marketplace offer-regen concern (§3)
was **VERIFIED already-synced**. All three gaps closed 2026-07-12.

## Clean (no action needed)

- **DLC5 Marketplace** — `MarketplaceBuyAction` (`IHostOnlyApply`) -> `MarketplaceReflection.TryBuy`; offers ride ObjectivesChannel #7.
- **Manufacturing** — `QueueManufactureAction` host-auth; `ManufactureCompletedAction` reward-suppressed; items via InventoryChannel #1.
- **Loot / rewards / ItemStorage** — InventoryChannel #1 full-storage snapshot + `PollHostDrift` backstop.
- **Research (normal)** — ResearchChannel #2 SSOT + `Start`/`ResearchAction` `IHostOnlyApply`; client `CompleteEchoOnly` (no double-reward).
- **Separately-acquired research** (haven-raid steal, reverse-eng, event grants, ally/diplomacy, vivisection/capture) — 5 `ResearchAcquisitionDirtyPatches` + `PollHostDrift` backstop; `Kill`/`HarvestCapturedUnitAction` host-auth; all funnel into `Research.Completed` snapshot.
- **Unlocks** (recipes / facilities / augments) — UnlockChannel #3 monotonic def-sets + `PollHostDrift`; popups via report-modal mirror `0x69` + `GeoUiRefresh`.

## Gaps (all closed 2026-07-12)

1. **BUG — haven resource trade NOT synced** (only canon violation). **FIXED `89b52c7`.**
   - `GeoHaven.TradeResource` (`GeoHaven.cs:715`), reached from `UIStateTrade.ConfirmTrade` (`UIStateTrade.cs:67`) AND `HavenInteractionController`, intercepted nowhere (grep of mod src = 0 hits).
   - Client executed trade locally (haven stock minus, `faction.Wallet.Apply`) -> host unaware; wallet-echo `0xA0` rolled client back within ~1 in-game hour -> flicker + temporary desync.
   - Fix: `HavenTradePatch` intercepts `TradeResource` at the MODEL chokepoint (catches both UI callers). Client relays `HavenTradeAction` intent {siteId, offerRes, wantRes, offerAmount} + suppresses local apply; host `HavenTradeReflection.TryTrade` re-derives the ratio from its own `GeoFactionDef.ResourceTradingRatios` (immutable — rate host-authoritative, un-spoofable), gates affordability on HOST stock/wallet (`HavenTradeIntent.CanExecute`, stale intent = no-op), executes native `TradeResource`. Modeled on `MarketplaceBuyAction`/`MarketplaceReflection.TryBuy`. Result mirrors on wallet echo `0xA0` + ch#5 stock tail; host's own trades untouched (native runs), solo/mod-inactive untouched.

2. **Haven StockedResources not mirrored** — haven tail channel #5 carried only `{population, infested}` -> stale trade-screen stock on clients (host trades/restocks invisible). **FIXED `89b52c7`.**
   - `GeoHavenTail` gains a `Stock` field (rounded `{ResourceType, Amount}` mirror of `GeoHaven.StockedResources`), appended to the EXISTING bit0 haven payload (zero new flag/section — new state rides the existing spine). Host reads via `ByResourceType(x).RoundedValue`; client rewrites the pack value-only (Clear+AddUnique, no cascade). Dirty: `HavenTradePatch` postfix marks ch#5 on any host `TradeResource`; the ch#5 `PollHostDrift` full-DTO hash is the backstop (catches passive restocks).

3. **Marketplace offer-regen dirty-trigger unconfirmed** — offers ride ObjectivesChannel #7 (Batch-4). **VERIFIED already-synced (no code needed).**
   - `ObjectivesReflection.Snapshot` folds the live offer list in via `MarketplaceReflection.SnapshotOffers` (`ObjectivesReflection.cs:195`), so `ObjectivesChannel.PollHostDrift` (`SyncEngine.cs:2262`) hashes the offers with the rest of the #7 snapshot and re-marks ch#7 dirty on ANY host regen. Belt-and-suspenders: the ch#7 hourly-tick heartbeat (`_hourToken`) unconditionally re-marks #7 every in-game hour. Either mechanism re-flushes the full offer list to clients — host offer-regen lands, no stale list.

## Addendum 2026-07-12 — steal-aircraft / vehicle acquisition (follow-up audit)

Verdict: **SYNCED, canon-compliant** — all vehicle acquisition (manufactured / story-gift / stolen / haven-defense
reward) rides the single GeoVehicleChannel #6 spine (identity/spawn/tombstone poll, composite key incl. owner,
crew/loadout tails) + position `0xA5` / travel `0xA6` / explore `0xA7`. No one-off rails.

- Grant path (game): steal-aircraft = ownership TRANSFER, not a spawn — `AircraftMissionOutcomeDef.ApplyToVehicle`
  (`AircraftMissionOutcomeDef.cs:24-43`) -> `Reward.ExistingVehicles` -> `TakeOverVehicle` flips Owner->Phoenix
  (`GeoFactionReward.cs:511-528`), triggered from `GeoMission.Complete` -> `ApplyOutcomes` -> `GeoSite.cs:801`.
- Host completes mission: owner flip changes the #6 composite key -> `HostObserve` emits new identity -> client
  `SpawnMirrorVehicle` (`GeoVehicleIdentityReflection.cs:259`); old key tombstoned -> `DespawnVehicle`. Fleet tab +
  geoscape icon refresh. Stolen craft's weapons/modules ride the #6 loadout tail.
- Client-squad completes mission: client runs native `reward.Apply` locally (transient), #6 idempotent-by-key +
  diplomacy #4 absolute overwrite reconverge — same pattern as all mission rewards.
- Open LOWs (ranked, none blocking): (1) pure `ModifyDiplomacy` delta mirrors only on the hourly #4 heartbeat ->
  host->client lag <=1 in-game hour (masked by client's local apply); (2) current HP of a damaged stolen craft rides
  no channel -> mirror spawns at full BaseStats, visual drift until repair/next identity change; (3) latent: client
  local outcome relies on host/client agreeing on the aircraft `StructuralTargetResult` HP (keep/destroy divergence
  healed by #6 existence; verify 0x96 destructible mirror covers the aircraft structural target).

## Cross-reference

- Interception time-lock feature landed same day, commits `ebe766b` + `48f50e8` (geoscape usable for non-fighting players during air combat, time control locked).
- Residual LOW: first-interception start-autosave-abort can leak the lock until menu/reload reset backstops.
