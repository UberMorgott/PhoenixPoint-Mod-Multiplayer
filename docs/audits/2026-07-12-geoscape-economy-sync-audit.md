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

## Haven-trade review minors (follow-up on the `89b52c7` ACCEPT verdict) — **CLOSED `419af82`**

- **Affordability boundary** — `HavenTradeReflection.TryTrade`'s funds gate read `ResourceUnit.RoundedValue`
  (= `CeilToInt`), so a sub-unit wallet balance (e.g. 4.3 -> 5) could pass a cost-5 trade the faction cannot cover
  (<1 overdraw at the boundary). Switched the FUNDS read to floor of the raw `ResourceUnit.Value` (`FloorWallet`) —
  conservative, still >= native which has NO gate. Stock read left as `RoundedValue` (funds is the overdraw surface).
- **Forward-compat pin** — added `GeoSiteTail` decode test with +3 trailing bytes AND a NON-empty stock: proves the
  reader consumes EXACTLY `stockCount` units then skips the future tail via the length-prefixed `recLen` (never
  mis-reads a trailing byte as a stock unit).

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
- Open LOWs — **all resolved 2026-07-12** (follow-up wave):
  - (1) **[CLOSED `0242c67`]** pure `ModifyDiplomacy` delta lag — HOST postfix on the SINGLE diplomacy funnel
    `PartyDiplomacy.Relation.Diplomacy` setter (PartyDiplomacy.cs:43-49; ModifyDiplomacy/SetDiplomacy/StartRelations
    all route through it) marks ch#4 dirty on every reputation write -> converges within a frame, not the hourly
    heartbeat. Models `ResearchAcquisitionDirtyPatches`; no wire change; hourly + forced-state event stay belts.
  - (2) **[CLOSED `57521d2`]** damaged stolen-craft HP — RECONCILED: the vehicle-HP surface already EXISTS as the
    **0xA6 GeoVehicleTravel WA-3 health tail** (`meta.Health` = Stats.HitPoints/MaxHitPoints; the audit table's
    "0xA6 = travel" was incomplete — it carries travel **+ HP**). The real miss is a spawn race: the one-shot
    change-only tail can land before the #6 identity mirror spawns the client vehicle (`ApplyTravelMeta.ResolveVehicle`
    no-op). Fix: a short HOST re-ship window (`GeoVehicleTravelMeta.NeedsReshipWindow`, `ReshipWindowPolls=3`)
    re-delivers the tail after the spawn. Converged onto 0xA6 — NO new rail, NO duplicate #6 HP tail.
  - (3) **[VERIFIED — no code]** aircraft `StructuralTargetResult` HP agreement (AircraftMissionOutcomeDef.cs:27):
    0x96 does **NOT** cover the aircraft structural target — it is a `StructuralTarget` **actor** (actor-registry
    netId, damaged via `StructuralTarget.ApplyDamage` through `StructuralTargetRootObjectDamageReceiver`), DISJOINT
    from the `DestructableBase`/`GuidInScene` leaf 0x96 patches (`DestructableDamageReceiver.ApplyDamage`). No sync
    added: the client-squad's transient LOCAL keep/destroy outcome is host-authoritative-corrected on the geoscape
    by GeoVehicleChannel #6 (keep/destroy = vehicle existence/tombstone) + the 0xA6 HP tail (kept-craft damage,
    closure 2). A StructuralTarget HP mirror would be a de-facto **new rail** (different identity system) for a
    purely transient, already-reconverged divergence -> declined per "converge, don't multiply / no new rail".

## Cross-reference

- Interception time-lock feature landed same day, commits `ebe766b` + `48f50e8` (geoscape usable for non-fighting players during air combat, time control locked).
- Residual LOW **[CLOSED `68f0915`]**: first-interception start-autosave-abort leaked the lock — the coroutine's
  `_interceptionGame.GameStopped()` (GeoLevelController.cs:1213) NREs on the still-null backing field (the lazy
  `InterceptionGame` property never ran on the abort path), so the GameStopped time-lock postfix never fired. HOST
  belt now closes the lock on `PhoenixSaveManager.ShowAutosaveError` (the one call the abort path always makes,
  before the OnStop NRE); existing GameStopped + outcome-33 Hide closes stay belts (all idempotent).
