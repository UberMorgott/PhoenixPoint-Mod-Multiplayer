using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// WHO IS STILL LOADING, AND HOW FAR ALONG — the load-window twin of <see cref="PlayerPanel"/>.
    ///
    /// SAME PLATE, SAME ROW, SAME METRICS. It builds its rows out of PlayerPanel's own themed numbers
    /// (<c>Pad</c>/<c>RowH</c>/<c>NameW</c>/<c>NameFont</c>/<c>NumberFont</c>, the same
    /// <c>LobbyTheme.ApplyPanelSkin</c> at the same alphas) so the two read as ONE system rather than as
    /// two mod panels that happen to list players. The row anatomy is the plaque's, unchanged: nickname
    /// in the left cell, the per-peer readout in a cell to its RIGHT — here a progress bar carrying
    /// "Downloading/Loading" and "NN%" instead of the ping meter.
    ///
    /// WHY IT IS NOT JUST THE PLAQUE. PlayerPanel is TACTICAL ONLY and hangs off the co-op ready button
    /// (<c>TacticalReadyButton.Rect</c>, PlayerPanel.SyncCore) — during a load there is no tactical HUD
    /// and no anchor, so it is hidden in exactly the two windows this panel exists for. Same styling,
    /// different lifetime; that is the whole reason both files exist.
    ///
    /// WHY THE BAR IS DRAWN, not cloned from the game. Same reason PlayerPanel draws its ping meter: at
    /// this row height the native ProgressBarController clone brings a whole prefab subtree with its own
    /// serialized layout and reads as a different widget beside the plaque's flat cells. The game's own
    /// bar is still THE display for this peer's own load — it is the bottom bar under the curtain, driven
    /// by NativeWidgetFactory. This panel is only about the OTHER peers.
    ///
    /// WHERE IT LIVES: its own ScreenSpaceOverlay canvas under ModGO, sorted one above the native curtain
    /// (<see cref="EnsureAboveCurtain"/>). Top-right, never a fullscreen cover — a previous fullscreen
    /// black "Cover" blacked out the host's own native loading screen (regression).
    ///
    /// VISIBILITY IS STATE-DRIVEN, per frame, from <see cref="LoadOverlayVisibility.ShouldShow"/>: a
    /// missed show OR a missed hide self-heals on the next frame, and there is deliberately no show entry
    /// point for anyone to call at the wrong moment. Display only — it reads the roster and the shared
    /// progress tracker and decides nothing. No peer ever waits on a human here.
    /// </summary>
    public sealed class LoadOverlayController : MonoBehaviour
    {
        // Wide enough for the fill, the phase word on the left and "100%" on the right, all in one cell —
        // the plaque's Number-over-the-ping-cell idiom, so the row stays two cells wide like the plaque's.
        private static int BarW => LobbyTheme.Scale(110);
        private static int PanelW => PlayerPanel.Pad * 2 + PlayerPanel.NameW + PlayerPanel.Pad + BarW;

        private sealed class Row
        {
            public GameObject Go;
            public Text Name;
            public RectTransform Fill;  // width driven by anchorMax.x — see SetFill
            public Text Label;          // "Downloading" / "Loading"
            public Text Percent;
        }

        /// <summary>
        /// THE FILL IS A RECT, NOT AN <c>Image.Type.Filled</c>. The row used to set <c>fillAmount</c> on a
        /// spriteless Image and drew a flat full-width strip at every percent: UGUI's Image.OnPopulateMesh
        /// early-returns to the plain Graphic quad when <c>activeSprite == null</c>, so <c>type</c>,
        /// <c>fillMethod</c> and <c>fillAmount</c> are never read and the bar could only ever be empty or
        /// whole. PlayerPanel's own signal bars are drawn the same way and for the same reason — flat
        /// Images sized by their RectTransform, no sprite anywhere in either panel.
        /// </summary>
        private static void SetFill(RectTransform fill, float t)
        {
            var max = new Vector2(Mathf.Clamp01(t), 1f);
            if (fill.anchorMax != max) fill.anchorMax = max;
        }

        private Canvas _canvas;
        // Kept so the visible frames can re-apply the aspect-adaptive match: this canvas lives for the
        // whole process, so the match ConfigureScaler computed at build is stale after any resolution /
        // windowed change. Mirrors LobbyPanel._scaler and MultiplayerUI._barScaler.
        private CanvasScaler _scaler;
        private GameObject _plate;
        private RectTransform _plateRect;
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<PeerListEntry> _others = new List<PeerListEntry>();
        private bool _visible;

        private void EnsureCanvas()
        {
            if (_canvas != null) return;

            var go = new GameObject("MultiplayerLoadOverlay");
            go.transform.SetParent(transform, false); // under ModGO (persistent root)

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 7000; // floor; EnsureAboveCurtain() raises it to the measured curtain+1
            _scaler = go.AddComponent<CanvasScaler>();
            LobbyTheme.ConfigureScaler(_scaler);
            // Display-only: no GraphicRaycaster, and every Graphic below is raycastTarget=false. This
            // canvas sits over the loading screen and must never eat a click meant for the game.

            _plate = new GameObject("Plate");
            _plate.transform.SetParent(go.transform, false);
            _plateRect = _plate.AddComponent<RectTransform>();
            _plateRect.anchorMin = _plateRect.anchorMax = _plateRect.pivot = new Vector2(1f, 1f); // top-right
            _plateRect.anchoredPosition = new Vector2(-PlayerPanel.Pad * 2, -PlayerPanel.Pad * 2);

            var img = _plate.AddComponent<Image>();
            img.raycastTarget = false;
            LobbyTheme.ApplyPanelSkin(img, _plate.AddComponent<Outline>(),
                Fade(LobbyTheme.PanelFill, PlayerPanel.FillAlpha),
                Fade(LobbyTheme.PanelBorder, PlayerPanel.BorderAlpha));
        }

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, a);

        /// <summary>
        /// The curtain's sorting order is SERIALIZED PREFAB DATA — nothing in the decompiled code
        /// (Base.Utils.SceneFadeController / LevelSwitchCurtainController) ever assigns a sortingOrder, so
        /// a hard-coded 7000 was a guess that could have rendered this whole panel BEHIND the loading
        /// screen. Measure it instead: sit one above the canvas the native loading bar actually draws on,
        /// never below our own floor. Re-measured on each show (the curtain is a scene object).
        /// </summary>
        private void EnsureAboveCurtain()
        {
            if (_canvas == null) return;
            var order = NativeWidgetFactory.CurtainCanvasOrder();
            if (order.HasValue && order.Value >= _canvas.sortingOrder)
                _canvas.sortingOrder = order.Value + 1;
        }

        public void Show()
        {
            // IDEMPOTENT: the state-driven visibility below calls this EVERY frame while a co-op load is
            // in progress. Re-zeroing the bars on each call would thrash live progress back to 0%, so the
            // one-time reset happens only on the rising edge.
            if (_visible) return;
            EnsureCanvas();
            EnsureAboveCurtain();
            // Rows are pooled across sessions — clear the previous run's fill so a second load starts at 0.
            foreach (var row in _rows)
            {
                SetFill(row.Fill, 0f);
                row.Percent.text = "0%";
            }
            _canvas.gameObject.SetActive(true);
            _visible = true;
        }

        public void Hide()
        {
            // IDEMPOTENT: also called every frame when no load is in progress.
            if (!_visible) return;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _visible = false;
        }

        private void Update()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || engine.SaveTransfer == null || engine.Session == null)
            {
                Hide();   // no live session → nothing can be loading
                return;
            }

            var coord = engine.SaveTransfer;
            // Authoritative per-peer completion from the shared tracker over the participating roster.
            var tracker = coord.Tracker;
            int expectedPeers = 0, donePeers = 0;
            foreach (var slot in engine.Session.GetRosterSlots())
            {
                expectedPeers++;
                if (tracker.IsDone(slot)) donePeers++;
            }

            bool shouldShow = LoadOverlayVisibility.ShouldShow(
                coord.LoadPhaseStarted, coord.InPhase2, coord.IsDownloading, expectedPeers, donePeers,
                coord.WaitingOnPeers); // EVERY peer sees the others' progress, on every load boundary

            if (shouldShow) Show(); else Hide();
            if (_visible) Refresh(engine);
        }

        /// <summary>Per frame while visible — the plaque's own idiom: re-read the roster and the tracker
        /// and assign a handful of strings, so an ALREADY-OPEN panel cannot miss a change. Row widgets are
        /// built only when the number of seats changes.</summary>
        private void Refresh(NetworkEngine engine)
        {
            // Aspect-adaptive match, recomputed while the overlay is up (one float write) rather than
            // once at build: the canvas is persistent and the player can change resolution between two
            // loads. Only the visible frames need it — nothing reads the scaler while the canvas is off.
            if (_scaler != null) _scaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;

            var session = engine.Session;
            var tracker = engine.SaveTransfer.Tracker;

            // Every OTHER slot: the panel says who ELSE is still loading, never yourself.
            _others.Clear();
            foreach (var p in session.GetLobbyRoster())
                if (p.SlotIndex != session.LocalSlotIndex) _others.Add(p);

            EnsureRows(_others.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < _others.Count;
                if (_rows[i].Go.activeSelf != used) _rows[i].Go.SetActive(used);
                if (used) Paint(_rows[i], _others[i], tracker);
            }
        }

        private static void Paint(Row row, PeerListEntry entry, RosterProgressTracker tracker)
        {
            row.Name.text = string.IsNullOrEmpty(entry.Nickname) ? "Player " + entry.SlotIndex : entry.Nickname;

            var (phase, percent) = tracker.Get(entry.SlotIndex);
            // A peer that reported LoadComplete is DONE even when its last progress row sat below 100:
            // done is event-driven and the rows stop arriving. Without this its bar freezes at e.g. 97%
            // for the whole held-at-the-barrier wait and reads as a stuck peer.
            if (tracker.IsDone(entry.SlotIndex)) { phase = 1; percent = 100; }

            // PHASE-AWARE fill: phase 0 (download) fills the bottom half, phase 1 (native load) the top,
            // so an instant loopback download shows a HALF bar and the bar only fills once the peer is
            // actually loaded. The raw percent still labels the current phase.
            SetFill(row.Fill, RosterProgressTracker.CombinedFill(phase, percent));
            // Phase 0 is "downloading" for a CLIENT row only — the host (slot 0) never downloads anything,
            // it publishes its own geo→tactical level load under phase 0 (HostEntryPhase).
            row.Label.text = (phase == 0 && entry.SlotIndex != 0) ? "Downloading" : "Loading";
            row.Percent.text = percent + "%";
        }

        /// <summary>Grow the pool to <paramref name="count"/> and size the plate to it. Rows are pooled,
        /// never destroyed; surplus rows are deactivated by the caller.</summary>
        private void EnsureRows(int count)
        {
            while (_rows.Count < count) _rows.Add(CreateRow(_rows.Count));
            _plateRect.sizeDelta = new Vector2(PanelW,
                PlayerPanel.Pad * 2 + PlayerPanel.RowH * Mathf.Max(count, 1));
        }

        private Row CreateRow(int index)
        {
            int pad = PlayerPanel.Pad, rowH = PlayerPanel.RowH, nameW = PlayerPanel.NameW;

            var go = new GameObject("Row" + index);
            go.transform.SetParent(_plate.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(PanelW - pad * 2, rowH);
            rt.anchoredPosition = new Vector2(pad, -(pad + rowH * index));

            var name = UiToolkit.CreateText(go, "Name", Vector2.zero, new Vector2(nameW, rowH), "",
                PlayerPanel.NameFont, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));

            // The bar cell, in the plaque's ping-cell position: a dim track with a left-anchored fill rect
            // over it, the phase word left and the percent right — the plaque's Number-in-the-cell idiom,
            // so there is no floating label to place or clip.
            var trackGo = new GameObject("Bar");
            trackGo.transform.SetParent(go.transform, false);
            var track = trackGo.AddComponent<Image>();
            track.color = Fade(LobbyTheme.MutedText, 0.20f);
            var trt = track.rectTransform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0f, 0.5f);
            trt.sizeDelta = new Vector2(BarW, rowH - pad);
            trt.anchoredPosition = new Vector2(nameW + pad, 0f);

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(trackGo.transform, false);
            var fill = fillGo.AddComponent<Image>();
            // NOT LobbyTheme.Accent: that is captured live from the game's WarningUIColor, so the bar came
            // out RED — the colour the game uses to say something is wrong, on a bar that only ever says a
            // peer is making progress. ReadyText is the plaque's own green, the one its ✓ and its best
            // signal bar already wear.
            fill.color = Fade(LobbyTheme.ReadyText, 0.75f);
            var frt = fill.rectTransform;
            // Left-anchored, right edge driven by SetFill; zero offsets so it tracks the track's width.
            frt.anchorMin = Vector2.zero; frt.anchorMax = new Vector2(0f, 1f);
            frt.pivot = new Vector2(0f, 0.5f);
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

            var label = UiToolkit.CreateText(trackGo, "Phase", new Vector2(pad * 0.5f, 0f),
                new Vector2(BarW, rowH), "", PlayerPanel.NumberFont, TextAnchor.MiddleLeft,
                new Vector2(0f, 0.5f));
            label.color = LobbyTheme.BodyText;

            var percent = UiToolkit.CreateText(trackGo, "Percent", new Vector2(-pad * 0.5f, 0f),
                new Vector2(BarW, rowH), "0%", PlayerPanel.NumberFont, TextAnchor.MiddleRight,
                new Vector2(1f, 0.5f));
            percent.color = LobbyTheme.BodyText;

            // Nothing here may eat a click (the plaque's rule, and this canvas has no raycaster anyway).
            track.raycastTarget = false;
            fill.raycastTarget = false;
            name.raycastTarget = false;
            label.raycastTarget = false;
            percent.raycastTarget = false;

            return new Row { Go = go, Name = name, Fill = frt, Label = label, Percent = percent };
        }
    }
}
