using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multipleer.Harmony
{
    /// <summary>
    /// Stops the dev/debug console hotkey from firing while the player is typing into a text field
    /// (our JOIN IP/STUN prompt, the RENAME prompt, the CHAT input — and any other UI InputField).
    ///
    /// THE BUG (decompiled, file:line — base game DLL at
    /// decompiled\AssemblyCSharp\Assembly-CSharp\src\):
    ///   Base.Utils.GameConsole.GameConsoleInput.Update() polls keys DIRECTLY every frame and opens
    ///   the console without ever consulting Unity UI focus:
    ///       GameConsoleInput.cs:219
    ///         if (Input.GetKeyDown((KeyCode)96)               // 96 = BackQuote  (` / ~)
    ///             || (!_console.Visible && Input.GetKeyDown((KeyCode)47)))   // 47 = Slash
    ///         { ... _console.ToggleVisibility(); }
    ///   Because this is a raw Input.GetKeyDown poll, the console-open key is swallowed GLOBALLY even
    ///   when one of our InputFields has focus, so the keystroke opens the console instead of being
    ///   inserted into the field (you cannot type an IPv4 address). The console's OWN bound-command
    ///   path right below it (GameConsoleInput.cs:286-298) already guards on a focused InputField via
    ///   EventSystem.currentSelectedGameObject — but the line-219 toggle does NOT.
    ///
    /// THE FIX:
    ///   We cannot prefix GameConsoleInput.Update (one giant per-frame method that also drives submit/
    ///   history/handler-disable). Instead we prefix the narrow method it calls to open/close the
    ///   console: GameConsoleWindow.ToggleVisibility() (GameConsoleWindow.cs:220-226). In the prefix:
    ///     - If the console is CURRENTLY VISIBLE, this call is a CLOSE → always allow (return true) so
    ///       BackQuote/Escape still close it. (The console's own _inputField is focused while visible;
    ///       a blanket InputField block here would otherwise trap the console open — hence the gate.)
    ///     - If the console is NOT visible, this call is an OPEN → suppress (return false) when a UI
    ///       InputField / TMP_InputField currently has focus and it is not the console's own field.
    ///   Suppressing the open whenever ANY text box is focused is the correct general UX (a focused
    ///   text box should eat the key), and it transparently covers our chat input, the native JOIN/
    ///   RENAME prompt, and the lobby — no Multipleer-specific flag needed.
    ///
    /// SAFETY: the console still opens normally whenever no text field is focused; the prefix is fully
    /// guarded and on ANY exception falls through to the original (return true), so it can never break
    /// or permanently disable the console.
    /// </summary>
    [HarmonyPatch]
    public static class ConsoleHotkeySuppressPatch
    {
        private static Type _windowType;

        // Resolve the base-game console window type by name; if the DLL/type is absent the patch is
        // simply skipped (Prepare returns false), never blocking PatchAll.
        public static bool Prepare()
        {
            _windowType = AccessTools.TypeByName("Base.Utils.GameConsole.GameConsoleWindow");
            return _windowType != null
                && AccessTools.Method(_windowType, "ToggleVisibility") != null;
        }

        public static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(_windowType, "ToggleVisibility");

        // Prefix on GameConsoleWindow.ToggleVisibility(). Returning false skips the open/close.
        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            try
            {
                // __instance is the GameConsoleWindow. Its Visible property == ((Behaviour)this).enabled.
                // If already visible, this toggle is a CLOSE — never suppress (keep Esc/backquote close).
                var beh = __instance as Behaviour;
                if (beh != null && beh.enabled)
                    return true; // console is open → this call closes it → allow

                // Console is closed → this call would OPEN it. Suppress only if a text field is focused.
                var es = EventSystem.current;
                var sel = es != null ? es.currentSelectedGameObject : null;
                if (sel == null)
                    return true; // nothing focused → normal console open

                // Defensive: never suppress because of the console's OWN input field (it isn't selected
                // while the console is closed, but guard anyway so we can't trap the console shut).
                var consoleField = (__instance as Component)
                    ?.GetComponentInChildren<InputField>(true);
                if (consoleField != null && consoleField.gameObject == sel)
                    return true;

                // Any legacy uGUI InputField focused? PP uses legacy UnityEngine.UI.InputField for
                // every text box (the console, the native MessageBox input prompt, and our chat) —
                // no TMP_InputField is used, so this single check covers all of them.
                if (sel.GetComponent<InputField>() != null)
                    return false; // text box has focus → eat the key, do NOT open the console

                return true; // focused object is not a text field → normal console open
            }
            catch (Exception e)
            {
                // Must never throw — a throwing prefix could break the console permanently.
                Debug.LogError("[Multipleer] ConsoleHotkeySuppressPatch error: " + e.Message);
                return true; // fall through to the original toggle
            }
        }
    }
}
