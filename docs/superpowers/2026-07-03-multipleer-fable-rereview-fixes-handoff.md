# Multipleer session handoff — 2026-07-03 (Fable re-review + fixes)

> Session 2026-07-02/03. Inner `main` HEAD `ace79ae` (local only, NOT pushed). Tests 935→995/995 green. Living tracker: [`../COOP-SYNC-ROADMAP.md`](../COOP-SYNC-ROADMAP.md).

## Commits (inner main, local only)

- **`fc2c8b5` fix(sim-freeze): client time-relay reads host anchor + unconditional freeze reschedule** — fixed `849743b` HIGH (client speed click paused host: `CurrentPaused()` read pinned local `Timing.Paused`; pause button could only unpause; now anchor-based + `!GlyphHostPaused` relay) + MED (freeze re-assert no-op via `Timing.Paused` setter short-circuit `Timing.cs:112`; now unconditional reflect-call of private `Timing.RescheduleUpdateables(Timing)` — geoscape `Timing.Scheduler` field is NULL, `GeoLevelController.cs:346-350`).
- **`e9e895b` fix(sync): host-resolved event texts on wire** (VoidOmen blank-window real fix). KEY: `VoidOmenDef` does NOT exist — VoidOmens = `GeoscapeEventDef`s `VoidOmen_{0..19}` with EMPTY loc keys (`TFTVDefsInjectedOnlyOnce.cs:7668-7679`); narrative is HOST-side runtime mutation only (`TFTVODIandVoidOmenRoll.cs:638-639`) → client def resolution can never work. `EventRaised` +flag `0x08` +`[u16+UTF8 title][u16+UTF8 narrative]`; `EventDismiss` +`wireOutcome`/`wireNarrative`; host resolves at broadcast (`EventDisplayPatches.cs`); client `ApplyWireTexts` stamps def; empty wire → local fallback byte-identical. Diag in `BuildResultEvent`: `descCount/outLen/narrLen/wireOutLen/wireNarrLen/bodyLen`.
- **`d3686b1` fix(sync): correlator hardening** — `BufferPending` eviction skips occIds in `_queue` (was: >16 out-of-order dismisses → stale prompt wedges `_shownSlot` forever → ALL client dialogs starve); `DrainQueuedRaises` guards `TryGetValue` miss (+ `EventCorrelator.AbortShow`); `ResetEventMirror` also resets `EventDisplay._openOccurrenceId`; `623ee9c` comments corrected (occIds are process-lifetime monotonic, NOT reused; real reload hazard = stale busy slot).
- **`4dbbd14` fix(sync): client OK on single-choice prompt advances host modal** — new packet `EventAdvanceRequest=0x6B` (reuses `EncodeEventDismiss` codec); host `TryHostNativeAdvanceSingleChoice` drives native `OnChoiceSelected(Choices[0])` gated completed+single-choice+this-occurrence+not-paging+not-advanced; idempotence via `EventOccurrenceIds.MarkAdvanced/WasAdvanced` (first-wins with host's own click).
- **`f80bc0c` chore(sync): wallet rail diag** — 16 INFO sites all grep-tag `Wallet` (host dirty/poll-drift Δ/flush/full-broadcast + guards; client apply ver+localΔ + guards; `WalletWatcher` Bind/Rebind; `WalletReflection` not-ready).
- **`ace79ae` fix(sync): host native advance false-negative ROOT CAUSE** — `EventReflection.Ensure()` looked up `PhoenixPoint.Geoscape.View.GeoscapeModulesData` but real type = `Base.UI.GeoscapeModulesData` (decompile `Base.UI/GeoscapeModulesData.cs:7`) → `TypeByName` silent null → BOTH `TryHostNativeAdvanceSingleChoice` AND `TryHostNativeResolve` (multi-choice!) ALWAYS guard-failed — the original "client click never closes host modal" bug across ALL event kinds. Fix: type derived from `_gvModulesField.FieldType` (self-grounding); `NativeDriveGuard.cs` per-member greppable guard tags; one-shot startup lookup-audit log; `EventCorrelator.Advanced()` Ignores terminally-resolved occId (late/dup `AdvanceResult` dedup).

## Deployed

- `D:\Steam\steamapps\common\Phoenix Point\Mods\Multipleer\Multipleer.dll` — SHA256 `15f9a08e91fccfad35b69467ebd0c46448ae2eff17574e46299998b368a22e3a`, 608768 B, 2026-07-03 00:43:40.

## In-game VERIFIED this session (run 00:27; roles: `multipleer.log` = HOST that run)

- **Wallet rail CONVERGES** — client applied ver=1→25, final identical: Supplies 2862 / Materials 1332 / Tech 1393 / Mutagen 650. Roadmap backlog item #5 (`f005acc`) = CONFIRMED.
- **`0x6B` advance-request wire works** (send+receive logged).
- Host reward grant at TRIGGER for single-choice = vanilla native behavior (`GeoscapeEventSystem.cs:651-655` auto-`CompleteEvent`; window is informational) — NOT a bug, user-visible quirk explained.
- **Role-swap trap:** instance failing to bind 14242 (`AddressAlreadyInUse`) silently becomes CLIENT; log ERROR only — in-game invisibility is a UX gap (backlog).

## PENDING in-game verification (FIRST THING next session; restart both instances, HOST FIRST)

1. `ace79ae` single-choice: client OK click advances/closes host modal; no repeat windows on client.
2. `ace79ae` multi-choice: client choice click closes host modal (`TryHostNativeResolve` functional for the FIRST TIME ever; if it fails now, host log `[Multipleer] TryHostNativeResolve guard=…` names the exact guard — paging guard next suspect).
3. Check host log startup lookup-audit line — must list ZERO null members.
4. `e9e895b`: VoidOmen window shows title+text (client log `BuildResultEvent … wireNarrLen>0`).
5. `fc2c8b5`: client speed/pause clicks control HOST correctly (no self-pause poison).
6. Remaining old backlog items: aim relay `34cca92`, status magnitude `b8e50ec`+`dd47ad1`, invis-targeting `3eaf77c`, camera-steal `cda982c`, host-bind WARN `09bf9ce` + save/load arbiter `1878aa8`, event dedup `b0e20a0`.

## Known open issues (LOW, logged)

- `OnWalletSync` marks version via `_tracker.MarkWallet` even when client wallet still null → mid-load broadcast consumed-never-applied; relies on `SessionManager` ready re-broadcast (`SessionManager.cs:348`); `guard=wallet-null` log will pin in-game.
- In-flight pre-reload raise processed after `ResetEventMirror` can wedge slot (no packet gating at reset point).
- Glyph pre-anchor cosmetic: shows "running ×1" until first anchor.
- Arbiter comment `SaveTransferCoordinator.cs:639-645` (commit `1878aa81`) repeats wrong occId-reuse claim — fix opportunistically.
- `CompleteEventByOccurrence` fallback grants but SKIPS UI-layer Requirements `Wallet.Take` (cost lives ONLY in `UIModuleSiteEncounters.OnChoiceSelected:571-573`) — with reflection fixed the fallback should rarely trip; consider eliminating fallback in favor of always-native path.
- `de3aac7` sim-freeze flag default-ON = accepted deviation from spec (spec said flip at S3).

## Next arcs (order)

1. Finish pending verifications above.
2. **UNCHANNELLED OUTCOMES channel** — client never receives: recruits/units, skillpoints, StartMission, haven pop, soldier/aircraft damage, SDI, objectives, timers, subfactions (acknowledged TODOs `AnswerEventAction.cs:71-72`, `SyncEngine.cs:226-229`); start with units/skillpoints/StartMission.
3. **Sim-freeze S2+** per outer spec `E:\DEV\PhoenixPoint\docs\superpowers\specs\2026-07-02-multipleer-inc4-client-sim-freeze-design.md`.
4. **Action-relay `0x60`-`0x62` → envelope cutover** per outer spec `…-action-relay-envelope-cutover-design.md` (single atomic commit, no dual-rail).

## Native ground truth (recorded in memory graph; cite for event work)

- Single reward-grant point = `GeoscapeEvent.CompleteEvent` → `GenerateFactionReward`+`ChoiceReward.Apply` (`GeoscapeEvent.cs:101-102`, no dedup in `Apply`).
- Requirements cost charged in UI layer ONLY (`UIModuleSiteEncounters.OnChoiceSelected:571-573`).
- Hidden force-complete: `UIStateGeoscapeEvent.ExitState` (`Choices.Last()` if `Record==Triggered`, `:61-65`).
- Toasts = `GeoscapeLog` → `UIModuleStatusBarMessages` (fire wherever model mutation runs).
