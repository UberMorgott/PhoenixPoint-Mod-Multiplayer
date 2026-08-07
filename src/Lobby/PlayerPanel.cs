using System;
using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
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
    /// WHERE IT LIVES. One panel object on the mod's own persistent overlay canvas
    /// (<c>MultiplayerUI.EnsureBarCanvas</c>), for BOTH scenes, positioned per frame:
    ///   • TACTICAL — pinned under the co-op ready button (<see cref="TacticalReadyButton.Rect"/>), which
    ///     is itself pinned under the native End Turn button. Cross-canvas by screen point rather than by
    ///     re-parenting, which is what keeps it out of the End Turn row's layout group: a panel that hung
    ///     BELOW the ready clone as its SIBLING would be measured by that clone's own
    ///     <c>TacticalReadyRowFollower.ExtraClearance</c> sweep of "the lowest thing any sibling draws",
    ///     pushing the ready button down, pushing the panel down after it, forever.
    ///   • GEOSCAPE — the right edge, vertically centred. There is no readiness on the geoscape, so the
    ///     status column there reads connection only (connected / dropped), exactly as specified.
    /// Tactical VISIBILITY is inherited the same way the ready button inherits it — the panel is shown
    /// only while that clone is <c>activeInHierarchy</c>, so whatever hides the End Turn module (enemy
    /// turn, cinematic, tutorial lockout) hides this too, with no rule of ours in the middle.
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
        private static int BarsFor(int ms) => ms < 60 ? 4 : ms < 120 ? 3 : ms < 250 ? 2 : 1;

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
        private static int Pad => LobbyTheme.ScaledPadding / 2;
        private static int RowH => LobbyTheme.ScaledRowHeight;
        private static int NameW => LobbyTheme.ScaledRosterNameWidth;
        /// <summary>Wide enough for the four bars AND for the "123 ms" the hover swaps in over them — the
        /// number replaces the meter in the same cell, so there is no floating tooltip to place, clip or
        /// keep on screen.</summary>
        private static int PingW => LobbyTheme.Scale(60);
        private static int StatusW => LobbyTheme.ScaledIconButtonSize;
        private static int PanelW => Pad * 2 + NameW + Pad + PingW + Pad + StatusW;

        /// <summary>Gap between the panel's top edge and the ready button's bottom edge, and the geoscape
        /// margin from the screen edge. One value, themed.</summary>
        private static int Margin => LobbyTheme.ScaledPadding;

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
            LobbyTheme.ApplyPanelSkin(img, outline, LobbyTheme.PanelFill, LobbyTheme.PanelBorder);

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
            bool tactical = anchor != null && anchor.gameObject.activeInHierarchy;
            bool show = session != null && engine.IsActiveSession &&
                        engine.SaveTransfer?.SessionStarted == true &&
                        (tactical || GeoRuntime.Instance.IsGeoscapeActive);

            if (!show)
            {
                if (_root.activeSelf) _root.SetActive(false);
                return;
            }
            if (!_root.activeSelf) _root.SetActive(true);

            var roster = session.GetLobbyRoster();
            EnsureRows(roster.Count);
            Place(tactical ? anchor : null, roster.Count);

            var mouse = (Vector2)Input.mousePosition;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool used = i < roster.Count;
                if (row.Go.activeSelf != used) row.Go.SetActive(used);
                if (!used) continue;
                Paint(row, roster[i], session, engine, tactical, mouse);
            }
        }

        /// <summary>Name, meter, glyph. The one method that turns a roster row into pixels.</summary>
        private void Paint(Row row, PeerListEntry entry, SessionManager session, NetworkEngine engine,
                           bool tactical, Vector2 mouse)
        {
            row.Name.text = string.IsNullOrEmpty(entry.Nickname) ? "Player" : entry.Nickname;
            row.Name.color = entry.Paused ? LobbyTheme.MutedText : LobbyTheme.BodyText;

            // WHOSE LINK IS THIS. Every id in the table is a TRANSPORT id, and the host's row does not
            // carry one a client can use: BuildPeerList stamps it with the host's OWN LocalSteamId, which
            // is that machine's handle for itself (0 on DirectIP) and not the handle this client's
            // transport knows the host by. A client therefore looks its host reading up under HostPeerId —
            // the id it actually measured against — and everything else matches by construction, because
            // the host both keys the table and writes the roster with the same ids.
            ulong id = entry.IsHost && !engine.IsHost && session.HostPeerId.HasValue
                ? session.HostPeerId.Value
                : entry.SteamId;
            int ms = session.Ping.GetPingMs(id);

            bool hover = ms >= 0 && RectTransformUtility.RectangleContainsScreenPoint(row.PingCell, mouse, null);
            int bars = ms < 0 ? 0 : BarsFor(ms);
            for (int b = 0; b < row.Bars.Length; b++)
            {
                row.Bars[b].enabled = bars > 0 && !hover;
                row.Bars[b].color = b < bars ? BarColors[bars - 1] : Dim(LobbyTheme.MutedText);
            }
            // NO SAMPLE IS SAID OUT LOUD (law L145). An em-dash, permanently — never a stale number, never
            // a spinner, and nothing anywhere waits for the sample that did not arrive.
            row.Number.enabled = ms < 0 || hover;
            row.Number.text = ms < 0 ? "—" : ms + " ms";

            // STATUS. Grey always wins: a peer that has gone silent is not "not ready", it is not here, and
            // showing its last-known readiness would be the one lie this column must not tell. In tactical
            // the glyph is the advisory ready flag; on the geoscape there is no readiness to report, so a
            // present peer is simply present.
            if (entry.Paused)
            {
                row.Status.text = "✗";
                row.Status.color = LobbyTheme.MutedText;
            }
            else if (!tactical || entry.TacReady)
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

        private static Color Dim(Color c) => new Color(c.r, c.g, c.b, 0.25f);

        // ─── Placement ──────────────────────────────────────────────────────

        /// <summary>
        /// Pin the panel under <paramref name="anchor"/> (tactical) or to the right screen edge (geoscape).
        ///
        /// The tactical case crosses canvases — ours is a ScreenSpaceOverlay of the mod's own, the ready
        /// button lives on the game's HUD canvas — so it goes world → SCREEN → our canvas' local space.
        /// The screen point is the one currency both canvases are guaranteed to agree on whatever render
        /// mode, camera or scale the HUD is using; converting rect-to-rect directly would silently be wrong
        /// the moment the HUD canvas is not an overlay.
        /// </summary>
        private void Place(RectTransform anchor, int rowCount)
        {
            _rootRect.sizeDelta = new Vector2(PanelW, Pad * 2 + RowH * Mathf.Max(rowCount, 1));

            if (anchor == null)
            {
                // Geoscape: right edge, vertically centred — the one large stretch of the geoscape frame
                // that carries no native HUD. ponytail: if it ever collides with a mod's own panel, this
                // anchor pair is the whole fix.
                _rootRect.anchorMin = _rootRect.anchorMax = _rootRect.pivot = new Vector2(1f, 0.5f);
                _rootRect.anchoredPosition = new Vector2(-Margin, 0f);
                return;
            }

            anchor.GetWorldCorners(_corners);
            var srcCanvas = anchor.GetComponentInParent<Canvas>();
            var cam = srcCanvas == null || srcCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : srcCanvas.worldCamera;
            // Corners are BL, TL, TR, BR — the bottom-left one is where a panel that hangs under the
            // button, left-aligned with it, puts its own top-left corner.
            var screen = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out local))
                return;   // degenerate canvas — keep the last good position rather than fling the panel

            _rootRect.anchorMin = _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(0f, 1f);
            _rootRect.anchoredPosition = local - new Vector2(0f, Margin);
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
                LobbyTheme.ScaledRowFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));

            // The meter cell. Also the hover rect — RectangleContainsScreenPoint is tested against it, so
            // the hover needs no EventSystem handler and the cell needs no raycastTarget.
            var cellGo = new GameObject("Ping");
            cellGo.transform.SetParent(go.transform, false);
            var cell = cellGo.AddComponent<RectTransform>();
            cell.anchorMin = cell.anchorMax = cell.pivot = new Vector2(0f, 0.5f);
            cell.sizeDelta = new Vector2(PingW, RowH);
            cell.anchoredPosition = new Vector2(NameW + Pad, 0f);

            // Four stacked bars — the one thing here that is DRAWN rather than reused, because the game
            // ships no discrete signal-strength widget (its only bar is the continuous loading bar
            // NativeWidgetFactory clones, which reads as progress, not as strength). Four flat Images.
            int barW = Mathf.Max(2, LobbyTheme.Scale(5));
            int gap = Mathf.Max(1, LobbyTheme.Scale(2));
            int full = Mathf.Max(4, LobbyTheme.Scale(18));
            var bars = new Image[BarCount];
            for (int b = 0; b < BarCount; b++)
            {
                var bgo = new GameObject("Bar" + b);
                bgo.transform.SetParent(cellGo.transform, false);
                var brt = bgo.AddComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 0f);
                brt.sizeDelta = new Vector2(barW, full * (b + 1) / (float)BarCount);
                brt.anchoredPosition = new Vector2(b * (barW + gap), (RowH - full) / 2f);
                bars[b] = bgo.AddComponent<Image>();
                bars[b].raycastTarget = false;
            }

            var number = UiToolkit.CreateText(cellGo, "Number", Vector2.zero, new Vector2(PingW, RowH), "",
                LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            number.color = LobbyTheme.SubText;

            var status = UiToolkit.CreateText(go, "Status", new Vector2(NameW + Pad + PingW + Pad, 0f),
                new Vector2(StatusW, RowH), "", LobbyTheme.ScaledRowFontSize, TextAnchor.MiddleCenter,
                new Vector2(0f, 0.5f));

            name.raycastTarget = false;
            number.raycastTarget = false;
            status.raycastTarget = false;

            return new Row { Go = go, Name = name, Status = status, Number = number, PingCell = cell, Bars = bars };
        }
    }
}
