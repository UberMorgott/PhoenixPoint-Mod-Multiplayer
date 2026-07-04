# Multiplayer — Native Lobby + Save-Picker UI Plan (decompile-grounded)

> Goal: REPLACE the from-code uGUI overlay (`MultiplayerCanvas` + `UiToolkit` +
> `LobbyPanel`/`SavePickerPanel`) with **cloned NATIVE Phoenix Point widgets** so the
> lobby and save-picker look like the game. This is a **UI-surface swap only** — ALL
> network/session logic (roster / ready / rename / save-transfer / barrier) stays byte-
> for-byte unchanged.
>
> Source of every cite below = the decompiled game tree
> `decompiled\AssemblyCSharp\Assembly-CSharp\src\` (provenance:
> `docs/research/source-provenance.md` §Game). Mod files cited from
> `Multiplayer\src\`. Investigated via Serena symbolic tools.

---

## 0. Verdict (read first)

- **Recommended approach: CLONE native widgets/prefabs into a mod-owned container**
  (option *b*), parented to the **main-menu Canvas**. Reason: it is the only approach
  with a proven SDK path — the mod already does it (`InjectNetworkButtonPatch`) and the
  game itself builds its lists the exact same way. Cloning carries the game's full
  visual style automatically (style lives on the prefab's components, not in code).
- **REJECTED: register/push our own `UIState` onto the game's UI state stack**
  (option *a*) — see §1. No runtime-feasible SDK path; needs editor-authored
  `UIModuleDef` + serialized prefab + a typed context. High risk, unnecessary.

---

## 1. Feasibility: native `UIState` push vs clone-widgets-into-container

### (a) Push our own `UIState` — NOT FEASIBLE for a runtime mod
Evidence:
- The home screen drives a **typed, generic** state stack:
  `private StateStack<HomeScreenViewContext> _statesStack`
  (`PhoenixPoint.Home.View\HomeScreenView.cs:29`), created in `InitView`
  (`HomeScreenView.cs:101` `new StateStack<HomeScreenViewContext>(_context)`). A pushed
  state must satisfy that context type.
- UIStates reference their UI **modules** through serialized `[LinkableUIAsset]` fields
  on module-data holders located **by tag** at init:
  `GameObject.FindGameObjectWithTag(MainUILayerDef.CommonTagModulesUsed)` →
  `CommonModulesData`, and `…HomeScreenTagModulesUsed` → `HomeScreenModulesData`
  (`HomeScreenView.InitView:84-88`). Modules themselves derive from `UIModule`
  (`Base.UI\UIModule.cs:8`) / `UIModuleBehavior` and are bound to a `UIModuleDef`
  (`Base.UI\UIModuleDef.cs`) + a prefab **authored in the Unity editor**.
- A new screen therefore needs: a new `UIModuleDef`, a serialized module prefab packed
  in a UI asset bundle, and registration into the layer's module data — none of which a
  Harmony-only runtime mod can synthesize. `docs/specs/03-open-questions-sdk.md` already
  flags the state-stack/prefab path as SDK-unverified.
- Verdict: **HIGH risk, no path.** Do not pursue.

### (b) Clone native widgets/prefabs into our container — FEASIBLE, PROVEN
Evidence (the pattern is identical in mod and game):
- Mod, working today: `Object.Instantiate(template, GameModueButtonsGroup.transform)`
  then relabel `Text` + rewire `Button.onClick`
  (`Multiplayer\src\Harmony\MainMenuPatches.cs:40-60`).
- Game, main menu: `Object.Instantiate<GameObject>(TemplateMenuButton,
  GameModueButtonsGroup.transform)` → set Text → add `onClick` listener
  (`UIModuleMainMenuButtons.Init` `…ViewModules\UIModuleMainMenuButtons.cs:131-141`).
- Game, save list: `Object.Instantiate<UIModuleSaveGameSlot>(SaveSlotPrefab,
  _listItemsParent)` (`UIModuleSaveGame.GetSaveSlotModule`
  `…ViewModules\UIModuleSaveGame.cs:321`).
- Cloning preserves style automatically: the prefab carries its own `Animator`,
  sprites, fonts, `UIColorController`/`UIInteractableColorController`
  (`Base.UI\UIColorController.cs`, `UIInteractableColorController.cs`) — see §4.
- Verdict: **LOW–MED risk, proven.** Use this.

---

## 2. Widget map (lobby/picker element → native template → re-wire)

All templates live under `…\Assembly-CSharp\src\`. "Capture" = grab the field/prefab by
reflection from the live (even if inactive) module instance, exactly like
`InjectNetworkButtonPatch` grabs `TemplateMenuButton`/`GameModueButtonsGroup`.

| UI element | Native template (type + where, file:line) | Re-wire after clone |
|---|---|---|
| **Menu-style button** (Leave / Play) | `TemplateMenuButton` (GameObject) field on `UIModuleMainMenuButtons` — `PhoenixPoint.Home.View.ViewModules\UIModuleMainMenuButtons.cs` (field list; used at `:131`). Already captured by the mod. | Relabel child `Text`; `Button.onClick.RemoveAllListeners()` + add our handler (same as `MainMenuPatches.cs:44-60`). |
| **Styled in-panel button** (alt) | `PhoenixGeneralButton` — `PhoenixPoint.Common.View.ViewControllers\PhoenixGeneralButton.cs:9`. Has `BaseButton` (Unity `Button`, `:66`), `PointerClicked` event (`:22`), `SetInteractable(bool)` (`:282`), `SetEnabled` (`:129`). | Set `PointerClicked` delegate; gate via `SetInteractable` (use for Play all-ready gating instead of `UiToolkit.SetButtonInteractable`). |
| **Ready toggle** (checkbox) | Unity `Toggle`, game-styled in prefab: `UIModuleTelemetryInformation.SettingButton` — `…Home.View.ViewModules\UIModuleTelemetryInformation.cs:14` (`public Toggle SettingButton`). Alt: `GameAdditionalContentEntry.ActivateEntitlementToggle` (`.isOn`) inside the `UIModuleGameSettings` DLC list (`UIModuleGameSettings.cs:151,198`). | Clone the toggle GO; `Toggle.onValueChanged` → `OnLobbyToggleReady()`. (Simpler alt: keep a `PhoenixGeneralButton` whose label flips READY/NOT-READY — preserves current behavior, fewer moving parts.) |
| **Nickname text input** | Unity `InputField`, game-styled: `ModSettingController.TextField` — `PhoenixPoint.Home.View.ViewControllers\ModSettingController.cs:14` (`public InputField TextField`). | Clone its GO; `InputField.onEndEdit` → `OnLobbyRename(v)`. Keep `characterLimit`. |
| **Scroll list** (roster + saves) | `VerticalScrollRectScroller Scroller` — `…ViewModules\UIModuleSaveGame.cs:26`; content parent `_listItemsParent` (`:34`); nav via `UINavigationalElementsHolder` (`Base.UI\UINavigationalElementsHolder.cs`). Init: `Scroller.Init(GameUtl.GameComponent<InputController>())` (`UIModuleSaveGame.cs:102`). | After building rows, call `Scroller.Init(...)`; feed nav elements to `UINavigationalElementsHolder.SetFixedInteractableElements(...)` (pattern: `UIModuleSaveGame.cs:308`). |
| **Save row** (name + dates + load btn) | `UIModuleSaveGameSlot SaveSlotPrefab` — `…ViewModules\UIModuleSaveGame.cs:22` (field) / row class `…ViewModules\UIModuleSaveGameSlot.cs:22`. Fields: `SaveNameField`/`IngameDateField`/`RealtimeDateField`/`LocationField` (`Text`, `:29-38`), `SaveIcon` (`Image`), `LoadGameButton`/`DeleteGameButton` (`Button`, `:47-50`); events `OnLoadPressed`/`OnDeletePressed` (`:95,97`); `InitUsedSaveSlot(meta, isSaveMode, onOverwrite, onLoad, onDelete, showConfirm)` (`:113`). | For the **picker**: clone `SaveSlotPrefab`, call `InitUsedSaveSlot(meta, false, …, onLoad: m => HostStartSession(m), onDelete: noop, showConfirm:false)` — gives native row visuals + our handler. |
| **Roster row** (name + status/progress) | Reuse `UIModuleSaveGameSlot` as a generic 2-line row (`SaveNameField` = player name+tags, `RealtimeDateField` = READY / progress %), buttons hidden; OR clone a single styled `Text` (e.g. from any module) into list rows. Simpler: a small custom row built from the cloned `Text` + `Toggle` widgets above. | Set the two `Text` values each `Refresh()`; recolor via `Text.color` (already done in `LobbyPanel.RefreshRoster`). |
| **Panel / window background** | No standalone "panel" template found cheaply. Two options: (1) clone a native module **root** (e.g. the `UIModuleSaveGame` subtree) and strip its content; (2) parent rows into a plain `RectTransform` and apply a native background `Sprite` (e.g. reuse a sprite from a captured prefab's root `Image`). Lowest effort = (2) with a captured sprite, or simply rely on the cloned scroll-list module's own framing. | Set background `Image.sprite` from a captured native sprite; otherwise keep a subtle solid (acceptable, but less "native"). |
| **List-build helper** (optional) | `UIUtil.EnsureActiveComponentsInContainer<T,E>(container, prefab, elements, init)` — `Base.UI\UIUtil.cs:109`; pooled, the game's own list builder (`UIModuleModManager.Init:145`). Also `GetComponentsFromContainer` (`:154`). | Use it to pool+init rows instead of the hand-rolled pool in `LobbyPanel`/`SavePickerPanel` (drop-in replacement for the row loops). |

---

## 3. Save-picker: reuse native Load screen vs clone its row template

**Decision: CLONE the native save-ROW template (`SaveSlotPrefab` / `UIModuleSaveGameSlot`)
into our own scroll list — do NOT re-host the whole `LoadGameModule`.**

Why not re-host `LoadGameModule`:
- The native load screen is driven by `UIStateHomeLoadGame`
  (`…Home.View.ViewStates\UIStateHomeLoadGame.cs`) which toggles
  `_pauseScreenModule.LoadGameModule` active and calls `InitLoadMode(...)`
  (`UIStateHomeLoadGame.PushState:22`). `LoadGameModule` is a serialized
  `UIModuleSaveGame` field on `UIModulePauseScreen` (`UIModulePauseScreen.cs:48`,
  `[LinkableUIAsset]`). Its rows' load buttons hard-wire `OnLoadGamePressed →
  PhoenixSaveManager.LoadGame(meta)` (`UIModuleSaveGame.cs:166-171`) — i.e. it would
  start a **single-player** load, exactly what lobby-first must avoid. Re-hosting also
  yanks the pause-screen panel state around (`PausePanel.SetActive(false)`).

Why cloning the row works and ties to the chosen data path:
- `UIModuleSaveGame.InitializeSaveGames` already enumerates
  `saveManager.GetSaves().ToList()` and builds rows via `GetSaveSlotModule(i)` +
  `InitUsedSaveSlot(...)` (`UIModuleSaveGame.cs:246, 293-296`). We mirror that loop over
  our **already-chosen** source `PhoenixSaveManager.GetSaves()`
  (`SavePickerPanel.GetLoadableSaves` → `sm.GetSaves()`, mod `SavePickerPanel.cs:137`;
  decompile `PhoenixSaveManager.GetSaves:279`, per `flow-reconciliation-plan.md §2/§7`).
- For each `SavegameMetaData`/`PPSavegameMetaData`, clone `SaveSlotPrefab` into our
  scroller content and call `InitUsedSaveSlot(meta, isSaveMode:false, onOverwrite:noop,
  onLoad: chosen => MultiplayerUI.OnPickSave(chosen), onDelete:noop,
  showSaveConfirmationPopup:false)`. The native row paints name/date/location/icon for
  us; our `onLoad` routes to `SaveTransferCoordinator.HostStartSession(chosen)`
  (unchanged network entry, `MultiplayerUI.OnLobbyPlay → _savePicker.Show(...)`).
- Net: native row visuals, our control flow, same data + same transfer call.

Capture note: `SaveSlotPrefab` is a serialized field on the `UIModuleSaveGame` component
that is itself a child of `UIModulePauseScreen` (`LoadGameModule`). That module GO is
present in the menu UI tree but **inactive** until the load screen first opens
(`UIModulePauseScreen.ShowModule` sets it inactive; `OnLoadPressed`/`UIStateHomeLoadGame`
activate it — `UIModulePauseScreen.cs:129,159-161`). The **prefab reference field** is
readable even while the host module is inactive (reflection on the component), so capture
does not require opening the screen — but see §6 risk + fallback.

---

## 4. How native widgets get their style (and what a clone loses)

- Style is **on the prefab**, not in code: each native widget carries `Animator` +
  `UIColorController`/`UIInteractableColorController`
  (`Base.UI\UIColorController.cs`, `UIInteractableColorController.cs`), themed sprites,
  and the game font on its `Text` children. `PhoenixGeneralButton` even self-manages
  hover/press/enabled visual states via its `Animator`
  (`PhoenixGeneralButton.Awake:104`, `SetAnimationState:370`, `UpdateColorElements:157`).
  Therefore `Object.Instantiate(prefab)` reproduces the game look with **zero** theme
  wiring — confirmed by the mod's existing button clone rendering correctly.
- What a clone **loses / must be re-wired**:
  - **Event handlers**: cloned `Button.onClick` / `Toggle.onValueChanged` /
    `InputField.onEndEdit` / `PhoenixGeneralButton.PointerClicked` carry the *original*
    listeners (e.g. a cloned save row still points `OnLoadPressed` at SP load). Always
    `RemoveAllListeners()` (or reset the `PhoenixGeneralButton` event via
    `RemoveAllClickedDelegates`, `PhoenixGeneralButton.cs:361`) and attach ours — exactly
    as `MainMenuPatches.cs:55` does.
  - **Controllers expecting a parent module**: `UIModuleSaveGameSlot.Awake` wires its
    buttons to native handlers (`UIModuleSaveGameSlot.cs:208`); using `InitUsedSaveSlot`
    with our delegates overrides the *behavior*, but the slot still expects a
    `MessageBox` (`InternalInit` grabs `GameUtl.GetMessageBox()`, `:189-196`) — fine, the
    box exists. If we suppress confirmation (`showConfirmationForLoad:false`) the box is
    not shown.
  - **Navigation**: cloned `Selectable`s are not in any `UINavigationalElementsHolder`.
    Re-register via `SetFixedInteractableElements(...)` (pattern `UIModuleSaveGame.cs:308`)
    or set `Navigation.Mode.Automatic` (mod already does this for the menu button,
    `MainMenuPatches.cs:51-53`) so gamepad/keyboard nav works.
  - **Scroller init**: a cloned `VerticalScrollRectScroller` needs
    `Scroller.Init(GameUtl.GameComponent<InputController>())` once
    (`UIModuleSaveGame.cs:102`).

---

## 5. Parenting / canvas at the main menu

- The main-menu modules live under a **root Canvas that has `UIModuleBehavior`
  children** — the game collects exactly these:
  `Object.FindObjectsOfType<Canvas>()` → keep `val.isRootCanvas &&
  GetComponentInChildren<UIModuleBehavior>() != null`
  (`HomeScreenView.InitView:88-93`).
- **Recommended parent = the Canvas that owns `UIModuleMainMenuButtons`**, reached from
  the mod's existing, proven injection point:
  `GameModueButtonsGroup.GetComponentInParent<Canvas>()` (the field is already captured
  in `MainMenuPatches.cs:35`). Parenting our lobby panel there makes it render in the
  menu's own canvas with the game's `CanvasScaler` + correct sort order — **replacing**
  the mod's separate `MultiplayerCanvas` overlay (`MultiplayerUI.EnsureUiRoot:57-85`).
- The capture+parent must happen while the menu UI exists. The existing
  `InjectNetworkButtonPatch.Postfix` (runs in `UIModuleMainMenuButtons.Init`) is the
  natural hook to ALSO grab the menu Canvas (and any same-frame templates) and hand them
  to `MultiplayerUI`. The cloned panel persists with the mod GO across menu→geoscape (as
  the overlay does today), or is rebuilt per menu entry.

---

## 6. Migration outline (UI swap only — network logic untouched)

### Removed / gutted
- `Multiplayer\src\UI\UiToolkit.cs` — DELETE (Arial-font, hand-rolled `Text`/`Button`/
  `InputField` builders). Superseded by cloned native widgets.
- `Multiplayer\src\UI\MultiplayerUI.cs` — REMOVE the overlay-canvas plumbing:
  `EnsureUiRoot` / `_canvas` / `MultiplayerCanvas` / `CanvasScaler` /
  `GraphicRaycaster` / `EnsureEventSystem` (`MultiplayerUI.cs:57-99`) and the
  `CreateText`/`CreateInGameBar` from-code helpers (`:357-424`). Keep the class as the
  controller (Instance, Update loop, the MessageBox connect dialogs, all `OnLobby*`
  callbacks).
- `LobbyPanel.cs` / `SavePickerPanel.cs` — REPLACE their `Build`/row-construction bodies
  (the `new GameObject(...) + AddComponent<Image/Text/Button/InputField>` paths) with
  clone-from-template construction. **Keep verbatim**: `Refresh`, `RefreshRoster`,
  `RefreshControls`, `AllReady`, `ProgressFor`, `GetLoadableSaves`, `DescribeSave`,
  `OnPick` — all read from `NetworkEngine`/`SessionManager`/`SaveTransferCoordinator`
  and are pure UI-data logic, not network logic.

### New native-build structure (suggested)
- `NativeWidgetFactory` (small) — captures + clones templates:
  - `CaptureFromMainMenu(UIModuleMainMenuButtons inst)`: `TemplateMenuButton`,
    `GameModueButtonsGroup.GetComponentInParent<Canvas>()` (parent), and (if reachable)
    the `UIModulePauseScreen` via `FindObjectsOfType<UIModulePauseScreen>()` →
    `SaveGameModule.SaveSlotPrefab` field by reflection.
  - `CloneButton(parent, label, onClick)`, `CloneToggle(...)`, `CloneInput(...)`,
    `CloneSaveRow(parent)`, `CloneScroller(parent)` — each `Instantiate` + reset
    listeners + set Navigation, returning the typed component.
- `LobbyPanel.Build(Canvas menuCanvas)` — clone a container under `menuCanvas`; build
  roster scroller (cloned `VerticalScrollRectScroller`), nickname (cloned `InputField`),
  Leave/Ready/Play (cloned `TemplateMenuButton` or `PhoenixGeneralButton`); rows via
  `UIUtil.EnsureActiveComponentsInContainer` or cloned `UIModuleSaveGameSlot`.
- `SavePickerPanel.Build(Canvas menuCanvas)` — clone a scroller; in `Populate`, clone
  `SaveSlotPrefab` per save and `InitUsedSaveSlot(meta,false,noop,onLoad,noop,false)`.
- `MultiplayerUI` — call `NativeWidgetFactory.CaptureFromMainMenu` from the existing
  `InjectNetworkButtonPatch.Postfix` (or a small new Postfix on the same `Init`), then
  `_lobby.Build(menuCanvas)` / `_savePicker.Build(menuCanvas)`. Everything else
  (dialogs, Update, callbacks) unchanged.

### Unchanged (do NOT touch)
- `NetworkEngine`, `SessionManager` (`GetLobbyRoster`/`HostReady`/`SetClientReady`/
  `SendRename`/`BroadcastPeerList`), `SaveTransferCoordinator.HostStartSession`, the
  message layer, `FinishLevelBarrierPatch`, all `OnLobby*` callbacks. The picker still
  yields a `SavegameMetaData` into `HostStartSession(chosen)` — identical contract.

---

## 7. Risks / SDK limits + fallback

- **R1 — Template capture timing (highest).** `SaveSlotPrefab` lives on
  `UIModuleSaveGame` under `UIModulePauseScreen`, which is **inactive at the main menu**
  until the Load screen first opens (`UIModulePauseScreen.cs:129,159`). The prefab
  *field* is readable by reflection on the inactive component (it is a serialized
  reference, not a live instance), but if `UIModulePauseScreen` is not yet instantiated
  in the menu scene at capture time, capture returns null.
  - *Mitigation:* search via `FindObjectsOfType<UIModuleSaveGame>(includeInactive:true)`
    at the menu; if found, read `SaveSlotPrefab`. Capture lazily on first `Play` (the
    player has clearly reached the menu) rather than in `Awake`.
  - *Fallback:* if `SaveSlotPrefab` is unobtainable, the save-picker keeps the current
    from-code row (guarded). Same fallback for the toggle/input if those modules aren't
    loaded — clone what's available, fall back per-widget.
- **R2 — Cloned controllers expect siblings.** `UIModuleSaveGameSlot`/`PhoenixGeneralButton`
  call `Awake`/`OnEnable` on instantiate and may touch `GameUtl.GetMessageBox()` /
  `InputController`. These globals exist at the menu, but re-parenting a slot outside its
  original `UINavigationalElementsHolder` can break gamepad nav until re-registered (§4).
  - *Mitigation:* always `Scroller.Init(...)` + `SetFixedInteractableElements(...)`; test
    with controller.
- **R3 — Canvas/scaler differences.** Parenting under the menu Canvas (§5) means we
  inherit its `CanvasScaler`; our hand-tuned offsets from the overlay era won't map 1:1.
  - *Mitigation:* lay out with anchors/`LayoutGroup`, not absolute px; or use the cloned
    scroller's own layout.
- **R4 — Geoscape persistence.** Today the overlay survives menu→geoscape
  (`DontDestroyOnLoad` on the event system). A panel parented to the *menu* Canvas dies on
  scene load. That is acceptable: the lobby is a **menu-time** surface (lobby-first), and
  the in-session status bar can stay as the only in-geoscape element (or be re-cloned
  into the geoscape HUD canvas later). Confirm the lobby is hidden before BEGIN anyway
  (`LobbyPanel.Refresh` hides on `SessionStarted`, `LobbyPanel.cs:150`).
- **R5 — No native `UIState` push** (see §1) — out of scope; the clone approach needs
  none.

---

## 8. Decompile anchors cited
- `Base.UI\UIModule.cs:8` — `UIModule` base (`UIModuleDef`, `SetBehaviour`).
- `Base.UI\UIModuleDef.cs`, `Base.UI\LinkableUIAsset.cs` — module def + serialized-asset attr.
- `Base.UI\UIUtil.cs:109,154` — `EnsureActiveComponentsInContainer`, `GetComponentsFromContainer` list builders.
- `Base.UI\UINavigationalElementsHolder.cs` — nav registration (`SetFixedInteractableElements`).
- `PhoenixPoint.Home.View\HomeScreenView.cs:29,84-93,101` — typed state stack + module-by-tag + root-canvas collection.
- `…Home.View.ViewModules\UIModuleMainMenuButtons.cs:131-141` — native `Instantiate(TemplateMenuButton,…)` clone pattern; `TemplateMenuButton`/`GameModueButtonsGroup` fields.
- `…Common.View.ViewControllers\PhoenixGeneralButton.cs:9,22,66,282` — styled button (`PointerClicked`, `BaseButton`, `SetInteractable`).
- `…Home.View.ViewModules\UIModuleTelemetryInformation.cs:14` — `Toggle SettingButton` (native toggle).
- `…Home.View.ViewControllers\ModSettingController.cs:14` — `InputField TextField` (native input).
- `…Common.View.ViewModules\UIModuleSaveGame.cs:22,26,34,102,246,293-296,308,321` — save list: `SaveSlotPrefab`, `Scroller`, `_listItemsParent`, build loop, slot instantiate.
- `…Common.View.ViewModules\UIModuleSaveGameSlot.cs:22,29-50,113,166` — save-row template fields + `InitUsedSaveSlot` + native SP-load wiring.
- `…Common.View.ViewModules\UIModulePauseScreen.cs:45,48,129,159-161` — `SaveGameModule`/`LoadGameModule` (`UIModuleSaveGame`, `[LinkableUIAsset]`), inactive-until-open.
- `…Home.View.ViewStates\UIStateHomeLoadGame.cs:15,22,32` — native load-state drives `LoadGameModule.InitLoadMode` (why re-hosting = SP load).
- Mod: `Multiplayer\src\Harmony\MainMenuPatches.cs:35,40-60` (proven clone), `MultiplayerUI.cs:57-99,357-424` (overlay to remove), `LobbyPanel.cs`, `SavePickerPanel.cs`, `UiToolkit.cs`.
- Data path (unchanged): `PhoenixSaveManager.GetSaves:279`, `SerializationComponent.ReadSavegameBinary:280` (per `flow-reconciliation-plan.md §2/§7`).
