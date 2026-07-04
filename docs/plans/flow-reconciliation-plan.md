# Multiplayer — Flow Reconciliation Plan (corrected to the documented lobby flow)

> Goal: reconcile the EXISTING mod into the **canonical lobby-first flow** the user
> confirmed and that `docs/specs/02-session-lifecycle-and-player-management.md`
> documents. The network/save/barrier FOUNDATION is sound (NetworkEngine /
> SessionManager / SaveTransferCoordinator / FinishLevelBarrierPatch) and the
> ready/roster/rename messages already exist. What is MISSING is an **interactive
> lobby UI** and a **save-picker**; what is WRONG is the CREATE path (starts vanilla
> single-player, never hosts), the JOIN path (connects but shows no lobby), and the
> save-pick ordering (`HostStartSession` auto-uses `LatestSave??LatestLoad` instead
> of a save chosen AFTER `Play`).
>
> This plan REWIRES the existing path and adds exactly the two new surfaces the docs
> call for (interactive lobby panel + save-picker). No parallel systems.

---

## 1. Confirmed flow (canonical) — with doc cites

1. Main menu → **"NETWORK GAME"** button → `docs/engine/03-harmony-patches.md` §Main-Menu Injection.
2. Network screen → **Create Game** (host) / **Join Game** (client) → `03-harmony-patches.md` §"Network Game" Screen.
3. **Create Game opens the LOBBY IMMEDIATELY; campaign is NOT loaded yet** (lobby-first) → `specs/02` §1 "Lobby-First Model" (lines 9-14). Host cannot play while in lobby; on-disk save is the single source of truth at start.
4. Lobby = **live roster** + **ready** status + **nickname edit** + host **Play** (enabled only when all ready) → `specs/02` §1 "Lobby Room Contents" (lines 16-26). Client view = roster + ready toggle. Both = nickname edit field.
5. **Roster / ready / nickname sync** via lobby-state broadcast: `PlayerListUpdate`/PEER_LIST (roster), `ClientReady`/`AllClientsReady` (ready), `PlayerRename`/RENAME (nickname) → `specs/02` §1 "Nicknames (live)" lines 34-38, "Why Identity Matters" lines 40-45.
6. All ready → host presses **Play** → **THEN the native save-picker opens** (host chooses the campaign save) — selection happens **after** the lobby, never before → `specs/02` §2 "Trigger" (lines 53-58).
7. Host serializes the chosen save → broadcasts as length-prefixed blob, gzip already on disk (`.zsav`/`.zjsav`), chunked on Steam P2P → `specs/02` §2 "Save Transfer" (lines 60-68). (`SaveChunk`/`SaveDone` packets.)
8. **Barrier (ready-gate):** each client loads → sends `LOADED` ack (`ClientLoaded`); host waits for ALL (+ itself); loading curtain + input block held on everyone → host broadcasts `BEGIN` (`SessionBegin`) → all unblock simultaneously → enter geoscape synced → `specs/02` §2 "Barrier Sync" (lines 70-81). After BEGIN there are no more barriers.

### Doc details that REFINE my brief (these are canonical, plan to them)
- **Lobby-first is hard:** the campaign must NOT load on Create — only the on-disk save loads at Play. This is exactly why the current vanilla-SP CREATE path is wrong (Gap-1).
- **Nickname/RENAME:** every player (incl. host) edits their OWN nickname in the lobby; `PlayerRename` packet → broadcast → live UI update. Foundation handler `SessionManager.HandleRename` exists (SessionManager.cs:341).
- **Per-player PROGRESS (two phases):** Phase-1 download `bytes/total` (exact), Phase-2 game-load % (Harmony-hook PP loading progress — SDK-unverified) → `specs/02` §2 "Loading Screen — Per-Player Progress" (lines 86-98). Throttle ~150ms/5%, may be unreliable. `LoadProgress`/`ReportDownloadProgress` foundation exists (SaveTransferCoordinator.cs:420-437).
- **Barrier TIMEOUT is REQUIRED:** a client that never sends `LOADED` must not hang the barrier — host shows "waiting for X", offers kick/abort after N seconds → `specs/02` §2 line 80. Foundation has `BarrierTimeoutMs` (SaveTransferCoordinator.cs:49) but the user-facing waiting/kick UI is missing.
- **Save-pick AFTER lobby** (not "Create then play immediately"): `Play` enabled only when all ready, THEN picker. My step-4/step-5 summary matches; the doc adds "Play gated on all-ready" and "picker is native".

---

## 2. Save-picker API (found in the decompile) + HostStartSession signature change

**Canonical save enumeration on `PhoenixSaveManager`:**
- `public IEnumerable<SavegameMetaData> GetSaves()` — `PhoenixSaveManager.GetSaves` — `decompiled/.../PhoenixPoint.Common.Saves/PhoenixSaveManager.cs:279` — returns `_allSaves.Values` (the in-memory authoritative collection the game builds in `InitSaves()`, line 317). **Synchronous, no coroutine** → simplest source for a picker list.
- Async alternative: `public IEnumerator<NextUpdate> GetSavegames(ByRef<List<SavegameMetaData>>)` — `PhoenixSaveManager.cs:186` (delegates to `Serializer.GetSavegames`). Use only if `_allSaves` is not yet populated; otherwise `GetSaves()` is enough.
- Lookup helper: `GetSaveGame(string name, ByRef<SavegameMetaData>)` — `PhoenixSaveManager.cs:173`.

**The chosen entry feeds the existing transfer path directly:** the picker yields a `SavegameMetaData`, and
`SerializationComponent.ReadSavegameBinary(SavegameMetaData metaData, ByRef<byte[]> result)` — `decompiled/.../Base.Serialization/SerializationComponent.cs:280` — already takes a `SavegameMetaData`. The mod ALREADY calls this in `SaveTransferCoordinator.HostSerializeAndSendCrt` (SaveTransferCoordinator.cs:137). So a `SavegameMetaData` from `GetSaves()` is a drop-in.

**HostStartSession change (confirmed feasible):**
- Current: `HostStartSession()` self-selects `saveManager.LatestSave ?? saveManager.LatestLoad` (SaveTransferCoordinator.cs:118) and aborts if null — contradicts "pick after Play".
- Change to: `HostStartSession(SavegameMetaData chosen)` — DROP the `LatestSave??LatestLoad` block (lines 115-124); pass `chosen` straight into `HostSerializeAndSendCrt(game, chosen)` (line 130/134). The picker (new code) supplies `chosen` from `GetSaves()`. Keep the Stun best-effort warning and `TryGetGame` guard.
- Caller `MultiplayerUI` (lobby START) opens the picker, then calls `engine.SaveTransfer.HostStartSession(picked)`.

> Note: the vanilla native Load screen (`UIStateHomeLoadGame` → `UIModulePauseScreen.LoadGameModule.InitLoadMode`) is a full prefab module and too heavyweight to re-host for a picker. `GetSaves()` is the same underlying collection it shows, so a minimal mod-drawn picker over `GetSaves()` is faithful and far cheaper.

---

## 3. Lobby UI + save-picker approach decision (grounded)

**Evidence about the current UI:**
- `MultiplayerUI : MonoBehaviour` (MultiplayerUI.cs:12) — has `Awake` (23), `Update` (307), and builds a runtime **uGUI** in-game bar via `GameObject`/`RectTransform`/`Image`/`Text` (`CreateInGameBar` 283-301, `CreateText` 332-348). **It does NOT use `OnGUI`/IMGUI.**
- The "lobby" today is `ShowLobbyDialog` (212-246): a single `MessageBox.ShowSimplePrompt` snapshot listing `engine.LocalSteamId` + `Session.GetConnectedClients()`, with DISCONNECT / START GAME / CLOSE. It does NOT refresh on PEER_LIST, has no ready toggle, no nickname field — a one-shot dialog, not a live panel.

**Lobby decision → extend `MultiplayerUI` with a runtime uGUI panel (NOT IMGUI, NOT a cloned native prefab).** Why:
- `MultiplayerUI` already builds uGUI from code and runs `Update()` every frame → a lobby `GameObject` panel (player rows + Ready button + nickname `InputField` + Play button) is the same proven pattern as the existing bar, and `Update()` (or an `OnClientConnected`/roster-changed callback) can refresh rows live as `PlayerListUpdate`/`ClientReady`/`PlayerRename` arrive. Live refresh is the doc-required behaviour (roster/ready/progress update as messages come in) and a MessageBox snapshot cannot do it.
- Cloning a native PP prefab (per `03-harmony-patches.md` §Navigation, pushing onto PP's UI state stack) is the "indistinguishable-from-vanilla" ideal but the prefab/state-stack APIs are SDK-unverified (`specs/03-open-questions-sdk`); higher risk, not needed for a functional lobby. Defer the prefab polish.
- IMGUI `OnGUI` would work but is inconsistent with the existing uGUI bar and looks alien; no reason to introduce a second UI paradigm.

**Save-picker decision → minimal mod-drawn uGUI list over `PhoenixSaveManager.GetSaves()`** (a scrollable list of save names; click → `SavegameMetaData`), opened from the lobby Play button. Why: reusing the native `LoadGameModule` requires re-hosting a heavy prefab module with confirmation/load semantics we must suppress; a small uGUI list reusing the SAME panel toolkit as the lobby is feasible-minimal and directly yields the `SavegameMetaData` that `HostStartSession(chosen)` needs.

---

## 4. Dead / orphan code to delete (confirmed via find_referencing_symbols)

- **`MultiplayerUI.PendingAutoHost`** (property, MultiplayerUI.cs:15) — written 2× in `OnCreateResult` (lines 88, 93), **read nowhere**. DELETE.
- **`NetworkMenuHelper.TriggerNewCampaign`** (MainMenuPatches.cs:153) and **`TriggerLoadSave`** (176) — only callers are the dead `OnCreateResult` branches; they push vanilla single-player (`UIStateNewGeoscapeGameSettings`/`UIStateHomeLoadGame`) and break lobby-first. DELETE with their callers.
- **`WireNetworkMenuPatch`** (MainMenuPatches.cs:73-127) and the fields it populates — `NetworkMenuHelper.StoredMainMenuState` (130) and `FirstGeoscapeDef` (132) are consumed ONLY by the two Trigger* methods. DELETE the whole patch + these two fields once §5 lands.
- **`NetworkMenuHelper.StoredMainMenuModule`** (131) and **`NetworkGameButton`** (133) — set in `InjectNetworkButtonPatch.Postfix` (63, 60) but **read nowhere** → dead writes. Safe to delete (low priority; harmless).
- Also delete `EnsureTypesResolved` + the `_homeStateType`/`_geoscapeSettingsType`/`_loadGameStateType`/`_stateStackActionType`/`_geoscapeDefType`/`_typesResolved` fields (MainMenuPatches.cs:135-151) — used only by the Trigger* methods.

> KEEP: `InjectNetworkButtonPatch` (the button), NetworkEngine, SessionManager,
> SaveTransferCoordinator, FinishLevelBarrierPatch, the connect dialogs, the message
> layer. These are the foundation we wire INTO.

---

## 5. Stage table — intended (doc) → existing-code-state → MODIFY → change → NEW code

| Stage | Intended (doc) | Existing-code state | MODIFY (file:method) | Change | NEW code (unavoidable) |
|-------|----------------|---------------------|----------------------|--------|------------------------|
| Inject button | NETWORK GAME in main menu | WORKS | — | keep | — |
| Network menu | Create / Join entry | WORKS (MessageBox `ShowMainDialog`) | — | keep (or later replace with prefab) | — |
| **CREATE → host + lobby** | Create opens lobby NOW; campaign NOT loaded; host session started | DEAD-END: `OnCreateResult` sets dead `PendingAutoHost` + `TriggerNewCampaign/LoadSave` → vanilla SP, no host | `MultiplayerUI.OnCreateResult` | rewire: `NetworkEngine.Create()` → `Initialize(transport)` → `StartHost(0)` → open the new **lobby panel**; do NOT load any campaign | opens **lobby panel** (new, see below) |
| Orphan/dead | n/a | `PendingAutoHost`, `Trigger*`, `WireNetworkMenuPatch`, helper fields all dead | as §4 | delete-dead | — |
| **JOIN → client lobby** | Connect → Lobby(client) with live roster | `ShowDirectConnect`/`ShowStunConnect`/`ShowSteamConnect` call `JoinGame` then only `ShowInGameBar()`; no lobby shown | `MultiplayerUI.ShowDirectConnect` (131), `ShowStunConnect` (154), Steam HOST branch (193-199) | after `JoinGame`/`StartHost`, open the **lobby panel** instead of (or in addition to) `ShowInGameBar` | uses lobby panel |
| **Live roster** | live player list (refresh on PEER_LIST) | `BuildPeerList`/`BroadcastPeerList`/`HandlePeerList` WORK (SessionManager.cs:268-309); host re-broadcasts on AddClient & on ready; **no UI reads it live** | NEW lobby panel + `MultiplayerUI.Update` | lobby rebuilds rows each refresh from `engine.Session.Clients`; subscribe to roster-changed / poll in `Update` | lobby panel rows |
| **Ready toggle + gating** | every player toggles Ready; host Play enabled only all-ready | `SessionManager.SetClientReady`/`HandleReadyState`/`OnAllClientsReady` WORK (SessionManager.cs:203-240); `ClientReady`/`AllClientsReady` packets exist | NEW lobby panel | Ready button → `engine.Session.SetClientReady(localId)`; host Play button enabled on `OnAllClientsReady` | Ready button + Play-enable wiring |
| **Nickname edit** | each player edits own nick; RENAME broadcast → live | `SessionManager.HandleRename` (341) + `PlayerRename` packet exist; **no send-side / UI** | NEW lobby panel; (verify a `SendRename`/equivalent exists, else add a tiny sender) | nickname `InputField` → send `PlayerRename`; rows show nickname from `PeerListEntry.Nickname` | nickname field + send call |
| **Play → save-picker** | all ready → Play → native save-picker → chosen save | `HostStartSession` auto-uses `LatestSave??LatestLoad`, aborts if none; NO picker; wrong ordering | `SaveTransferCoordinator.HostStartSession` (90); lobby Play handler (was `ShowLobbyDialog` START branch 239-244) | change sig to `HostStartSession(SavegameMetaData chosen)`, drop auto-select (115-124); Play opens picker → `HostStartSession(picked)` | **save-picker panel** over `GetSaves()` |
| Save transfer | broadcast chunks → clients load | `SendBlob`/`OnSaveChunk`/`OnSaveDone`/`ClientLoadCrt`/`PrepareEntryFromBlobCrt` WORK | `HostSerializeAndSendCrt` (134) takes `chosen` param | pass chosen `metaData` through | — |
| Per-player progress | download% + load% per peer | `ReportDownloadProgress`/`OnLoadProgress`/`LoadProgress` packet exist (420-437); Phase-2 load% source SDK-unverified | lobby panel rows | show download% from foundation; Phase-2 load% deferred (SDK) | progress display in rows |
| Barrier LOADED→BEGIN | all LOADED → BEGIN → enter together | `SendLoaded`/`OnClientLoaded`/`TryReleaseBarrier`/`Begin`/`OnBegin`/`EnterLevel` + `FinishLevelBarrierPatch` WORK | — | keep | — |
| Barrier timeout UI | "waiting for X" + kick/abort after N s | `BarrierTimeoutMs` exists (49); no user UI | lobby/loading panel + `SaveTransferCoordinator.Update` (443) | surface waiting peers + kick/abort button | small waiting UI (later) |
| Enter level | BEGIN → FinishLevel together | WORKS (`EnterLevel`→`FinishLevel`, gate `FinishLevelBarrierPatch`) | — | keep | — |

**Genuinely NEW code = the interactive lobby panel + the save-picker panel (both uGUI in `MultiplayerUI`), plus tiny senders (ready/rename hookups) and the progress/waiting display.** Everything else is rewire/reuse of existing foundation methods.

---

## 6. Ordered, in-game-verifiable increments (smallest first)

> Two instances; DirectIP is simplest for LAN testing (`tools/COOP-TESTING.md`).

- **T1 — NETWORK GAME opens a lobby panel; host/client can both open it.**
  - Touch: `src/UI/MultiplayerUI.cs` — add the lobby uGUI panel (build once, like `CreateInGameBar`; show/hide methods). Rewire `OnCreateResult` → `Create/Initialize/StartHost` + open lobby panel; remove `PendingAutoHost`. Delete dead `Trigger*` path callers.
  - Touch: `src/Harmony/MainMenuPatches.cs` — delete `WireNetworkMenuPatch`, `TriggerNewCampaign`, `TriggerLoadSave`, `EnsureTypesResolved` + dead fields (§4).
  - New file: optional `src/UI/LobbyPanel.cs` if `MultiplayerUI` grows too large; else inline.
  - Verify: main menu → NETWORK GAME → CREATE → an (even minimal) lobby PANEL appears listing the host; status bar shows `Host`; **no** vanilla campaign loads. Client side: a JOIN that reaches connect also opens the panel.

- **T2 — Client join shows in host roster live, and vice-versa (PEER_LIST).**
  - Touch: `src/UI/MultiplayerUI.cs` — `ShowDirectConnect`/`ShowStunConnect`/Steam-HOST: after `JoinGame`/`StartHost`, open the lobby panel. Lobby panel rebuilds rows from `engine.Session.Clients`, refreshed in `Update()` (or via a roster-changed hook). Host `BroadcastPeerList` already fires on `HandleConnectionRequest`/`AddClient`.
  - Verify: 2nd instance joins → host panel lists BOTH players; client panel lists BOTH. Confirms JOIN→AddClient→BroadcastPeerList→HandlePeerList end-to-end, live.

- **T3 — Ready toggle + all-ready gating; (and nickname edit).**
  - Touch: `src/UI/MultiplayerUI.cs` (lobby panel) — Ready button → `engine.Session.SetClientReady(localId)`; row shows each peer's `Ready`; host Play button disabled until `OnAllClientsReady`. Nickname `InputField` → send `PlayerRename` (verify/ add a small `SendRename` next to `HandleRename`).
  - Touch (verify only): `src/Network/SessionManager.cs` `SetClientReady`/`HandleReadyState`/`HandleRename` (already present).
  - Verify: both toggle Ready → host's Play enables only when all ready; rename on one side updates the other's roster row instantly.

- **T4 — Host PLAY → save-picker → chosen save serialized.**
  - Touch: `src/UI/MultiplayerUI.cs` — Play opens a save-picker uGUI list over `PhoenixSaveManager.GetSaves()`; selection → `engine.SaveTransfer.HostStartSession(chosen)`.
  - Touch: `src/Network/SaveTransferCoordinator.cs` — `HostStartSession(SavegameMetaData chosen)` (drop `LatestSave??LatestLoad`); thread `chosen` into `HostSerializeAndSendCrt`.
  - Verify: all-ready → Play → picker lists existing saves → pick one → logs show `ReadSavegameBinary` → `SendBlob`.

- **T5 — Save broadcast + client load + progress.**
  - Touch: none new (reuses `SendBlob`/`OnSaveChunk`/`OnSaveDone`/`ClientLoadCrt`); optionally surface `ReportDownloadProgress` % in the lobby/loading rows.
  - Verify: client logs `OnSaveChunk`→`OnSaveDone`→`SendLoaded`; download % advances per peer.

- **T6 — Barrier BEGIN → all enter together.**
  - Touch: none new (`OnClientLoaded`→`TryReleaseBarrier`→`Begin`→`SessionBegin`; gate `FinishLevelBarrierPatch`→`EnterLevel`→`FinishLevel`).
  - Verify: host gets all `LOADED` → `BEGIN` → host AND client transition into the SAME loaded geoscape simultaneously; no one stuck on the curtain.

> Follow-ups (post-T6, separate effort): barrier-timeout "waiting for X" + kick/abort UI (`specs/02` line 80); Phase-2 game-load % (SDK-unverified loading-progress hook); cloned-native-prefab lobby on PP's UI state stack (`03-harmony-patches.md` §Navigation) replacing the mod-drawn panel; Steam invite button polish.

---

## 7. Decompiled hooks cited

- `PhoenixPoint.Common.Saves/PhoenixSaveManager.cs:279` — `GetSaves() : IEnumerable<SavegameMetaData>` — **save-picker source** (returns `_allSaves.Values`).
- `PhoenixPoint.Common.Saves/PhoenixSaveManager.cs:186` — `GetSavegames(ByRef<List<SavegameMetaData>>)` — async fallback.
- `Base.Serialization/SerializationComponent.cs:280` — `ReadSavegameBinary(SavegameMetaData, ByRef<byte[]>)` — consumes a picker entry; already called by `SaveTransferCoordinator.HostSerializeAndSendCrt`.
- `PhoenixPoint.Common.Game/PhoenixGame.cs` — `FinishLevel(...)` — level-transition seam already targeted by `FinishLevelBarrierPatch` (foundation; unchanged).
- `PhoenixPoint.Home.View.ViewModules/UIModuleMainMenuButtons.Init` — button-injection target (`InjectNetworkButtonPatch`, working; unchanged).
