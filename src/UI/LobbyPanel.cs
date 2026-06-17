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
        // Local optimistic ready flag — flipped on click for instant label feedback, then re-synced
        // from authoritative roster state (me.Ready) in RefreshControls.
        private bool _localReady;
        private Button _playButton;
        // The native PhoenixGeneralButton controller on the cloned Play button (resolved via
        // reflection — the mod can't reference the type). Its Update() repaints ONLY when its own
        // IsEnabled/VisualHovered/VisualPressed change, NOT when BaseButton.interactable changes, so
        // writing interactable alone leaves the button stale (greyed) until a real pointer-enter.
        // SetInteractable(bool) is the complete native "appear now" path: it sets BaseButton.interactable,
        // flips IsEnabled, runs the animator and calls UpdateColorElements() — no pointer needed.
        // object/MethodInfo because the type isn't referenceable at compile time.
        private object _playButtonCtrl;
        private System.Reflection.MethodInfo _playButtonSetInteractable;
        // Cached last-applied Play-button visual state (active + interactable). Refresh() runs every
        // frame, so we force an immediate visual refresh ONLY on a real transition (e.g. the moment
        // the second player's Ready arrives), never every frame. -1 = not yet applied.
        private int _playActiveCache = -1;
        private int _playInteractableCache = -1;

        // ─── Full-screen 5-zone layout ─────────────────────────────────────
        private Text _railStunValue;
        private Text _railSaveValue;
        private Button _chooseSaveBtn;
        private Button _inviteBtn;

        // Chat zone. Version diff drives a cheap re-render; we track the subscribed session so we
        // re-bind (and drop the old handler) when the session instance is re-created on Join.
        // Capacity 500 = effectively whole-session history for a lobby chat (matches the host's
        // ChatHistoryCap). The instance is readonly + never recreated, so history persists for the
        // entire lobby lifetime (Show/Hide/Refresh never clear it).
        private readonly ChatLog _chat = new ChatLog(500);
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

        // Layout constants for the roster area. RowHeight now feeds LayoutElement.minHeight; the old
        // RosterTop / RowWidth pixel constants are gone (layout groups own position/width).
        private const float RowHeight = 30f;

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

            // ROOT layout: a VerticalLayoutGroup stacks TopBar / MainRow / Footer top-to-bottom and
            // owns all vertical placement (replaces the old per-zone anchoredPosition Y math). Padding
            // keeps zones off the screen edges on ultrawide; childForceExpandWidth lets each row span
            // the full width while childForceExpandHeight=false lets MainRow's flexibleHeight absorb
            // all the leftover vertical space.
            var rootVlg = _root.AddComponent<VerticalLayoutGroup>();
            rootVlg.padding = new RectOffset(48, 48, 24, 24);
            rootVlg.spacing = 16;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;
            rootVlg.childAlignment = TextAnchor.UpperCenter;

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
                BuildMainRow();
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
            // A framed header card whose height is fixed (LE.minHeight 78, flexibleHeight 0) so the
            // root VLG never stretches it — MainRow takes the slack. Title/subtitle are stacked by an
            // inner VLG (no manual anchoredPosition).
            var bar = new GameObject("TopBar");
            bar.transform.SetParent(_root.transform, false);
            bar.AddComponent<RectTransform>();
            AddFramedPanel(bar);

            var le = LE(bar);
            le.minHeight = 78;
            le.flexibleHeight = 0;

            var vlg = bar.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.spacing = 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var title = UiToolkit.CreateText(bar, "Title", Vector2.zero,
                new Vector2(540, 44), "CO-OP LOBBY", 32, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f));
            LE(title.gameObject).minHeight = 44;

            _roleText = UiToolkit.CreateText(bar, "Subtitle", Vector2.zero,
                new Vector2(540, 24), "your lobby", 16, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f));
            LE(_roleText.gameObject).minHeight = 24;
        }

        // MAIN ROW: three equal-height cards (ConnectColumn / ChatColumn / PlayersColumn) sharing the
        // row width 1:2:1 via per-column LayoutElement.flexibleWidth under childForceExpandWidth. Takes
        // ALL leftover vertical space (LE.flexibleHeight 1) so the row stretches to fill between the
        // fixed-height TopBar and Footer.
        private void BuildMainRow()
        {
            var mainRow = new GameObject("MainRow");
            mainRow.transform.SetParent(_root.transform, false);
            mainRow.AddComponent<RectTransform>();

            LE(mainRow).flexibleHeight = 1;

            var hlg = mainRow.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 24;
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            BuildConnectRail(mainRow);
            BuildChatZone(mainRow);
            BuildRosterZone(mainRow);
        }

        // LEFT COLUMN "Connect": STUN code (click-to-copy), Save block, Choose Save / Invite buttons.
        private void BuildConnectRail(GameObject parent)
        {
            var rail = new GameObject("ConnectColumn");
            rail.transform.SetParent(parent.transform, false);
            rail.AddComponent<RectTransform>();
            AddFramedPanel(rail);

            LE(rail).flexibleWidth = 1;

            var vlg = rail.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.spacing = 8;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var hdr = UiToolkit.CreateText(rail, "ConnectHdr", Vector2.zero,
                new Vector2(260, 28), "CONNECT", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            LE(hdr.gameObject).minHeight = 28;

            var stunLabel = UiToolkit.CreateText(rail, "StunLabel", Vector2.zero,
                new Vector2(260, 20), "STUN code (click to copy):", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            LE(stunLabel.gameObject).minHeight = 20;

            _railStunValue = MakeCopyableValue(rail, "StunValue", () => _owner.GetRailStunCode());
            LE(_railStunValue.transform.parent.gameObject).minHeight = 32;

            var saveLabel = UiToolkit.CreateText(rail, "SaveLabel", Vector2.zero,
                new Vector2(260, 20), "Save to load:", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            LE(saveLabel.gameObject).minHeight = 20;

            _railSaveValue = UiToolkit.CreateText(rail, "SaveValue", Vector2.zero,
                new Vector2(248, 40), "(none)", 14, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            LE(_railSaveValue.gameObject).minHeight = 40;

            _chooseSaveBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "ChooseSaveBtn",
                "CHOOSE SAVE…", () => _owner.OnLobbyChooseSave());
            if (_chooseSaveBtn != null)
                AddCloneLayoutElement(_chooseSaveBtn, rail.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _chooseSaveBtn = UiToolkit.CreateButton(rail, "ChooseSaveBtn", "CHOOSE SAVE…",
                    Vector2.zero, new Vector2(220, 36), new Vector2(0f, 1f),
                    () => _owner.OnLobbyChooseSave());
                LE(_chooseSaveBtn.gameObject).preferredHeight = 38;
            }

            _inviteBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "InviteBtn",
                "INVITE VIA STEAM", () => _owner.InvitePlayers());
            if (_inviteBtn != null)
                AddCloneLayoutElement(_inviteBtn, rail.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _inviteBtn = UiToolkit.CreateButton(rail, "InviteBtn", "INVITE VIA STEAM",
                    Vector2.zero, new Vector2(220, 36), new Vector2(0f, 1f),
                    () => _owner.InvitePlayers());
                LE(_inviteBtn.gameObject).preferredHeight = 38;
            }
        }

        // Click-to-copy value text: a button whose label is the live value; click copies it.
        // Click-to-copy value text: a button whose label is the live value; click copies it. The
        // button rect is owned by the parent layout group (caller sets the LayoutElement.minHeight).
        private Text MakeCopyableValue(GameObject parent, string name, System.Func<string> getValue)
        {
            var btn = UiToolkit.CreateButton(parent, name, "", Vector2.zero, new Vector2(240, 32),
                new Vector2(0f, 1f), () => _owner.CopyToClipboard(getValue()));
            var label = btn.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.alignment = TextAnchor.MiddleLeft;
                label.fontSize = 24;
                label.fontStyle = FontStyle.Bold;
            }
            return label;
        }

        // CENTER CHAT: native scroller (fallback plain rect) + inline input + Send.
        private void BuildChatZone(GameObject parent)
        {
            var chat = new GameObject("ChatColumn");
            chat.transform.SetParent(parent.transform, false);
            chat.AddComponent<RectTransform>();
            AddFramedPanel(chat);

            LE(chat).flexibleWidth = 2;   // center widest (1:2:1)

            var vlg = chat.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.spacing = 8;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var hdr = UiToolkit.CreateText(chat, "ChatHdr", Vector2.zero,
                new Vector2(300, 28), "CHAT", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            var hle = LE(hdr.gameObject); hle.minHeight = 28; hle.flexibleHeight = 0;

            // Scroll host: fills the middle of the column (flexibleHeight 1). Plain RectTransform — NOT
            // a layout group — so the scroller content's ContentSizeFitter is conflict-free (§E-1).
            var scrollHost = new GameObject("ChatScrollHost");
            scrollHost.transform.SetParent(chat.transform, false);
            scrollHost.AddComponent<RectTransform>();
            var sle = LE(scrollHost); sle.flexibleHeight = 1; sle.minHeight = 80;

            // Chat content = a plain from-code top-anchored rect, identical to the proven RosterContent
            // construction (BuildRosterZone). The native CloneScroller was the ONLY structural
            // difference between the working roster list and the chat list; under the cloned native
            // ScrollRect's viewport mask the from-code chat rows did not render ("nothing shows" even
            // though the data pipeline appended them). Mirroring the known-good roster rect — top
            // anchors, VLG + ContentSizeFitter via ConfigureScrollContent, parent is scrollHost (not a
            // layout group → fitter conflict-free, §E-1) — makes the rows visible deterministically.
            var contentGo = new GameObject("ChatContent");
            contentGo.transform.SetParent(scrollHost.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            contentRect.anchoredPosition = Vector2.zero;
            _chatContent = contentRect;
            ConfigureScrollContent(_chatContent, padding: 4);

            // Input row: input (flexibleWidth 1) + Send (preferredWidth 70), laid out by an HLG.
            var inputRow = new GameObject("ChatInputRow");
            inputRow.transform.SetParent(chat.transform, false);
            inputRow.AddComponent<RectTransform>();
            var irle = LE(inputRow); irle.minHeight = 30; irle.flexibleHeight = 0;
            var irhlg = inputRow.AddComponent<HorizontalLayoutGroup>();
            irhlg.spacing = 8;
            irhlg.childControlWidth = true;
            irhlg.childControlHeight = true;
            irhlg.childForceExpandWidth = false;
            irhlg.childForceExpandHeight = true;

            // Both the Enter key (InputField.onEndEdit) and the SEND button route through ONE helper
            // (SendCurrentChatInput) so the input text is read, empty-checked, sent, and cleared
            // identically on every path. The send goes via OnLobbyChatSend → SessionManager.SendChat,
            // which on the host echoes locally (sender sees their own line) and on a client relays to
            // the host who broadcasts it back — so the sender always sees their message.
            _chatInput = UiToolkit.CreateInputField(inputRow, "ChatInput", "",
                Vector2.zero, new Vector2(220, 26), new Vector2(0f, 0f),
                _ => SendCurrentChatInput());
            LE(_chatInput.gameObject).flexibleWidth = 1;

            var send = UiToolkit.CreateButton(inputRow, "ChatSend", "SEND",
                Vector2.zero, new Vector2(70, 26), new Vector2(0f, 0f),
                SendCurrentChatInput);
            var sndle = LE(send.gameObject); sndle.preferredWidth = 70; sndle.flexibleWidth = 0;
        }

        // Single chat-send entry point shared by the SEND button and the Enter key. Reads the current
        // input text, ignores empty/whitespace, sends it down the existing chat path, then clears the
        // field. The sent line comes back via OnChatReceived (host echo / host broadcast) → _chat →
        // RenderChat, so the sender sees their own message.
        private void SendCurrentChatInput()
        {
            if (_chatInput == null) return;
            var text = _chatInput.text;
            if (!string.IsNullOrWhiteSpace(text))
                _owner.OnLobbyChatSend(text);
            _chatInput.text = "";
        }

        // RIGHT ROSTER: header with small count + scrollable rows (re-uses the existing pool).
        private void BuildRosterZone(GameObject parent)
        {
            var players = new GameObject("PlayersColumn");
            players.transform.SetParent(parent.transform, false);
            players.AddComponent<RectTransform>();
            AddFramedPanel(players);

            LE(players).flexibleWidth = 1;

            var vlg = players.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(12, 12, 12, 12);
            vlg.spacing = 8;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            _connectText = UiToolkit.CreateText(players, "PlayersHdr", Vector2.zero,
                new Vector2(300, 28), "PLAYERS (0)", 20, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            var hle = LE(_connectText.gameObject); hle.minHeight = 28; hle.flexibleHeight = 0;

            // Scroll host fills the column (flexibleHeight 1); plain rect, NOT a layout group.
            var scrollHost = new GameObject("RosterScrollHost");
            scrollHost.transform.SetParent(players.transform, false);
            scrollHost.AddComponent<RectTransform>();
            LE(scrollHost).flexibleHeight = 1;

            // RosterContent (= _rosterArea): top-stretched in the host, rows stacked by a VLG and
            // sized by a ContentSizeFitter (host is not a layout group → conflict-free, §E-1).
            _rosterArea = new GameObject("RosterContent");
            _rosterArea.transform.SetParent(scrollHost.transform, false);
            var rosterRect = _rosterArea.AddComponent<RectTransform>();
            rosterRect.anchorMin = new Vector2(0f, 1f);
            rosterRect.anchorMax = new Vector2(1f, 1f);
            rosterRect.pivot = new Vector2(0.5f, 1f);
            rosterRect.offsetMin = Vector2.zero;
            rosterRect.offsetMax = Vector2.zero;
            rosterRect.anchoredPosition = Vector2.zero;
            ConfigureScrollContent(rosterRect, padding: 0);
        }

        // FOOTER: Leave / Join… / Ready / Play (host).
        // FOOTER: Leave (left) … Join / Ready / Play (right), pushed apart by a flexible Spacer. Fixed
        // height (LE.minHeight 48, flexibleHeight 0); buttons laid out by a HorizontalLayoutGroup so
        // there is zero fractional-X math.
        private void BuildFooter()
        {
            var footer = new GameObject("Footer");
            footer.transform.SetParent(_root.transform, false);
            footer.AddComponent<RectTransform>();
            var fle = LE(footer); fle.minHeight = 48; fle.flexibleHeight = 0;

            var hlg = footer.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 12;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            // Leave (left).
            _leaveButton = NativeWidgetFactory.CloneMenuButton(footer.transform, "LeaveBtn", "LEAVE",
                () => _owner.OnLobbyLeave());
            if (_leaveButton != null)
                AddCloneLayoutElement(_leaveButton, footer.transform, FooterButtonSize.x, FooterButtonSize.y);
            else
            {
                _leaveButton = UiToolkit.CreateButton(footer, "LeaveBtn", "LEAVE",
                    Vector2.zero, new Vector2(150, 40), new Vector2(0f, 0f),
                    () => _owner.OnLobbyLeave());
                LE(_leaveButton.gameObject).preferredWidth = 150;
            }

            // Flexible spacer pushes the remaining buttons to the right edge.
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(footer.transform, false);
            spacer.AddComponent<RectTransform>();
            LE(spacer).flexibleWidth = 1;

            // Join.
            _joinButton = NativeWidgetFactory.CloneMenuButton(footer.transform, "JoinBtn", "JOIN…",
                () => _owner.OnLobbyJoinPrompt());
            if (_joinButton != null)
                AddCloneLayoutElement(_joinButton, footer.transform, FooterButtonSize.x, FooterButtonSize.y);
            else
            {
                _joinButton = UiToolkit.CreateButton(footer, "JoinBtn", "JOIN…",
                    Vector2.zero, new Vector2(150, 40), new Vector2(0f, 0f),
                    () => _owner.OnLobbyJoinPrompt());
                LE(_joinButton.gameObject).preferredWidth = 150;
            }

            // Ready: a plain cloned MENU BUTTON acting as a toggle — same widget family as
            // Leave/Join/Play, which render cleanly in a LayoutElement slot. (The old approach cloned a
            // GameOptionViewController option-row whose internal layout + "NEEDS TEXT" placeholder leaked
            // OUTSIDE its wrapper; the menu button has no such stray children.) The button is interactable
            // for everyone — OnLobbyToggleReady drives both the host (HostReady) and client (ClientReady)
            // paths — so it is NOT host-gated. Authoritative ready state arrives via the roster and is
            // re-synced into _localReady + the label in RefreshControls.
            _readyButton = NativeWidgetFactory.CloneMenuButton(footer.transform, "ReadyBtn", "READY",
                () =>
                {
                    _localReady = !_localReady;        // optimistic flip for instant feedback
                    _owner.OnLobbyToggleReady();        // reuse the EXACT existing propagation path
                    UpdateReadyButtonLabel();
                });
            if (_readyButton != null)
                AddCloneLayoutElement(_readyButton, footer.transform, 150, FooterButtonSize.y);
            else
            {
                _readyButton = UiToolkit.CreateButton(footer, "ReadyBtn", "READY",
                    Vector2.zero, new Vector2(150, 40), new Vector2(0f, 0f),
                    () =>
                    {
                        _localReady = !_localReady;
                        _owner.OnLobbyToggleReady();
                        UpdateReadyButtonLabel();
                    });
                LE(_readyButton.gameObject).preferredWidth = 150;
            }
            _readyButtonLabel = _readyButton != null ? _readyButton.GetComponentInChildren<Text>() : null;
            UpdateReadyButtonLabel();

            // Play (host).
            _playButton = NativeWidgetFactory.CloneMenuButton(footer.transform, "PlayBtn", "PLAY ▸",
                () => _owner.OnLobbyPlay());
            if (_playButton != null)
            {
                AddCloneLayoutElement(_playButton, footer.transform, FooterButtonSize.x, FooterButtonSize.y);

                // Resolve the native PhoenixGeneralButton controller on the clone ONCE. It lives on the
                // prefab root that also carries BaseButton; the cloned child Button is at-or-below it, so
                // GetComponentInParent(type) finds it. Fall back to GetComponentInChildren if the layout
                // ever puts the controller below the Button. We then drive its SetInteractable(bool) to
                // repaint the enabled/greyed visual immediately (see RefreshPlayButtonVisual).
                var pgbType = HarmonyLib.AccessTools.TypeByName(
                    "PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton");
                if (pgbType != null)
                {
                    _playButtonCtrl = _playButton.GetComponentInParent(pgbType)
                        ?? _playButton.GetComponentInChildren(pgbType);
                    if (_playButtonCtrl != null)
                        _playButtonSetInteractable =
                            HarmonyLib.AccessTools.Method(pgbType, "SetInteractable", new[] { typeof(bool) });
                }
            }
            else
            {
                _playButton = UiToolkit.CreateButton(footer, "PlayBtn", "PLAY ▸",
                    Vector2.zero, new Vector2(150, 40), new Vector2(0f, 0f),
                    () => _owner.OnLobbyPlay());
                LE(_playButton.gameObject).preferredWidth = 150;
            }
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

        // Get-or-add a LayoutElement on a GameObject (caller sets the size fields). Centralises the
        // null-coalescing pattern used throughout the layout-driven build.
        private static LayoutElement LE(GameObject go)
        {
            return go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        }

        // Walk UP from a Clone*-returned widget (often a NESTED inner Button/Toggle) to the clone ROOT
        // — the topmost transform whose parent is <paramref name="container"/>. Clone* parents that
        // root directly under the container; the inner widget we get back is below it. Returns the
        // clone root transform (or the start transform if no walk applies), or null.
        private static Transform ResolveCloneRoot(Transform start, Transform container)
        {
            if (start == null) return null;
            var t = start;
            while (t.parent != null && t.parent != container)
                t = t.parent;
            return t;
        }

        // Cap the label font on a cloned widget so a large menu-sized glyph run fits its constrained
        // rect (the prefab text is sized for the full-width menu column).
        private static void CapCloneFont(Transform start)
        {
            if (start == null) return;
            var label = start.GetComponentInChildren<Text>(true);
            if (label != null && label.fontSize > ClonedButtonFontCap)
                label.fontSize = ClonedButtonFontCap;
        }

        // Size a cloned native button via a LayoutElement on its clone ROOT (the parent LayoutGroup
        // then drives position/size — no anchoredPosition/sizeDelta math). Resolves the OUTERMOST clone
        // root (the GameObject Clone* parented directly under <paramref name="container"/> — the
        // returned Button may be that root or a nested child), resets its localScale to 1 (the prefab
        // can carry a >1 scale), sets preferred W/H + flexibleWidth 0, and caps the label font.
        private static void AddCloneLayoutElement(Button btn, Transform container, float w, float h)
        {
            if (btn == null) return;
            AddCloneLayoutElement(btn.transform, container, w, h);
        }

        private static void AddCloneLayoutElement(Transform start, Transform container, float w, float h)
        {
            var root = ResolveCloneRoot(start, container);
            if (root == null) return;

            // Reset any prefab scale so our preferred size is honoured 1:1.
            root.localScale = Vector3.one;

            var le = LE(root.gameObject);
            le.preferredWidth = w;
            le.preferredHeight = h;
            le.flexibleWidth = 0;

            CapCloneFont(start);
        }

        // Stretch a cloned native scroller GO (an ancestor of its returned content RectTransform, child
        // of <paramref name="host"/>) to fill the group-sized host: relative 0..1 anchors, zero
        // offsets → rubber, no pixel math. No LayoutElement on it (the host is not a layout group).
        private static void StretchScrollerToHost(RectTransform content, Transform host)
        {
            if (content == null || host == null) return;
            Transform t = content.transform;
            while (t.parent != null && t.parent != host)
                t = t.parent;
            var rt = t as RectTransform;
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }

        // Configure a ScrollRect content (or fallback list) RectTransform to stack rows top-down and
        // self-size by content: pivot top, VerticalLayoutGroup + ContentSizeFitter(PreferredSize). Its
        // parent is the viewport / scroll host (NOT a layout group) so the fitter is conflict-free (§E-1).
        private static void ConfigureScrollContent(RectTransform content, int padding)
        {
            if (content == null) return;
            content.pivot = new Vector2(0.5f, 1f);   // growth/scroll downward

            var vlg = content.gameObject.GetComponent<VerticalLayoutGroup>()
                      ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2;
            vlg.padding = new RectOffset(padding, padding, padding, padding);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var fitter = content.gameObject.GetComponent<ContentSizeFitter>()
                         ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
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

            // Hide the in-game status bar (its own canvas, sort 5000) while the lobby is open: it is
            // shown right before Show() on the host/join paths and, on a root canvas above the lobby's
            // 4000, otherwise overlaps the lobby's footer. Restored in Hide() so the in-geoscape bar
            // returns once the lobby closes (session play).
            MultiplayerUI.Instance?.HideInGameBar();

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

            // Restore the in-game status bar we hid on Show — but ONLY while a session is still live
            // (lobby closed because the geoscape was entered → the bar belongs back in-game). When the
            // lobby closes because the session was torn down (LEAVE / disconnect), the engine is no
            // longer active and the bar must stay hidden; the disconnect paths also explicitly hide it.
            var engine = NetworkEngine.Instance;
            if (engine != null && engine.IsActive)
                MultiplayerUI.Instance?.ShowInGameBar();
        }

        // Hide the lobby overlay so the game's NATIVE Load screen (UIStateHomeLoadGame) is visible
        // underneath: deactivate the lobby canvas AND restore the native menu chrome we disabled on
        // Show() (HideMenuChrome turned every native root canvas off — the native Load screen renders
        // on those, so without this restore it would be invisible). The session is left intact; a
        // later Show() re-hides the chrome again. The in-game status bar (sort 5000) is also hidden so
        // it can't overlap the native screen.
        public void HideForNativeScreen()
        {
            if (_lobbyCanvas != null) _lobbyCanvas.gameObject.SetActive(false);
            NativeWidgetFactory.RestoreMenuChrome();
            MultiplayerUI.Instance?.HideInGameBar();
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

            // Grow the chat row pool. The content's VerticalLayoutGroup positions each row top-down and
            // the ContentSizeFitter sizes the content — no manual anchoredPosition Y math.
            while (_chatRows.Count < lines.Count)
            {
                var t = UiToolkit.CreateText(_chatContent.gameObject, $"ChatRow{_chatRows.Count}",
                    Vector2.zero, new Vector2(0, 18), "", 12,
                    TextAnchor.UpperLeft, new Vector2(0f, 1f));
                var le = LE(t.gameObject);
                le.minHeight = 18;
                le.flexibleWidth = 1;
                _chatRows.Add(t);
            }

            for (int i = 0; i < _chatRows.Count; i++)
            {
                var row = _chatRows[i];
                if (i >= lines.Count) { if (row.gameObject.activeSelf) row.gameObject.SetActive(false); continue; }
                if (!row.gameObject.activeSelf) row.gameObject.SetActive(true);
                var line = lines[i];
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

            // Ready state: re-sync the local flag from authoritative roster state (me.Ready) and relabel
            // the button. This overrides any optimistic click flip with the truth (e.g. after a
            // reconnect or if the host's view differs), without re-invoking OnLobbyToggleReady.
            _localReady = me != null && me.Ready;
            UpdateReadyButtonLabel();

            // Play button: host only, enabled when host is ready AND every remote client is ready.
            // Cloned native buttons self-manage their disabled visuals from Button.interactable
            // (UIInteractableColorController/Animator on the prefab), so gate via interactable.
            if (_playButton != null)
            {
                var playable = engine.IsHost && AllReady(roster);

                var activeNow = engine.IsHost ? 1 : 0;
                if (_playButton.gameObject.activeSelf != engine.IsHost)
                    _playButton.gameObject.SetActive(engine.IsHost);

                var interactableNow = playable ? 1 : 0;
                _playButton.interactable = playable;

                // The cloned native button's enabled/greyed visual is driven by its PhoenixGeneralButton
                // controller, which repaints ONLY when its own IsEnabled changes (via SetInteractable) —
                // NOT when BaseButton.interactable changes. So when the second player's Ready flips
                // AllReady false→true (remote peer-ready path) OR the host toggles its own Ready (local
                // path) — both flow through here — we must drive SetInteractable so the button appears
                // immediately, without a pointer-enter. Fire ONLY on a real transition (the cache guard
                // is per-instance and changes only when playable/active actually flips, so it covers BOTH
                // the local and remote paths) to avoid repainting every frame.
                if (activeNow != _playActiveCache || interactableNow != _playInteractableCache)
                {
                    _playActiveCache = activeNow;
                    _playInteractableCache = interactableNow;
                    RefreshPlayButtonVisual(playable);
                }
            }
        }

        // Make the Play button's enabled/greyed visual appear immediately, without a pointer-enter.
        // The clone is driven by the native PhoenixGeneralButton controller, whose Update() repaints
        // only when ITS OWN IsEnabled/VisualHovered/VisualPressed change — not when BaseButton.interactable
        // changes. SetInteractable(bool) is the complete native path: it sets BaseButton.interactable,
        // flips IsEnabled, advances the animator (SetAnimationState → _animator.Update(0f)) and calls
        // ResetButtonAnimations → UpdateColorElements(). The old approach (toggling Button.enabled +
        // LayoutRebuilder + Canvas.ForceUpdateCanvases) did nothing because it never touched the
        // controller, so it has been dropped.
        private void RefreshPlayButtonVisual(bool playable)
        {
            if (_playButtonSetInteractable != null && _playButtonCtrl != null)
            {
                try { _playButtonSetInteractable.Invoke(_playButtonCtrl, new object[] { playable }); }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError("[Multipleer] Play button repaint failed: " + e.Message);
                }
            }
            else if (_playButton != null)
            {
                // Fallback (controller unresolved, e.g. the UiToolkit-created non-native button): just
                // set interactable, which IS sufficient for a plain Unity Selectable.
                _playButton.interactable = playable;
            }
        }

        // Reflect the current local ready state on the ready button's label. Call after a click flip and
        // whenever RefreshControls re-syncs _localReady from authoritative roster state. "✓ READY" =
        // readied up; "READY" = press to ready up.
        private void UpdateReadyButtonLabel()
        {
            if (_readyButtonLabel == null) return;
            _readyButtonLabel.text = _localReady ? "✓ READY" : "READY";
        }

        // All-ready start gate (lobby-computed): IGNORE the host self-entry (the host is the starter,
        // not a ready-gated player) and require at least one NON-host peer, all ready. Delegates to
        // LobbyController.AllClientsReady so the Play-button visual uses the EXACT same rule the
        // press-time guards (OnLobbyPlay / HostStartSession) use — visual and gate can never disagree
        // (the old "host self-entry Ready=HostReady ⇒ AllReady true while alone" lit Play solo = Bug B).
        private static bool AllReady(List<PeerListEntry> roster)
        {
            if (roster == null) return false;
            var nonHostReady = new List<bool>();
            foreach (var p in roster)
                if (!p.IsHost) nonHostReady.Add(p.Ready);
            return LobbyController.AllClientsReady(nonHostReady);
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
            go.AddComponent<RectTransform>();
            // The RosterContent VerticalLayoutGroup positions/sizes the row; LE feeds its min height and
            // lets it span the column width.
            var le = LE(go);
            le.minHeight = RowHeight;
            le.flexibleWidth = 1;

            var label = UiToolkit.CreateText(go, "Label", new Vector2(10, 0),
                new Vector2(0, RowHeight), "", 15, TextAnchor.MiddleLeft,
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
