using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
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
        private CanvasScaler _scaler;   // kept so Refresh() can re-apply the aspect-adaptive match live

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
        // Parity soft-gate: the READY button's native controller + last-applied interactable state
        // (mirror of the Play button's repaint machinery — cloned native buttons repaint only via
        // SetInteractable, see RefreshPlayButtonVisual). -1 = not yet applied.
        private object _readyButtonCtrl;
        private System.Reflection.MethodInfo _readyButtonSetInteractable;
        private int _readyInteractableCache = -1;
        // True while the LOCAL player's own roster row carries parity diffs → READY locked.
        private bool _parityLocked;

        // ─── Full-screen 5-zone layout ─────────────────────────────────────
        private Text _inviteCodeValue;   // ONE unified invite code (click-to-copy), refreshed live
        private Text _railSaveValue;
        private Button _chooseSaveBtn;
        private Button _newCampaignBtn;
        private Button _inviteBtn;
        // Connect-rail SHARE + SAVE section roots (host-only): gated on/off per frame like _chooseSaveBtn.
        // The JOIN section (and its button) stay always-visible. Section roots own the header + body so
        // hiding the whole section (header + separator + controls) is a single SetActive.
        private GameObject _shareSection;
        private GameObject _saveSection;

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

        // JOIN button — lives in the connect rail's always-visible "JOIN A GAME" section (moved out of
        // the footer). Wired to OnLobbyJoinPrompt (unchanged native join path).
        private Button _joinButton;

        // Roster rows (player list). Pooled: one row GameObject per slot, reused across refreshes.
        private GameObject _rosterArea;
        private readonly List<RosterRow> _rows = new List<RosterRow>();
        // Pool index of the row currently rendering the LOCAL player ("you"), refreshed each frame in
        // RefreshRoster. The rename pencil's onClick re-checks against this so a pooled row reused for
        // another peer never renames the wrong one. -1 = local row not currently shown.
        private int _myRowIndex = -1;

        // Roster row height feeds LayoutElement.minHeight; theme-scaled so rows grow with UiScale.
        private static float RowHeight => LobbyTheme.ScaledRowHeight;

        private class RosterRow
        {
            public GameObject Go;
            public Text ReadyLabel;    // col 1: far-left fixed-width ready check ("✓" when ready, else blank)
            public Text NameLabel;     // col 2: bare nickname, left-aligned, fills remaining width
            public Button WarnBtn;     // col 3: parity warning badge ("!"); shown only on a mismatched row,
                                       //        click → exact diff list via the native message box
            public Button RenameBtn;   // col 4: far-right pencil; shown/interactable only on the local row
            public string ParityDiffs; // current row's diff text ("" = OK) — read by WarnBtn's onClick
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

            // Dedicated overlay Canvas (mirrors MultiplayerUI.MultiplayerBarCanvas EXACTLY):
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
            var canvasGo = new GameObject("MultiplayerLobbyCanvas");
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

            // Responsive: native aspect-adaptive scaler (ScaleWithScreenSize @1920×1080, match WIDTH
            // ≤16:9 / HEIGHT >16:9) replaces the old fixed match=0.5 so the page reflows cleanly across
            // 16:9 / 16:10 / 21:9 / 4:3. The match is recomputed live in Refresh() as the resolution
            // can change at runtime (windowed resize / monitor swap).
            _scaler = canvasGo.AddComponent<CanvasScaler>();
            LobbyTheme.ConfigureScaler(_scaler);

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

            _root = new GameObject("MultiplayerLobbyPanel");
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
            var pad = LobbyTheme.ScaledPadding;
            rootVlg.padding = new RectOffset(pad * 3, pad * 3, pad + pad / 2, pad + pad / 2);
            rootVlg.spacing = pad;
            rootVlg.childControlWidth = true;
            rootVlg.childControlHeight = true;
            rootVlg.childForceExpandWidth = true;
            rootVlg.childForceExpandHeight = false;
            rootVlg.childAlignment = TextAnchor.UpperCenter;

            // FULL-SCREEN PAGE (restyle Option A): a themed page background behind all zones so the
            // lobby reads as a dedicated PAGE, not a floating panel over the menu art. A dim navy
            // PageBackdrop fill + a native sliced sprite frame (via ApplyPanelSkin) anchored stretch
            // (the root already fills the 1920×1080 reference rect). The menu chrome is hidden on
            // Show() so this page owns the whole screen. The Image is added on _root, created before
            // its child zones, so it sits BEHIND them; the VerticalLayoutGroup ignores the root's own
            // Image (it only lays out CHILD rects), so this is layout-safe.
            var pageBg = _root.AddComponent<Image>();
            var pageFrame = _root.AddComponent<Outline>();
            LobbyTheme.ApplyPanelSkin(pageBg, pageFrame, LobbyTheme.PageBackdrop, LobbyTheme.HeaderBorder);

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

            var titleH = LobbyTheme.ScaledHeaderFontSize + LobbyTheme.ScaledPadding;
            var subH = LobbyTheme.ScaledSubFontSize + LobbyTheme.ScaledPadding / 2;

            var le = LE(bar);
            le.minHeight = titleH + subH + LobbyTheme.ScaledPadding;
            le.flexibleHeight = 0;

            var vlg = bar.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.spacing = 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var title = UiToolkit.CreateText(bar, "Title", Vector2.zero,
                new Vector2(540, titleH), "CO-OP LOBBY", LobbyTheme.ScaledHeaderFontSize,
                TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));
            title.color = LobbyTheme.Accent;   // amber page title (accent highlight)
            LE(title.gameObject).minHeight = titleH;

            _roleText = UiToolkit.CreateText(bar, "Subtitle", Vector2.zero,
                new Vector2(540, subH), "your lobby", LobbyTheme.ScaledSubFontSize,
                TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));
            _roleText.color = LobbyTheme.SubText;
            LE(_roleText.gameObject).minHeight = subH;
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
            hlg.spacing = LobbyTheme.ScaledPadding + LobbyTheme.ScaledPadding / 2;
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
        // LEFT COLUMN "Connect": three labeled sections stacked top-to-bottom, each a themed header +
        // thin separator + its controls. SHARE (host-only: STUN code to copy + Invite-via-Steam),
        // SAVE (host-only: chosen save value + Choose Save…), JOIN (always: Join a game…). Host-only
        // sections are gated on/off per frame in Refresh (mirror of the old _chooseSaveBtn gate); JOIN
        // is moved here out of the footer so the footer keeps only Leave / Ready / Play.
        private void BuildConnectRail(GameObject parent)
        {
            var rail = new GameObject("ConnectColumn");
            rail.transform.SetParent(parent.transform, false);
            rail.AddComponent<RectTransform>();
            AddFramedPanel(rail);

            LE(rail).flexibleWidth = 1;

            var pad = LobbyTheme.ScaledPadding;
            var vlg = rail.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad, pad);
            vlg.spacing = pad;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var labelH = LobbyTheme.ScaledSubFontSize + pad / 2;

            // ── SECTION "SHARE" (host-only): how others connect to you ──────────
            _shareSection = AddRailSection(rail, "SHARE");

            var stunLabel = UiToolkit.CreateText(_shareSection, "InviteLabel", Vector2.zero,
                new Vector2(260, labelH), "INVITE CODE (click to copy):",
                LobbyTheme.ScaledSubFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            stunLabel.color = LobbyTheme.SubText;
            LE(stunLabel.gameObject).minHeight = labelH;

            // ONE unified invite code: carries Steam id and/or public endpoint, so a friend pastes this
            // single string into JOIN A GAME and the client cascades Steam → hole-punch → direct. Value
            // refreshed live in Refresh() (self-heals as UPnP/STUN discovery + Steam become ready).
            _inviteCodeValue = MakeCopyableValue(_shareSection, "InviteValue", () => _owner.GetOwnUnifiedCode());
            LE(_inviteCodeValue.transform.parent.gameObject).minHeight = LobbyTheme.ScaledRowHeight;

            _inviteBtn = NativeWidgetFactory.CloneMenuButton(_shareSection.transform, "InviteBtn",
                "INVITE VIA STEAM", () => _owner.InvitePlayers());
            if (_inviteBtn != null)
                AddCloneLayoutElement(_inviteBtn, _shareSection.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _inviteBtn = UiToolkit.CreateButton(_shareSection, "InviteBtn", "INVITE VIA STEAM",
                    Vector2.zero, RailButtonSize, new Vector2(0f, 1f),
                    () => _owner.InvitePlayers());
                LE(_inviteBtn.gameObject).preferredHeight = RailButtonSize.y;
            }

            // ── SECTION "SAVE" (host-only): the campaign save to load ───────────
            _saveSection = AddRailSection(rail, "SESSION SAVE");

            _railSaveValue = UiToolkit.CreateText(_saveSection, "SaveValue", Vector2.zero,
                new Vector2(248, labelH * 2), "(none)", LobbyTheme.ScaledSubFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            LE(_railSaveValue.gameObject).minHeight = labelH * 2;

            _chooseSaveBtn = NativeWidgetFactory.CloneMenuButton(_saveSection.transform, "ChooseSaveBtn",
                "CHOOSE SAVE…", () => _owner.OnLobbyChooseSave());
            if (_chooseSaveBtn != null)
                AddCloneLayoutElement(_chooseSaveBtn, _saveSection.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _chooseSaveBtn = UiToolkit.CreateButton(_saveSection, "ChooseSaveBtn", "CHOOSE SAVE…",
                    Vector2.zero, RailButtonSize, new Vector2(0f, 1f),
                    () => _owner.OnLobbyChooseSave());
                LE(_chooseSaveBtn.gameObject).preferredHeight = RailButtonSize.y;
            }

            // P0 new-campaign bootstrap: start a FRESH campaign instead of picking a save (host runs
            // the native new-game flow; clients auto-join at the first geoscape frame). Lives in the
            // host-gated SAVE section, so it inherits the host-only visibility.
            _newCampaignBtn = NativeWidgetFactory.CloneMenuButton(_saveSection.transform, "NewCampaignBtn",
                "NEW CAMPAIGN…", () => _owner.OnLobbyNewCampaign());
            if (_newCampaignBtn != null)
                AddCloneLayoutElement(_newCampaignBtn, _saveSection.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _newCampaignBtn = UiToolkit.CreateButton(_saveSection, "NewCampaignBtn", "NEW CAMPAIGN…",
                    Vector2.zero, RailButtonSize, new Vector2(0f, 1f),
                    () => _owner.OnLobbyNewCampaign());
                LE(_newCampaignBtn.gameObject).preferredHeight = RailButtonSize.y;
            }

            // ── SECTION "JOIN" (always): join someone else's game ───────────────
            var joinSection = AddRailSection(rail, "JOIN A GAME");

            _joinButton = NativeWidgetFactory.CloneMenuButton(joinSection.transform, "JoinBtn",
                "JOIN A GAME…", () => _owner.OnLobbyJoinPrompt());
            if (_joinButton != null)
                AddCloneLayoutElement(_joinButton, joinSection.transform, RailButtonSize.x, RailButtonSize.y);
            else
            {
                _joinButton = UiToolkit.CreateButton(joinSection, "JoinBtn", "JOIN A GAME…",
                    Vector2.zero, RailButtonSize, new Vector2(0f, 1f),
                    () => _owner.OnLobbyJoinPrompt());
                LE(_joinButton.gameObject).preferredHeight = RailButtonSize.y;
            }
        }

        // Build a rail SECTION: a child GameObject (its own top-aligned VLG) holding an amber section
        // header + a thin themed separator line; controls are then added to the returned GameObject by
        // the caller. Returning the section root lets the caller SetActive the WHOLE section (header +
        // separator + controls) in one call for host-gating.
        private GameObject AddRailSection(GameObject rail, string title)
        {
            var pad = LobbyTheme.ScaledPadding;

            var section = new GameObject($"Section_{title}");
            section.transform.SetParent(rail.transform, false);
            section.AddComponent<RectTransform>();
            var svlg = section.AddComponent<VerticalLayoutGroup>();
            svlg.padding = new RectOffset(0, 0, 0, 0);
            svlg.spacing = pad / 2;
            svlg.childControlWidth = true;
            svlg.childControlHeight = true;
            svlg.childForceExpandWidth = true;
            svlg.childForceExpandHeight = false;
            svlg.childAlignment = TextAnchor.UpperLeft;

            var hdrH = LobbyTheme.ScaledSectionHeaderFontSize + pad / 2;
            var hdr = UiToolkit.CreateText(section, "Hdr", Vector2.zero,
                new Vector2(260, hdrH), title, LobbyTheme.ScaledSectionHeaderFontSize,
                TextAnchor.UpperLeft, new Vector2(0f, 1f));
            hdr.color = LobbyTheme.Accent;
            hdr.fontStyle = FontStyle.Bold;
            var hle = LE(hdr.gameObject); hle.minHeight = hdrH; hle.flexibleHeight = 0;

            // Thin separator line under the header (a themed flat Image at fixed height).
            var sep = new GameObject("Separator");
            sep.transform.SetParent(section.transform, false);
            sep.AddComponent<RectTransform>();
            var sepImg = sep.AddComponent<Image>();
            sepImg.color = LobbyTheme.Separator;
            var sepLe = LE(sep); sepLe.minHeight = LobbyTheme.ScaledSeparatorThickness; sepLe.flexibleHeight = 0;

            return section;
        }

        // Click-to-copy value text: a button whose label is the live value; click copies it.
        // Click-to-copy value text: a button whose label is the live value; click copies it. The
        // button rect is owned by the parent layout group (caller sets the LayoutElement.minHeight).
        private Text MakeCopyableValue(GameObject parent, string name, System.Func<string> getValue)
        {
            var btn = UiToolkit.CreateButton(parent, name, "", Vector2.zero,
                new Vector2(240, LobbyTheme.ScaledRowHeight),
                new Vector2(0f, 1f), () => _owner.CopyToClipboard(getValue()));
            var label = btn.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.alignment = TextAnchor.MiddleLeft;
                label.fontSize = LobbyTheme.ScaledRowFontSize;
                label.fontStyle = FontStyle.Bold;
                label.color = LobbyTheme.Accent;
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

            var pad = LobbyTheme.ScaledPadding;
            var vlg = chat.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad, pad);
            vlg.spacing = pad / 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var hdrH = LobbyTheme.ScaledBodyFontSize + pad / 2;
            var hdr = UiToolkit.CreateText(chat, "ChatHdr", Vector2.zero,
                new Vector2(300, hdrH), "CHAT", LobbyTheme.ScaledBodyFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            hdr.color = LobbyTheme.Accent;
            var hle = LE(hdr.gameObject); hle.minHeight = hdrH; hle.flexibleHeight = 0;

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
            var inputH = LobbyTheme.ScaledRowHeight;
            var irle = LE(inputRow); irle.minHeight = inputH; irle.flexibleHeight = 0;
            var irhlg = inputRow.AddComponent<HorizontalLayoutGroup>();
            irhlg.spacing = pad / 2;
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
                Vector2.zero, new Vector2(220, inputH), new Vector2(0f, 0f),
                _ => SendCurrentChatInput());
            LE(_chatInput.gameObject).flexibleWidth = 1;

            var sendW = LobbyTheme.Scale(90);
            var send = UiToolkit.CreateButton(inputRow, "ChatSend", "SEND",
                Vector2.zero, new Vector2(sendW, inputH), new Vector2(0f, 0f),
                SendCurrentChatInput);
            var sndle = LE(send.gameObject); sndle.preferredWidth = sendW; sndle.flexibleWidth = 0;
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

            var pad = LobbyTheme.ScaledPadding;
            var vlg = players.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad, pad);
            vlg.spacing = pad / 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var hdrH = LobbyTheme.ScaledBodyFontSize + pad / 2;
            _connectText = UiToolkit.CreateText(players, "PlayersHdr", Vector2.zero,
                new Vector2(300, hdrH), "PLAYERS (0)", LobbyTheme.ScaledBodyFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _connectText.color = LobbyTheme.Accent;
            var hle = LE(_connectText.gameObject); hle.minHeight = hdrH; hle.flexibleHeight = 0;

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

        // FOOTER: Leave (left) … Ready (client) / Play (host) (right), pushed apart by a flexible
        // Spacer. Fixed height (LE.minHeight, flexibleHeight 0); buttons laid out by a
        // HorizontalLayoutGroup so there is zero fractional-X math. (JOIN moved to the connect rail.)
        private void BuildFooter()
        {
            var footer = new GameObject("Footer");
            footer.transform.SetParent(_root.transform, false);
            footer.AddComponent<RectTransform>();
            var fle = LE(footer); fle.minHeight = FooterButtonSize.y; fle.flexibleHeight = 0;

            var hlg = footer.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = LobbyTheme.ScaledPadding / 2;
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
                    Vector2.zero, FooterButtonSize, new Vector2(0f, 0f),
                    () => _owner.OnLobbyLeave());
                LE(_leaveButton.gameObject).preferredWidth = FooterButtonSize.x;
            }

            // Flexible spacer pushes the remaining buttons to the right edge. (JOIN moved out of the
            // footer into the connect rail's "JOIN A GAME" section; the footer now carries only
            // Leave (left) … Ready / Play (right).)
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(footer.transform, false);
            spacer.AddComponent<RectTransform>();
            LE(spacer).flexibleWidth = 1;

            // Ready: a plain cloned MENU BUTTON acting as a toggle — same widget family as
            // Leave/Join/Play, which render cleanly in a LayoutElement slot. (The old approach cloned a
            // GameOptionViewController option-row whose internal layout + "NEEDS TEXT" placeholder leaked
            // OUTSIDE its wrapper; the menu button has no such stray children.) The button is CLIENT-ONLY —
            // OnLobbyToggleReady is a no-op for the host (the host is the starter, it has no Ready), so the
            // button is host-gated OFF (mirror of _playButton/_chooseSaveBtn, inverted). Authoritative
            // client ready state arrives via the roster and is re-synced into _localReady + the label in
            // RefreshControls. Created here, then frame-gated by host-state in RefreshControls.
            _readyButton = NativeWidgetFactory.CloneMenuButton(footer.transform, "ReadyBtn", "READY",
                () =>
                {
                    _localReady = !_localReady;        // optimistic flip for instant feedback
                    _owner.OnLobbyToggleReady(_localReady); // send the post-flip ready/unready intent
                    UpdateReadyButtonLabel();
                });
            if (_readyButton != null)
            {
                AddCloneLayoutElement(_readyButton, footer.transform, FooterButtonSize.x, FooterButtonSize.y);
                // Parity soft-gate: resolve the clone's native PhoenixGeneralButton controller ONCE so
                // the READY lock can repaint the greyed visual immediately (same machinery + reasons as
                // the Play button below — interactable alone doesn't repaint a cloned native button).
                var readyPgbType = HarmonyLib.AccessTools.TypeByName(
                    "PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton");
                if (readyPgbType != null)
                {
                    _readyButtonCtrl = _readyButton.GetComponentInParent(readyPgbType)
                        ?? _readyButton.GetComponentInChildren(readyPgbType);
                    if (_readyButtonCtrl != null)
                        _readyButtonSetInteractable =
                            HarmonyLib.AccessTools.Method(readyPgbType, "SetInteractable", new[] { typeof(bool) });
                }
            }
            else
            {
                _readyButton = UiToolkit.CreateButton(footer, "ReadyBtn", "READY",
                    Vector2.zero, FooterButtonSize, new Vector2(0f, 0f),
                    () =>
                    {
                        _localReady = !_localReady;
                        _owner.OnLobbyToggleReady(_localReady);
                        UpdateReadyButtonLabel();
                    });
                LE(_readyButton.gameObject).preferredWidth = FooterButtonSize.x;
            }
            _readyButtonLabel = _readyButton != null ? _readyButton.GetComponentInChildren<Text>() : null;
            UpdateReadyButtonLabel();

            // Client-only: hide the Ready button for the host at creation (it is meaningless for the
            // starter, and OnLobbyToggleReady no-ops for the host). Mirrors the host-only visibility of
            // _chooseSaveBtn/_playButton, inverted. RefreshControls keeps this in sync each frame.
            var engine = NetworkEngine.Instance;
            if (_readyButton != null)
            {
                var clientShow = engine == null || !engine.IsHost;
                if (_readyButton.gameObject.activeSelf != clientShow)
                    _readyButton.gameObject.SetActive(clientShow);
            }

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
                    Vector2.zero, FooterButtonSize, new Vector2(0f, 0f),
                    () => _owner.OnLobbyPlay());
                LE(_playButton.gameObject).preferredWidth = FooterButtonSize.x;
            }
        }

        // FRAMED PANEL: a themed card backing Image on the zone root (native sliced sprite frame via
        // LobbyTheme.ApplyPanelSkin when one is captured, else a flat CardBackground fill) + a themed
        // Outline border, so each zone reads as a discrete framed card on the page. The Image is added
        // on the zone root (created before its child widgets) so it sits behind them; the Outline is a
        // cheap themed frame with no extra GameObject.
        private static void AddFramedPanel(GameObject zone, Color fill, Color border)
        {
            var img = zone.AddComponent<Image>();
            var outline = zone.AddComponent<Outline>();
            LobbyTheme.ApplyPanelSkin(img, outline, fill, border);
        }

        // Default-coloured overload used by every zone (themed card fill + card border).
        private static void AddFramedPanel(GameObject zone)
        {
            AddFramedPanel(zone, LobbyTheme.CardBackground, LobbyTheme.CardBorder);
        }

        // Cloned-button sizing (theme-scaled). The native TemplateMenuButton clone is sized for the
        // full-width main-menu column, so dropped unmodified into a lobby zone it overflows; we
        // constrain every clone to a rect that fits its zone (growing with UiScale) and cap its label
        // font so the text stays inside that rect.
        private static Vector2 RailButtonSize => new Vector2(LobbyTheme.Scale(220), LobbyTheme.Scale(40));
        private static Vector2 FooterButtonSize => new Vector2(LobbyTheme.Scale(160), LobbyTheme.Scale(44));
        private static int ClonedButtonFontCap => LobbyTheme.ScaledClonedButtonFontCap;

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

            // Keep the aspect-adaptive scaler match live: the screen aspect can change at runtime
            // (windowed resize, monitor swap), so re-apply the native rule each frame the lobby is up.
            if (_scaler != null) _scaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;

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
            if (_inviteCodeValue != null) _inviteCodeValue.text = _owner.GetOwnUnifiedCode();
            if (_railSaveValue != null)
            {
                var n = engine.Session?.ChosenSaveName;
                var m = engine.Session?.ChosenSaveMeta;
                _railSaveValue.text = string.IsNullOrEmpty(n) ? "(none)"
                    : (string.IsNullOrEmpty(m) ? n : $"{n}\n{m}");
            }
            // Host-only rail sections (SHARE + SAVE): visible only to the host (clients only see JOIN).
            // Gating the whole section root hides its header + separator + controls together; mirrors
            // the old per-button gate. JOIN's section stays always-visible.
            if (_shareSection != null && _shareSection.activeSelf != engine.IsHost)
                _shareSection.SetActive(engine.IsHost);
            if (_saveSection != null && _saveSection.activeSelf != engine.IsHost)
                _saveSection.SetActive(engine.IsHost);

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
                var rowH = LobbyTheme.ScaledRowFontSize + 4;
                var t = UiToolkit.CreateText(_chatContent.gameObject, $"ChatRow{_chatRows.Count}",
                    Vector2.zero, new Vector2(0, rowH), "", LobbyTheme.ScaledRowFontSize,
                    TextAnchor.UpperLeft, new Vector2(0f, 1f));
                var le = LE(t.gameObject);
                le.minHeight = rowH;
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

            // Ready button: CLIENT-ONLY. Keep it host-gated OFF each frame (mirror of _playButton's
            // host-gate, inverted). On the host we must NOT re-show it and must NOT re-sync _localReady /
            // relabel it — the host's roster row is always Ready=false (HostReady), so re-syncing would
            // flip the optimistic click back and make the button flicker. The host has no Ready.
            if (_readyButton != null)
            {
                var clientShow = !engine.IsHost;
                if (_readyButton.gameObject.activeSelf != clientShow)
                    _readyButton.gameObject.SetActive(clientShow);
            }
            if (!engine.IsHost)
            {
                // Client path: re-sync the local flag from authoritative roster state (me.Ready) and
                // relabel the button. This overrides any optimistic click flip with the truth (e.g. after
                // a reconnect or if the host's view differs), without re-invoking OnLobbyToggleReady.
                _localReady = me != null && me.Ready;

                // Parity soft-gate READY LOCK: my own roster row carries host-computed diffs → grey the
                // button (label names the reason; the row's "!" badge click shows the exact diffs). The
                // host ALSO ignores a READY from a mismatched client (SetClientReady), so this lock is
                // UX only — authority stays host-side. Repaint via the native SetInteractable path on a
                // real transition only (cache guard), mirroring the Play button.
                _parityLocked = me != null
                    && !Multiplayer.Network.Parity.ParityComparer.ReadyAllowed(me.ParityDiffs);
                if (_readyButton != null)
                {
                    var interactable = !_parityLocked;
                    _readyButton.interactable = interactable;
                    var interactableNow = interactable ? 1 : 0;
                    if (interactableNow != _readyInteractableCache)
                    {
                        _readyInteractableCache = interactableNow;
                        if (_readyButtonCtrl != null && _readyButtonSetInteractable != null)
                        {
                            try { _readyButtonSetInteractable.Invoke(_readyButtonCtrl, new object[] { interactable }); }
                            catch (System.Exception e)
                            {
                                UnityEngine.Debug.LogError("[Multiplayer] Ready button repaint failed: " + e.Message);
                            }
                        }
                    }
                }
                UpdateReadyButtonLabel();
            }

            // Play button: host only, enabled ONLY when the FULL start gate is open. The visual reads
            // the SAME controller gate the press path uses (LobbyController.CanStart, via the single
            // MultiplayerUI projection) — clients>=1 && all clients ready && save chosen && HostLobby &&
            // !locked. So the button greys for host-alone, any client un-ready, NO save chosen, and once
            // the lobby locks on start, exactly matching OnLobbyPlay (no second, drifting rule = Bug B).
            // Cloned native buttons self-manage their disabled visuals from Button.interactable
            // (UIInteractableColorController/Animator on the prefab), so gate via interactable.
            if (_playButton != null)
            {
                var playable = engine.IsHost && (MultiplayerUI.Instance?.EvaluateStartGate() ?? false);

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
                    UnityEngine.Debug.LogError("[Multiplayer] Play button repaint failed: " + e.Message);
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
            // Parity soft-gate: the locked button itself names the reason (the row's "!" badge click
            // shows the exact diff list).
            _readyButtonLabel.text = _parityLocked ? "MODS MISMATCH"
                : _localReady ? "✓ READY" : "READY";
        }

        // Rebuild/refresh the player rows from the unified lobby roster (host self-entry + clients).
        private List<PeerListEntry> RefreshRoster(NetworkEngine engine)
        {
            var roster = engine.Session?.GetLobbyRoster() ?? new List<PeerListEntry>();
            var localGuid = ClientIdentity.PlayerGuid;

            // (B) ORDER for display: HOST first, then the LOCAL player ("you"), then everyone else in
            // their existing wire order. Render-only reorder — SessionManager / the wire order is left
            // untouched. The local match uses the SAME isMe test as the rename gate (host→IsHost,
            // client→PlayerGuid). When you ARE the host, host==you, so this collapses to host-then-others.
            roster = OrderForDisplay(roster, engine, localGuid);

            // Grow the row pool to match.
            while (_rows.Count < roster.Count)
                _rows.Add(CreateRow(_rows.Count));

            // Track which pool slot now renders the local player so the rename pencil can re-check
            // ownership on click (reset first; set when we find the local row below).
            _myRowIndex = -1;

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
                if (isMe) _myRowIndex = i;

                // COL 2 — NICKNAME only (no "(you)" tag, no role text). The local row is identified by
                // its rename pencil, so no extra marker is needed.
                row.NameLabel.text = name;
                row.NameLabel.color = LobbyTheme.BodyText;

                // COL 1 — READY CHECK: green "✓" when a client is ready, blank otherwise. The host has no
                // ready state (it starts the game; HostReady is always false) → always blank.
                var ready = !p.IsHost && p.Ready;
                row.ReadyLabel.text = ready ? "✓" : "";
                row.ReadyLabel.color = LobbyTheme.ReadyText;

                // During an active save transfer, tint the check blue as a subtle download cue (the
                // green-✓-when-ready remains the primary signal).
                var st = engine.SaveTransfer;
                if (st != null && st.TransferActive)
                {
                    var progress = ProgressFor(engine, st, p, isMe);
                    if (progress != null)
                    {
                        row.ReadyLabel.text = "✓";
                        row.ReadyLabel.color = new Color(0.7f, 0.8f, 1f);
                    }
                }

                // PARITY badge: shown only when this peer's row carries diffs (host sees it on the
                // mismatched client's row; the client sees it on its OWN row). Click → exact diff list.
                row.ParityDiffs = p.ParityDiffs ?? "";
                if (row.WarnBtn != null)
                {
                    var warn = row.ParityDiffs.Length > 0;
                    if (row.WarnBtn.gameObject.activeSelf != warn)
                        row.WarnBtn.gameObject.SetActive(warn);
                }

                // RENAME pencil: shown + interactable ONLY on the local player's own row (the onClick
                // also re-checks _myRowIndex, so a pooled row can never rename the wrong peer).
                if (row.RenameBtn != null)
                {
                    if (row.RenameBtn.gameObject.activeSelf != isMe)
                        row.RenameBtn.gameObject.SetActive(isMe);
                    row.RenameBtn.interactable = isMe;
                }
            }

            return roster;
        }

        // Render-only reorder: HOST first, then the LOCAL player ("you"), then the rest in their original
        // order. Builds a NEW list (the source list from GetLobbyRoster is a fresh copy, but we don't
        // mutate it to keep this side-effect free). isMe matches the rename/own-row gate exactly.
        private static List<PeerListEntry> OrderForDisplay(List<PeerListEntry> roster, NetworkEngine engine, System.Guid localGuid)
        {
            if (roster == null || roster.Count <= 1) return roster ?? new List<PeerListEntry>();

            var ordered = new List<PeerListEntry>(roster.Count);
            PeerListEntry host = null;
            PeerListEntry me = null;

            foreach (var p in roster)
                if (p.IsHost) { host = p; break; }

            foreach (var p in roster)
            {
                var isMe = engine.IsHost ? p.IsHost : p.PlayerGuid == localGuid;
                if (isMe) { me = p; break; }
            }

            if (host != null) ordered.Add(host);
            if (me != null && !ReferenceEquals(me, host)) ordered.Add(me);
            foreach (var p in roster)
                if (!ReferenceEquals(p, host) && !ReferenceEquals(p, me))
                    ordered.Add(p);

            return ordered;
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

        // Build ONE structured roster row: a fixed-width HLG container holding, left→right,
        // (1) a fixed-width NAME label, (2) a flexible spacer, (3) a fixed-width themed STATUS label,
        // (4) a far-right square rename PENCIL button (shown/interactable only on the local row).
        // The row container fills the roster column (flexibleWidth 1) so every row is identical width;
        // the inner fixed widths keep the name column and the status/pencil cluster aligned across rows.
        // Build ONE structured roster row: a fixed-width HLG container holding, left→right,
        // (1) a fixed-width NAME label, (2) a flexible spacer, (3) a fixed-width themed STATUS label,
        // (4) a far-right square rename PENCIL button (shown/interactable only on the local row).
        // The row container fills the roster column (flexibleWidth 1) so every row is identical width;
        // the inner fixed widths keep the name column and the status/pencil cluster aligned across rows.
        private RosterRow CreateRow(int index)
        {
            var go = new GameObject($"Row{index}");
            go.transform.SetParent(_rosterArea.transform, false);
            go.AddComponent<RectTransform>();
            // The RosterContent VerticalLayoutGroup positions/sizes the row; LE feeds its min height and
            // lets it span the full column width (uniform row width across the pool).
            var le = LE(go);
            le.minHeight = RowHeight;
            le.flexibleWidth = 1;

            var pad = LobbyTheme.ScaledPadding;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = pad / 2;
            hlg.padding = new RectOffset(pad / 2, pad / 2, 0, 0);
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            var icon = LobbyTheme.ScaledIconButtonSize;

            // (1) READY CHECK — far-left fixed-width column. Shows a green "✓" when the player is ready,
            // blank otherwise (host has no ready state → blank). Text + color set per-frame in RefreshRoster.
            var readyLabel = UiToolkit.CreateText(go, "Ready", Vector2.zero,
                new Vector2(icon, RowHeight), "",
                LobbyTheme.ScaledRowFontSize, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
            var rdle = LE(readyLabel.gameObject);
            rdle.preferredWidth = icon;
            rdle.flexibleWidth = 0;

            // (2) NICKNAME — bare player name, left-aligned, fills the remaining width so the pencil sits
            // hard against the right edge. Color is the themed body color (set per-frame in RefreshRoster).
            var nameLabel = UiToolkit.CreateText(go, "Name", Vector2.zero,
                new Vector2(LobbyTheme.ScaledRosterNameWidth, RowHeight), "",
                LobbyTheme.ScaledRowFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            var nle = LE(nameLabel.gameObject);
            nle.preferredWidth = LobbyTheme.ScaledRosterNameWidth;
            nle.flexibleWidth = 1;

            // (3) PARITY warning badge — a small native-cloned button ("!", warning tint), hidden unless
            // the row's peer has parity diffs (toggled per-frame in RefreshRoster, same pattern as the
            // pencil). Click → the EXACT host-computed diff list via the native message box (the lobby
            // has no hover-tooltip mechanism; the message box is this mod's canonical detail surface).
            // Reads the CURRENT pool row's diffs by index so a reused row never shows stale text.
            System.Action showDiffs = () =>
            {
                if (index < _rows.Count) _owner.OnLobbyShowParityDiffs(_rows[index].ParityDiffs);
            };
            var warnBtn = NativeWidgetFactory.CloneMenuButton(go.transform, "ParityWarnBtn", "!",
                () => showDiffs());
            if (warnBtn != null)
                AddCloneLayoutElement(warnBtn, go.transform, icon, icon);
            else
            {
                warnBtn = UiToolkit.CreateButton(go, "ParityWarnBtn", "!",
                    Vector2.zero, new Vector2(icon, icon), new Vector2(1f, 0.5f),
                    () => showDiffs());
                var wle = LE(warnBtn.gameObject);
                wle.preferredWidth = icon; wle.preferredHeight = icon; wle.flexibleWidth = 0;
            }
            // Warning tint on the badge glyph so it reads as "attention", not a normal action button.
            var warnTxt = warnBtn.GetComponentInChildren<Text>();
            if (warnTxt != null) warnTxt.color = new Color(1f, 0.75f, 0.2f);
            warnBtn.gameObject.SetActive(false); // hidden until RefreshRoster sees diffs

            // (4) RENAME pencil — a small native-cloned button on the far right. The onClick re-checks
            // live ownership (this pool slot must currently render the local player, tracked in
            // _myRowIndex) so a pooled row reused for another peer can never rename the wrong one; the
            // button is also hidden/disabled for non-local rows each frame in RefreshRoster. Reuse the
            // EXACT existing rename path (OnLobbyRenamePrompt → native prompt → SendRename), untouched.
            var renameBtn = NativeWidgetFactory.CloneMenuButton(go.transform, "RenameBtn", "✎",
                () => { if (index == _myRowIndex) _owner.OnLobbyRenamePrompt(); });
            if (renameBtn != null)
                AddCloneLayoutElement(renameBtn, go.transform, icon, icon);
            else
            {
                renameBtn = UiToolkit.CreateButton(go, "RenameBtn", "✎",
                    Vector2.zero, new Vector2(icon, icon), new Vector2(1f, 0.5f),
                    () => { if (index == _myRowIndex) _owner.OnLobbyRenamePrompt(); });
                var rle = LE(renameBtn.gameObject);
                rle.preferredWidth = icon; rle.preferredHeight = icon; rle.flexibleWidth = 0;
            }

            return new RosterRow
            {
                Go = go, ReadyLabel = readyLabel, NameLabel = nameLabel,
                WarnBtn = warnBtn, RenameBtn = renameBtn, ParityDiffs = ""
            };
        }
    }
}
