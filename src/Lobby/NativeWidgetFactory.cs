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
                    MpLog.LogError("[Multiplayer] CaptureFromMainMenu (menu font) failed: " + e.Message);
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
                    if (ReferenceEquals(c, lobbyCanvas)) continue;          // never disable the lobby's own canvas (L113: identity, not liveness)
                    if (modRoot != null && c.transform.IsChildOf(modRoot))  // never disable any Multiplayer canvas
                        continue;                                           // (status bar lives under the same ModGO)

                    _hiddenCanvases.Add(new HiddenCanvas { Canvas = c, WasEnabled = c.enabled });
                    c.enabled = false;
                }
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] HideMenuChrome (root-canvas sweep) failed: " + e.Message);
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
        //  Native loading bar — Base.Utils.ProgressBarController (the LIVE one, not a clone)
        // ═══════════════════════════════════════════════════════════════════
        //
        // The vanilla loading-screen bottom bar is Base.Utils.ProgressBarController (MonoBehaviour):
        //   - public Image ProgressFill;  (type=Filled; set .fillAmount 0..1 to drive it)
        //   - public Text  ProgressText;  (the "NN%" text)
        // Its own Update() ramps ProgressFill.fillAmount toward _currentLoadingProgress?.Progress and
        // NEVER lowers it — which is why ResetBarFill below writes the Image directly. It is reached at
        // runtime via Base.Utils.SceneFadeController's public ProgressBar field. The mod assembly cannot
        // strong-reference these Base.Utils types, so we resolve them by name (AccessTools.TypeByName /
        // AccessTools.Field) and operate on the UnityEngine base types. The curtain (hence the bar) is
        // guaranteed present after MultiplayerUI.DropCurtainEarly().
        //
        // The bar-CLONE helpers that used to live here (template capture, CloneLoadingBar,
        // GetProgressBarController, GetProgressText, DisableController) are gone with their only caller:
        // the co-op load panel now draws its per-peer rows in the PlayerPanel plaque's style instead of
        // instantiating a prefab subtree per row. This bar remains THIS peer's own load display.

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
                MpLog.LogError("[Multiplayer] CaptureLiveProgressBar failed: " + e.Message);
                result = null;
            }
            MpLog.Log("[Multiplayer] live progress bar " + (result != null ? "captured" : "NOT found"));
            return result;
        }

        /// <summary>
        /// The sorting order of the canvas the native loading curtain draws on, or null if it cannot be
        /// resolved. The curtain's order is SERIALIZED PREFAB DATA — nothing in the decompiled code
        /// (SceneFadeController / LevelSwitchCurtainController) ever assigns a sortingOrder — so it can
        /// only be read at runtime. Reached from the same anchor as the bar itself
        /// (SceneFadeController.ProgressBar → its root Canvas).
        /// </summary>
        public static int? CurtainCanvasOrder()
        {
            try
            {
                var pbc = CaptureLiveProgressBar();
                var c = pbc != null ? pbc.GetComponentInParent<Canvas>() : null;
                if (c == null) return null;
                var root = c.rootCanvas != null ? c.rootCanvas : c;
                return root.sortingOrder;
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] CurtainCanvasOrder failed: " + e.Message);
                return null;
            }
        }

        /// <summary>The ProgressBarController's ProgressFill Image (the Filled bar we drive), or null.</summary>
        public static Image GetProgressFill(Component pbc)
        {
            if (pbc == null) return null;
            var f = HarmonyLib.AccessTools.Field(pbc.GetType(), "ProgressFill");
            return f?.GetValue(pbc) as Image;
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
        // The live ProgressBarController we hijacked, plus the ILoadingProgress it was reading BEFORE.
        // BeginDownloadBar overwrites a PRIVATE field of a live game component; until this pair existed
        // nothing ever put it back, so once our window closed the game's bar was permanently wired to an
        // abandoned LoadingProgress pinned at its last value and the native controller could never move
        // it again. Restored by RestoreBarSource (only when the bar is still ours — see there).
        private static Component _barOwner;
        private static object _barSourceBefore;
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
                // Private ILoadingProgress field — set via reflection (same field-access style as
                // GetProgressFill). The live controller's Update() eases fillAmount toward .Progress.
                var field = HarmonyLib.AccessTools.Field(pbc.GetType(), "_currentLoadingProgress");
                // Remember what the bar was reading before the FIRST hijack of this window, so
                // EndDownloadBar can hand it back. Re-entrant Begin must not record our own object.
                if (_barOwner == null)
                {
                    _barOwner = pbc;
                    _barSourceBefore = field?.GetValue(pbc);
                }
                _downloadProgress = new Base.Core.LoadingProgress();
                field?.SetValue(pbc, _downloadProgress);
                _downloadProgress.UpdateProgress(0f);

                SetCurtainLabel(label); // phase label (restored at hand-off / reveal / abort)
                return true;
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] BeginDownloadBar failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Drive the native bottom bar to the download fraction 0..1 (Update eases toward it).</summary>
        public static void SetDownloadBar(float fraction01)
        {
            _downloadProgress?.UpdateProgress(Mathf.Clamp01(fraction01));
        }

        /// <summary>
        /// Force the live native bar back to EMPTY. The controller is monotone — its Update only ever
        /// raises fillAmount (decompile Base.Utils/ProgressBarController.cs:71,
        /// <c>ProgressFill.fillAmount &lt; num</c>) — so a new phase that starts at 0% inherits the
        /// previous phase's fill and stands still until it is passed. Lowering the SOURCE
        /// (<see cref="SetDownloadBar"/>, <see cref="BeginDownloadBar"/>) cannot do this; only writing the
        /// Image can. Used at the mirrored-host-load → own-download edge. The next edge needs nothing:
        /// the game's own <c>ProgressBarController.SetLoadingLevel</c>:52 writes fillAmount itself.
        /// </summary>
        public static void ResetBarFill()
        {
            var fill = GetProgressFill(CaptureLiveProgressBar());
            if (fill == null)
            {
                MpLog.LogWarning("[Multiplayer] ResetBarFill: no live ProgressFill — the bar keeps the " +
                                 "previous phase's fill (native controller never lowers it).");
                return;
            }
            fill.fillAmount = 0f;
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
        /// Stop driving the download bar, restore the native loading label AND hand the bar's source back
        /// to whatever the game had it on. Does NOT lift the curtain (the level load continues under it).
        /// </summary>
        public static void EndDownloadBar()
        {
            RestoreCurtainLabel();
            RestoreBarSource();
            _downloadProgress = null;
        }

        /// <summary>
        /// Put the hijacked ProgressBarController back on its original ILoadingProgress — but ONLY while
        /// the field still holds OUR object. The native level-load path (SceneFadeController
        /// .DropCurtainInstant(level) → ProgressBar.SetLoadingLevel) reassigns that same field and runs
        /// BEFORE our download→level-load hand-off, so an unconditional restore would clobber the live
        /// level's progress with a stale source and freeze the real load's bar. Idempotent; Unity-null
        /// aware (a destroyed controller simply re-arms on the next BeginDownloadBar).
        /// </summary>
        private static void RestoreBarSource()
        {
            if (_barOwner == null) { _barSourceBefore = null; return; }
            try
            {
                var f = HarmonyLib.AccessTools.Field(_barOwner.GetType(), "_currentLoadingProgress");
                if (f != null && ReferenceEquals(f.GetValue(_barOwner), _downloadProgress))
                    f.SetValue(_barOwner, _barSourceBefore);
            }
            catch (Exception e)
            {
                MpLog.LogError("[Multiplayer] RestoreBarSource failed: " + e.Message);
            }
            _barOwner = null;
            _barSourceBefore = null;
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
                MpLog.LogError("[Multiplayer] GetSceneLoadingText failed: " + e.Message);
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
                MpLog.LogError("[Multiplayer] TryGetPanelBackgroundSprite failed: " + e.Message);
                return null;
            }
        }
    }
}
