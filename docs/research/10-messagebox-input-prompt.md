# 10 — Native MessageBox input prompt: field access, placeholder, focus

Authoritative decompile tree: `decompiled\TFTV` (see monorepo `docs/research/source-provenance.md`).
All file:line below are in `decompiled\AssemblyCSharp\Assembly-CSharp\src\`.

## What the native input prompt is
- `MessageBox.ShowInputPrompt(content, suggestedText, validateHandler, icon, buttons, callback, ...)`
  — `Base.UI.MessageBox\MessageBox.cs:122`. Builds a `ModalData{DialogMode.InputDialogBox}` and calls
  `ShowPromptImpl` (`:154`) → `GetPromptController(InputDialogBox).Show(data)` **synchronously**.
- The input controller = `MessageBoxInputPromptController` (`Base.UI.MessageBox.PromptControllers\MessageBoxInputPromptController.cs`).
  - Field is **`UnityEngine.UI.InputField`** (legacy uGUI, **NOT** TMP): `[SerializeField] private InputField _inputField;` (`:11`).
  - `Show()` (`:13-22`): adds the validator, sets `_inputField.text = data.SuggestedText`, `SetActive(true)`.
  - Focus is engine-driven in `OnShowReady()` (`:24-34`): `((Selectable)_inputField).Select(); _inputField.ActivateInputField();`
    (joystick path shows the virtual keyboard instead).

## Timing — synchronous
- `MessageBoxPromptController.Show` (base, `MessageBoxPromptController.cs:62-123`) calls `OnShowReady()`
  synchronously at `:120` in the normal case. It DEFERS to the next `Update()` (`:134-143`) only when
  `_queryWhenVisible` is set — that happens only if `TextContent.gameObject.activeSelf &&
  !activeInHierarchy` (`:72`), i.e. the MessageBox root isn't yet in the hierarchy. In the main-menu
  context the MessageBox is active, so `Show()` + activation + focus all complete **inside** the
  `ShowInputPrompt(...)` call. ⇒ A mod can reconfigure the field on the very next line; no coroutine needed.

## Reaching the field from mod code
- `GameUtl.GetMessageBox().GetComponentInChildren<InputField>(true)` (includeInactive) — the InputField
  lives on the input-prompt controller, a child of the MessageBox; it's already active after `Show()`.
- `InputField.placeholder` is a `Graphic` (uGUI), in PP's prefab a `UnityEngine.UI.Text`. uGUI shows the
  placeholder automatically iff `text == ""`. So setting `_inputField.text = ""` makes the hint appear;
  set `(placeholder as Text).text` / `.color` to customize.

## CRITICAL constraint — empty OK is BLOCKED by the engine
- `MessageBoxInputPromptController.ValidateResult` (`:50-57`) returns
  `!string.IsNullOrWhiteSpace(_inputField.text)` for OK/Retry/Yes. `OnButtonClick`
  (`MessageBoxPromptController.cs:235-250`) only `Invoke`s the callback when `ValidateResult` is true.
  ⇒ **An empty/whitespace field can never fire the OK callback** — the dialog just stays open.
- Consequence for "web-style empty + placeholder": you cannot rename/connect to nothing by accident; an
  empty submit is silently inert. To "keep current value on empty submit", let the user **Cancel**
  (no-op that preserves current) and rely on the engine block to forbid empty OK. No prefill needed.
- `CreateResultData` (`:59-64`) returns `_inputField.text.Trim()` as `InputTextResult`.
- `CloseModal` (`:36-48`) resets `_inputField.text = ""` and deactivates — so the field is reusable.

## Mod usage (Multipleer)
- `src\UI\MultiplayerUI.cs` — `TryUpgradePromptInput(string placeholderHint)` (helper inserted after
  `OnLobbyRenamePrompt`): finds the field via `GetComponentInChildren<InputField>(true)`, clears `.text`,
  sets `placeholder as Text` `.text`+`.color = new Color(1,1,1,0.4f)` (translucent grey), then
  `EventSystem.current?.SetSelectedGameObject(field.gameObject); field.ActivateInputField(); caretPosition=0`.
  Returns false if the field can't be found → caller's prefilled `suggestedText` survives as a graceful
  fallback (RENAME passes `current` as suggestedText for exactly this reason; JOIN has no value to prefill).
- Called immediately after `mb.ShowInputPrompt(...)` in `OnLobbyRenamePrompt` and `OnLobbyJoinPrompt`
  (synchronous — works because `Show()` activates the field in the same call).
