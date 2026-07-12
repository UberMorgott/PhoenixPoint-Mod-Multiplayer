# Geoscape Economy / Research Sync Audit — 2026-07-12

Verdict: all subsystems follow the sync canon (host-authoritative, one writer, reward-free client echo) **except haven resource trade**.

## Clean (no action needed)

- **DLC5 Marketplace** — `MarketplaceBuyAction` (`IHostOnlyApply`) -> `MarketplaceReflection.TryBuy`; offers ride ObjectivesChannel #7.
- **Manufacturing** — `QueueManufactureAction` host-auth; `ManufactureCompletedAction` reward-suppressed; items via InventoryChannel #1.
- **Loot / rewards / ItemStorage** — InventoryChannel #1 full-storage snapshot + `PollHostDrift` backstop.
- **Research (normal)** — ResearchChannel #2 SSOT + `Start`/`ResearchAction` `IHostOnlyApply`; client `CompleteEchoOnly` (no double-reward).
- **Separately-acquired research** (haven-raid steal, reverse-eng, event grants, ally/diplomacy, vivisection/capture) — 5 `ResearchAcquisitionDirtyPatches` + `PollHostDrift` backstop; `Kill`/`HarvestCapturedUnitAction` host-auth; all funnel into `Research.Completed` snapshot.
- **Unlocks** (recipes / facilities / augments) — UnlockChannel #3 monotonic def-sets + `PollHostDrift`; popups via report-modal mirror `0x69` + `GeoUiRefresh`.

## Gaps (open, ranked)

1. **BUG — haven resource trade NOT synced** (only canon violation).
   - `GeoHaven.TradeResource` (`GeoHaven.cs:715`), reached from `UIStateTrade` via `HavenTradeAbility.ActivateInternal` (`HavenTradeAbility.cs:14`), intercepted nowhere (grep of mod src = 0 hits).
   - Client executes trade locally (haven stock minus, `faction.Wallet.Apply`) -> host unaware; wallet-echo `0xA0` rolls client back within ~1 in-game hour -> flicker + temporary desync.
   - Fix pattern: intercept `TradeResource` -> client intent modeled on `MarketplaceBuyAction` + suppress local apply.

2. **Haven StockedResources not mirrored** — haven tail channel #5 carries only `{population, infested}` -> stale trade-screen stock on clients (host trades/restocks invisible). Cosmetic but compounds gap #1.

3. **Marketplace offer-regen dirty-trigger unconfirmed** — offers ride ObjectivesChannel #7 (Batch-4), but no verified dirty trigger when host regenerates offers (DLC5 var/periodic) -> possibly stale offer list on clients.
   - Verify `ObjectivesChannel.PollHostDrift` / `ObjectivesReflection`.

## Cross-reference

- Interception time-lock feature landed same day, commits `ebe766b` + `48f50e8` (geoscape usable for non-fighting players during air combat, time control locked).
- Residual LOW: first-interception start-autosave-abort can leak the lock until menu/reload reset backstops.
