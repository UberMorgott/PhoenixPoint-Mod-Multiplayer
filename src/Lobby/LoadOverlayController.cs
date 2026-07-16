using System.Collections.Generic;
using Base.Core;
using Base.Levels;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
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
        }

        private readonly Dictionary<byte, Row> _rows = new Dictionary<byte, Row>();

        private void EnsureCanvas()
        {
            if (_canvas != null) return;
            var go = new GameObject("MultiplayerLoadOverlay");
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

        // Stretch a RectTransform to fully fill its parent (anchors span, zero offsets).
        private static void ForceStretchFill(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Force every RectTransform from <paramref name="leaf"/> UP TO AND INCLUDING the child that is
        // a direct child of <paramref name="stopRoot"/> to stretch-fill its parent. Used so a cloned
        // native bar's track + fill (whatever the nesting) span the right cell, instead of keeping the
        // prefab's serialized (thin/top-right) layout. Stops at stopRoot's direct child (the bar root
        // itself is anchored into the cell separately by the caller).
        private static void ForceStretchFillChain(RectTransform leaf, Transform stopRoot)
        {
            var rt = leaf;
            int guard = 0;
            while (rt != null && rt.transform != stopRoot && guard++ < 16)
            {
                ForceStretchFill(rt);
                rt = rt.parent as RectTransform;
            }
        }

        private Row BuildRow(byte slot, string name)
        {
            // Compact row container laid out as ONE horizontal line via two explicit anchored cells:
            // NICKNAME in the LEFT half, progress bar in the RIGHT half. Height ~36px; several rows
            // still fit the ~270px-tall top-right panel. Explicit sub-rects (not a layout group) keep
            // the two cells from ever overlapping by construction.
            var go = new GameObject("Row" + slot);
            go.transform.SetParent(_root, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, 36f);
            var rowLe = go.AddComponent<LayoutElement>();
            rowLe.minHeight = 36f;

            // Name label (top strip). Default to the captured native menu font (a real PP font);
            // the NATIVE-clone branch below upgrades this to the native loading-screen font read
            // off the cloned bar's ProgressText. Fallback chain always yields a non-null Font.
            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(go.transform, false);
            var nameTxt = nameGo.AddComponent<Text>();
            nameTxt.font = NativeWidgetFactory.MenuFont
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameTxt.fontSize = 20;
            nameTxt.color = Color.white;
            nameTxt.alignment = TextAnchor.MiddleLeft; // vertically centered, left-aligned in its cell
            nameTxt.horizontalOverflow = HorizontalWrapMode.Overflow;
            nameTxt.text = name;
            // Name occupies the LEFT cell (left ~half of the row), full row height so a 20px glyph
            // sits centered without clipping. The bar lives in the RIGHT cell (0.5..1) — no overlap.
            var nrt = nameTxt.rectTransform;
            nrt.anchorMin = new Vector2(0f, 0f); nrt.anchorMax = new Vector2(0.5f, 1f);
            nrt.offsetMin = new Vector2(6f, 0f); nrt.offsetMax = new Vector2(-2f, 0f);

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

                        // The cloned native ProgressText carries the native PP loading-screen font
                        // (same legacy Text+Font family as the geoscape bottom nav bar). Capture it
                        // and reuse it for our nickname label so the label matches the native look.
                        // Priority: native loading font → captured menu font → builtin Arial.
                        var nativeFont = nativePct != null ? nativePct.font : null;
                        nameTxt.font = nativeFont
                            ?? NativeWidgetFactory.MenuFont
                            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                        // Keep the native ProgressText's own font as-is (it IS the native component);
                        // just ensure its size renders readable in our shrunk clone.
                        if (nativePct != null && nativePct.fontSize < 16) nativePct.fontSize = 16;

                        // Place the cloned bar ROOT into the RIGHT cell (0.5..1 of the row), full row
                        // height with small margins. CloneLoadingBar clones the ENTIRE native subtree
                        // and the native children keep their own serialized anchors, so re-anchoring
                        // ONLY the root is not enough — the bar would render at its own size/position.
                        // ROBUST (2b-lite): after re-anchoring the root, force every RectTransform from
                        // the ProgressFill Image UP to the bar root to stretch-fill its parent, so the
                        // track + fill are guaranteed to span the right cell regardless of the prefab's
                        // serialized layout.
                        var brt = bar.GetComponent<RectTransform>();
                        if (brt != null)
                        {
                            brt.anchorMin = new Vector2(0.5f, 0f);
                            brt.anchorMax = new Vector2(1f, 1f);
                            brt.offsetMin = new Vector2(2f, 4f);
                            brt.offsetMax = new Vector2(-6f, -4f);
                        }
                        // Stretch-fill the whole chain root → … → ProgressFill so the bar fills the cell.
                        ForceStretchFillChain(nativeFill.rectTransform, bar.transform);
                        // Stretch the % text too (if present) so it centers inside the bar, not off-cell.
                        if (nativePct != null) ForceStretchFill(nativePct.rectTransform);

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
            // RIGHT cell (0.5..1), full row height — matches the native path so the from-code bar sits
            // beside the LEFT-cell Name without overlapping it.
            var brt2 = barBgImg.rectTransform;
            brt2.anchorMin = new Vector2(0.5f, 0f); brt2.anchorMax = new Vector2(1f, 1f);
            brt2.offsetMin = new Vector2(2f, 4f); brt2.offsetMax = new Vector2(-6f, -4f);

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
            label.font = NativeWidgetFactory.MenuFont
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = 14;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = Color.white;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = Vector2.zero;

            var pctGo = new GameObject("Pct");
            pctGo.transform.SetParent(barBg.transform, false);
            var pct = pctGo.AddComponent<Text>();
            pct.font = NativeWidgetFactory.MenuFont
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
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
            // IDEMPOTENT: the state-driven visibility (Update → LoadOverlayVisibility.ShouldShow) calls this
            // EVERY frame while a co-op load is in progress. Re-zeroing the bars on each call would thrash
            // the live progress back to 0% every frame, so do the one-time reset + activate only on the
            // rising edge (not already visible). Re-entrant calls while visible are a no-op.
            if (_visible) return;
            EnsureCanvas();
            // Rows persist across sessions (_rows is never cleared; Hide() only deactivates the canvas).
            // Reset each row's visible bar/text to 0 so a 2nd co-op load starts from 0 instead of the
            // prior run's filled state.
            foreach (var row in _rows.Values)
            {
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
            // IDEMPOTENT: also called every frame from the state-driven path when no load is in progress.
            if (!_visible) return;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _visible = false;
        }

        private void Update()
        {
            // STATE-DRIVEN visibility (robustness fix): the overlay no longer relies on a single fire-once
            // Harmony show that a frame-race could miss ("через раз"). Each frame we build a snapshot from
            // the live SaveTransfer coordinator + per-peer tracker and ask the pure predicate whether the
            // overlay should be up, then Show()/Hide() idempotently. This self-heals every transition path
            // and every frame-race: a missed show OR a missed hide is corrected on the very next frame.
            //
            // The phase-2 native-load driver (progress read + done detection) lives in
            // SaveTransferCoordinator.Update() and runs on every peer regardless of overlay visibility.
            var engine = NetworkEngine.Instance;
            if (engine == null || engine.SaveTransfer == null || engine.Session == null)
            {
                // No live session/coordinator → nothing can be loading; ensure the overlay is down.
                Hide();
                return;
            }

            var coord = engine.SaveTransfer;
            // Authoritative per-peer completion from the shared tracker over the participating roster slots.
            var tracker = coord.Tracker;
            int expectedPeers = 0, donePeers = 0;
            foreach (var slot in engine.Session.GetRosterSlots())
            {
                expectedPeers++;
                if (tracker.IsDone(slot)) donePeers++;
            }

            bool shouldShow = LoadOverlayVisibility.ShouldShow(
                coord.LoadPhaseStarted, coord.InPhase2, coord.IsDownloading, expectedPeers, donePeers,
                coord.HostWaitingOnPeers); // overlay fix 2026-07-13: host sees clients' progress on tac-entry

            if (shouldShow) Show(); else Hide();

            // Repaint the per-slot bars only while visible (skip self).
            if (_visible) Refresh(engine);
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

                // PHASE-AWARE fill: phase 0 (download) fills the bottom half, phase 1 (native load) the top,
                // so the instant loopback download (phase 0, 100%) shows a HALF bar and the bar only fills
                // completely when the peer is actually loaded. The raw percent still labels the current phase.
                var fillAmount = RosterProgressTracker.CombinedFill(phase, percent);
                string phaseLabel = phase == 0 ? "Downloading" : "Loading";

                if (row.Native && row.NativeFill != null)
                {
                    // Native cloned bar (its own Update() disabled): drive ProgressFill directly.
                    row.NativeFill.fillAmount = fillAmount;
                    if (row.NativePct != null) row.NativePct.text = phaseLabel + " " + percent + "%";
                }
                else if (!row.Native && row.Fill != null)
                {
                    // From-code fallback row. Guarded on !Native so a native row whose cloned
                    // ProgressFill was destroyed at runtime is skipped, not NRE'd (its Fill is null).
                    row.Fill.fillAmount = fillAmount;
                    row.Percent.text = percent + "%";
                    row.Label.text = phaseLabel;
                }
            }
        }
    }
}
