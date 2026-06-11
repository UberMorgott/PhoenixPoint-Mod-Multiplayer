using System;
using System.Linq;
using System.Reflection;
using Base.Serialization;
using HarmonyLib;
using Multipleer.UI;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Home.View.ViewModules;
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
        /// Open the game's native Load screen by firing the live main-menu LoadGameButton's
        /// PointerClicked event (the exact handler the player triggers, UIStateMainMenu.cs:46-47).
        /// Returns false if the menu module/button could not be found (caller falls back).
        /// </summary>
        public static bool OpenNativeLoadScreen()
        {
            try
            {
                // The live UIModuleMainMenuButtons (even if some children are inactive). Its
                // LoadGameButton is a PhoenixGeneralButton whose PointerClicked is subscribed by the
                // current UIStateMainMenu to OnLoadGameButtonClicked.
                var module = Resources.FindObjectsOfTypeAll<UIModuleMainMenuButtons>()
                    .FirstOrDefault(m => m != null && m.LoadGameButton != null);
                if (module == null) return false;

                PhoenixGeneralButton loadBtn = module.LoadGameButton;
                if (loadBtn == null || loadBtn.PointerClicked == null) return false;

                // Fire the same delegate a real click fires (PhoenixGeneralButton.OnPointerClick:353).
                loadBtn.PointerClicked.Invoke();
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
        [HarmonyPatch(typeof(PhoenixPoint.Home.View.ViewStates.UIStateHomeLoadGame), "ExitState")]
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
