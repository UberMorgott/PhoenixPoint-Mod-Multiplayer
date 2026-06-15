# Co-op Sync v2 — Host-authoritative state echo (frozen-client model)

Supersedes the client command-replay half of `2026-06-15-coop-action-sync-engine.md`. Currency echo (wallet) and the client→host intent relay + PermissionGate + bridge harness are KEPT.

## Root causes (verified in-game + decompile, 2026-06-16)
- **Client does NOT simulate the geoscape.** `TimeSyncManager.WriteClock` (TimeSyncManager.cs:475-507) overwrites the client `Timing` every frame via `Timing.ProcessInstanceData` with `OwnNow=0` and fires NO events / reschedules nothing (comment :471-473). So client research/manufacturing/vehicles/events/hourly-tick NEVER advance locally — client state changes ONLY through our sync. Command-replay is therefore the client's sole state source → any missed/no-op apply = PERMANENT desync.
- **8 vs 7 manufacture**: `ManufactureCompletedAction` resolves the queue item by def-GUID + queue-index with no idempotent reconciliation. Once queues diverge (instant path `ItemManufacturing.ManufactureItem:180-190` calls `FinishManufactureItem` inline w/o enqueue; + coalescing), index/resolver mismatches and a completion no-ops on the client (`ManufactureReflection.Complete` returns at target==null) → one item lost forever. `SequenceTracker` only dedupes; it never retries a lost apply.
- **Event dialog only on host**: client never raises the event (no sim → no `GeoscapeEventRaised` → `GeoscapeView.OnGeoscapeEventRaised`:2109 never runs → no `UIStateGeoscapeEvent`). Pause arrives via the time-sync anchor. NOT suppressed by our patches.
- **UI not reactive**: `UIModuleManufacturing`/`UIModuleResearch`/`UIModuleBaseLayout` do NOT live-update from model events; they rebuild only on `Init`/open. Our reflective state writes don't rebind → visible only after menu re-enter.

## Architecture decision
Client = pure frozen viewer. Host = sole simulator + single source of truth. **Generalize the WORKING wallet echo to every faction subsystem.** Client sends INTENTS up (already does); host applies; host echoes authoritative per-subsystem STATE; client overwrites local + refreshes the open UI. Drop client command-replay (fragile). This is textbook host-authoritative state replication and the only pattern proven to work here (currency). Echo is event-driven + coalesced (NOT continuous streaming — that was the SD-AIDR failure).

## StateChannel infra (NEW — generalizes wallet echo)
- `PacketType.StateSync = 0x64`: payload `[channelId:u8][version:u64][len:u16][payload:N]`.
- `IStateChannel { byte ChannelId; byte[] Snapshot(GeoRuntime rt); void Apply(GeoRuntime rt, byte[] data); void AttachHost(SyncEngine eng); void DetachHost(); }`. A `StateChannelRegistry` maps id→channel.
- Host: each channel subscribes its game change-event → `SyncEngine.MarkChannelDirty(id)`. `SyncEngine.Tick` (host) coalesces → for each dirty channel: `Snapshot` + `++version[id]` + `BroadcastToAll(StateSync)`. Also mark all channels dirty on host `HourTicked` (for progress) and on a new client becoming ready (late-join convergence).
- Client: `OnStateSync` → channel by id → if `version > _lastVersion[id]` → `using(SyncApplyScope.Enter()) channel.Apply(rt, payload)` → `GeoUiRefresh.Refresh(screen)`.
- Per-channel version drop (extend `SequenceTracker` or a small `Dictionary<byte,ulong>`).
- **Wallet stays on its existing `WalletSync` packet** (works; do NOT migrate now — don't break currency). Slight duplication OK.

## Channels
1. **Inventory (id=1)** — FIXES 8v7. Path: `GeoRuntime.PhoenixFaction()` → `GeoFaction.ItemStorage` (GeoFaction.cs:75, type `ItemStorage`; Phoenix uses global storage). Model: `ItemStorage.Items : IReadOnlyDictionary<ItemDef,GeoItem>` (ItemStorage.cs:22); count = `GeoItem.CommonItemData.Count` (CommonItemData.cs:39). Snapshot = list of `(ItemDef.Guid string, int count)`. Apply (client): resolve guid→ItemDef via `DefReflection.GetDefByGuid`; reconcile to match (delta via `ItemStorage.AddItem(GeoItem)` / `RemoveItem(GeoItem)`; remove defs present on client but absent in snapshot). Host change event: `ItemStorage.StorageChanged` (Action, ItemStorage.cs:17) — subscribe exactly like `WalletWatcher` subscribes `Wallet.ResourcesChanged`.
2. **Research (id=2)** — fixes cancel/switch. Path: `GeoFaction.Research`. Snapshot = completed `ResearchID`s + queued `(ResearchID, accumulated/progress)`. Apply: complete newly-completed (`Research.CompleteResearch`), rebuild queue to match (`AddResearchToQueue` + set progress; clear removed). Host change events: `GeoFaction.ResearchStartedEventHandler`/`ResearchCompletedEventHandler` (GeoFaction.cs:309-311). Also add a **CancelResearch intent** (client interceptor on the remove-from-queue method → host).
3. **Manufacturing (id=3, round-2)** — `ItemManufacturing.Queue : List<ManufactureQueueItem>` (:64) → ordered `(RelatedItemDef.Guid, AccumulatedPoints)`. Apply: clear+rebuild. Events: `OnItemAdded/Completed/Removed/QueueReordered`.
4. **Base (id=4, round-2)** — per base/facility `(facilityDefGuid, gridPos, state, health/progress)`.

> Higher-fidelity alt (if hand-rolling a channel is too lossy): `GeoFaction.RecordInstanceData()` → `GeoFactionInstanceData` already aggregates Wallet/ItemStorage/Research/ManufactureQueue as `[SerializeType]` objects; load applies via `LevelStartLoadedGame` (`ItemStorage.Clear()+AddItems`, `Wallet.CopyFrom`). Could serialize a subsystem via `Base.Serialization.General`. Use only if needed.

## GeoUiRefresh helper (NEW)
Access `GeoLevelController.View.GeoscapeModules` (`GeoscapeModulesData`): `.ManufacturingModule` (:54), `.ResearchModule` (:58), `.BaseLayoutModule` (:42). After a channel apply, IF the matching module is currently open, force rebuild by re-invoking its `Init` via reflection — Manufacturing `Init`:337 (→SetupQueue+DoFilter+RefreshItemList), Research `Init(GeoscapeViewContext)`:172 (→ShowAvailable+SetupQueue), Base `Uninit()` then `Init(pxBase,context,forceBaseLayoutRebuild:true)`:281. Alternative: re-push the UIState (`"Manufacture"`/`"Research"`/`"PXBaseLayout"`). Only when open; cache the needed context. This is the fiddly part — verify Init args + an "is-open" check via Serena/decompile; if hard, best-effort (correctness already comes from the echo).

## Event display (separate from channels)
- `PacketType.EventRaised = 0x65`, `PacketType.EventDismiss = 0x66`.
- Host: Harmony **postfix** on `GeoscapeView.OnGeoscapeEventRaised` (GeoscapeView.cs:2109) [host + active session] → broadcast `EventRaised(eventId, siteId?)`. (eventId from the raised `GeoscapeEvent.EventID`.)
- Client: `OnEventRaised` → reconstruct via `EventReflection`: `ResolveEventData`/`GeoscapeEventSystem.GetEventByID` (:280) → `new GeoscapeEvent(data, new GeoscapeEventContext(site, faction))` → push dialog: `geoView._viewSwichQuery.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent), priority){ PauseGame = true })` (GeoscapeView.cs:2131-2140; `_viewSwichQuery` private field via AccessTools; `UIStateGeoscapeEvent` public ctor :42). Sync `siteId` for context fidelity where possible.
- Answer: existing `AnswerEventAction` (client→host intent; host `CompleteEvent` → reward echoed via wallet+inventory channels). On answer, host broadcasts `EventDismiss(eventId)` → all clients close their open `UIStateGeoscapeEvent` via `GeoscapeView` `FinishQueriedState`/`FinishCurrentState` (the dialog is closed by the UI module, NOT by `CompleteEvent`, so an explicit dismiss is required). Display to ALL; answering gated by `ActionCategory.Dialogs`/`ManageDialogs` (already).

## Round-2 cleanup (after channels proven)
Remove client-side `ActionApply` replay + completion interceptors (`FinishManufactureItem`/`CompleteResearch`/`CompleteFacility` suppression) + `*Completed` action classes — superseded by state channels. Keep client start/cancel intent interceptors + host apply.

## Increments (each independently in-game testable)
- **A** (now): StateChannel infra + Inventory channel + GeoUiRefresh helper. ADDITIVE (coexists with command-replay; echo self-corrects). Fixes 8v7 + inventory reactivity.
- **B**: Event display + dismiss.
- **C**: Research channel + CancelResearch intent.
- **D** (round-2): Manufacturing + Base channels; remove command-replay machinery.
