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
    /// While the co-op lobby overlay is open, Escape (the "OptionsMenu" input action) should LEAVE
    /// the lobby instead of opening the main-menu Options screen. Escape is event-driven, dispatched
    /// to UIStateMainMenu.OnInputEvent(InputEvent) — there is no per-frame poll to intercept — so we
    /// prefix that handler: when the lobby is open and a "OptionsMenu" press arrives, we run the same
    /// teardown the on-screen LEAVE button does (MultiplayerUI.OnLobbyLeave) and skip the original
    /// (return false → Options not opened). Otherwise the original runs unchanged.
    /// </summary>
    [HarmonyPatch]
    public static class LobbyEscapeLeavePatch
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
                MultiplayerUI.Instance.OnLobbyLeave();
                return false; // skip original → Options menu NOT opened
            }
            return true;
        }
    }
}
