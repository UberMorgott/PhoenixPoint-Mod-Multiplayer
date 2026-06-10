using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Input;
using Base.Serialization;
using I2.Loc;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Common.View.ViewModules;
using PhoenixPoint.Home.View.ViewControllers;
using PhoenixPoint.Home.View.ViewModules;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Captures + clones NATIVE Phoenix Point UI widgets so the lobby and save-picker
    /// inherit the game's look (style lives on the prefab's components, not in code).
    ///
    /// Capture path is the one already proven by the mod's network-button injection
    /// (Harmony/MainMenuPatches.cs): the same Postfix on UIModuleMainMenuButtons.Init hands
    /// us the TemplateMenuButton GameObject and the menu Canvas. The native save row
    /// (UIModuleSaveGame.SaveSlotPrefab) is captured LAZILY because its host module
    /// (UIModulePauseScreen.LoadGameModule) is inactive until the Load screen first opens.
    ///
    /// Decompile anchors:
    ///   - TemplateMenuButton clone pattern: UIModuleMainMenuButtons.Init (Instantiate →
    ///     SetActive(true) → relabel child Text[0] → wire child Button.onClick).
    ///   - SaveSlotPrefab (UIModuleSaveGameSlot) field on UIModuleSaveGame.cs:22;
    ///     InitUsedSaveSlot(PPSavegameMetaData, bool, Action&lt;PPSavegameMetaData&gt;×3, bool)
    ///     at UIModuleSaveGameSlot.cs:112 self-resets its load/delete delegates (-= then +=),
    ///     so cloned rows do NOT carry the native single-player load wiring.
    ///
    /// Per-widget fallback: any template that cannot be captured returns null/false here and
    /// the caller keeps that one element as the existing from-code widget (UiToolkit), rather
    /// than failing the whole panel.
    /// </summary>
    internal static class NativeWidgetFactory
    {
        // Captured at the main menu (UIModuleMainMenuButtons.Init Postfix).
        private static GameObject _menuButtonTemplate;
        private static Canvas _menuCanvas;

        // The main-menu buttons module (UIModuleMainMenuButtons) instance, captured from the same
        // Init Postfix. Source of the menu "chrome" the lobby hides while open: the buttons module
        // root GameObject + its per-edition visual lists (which carry the PhoenixLogo_* /TFTV logo).
        private static Component _menuButtonsModule;

        // Objects the lobby deactivated on its last Show() (so Hide() restores EXACTLY them). Holds
        // only objects that were active when hidden; cleared on restore. Static + idempotent so a
        // double Show never double-stores and a Hide after no Show is a safe no-op.
        private static readonly List<GameObject> _hiddenChrome = new List<GameObject>();
        private static bool _chromeHidden;

        // Captured lazily on first Play (save row prefab lives on an inactive module).
        private static UIModuleSaveGameSlot _saveRowPrefab;
        private static bool _saveRowProbed;

        /// <summary>The native main-menu Canvas all mod panels parent under (null until captured).</summary>
        public static Canvas MenuCanvas => _menuCanvas;

        /// <summary>True once the menu button template + canvas have been captured.</summary>
        public static bool HasMenuButton => _menuButtonTemplate != null;

        // Called from InjectNetworkButtonPatch.Postfix with the already-grabbed template +
        // the Canvas reached via GameModueButtonsGroup.GetComponentInParent&lt;Canvas&gt;() +
        // the UIModuleMainMenuButtons instance (__instance of the patched Init).
        public static void CaptureFromMainMenu(GameObject templateMenuButton, Canvas menuCanvas,
            Component menuButtonsModule = null)
        {
            if (templateMenuButton != null) _menuButtonTemplate = templateMenuButton;
            if (menuCanvas != null) _menuCanvas = menuCanvas;
            if (menuButtonsModule != null) _menuButtonsModule = menuButtonsModule;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Main-menu chrome hide / restore (lobby reads as a separate page)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Hide the main-menu's own UI chrome so the lobby reads as a separate page laid over the
        /// rendered background art: the menu BUTTONS module root, the LOGO / per-edition title art
        /// (MainMenuButtonsModule.VanillaVisuals/YoeVisuals/CEVisuals/DemoVisuals — these carry the
        /// PhoenixLogo_* images and TFTV's cloned logo, ref TFTVNewGameMenu.SetTFTVLogo), and the
        /// bottom version/build label (UIModuleBuildRevision root). The 3D scene backdrop is NOT a
        /// UI module and is left untouched, so the background art still shows through.
        ///
        /// Records ONLY objects that were active when hidden; idempotent (a second call without an
        /// intervening Restore is a no-op) so repeated Show() never double-stores.
        /// </summary>
        public static void HideMenuChrome()
        {
            if (_chromeHidden) return;
            _hiddenChrome.Clear();

            // Buttons module root (Continue/New Game/Load/etc.).
            if (_menuButtonsModule != null)
                HideOne(_menuButtonsModule.gameObject);

            // Logo + per-edition title art (lists serialized on the buttons module). Only one list is
            // active at a time (the player's edition), but we sweep all — HideOne skips inactive.
            var buttons = _menuButtonsModule as UIModuleMainMenuButtons;
            if (buttons != null)
            {
                HideVisualList(buttons.VanillaVisuals);
                HideVisualList(buttons.YoeVisuals);
                HideVisualList(buttons.CEVisuals);
                HideVisualList(buttons.DemoVisuals);
            }

            // Bottom version/build/copyright label module. Found by type (inactive-safe); its root is
            // the whole revision group. If absent (none in scene), simply nothing to hide.
            try
            {
                var rev = Resources.FindObjectsOfTypeAll<UIModuleBuildRevision>()
                    .FirstOrDefault(m => m != null && m.gameObject.scene.IsValid());
                if (rev != null) HideOne(rev.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] HideMenuChrome (build revision) failed: " + e.Message);
            }

            _chromeHidden = true;
        }

        /// <summary>
        /// Restore EXACTLY the chrome objects hidden by the last HideMenuChrome(). Guards nulls
        /// (objects can be destroyed across a scene change). Safe to call when nothing was hidden.
        /// This is the bulletproof restore the menu depends on after the lobby closes.
        /// </summary>
        public static void RestoreMenuChrome()
        {
            if (!_chromeHidden) return;
            foreach (var go in _hiddenChrome)
                if (go != null) go.SetActive(true);
            _hiddenChrome.Clear();
            _chromeHidden = false;
        }

        // Deactivate one object and remember it — only if it is currently active (so restore never
        // re-activates something the game had intentionally hidden).
        private static void HideOne(GameObject go)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            _hiddenChrome.Add(go);
        }

        private static void HideVisualList(List<GameObject> visuals)
        {
            if (visuals == null) return;
            foreach (var go in visuals)
                HideOne(go);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Menu-style button (native) — TemplateMenuButton clone
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Clone the native TemplateMenuButton into <paramref name="parent"/>, relabel it and
        /// re-wire its click. Mirrors UIModuleMainMenuButtons.Init exactly. Returns the child
        /// Unity Button (gating via Button.interactable), or null if no template was captured.
        /// Re-wire: onClick.RemoveAllListeners() + Navigation.Automatic (cloned listeners would
        /// otherwise still point at the native menu handler).
        /// </summary>
        public static Button CloneMenuButton(Transform parent, string name, string label, Action onClick)
        {
            if (_menuButtonTemplate == null || parent == null) return null;

            var obj = UnityEngine.Object.Instantiate(_menuButtonTemplate, parent);
            obj.name = name;
            obj.SetActive(true);

            var texts = obj.GetComponentsInChildren<Text>(true);
            if (texts.Length > 0)
                texts[0].text = label;

            var btn = obj.GetComponentInChildren<Button>();
            if (btn != null)
            {
                var nav = btn.navigation;
                nav.mode = Navigation.Mode.Automatic;
                btn.navigation = nav;

                btn.onClick.RemoveAllListeners();
                if (onClick != null)
                    btn.onClick.AddListener((UnityAction)(() => onClick()));
            }
            return btn;
        }

        /// <summary>Set the label on a cloned menu button (its first child Text).</summary>
        public static void SetMenuButtonLabel(Button btn, string label)
        {
            if (btn == null) return;
            var t = btn.GetComponentInChildren<Text>(true);
            if (t != null) t.text = label;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Native save row — UIModuleSaveGameSlot clone
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lazily locate the native save-row prefab. UIModuleSaveGame lives under
        /// UIModulePauseScreen and is INACTIVE at the menu, so search includeInactive and read
        /// the public serialized SaveSlotPrefab field. Returns null if the module is not yet in
        /// the scene (caller falls back to a from-code row).
        /// </summary>
        public static UIModuleSaveGameSlot TryGetSaveRowPrefab()
        {
            if (_saveRowPrefab != null) return _saveRowPrefab;
            if (_saveRowProbed && _saveRowPrefab == null) { /* re-probe each call until found */ }

            try
            {
                // FindObjectsOfTypeAll includes INACTIVE objects (the pause-screen save module
                // is inactive at the menu until the Load screen first opens).
                var modules = Resources.FindObjectsOfTypeAll<UIModuleSaveGame>();
                foreach (var m in modules)
                {
                    if (m != null && m.SaveSlotPrefab != null)
                    {
                        _saveRowPrefab = m.SaveSlotPrefab;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] TryGetSaveRowPrefab failed: " + e.Message);
            }
            _saveRowProbed = true;
            return _saveRowPrefab;
        }

        /// <summary>
        /// Clone the native save row under <paramref name="parent"/> (active). The caller then
        /// calls InitUsedSaveSlot(...), which itself resets the load/overwrite/delete delegates,
        /// so no manual listener-reset is needed here. Returns null if no prefab was captured.
        /// </summary>
        public static UIModuleSaveGameSlot CloneSaveRow(Transform parent)
        {
            var prefab = TryGetSaveRowPrefab();
            if (prefab == null || parent == null) return null;

            var row = UnityEngine.Object.Instantiate(prefab, parent);
            row.gameObject.SetActive(true);
            return row;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Native Ready toggle — GameOptionViewController clone
        // ═══════════════════════════════════════════════════════════════════

        private static GameOptionViewController _toggleTemplate;
        private static bool _toggleProbed;

        // Lazily locate a native toggle controller template (GameSettings option). Inactive at
        // the menu → search includeInactive. Returns null if none in the scene (caller falls
        // back to the label-flip menu button).
        private static GameOptionViewController TryGetToggleTemplate()
        {
            if (_toggleTemplate != null) return _toggleTemplate;
            if (_toggleProbed && _toggleTemplate == null) { /* re-probe each call until found */ }
            try
            {
                _toggleTemplate = Resources.FindObjectsOfTypeAll<GameOptionViewController>()
                    .FirstOrDefault(g => g != null && g.CheckedToggle != null);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] TryGetToggleTemplate failed: " + e.Message);
            }
            _toggleProbed = true;
            return _toggleTemplate;
        }

        /// <summary>
        /// Clone the native Ready toggle. Resets the cloned toggle's onValueChanged via the
        /// controller's own RemoveAllClickListeners(), sets a raw runtime label on OptionText
        /// (stripping its Localize so it is not overwritten), and wires our callback. Returns the
        /// Unity Toggle (read .isOn for state), or null if no template was capturable.
        /// </summary>
        public static Toggle CloneReadyToggle(Transform parent, string label, Action<bool> onValueChanged)
        {
            var template = TryGetToggleTemplate();
            if (template == null || parent == null) return null;

            var ctrl = UnityEngine.Object.Instantiate(template, parent);
            ctrl.gameObject.SetActive(true);
            ctrl.RemoveAllClickListeners();

            // Raw label: strip the Localize so OptionText.text is not reset on next loc refresh.
            if (ctrl.OptionText != null)
            {
                var loc = ctrl.OptionText.GetComponent<Localize>();
                if (loc != null) UnityEngine.Object.Destroy(loc);
                ctrl.OptionText.text = label;
            }

            var toggle = ctrl.CheckedToggle;
            if (toggle != null && onValueChanged != null)
                toggle.onValueChanged.AddListener((UnityEngine.Events.UnityAction<bool>)(v => onValueChanged(v)));

            return toggle;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Native scroller — UIModuleSaveGame.Scroller clone (roster + chat)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Clone the native save-list scroller subtree, strip its rows, and return its content
        /// RectTransform (rows are parented here by the caller). Initializes the cloned
        /// VerticalScrollRectScroller with the live InputController so wheel/drag work.
        /// Returns null if no UIModuleSaveGame is in the scene (caller falls back to a plain
        /// RectTransform list).
        /// </summary>
        public static RectTransform CloneScroller(Transform parent)
        {
            if (parent == null) return null;
            try
            {
                var module = Resources.FindObjectsOfTypeAll<UIModuleSaveGame>()
                    .FirstOrDefault(m => m != null && m.Scroller != null
                        && m.Scroller.GetComponent<ScrollRect>() != null);
                if (module == null) return null;

                var scrollerGo = UnityEngine.Object.Instantiate(module.Scroller.gameObject, parent);
                scrollerGo.SetActive(true);

                var scroller = scrollerGo.GetComponent<VerticalScrollRectScroller>();
                var scrollRect = scrollerGo.GetComponent<ScrollRect>();
                var content = scrollRect != null ? scrollRect.content : null;
                if (content == null) return null;

                // Strip any cloned rows from the content.
                for (int i = content.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(content.GetChild(i).gameObject);

                if (scroller != null)
                    scroller.Init(GameUtl.GameComponent<InputController>());

                return content;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] CloneScroller failed: " + e.Message);
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Native panel background sprite
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Find a styled native panel Image sprite to skin the lobby background. Searches the
        /// captured menu Canvas's hierarchy for an Image carrying a sprite; prefers a sliced
        /// sprite (panel-style) and otherwise returns the first sprite found, or null (caller
        /// keeps a solid color).
        /// </summary>
        public static Sprite TryGetPanelBackgroundSprite()
        {
            try
            {
                if (_menuCanvas == null) return null;
                var images = _menuCanvas.GetComponentsInChildren<Image>(true);
                var img = images.FirstOrDefault(i => i != null && i.sprite != null
                    && i.type == Image.Type.Sliced);
                return img != null ? img.sprite : images.FirstOrDefault(i => i != null && i.sprite != null)?.sprite;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] TryGetPanelBackgroundSprite failed: " + e.Message);
                return null;
            }
        }
    }
}
