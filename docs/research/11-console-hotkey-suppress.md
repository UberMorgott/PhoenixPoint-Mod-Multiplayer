# 11 — Dev console hotkey vs. UI text focus (suppress while typing)

Base-game decompile tree: `decompiled\AssemblyCSharp\Assembly-CSharp\src\` (see monorepo
`docs/research/source-provenance.md`). The console is a BASE-GAME feature (not TFTV).

## Symptom (confirmed in-game)
- Typing into a Multipleer text field (JOIN IP/STUN prompt, RENAME prompt, CHAT input) and pressing
  the console-open key OPENS the dev/debug console instead of inserting the character — so you cannot
  type an IPv4 address (dots). The hotkey fires GLOBALLY, ignoring Unity UI focus.

## Root cause — raw per-frame key poll, no focus check
- `Base.Utils.GameConsole.GameConsoleInput.Update()` polls keys directly via `Input.GetKeyDown`:
  - `GameConsoleInput.cs:219`
    ```
    if (Input.GetKeyDown((KeyCode)96)                                  // 96 = BackQuote (` / ~)
        || (!_console.Visible && Input.GetKeyDown((KeyCode)47)))       // 47 = Slash
    { ... _console.ToggleVisibility(); }                               // :228
    ```
  - This open path does **NOT** consult `EventSystem.currentSelectedGameObject`, so it eats the key
    even when a UI `InputField` has focus.
- Contrast: the console's *bound-command* path just below DOES guard on focus —
  `GameConsoleInput.cs:286-288` builds `flag = EventSystem.current.currentSelectedGameObject` has an
  `InputField`, then `if (_console.Visible || flag) return;` (`:288`). Only line-219 lacks the guard.
- Bindings: keys are `KeyCode 96` (BackQuote) and `KeyCode 47` (Slash). The user reports the "."/dot
  key — perceived adjacent key; the suppression is binding-agnostic so it fixes whichever key opens it.
- `DisableConsoleAccess` (`GameConsoleWindow.cs:78`, default `true`) is unlocked by a key-sequence
  cheat code (`GameConsoleInput.cs:175-202`, `EnableConsoleCodeInfos`); once unlocked the line-219
  toggle is live.

## Toggle method (the narrow patch point)
- `GameConsoleWindow.ToggleVisibility()` — `GameConsoleWindow.cs:220-226`:
  `((Behaviour)this).enabled = !DisableConsoleAccess && !((Behaviour)_instance).enabled;` then drives
  the animator + clears/activates `_inputField`. `Visible => ((Behaviour)this).enabled`
  (`GameConsoleWindow.cs:82`). Public instance method, multiple callers (line 228, 246, ShowConsole) →
  not inlined → Harmony-patchable.
- Do **NOT** prefix `GameConsoleInput.Update` (one giant per-frame method: also submit/history/
  handler-disable). Do **NOT** blanket-block `ToggleVisibility` either: while the console is OPEN its
  own `_inputField` is force-selected (`GameConsoleWindow.cs:148-151`), so an unconditional
  "InputField focused → block" would trap the console open (can't close with Esc/backquote).

## Fix — `src/Harmony/ConsoleHotkeySuppressPatch.cs`
- `[HarmonyPatch]` + `Prepare()`/`TargetMethod()` resolving `Base.Utils.GameConsole.GameConsoleWindow`
  ::`ToggleVisibility` by name (skips cleanly if absent). Picked up by `MultipleerMain` `PatchAll`.
- Prefix `(object __instance)`:
  - `__instance` visible (`((Behaviour)__instance).enabled`) → this toggle is a CLOSE → `return true`
    (always allow; keeps Esc/backquote close working).
  - console closed (OPEN attempt): read `EventSystem.current.currentSelectedGameObject`; if it is a
    `UnityEngine.UI.InputField` (and not the console's own field) → `return false` (suppress open).
    Otherwise `return true`.
- Single legacy-`InputField` check covers ALL text boxes — console input, native MessageBox input
  prompt (doc 10: legacy uGUI `InputField`), and our chat input. No TMP_InputField in PP, so no
  TextMeshPro assembly reference needed.
- Fully `try/catch`-guarded → on any exception returns `true` (original runs); a throwing prefix could
  break the console permanently, so it must never throw.

## Net behaviour
- Any focused UI text field eats the console key (dot/slash/backquote insert or are ignored, console
  stays closed) → IPv4 typeable; also fixes chat + rename.
- Console still opens normally when no text field is focused, and still closes via Esc/backquote.
