using System;
using System.Reflection;
using Base.Serialization;
using Base.UI;
using HarmonyLib;
using Multipleer.UI;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Home.View;
using PhoenixPoint.Home.View.ViewStates;
using UnityEngine;

namespace Multipleer.Harmony
{
    /// <summary>
    /// Co-op "Choose save" intercept. Instead of hand-building a save-row list (which kept
    /// rendering empty), the host opens the game's OWN native Load screen and we INTERCEPT the
    /// load-on-click so it does NOT load the campaign — it captures the chosen SavegameMetaData
    /// and hands it back to the lobby as the co-op base save.
    ///
    /// NATIVE LOAD FLOW (decompiled, file:line):
    ///   - Open: UIStateMainMenu.OnLoadGameButtonClicked() → SwitchToState(new UIStateHomeLoadGame(),
    ///     PushOnTop)  (UIStateMainMenu.cs:134-137). That handler is subscribed to the menu's
    ///     LoadGameButton.PointerClicked (UIStateMainMenu.cs:46-47), so firing that event opens the
    ///     screen exactly like the player clicking "LOAD GAME".
    ///   - Build: UIStateHomeLoadGame.EnterState activates LoadGameModule (a UIModuleSaveGame),
    ///     PushState → InitLoadMode(SwitchToPreviousState, showConfirmationForLoad:false)
    ///     (UIStateHomeLoadGame.cs:12-28) → InitializeSaveGames builds rows via
    ///     UIModuleSaveGameSlot.InitUsedSaveSlot(..., onLoad: OnLoadGamePressed, ...)
    ///     (UIModuleSaveGame.cs:276,283,294).
    ///   - LOAD-ON-CLICK (what we avoid): slot click → UIModuleSaveGameSlot.OnLoadGamePressedCrt →
    ///     OnLoadPressed.Invoke(_data) (UIModuleSaveGameSlot.cs:273) → wired to
    ///     UIModuleSaveGame.OnLoadGamePressed(PPSavegameMetaData) (UIModuleSaveGame.cs:166-171) →
    ///     SaveManager.LoadGame(meta).  >>> We Prefix UIModuleSaveGame.OnLoadGamePressed and, while
    ///     armed, capture the meta + close the screen + return false (skip the real load). <<<
    ///   - Close (cancel OR after pick): UIStateHomeLoadGame.ExitState (UIStateHomeLoadGame.cs:30-34)
    ///     runs in BOTH cases, so a Postfix on it re-shows the lobby and (if a meta was captured)
    ///     hands it to the lobby's chosen-save handler.
    ///
    /// FEASIBILITY: pushing UIStateHomeLoadGame is a normal home-screen state push; it does NOT
    /// touch the network session (engine is independent of the UI) and does NOT destroy the mod's
    /// own ModGO canvases. The only conflict is visual: the lobby's Show() disables every native
    /// root canvas (NativeWidgetFactory.HideMenuChrome), so we MUST restore chrome before opening
    /// the native screen and re-hide it (via lobby Show) afterwards — done by MultiplayerUI.
    /// </summary>
    [HarmonyPatch]
    public static class SaveLoadInterceptPatch
    {
        // Co-op pick mode. While armed, the native load-on-click is intercepted instead of loading.
        private static bool _armed;
        private static Action<SavegameMetaData> _onPicked;
        private static SavegameMetaData _pickedMeta;

        public static bool IsArmed => _armed;

        /// <summary>Arm co-op pick mode: the next native save load is captured, not executed.</summary>
        public static void Arm(Action<SavegameMetaData> onPicked)
        {
            _armed = true;
            _pickedMeta = null;
            _onPicked = onPicked;
        }

        /// <summary>Disarm and forget any captured meta + callback.</summary>
        public static void Disarm()
        {
            _armed = false;
            _pickedMeta = null;
            _onPicked = null;
        }

        /// <summary>
        /// Open the game's native Load screen by driving the home-screen state stack straight to
        /// UIStateHomeLoadGame — exactly what UIStateMainMenu.OnLoadGameButtonClicked does
        /// (SwitchToState(new UIStateHomeLoadGame(), PushOnTop), UIStateMainMenu.cs:134-137).
        ///
        /// WHY NOT the LoadGameButton.PointerClicked event: that handler is (un)subscribed per
        /// UIStateMainMenu Enter/Exit (UIStateMainMenu.cs:47/72). Pushing the load state on top runs
        /// MainMenu.ExitState (StateStack.SwitchToState:79), which REMOVES the subscription; by the
        /// 2nd Choose-Save click the cached button's PointerClicked is stale/null, so firing it was
        /// silently a no-op (and the lobby then fell to the wrong custom window). Pushing the state
        /// directly is subscription-independent and works on EVERY click. The pick is still captured
        /// by our Prefix on UIModuleSaveGame.OnLoadGamePressed regardless of how the screen opened.
        ///
        /// Returns false if the live HomeScreenView / its state stack could not be reached (caller
        /// surfaces a warning — there is NO custom-window fallback).
        /// </summary>
        public static bool OpenNativeLoadScreen()
        {
            try
            {
                // _statesStack is private (StateStack<HomeScreenViewContext>); reach it by reflection.
                var stackField = AccessTools.Field(typeof(HomeScreenView), "_statesStack");
                if (stackField == null)
                {
                    Debug.LogError("[Multipleer] OpenNativeLoadScreen: HomeScreenView._statesStack field not found.");
                    return false;
                }

                // Find the LIVE home-screen view — the one whose state stack is already initialized
                // (FindObjectsOfTypeAll can also surface uninitialized template instances; pick the
                // one with a non-null _statesStack).
                object stack = null;
                foreach (var v in Resources.FindObjectsOfTypeAll<HomeScreenView>())
                {
                    if (v == null) continue;
                    var s = stackField.GetValue(v);
                    if (s != null) { stack = s; break; }
                }
                if (stack == null)
                {
                    Debug.LogError("[Multipleer] OpenNativeLoadScreen: no live HomeScreenView with an initialized state stack.");
                    return false;
                }

                // Push UIStateHomeLoadGame on top exactly like UIStateMainMenu.OnLoadGameButtonClicked.
                var switchToState = AccessTools.Method(stack.GetType(), "SwitchToState");
                if (switchToState == null)
                {
                    Debug.LogError("[Multipleer] OpenNativeLoadScreen: StateStack.SwitchToState not found.");
                    return false;
                }

                var loadState = new UIStateHomeLoadGame();
                switchToState.Invoke(stack, new object[] { loadState, StateStackAction.PushOnTop });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] OpenNativeLoadScreen failed: " + e.Message);
                return false;
            }
        }

        // ── Prefix: intercept the actual load on a save click ──────────────────────────────────
        // UIModuleSaveGame.OnLoadGamePressed(PPSavegameMetaData) is the method that calls
        // SaveManager.LoadGame (UIModuleSaveGame.cs:166-171). While armed we capture the meta,
        // close the native screen, and skip the load.
        [HarmonyPatch(typeof(UIModuleSaveGame), "OnLoadGamePressed")]
        [HarmonyPrefix]
        public static bool OnLoadGamePressed_Prefix(UIModuleSaveGame __instance, PPSavegameMetaData ppSaveGameChosen)
        {
            if (!_armed) return true; // normal single-player load

            try
            {
                _pickedMeta = ppSaveGameChosen;

                // Close the native Load screen the same way its own close button does
                // (UIModuleSaveGame.CloseModule:145 → Close == SwitchToPreviousState). That triggers
                // UIStateHomeLoadGame.ExitState, where our Postfix re-shows the lobby + delivers the
                // captured meta. Use reflection: CloseModule is private.
                var close = AccessTools.Method(typeof(UIModuleSaveGame), "CloseModule");
                close?.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] OnLoadGamePressed intercept failed: " + e.Message);
            }

            return false; // skip SaveManager.LoadGame — we only wanted the metadata
        }

        // ── Postfix: native Load screen closed (pick OR cancel) → back to the lobby ─────────────
        // UIStateHomeLoadGame.ExitState runs on every close (UIStateHomeLoadGame.cs:30). If armed,
        // re-show the lobby; if a meta was captured, deliver it to the lobby's chosen-save handler.
        [HarmonyPatch(typeof(UIStateHomeLoadGame), "ExitState")]
        [HarmonyPostfix]
        public static void LoadScreenExitState_Postfix()
        {
            if (!_armed) return;

            var meta = _pickedMeta;
            var cb = _onPicked;
            Disarm();

            try
            {
                // Re-show the lobby (re-hides the native chrome we restored on open) and deliver the
                // chosen save (null on cancel → just re-open the lobby with no selection change).
                MultiplayerUI.Instance?.OnNativeSaveScreenClosed(meta, cb);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] LoadScreen exit handling failed: " + e.Message);
            }
        }
    }
}
