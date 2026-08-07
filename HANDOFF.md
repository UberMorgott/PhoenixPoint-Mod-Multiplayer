# Handoff — session 2026-08-07/08

Previous handoff is OBSOLETE. Sections 5c/5d/5e are folded in below with resolutions.

## 1. What this session did

### Join / lobby

| sha | what |
|---|---|
| `94fb77b` | fix(join): stale Steam P2P session → half-open peer, phantom roster row, 54 s silent hang; register the peer from its own first packet (L180) |
| `3161d33` | feat(lobby): ready gate — host cannot start until every LIVE peer has readied; owner ruling 2026-08-07: no-quorum mandate covers IN-GAME progress only, the lobby is exempt (reverses `afc111a`) |
| `fd90105` | fix(lobby): unjoined row (`Guid.Empty`) reaped after a deadline, not PausePeer'd forever; both halves of the gate count the same peers (L180 L181) |
| `061f422` | fix(lobby): on a fan-out miss, the host register from the packet that named it — the line already printed the whole diagnosis |
| `aa1aeb3` | chore(lobby): drop a duplicate using |

### Load boundary / transition

| sha | what |
|---|---|
| `17bf9fe` | fix(transition): curtain hold arm reads `loadBoundaryAnnounced` — every peer held across the boundary the host crosses (L173) |
| `9290193` | fix(transition): `CurtainTakedownGate.State()` names every input of `CurtainHoldArmed`, including the third term `17bf9fe` added |
| `88fa596` | fix(transition): host skips the redundant second load on new-campaign — it already holds the live level it just built; CRC detection via `DiffEngine.HandleCrcReport` (L174) |

### Windows / events

| sha | what |
|---|---|
| `19af84c` | fix(windows): `HoldsForOpenScreen` queues a notification behind a player-opened screen; `ReplenishSync.ClientArrivalTick` re-asks the game's `GetMissingItems()` after state arrives; `HintMirror` relay is unconditional before the dedupe (L163 L164 L165) |
| `0ecff8f` | fix(windows): `MayAnswerQueued` lets a client answer a HOST-raised window still in the queue; deployment-prep constructor calls deferred to open-time (`DeploymentRosterRefresh.Prefix`); window-drop counter (L175 L176) |
| `b69198a` | fix(windows): `MissionEncounterNav.NoDeploymentReason` gives host and client different reasons — the host never tells itself "the host's mission has not arrived" (L191) |

### Tactical

| sha | what |
|---|---|
| `f1eebab` | fix(tactical): `TacticalAutoEndTurn.CanStillAct` folds over the shared faction's AP; `CameraAbilityHintGate` survivorship + no `RemoveHint`/`RemoveHints`/`ForcedReset` patches (L161 L162) |
| `dc976fc` | fix(tactical): `TacticalUiRepaint.VehicleSlot` strips null from `GetVehicleReadyItems`; `RepaintContainerView` routes through `UpdateVehicleData`/`UpdateVehicleSecondaryData` (L170) |
| `eec73ed` | fix(tactical): `TacticalContainerOpen.InventoryWindowMayOpen` — a loot crate's window opens only where the holding peer's soldier stands; `LocalAbilities` declares `OpenCrateAbility` local (L178 L179) |
| `7b75de9` | fix(tactical): `GroundTargetingExemptsTheOrder` reproduces `TacticalViewState.IsTargetingGround:200-219` — free-aim ground throw/cone/free-aim not held to the grid sweep (L169) |
| `7260b25` | fix(tactical): `MovePollMustBeWithheld` is a STANDING gate on `MoveAbility.GetTargetsData` — re-selecting a mirrored actor cannot restart the sweep (L168) |
| `552a331` | fix(tactical): `LootMirror` keys by item def guid not queue position; weapon selection rides the 0x82 settle; `Bleed_StatusDef` source resolved before apply (L185 L186) |

### Rail / clock / market / scrapcart / replenish

| sha | what |
|---|---|
| `7b6f350` | fix(rail): `EquipSync.JudgeHostFlush` blocks a stale open-screen from undoing a peer's loadout; `DiffEngine.EscalateAt` escalates a permanent exclusion; `MarketplaceSync` holds a dropped push (L187 L188 L189) |
| `08eb214` | fix(clock): `TimeAnchor.Drifted` reads the anchor's own `Paused`/`Scale` — a speed click is not drift; `ApplyIfTouched` writes a log line on the jerked peer (L190) |
| `a5a62e3` | fix(market): offer addressed by content key, not positional row (L171) |
| `db4dc8f` | fix(market): refused purchase answered in the panel, not via `IntentRail.Reject`'s notify |
| `70097b8` | fix(scrapcart): `ManufactureSync.ScrappableCount` reproduces `StripPartialMagsFromScrapStorage:1081-1082`; cart staged against the pool not the raw stack (L172) |
| `a1c11dd` | fix(replenish): instrumentation — `[MP][replenish] … the game's own gate saw N short soldier(s)` |

### UI / features

| sha | what |
|---|---|
| `ced9412` | feat(net): per-peer RTT on the existing heartbeat — echo the stamp, not restamp; client acks the host too; SRTT alpha=1/4, cadence 1 s (L158 L159) |
| `3f8d595` | feat(ui): player panel — name/ping-bars/status, tactical only (L158 L159) |
| `b8a41d7` | feat(ui): ping markers — hotkey **H**, `GeoscapeGlobeMarkers.AddMarker` / `LocatedBeaconPrefab`, `MessageBoxPromptController.WindowShowEvent` for sound (L160 L182) |
| `1dc2701` | fix(ui): ping hotkey moved to `MultiplayerConfig.PingMarkerKey`, a key the game and TFTV leave free |
| `04bd013` | fix(ui): player panel removed from geoscape, tactical only |
| `f277459` | feat(ui): native sound on ping arrival |
| `30a64e3` | fix(ui): `SetActive(true)` on fresh-instantiated markers; `TacticalReadyButton.TrimRaycast` sets `raycastTarget` + `targetGraphic` (L182) |

### Harness

| sha | what |
|---|---|
| `e827eae` | chore(laws): snapshot split (baseline vs contract), twin pairing frozen |
| `bf72ecc` | docs(laws): P4c ruled an uncovered hole; rows-vs-registrations explained |
| `f6d86b5` | chore(laws): L156 — in-inventory reorder repaints the open equip screen |
| `b73ce0a` | fix(tactical): L157 — mirrored tactical inventory repaint does not re-commit on the watcher |
| `0713d8f` | fix(laws): L163 updated to three-argument hold + L175 carve-out |
| `57b49aa` | chore(laws): renumber the move-sweep law to its assigned number |
| `332d519` | docs(laws): L185/L186 falsification evidence recorded |
| `9174362` | fix(harness): per-law exception containment, staleness guard, `laws-run=N/N` (L193) |
| `0ab99df` | chore(tactical): 0x80 refusal logs seq/op/cursor |

### Law count at HEAD

- `tools/law-count.txt`: `files=108`, `inline=60` = **168 registrations**
- New file-backed laws this session: L155-L193 (not all numbers used; L166/L167 absent)

---

## 2. Features added

- **Per-peer RTT** on the existing `Heartbeat 0x06` / `HeartbeatAck 0x07` — no new packet type, no new surface id. `HeartbeatAck` echoes the received stamp (was restamping); client now acks the host (was one-way). SRTT: RFC 6298 alpha=1/4, cadence lowered 5000->1000 ms. Sample guards: dedup by stamp (StunUDP sends reliable twice), discard across a main-thread stall. Host piggybacks the full ping table on the broadcast heartbeat. `SessionManager.cs:810-824`, `PingTable.cs`.
- **Player panel** — tactical only (removed from geoscape in `04bd013`). Name | ping bars (<60/<120/<250/else ms, tooltip = numeric) | status (ready/not-ready/dropped). Fed by `PingTable.GetPingMs` (negative = unmeasured/stale = em-dash). `PlayerPanel.cs`.
- **Ping markers** — hotkey **H**, rebindable via `MultiplayerConfig.PingMarkerKey`. Geoscape: `GeoscapeGlobeMarkers.AddMarker` (native expiry timer). Tactical: `LocatedBeaconPrefab` + `GroundMarker`. Sound: `MessageBoxPromptController.WindowShowEvent` (native). Per-peer colour via `PeerTint`. Five-second lifetime, no state persisted. `PingMarkers.cs`.
- **Deployment countdown** — five-second overlay on Deploy press; one peer's cancel stops it for everyone. Proceeds by default, no action needed (NOT a quorum — L177 pins this). Rides mod-state root `M#deploy` on 0xAC, veto is op 2 on 0xB8. `DeployCountdown.cs`, `CountdownPanel.cs`.
- **Lobby ready gate** — owner ruling 2026-08-07: the no-quorum mandate covers IN-GAME progress only; the lobby is EXEMPT and a gate is wanted there. `LobbyController.IsLivePeer` is the ONE definition (not host, not `Paused`, `PlayerGuid != Guid.Empty`). L84 arm (c) retargeted. This REVERSES commit `afc111a` ("start on the host's own readiness, never on a peer quorum").

---

## 3. Harness changes — READ BEFORE TRUSTING ANY GREEN

Three ways a "proof" was fiction in this session:

**(a) Stale DLL.** A plain `dotnet run --no-build` (or running `RailCheck.exe` directly) reflects over whatever `Multiplayer.dll` is on disk. One run reported thirteen violations that were pure artefact; another gave an agent GREEN with the asserted call DELETED. **Now:** `StaleBuild` (L193 arm c) is a hard stop with exit 2 (`RAILCHECK REFUSED`) when `Multiplayer.dll` is older than any `src/**/*.cs`. The procedure is `dotnet build` then `dotnet run --no-build` (or just `dotnet run`, which rebuilds via ProjectReference). `.githooks/pre-commit` was already safe (plain `dotnet run`).

**(b) One throwing law aborted the run.** `laws.AddRange(X.Check())` let `RailMeta.CountMiss` (reading `UnityEngine.Time.realtimeSinceStartup`, an ECall outside the player) die at law #31 of 153, and `L98_ApAuthority` `Invoke`ing a production method by a hardcoded 7-argument array crashed on a signature change. Laws #32-153 never ran. **Now:** every registration goes through `Program.Add(laws, () => ...)` which `catch`es each law's exception and reports it AS that law's violation. The GREEN line prints `laws-run=N/N`. L193 arm (d) forbids `laws.AddRange(...)` by IL scan. `law-integrity.ps1` updated to match the new registration shape.

**(c) Falsification edit that did not apply.** A `perl -0pi` pattern failed silently on a CRLF working copy. The agent got GREEN with the very method his new law asserted still intact. **Lesson:** verify the falsification edit landed (`git diff` the target file) before trusting a falsification run.

---

## 4. Still open / not verified in game

Everything below is BUILT + DEPLOYED but UNVERIFIED in a live session.

- **Geoscape-return hang** — the host's own world was loaded twice; `88fa596` skips the redundant second load. Check: one progress bar, not two, on a new campaign.
- **False out-of-AP turn end** — `f1eebab` gates on `CanStillAct` over the shared faction. Check: the "everyone is done" prompt never fires while an ally has AP.
- **Grenade / cone / free-aim from a client** — `7b75de9` exempts ground-targeting from the `TargetIsOffered` grid sweep. Check: a client can throw a grenade where the arc draws green.
- **Kaos shop** — `a5a62e3` + `db4dc8f` address offers by content key and answer refusals in the panel. Check: buy a soldier, other goods stay; the sold item vanishes on both peers.
- **Vehicle inventory in a mission** — `dc976fc` handles the null weapon slot a bare vehicle produces. Check: open a vehicle's inventory mid-battle without an NRE.
- **Post-mission replenish** — `19af84c` re-asks `GetMissingItems()` after returning state arrives. Check: the button APPEARS AND works. Grep: `[MP][replenish] … the game's own gate saw N short soldier(s)`.
- **Player panel** — `3f8d595` + `04bd013`. Check: visible in tactical, absent on geoscape, bars update.
- **Ping markers** — `b8a41d7` + `30a64e3`. Check: visual appears (was audible-only before the fix), per-peer colour, beam vertical shaft. The off-screen arrow idea (one `UIObjectTracker` with `KeepOnScreen=true`) is NOT shipped — only the on-globe marker and the beacon.
- **Lobby ready gate** — `3161d33` + `fd90105`. Check: PLAY blocked until peer readies; a phantom never blocks it.
- **Double load gone** — `88fa596`. Check: the host sees one 0-100 progress bar, not two, on a new campaign.
- **Phantom peer** — `94fb77b` + `fd90105` + `061f422`. Check: a retry join does not leave an `Unknown` seat.
- **Log strings worth grepping:**
  - `[MP][replenish] … the game's own gate saw N short soldier(s)`
  - `[MP][windows] queue HELD while <screen> is open`
  - `[Multiplayer][tac] rebuilt the open inventory panels`
  - CRC divergence: `DiffEngine.HandleCrcReport`

---

## 5. Superseded from the previous handoff

**5d — inventory cell position: CLOSED.** Vanilla has no coordinate field (`GeoItem.cs:12-23`); a cell IS a list index. `UIInventoryList.ItemChangedHandler:855-859` clamps with `Insert(Math.Min(...))`, so even the mover loses his hole on re-open. v1 shipped exact-cell only for the TACTICAL in-mission screen and only visually (commit `83ae20b`, `dstUiCell:i16`), never durable. L156 now holds the in-inventory reorder repainting the open equip screen; L157 holds the tactical inventory repaint not re-committing on the watcher.

**5c — shot-animation desync: still OPEN** but the culling premise is REFUTED. `AnimatorCullingMode` is `CullUpdateTransforms`, not `CullCompletely`, so offscreen actors DO advance their state machines — the camera-dependency observed is not explained by culling alone. Option (C) ("remove the second channel") is supported only in the narrow form where the wire carries the CONFIRMATION, not a second activation; deleting `AccumulationClientGate` is off the table because TFTV replaces the DamageResult factory. The root — one event going two channels (`TacticalCommandSync.cs:750-753`, native `Activate` inside `SyncApplyScope` + host resolution on the rail) — is documented but no code was written. The three decompile questions from the previous handoff are answered: (1) culling mode = `CullUpdateTransforms`; (2) `AnimatorStateCheckpoint` does poll `GetCurrentAnimatorStateInfo` in a loop; (3) the mirror's `Activate` produces effects inside `SyncApplyScope`, and the suppression mechanism must be identified before (C) is attempted.

---

## 6. Rules that bit us, for the next session

- **Shared-tree discipline with N agents.** A `git add -A` swept another agent's scratch dir and half-written files into a commit. Explicit pathspecs only — never `git add -A` in a multi-agent tree.
- **Law numbers must be ASSIGNED, not max+1.** Three collisions in one minute when two agents each took the next number. Coordinate via `tools/law-count.txt` and the existing `docs/laws.md` index.
- **A commit that registers a law must include the law FILE.** Five orphan registrations broke a clean checkout of HEAD — the `Add(laws, () => L<n>...Check(...))` line compiled against a file another commit (or another agent) had not pushed yet.
- **A law that `Invoke`s a production method by a hardcoded argument array is a tripwire on that method's signature.** `L98_ApAuthority` crashed with `TargetParameterCountException` when another agent changed the target's parameter list — and it crashed as a HARNESS-CRASH (L193 arm a now contains it), not as a red line for L98. Contained now, but any Invoke-by-array law is inherently fragile.
- **A `perl -0pi` falsification on a CRLF working copy fails silently.** Always `git diff` the target after a falsification edit, before trusting the result.
