using System.Collections.Generic;
using Multipleer.Network;
using Multipleer.Network.MessageLayer;
using Multipleer.Transport;
using UnityEngine;
using UnityEngine.UI;

namespace Multipleer.UI
{
    /// <summary>
    /// Runtime uGUI lobby panel (built from code, same proven pattern as MultiplayerUI's in-game
    /// bar — NOT IMGUI, NOT a cloned native prefab). Shows the live roster, ready toggle, nickname
    /// edit, host Play button, and per-peer transfer progress; refreshes every frame from the
    /// SessionManager roster as PEER_LIST / ready / rename / progress messages arrive.
    ///
    /// T1 scope: build + show/hide cleanly, title, role, connect info, Leave button. Roster rows,
    /// ready/nickname/play, and progress are layered on in later increments.
    /// </summary>
    public class LobbyPanel
    {
        private readonly MultiplayerUI _owner;

        // The lobby's OWN overlay Canvas (created in Build). The lobby no longer parents directly
        // under the captured menu Canvas (whose CanvasScaler is unknown → everything rendered tiny
        // and the Leave button fell offscreen); it gets a dedicated ScreenSpaceOverlay canvas with a
        // 1920×1080 ScaleWithScreenSize scaler so the absolute-px layout below is deterministic.
        private Canvas _lobbyCanvas;

        private GameObject _root;
        private Text _roleText;
        private Text _connectText;

        // Interactive controls. Rename moved to the native prompt (own-row click → OnLobbyRenamePrompt),
        // so there is no from-code nickname field in the full-screen tree.
        private Button _leaveButton;
        private Button _readyButton;
        private Text _readyButtonLabel;
        private Toggle _readyToggle;
        private Button _playButton;

        // ─── Full-screen 5-zone layout ─────────────────────────────────────
        private Text _railIpValue;
        private Text _railLocalValue;       // 127.0.0.1:<port> for same-PC two-instance play
        private Text _railStunValue;
        private Text _railSaveValue;
        private Button _chooseSaveBtn;
        private Button _inviteBtn;

        // Chat zone. Version diff drives a cheap re-render; we track the subscribed session so we
        // re-bind (and drop the old handler) when the session instance is re-created on Join.
        private readonly ChatLog _chat = new ChatLog(100);
        private int _chatRenderedVersion = -1;
        private SessionManager _chatSession;
        private System.Action<string, string, bool> _chatHandler;
        private RectTransform _chatContent;        // native scroller content (or fallback rect)
        private readonly List<Text> _chatRows = new List<Text>();
        private InputField _chatInput;

        // Footer buttons.
        private Button _joinButton;

        // Roster rows (player list). Pooled: one row GameObject per slot, reused across refreshes.
        private GameObject _rosterArea;
        private readonly List<RosterRow> _rows = new List<RosterRow>();

        // Layout constants for the roster area.
        private const float RosterTop = 130f;     // px from panel top to first row
        private const float RowHeight = 30f;
        private const float RowWidth = 520f;

        private class RosterRow
        {
            public GameObject Go;
            public Text Label;
            public Button Button;        // own-row click → rename
            public bool RenameWired;
        }

        // SINGLE visibility lever = the lobby's own overlay Canvas GameObject. While that GO is
        // inactive, the WHOLE lobby subtree (_root + every zone + every cloned native widget) is
        // hidden no matter what — so IsVisible, Show, Hide and Build all key on this ONE object and
        // can never disagree (no "panel shows but IsVisible says hidden", or vice-versa). _root stays
        // permanently active *inside* the canvas after Build; only the canvas GO is ever toggled.
        public bool IsVisible => _lobbyCanvas != null && _lobbyCanvas.gameObject.activeSelf;

        public LobbyPanel(MultiplayerUI owner)
        {
            _owner = owner;
        }

        // ─── Build (once) ──────────────────────────────────────────────────

        // Build on the lobby's OWN ROOT overlay Canvas (created here, parented under ModGO so it is a
        // root canvas — see the critical note in the body). The menuCanvas param is still required as
        // a readiness gate (we only build once the native menu is captured) and as the source for
        // cloned native widgets. Buttons are cloned native widgets (TemplateMenuButton via
        // NativeWidgetFactory) so they match the game's look; the panel background, roster rows and
        // nickname field stay from-code (documented per-widget fallbacks — no cheap native template
        // at the menu for these).
        public void Build(Canvas menuCanvas)
        {
            if (_root != null || menuCanvas == null) return;

            // Dedicated overlay Canvas (mirrors MultiplayerUI.MultipleerBarCanvas EXACTLY):
            // ScreenSpaceOverlay + 1920×1080 ScaleWithScreenSize scaler + GraphicRaycaster. Sort order
            // 4000 sits above the menu chrome but BELOW the 5000 in-game status bar.
            //
            // CRITICAL — parent under the mod's ModGO transform (_owner.transform), NOT under the
            // menu Canvas. A Canvas parented under ANOTHER Canvas is a NESTED (non-root) canvas:
            // Unity then IGNORES its CanvasScaler and does NOT drive its RectTransform to the screen
            // rect — the auto-added RectTransform keeps its default (0.5,0.5) anchors / 100×100
            // sizeDelta, so the whole lobby collapsed into a tiny ~100px centred box (the cramping
            // bug). The proven status-bar canvas avoids this by living under ModGO (a plain
            // GameObject, no Canvas ancestor) → it is a ROOT canvas → scaler honoured, overlay fills
            // the screen. We mirror that here. ModGO is persistent, so the lobby canvas now shares the
            // mod's lifetime (the lobby is only Show()n while a session is active, like the bar).
            var canvasGo = new GameObject("MultipleerLobbyCanvas");
            canvasGo.transform.SetParent(_owner.transform, false);
            // Hide the lobby canvas GO from the very first frame. The canvas is the SINGLE visibility
            // lever: while inactive, its whole subtree (_root + every zone) is hidden no matter what.
            // This makes startup-hidden robust even if a Build sub-step throws (the throw is swallowed
            // by the OnMenuReady try/catch in MainMenuPatches) — the half-built panel can never leak
            // onto the menu. Show() re-activates this GO (and _root) when the user opens the lobby.
            canvasGo.SetActive(false);
            _lobbyCanvas = canvasGo.AddComponent<Canvas>();
            _lobbyCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _lobbyCanvas.sortingOrder = 4000;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // Belt-and-suspenders: explicitly full-stretch the canvas RectTransform. On a root overlay
            // canvas Unity drives this anyway, but it makes the full-screen intent explicit and is
            // harmless if the canvas ever ends up nested again.
            var canvasRect = canvasGo.GetComponent<RectTransform>();
            if (canvasRect != null)
            {
                canvasRect.anchorMin = Vector2.zero;
                canvasRect.anchorMax = Vector2.one;
                canvasRect.pivot = new Vector2(0.5f, 0.5f);
                canvasRect.offsetMin = Vector2.zero;
                canvasRect.offsetMax = Vector2.zero;
                canvasRect.localScale = Vector3.one;
            }

            _root = new GameObject("MultipleerLobbyPanel");
            _root.transform.SetParent(_lobbyCanvas.transform, false);

            var rect = _root.AddComponent<RectTransform>();
            // Full-screen: stretch anchors 0..1, zero offsets (was ~85% inset). With the canvas now a
            // proper root overlay (above), this rect fills the 1920×1080 reference rect and every zone
            // anchored as a fraction of it spreads across the full screen.
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;

            // NO full-screen fill. The lobby is laid OVER the game's main-menu background art (the
            // rendered 3D backdrop) as a clean separate "page": the menu's own chrome (buttons,
            // logo, version label) is hidden on Show() so the art reads as the page background, and
            // each zone gets its OWN framed panel for text contrast. A blanket dark Image here would
            // blackout the art — exactly what the redesign removes. The root keeps its full-screen
            // RectTransform purely for child layout/anchoring.

            // try/finally guarantees the lobby is left fully hidden even if a zone-build sub-step
            // throws. A throw here is swallowed upstream (OnMenuReady try/catch), so without this the
            // already-active canvas/root + partially built zones would leak onto the main menu at
            // startup (the "lobby open + chrome visible + empty fields" bug). The finally pins both
            // visibility levers OFF; Show() is the only thing that turns them back on.
            try
            {
                BuildTopBar();
                BuildConnectRail();
                BuildChatZone();
                BuildRosterZone();
                BuildFooter();
            }
            finally
            {
                // Pin the SINGLE visibility lever OFF on EVERY path (normal completion, early-return
                // sub-step, or a thrown+swallowed exception): the lobby canvas GO is forced inactive,
                // which hides the entire subtree. _root is left ACTIVE *inside* the inactive canvas so
                // its active-state can never disagree with the lever (the canvas alone gates
                // visibility). Show() turns the canvas back on; nothing else does.
                _root.SetActive(true);
                if (_lobbyCanvas != null) _lobbyCanvas.gameObject.SetActive(false);
            }
        }

        // TOP BAR: title + subtitle (no X/4 counter — small count lives in the roster header).
        private void BuildTopBar()
        {
            // A centred framed header card so the large title/subtitle keep contrast over arbitrary
            // background art (the root itself has no fill).
            var bar = new GameObject("TopBar");
            bar.transform.SetParent(_root.transform, false);
            var brt = bar.AddComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.5f, 1f);
            brt.anchorMax = new Vector2(0.5f, 1f);
            brt.pivot = new Vector2(0.5f, 1f);
            brt.sizeDelta = new Vector2(560, 78);
            brt.anchoredPosition = new Vector2(0, -8);
            AddFramedPanel(bar);

            UiToolkit.CreateText(bar, "Title", new Vector2(0, -8),
                new Vector2(540, 44), "CO-OP LOBBY", 32, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f));
            _roleText = UiToolkit.CreateText(bar, "Subtitle", new Vector2(0, -50),
                new Vector2(540, 24), "your lobby", 16, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f));
        }

        // LEFT RAIL "Connect": IP (click-to-copy), STUN code (click-to-copy), Save block, Invite.
        private void BuildConnectRail()
        {
            var rail = new GameObject("ConnectRail");
            rail.transform.SetParent(_root.transform, false);
            var rrt = rail.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0f, 0f);
            rrt.anchorMax = new Vector2(0.25f, 1f);
            rrt.offsetMin = new Vector2(24, 80);
            rrt.offsetMax = new Vector2(-12, -90);
            AddFramedPanel(rail);

            UiToolkit.CreateText(rail, "ConnectHdr", new Vector2(12, -10),
                new Vector2(260, 28), "CONNECT", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            UiToolkit.CreateText(rail, "IpLabel", new Vector2(12, -44),
                new Vector2(260, 20), "Your IP (LAN):", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railIpValue = MakeCopyableValue(rail, "IpValue", new Vector2(12, -64),
                () => _owner.GetRailIp());

            // Same-PC value: 127.0.0.1:<port> so a second instance on THIS machine can connect.
            UiToolkit.CreateText(rail, "LocalLabel", new Vector2(12, -92),
                new Vector2(260, 20), "Same PC (2nd instance):", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railLocalValue = MakeCopyableValue(rail, "LocalValue", new Vector2(12, -112),
                () => _owner.GetRailLocalIp());

            UiToolkit.CreateText(rail, "StunLabel", new Vector2(12, -140),
                new Vector2(260, 20), "STUN code:", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railStunValue = MakeCopyableValue(rail, "StunValue", new Vector2(12, -160),
                () => _owner.GetRailStunCode());

            UiToolkit.CreateText(rail, "SaveLabel", new Vector2(12, -200),
                new Vector2(260, 20), "Save to load:", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railSaveValue = UiToolkit.CreateText(rail, "SaveValue", new Vector2(12, -220),
                new Vector2(248, 40), "(none)", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            _chooseSaveBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "ChooseSaveBtn",
                "CHOOSE SAVE…", () => _owner.OnLobbyChooseSave());
            if (_chooseSaveBtn != null) AnchorButton(_chooseSaveBtn, rail.transform, new Vector2(0f, 1f), new Vector2(12, -264), RailButtonSize);
            else _chooseSaveBtn = UiToolkit.CreateButton(rail, "ChooseSaveBtn", "CHOOSE SAVE…",
                new Vector2(12, -264), new Vector2(220, 36), new Vector2(0f, 1f),
                () => _owner.OnLobbyChooseSave());

            _inviteBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "InviteBtn",
                "INVITE VIA STEAM", () => _owner.InvitePlayers());
            if (_inviteBtn != null) AnchorButton(_inviteBtn, rail.transform, new Vector2(0f, 1f), new Vector2(12, -308), RailButtonSize);
            else _inviteBtn = UiToolkit.CreateButton(rail, "InviteBtn", "INVITE VIA STEAM",
                new Vector2(12, -308), new Vector2(220, 36), new Vector2(0f, 1f),
                () => _owner.InvitePlayers());
        }

        // Click-to-copy value text: a button whose label is the live value; click copies it.
        private Text MakeCopyableValue(GameObject parent, string name, Vector2 pos, System.Func<string> getValue)
        {
            var btn = UiToolkit.CreateButton(parent, name, "", pos, new Vector2(240, 22),
                new Vector2(0f, 1f), () => _owner.CopyToClipboard(getValue()));
            var label = btn.GetComponentInChildren<Text>();
            if (label != null) { label.alignment = TextAnchor.MiddleLeft; label.fontSize = 15; }
            return label;
        }

        // CENTER CHAT: native scroller (fallback plain rect) + inline input + Send.
        private void BuildChatZone()
        {
            var chat = new GameObject("ChatZone");
            chat.transform.SetParent(_root.transform, false);
            var crt = chat.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0.27f, 0f);
            crt.anchorMax = new Vector2(0.66f, 1f);
            crt.offsetMin = new Vector2(0, 80);
            crt.offsetMax = new Vector2(0, -90);
            AddFramedPanel(chat);

            UiToolkit.CreateText(chat, "ChatHdr", new Vector2(12, -10),
                new Vector2(300, 28), "CHAT", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            // Scroll area (native scroller content if capturable, else a plain stacked rect).
            var scrollHost = new GameObject("ChatScrollHost");
            scrollHost.transform.SetParent(chat.transform, false);
            var shrt = scrollHost.AddComponent<RectTransform>();
            shrt.anchorMin = new Vector2(0f, 0f);
            shrt.anchorMax = new Vector2(1f, 1f);
            shrt.offsetMin = new Vector2(8, 40);
            shrt.offsetMax = new Vector2(-8, -40);

            _chatContent = NativeWidgetFactory.CloneScroller(scrollHost.transform);
            if (_chatContent == null)
            {
                // Fallback: a plain content rect, top-anchored, rows stacked downward.
                var fb = new GameObject("ChatContentFallback");
                fb.transform.SetParent(scrollHost.transform, false);
                var fbrt = fb.AddComponent<RectTransform>();
                fbrt.anchorMin = new Vector2(0f, 1f);
                fbrt.anchorMax = new Vector2(1f, 1f);
                fbrt.pivot = new Vector2(0.5f, 1f);
                fbrt.sizeDelta = new Vector2(0, 600);
                fbrt.anchoredPosition = Vector2.zero;
                _chatContent = fbrt;
            }

            // Inline input (from-code: no native inline-input template — justified §4a) + Send.
            _chatInput = UiToolkit.CreateInputField(chat, "ChatInput", "",
                new Vector2(0, 4), new Vector2(220, 26), new Vector2(0f, 0f),
                v => { _owner.OnLobbyChatSend(v); if (_chatInput != null) _chatInput.text = ""; });

            var send = UiToolkit.CreateButton(chat, "ChatSend", "SEND",
                new Vector2(230, 4), new Vector2(70, 26), new Vector2(0f, 0f),
                () => { if (_chatInput != null) { _owner.OnLobbyChatSend(_chatInput.text); _chatInput.text = ""; } });
        }

        // RIGHT ROSTER: header with small count + scrollable rows (re-uses the existing pool).
        private void BuildRosterZone()
        {
            var players = new GameObject("PlayersZone");
            players.transform.SetParent(_root.transform, false);
            var prt = players.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(0.68f, 0f);
            prt.anchorMax = new Vector2(1f, 1f);
            prt.offsetMin = new Vector2(0, 80);
            prt.offsetMax = new Vector2(-24, -90);
            AddFramedPanel(players);

            _connectText = UiToolkit.CreateText(players, "PlayersHdr", new Vector2(12, -10),
                new Vector2(300, 28), "PLAYERS (0)", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            _rosterArea = new GameObject("RosterArea");
            _rosterArea.transform.SetParent(players.transform, false);
            var rosterRect = _rosterArea.AddComponent<RectTransform>();
            rosterRect.anchorMin = new Vector2(0f, 1f);
            rosterRect.anchorMax = new Vector2(1f, 1f);
            rosterRect.pivot = new Vector2(0.5f, 1f);
            rosterRect.sizeDelta = new Vector2(0, 400);
            rosterRect.anchoredPosition = new Vector2(0, -46);
        }

        // FOOTER: Leave / Join… / Ready / Play (host).
        private void BuildFooter()
        {
            // Footer buttons sit in the 0..80px band below the framed zones. Anchor them a clear
            // ~44px above the screen bottom (chrome is hidden once the lobby is open, so nothing else
            // occupies the edge) — keeps LEAVE/JOIN/READY/PLAY fully on-screen and off the very edge.
            _leaveButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "LeaveBtn", "LEAVE",
                () => _owner.OnLobbyLeave());
            if (_leaveButton != null) AnchorButton(_leaveButton, _root.transform, new Vector2(0f, 0f), new Vector2(24, 44), FooterButtonSize);
            else _leaveButton = UiToolkit.CreateButton(_root, "LeaveBtn", "LEAVE",
                new Vector2(24, 44), new Vector2(140, 40), new Vector2(0f, 0f),
                () => _owner.OnLobbyLeave());

            _joinButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "JoinBtn", "JOIN…",
                () => _owner.OnLobbyJoinPrompt());
            if (_joinButton != null) AnchorButton(_joinButton, _root.transform, new Vector2(0.35f, 0f), new Vector2(0, 44), FooterButtonSize);
            else _joinButton = UiToolkit.CreateButton(_root, "JoinBtn", "JOIN…",
                new Vector2(0, 44), new Vector2(140, 40), new Vector2(0.35f, 0f),
                () => _owner.OnLobbyJoinPrompt());

            // Ready: native toggle if capturable, else label-flip menu button (fallback).
            _readyToggle = NativeWidgetFactory.CloneReadyToggle(_root.transform, "READY",
                _ => _owner.OnLobbyToggleReady());
            if (_readyToggle != null)
            {
                var trt = _readyToggle.transform as RectTransform;
                if (trt != null) { trt.anchorMin = new Vector2(0.6f, 0f); trt.anchorMax = new Vector2(0.6f, 0f);
                    trt.pivot = new Vector2(0.5f, 0f); trt.anchoredPosition = new Vector2(0, 44); }
            }
            else
            {
                _readyButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "ReadyBtn", "READY",
                    () => _owner.OnLobbyToggleReady());
                if (_readyButton != null) AnchorButton(_readyButton, _root.transform, new Vector2(0.6f, 0f), new Vector2(0, 44), FooterButtonSize);
                else _readyButton = UiToolkit.CreateButton(_root, "ReadyBtn", "READY",
                    new Vector2(0, 44), new Vector2(160, 40), new Vector2(0.6f, 0f),
                    () => _owner.OnLobbyToggleReady());
                _readyButtonLabel = _readyButton != null ? _readyButton.GetComponentInChildren<Text>() : null;
            }

            _playButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "PlayBtn", "PLAY ▸",
                () => _owner.OnLobbyPlay());
            if (_playButton != null) AnchorButton(_playButton, _root.transform, new Vector2(1f, 0f), new Vector2(-24, 44), FooterButtonSize);
            else _playButton = UiToolkit.CreateButton(_root, "PlayBtn", "PLAY ▸",
                new Vector2(-24, 44), new Vector2(140, 40), new Vector2(1f, 0f),
                () => _owner.OnLobbyPlay());
        }

        // Default framed-panel colours: a light SEMI-opaque dark backing (so arbitrary background
        // art behind the panel stays faintly visible while text keeps contrast) + a lighter 1-2px
        // border outline. Tuned per the redesign brief.
        private static readonly Color PanelFill = new Color(0.06f, 0.07f, 0.10f, 0.62f);
        private static readonly Color PanelBorder = new Color(0.5f, 0.55f, 0.65f, 0.9f);

        // FRAMED PANEL: a semi-opaque dark backing Image on the zone root + a border Outline, so the
        // zone reads as a discrete bordered card over the menu background art (NOT a full-screen
        // blackout). The Image is added on the zone root, which is created/parented before its child
        // widgets, so it sits behind them. The Outline (uGUI effect) draws a duplicated border around
        // the Image graphic — a cheap 1-2px frame with no extra GameObject.
        private static void AddFramedPanel(GameObject zone, Color fill, Color border)
        {
            var img = zone.AddComponent<Image>();
            img.color = fill;

            var outline = zone.AddComponent<Outline>();
            outline.effectColor = border;
            outline.effectDistance = new Vector2(2f, 2f);
        }

        // Default-coloured overload used by every zone.
        private static void AddFramedPanel(GameObject zone)
        {
            AddFramedPanel(zone, PanelFill, PanelBorder);
        }

        // Cloned-button sizing. The native TemplateMenuButton clone is sized/scaled for the full-width
        // main-menu column, so dropped unmodified into a lobby zone it overflows badly. We constrain
        // every cloned button to a fixed rect that fits its zone and cap its label font so the text
        // stays inside that rect.
        private static readonly Vector2 RailButtonSize = new Vector2(216, 38);   // left rail inner width
        private static readonly Vector2 FooterButtonSize = new Vector2(150, 40); // footer corner button
        private const int ClonedButtonFontCap = 18;

        // Position AND size a cloned native button so it fits its zone. Resolves the OUTERMOST clone
        // root (the GameObject CloneMenuButton parented directly under <paramref name="panel"/> — the
        // returned Button may be that root or a nested child), resets its localScale to 1 (the prefab
        // can carry a >1 scale), constrains its RectTransform to a fixed size, and caps the label font
        // so the text does not overflow the rect.
        private static void AnchorButton(Button btn, Transform panel, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            if (btn == null) return;

            // Walk up from the Button to the topmost transform whose parent is the panel: that is the
            // clone root CloneMenuButton instantiated (the RectTransform carrying the visual size).
            var t = btn.transform;
            while (t.parent != null && t.parent != panel)
                t = t.parent;
            var rt = t as RectTransform;
            if (rt == null) rt = btn.transform as RectTransform;
            if (rt == null) return;

            // Reset any prefab scale so our fixed size is honoured 1:1.
            rt.localScale = Vector3.one;

            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;

            // Cap the label font so a large menu-sized glyph run fits the constrained rect.
            var label = btn.GetComponentInChildren<Text>(true);
            if (label != null && label.fontSize > ClonedButtonFontCap)
                label.fontSize = ClonedButtonFontCap;
        }

        // ─── Show / Hide ───────────────────────────────────────────────────

        // The ONLY method that makes the lobby visible. Reachable solely from the user-driven button
        // path: MultiplayerUI.ShowNetworkMenu (the injected NETWORK GAME button onClick) →
        // StartHostAndOpenLobby → Show, plus the footer Join… re-show. Nothing in Build / Awake /
        // Update / OnMenuReady / the Harmony postfix calls it, so the lobby can never auto-open.
        public void Show()
        {
            if (_lobbyCanvas == null || _root == null) return;

            // Turn the SINGLE visibility lever ON: activate the lobby canvas GO (its whole subtree,
            // incl. the always-active _root, becomes visible) and re-assert Canvas.enabled. Done
            // BEFORE hiding the menu chrome so no edge path can leave the lobby blank while the menu
            // canvases are down. Mirrors the proven always-under-ModGO status-bar canvas.
            if (!_lobbyCanvas.gameObject.activeSelf) _lobbyCanvas.gameObject.SetActive(true);
            _lobbyCanvas.enabled = true;

            // Hide the main-menu chrome (buttons / logo / version) so the lobby reads as a separate
            // page over the background art. Idempotent: a second Show without an intervening Hide
            // won't double-store (HideMenuChrome guards _chromeHidden).
            NativeWidgetFactory.HideMenuChrome(_lobbyCanvas);
            Refresh();
        }

        public void Hide()
        {
            // Turn the SINGLE visibility lever OFF: deactivate the lobby canvas GO (hides the whole
            // subtree). _root stays active inside it, ready for the next Show().
            if (_lobbyCanvas != null) _lobbyCanvas.gameObject.SetActive(false);
            // CRITICAL: restore the menu chrome we hid on Show, or the main menu stays broken after
            // leaving the lobby. Bulletproof: restores exactly the stored objects, guards nulls, and
            // is a safe no-op if nothing was hidden.
            NativeWidgetFactory.RestoreMenuChrome();
        }

        // ─── Per-frame refresh (driven from MultiplayerUI.Update) ──────────

        public void Refresh()
        {
            // Key the work-guard on the SAME single lever as IsVisible: do nothing unless the lobby
            // canvas GO is actually on screen.
            if (!IsVisible) return;

            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive)
            {
                Hide();
                return;
            }

            // Session has begun (BEGIN released the barrier → level entered): the lobby overlay must
            // get out of the way of the loaded geoscape. The session itself stays active.
            if (engine.SaveTransfer != null && engine.SaveTransfer.SessionStarted)
            {
                Hide();
                return;
            }

            // Subscribe once to chat events (session may be re-created on Join).
            EnsureChatSubscription(engine);

            // TOP BAR subtitle: host hint or chosen save.
            if (_roleText != null)
            {
                var chosen = engine.Session?.ChosenSaveName;
                _roleText.text = string.IsNullOrEmpty(chosen)
                    ? (engine.IsHost ? "your lobby" : "joined lobby")
                    : $"save: {chosen}";
            }

            // CONNECT RAIL values.
            if (_railIpValue != null) _railIpValue.text = _owner.GetRailIp();
            if (_railLocalValue != null) _railLocalValue.text = _owner.GetRailLocalIp();
            if (_railStunValue != null) _railStunValue.text = _owner.GetRailStunCode();
            if (_railSaveValue != null)
            {
                var n = engine.Session?.ChosenSaveName;
                var m = engine.Session?.ChosenSaveMeta;
                _railSaveValue.text = string.IsNullOrEmpty(n) ? "(none)"
                    : (string.IsNullOrEmpty(m) ? n : $"{n}\n{m}");
            }
            // Host-only rail controls.
            if (_chooseSaveBtn != null && _chooseSaveBtn.gameObject.activeSelf != engine.IsHost)
                _chooseSaveBtn.gameObject.SetActive(engine.IsHost);

            // CHAT: re-render only when the log changed.
            if (_chat.Version != _chatRenderedVersion)
            {
                RenderChat();
                _chatRenderedVersion = _chat.Version;
            }

            // ROSTER + controls.
            var roster = RefreshRoster(engine);
            if (_connectText != null) _connectText.text = $"PLAYERS ({roster.Count})";
            RefreshControls(engine, roster);
        }

        private void EnsureChatSubscription(NetworkEngine engine)
        {
            // Re-bind when the session instance changes (smart-Join tears down the auto-hosted
            // session and creates a new SessionManager — see MultiplayerUI.OnLobbyJoin).
            if (ReferenceEquals(_chatSession, engine.Session)) return;

            if (_chatSession != null && _chatHandler != null)
                _chatSession.OnChatReceived -= _chatHandler;

            _chatSession = engine.Session;
            if (_chatSession == null) { _chatHandler = null; return; }

            _chatHandler = (nick, text, isSystem) =>
            {
                if (isSystem) _chat.AppendSystem(text);
                else _chat.Append(nick, text, false);
            };
            _chatSession.OnChatReceived += _chatHandler;
        }

        private void RenderChat()
        {
            if (_chatContent == null) return;
            var lines = _chat.Lines;

            // Grow the chat row pool.
            while (_chatRows.Count < lines.Count)
            {
                var t = UiToolkit.CreateText(_chatContent.gameObject, $"ChatRow{_chatRows.Count}",
                    new Vector2(6, -_chatRows.Count * 20), new Vector2(0, 20), "", 12,
                    TextAnchor.UpperLeft, new Vector2(0f, 1f));
                var rt = t.rectTransform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.offsetMin = new Vector2(6, rt.offsetMin.y);
                rt.offsetMax = new Vector2(-6, rt.offsetMax.y);
                _chatRows.Add(t);
            }

            for (int i = 0; i < _chatRows.Count; i++)
            {
                var row = _chatRows[i];
                if (i >= lines.Count) { if (row.gameObject.activeSelf) row.gameObject.SetActive(false); continue; }
                if (!row.gameObject.activeSelf) row.gameObject.SetActive(true);
                var line = lines[i];
                row.rectTransform.anchoredPosition = new Vector2(0, -i * 20);
                if (line.IsSystem) { row.text = line.Text; row.color = new Color(0.7f, 0.7f, 0.5f); }
                else { row.text = $"{line.Sender}: {line.Text}"; row.color = Color.white; }
            }
        }

        // Update ready-button label, play-button gating, and one-time nickname field init.
        private void RefreshControls(NetworkEngine engine, List<PeerListEntry> roster)
        {
            var localGuid = ClientIdentity.PlayerGuid;

            // Find my own row to read my current ready/nickname state.
            PeerListEntry me = null;
            foreach (var p in roster)
            {
                var isMe = engine.IsHost ? p.IsHost : p.PlayerGuid == localGuid;
                if (isMe) { me = p; break; }
            }

            // Ready state: prefer the native toggle (set isOn without firing our listener), else
            // the label-flip button.
            if (_readyToggle != null)
            {
                var want = me != null && me.Ready;
                if (_readyToggle.isOn != want)
                {
                    // SetIsOnWithoutNotify keeps the toggle visual in sync with authoritative state
                    // without re-invoking OnLobbyToggleReady.
                    _readyToggle.SetIsOnWithoutNotify(want);
                }
            }
            else if (_readyButtonLabel != null)
            {
                _readyButtonLabel.text = (me != null && me.Ready) ? "READY ✓" : "NOT READY";
            }

            // Play button: host only, enabled when host is ready AND every remote client is ready.
            // Cloned native buttons self-manage their disabled visuals from Button.interactable
            // (UIInteractableColorController/Animator on the prefab), so gate via interactable.
            if (_playButton != null)
            {
                var playable = engine.IsHost && AllReady(roster);
                if (_playButton.gameObject.activeSelf != engine.IsHost)
                    _playButton.gameObject.SetActive(engine.IsHost);
                _playButton.interactable = playable;
            }
        }

        // All-ready gate (lobby-computed): every roster entry (host self-entry + all clients) ready.
        private static bool AllReady(List<PeerListEntry> roster)
        {
            if (roster == null || roster.Count == 0) return false;
            foreach (var p in roster)
                if (!p.Ready) return false;
            return true;
        }

        // Rebuild/refresh the player rows from the unified lobby roster (host self-entry + clients).
        private List<PeerListEntry> RefreshRoster(NetworkEngine engine)
        {
            var roster = engine.Session?.GetLobbyRoster() ?? new List<PeerListEntry>();
            var localGuid = ClientIdentity.PlayerGuid;

            // Grow the row pool to match.
            while (_rows.Count < roster.Count)
                _rows.Add(CreateRow(_rows.Count));

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (i >= roster.Count)
                {
                    if (row.Go.activeSelf) row.Go.SetActive(false);
                    continue;
                }

                if (!row.Go.activeSelf) row.Go.SetActive(true);

                var p = roster[i];
                var name = string.IsNullOrEmpty(p.Nickname)
                    ? (p.IsHost ? "Host" : $"Player {p.SteamId}")
                    : p.Nickname;

                // "you" = host's own row (IsHost) on the host; the GUID-matching row on a client.
                var isMe = engine.IsHost ? p.IsHost : p.PlayerGuid == localGuid;

                var tags = "";
                if (p.IsHost) tags += " (host)";
                if (isMe) tags += " (you)";

                // During an active save transfer, show download progress instead of ready status.
                var status = p.Ready ? "READY" : "not ready";
                var color = p.Ready ? new Color(0.5f, 0.9f, 0.5f) : Color.white;

                var st = engine.SaveTransfer;
                if (st != null && st.TransferActive)
                {
                    var progress = ProgressFor(engine, st, p, isMe);
                    if (progress != null)
                    {
                        status = progress;
                        color = new Color(0.7f, 0.8f, 1f);
                    }
                }

                var renameHint = isMe ? "   ✎ (click row to rename)" : "";
                row.Label.text = $"{name}{tags}    -    {status}{renameHint}";
                row.Label.color = color;
                WireRenameClick(row, isMe);
            }

            return roster;
        }

        // Returns a progress/loading string for a roster row during an active transfer, or null to
        // fall back to ready status. Reliable subset: my own exact download %, and (on the host) each
        // client's reported download %. Phase-2 game-load % is an OPEN SDK item (not shown).
        private static string ProgressFor(NetworkEngine engine, SaveTransferCoordinator st,
            PeerListEntry p, bool isMe)
        {
            if (isMe)
            {
                if (st.IsBarrierPending) return "loaded — waiting";
                var pct = st.LocalDownloadPercent;
                if (pct >= 0 && pct < 100) return $"downloading {pct}%";
                if (engine.IsHost) return "sending save";
                return null;
            }

            // Host can show each connected client's reported download %.
            if (engine.IsHost && !p.IsHost)
            {
                if (st.TryGetPeerDownloadPercent(p.SteamId, out var cpct))
                    return cpct >= 100 ? "loaded — waiting" : $"downloading {cpct}%";
            }
            return null;
        }

        private RosterRow CreateRow(int index)
        {
            var go = new GameObject($"Row{index}");
            go.transform.SetParent(_rosterArea.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(RowWidth, RowHeight);
            rect.anchoredPosition = new Vector2(0, -index * RowHeight);

            var label = UiToolkit.CreateText(go, "Label", new Vector2(10, 0),
                new Vector2(RowWidth - 20, RowHeight), "", 15, TextAnchor.MiddleLeft,
                new Vector2(0f, 0.5f));

            // Add the Image BEFORE the Button so the Button has a Graphic to raycast against
            // (Unity requires a Graphic on the same/child object). The transparent Image is the
            // hit target for the own-row rename click.
            var img = go.AddComponent<Image>();
            img.color = new Color(0, 0, 0, 0); // transparent hit area
            var btn = go.AddComponent<Button>();
            var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;

            return new RosterRow { Go = go, Label = label, Button = btn };
        }

        // Wire the own-row click to the native rename prompt exactly once per row; the listener
        // checks live ownership so a pooled row reused for another peer never renames the wrong one.
        private void WireRenameClick(RosterRow row, bool isMe)
        {
            if (row.Button == null) return;
            if (!row.RenameWired)
            {
                row.Button.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                {
                    if (row == FindMyRow()) _owner.OnLobbyRenamePrompt();
                }));
                row.RenameWired = true;
            }
            row.Button.interactable = isMe;
        }

        // Returns the pooled row currently rendering the local player's roster entry, or null.
        private RosterRow FindMyRow()
        {
            var engine = NetworkEngine.Instance;
            if (engine?.Session == null) return null;
            var roster = engine.Session.GetLobbyRoster();
            var localGuid = ClientIdentity.PlayerGuid;
            for (int i = 0; i < roster.Count && i < _rows.Count; i++)
            {
                var p = roster[i];
                var isMe = engine.IsHost ? p.IsHost : p.PlayerGuid == localGuid;
                if (isMe) return _rows[i];
            }
            return null;
        }
    }
}
