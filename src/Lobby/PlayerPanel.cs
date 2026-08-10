using System;
using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Tactical;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// WHO ELSE IS HERE, HOW FAR AWAY THEY ARE, AND WHETHER THEY SAY THEY ARE DONE — three columns, and
    /// not one decision.
    ///
    /// A PRESENTATION SEAM (P4c, law L158, whose seam set names this class). It READS
    /// <see cref="SessionManager.GetLobbyRoster"/> and <see cref="SessionManager.Ping"/> and writes
    /// nothing but its own widgets: no native call, no rail write, no <c>[SerializeMember]</c> leaf. The
    /// grey "dropped" marker in particular is a REPORT, never an input — no start, launch, barrier or
    /// turn predicate may read this file, and none does (the NO-QUORUM mandate, laws L84/L91/L119).
    ///
    /// WHERE IT LIVES. TACTICAL ONLY, one panel object on the mod's own persistent overlay canvas
    /// (<c>MultiplayerUI.EnsureBarCanvas</c>), positioned per frame under the co-op ready button
    /// (<see cref="TacticalReadyButton.Rect"/>), which is itself pinned under the native End Turn button.
    /// Cross-canvas by screen point rather than by re-parenting, which is what keeps it out of the End
    /// Turn row's layout group: a panel that hung BELOW the ready clone as its SIBLING would be measured
    /// by that clone's own <c>TacticalReadyRowFollower.ExtraClearance</c> sweep of "the lowest thing any
    /// sibling draws", pushing the ready button down, pushing the panel down after it, forever.
    ///
    /// IT IS RIGHT-ALIGNED AND CLAMPED, and both halves are the same bug. The End Turn row lives at the
    /// bottom RIGHT of the HUD; hanging the panel off the anchor's bottom-LEFT corner with a left pivot
    /// made it grow RIGHTWARDS from there, straight off the edge of the screen with half of it invisible.
    /// It now hangs off the bottom-RIGHT corner with a (1,1) pivot so it grows INWARDS, and
    /// <see cref="ClampOnScreen"/> then holds the whole rect inside the canvas on both axes — the anchor
    /// is a native widget whose position moves with resolution, aspect and every UI-scale setting, so
    /// "lands on screen" is not a property that arithmetic against one screen can have on its own.
    ///
    /// THERE IS NO GEOSCAPE PANEL. It was removed on the owner's report (2026-08-07): at geoscape sizes
    /// the plate is big enough to cover the interface it reports over, and its status column there had
    /// nothing to say the lobby roster does not — there is no readiness on the geoscape. Tactical is
    /// where the three columns are load-bearing, so tactical is the only place it draws.
    ///
    /// VISIBILITY is inherited the same way the ready button inherits it — the panel is shown only while
    /// that clone is <c>activeInHierarchy</c>, so whatever hides the End Turn module (enemy turn,
    /// cinematic, tutorial lockout) hides this too, with no rule of ours in the middle.
    ///
    /// IT CANNOT EAT A CLICK. Every Graphic it builds has <c>raycastTarget = false</c>, including the ping
    /// meter, and the hover that reveals the millisecond number is a rect/mouse-position test rather than
    /// an EventSystem handler. The tactical HUD gates its confirm click on
    /// <c>EventSystem.IsPointerOverGameObject()</c> and this panel deliberately overhangs the HUD, so a
    /// raycastable surface here would be a dead map click — the same hazard
    /// <c>TacticalReadyButton.TrimRaycast</c> exists for.
    ///
    /// REACTIVITY IS BY CONSTRUCTION, NOT BY EDGE. It re-reads the roster and the ping table every frame
    /// (<see cref="Sync"/>, driven from <c>MultiplayerUI.Update</c> — the same per-frame idiom
    /// <c>LobbyPanel.Refresh</c> already runs on). A peer going ready, un-ready or silent therefore
    /// appears on an ALREADY-OPEN panel within one frame and cannot be missed, which is strictly stronger
    /// than subscribing to an edge: there is no event to forget to raise. Row WIDGETS are rebuilt only
    /// when the set of seats changes; per-frame work is assigning a handful of strings and colours.
    /// ponytail: if N ever leaves co-op sizes, gate the refresh on a dirty flag before anything fancier.
    ///
    /// THE PING NUMBER IS NOT MEASURED HERE. <see cref="PingTable"/> owns it (host measures every peer on
    /// the heartbeat and publishes its table; a client measures its own link to the host). This file only
    /// renders it, and renders NO SAMPLE as an em-dash rather than as a stale number or a spinner: law
    /// L145 — one peer's silence must never leave another peer's UI waiting on it.
    /// </summary>
    internal sealed class PlayerPanel : MonoBehaviour
    {
        // ─── Bar meter thresholds ───────────────────────────────────────────
        // Four bars, cellular-style. The gradient is the meaning: green is a link co-op plays on, red is
        // one it survives. Boundaries are the recon's (rtt-recon.md): <60 / <120 / <250 / else.
        internal static int BarsFor(int ms) => ms < 60 ? 4 : ms < 120 ? 3 : ms < 250 ? 2 : 1;

        /// <summary>Indexed by (bars - 1). Unfilled bars use <see cref="LobbyTheme.MutedText"/> dimmed.</summary>
        private static readonly Color[] BarColors =
        {
            new Color(0.85f, 0.30f, 0.30f, 1f),   // 1 — >=250 ms
            new Color(0.95f, 0.70f, 0.20f, 1f),   // 2 — <250 ms
            new Color(0.80f, 0.85f, 0.30f, 1f),   // 3 — <120 ms
            new Color(0.35f, 0.85f, 0.40f, 1f),   // 4 — <60 ms
        };

        private const int BarCount = 4;

        // ─── Geometry (all theme-scaled; UiScale is the one knob) ──────────
        // OVERHANG SIZING, not lobby sizing. The lobby's Scaled* row metrics are built for a full-screen
        // page; over a live battle map the same numbers are a plate that covers the HUD it reports over
        // (the owner's report, 2026-08-07). So the bases below are the panel's own — roughly half the
        // lobby's — and they still go through LobbyTheme.Scale, so UiScale remains the one knob.
        internal static int Pad => LobbyTheme.Scale(6);
        internal static int RowH => LobbyTheme.Scale(24);
        internal static int NameW => LobbyTheme.Scale(80);
        /// <summary>Wide enough for the four bars AND for the "123 ms" the hover swaps in over them — the
        /// number replaces the meter in the same cell, so there is no floating tooltip to place, clip or
        /// keep on screen.</summary>
        private static int PingW => LobbyTheme.Scale(44);
        private static int StatusW => LobbyTheme.Scale(18);
        private static int PanelW => Pad * 2 + NameW + Pad + PingW + Pad + StatusW;
        internal static int NameFont => LobbyTheme.Scale(13);
        internal static int NumberFont => LobbyTheme.Scale(11);

        /// <summary>Gap between the panel's top edge and the ready button's bottom edge, and the margin
        /// <see cref="ClampOnScreen"/> keeps from every screen edge. One value, themed.</summary>
        private static int Margin => Pad;

        /// <summary>SEE-THROUGH ON PURPOSE. The panel deliberately overhangs the tactical HUD, so an
        /// opaque plate hides the interface rather than reporting over it. Theme colours, panel alpha.</summary>
        internal const float FillAlpha = 0.45f;
        internal const float BorderAlpha = 0.35f;

        private sealed class Row
        {
            public GameObject Go;
            public Text Name;
            public Text Status;
            public Text Number;        // "123 ms" on hover, or the em-dash when there is no sample
            public RectTransform PingCell;
            public Image[] Bars;
        }

        private RectTransform _canvasRect;
        private GameObject _root;
        private RectTransform _rootRect;
        private readonly List<Row> _rows = new List<Row>();
        private string _signature = "";
        private bool _loggedFailure;

        /// <summary>Build the (initially hidden) panel under the mod's overlay canvas. One call, from
        /// <c>MultiplayerUI.Awake</c>; the canvas is persistent, so the panel survives every scene load and
        /// is simply repositioned when the scene changes under it.</summary>
        internal void Attach(Transform barCanvas)
        {
            _canvasRect = barCanvas as RectTransform;
            _root = new GameObject("MultiplayerPlayerPanel");
            _root.transform.SetParent(barCanvas, false);
            _rootRect = _root.AddComponent<RectTransform>();

            var img = _root.AddComponent<Image>();
            img.raycastTarget = false;
            var outline = _root.AddComponent<Outline>();
            LobbyTheme.ApplyPanelSkin(img, outline, Fade(LobbyTheme.PanelFill, FillAlpha),
                                                   Fade(LobbyTheme.PanelBorder, BorderAlpha));

            _root.SetActive(false);
        }

        /// <summary>
        /// THE REPAINT, and the containment point law L158 arm (c) names: everything below runs inside one
        /// try/catch, because this is called from <c>MultiplayerUI.Update</c> — a throw here would unwind
        /// into the game's own Update loop and take the session with it. Failure hides the panel and logs
        /// once; the session is untouched, which is the entire promise of a presentation seam.
        /// </summary>
        internal void Sync()
        {
            try
            {
                SyncCore();
            }
            catch (Exception e)
            {
                if (_root != null) _root.SetActive(false);
                if (_loggedFailure) return;
                _loggedFailure = true;
                Debug.LogError("[Multiplayer] player panel FAILED and is hidden for the rest of this run " +
                               "(nothing else is affected — it reads state and decides nothing): " + e);
            }
        }

        private void SyncCore()
        {
            if (_root == null) return;

            var engine = NetworkEngine.Instance;
            var session = engine == null ? null : engine.Session;
            // The tactical anchor is Unity-null between battles; ReferenceEquals is deliberately NOT used
            // here, because the question really is "is this object still alive", not "do we hold a handle".
            var anchor = TacticalReadyButton.Rect;
            // TACTICAL ONLY. No anchor, no panel — there is no geoscape placement to fall back to any
            // more, and the absence IS the feature (see the class comment).
            bool show = anchor != null && anchor.gameObject.activeInHierarchy &&
                        session != null && engine.IsActiveSession &&
                        engine.SaveTransfer?.SessionStarted == true;

            if (!show)
            {
                if (_root.activeSelf) _root.SetActive(false);
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);

            var roster = session.GetLobbyRoster();
            EnsureRows(roster.Count);
            Place(anchor, roster.Count);

            var mouse = (Vector2)Input.mousePosition;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool used = i < roster.Count;
                if (row.Go.activeSelf != used) row.Go.SetActive(used);
                if (!used) continue;
                Paint(row, roster[i], session, engine, mouse);
            }
        }

        /// <summary>Name, meter, glyph. The one method that turns a roster row into pixels.</summary>
        private void Paint(Row row, PeerListEntry entry, SessionManager session, NetworkEngine engine,
                           Vector2 mouse)
        {
            row.Name.text = string.IsNullOrEmpty(entry.Nickname) ? "Player" : entry.Nickname;
            row.Name.color = entry.Paused ? LobbyTheme.MutedText : LobbyTheme.BodyText;

            // WHOSE LINK IS THIS — PingTable.PingMsFor, which is where the host-id remap this method used
            // to spell out inline now lives, because the lobby roster draws the same meter and the rule
            // may exist exactly once.
            int ms = PingTable.PingMsFor(session, engine, entry);

            bool hover = ms >= 0 && RectTransformUtility.RectangleContainsScreenPoint(row.PingCell, mouse, null);
            PaintBars(row.Bars, ms);
            // The number REPLACES the meter here (this panel overhangs a live battle map and has no room
            // for both) — the lobby row shows the two side by side instead.
            if (hover) foreach (var bar in row.Bars) bar.enabled = false;
            // NO SAMPLE IS SAID OUT LOUD (law L145). An em-dash, permanently — never a stale number, never
            // a spinner, and nothing anywhere waits for the sample that did not arrive.
            row.Number.enabled = ms < 0 || hover;
            row.Number.text = ms < 0 ? "—" : ms + " ms";

            // STATUS. Grey always wins: a peer that has gone silent is not "not ready", it is not here, and
            // showing its last-known readiness would be the one lie this column must not tell. Otherwise
            // the glyph is the advisory ready flag.
            if (entry.Paused)
            {
                row.Status.text = "✗";
                row.Status.color = LobbyTheme.MutedText;
            }
            else if (entry.TacReady)
            {
                row.Status.text = "✓";
                row.Status.color = LobbyTheme.ReadyText;
            }
            else
            {
                row.Status.text = "✗";
                row.Status.color = new Color(0.85f, 0.30f, 0.30f, 1f);
            }
        }

        /// <summary>THE METER, for both screens that draw one. <paramref name="ms"/> below zero is NO
        /// SAMPLE and blanks every bar — the caller says so in words (an em-dash), never with a lit bar.
        /// Shared with the lobby roster so the two meters cannot disagree about what a colour means; the
        /// thresholds law L159 pins live in <see cref="BarsFor"/>, which both go through.</summary>
        internal static void PaintBars(Image[] bars, int ms)
        {
            if (bars == null) return;
            int lit = ms < 0 ? 0 : BarsFor(ms);
            for (int b = 0; b < bars.Length; b++)
            {
                bars[b].enabled = lit > 0;
                bars[b].color = b < lit ? BarColors[lit - 1] : Dim(LobbyTheme.MutedText);
            }
        }

        /// <summary>The four stacked bars themselves, bottom-left of <paramref name="cell"/> and vertically
        /// centred in a <paramref name="rowH"/>-tall row. The one thing here that is DRAWN rather than
        /// reused, because the game ships no discrete signal-strength widget (its only bar is the
        /// continuous loading bar NativeWidgetFactory clones, which reads as progress, not as strength).
        /// Four flat Images — no sprite is generated anywhere in this mod.</summary>
        internal static Image[] CreateBars(Transform cell, int rowH)
        {
            int barW = Mathf.Max(2, LobbyTheme.Scale(3));
            int gap = Mathf.Max(1, LobbyTheme.Scale(1));
            int full = Mathf.Max(4, LobbyTheme.Scale(12));
            var bars = new Image[BarCount];
            for (int b = 0; b < BarCount; b++)
            {
                var bgo = new GameObject("Bar" + b);
                bgo.transform.SetParent(cell, false);
                var brt = bgo.AddComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 0f);
                brt.sizeDelta = new Vector2(barW, full * (b + 1) / (float)BarCount);
                brt.anchoredPosition = new Vector2(b * (barW + gap), (rowH - full) / 2f);
                bars[b] = bgo.AddComponent<Image>();
                bars[b].raycastTarget = false;
            }
            return bars;
        }

        /// <summary>Total width the four bars occupy, so a caller can size the cell that holds them.</summary>
        internal static int BarsWidth => BarCount * Mathf.Max(2, LobbyTheme.Scale(3)) +
                                         (BarCount - 1) * Mathf.Max(1, LobbyTheme.Scale(1));

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, a);
        private static Color Dim(Color c) => Fade(c, 0.25f);

        // ─── Placement ──────────────────────────────────────────────────────

        /// <summary>
        /// Pin the panel under <paramref name="anchor"/>, right-aligned with it and clamped on screen.
        ///
        /// It crosses canvases — ours is a ScreenSpaceOverlay of the mod's own, the ready button lives on
        /// the game's HUD canvas — so it goes world → SCREEN → our canvas' local space. The screen point
        /// is the one currency both canvases are guaranteed to agree on whatever render mode, camera or
        /// scale the HUD is using; converting rect-to-rect directly would silently be wrong the moment the
        /// HUD canvas is not an overlay.
        /// </summary>
        private void Place(RectTransform anchor, int rowCount)
        {
            var size = new Vector2(PanelW, Pad * 2 + RowH * Mathf.Max(rowCount, 1));
            _rootRect.sizeDelta = size;

            anchor.GetWorldCorners(_corners);
            var srcCanvas = anchor.GetComponentInParent<Canvas>();
            var cam = srcCanvas == null || srcCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : srcCanvas.worldCamera;
            // Corners are BL, TL, TR, BR — the bottom-RIGHT one, because the End Turn row it hangs off
            // sits at the right of the HUD and a panel wider than the button must therefore grow LEFT.
            // Taking BL here (and pivoting left) is what pushed it off the edge of the screen.
            var screen = RectTransformUtility.WorldToScreenPoint(cam, _corners[3]);

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out local))
                return;   // degenerate canvas — keep the last good position rather than fling the panel

            _rootRect.anchorMin = _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(1f, 1f);
            _rootRect.anchoredPosition = ClampOnScreen(local - new Vector2(0f, Margin), size);
        }

        /// <summary>
        /// The whole rect, inside the canvas, at any resolution and any UI scale.
        ///
        /// <paramref name="topRight"/> is an anchoredPosition for anchors (0.5,0.5) and pivot (1,1), i.e.
        /// literally the panel's TOP-RIGHT corner measured from the canvas centre — so the four edges are
        /// four subtractions and the clamp is one line per axis. The canvas' own <c>rect</c> is the bound
        /// rather than <c>Screen.width/height</c>: our canvas is a ScaleWithScreenSize one, so its rect is
        /// already denominated in the same scaled units this position is, and reading raw pixels here
        /// would be wrong by exactly the scale factor on every screen that is not 1920×1080.
        ///
        /// The <c>Mathf.Min</c> is the degenerate case — a panel taller or wider than the canvas has no
        /// position that satisfies both edges, and pinning it to the top-right one is the useful answer.
        /// </summary>
        private Vector2 ClampOnScreen(Vector2 topRight, Vector2 size)
        {
            var half = _canvasRect.rect.size * 0.5f;
            float maxX = half.x - Margin, minX = -half.x + Margin + size.x;
            float maxY = half.y - Margin, minY = -half.y + Margin + size.y;
            return new Vector2(Mathf.Clamp(topRight.x, Mathf.Min(minX, maxX), maxX),
                               Mathf.Clamp(topRight.y, Mathf.Min(minY, maxY), maxY));
        }

        private readonly Vector3[] _corners = new Vector3[4];

        // ─── Row pool ───────────────────────────────────────────────────────

        /// <summary>Grow the pool to <paramref name="count"/>. Rows are never destroyed (RailCheck L67 bans
        /// Object.Destroy from the tactical namespace and a pool is cheaper than the churn anyway) — surplus
        /// rows are deactivated by the caller. Rebuilt only when the seat count changes.</summary>
        private void EnsureRows(int count)
        {
            var signature = count.ToString();
            if (signature == _signature) return;
            _signature = signature;

            while (_rows.Count < count) _rows.Add(CreateRow(_rows.Count));
            for (int i = 0; i < _rows.Count; i++)
            {
                var rt = _rows[i].Go.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(Pad, -(Pad + RowH * i));
            }
        }

        private Row CreateRow(int index)
        {
            var go = new GameObject("Row" + index);
            go.transform.SetParent(_root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(PanelW - Pad * 2, RowH);

            var name = UiToolkit.CreateText(go, "Name", Vector2.zero, new Vector2(NameW, RowH), "",
                NameFont, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));

            // The meter cell. Also the hover rect — RectangleContainsScreenPoint is tested against it, so
            // the hover needs no EventSystem handler and the cell needs no raycastTarget.
            var cellGo = new GameObject("Ping");
            cellGo.transform.SetParent(go.transform, false);
            var cell = cellGo.AddComponent<RectTransform>();
            cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 0.5f);
            cell.sizeDelta = new Vector2(PingW, RowH);
            cell.anchoredPosition = new Vector2(NameW + Pad, 0f);

            var bars = CreateBars(cellGo.transform, RowH);

            var number = UiToolkit.CreateText(cellGo, "Number", Vector2.zero, new Vector2(PingW, RowH), "",
                NumberFont, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            number.color = LobbyTheme.SubText;

            var status = UiToolkit.CreateText(go, "Status", new Vector2(NameW + Pad + PingW + Pad, 0f),
                new Vector2(StatusW, RowH), "", NameFont, TextAnchor.MiddleCenter,
                new Vector2(0f, 0.5f));

            name.raycastTarget = false;
            number.raycastTarget = false;
            status.raycastTarget = false;

            return new Row { Go = go, Name = name, Status = status, Number = number, PingCell = cell, Bars = bars };
        }
    }
}
