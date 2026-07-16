using System;
using System.Linq;
using Base.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// SINGLE source of style for every hand-rolled Multiplayer UI surface (lobby, save-picker,
    /// status bar, network menu). Centralises the whole skin so the user can re-tune the entire UI
    /// from ONE place — the most important knob being <see cref="UiScale"/>.
    ///
    /// Native-capture-first: at first use it tries to read the game's OWN font + colors + panel
    /// sprite off live native widgets, so the lobby matches the menu's look exactly. If any capture
    /// fails it falls back to the TFTV "HAVEN RECRUITS" navy+amber hexes (HavenRecruitsMain.cs:166-176).
    /// Capture is lazy + idempotent + fully guarded — a missing native source never breaks the UI,
    /// it just keeps the fallback for that one value.
    ///
    /// Sizes: each element has a BASE size (in 1920×1080 reference px); the effective on-screen size
    /// is base × <see cref="UiScale"/>, exposed via the Scaled* int helpers. Bumping UiScale (or one
    /// base size) re-scales the whole UI proportionally — no scattered literals anywhere else.
    /// </summary>
    internal static class LobbyTheme
    {
        // ─── THE knob ───────────────────────────────────────────────────────
        // Global multiplier on every font size / row height / padding / border. ~1.4 makes the
        // lobby comfortably readable on a big screen. THIS is the one value to tune in-game first.
        public const float UiScale = 1.4f;

        // ─── Base sizes (1920×1080 reference px, BEFORE UiScale) ────────────
        public const int HeaderFontSizeBase = 28;   // page / panel titles ("CO-OP LOBBY")
        public const int BodyFontSizeBase = 20;     // section headers, primary body text
        public const int RowFontSizeBase = 18;      // roster / save / chat rows
        public const int SubFontSizeBase = 15;      // secondary / subtitle / date lines
        public const int ButtonFontSizeBase = 16;   // from-code button labels
        public const int RowHeightBase = 44;        // roster row height
        public const int SaveRowHeightBase = 84;    // save-picker row (preview + 3 lines)
        public const int PaddingBase = 16;          // panel inner padding
        public const int BorderThicknessBase = 2;   // Outline frame thickness
        public const int ClonedButtonFontCapBase = 22;   // cap on cloned native menu-button labels

        // Structured roster row (A) + sectioned rail (C) base sizes.
        public const int RosterNameWidthBase = 150;     // fixed name-column width so all rows align
        public const int RosterStatusWidthBase = 96;    // fixed status-column width (right side, before pencil)
        public const int IconButtonSizeBase = 34;       // square mini-button (rename pencil)
        public const int SectionHeaderFontSizeBase = 17; // rail section header ("SHARE"/"SAVE"/"JOIN")
        public const int SeparatorThicknessBase = 2;     // thin section-divider line height

        // ─── Scaled effective sizes (base × UiScale) ────────────────────────
        public static int ScaledHeaderFontSize => Scale(HeaderFontSizeBase);
        public static int ScaledBodyFontSize => Scale(BodyFontSizeBase);
        public static int ScaledRowFontSize => Scale(RowFontSizeBase);
        public static int ScaledSubFontSize => Scale(SubFontSizeBase);
        public static int ScaledButtonFontSize => Scale(ButtonFontSizeBase);
        public static int ScaledRowHeight => Scale(RowHeightBase);
        public static int ScaledSaveRowHeight => Scale(SaveRowHeightBase);
        public static int ScaledPadding => Scale(PaddingBase);
        public static int ScaledBorderThickness => Mathf.Max(1, Scale(BorderThicknessBase));
        public static int ScaledClonedButtonFontCap => Scale(ClonedButtonFontCapBase);

        public static int ScaledRosterNameWidth => Scale(RosterNameWidthBase);
        public static int ScaledRosterStatusWidth => Scale(RosterStatusWidthBase);
        public static int ScaledIconButtonSize => Scale(IconButtonSizeBase);
        public static int ScaledSectionHeaderFontSize => Scale(SectionHeaderFontSizeBase);
        public static int ScaledSeparatorThickness => Mathf.Max(1, Scale(SeparatorThicknessBase));

        /// <summary>base × UiScale, rounded to an int (min 1). The one place sizing is computed.</summary>
        public static int Scale(int baseValue) => Mathf.Max(1, Mathf.RoundToInt(baseValue * UiScale));

        // ─── Palette (FALLBACK = TFTV HAVEN RECRUITS hexes; overwritten by capture) ──
        // Navy + amber. Captured natives replace PanelFill / PanelBorder / BodyText / Accent etc.
        // where a live source is found; the rest stay these grounded TFTV values.
        private static Color _headerBg = Hex(0x16, 0x22, 0x2a, 1.00f);     // header/title bar fill
        private static Color _headerBorder = Hex(0x22, 0x2e, 0x40, 1.00f); // header border
        private static Color _accent = Hex(0xff, 0xb3, 0x39, 1.00f);       // amber highlight
        private static Color _cardBg = Hex(0x1e, 0x20, 0x26, 1.00f);       // row/card body
        private static Color _cardBorder = Hex(0x23, 0x30, 0x44, 1.00f);   // row/card border
        private static Color _panelFill = Hex(0x0a, 0x0e, 0x16, 0.92f);    // panel backing (navy, mostly opaque)
        private static Color _panelBorder = Hex(0x22, 0x2e, 0x40, 0.95f);  // panel frame outline
        private static Color _inputBg = Hex(0x12, 0x16, 0x20, 1.00f);      // input-field backing
        private static Color _bodyText = Color.white;                       // primary text
        private static Color _subText = new Color(0.75f, 0.80f, 0.90f, 1f); // secondary text
        private static Color _pageBackdrop = new Color(0f, 0f, 0f, 0.95f);  // full-screen page base
        private static readonly Color _readyText = new Color(0.50f, 0.90f, 0.50f, 1f);  // status READY = green
        private static readonly Color _mutedText = new Color(0.55f, 0.60f, 0.68f, 1f);  // status "not ready" = dim
        private static readonly Color _separator = new Color(0.20f, 0.27f, 0.38f, 0.85f); // thin section divider

        // ─── Public palette accessors (ensure capture first) ────────────────
        public static Color HeaderBackground { get { EnsureCaptured(); return _headerBg; } }
        public static Color HeaderBorder { get { EnsureCaptured(); return _headerBorder; } }
        public static Color Accent { get { EnsureCaptured(); return _accent; } }
        public static Color CardBackground { get { EnsureCaptured(); return _cardBg; } }
        public static Color CardBorder { get { EnsureCaptured(); return _cardBorder; } }
        public static Color PanelFill { get { EnsureCaptured(); return _panelFill; } }
        public static Color PanelBorder { get { EnsureCaptured(); return _panelBorder; } }
        public static Color InputBackground { get { EnsureCaptured(); return _inputBg; } }
        public static Color BodyText { get { EnsureCaptured(); return _bodyText; } }
        public static Color SubText { get { EnsureCaptured(); return _subText; } }
        public static Color PageBackdrop { get { EnsureCaptured(); return _pageBackdrop; } }
        // Status semantics (roster rows): READY = green, "not ready" = dim/muted, host = Accent (amber).
        public static Color ReadyText => _readyText;
        public static Color MutedText => _mutedText;
        // Thin divider line between rail sections.
        public static Color Separator => _separator;

        /// <summary>The captured native menu font (Purista Semibold), or Arial fallback.</summary>
        public static Font Font => NativeWidgetFactory.MenuFont ?? UiToolkit.DefaultFont;

        // ─── Native panel sprite (sliced frame), captured lazily ────────────
        private static Sprite _panelSprite;
        private static bool _panelSpriteResolved;

        /// <summary>
        /// A native sliced panel sprite to skin panel backgrounds (so they use the game's own framed
        /// look). Null if none captured → caller uses a themed flat fill + Outline. Resolved once.
        /// </summary>
        public static Sprite PanelSprite
        {
            get
            {
                if (!_panelSpriteResolved)
                {
                    _panelSpriteResolved = true;
                    try { _panelSprite = NativeWidgetFactory.TryGetPanelBackgroundSprite(); }
                    catch (Exception e) { Debug.LogError("[Multiplayer] LobbyTheme panel sprite capture failed: " + e.Message); }
                }
                return _panelSprite;
            }
        }

        // ─── Native-color capture ───────────────────────────────────────────
        private static bool _captured;

        /// <summary>
        /// Read native colors + text style off live game widgets, ONCE. Tries:
        ///   • a live <see cref="UIColorController"/> whose ColorSetup carries Primary/Secondary/Warning
        ///     UIColors (Base.UI\UIColorController.cs) → drives BodyText (primary) + Accent (warning) +
        ///     SubText (secondary);
        ///   • a live <see cref="SnapTextPropertiesDef"/> (off any SnapshotText) → drives BodyText color.
        /// Every step is independently guarded: a failure leaves that value at its TFTV fallback. Safe
        /// to call from any property getter; the flag flips true on the first attempt regardless of how
        /// many sub-captures succeed (we never thrash the scene every frame).
        /// </summary>
        public static void EnsureCaptured()
        {
            if (_captured) return;
            _captured = true;

            try
            {
                CaptureFromColorControllers();
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] LobbyTheme color capture failed: " + e.Message);
            }
        }

        private static void CaptureFromColorControllers()
        {
            // Find a live UIColorController whose fallback ColorSetup triple is populated (most menu
            // text controllers use a single PrimaryColorDef instead — those carry no triple, so we
            // scan for one that has the full ColorSetup).
            var controllers = Resources.FindObjectsOfTypeAll<UIColorController>();
            if (controllers != null)
            {
                var withSetup = controllers.FirstOrDefault(c => c != null && c.ColorSetup != null);
                if (withSetup != null)
                {
                    var setup = withSetup.ColorSetup;
                    // Primary = main readable text/icon color; keep it as the body text color.
                    _bodyText = setup.PrimaryUIColor;
                    // Secondary = dimmer companion color → subtitles / secondary lines.
                    _subText = setup.SecondaryUIColor;
                    // Warning = the game's amber/orange highlight → our accent.
                    _accent = setup.WarningUIColor;
                }
                else
                {
                    // No full triple anywhere — fall back to single PrimaryColorDef where present.
                    var withPrimary = controllers.FirstOrDefault(c => c != null && c.PrimaryColorDef != null);
                    if (withPrimary != null)
                        _bodyText = withPrimary.PrimaryColorDef.Color;
                }
            }
        }

        // ─── Responsive aspect-adaptive scaler rule (native PP behaviour) ──
        // Native CanvasScalerController sets CanvasScaler.matchWidthOrHeight by aspect, threshold 16:9:
        // match WIDTH (0) at ≤16:9 (16:10, 4:3 — wider-than-tall content fits the width), match HEIGHT
        // (1) above 16:9 (21:9 ultrawide — keep height fixed so content doesn't blow up sideways).
        private const float Sixteen9 = 16f / 9f;

        /// <summary>The native aspect-adaptive matchWidthOrHeight for the current screen.</summary>
        public static float CurrentMatch
        {
            get
            {
                float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : Sixteen9;
                return aspect > Sixteen9 ? 1f : 0f;
            }
        }

        /// <summary>
        /// Configure a CanvasScaler with the native ScaleWithScreenSize @1920×1080 + aspect-adaptive
        /// match rule (replaces the old fixed match=0.5). Call from each of our root canvases.
        /// </summary>
        public static void ConfigureScaler(CanvasScaler scaler)
        {
            if (scaler == null) return;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = CurrentMatch;
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        /// <summary>Apply a native sliced panel sprite (or flat fill) + themed Outline to a panel Image.</summary>
        public static void ApplyPanelSkin(Image img, Outline outline, Color fill, Color border)
        {
            if (img != null)
            {
                var sprite = PanelSprite;
                if (sprite != null)
                {
                    img.sprite = sprite;
                    img.type = Image.Type.Sliced;
                }
                img.color = fill;
            }
            if (outline != null)
            {
                outline.effectColor = border;
                var d = ScaledBorderThickness;
                outline.effectDistance = new Vector2(d, d);
            }
        }

        private static Color Hex(int r, int g, int b, float a)
            => new Color(r / 255f, g / 255f, b / 255f, a);
    }
}
