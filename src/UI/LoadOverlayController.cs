using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using UnityEngine;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Co-op loading overlay: a mod-owned ScreenSpaceOverlay canvas (sortingOrder 7000) drawn over
    /// the vanilla curtain. One row per slot (name + single bar + phase label + % text). Drives this
    /// peer's phase-2 native-load read and reports it to the host each frame (throttled).
    /// </summary>
    public sealed class LoadOverlayController : MonoBehaviour
    {
        private Canvas _canvas;
        private Transform _root;
        private bool _visible;

        // Cached clone template of the native loading bar (Base.Utils.ProgressBarController's
        // gameObject), captured lazily on first Show — by then DropCurtainEarly() has run and the
        // curtain (hence the bar) exists. Null after a failed capture → rows fall back to from-code.
        private GameObject _barTemplate;
        private bool _barTemplateTried;

        private sealed class Row
        {
            public Text Name;
            // Native path: a clone of the vanilla ProgressBarController bar. NativeFill is its
            // (disabled-controller) ProgressFill Image we drive directly; NativePct its ProgressText.
            public GameObject BarGo;
            public Image NativeFill;
            public Text NativePct;
            public bool Native;
            // From-code fallback path (used only when the native template was not captured).
            public Image Fill;
            public Text Label;   // "Downloading" / "Loading"
            public Text Percent;
            // Eased DISPLAY fill (0..1). The native LoadingProgress source is coarse/step-quantized,
            // so we animate this toward the raw target each frame (FillEase) instead of snapping —
            // turns 0/40/69/80 into a continuously climbing bar. Reset to 0 on (re)build + on Show().
            public float DisplayFill;
        }

        // Per-second catch-up speed of the eased bar fill (fraction of the full bar / sec). A full
        // 0→1 sweep takes ~1/RATE s of catch-up; on a coarse step the bar ramps smoothly instead of
        // snapping, and during the ~4.5s plateau (once caught up) it simply holds at the real target.
        private const float FillEaseRate = 0.6f;
        private readonly Dictionary<byte, Row> _rows = new Dictionary<byte, Row>();

        private void EnsureCanvas()
        {
            if (_canvas != null) return;
            var go = new GameObject("MultipleerLoadOverlay");
            go.transform.SetParent(transform, false); // under ModGO (persistent root)

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 7000; // above the native curtain (confirm in-game, open item #1)

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            // Display-only: no GraphicRaycaster.
            // NOTE: the overlay is TOP-RIGHT ONLY. It must NEVER add a fullscreen opaque cover —
            // a previous fullscreen-black "Cover" blacked out the native loading screen on the host
            // (regression). The native loading screen stays visible during load; this overlay only
            // adds the per-player roster panel in the top-right corner.

            var panel = new GameObject("Panel");
            panel.transform.SetParent(go.transform, false);
            var prt = panel.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(1f, 1f); // top-right
            prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-24f, -24f);
            prt.sizeDelta = new Vector2(460f, 270f); // ~quarter screen
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6f;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);

            _root = panel.transform;

            // Capture the native loading-bar clone template ONCE, now that the canvas/panel exist.
            // By the time the overlay first shows, MultiplayerUI.DropCurtainEarly() has run, so the
            // curtain (and its ProgressBarController bar) is present. A null result (capture failed)
            // makes every BuildRow fall back to the from-code row.
            if (!_barTemplateTried)
            {
                _barTemplateTried = true;
                _barTemplate = NativeWidgetFactory.CaptureLoadingBarTemplate();
            }
        }

        private Row BuildRow(byte slot, string name)
        {
            // Compact row container (one name label + one bar). Height ~30px so several fit the
            // ~270px-tall top-right panel.
            var go = new GameObject("Row" + slot);
            go.transform.SetParent(_root, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, 30f);
            var rowLe = go.AddComponent<LayoutElement>();
            rowLe.minHeight = 30f;

            // Name label (top half). Builtin Arial is fine; the captured menu font is optional polish.
            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(go.transform, false);
            var nameTxt = nameGo.AddComponent<Text>();
            nameTxt.font = NativeWidgetFactory.MenuFont != null
                ? NativeWidgetFactory.MenuFont
                : Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameTxt.fontSize = 16;
            nameTxt.color = Color.white;
            nameTxt.text = name;
            var nrt = nameTxt.rectTransform;
            nrt.anchorMin = new Vector2(0f, 0.55f); nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(6f, 0f); nrt.offsetMax = new Vector2(-6f, 0f);

            // ── NATIVE PATH: clone the vanilla loading bar, disable its self-driving Update(), and
            //    drive its ProgressFill manually. Falls through to the from-code path if anything
            //    needed is missing (null template or null ProgressFill on the clone).
            if (_barTemplate != null)
            {
                var bar = NativeWidgetFactory.CloneLoadingBar(_barTemplate, go.transform);
                if (bar != null)
                {
                    var pbc = NativeWidgetFactory.GetProgressBarController(bar);
                    NativeWidgetFactory.DisableController(pbc);
                    var nativeFill = NativeWidgetFactory.GetProgressFill(pbc);
                    if (nativeFill != null)
                    {
                        var nativePct = NativeWidgetFactory.GetProgressText(pbc);

                        // Constrain the cloned bar to the bottom strip of the row: stretch full width,
                        // compact height. The native bar may carry its own RectTransform size, so we
                        // explicitly anchor-stretch it and add a LayoutElement so the VerticalLayoutGroup
                        // keeps it inside the ~460px-wide panel.
                        var brt = bar.GetComponent<RectTransform>();
                        if (brt != null)
                        {
                            brt.anchorMin = new Vector2(0f, 0f);
                            brt.anchorMax = new Vector2(1f, 0.5f);
                            brt.offsetMin = new Vector2(6f, 2f);
                            brt.offsetMax = new Vector2(-6f, -1f);
                            brt.sizeDelta = new Vector2(brt.sizeDelta.x < 0 ? brt.sizeDelta.x : 0f, 0f);
                        }
                        var barLe = bar.GetComponent<LayoutElement>();
                        if (barLe == null) barLe = bar.AddComponent<LayoutElement>();
                        barLe.preferredHeight = 24f;
                        barLe.flexibleWidth = 1f;

                        nativeFill.fillAmount = 0f;
                        return new Row
                        {
                            Name = nameTxt,
                            BarGo = bar,
                            NativeFill = nativeFill,
                            NativePct = nativePct,
                            Native = true,
                        };
                    }

                    // ProgressFill missing — discard the clone and fall back below.
                    UnityEngine.Object.Destroy(bar);
                }
            }

            // ── FALLBACK PATH (no native template): the original from-code bar (BarBg + Fill +
            //    Label + Percent), unchanged behavior.
            var barBg = new GameObject("BarBg");
            barBg.transform.SetParent(go.transform, false);
            var barBgImg = barBg.AddComponent<Image>();
            barBgImg.color = new Color(1f, 1f, 1f, 0.15f);
            var brt2 = barBgImg.rectTransform;
            brt2.anchorMin = new Vector2(0f, 0f); brt2.anchorMax = new Vector2(1f, 0.5f);
            brt2.offsetMin = new Vector2(6f, 2f); brt2.offsetMax = new Vector2(-6f, -1f);

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(barBg.transform, false);
            var fill = fillGo.AddComponent<Image>();
            fill.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            var frt = fill.rectTransform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(barBg.transform, false);
            var label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = 14;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = Color.white;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = Vector2.zero;

            var pctGo = new GameObject("Pct");
            pctGo.transform.SetParent(barBg.transform, false);
            var pct = pctGo.AddComponent<Text>();
            pct.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            pct.fontSize = 14;
            pct.alignment = TextAnchor.MiddleRight;
            pct.color = Color.white;
            var prt2 = pct.rectTransform;
            prt2.anchorMin = Vector2.zero; prt2.anchorMax = Vector2.one;
            prt2.offsetMin = Vector2.zero; prt2.offsetMax = new Vector2(-6f, 0f);

            return new Row { Name = nameTxt, Fill = fill, Label = label, Percent = pct, Native = false };
        }

        public void Show()
        {
            EnsureCanvas();
            // Rows persist across sessions (_rows is never cleared; Hide() only deactivates the canvas).
            // Reset each row's eased fill + visible bar/text to 0 so a 2nd co-op load animates from 0
            // instead of starting at the prior run's filled state.
            foreach (var row in _rows.Values)
            {
                row.DisplayFill = 0f;
                if (row.NativeFill != null) row.NativeFill.fillAmount = 0f;
                if (row.NativePct != null) row.NativePct.text = "0%";
                if (row.Fill != null) row.Fill.fillAmount = 0f;
                if (row.Percent != null) row.Percent.text = "0%";
            }
            _canvas.gameObject.SetActive(true);
            _visible = true;
        }

        public void Hide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _visible = false;
        }

        private void Update()
        {
            // UI refresh only. The phase-2 native-load driver (progress read + done detection) moved
            // to SaveTransferCoordinator.Update() so it runs on every peer regardless of overlay
            // visibility — see RosterProgressTracker.InPhase2 / SaveTransferCoordinator pump.
            if (!_visible) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || engine.SaveTransfer == null || engine.Session == null) return;
            Refresh(engine);
        }

        private void Refresh(NetworkEngine engine)
        {
            var tracker = engine.SaveTransfer.Tracker;
            // Build a row for every OTHER slot in the roster — HIDE the local player's own row (the
            // overlay shows who ELSE is still loading, not yourself). The tracker holds merged
            // per-slot progress for all remote slots.
            foreach (var p in engine.Session.GetLobbyRoster())
            {
                if (p.SlotIndex == engine.Session.LocalSlotIndex) continue; // skip self
                // Empty nickname → fallback label so the bar still says WHO it is.
                var label = string.IsNullOrEmpty(p.Nickname) ? "Player " + p.SlotIndex : p.Nickname;
                if (!_rows.TryGetValue(p.SlotIndex, out var row))
                {
                    EnsureCanvas();
                    row = BuildRow(p.SlotIndex, label);
                    _rows[p.SlotIndex] = row;
                }
                row.Name.text = label;
                var (phase, percent) = tracker.Get(p.SlotIndex);

                // The native LoadingProgress source is coarse/step-quantized (pct 0→40→69→80→done),
                // so rendering it raw snaps + plateaus. Ease the DISPLAYED fill toward the raw target
                // a bounded step per frame (monotonic-up, clamped 0..1) so the bar climbs continuously
                // and steps glide instead of jumping. Show the eased % so the number tracks the bar.
                var target = percent / 100f;
                row.DisplayFill = FillEase.EaseFill(row.DisplayFill, target, FillEaseRate * Time.deltaTime);
                var displayPct = Mathf.RoundToInt(row.DisplayFill * 100f);

                if (row.Native && row.NativeFill != null)
                {
                    // Native cloned bar (its own Update() disabled): drive ProgressFill directly.
                    row.NativeFill.fillAmount = row.DisplayFill;
                    if (row.NativePct != null) row.NativePct.text = displayPct + "%";
                }
                else if (!row.Native && row.Fill != null)
                {
                    // From-code fallback row. Guarded on !Native so a native row whose cloned
                    // ProgressFill was destroyed at runtime is skipped, not NRE'd (its Fill is null).
                    row.Fill.fillAmount = row.DisplayFill;
                    row.Percent.text = displayPct + "%";
                    row.Label.text = phase == 0 ? "Downloading" : "Loading";
                }
            }
        }
    }
}
