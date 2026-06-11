using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Input;
using Base.Serialization;
using Base.UI;
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

        // The native menu's uGUI Font, captured from the TemplateMenuButton's child Text (the same
        // Text[] UIModuleMainMenuButtons.Init relabels). The native menu UI is uGUI UnityEngine.UI.Text
        // (not TMP) — the template button carries a Text whose .font is the menu typeface — so we reuse
        // that exact Font on every from-code label. Null until captured (UiToolkit falls back to Arial).
        private static Font _menuFont;

        /// <summary>The native main-menu uGUI Font (null until captured), applied to from-code labels.</summary>
        public static Font MenuFont => _menuFont;

        // Root canvases the lobby disabled on its last Show() (so Hide() restores EXACTLY their
        // prior enabled state). The PP home screen is MULTIPLE root canvases (one per module
        // scene), so we disable every root canvas that hosts a UIModuleBehavior wholesale rather
        // than chasing individual chrome objects. Static + idempotent so a double Show never
        // double-stores and a Hide after no Show is a safe no-op.
        private static readonly List<HiddenCanvas> _hiddenCanvases = new List<HiddenCanvas>();
        private static bool _chromeHidden;

        // A disabled root canvas + the enabled value it had BEFORE we disabled it (so restore is
        // exact and never blindly forces enabled=true, which could fight the game's own refresh).
        private struct HiddenCanvas
        {
            public Canvas Canvas;
            public bool WasEnabled;
        }

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
            // menuButtonsModule was used by the old chrome-hide path (now superseded by the
            // root-canvas sweep in HideMenuChrome); the param is kept for call-site compatibility.

            // Capture the menu typeface from the template button's child Text (includeInactive: the
            // template is kept inactive as a prefab). Guarded — a missing Text/font leaves _menuFont
            // null and UiToolkit keeps its Arial fallback.
            if (_menuFont == null && _menuButtonTemplate != null)
            {
                try
                {
                    var t = _menuButtonTemplate.GetComponentsInChildren<Text>(true).FirstOrDefault(x => x != null && x.font != null);
                    if (t != null) _menuFont = t.font;
                }
                catch (Exception e)
                {
                    Debug.LogError("[Multipleer] CaptureFromMainMenu (menu font) failed: " + e.Message);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Main-menu chrome hide / restore (lobby reads as a separate page)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Hide ALL main-menu chrome wholesale so the lobby reads as a separate page laid over the
        /// rendered background art. The PP home screen is built from MULTIPLE root canvases (one per
        /// module scene), and decorative art such as the TFTV "TERROR FROM THE VOID" logo does NOT
        /// always live on a canvas that carries a UIModuleBehavior:
        ///   * The TFTV logo is built on HomeScreenModules.MainMenuButtonsModule.VanillaVisuals[0]
        ///     (PhoenixLogo_text / PhoenixLogo_symbol — see refs\TFTV-src\TFTV\TFTVNewGameMenu.cs
        ///     SetTFTVLogo). PP ALSO has HomeSceneReferences.VanillaVisuals (EditionVisualsController),
        ///     a separate scene-visuals root that has NO UIModuleBehavior. The earlier sweep, which
        ///     only disabled root canvases CONTAINING a UIModuleBehavior, left the logo's canvas
        ///     visible behind the lobby.
        /// So we now BROADEN the sweep: disable EVERY active root Canvas EXCEPT our own (the lobby
        /// canvas and the Multipleer status-bar canvas — both children of ModGO). This is safe because
        /// we record each canvas's prior enabled value and RestoreMenuChrome() puts every one back
        /// exactly, and because our own buttons render on the (excluded) lobby canvas and route input
        /// through the EventSystem (not a separate canvas), so disabling the rest cannot break them.
        ///
        /// We toggle Canvas.enabled (NOT GameObject.SetActive, NOT CanvasGroup): that stops rendering
        /// and raycasting while leaving each module's state machine untouched, so the game's own
        /// refresh can't fight us and restore is exact. FindObjectsOfType returns only ACTIVE
        /// canvases, so we never touch (or wrongly re-enable) an intentionally-inactive one. Records
        /// the prior enabled value per canvas; idempotent (a second call without an intervening
        /// Restore is a no-op) so repeated Show() never double-stores.
        /// </summary>
        /// <param name="lobbyCanvas">The lobby's own overlay Canvas; never disabled.</param>
        public static void HideMenuChrome(Canvas lobbyCanvas)
        {
            if (_chromeHidden) return;
            _hiddenCanvases.Clear();

            // The mod's own ModGO root: both our canvases (lobby + status bar) are children of it.
            // Anything under this transform is OURS and must never be disabled. The lobby canvas is a
            // direct child of ModGO, so its parent IS the ModGO transform.
            Transform modRoot = lobbyCanvas != null ? lobbyCanvas.transform.parent : null;

            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isRootCanvas) continue;
                    if (c == lobbyCanvas) continue;                         // never disable the lobby's own canvas
                    if (modRoot != null && c.transform.IsChildOf(modRoot))  // never disable any Multipleer canvas
                        continue;                                           // (status bar lives under the same ModGO)

                    _hiddenCanvases.Add(new HiddenCanvas { Canvas = c, WasEnabled = c.enabled });
                    c.enabled = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] HideMenuChrome (root-canvas sweep) failed: " + e.Message);
            }

            _chromeHidden = true;
        }

        /// <summary>
        /// Restore EXACTLY the root canvases disabled by the last HideMenuChrome(), each back to its
        /// SAVED enabled value (never blindly forced true). Guards nulls (canvases can be destroyed
        /// across a scene change). Safe to call when nothing was hidden. This is the bulletproof
        /// restore the menu depends on after the lobby closes.
        /// </summary>
        public static void RestoreMenuChrome()
        {
            if (!_chromeHidden) return;
            foreach (var h in _hiddenCanvases)
                if (h.Canvas != null) h.Canvas.enabled = h.WasEnabled;
            _hiddenCanvases.Clear();
            _chromeHidden = false;
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

            // Overwrite EVERY Text child, not just the first. The native TemplateMenuButton subtree
            // can carry more than one Text (e.g. a shadow/echo label), and any child we leave alone
            // keeps the prefab's serialized placeholder string ("NEEDS TEXT"), which then renders as a
            // stray ghost label in the lobby. Setting them all to our label removes that placeholder.
            var texts = obj.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++)
                if (texts[i] != null) texts[i].text = label;

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
