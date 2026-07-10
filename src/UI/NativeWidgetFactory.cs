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

namespace Multiplayer.UI
{
    /// <summary>
    /// Captures + clones NATIVE Phoenix Point UI widgets so the lobby and save-picker
    /// inherit the game's look (style lives on the prefab's components, not in code).
    ///
    /// Capture path is the one already proven by the mod's network-button injection
    /// (Harmony/MainMenuPatches.cs): the same Postfix on UIModuleMainMenuButtons.Init hands
    /// us the TemplateMenuButton GameObject and the menu Canvas. The native save-list scroller
    /// (UIModuleSaveGame.Scroller) is cloned LAZILY because its host module
    /// (UIModulePauseScreen.LoadGameModule) is inactive until the Load screen first opens.
    ///
    /// Decompile anchors:
    ///   - TemplateMenuButton clone pattern: UIModuleMainMenuButtons.Init (Instantiate →
    ///     SetActive(true) → relabel child Text[0] → wire child Button.onClick).
    ///   - Scroller (VerticalScrollRectScroller) field on UIModuleSaveGame.cs; the save picker
    ///     parents its own lightweight, mod-drawn rows under the cloned scroller's content
    ///     (it does NOT clone the heavyweight native save-slot row prefab).
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
                    Debug.LogError("[Multiplayer] CaptureFromMainMenu (menu font) failed: " + e.Message);
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
        /// canvas and the Multiplayer status-bar canvas — both children of ModGO). This is safe because
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
                    if (modRoot != null && c.transform.IsChildOf(modRoot))  // never disable any Multiplayer canvas
                        continue;                                           // (status bar lives under the same ModGO)

                    _hiddenCanvases.Add(new HiddenCanvas { Canvas = c, WasEnabled = c.enabled });
                    c.enabled = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] HideMenuChrome (root-canvas sweep) failed: " + e.Message);
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
                Debug.LogError("[Multiplayer] CloneScroller failed: " + e.Message);
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
        // ═══════════════════════════════════════════════════════════════════
        //  Native loading bar — Base.Utils.ProgressBarController clone
        // ═══════════════════════════════════════════════════════════════════
        //
        // The vanilla loading-screen bottom bar is Base.Utils.ProgressBarController (MonoBehaviour):
        //   - public Image ProgressFill;  (type=Filled; set .fillAmount 0..1 to drive it)
        //   - public Text  ProgressText;  (the "NN%" text)
        // Its own Update() ramps ProgressFill.fillAmount toward _currentLoadingProgress?.Progress, so
        // to drive it MANUALLY we DISABLE the component (Behaviour.enabled=false) and set fillAmount
        // ourselves. The clonable template is reached at runtime via Base.Utils.SceneFadeController's
        // public ProgressBar field. The mod assembly cannot strong-reference these Base.Utils types,
        // so we resolve them by name (AccessTools.TypeByName / AccessTools.Field) and operate on the
        // UnityEngine base types (Component/GameObject/Behaviour/Image/Text). The curtain (hence the
        // bar) is guaranteed present after MultiplayerUI.DropCurtainEarly().

        /// <summary>
        /// Capture the live native loading-bar GameObject (SceneFadeController.ProgressBar's
        /// gameObject) to use as a clone template. Returns null on any failure (caller falls back to
        /// the from-code row). Safe to call after the curtain has dropped (DropCurtainEarly()).
        /// </summary>
        public static GameObject CaptureLoadingBarTemplate()
        {
            GameObject result = null;
            try
            {
                var sfcType = HarmonyLib.AccessTools.TypeByName("Base.Utils.SceneFadeController");
                if (sfcType == null) return null;
                var sfc = UnityEngine.Object.FindObjectOfType(sfcType);
                if (sfc == null) return null;
                var pbField = HarmonyLib.AccessTools.Field(sfcType, "ProgressBar");
                var pbc = pbField?.GetValue(sfc) as Component;
                if (pbc == null) return null;
                result = pbc.gameObject;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] CaptureLoadingBarTemplate failed: " + e.Message);
                result = null;
            }
            Debug.Log("[Multiplayer] loading-bar template " + (result != null ? "captured" : "NOT found"));
            return result;
        }

        /// <summary>
        /// Capture the LIVE native ProgressBarController COMPONENT (not the template GameObject):
        /// SceneFadeController.ProgressBar. Its ProgressFill.fillAmount is the REAL on-screen eased
        /// value (ProgressBarController.Update eases fillAmount toward the coarse Progress). Returns
        /// null on any failure. Used as the per-frame percent source for the co-op load bars.
        /// </summary>
        public static Component CaptureLiveProgressBar()
        {
            Component result = null;
            try
            {
                var sfcType = HarmonyLib.AccessTools.TypeByName("Base.Utils.SceneFadeController");
                if (sfcType == null) return null;
                var sfc = UnityEngine.Object.FindObjectOfType(sfcType);
                if (sfc == null) return null;
                result = HarmonyLib.AccessTools.Field(sfcType, "ProgressBar").GetValue(sfc) as Component;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] CaptureLiveProgressBar failed: " + e.Message);
                result = null;
            }
            Debug.Log("[Multiplayer] live progress bar " + (result != null ? "captured" : "NOT found"));
            return result;
        }

        /// <summary>Clone a captured loading-bar template under <paramref name="parent"/> and activate it.</summary>
        public static GameObject CloneLoadingBar(GameObject template, Transform parent)
        {
            if (template == null) return null;
            var go = UnityEngine.Object.Instantiate(template, parent);
            go.SetActive(true);
            return go;
        }

        /// <summary>Fetch the cloned bar's ProgressBarController component (by name), or null.</summary>
        public static Component GetProgressBarController(GameObject barClone)
        {
            if (barClone == null) return null;
            var pbcType = HarmonyLib.AccessTools.TypeByName("Base.Utils.ProgressBarController");
            if (pbcType == null) return null;
            return barClone.GetComponent(pbcType);
        }

        /// <summary>The ProgressBarController's ProgressFill Image (the Filled bar we drive), or null.</summary>
        public static Image GetProgressFill(Component pbc)
        {
            if (pbc == null) return null;
            var f = HarmonyLib.AccessTools.Field(pbc.GetType(), "ProgressFill");
            return f?.GetValue(pbc) as Image;
        }

        /// <summary>The ProgressBarController's ProgressText ("NN%") Text, or null.</summary>
        public static Text GetProgressText(Component pbc)
        {
            if (pbc == null) return null;
            var f = HarmonyLib.AccessTools.Field(pbc.GetType(), "ProgressText");
            return f?.GetValue(pbc) as Text;
        }

        /// <summary>Disable the controller's own Update() so we drive ProgressFill.fillAmount manually.</summary>
        public static void DisableController(Component pbc)
        {
            if (pbc is Behaviour b) b.enabled = false;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Native bottom bar — DOWNLOAD-phase driver (client save transfer)
        // ═══════════════════════════════════════════════════════════════════
        //
        // The client's WAN save DOWNLOAD precedes any real level load, so the game's own bottom bar
        // would sit inert. We drive the SAME native bar the level-load uses: after the curtain is
        // dropped (DropCurtainEarly → SceneFadeController.DropCurtainInstant(null), which activates the
        // bar but leaves its ProgressBarController._currentLoadingProgress null → bar frozen at ~1%),
        // we assign it a fresh Base.Core.LoadingProgress and UpdateProgress(0..1) with the download
        // fraction. The controller's own Update() then eases ProgressFill toward it (native easing +
        // "NN%" text). At phase-2 the native path (OnLevelStateChanged Loading →
        // SceneFadeController.DropCurtainInstant(level) → ProgressBar.SetLoadingLevel(level)) OVERWRITES
        // _currentLoadingProgress with the level's own progress — the bar hands off with no flicker.
        // Grounded: decompile Base.Utils.SceneFadeController.cs:70-80 / ProgressBarController.cs:46,62 /
        // Base.Core.LoadingProgress.cs (public UpdateProgress). ProgressBarController is Base.Utils
        // (reflection-only for the private field); Base.Core.LoadingProgress is referenceable directly.
        private static Base.Core.LoadingProgress _downloadProgress;
        // ONE curtain-label mechanism for every co-op phase ("Downloading save…" during transfer,
        // "Waiting for players…" at the held all-loaded barrier): the cached live
        // SceneFadeController.LoadingText + its pre-mod text. Unity-null-aware cache (a destroyed Text
        // re-resolves on next access). Restored via RestoreCurtainLabel at hand-off/reveal/abort.
        private static Text _curtainLabel;
        private static string _curtainLabelOriginal;

        /// <summary>
        /// Begin driving the native bottom bar for the download phase. Requires the curtain already
        /// dropped (so ProgressBar.gameObject is active). Assigns a fresh LoadingProgress to the live
        /// ProgressBarController's private _currentLoadingProgress and relabels the loading text.
        /// Returns false (no-op) if the native bar was not found — the top-right plaque then remains the
        /// only download indicator.
        /// </summary>
        public static bool BeginDownloadBar(string label)
        {
            try
            {
                var pbc = CaptureLiveProgressBar(); // SceneFadeController.ProgressBar component
                if (pbc == null) return false;
                _downloadProgress = new Base.Core.LoadingProgress();
                // Private ILoadingProgress field — set via reflection (same field-access style as
                // GetProgressFill). The live controller's Update() eases fillAmount toward .Progress.
                HarmonyLib.AccessTools.Field(pbc.GetType(), "_currentLoadingProgress")
                    ?.SetValue(pbc, _downloadProgress);
                _downloadProgress.UpdateProgress(0f);

                SetCurtainLabel(label); // phase label (restored at hand-off / reveal / abort)
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] BeginDownloadBar failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Drive the native bottom bar to the download fraction 0..1 (Update eases toward it).</summary>
        public static void SetDownloadBar(float fraction01)
        {
            _downloadProgress?.UpdateProgress(Mathf.Clamp01(fraction01));
        }

        /// <summary>
        /// Set the native loading-screen label (SceneFadeController.LoadingText). Captures the pre-mod
        /// text ONCE (until RestoreCurtainLabel) so the native string always comes back. Safe per-frame:
        /// uGUI's Text.text setter early-outs on an identical string. No-op if the label isn't found.
        /// </summary>
        public static void SetCurtainLabel(string label)
        {
            if (label == null) return;
            if (_curtainLabel == null) _curtainLabel = GetSceneLoadingText(); // Unity-null: re-resolve
            if (_curtainLabel == null) return;
            if (_curtainLabelOriginal == null) _curtainLabelOriginal = _curtainLabel.text;
            _curtainLabel.text = label;
        }

        /// <summary>Restore the native loading label captured by the first SetCurtainLabel. Idempotent.</summary>
        public static void RestoreCurtainLabel()
        {
            if (_curtainLabelOriginal != null && _curtainLabel != null)
                _curtainLabel.text = _curtainLabelOriginal;
            _curtainLabelOriginal = null;
        }

        /// <summary>
        /// Stop driving the download bar and restore the native loading label. Does NOT lift the curtain
        /// (the level load continues under it) and does NOT null the bar's source — the native level-load
        /// path reassigns it via SetLoadingLevel. Only clears OUR references + restores the label text.
        /// </summary>
        public static void EndDownloadBar()
        {
            RestoreCurtainLabel();
            _downloadProgress = null;
        }

        // The live SceneFadeController.LoadingText (native loading-screen label), or null. Resolved by
        // name — the mod assembly cannot strong-reference Base.Utils.SceneFadeController.
        private static Text GetSceneLoadingText()
        {
            try
            {
                var sfcType = HarmonyLib.AccessTools.TypeByName("Base.Utils.SceneFadeController");
                if (sfcType == null) return null;
                var sfc = UnityEngine.Object.FindObjectOfType(sfcType);
                if (sfc == null) return null;
                return HarmonyLib.AccessTools.Field(sfcType, "LoadingText").GetValue(sfc) as Text;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] GetSceneLoadingText failed: " + e.Message);
                return null;
            }
        }

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
                Debug.LogError("[Multiplayer] TryGetPanelBackgroundSprite failed: " + e.Message);
                return null;
            }
        }
    }
}
