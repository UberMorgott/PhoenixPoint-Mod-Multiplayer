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

## Cross-reference

- Interception time-lock feature landed same day, commits `ebe766b` + `48f50e8` (geoscape usable for non-fighting players during air combat, time control locked).
- Residual LOW: first-interception start-autosave-abort can leak the lock until menu/reload reset backstops.
