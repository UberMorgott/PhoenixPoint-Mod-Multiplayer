using System;
using System.Reflection;
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

                // Hand the native button template + the menu Canvas to the widget factory so the
                // lobby/save-picker can clone native widgets onto the menu's own canvas (same
                // proven path used for the network button below). Then (re)build the panels.
                var menuCanvas = group.GetComponentInParent<Canvas>();
                // __instance IS the UIModuleMainMenuButtons (this Postfix patches its Init), so it
                // carries the per-edition logo/visual lists (VanillaVisuals/YoeVisuals/CEVisuals/
                // DemoVisuals) the lobby hides while open. Hand it to the factory as a plain object
                // (the patch has no compile-time ref to the game type beyond reflection).
                NativeWidgetFactory.CaptureFromMainMenu(template, menuCanvas, __instance as Component);
                if (menuCanvas != null)
                    MultiplayerUI.Instance?.OnMenuReady(menuCanvas);

                if (group.transform.Find("MultipleerNetworkBtn") != null) return;

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
            catch (Exception e)
            {
                Debug.LogError($"[Multipleer] InjectNetworkButtonPatch failed: {e.Message}");
            }
        }
    }

    

    
}
