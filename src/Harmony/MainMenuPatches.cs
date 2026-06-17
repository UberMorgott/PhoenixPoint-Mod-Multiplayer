using System;
using System.Reflection;
using Base.Input;
using HarmonyLib;
using Multipleer.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Multipleer.Harmony
{
    [HarmonyPatch]
    public static class InjectNetworkButtonPatch
    {
        private static Type _targetType;
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            _targetType = AccessTools.TypeByName("PhoenixPoint.Home.View.ViewModules.UIModuleMainMenuButtons");
            if (_targetType == null) return false;
            _targetMethod = AccessTools.Method(_targetType, "Init");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                var templateField = AccessTools.Field(_targetType, "TemplateMenuButton");
                var groupField = AccessTools.Field(_targetType, "GameModueButtonsGroup");
                var template = templateField?.GetValue(__instance) as GameObject;
                var group = groupField?.GetValue(__instance) as GameObject;
                if (template == null || group == null) return;

                var menuCanvas = group.GetComponentInParent<Canvas>();

                // Capture the native template + menu Canvas for the widget factory (cheap, can't
                // throw meaningfully). Needed by both the button below and the lobby panels.
                NativeWidgetFactory.CaptureFromMainMenu(template, menuCanvas, __instance as Component);

                // ── 1) INJECT THE NETWORK BUTTON FIRST ──────────────────────────────────────
                // This MUST happen before any panel-build work (step 2). Init clears the whole
                // GameModueButtonsGroup on EVERY (re)entry via Object.Destroy (deferred to end of
                // frame), so on first load the button was being added correctly — but the network
                // button used to be injected AFTER OnMenuReady(menuCanvas), and an exception thrown
                // while building the lobby/save-picker there unwound past the injection and was
                // swallowed by the outer catch → button absent on FIRST menu load, reappearing only
                // on a later re-entry where the build no longer threw. Ordering the injection first
                // makes the button independent of panel-build success.
                InjectNetworkButton(template, group);

                // ── 2) BUILD THE LOBBY / SAVE-PICKER PANELS ─────────────────────────────────
                // __instance IS the UIModuleMainMenuButtons (this Postfix patches its Init), so it
                // carries the per-edition logo/visual lists the lobby hides while open. A failure
                // here is isolated (own try/catch) so it can NEVER suppress the button above.
                if (menuCanvas != null)
                {
                    try { MultiplayerUI.Instance?.OnMenuReady(menuCanvas); }
                    catch (Exception e)
                    {
                        Debug.LogError($"[Multipleer] OnMenuReady (panel build) failed: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Multipleer] InjectNetworkButtonPatch failed: {e.Message}");
            }
        }

        // Add exactly ONE "NETWORK GAME" button to the menu's GameModueButtonsGroup, robustly.
        //
        // Init destroys every group child on each (re)entry, but Object.Destroy is DEFERRED to the
        // end of the frame: a being-destroyed child is still found by transform.Find this same
        // frame. The old dedup `Find("MultipleerNetworkBtn") != null → return` therefore risked
        // matching a stale, about-to-die button on a same-session re-Init and SKIPPING the re-add —
        // after which the deferred Destroy removed it, leaving no button until the next Init. We fix
        // that by IMMEDIATELY destroying any pre-existing MultipleerNetworkBtn children (so a stale
        // one can never be mistaken for a live one) and then always instantiating a fresh button.
        // Net result: exactly one live button after every Init, first load included.
        private static void InjectNetworkButton(GameObject template, GameObject group)
        {
            // Remove any pre-existing instances NOW (immediate, not deferred) so the fresh one we add
            // below is unambiguously the only MultipleerNetworkBtn in the group this frame.
            for (int i = group.transform.childCount - 1; i >= 0; i--)
            {
                var child = group.transform.GetChild(i);
                if (child != null && child.name == "MultipleerNetworkBtn")
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            var obj = UnityEngine.Object.Instantiate(template, group.transform);
            obj.name = "MultipleerNetworkBtn";
            obj.gameObject.SetActive(true);

            var texts = obj.GetComponentsInChildren<Text>(true);
            if (texts.Length > 0)
                texts[0].text = "NETWORK GAME";

            var btn = obj.GetComponentInChildren<Button>();
            if (btn != null)
            {
                var nav = btn.navigation;
                nav.mode = Navigation.Mode.Automatic;
                btn.navigation = nav;

                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener((UnityAction)(() =>
                {
                    MultiplayerUI.Instance?.ShowNetworkMenu();
                }));
            }
        }
    }

    /// <summary>
    /// While the co-op lobby overlay is open, Escape (the "OptionsMenu" input action) must NOT leave
    /// the lobby and must NOT open the main-menu Options screen over it — the ONLY way out of the
    /// lobby is the explicit on-screen LEAVE button (MultiplayerUI.OnLobbyLeave). Escape is
    /// event-driven, dispatched to UIStateMainMenu.OnInputEvent(InputEvent), so we prefix that
    /// handler: when the lobby is open and an "OptionsMenu" press arrives, we simply SWALLOW it
    /// (return false → original skipped, Options not opened, nothing torn down). Outside the lobby
    /// the original runs unchanged. (Accidental Escape used to close the host listener / break the
    /// session — that teardown is removed here.)
    /// </summary>
    [HarmonyPatch]
    public static class LobbyEscapeSuppressPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Home.View.ViewStates.UIStateMainMenu");
            if (t == null) return false;
            _targetMethod = AccessTools.Method(t, "OnInputEvent");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPrefix]
        public static bool Prefix(InputEvent ev)
        {
            if (MultiplayerUI.Instance?.IsLobbyOpen == true
                && ev.Type == InputEventType.Pressed
                && ev.Name == "OptionsMenu")
            {
                // Swallow Escape inside the lobby: do NOT open Options, do NOT leave. Leaving is
                // ONLY via the LEAVE button.
                return false; // skip original
            }
            return true;
        }
    }

    /// <summary>
    /// While the co-op lobby overlay is open, the right-click / back gesture (the "Cancel" input
    /// action) must NOT leave the lobby either. In the home-screen UI framework "Cancel" is captured
    /// in HomeScreenViewState.OnInputEventInternal (sets _cancelEventPending), which the state's
    /// update loop then turns into SwitchToPreviousState()/OnCancel() — i.e. a back-navigation that
    /// can tear down the menu underneath the lobby. We prefix that base handler: when the lobby is
    /// open and a "Cancel" press arrives, we SWALLOW it (return false → the whole base body is
    /// skipped, _cancelEventPending is never set, so no back-navigation fires). The patch lives on
    /// the BASE state so it covers whichever home state hosts the lobby overlay, but the
    /// IsLobbyOpen gate makes it completely inert in every other menu/state — so normal right-click
    /// back-navigation is untouched outside the co-op lobby. Leaving is ONLY via the LEAVE button.
    /// </summary>
    [HarmonyPatch]
    public static class LobbyCancelSuppressPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Home.View.HomeScreenViewState");
            if (t == null) return false;
            _targetMethod = AccessTools.Method(t, "OnInputEventInternal");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPrefix]
        public static bool Prefix(InputEvent ev)
        {
            if (MultiplayerUI.Instance?.IsLobbyOpen == true
                && ev.Type == InputEventType.Pressed
                && ev.Name == "Cancel")
            {
                // Swallow right-click/back inside the lobby: skip the base body so the cancel is
                // never queued and no back-navigation/teardown runs.
                return false; // skip original
            }
            return true;
        }
    }

    /// <summary>
    /// Bug A fix — clean teardown on EVERY native return-to-menu path. PhoenixGame.FinishLevelAndGoToLobby
    /// is the single native chokepoint funnelling pause-menu exit, game-summary exit, game-over/end,
    /// user-switch reset, and the "something went wrong" error-yank back to the main menu. We postfix it
    /// and call NetworkEngine.TearDown() (idempotent), which clears IsActive, nulls the Instance singleton,
    /// detaches WalletWatcher/Sync, and tears down the transport — so the next NETWORK GAME click hosts a
    /// fresh lobby with no process restart. Inert when no session is active (TearDown is a safe no-op).
    /// </summary>
    [HarmonyPatch]
    public static class FinishLevelAndGoToLobbyTearDownPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Common.Game.PhoenixGame");
            if (t == null) return false;
            _targetMethod = AccessTools.Method(t, "FinishLevelAndGoToLobby");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                Multipleer.Network.NetworkEngine.Instance?.TearDown();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Multipleer] FinishLevelAndGoToLobby teardown failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Bug A fix (pair) — rebuild the lobby panels on the freshly-wired main-menu Canvas. After a
    /// scene round-trip the old menu Canvas is destroyed, so the cached lobby/save-picker panels are
    /// bound to dead transforms. UIStateMainMenu.EnterState() re-enters the main-menu state (native
    /// re-init); we postfix it to drop MultiplayerUI's _panelsBuilt latch so the very next
    /// InjectNetworkButtonPatch.Postfix → OnMenuReady rebuilds the panels on the live Canvas. Pairs with
    /// the TearDown patch above so the next NETWORK GAME click hosts a clean lobby.
    /// </summary>
    [HarmonyPatch]
    public static class MainMenuRebuildLobbyPatch
    {
        private static MethodBase _targetMethod;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Home.View.ViewStates.UIStateMainMenu");
            if (t == null) return false;
            _targetMethod = AccessTools.Method(t, "EnterState");
            return _targetMethod != null;
        }

        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                MultiplayerUI.Instance?.RebuildLobbyPanels();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Multipleer] main-menu lobby rebuild failed: {e.Message}");
            }
        }
    }
}
