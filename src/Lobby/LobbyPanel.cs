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
        private Text _connectText;

        // Interactive controls. Rename moved to the native prompt (own-row click → OnLobbyRenamePrompt),
        // so there is no from-code nickname field in the full-screen tree.
        private Button _leaveButton;
        private Button _readyButton;
        private Text _readyButtonLabel;
        // Local optimistic ready flag — flipped on click for instant label feedback, then re-synced
        // from authoritative roster state (me.Ready) in RefreshControls.
        private bool _localReady;
        // THE FOOTER'S CENTRE: what campaign this lobby is queued on. Same sentence on the host and on
        // every client, off the same authoritative Session.ChosenSaveName — nobody should have to ask
        // which save they are about to be pulled into.
        private Text _queuedCampaign;
        // The READY button's native PhoenixGeneralButton controller + last-applied interactable state
        // (resolved via reflection — the mod can't reference the type). Its Update() repaints ONLY when
        // its own IsEnabled/VisualHovered/VisualPressed change, NOT when BaseButton.interactable changes,
        // so writing interactable alone leaves the button stale (greyed) until a real pointer-enter.
        // SetInteractable(bool) is the complete native "appear now" path: it sets BaseButton.interactable,
        // flips IsEnabled, runs the animator and calls UpdateColorElements() — no pointer needed.
        // object/MethodInfo because the type isn't referenceable at compile time. -1 = not yet applied.
        private object _readyButtonCtrl;
        private System.Reflection.MethodInfo _readyButtonSetInteractable;
        private int _readyInteractableCache = -1;
        // Same three, for NEW CAMPAIGN — it greys on the lobby readiness gate exactly as PLAY does
        // (owner ruling 2026-08-07). It reads PeersReady rather than CanStart because a fresh campaign
        // picks no save, so the chosen-save half of the start gate must not apply to it.
        private object _newCampaignCtrl;
        private System.Reflection.MethodInfo _newCampaignSetInteractable;
        private int _newCampaignInteractableCache = -1;
        // True while the LOCAL player's own roster row carries parity diffs → READY locked.
        private bool _parityLocked;
        // True on a HOST with no chosen save → READY locked (there is nothing to be ready for, and
        // SessionManager.SetHostReady refuses it anyway).
        private bool _needsSave;

        // ─── Full-screen 5-zone layout ─────────────────────────────────────
        private Text _inviteCodeValue;   // ONE unified invite code (click-to-copy), refreshed live
        // THE HOST'S ADDRESS LINES — one per local IPv4 plus the UPnP endpoint, each click-to-copy.
        // Host-only (the whole group is one SetActive) and repainted only when the underlying list
        // actually changes; see RefreshAddresses for the two reference compares that decide that.
        private GameObject _addressGroup;
        private readonly List<AddressLine> _addressLines = new List<AddressLine>();
        private object _addressesShownFor;   // the LanIpResolver list instance last painted
        private object _upnpShownFor;        // the MappedEndpoint instance last painted
        private Button _newCampaignBtn;
        // HOST-ONLY CELLS, each toggled by one SetActive per frame. They hold the CLONE ROOT, not the
        // returned Button: a cloned native button is nested inside its clone root, so deactivating the
        // inner Button alone would leave the clone's frame still drawn (see CellRoot).
        private GameObject _inviteBtnCell;      // INVITE VIA STEAM — opens the HOST's Steam lobby overlay
        private GameObject _chooseSaveCell;     // footer: CHOOSE SAVE…
        private GameObject _newCampaignCell;    // footer: NEW CAMPAIGN…

        // INVITE VIA STEAM, GREYED WHEN THERE IS NO STEAM TO INVITE THROUGH — the same three-field shape
        // as READY and NEW CAMPAIGN above, for the same reason: a cloned native button repaints its
        // greyed visual ONLY through its PhoenixGeneralButton controller's SetInteractable, never off
        // Button.interactable alone. Writing interactable by itself leaves IsEnabled true, so the
        // controller's own Update → UpdateColorElements repaints the label at the first pointer-enter and
        // wipes any colour we wrote — a hover would have un-greyed the button and, because this applies
        // on change only, the grey would never have come back.
        // _inviteBtnLabel is the FALLBACK path's visual only (no clone → no controller → nothing else
        // shows the state; the from-code button's label colour is CreateButton's BodyText, not a prefab's,
        // so it needs no capture). -1 = not yet applied.
        private Button _inviteBtn;
        private Text _inviteBtnLabel;
        private object _inviteBtnCtrl;
        private System.Reflection.MethodInfo _inviteBtnSetInteractable;
        private int _inviteInteractableCache = -1;

        // COPY ACKNOWLEDGEMENT — the clicked value cell, what it really said, and when it goes back.
        // ONE cell at a time: two acks would need two timers and could leave one cell green forever.
        // The clock is Time.realtimeSinceStartup, the same one LobbyCountdown counts its display down
        // from — unscaled, so an ack does not freeze with the game.
        private const float CopiedAckSeconds = 1.5f;   // seconds, not pixels: nothing to Scale()
        private const string CopiedLabel = "COPIED";   // shorter than any real value, so it cannot overrun
        private Text _copiedCell;
        private string _copiedText;
        private Color _copiedColor;
        private float _copiedUntil;

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

        // THERE IS NO JOIN BUTTON IN THE LOBBY. It moved footer → rail → session card and has now left
        // entirely: joining is its own screen behind the gate (NetworkGatePanel), and a JOIN control
        // inside a session you are already in is an offer to abandon it, drawn on the card whose whole
        // job is bringing other people in.

        // Roster rows (player list). Pooled: one row GameObject per slot, reused across refreshes.
        private GameObject _rosterArea;
        private readonly List<RosterRow> _rows = new List<RosterRow>();

        // SELF CARD — the local player, drawn ABOVE the "PLAYERS (n)" header instead of inside the list.
        // It owns the rename control outright, which is why no list row carries a pencil any more: the
        // card is ALWAYS the local player, so the "am I renaming the right peer?" question that the old
        // pooled-row pencil had to re-ask on every click cannot be asked wrong here.
        private GameObject _selfCard;
        private Text _selfName;
        private Text _selfHostChip;
        private Text _selfStatus;      // local transfer/load line; hidden when there is nothing to say

        // Last player count written into the header. Refresh runs every frame and the count moves a
        // handful of times per lobby, so the interpolation is done on change, not per frame.
        private int _rosterCountShown = -1;

        // Roster row height feeds LayoutElement.minHeight; theme-scaled so rows grow with UiScale.
        private static float RowHeight => LobbyTheme.ScaledRowHeight;

        // Fully transparent — the roster row's tint plate when the row is neither the host's nor tinted.
        private static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);

        /// <summary>Host marker, drawn left of the host's nickname — <see cref="LobbyTheme.HostCrown"/>,
        /// which ASKS the captured font whether it has the crown and hands back a short ASCII label when it
        /// does not (a missing glyph renders blank, with no fallback of Unity's own). The host row also
        /// carries a VISIBLE tint (<see cref="LobbyTheme.HostRowTint"/>), so no information lives only in a
        /// glyph even if the probe is somehow wrong.</summary>
        private static string HostCrown => LobbyTheme.HostCrown;

        private class RosterRow
        {
            public GameObject Go;
            public Image Tint;         // row backing plate: lit for the host's row, clear otherwise
            public Text Crown;         // col 1: host crown, blank on every other row
            public Text NameLabel;     // col 2: nickname — FLEXES, so a long one stops clipping
            public Text StatusChip;    // col 3: the status IN WORDS ("READY" / "SEAT HELD" / progress)
            public Button WarnBtn;     // col 4: parity badge ("MODS ≠"); click → exact diff list
            public Image[] Bars;       // col 5: the tactical panel's 4-bar meter, reused
            public Text PingText;      // col 5: "N ms", or an em-dash when there is no sample
            public string ParityDiffs; // current row's diff text ("" = OK) — read by WarnBtn's onClick
            public int PingShown;      // last ms painted; the "N ms" string is rebuilt only on a change
            // "Player <id>" for a peer that has not named itself. Cached exactly like PingShown and for
            // the same reason: PaintRow runs every frame and the concat is otherwise a per-frame alloc
            // for a string that only changes when the row is reused for a different peer.
            public ulong FallbackNameFor;
            public string FallbackName;
        }

        // Same cache for the self card's own unnamed-peer label (RefreshSelfCard also runs every frame).
        private ulong _selfFallbackFor;
        private string _selfFallbackName;

        /// <summary>"Player &lt;id&gt;" for a peer with no nickname, allocated once per (row, peer).</summary>
        private static string FallbackName(ulong steamId, ref ulong cachedFor, ref string cached)
        {
            if (cached == null || cachedFor != steamId)
            {
                cachedFor = steamId;
                cached = "Player " + steamId;
            }
            return cached;
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

            // ROOT layout: a VerticalLayoutGroup stacks MainRow / Footer top-to-bottom and owns all
            // vertical placement (replaces the old per-zone anchoredPosition Y math). Padding
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

        // THERE IS NO TOP BAR. It held a static "CO-OP LOBBY" title and one subtitle that said either the
        // player's role ("your lobby" / "joined lobby") or the chosen save — and every word of that is
        // said better elsewhere: the save by the footer's QUEUED line (off the same authoritative
        // Session.ChosenSaveName), the role by the self card's HOST chip and the roster's crowned host
        // row. A framed card ~100px tall repeating what two other surfaces already say is exactly the
        // vertical space the roster and the chat wanted.

        // MAIN ROW: TWO columns, 3:2. LEFT (PlayersColumn, 60%) is the self card and the roster — the
        // thing players actually read, so it gets the width. RIGHT (RightColumn, 40%) stacks a compact
        // SESSION card over the chat. Takes ALL leftover vertical space (LE.flexibleHeight 1) so the row
        // stretches to fill everything above the fixed-height Footer.
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

            BuildRosterZone(mainRow);
            BuildRightColumn(mainRow);
        }

        /// <summary>
        /// A MAIN-ROW COLUMN'S SHARE OF THE ROW — and the reason it is THREE writes, not one.
        ///
        /// flexibleWidth alone does not give a ratio. HorizontalOrVerticalLayoutGroup hands every child
        /// its own min/preferred width FIRST and divides only the SURPLUS by flexibility, and a column's
        /// min/preferred is whatever its widest descendant reports (LayoutUtility.GetPreferredWidth even
        /// floors preferred at min). So the longest label inside a column silently bought that column
        /// extra width, and the actual split was a number nobody chose and nobody could read off the
        /// weights. Pinning min AND preferred to 0 makes a column contribute nothing to the pre-share
        /// pass, so the row divides EXACTLY by the weights.
        ///
        /// The price: a column no longer pushes back when its own content will not fit. That is paid for
        /// by hand — every fixed cell inside a column carries a real minWidth and each column's summed
        /// content minimum is checked against its share (see the per-row notes below).
        /// </summary>
        private static void PinColumnWidth(GameObject column, float weight)
        {
            var le = LE(column);
            le.minWidth = 0;
            le.preferredWidth = 0;
            le.flexibleWidth = weight;
        }

        // RIGHT COLUMN (40%): NOT a card of its own — a bare vertical container that stacks the compact
        // SESSION card (fixed height, sized by its own rows) over the chat card (flexibleHeight 1, so
        // the chat takes every pixel the session card leaves). The frame belongs to the two cards
        // inside; a third frame around them would just be a box around two boxes.
        private void BuildRightColumn(GameObject parent)
        {
            var col = new GameObject("RightColumn");
            col.transform.SetParent(parent.transform, false);
            col.AddComponent<RectTransform>();

            PinColumnWidth(col, 2f);

            var vlg = col.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = LobbyTheme.ScaledPadding;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            BuildSessionCard(col);
            BuildChatZone(col);
        }

        // SESSION CARD — EVERY WAY INTO THIS SESSION, listed in the order a player would try them:
        //
        //   1. INVITE VIA STEAM + THE INVITE CODE — one row: the button that needs no address at all,
        //      and beside it the copyable code (visible to clients too).
        //   2. THE ADDRESSES, host-only, one per line and each copyable: every local IPv4 the machine
        //      answers on (so the Radmin / ZeroTier / Hamachi adapter is listed beside the ordinary LAN
        //      one, labelled with the interface that carries it), then the UPnP-forwarded internet
        //      endpoint when the router actually opened one.
        //
        // EVERY ROW IS KEY-LEFT / VALUE-RIGHT, and every value is the click-to-copy cell (the header says
        // so once, so no row repeats "click to copy"). Values are LobbyTheme.ValueText, keys are SubText.
        //
        // THE BUTTON SHARING A ROW WITH THE CODE IS THE CASE THAT ONCE BROKE, so keep its two rules. The
        // card interior is 658 logical px at UiScale 1.4 and this row's summed minimum is 501 (button 280
        // + gap 11 + code cell 210), so nothing is crushed to zero — what overran the first time was the
        // cloned native button's LABEL, which keeps the prefab's own rect (sized for the full-width
        // main-menu column) no matter how small the cell around it gets, and no Text clips itself.
        // (a) AddCloneLayoutElement pins minWidth AND preferredWidth on the clone root (preferredWidth
        // alone leaves the effective minimum at ZERO and the cell collapses) and CLIPS the clone root
        // with a RectMask2D. The mask is the load-bearing half, not CapCloneFont: best-fit and Wrap are
        // measured against the LABEL's own RectTransform, and that rect is the prefab's — it does not
        // shrink when the clone root does (CloneMenuButton Instantiates with no rect normalisation), so
        // both settle on a size that fits a rect far wider than the cell. Only the mask actually stops
        // the glyphs at the cell edge. Delete it and INVITE VIA STEAM paints over the code again.
        // (b) the code cell carries its own minWidth + flexibleWidth 1 + MakeCopyableValue's RectMask2D,
        // so it takes the slack and clips its own text instead of reaching into the button.
        //
        // THERE IS NO JOIN BUTTON HERE ANY MORE. Joining lives behind the gate now (NetworkGatePanel's
        // join screen, reached from NETWORK GAME → JOIN SESSION); a "JOIN A GAME…" button inside a
        // session you are already in offered to tear that session down from the card whose entire job is
        // getting other people into it. Its prompt (MultiplayerUI.OnLobbyJoinPrompt) went with it.
        //
        // WHO SEES WHAT. The invite key is for EVERYONE — a client that cannot read the key its own
        // session is reachable at has nothing to forward to a friend, which is the whole reason the
        // host publishes it (SessionManager.HostInviteCode). Only the ADDRESS lines are gated: they are
        // the host machine's own addresses and mean nothing said by a client.
        private void BuildSessionCard(GameObject parent)
        {
            var pad = LobbyTheme.ScaledPadding;

            var card = new GameObject("SessionCard");
            card.transform.SetParent(parent.transform, false);
            card.AddComponent<RectTransform>();
            AddFramedPanel(card);

            // No minHeight: the card's own VerticalLayoutGroup already reports the exact preferred height
            // of its rows, and a hand-written minHeight would be a second number to keep in step with
            // them. flexibleHeight 0 is the whole of "do not stretch"; the chat below absorbs the slack.
            LE(card).flexibleHeight = 0;

            var vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad / 2, pad / 2);
            vlg.spacing = pad / 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            // ONE header + separator for the whole card, where there used to be three — and the header
            // row carries the ONE "click to copy" hint for every value cell below it, which is what let
            // the per-section labels go. Row, not a bare Text, so the hint can sit at the right edge.
            var hdrH = LobbyTheme.ScaledSectionHeaderFontSize + pad / 2;
            var hdrRow = AddCardRow(card, "HeaderRow");
            var hdrRowLe = LE(hdrRow); hdrRowLe.minHeight = hdrH; hdrRowLe.flexibleHeight = 0;

            var hdr = UiToolkit.CreateText(hdrRow, "Hdr", Vector2.zero,
                new Vector2(LobbyTheme.Scale(200), hdrH), "SESSION",
                LobbyTheme.ScaledSectionHeaderFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            hdr.color = LobbyTheme.Accent;
            hdr.fontStyle = FontStyle.Bold;
            // The title takes the slack (flex 1) so the hint is pushed to the right edge whatever the
            // card's width; both cells still carry a real minWidth, never preferredWidth alone.
            var hle = LE(hdr.gameObject);
            hle.minWidth = LobbyTheme.Scale(120); hle.preferredWidth = LobbyTheme.Scale(120);
            hle.flexibleWidth = 1;

            var hint = UiToolkit.CreateText(hdrRow, "CopyHint", Vector2.zero,
                new Vector2(LobbyTheme.Scale(120), hdrH), "click to copy",
                LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleRight, new Vector2(1f, 0.5f));
            hint.color = LobbyTheme.SubText;
            var hintLe = LE(hint.gameObject);
            hintLe.minWidth = LobbyTheme.Scale(120); hintLe.preferredWidth = LobbyTheme.Scale(120);
            hintLe.flexibleWidth = 0;

            var sep = new GameObject("Separator");
            sep.transform.SetParent(card.transform, false);
            sep.AddComponent<RectTransform>();
            sep.AddComponent<Image>().color = LobbyTheme.Separator;
            var sepLe = LE(sep);
            sepLe.minHeight = LobbyTheme.ScaledSeparatorThickness;
            sepLe.flexibleHeight = 0;

            // ── 1. INVITE VIA STEAM + THE INVITE CODE (everyone), one row ───────
            // Steam allows an explicit overlay invite from any lobby MEMBER — friends-only governs who
            // may BROWSE a lobby, not who may invite into it — and a client that accepted an invite is a
            // member of exactly that lobby (SteamInvite.JoinLobby keeps the handle). So a joiner can pull
            // their own friends in instead of asking the host to. OpenInviteOverlay says so out loud when
            // neither a published nor a joined lobby exists yet.
            var inviteRow = AddCardRow(card, "InviteRow");
            // AddCardRow's floor is the BUTTON height, but the code cell beside it is a ROW-height cell,
            // and a LayoutElement outranks its own layout group (priority 1 vs 0) — the row's 56 would
            // win over the group's honest 62 and the code's plate would bleed past the row. Raise the
            // floor to the tallest thing actually in the row.
            LE(inviteRow).minHeight = LobbyTheme.ScaledRowHeight;

            // The button is a FIXED cell: AddCardButton pins minWidth AND preferredWidth on both paths
            // (clone root via AddCloneLayoutElement, from-code fallback inline) and caps/best-fits the
            // clone's label — the two halves of the fix that let this row exist again.
            var inviteBtn = AddCardButton(inviteRow, "InviteBtn", "INVITE VIA STEAM",
                SessionButtonSize, () => _owner.InvitePlayers());
            _inviteBtnCell = CellRoot(inviteBtn, inviteRow.transform);

            // Handles for the greying (Refresh drives it). Same one-time controller resolution the READY
            // and NEW CAMPAIGN buttons do — resolved from the CELL, not from the returned Button, since
            // on the clone path the Button is nested inside the clone root and the controller sits at or
            // above it. With a controller, SetInteractable owns the whole visual; without one (the
            // from-code fallback) the label is all there is to grey.
            _inviteBtn = inviteBtn;
            _inviteBtnLabel = _inviteBtnCell != null ? _inviteBtnCell.GetComponentInChildren<Text>(true) : null;
            if (inviteBtn != null)
            {
                var invPgb = HarmonyLib.AccessTools.TypeByName(
                    "PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton");
                if (invPgb != null)
                {
                    _inviteBtnCtrl = inviteBtn.GetComponentInParent(invPgb)
                        ?? inviteBtn.GetComponentInChildren(invPgb);
                    if (_inviteBtnCtrl != null)
                        _inviteBtnSetInteractable =
                            HarmonyLib.AccessTools.Method(invPgb, "SetInteractable", new[] { typeof(bool) });
                }
            }

            // THE INVITE CODE, right-aligned in the slack the button leaves: the host's PUBLIC ENDPOINT,
            // 10 symbols, and never a Steam id — the button to its left IS the Steam channel, and a Steam
            // id in the code would be useless to the non-Steam player a code gets shared with
            // (MultiplayerUI.GetSessionInviteCode says why). Value refreshed live in Refresh(), so it
            // self-heals as UPnP/STUN discovery completes.
            //
            // It is the cell allowed to give up width — hence the flex, and hence the clip
            // (MakeCopyableValue's RectMask2D). A truncated code is still a correct COPY, because the
            // click copies the live value, not the drawn glyphs.
            // The COPY getter, not the display one: it returns "" while the cell is showing a placeholder
            // ("discovering…" and friends), which is what keeps the click silent instead of putting a
            // status line on the clipboard and saying COPIED over it. Refresh still DRAWS the full string.
            _inviteCodeValue = MakeCopyableValue(inviteRow, "InviteValue",
                                                 () => _owner.GetCopyableSessionInviteCode());
            _inviteCodeValue.alignment = TextAnchor.MiddleRight;
            var codeW = LobbyTheme.Scale(150);
            var codeLe = LE(_inviteCodeValue.transform.parent.gameObject);
            codeLe.minHeight = LobbyTheme.ScaledRowHeight;
            codeLe.minWidth = codeW;
            codeLe.preferredWidth = codeW;
            codeLe.flexibleWidth = 1;

            // ── 2. THE ADDRESSES (HOST only), one per line, each copyable ───────
            // Every local IPv4 with the adapter that carries it, then the UPnP-forwarded internet
            // endpoint — each one a key-left / value-right row like the one above (built on demand in
            // RefreshAddresses; this is just the box they land in). The whole group is one SetActive, so
            // a client never sees the host's addresses. No section label: the header's hint covers it.
            _addressGroup = AddCardGroup(card, "AddressGroup");
            _addressGroup.SetActive(false);   // shown per frame in Refresh, host only
        }

        // A vertical GROUP inside the session card: a plain VLG container holding a label and the row of
        // controls under it. Its whole subtree is one SetActive — that is what keeps host-gating a single
        // toggle now that the titled section roots are gone.
        private static GameObject AddCardGroup(GameObject card, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(card.transform, false);
            go.AddComponent<RectTransform>();
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = LobbyTheme.ScaledPadding / 4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;
            return go;
        }

        // A horizontal ROW of controls inside the session card. Fixed height (the buttons it holds are a
        // fixed height), so the card's own preferred height is the sum of its rows and nothing stretches.
        private static GameObject AddCardRow(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var le = LE(go); le.minHeight = SessionButtonSize.y; le.flexibleHeight = 0;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = LobbyTheme.ScaledPadding / 2;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            return go;
        }

        // One card/footer button: the cloned native menu button when one can be cloned, else the
        // from-code fallback — and either way a FIXED cell. minWidth as well as preferredWidth, the same
        // discipline every roster-row cell carries: preferredWidth alone leaves a LayoutElement's
        // effective minimum at ZERO, so the first sibling whose preferred width overran the row squeezed
        // the button to nothing while the text that overran it kept drawing. (The clone path already
        // pins both, in AddCloneLayoutElement; the fallback path did not, and now does.)
        /// (internal, not private: <see cref="NetworkGatePanel"/> builds its buttons through this same
        /// helper rather than re-deriving the clone-or-fallback + fixed-cell rules a second time.)
        internal static Button AddCardButton(GameObject row, string name, string label, Vector2 size,
                                            System.Action onClick)
        {
            var w = size.x;
            var h = size.y;

            var btn = NativeWidgetFactory.CloneMenuButton(row.transform, name, label, onClick);
            if (btn != null)
            {
                AddCloneLayoutElement(btn, row.transform, w, h);
                return btn;
            }

            btn = UiToolkit.CreateButton(row, name, label, Vector2.zero, size,
                new Vector2(0f, 0.5f), onClick);
            var le = LE(btn.gameObject);
            le.minWidth = w; le.preferredWidth = w;
            le.minHeight = h; le.preferredHeight = h;
            le.flexibleWidth = 0;
            return btn;
        }

        /// <summary>The GameObject to SetActive when gating a button cell on/off. A cloned native button
        /// is returned NESTED inside its clone root, so toggling the returned Button's own GameObject
        /// would hide the label and leave the clone's frame drawn; ResolveCloneRoot walks up to the child
        /// of <paramref name="container"/>, and hands back the from-code button unchanged when there was
        /// no clone.</summary>
        private static GameObject CellRoot(Button btn, Transform container)
        {
            if (btn == null) return null;
            var root = ResolveCloneRoot(btn.transform, container);
            return root != null ? root.gameObject : null;
        }

        /// <summary>SetActive, but only on a real transition (Refresh runs every frame).</summary>
        private static void SetCellActive(GameObject cell, bool on)
        {
            if (cell != null && cell.activeSelf != on) cell.SetActive(on);
        }

        /// <summary>
        /// Grey / un-grey INVITE VIA STEAM. ON CHANGE ONLY (-1 = never applied), the same transition
        /// guard the READY and NEW CAMPAIGN repaints use — SetInteractable runs an animator, so firing it
        /// every frame is not free.
        ///
        /// THE CONTROLLER OWNS THE VISUAL when there is one. SetInteractable is the complete native
        /// "appear now" path: it sets BaseButton.interactable, flips IsEnabled, runs the animator and
        /// calls UpdateColorElements. Writing Button.interactable alone leaves IsEnabled TRUE, and then
        /// the controller's own Update repaints the label on the first pointer-enter — a hover would
        /// un-grey the button, and with an apply-on-change gate the grey never returns. We still write
        /// interactable as well, because that is what actually refuses the click.
        ///
        /// THE LABEL BRANCH IS THE FALLBACK PATH ONLY. With no native template captured, AddCardButton
        /// builds the button from code: its Button is added at runtime with no targetGraphic, so its
        /// ColorTint transition has nothing to tint and interactable is invisible. There the label mute
        /// is the whole visual. The two branches are exclusive — nothing here can fight the controller.
        /// </summary>
        private void SetInviteButtonEnabled(bool on)
        {
            var now = on ? 1 : 0;
            if (now == _inviteInteractableCache) return;
            _inviteInteractableCache = now;

            if (_inviteBtn != null) _inviteBtn.interactable = on;

            if (_inviteBtnCtrl != null && _inviteBtnSetInteractable != null)
            {
                try { _inviteBtnSetInteractable.Invoke(_inviteBtnCtrl, new object[] { on }); }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError("[Multiplayer] Invite button repaint failed: " + e.Message);
                }
            }
            else if (_inviteBtnLabel != null)
            {
                _inviteBtnLabel.color = on ? LobbyTheme.BodyText : LobbyTheme.MutedText;
            }
        }

        /// <summary>
        /// SAY THAT A COPY HAPPENED, ON THE CELL THE PLAYER CLICKED: it shows the WORD "COPIED" in the
        /// roster's existing green for <see cref="CopiedAckSeconds"/>, then goes back to its own value
        /// and its own colour. A word, not a hue — a cell that merely changed colour asks the player to
        /// know what that colour means, and green here says nothing by itself.
        ///
        /// ONE ACK AT A TIME, which is the whole of the overlap case: clicking a second cell while the
        /// first is still green ENDS the first immediately (RestoreCopiedCell before the new capture), so
        /// no cell can be left stuck reading COPIED. Clicking the SAME cell again only pushes the
        /// deadline out — re-capturing would save "COPIED"/green AS the cell's real value and the ack
        /// would never come off.
        ///
        /// The capture is per-cell state rather than per-kind: whatever the cell was showing and however
        /// it was coloured is what comes back, so this knows nothing about invite codes or endpoints.
        /// </summary>
        private void NoteCopied(Text cell)
        {
            if (cell == null) return;
            if (!ReferenceEquals(cell, _copiedCell))
            {
                RestoreCopiedCell();          // whatever was green a moment ago is done NOW
                _copiedCell = cell;
                _copiedText = cell.text;      // captured BEFORE the swap, never after
                _copiedColor = cell.color;
                cell.text = CopiedLabel;
                cell.color = LobbyTheme.ReadyText;
            }
            _copiedUntil = Time.realtimeSinceStartup + CopiedAckSeconds;
        }

        /// <summary>Put the acknowledged cell back the way it was. No liveness question arises: the panel
        /// is built once (MultiplayerUI.Build), Hide only deactivates it, and address rows are POOLED —
        /// so no copyable cell is ever destroyed while an ack is live. The null test is the ordinary
        /// "nothing is acknowledging" case, not a guard against a dangling reference.</summary>
        private void RestoreCopiedCell()
        {
            if (_copiedCell == null) return;
            _copiedCell.text = _copiedText;
            _copiedCell.color = _copiedColor;
            _copiedCell = null;
        }

        // Click-to-copy value text: a button whose label is the live value; click copies it and the cell
        // says COPIED back (see NoteCopied). The
        // button rect is owned by the parent layout group (caller sets the LayoutElement min/preferred).
        private Text MakeCopyableValue(GameObject parent, string name, System.Func<string> getValue)
        {
            // Vector2.zero size, not a hand-picked one: the button rect comes from the parent layout
            // group and the label is stretched to the button below, so any number here would be a dead
            // literal that reads like a width someone chose.
            // The ack is wired HERE, in the one factory every copyable cell is built by, so a copy target
            // added later inherits it without its author knowing it exists. `label` is assigned a few
            // lines down and the closure captures the VARIABLE, not its (still null) value — the click
            // cannot run before the build finishes. Only a real copy acknowledges: CopyToClipboard
            // returns false for an empty value and the cell then says nothing.
            Text label = null;
            var btn = UiToolkit.CreateButton(parent, name, "", Vector2.zero, Vector2.zero,
                new Vector2(0f, 1f),
                () => { if (_owner.CopyToClipboard(getValue())) NoteCopied(label); });
            // The value is the one cell in its row that flexes, so it is the one that can be handed less
            // width than its text needs — and its label is a CreateText, i.e. horizontalOverflow =
            // Overflow, which would draw the tail of a long invite code straight over the button beside
            // it. RectMask2D clips it to its own rect, the same fix the roster's nickname cell carries.
            btn.gameObject.AddComponent<RectMask2D>();
            label = btn.GetComponentInChildren<Text>();
            if (label != null)
            {
                // STRETCH the label to the button rect. CreateButton anchors it at the centre with a
                // FIXED sizeDelta, so a right-aligned value would line up on the edge of that fixed rect
                // rather than the edge of the cell the layout group actually handed us — the value would
                // float somewhere mid-row. Rubber anchors make alignment mean what it says at any width.
                var lr = label.rectTransform;
                lr.anchorMin = Vector2.zero;
                lr.anchorMax = Vector2.one;
                lr.offsetMin = Vector2.zero;
                lr.offsetMax = Vector2.zero;

                label.alignment = TextAnchor.MiddleLeft;
                label.fontSize = LobbyTheme.ScaledRowFontSize;
                label.fontStyle = FontStyle.Bold;
                // ValueText, not Accent: every copyable value in the card reads in the one colour, and
                // Accent is captured from the game's WarningUIColor (any hue) — see LobbyTheme.ValueText.
                label.color = LobbyTheme.ValueText;
            }
            return label;
        }

        // CHAT CARD: header + scrolling log (plain rect host) + inline input + Send. Now the LOWER half
        // of the right column rather than the middle of three columns, so it claims its space
        // vertically: flexibleHeight 1 takes everything the fixed-height session card above leaves.
        private void BuildChatZone(GameObject parent)
        {
            var chat = new GameObject("ChatColumn");
            chat.transform.SetParent(parent.transform, false);
            chat.AddComponent<RectTransform>();
            AddFramedPanel(chat);

            LE(chat).flexibleHeight = 1;

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
            // construction (BuildRosterZone). A cloned native scroller (CloneScroller, since deleted)
            // was the ONLY structural difference between the working roster list and the chat list;
            // under the cloned native ScrollRect's viewport mask the from-code chat rows did not render
            // ("nothing shows" even though the data pipeline appended them). Mirroring the known-good
            // roster rect — top
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

            // Both the Enter key (polled in Refresh — see UiToolkit.SubmittedThisFrame for why it is NOT
            // hung on InputField.onEndEdit, which also fires on DESELECT and so broadcast a half-typed
            // line the moment the player clicked anywhere else) and the SEND button route through ONE
            // helper (SendCurrentChatInput) so the input text is read, empty-checked, sent, and cleared
            // identically on every path. The send goes via OnLobbyChatSend → SessionManager.SendChat,
            // which on the host echoes locally (sender sees their own line) and on a client relays to
            // the host who broadcasts it back — so the sender always sees their message.
            // Same fixed-cell/flexible-cell discipline as the roster row and the session card: SEND is a
            // fixed cell and pins minWidth (preferredWidth alone leaves its effective minimum at zero),
            // the input flexes but keeps a floor so it can never be squeezed to an unusable sliver.
            var inputMinW = LobbyTheme.Scale(120);
            _chatInput = UiToolkit.CreateInputField(inputRow, "ChatInput", "",
                Vector2.zero, new Vector2(220, inputH), new Vector2(0f, 0f));
            var cile = LE(_chatInput.gameObject);
            cile.minWidth = inputMinW;
            cile.preferredWidth = inputMinW;
            cile.flexibleWidth = 1;

            var sendW = LobbyTheme.Scale(90);
            var send = UiToolkit.CreateButton(inputRow, "ChatSend", "SEND",
                Vector2.zero, new Vector2(sendW, inputH), new Vector2(0f, 0f),
                SendCurrentChatInput);
            var sndle = LE(send.gameObject);
            sndle.minWidth = sendW; sndle.preferredWidth = sendW; sndle.flexibleWidth = 0;
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

        // LEFT COLUMN (60%): the self card, then the "PLAYERS (n)" header, then the scrollable rows
        // (re-uses the existing pool). The roster is the thing players actually look at, so it is the
        // wide half of the two-column page.
        private void BuildRosterZone(GameObject parent)
        {
            var players = new GameObject("PlayersColumn");
            players.transform.SetParent(parent.transform, false);
            players.AddComponent<RectTransform>();
            AddFramedPanel(players);

            // 3 of 3+2. A roster row carries a crown, a flexing nickname, a word status chip, a parity
            // badge and a ping meter — 548 logical px of summed MINIMUM at UiScale 1.4 — and the previous
            // three-column split left barely single digits of slack over that. See PinColumnWidth for why
            // the weight is only a true ratio once min/preferred are pinned to 0.
            PinColumnWidth(players, 3f);

            var pad = LobbyTheme.ScaledPadding;
            var vlg = players.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad, pad);
            vlg.spacing = pad / 2;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // The self card is created BEFORE the header, so the column's VerticalLayoutGroup (which
            // orders purely by sibling index) puts it above "PLAYERS (n)".
            BuildSelfCard(players);

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

        // THE SELF CARD: who YOU are, at a fixed place at the top of the roster zone rather than at
        // whichever list position the ordering happened to give you this frame. One framed card holding
        // the nickname (large), a HOST chip when this peer is the host, and the rename control — the ONLY
        // rename control in the panel now.
        private void BuildSelfCard(GameObject parent)
        {
            var pad = LobbyTheme.ScaledPadding;
            var nameH = LobbyTheme.ScaledSelfNameFontSize + pad / 2;
            var lineH = LobbyTheme.ScaledSubFontSize + pad / 4;
            var icon = LobbyTheme.ScaledIconButtonSize;

            _selfCard = new GameObject("SelfCard");
            _selfCard.transform.SetParent(parent.transform, false);
            _selfCard.AddComponent<RectTransform>();
            AddFramedPanel(_selfCard, LobbyTheme.CardBackground, LobbyTheme.CardBorder);

            var cle = LE(_selfCard);
            cle.minHeight = nameH + lineH + pad;
            cle.flexibleHeight = 0;

            var vlg = _selfCard.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad / 2, pad / 2);
            vlg.spacing = LobbyTheme.Scale(2);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            // Top line: nickname (flexes) · HOST · RENAME.
            var top = new GameObject("Top");
            top.transform.SetParent(_selfCard.transform, false);
            top.AddComponent<RectTransform>();
            var tle = LE(top); tle.minHeight = Mathf.Max(nameH, icon); tle.flexibleHeight = 0;
            var hlg = top.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = pad / 2;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;

            _selfName = UiToolkit.CreateText(top, "SelfName", Vector2.zero,
                new Vector2(LobbyTheme.ScaledRosterNameWidth, nameH), "",
                LobbyTheme.ScaledSelfNameFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            _selfName.color = LobbyTheme.BodyText;
            _selfName.fontStyle = FontStyle.Bold;
            // Same three writes as the roster row's name cell, for the same reason: preferredWidth so the
            // nickname's length cannot inflate the group's total and squeeze the tags beside it, and the
            // clip so a long one is truncated rather than drawn over them. (The tags below pin minWidth
            // for the other half of it.)
            _selfName.gameObject.AddComponent<RectMask2D>();
            var snle = LE(_selfName.gameObject);
            snle.minWidth = LobbyTheme.ScaledRosterNameWidth;
            snle.preferredWidth = LobbyTheme.ScaledRosterNameWidth;
            snle.flexibleWidth = 1;

            // NO "YOU" TAG. The card is a framed card of its own, above the PLAYERS header rather than
            // in the list, carrying the only RENAME control on the page — it cannot be anyone else, and
            // a label saying so was one more thing to read for no information.

            // Accent (the game's captured WarningUIColor) is used here as a ROLE highlight, the same way
            // every section header on this page uses it — not as a "good state" colour, which is the one
            // thing it must never be (it can arrive RED; see LoadOverlayController.cs:254).
            var hostW = LobbyTheme.Scale(48);
            _selfHostChip = UiToolkit.CreateText(top, "HostChip", Vector2.zero, new Vector2(hostW, lineH),
                "HOST", LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
            _selfHostChip.color = LobbyTheme.Accent;
            _selfHostChip.fontStyle = FontStyle.Bold;
            var hcle = LE(_selfHostChip.gameObject);
            hcle.minWidth = hostW; hcle.preferredWidth = hostW; hcle.flexibleWidth = 0;
            _selfHostChip.gameObject.SetActive(false);   // shown per frame in RefreshSelfCard

            // The rename control, reusing the EXACT existing rename path (OnLobbyRenamePrompt → native
            // prompt → SendRename) — only the widget it hangs off moved.
            var renameW = LobbyTheme.Scale(96);
            var renameBtn = NativeWidgetFactory.CloneMenuButton(top.transform, "RenameBtn", "RENAME",
                () => _owner.OnLobbyRenamePrompt());
            if (renameBtn != null)
                AddCloneLayoutElement(renameBtn, top.transform, renameW, icon);
            else
            {
                renameBtn = UiToolkit.CreateButton(top, "RenameBtn", "RENAME",
                    Vector2.zero, new Vector2(renameW, icon), new Vector2(1f, 0.5f),
                    () => _owner.OnLobbyRenamePrompt());
                var rle = LE(renameBtn.gameObject);
                rle.minWidth = renameW;
                rle.preferredWidth = renameW; rle.preferredHeight = icon; rle.flexibleWidth = 0;
            }

            // Second line: MY OWN transfer/load progress. It followed the local player up here from the
            // list row whose ready-check used to carry it — this is the one peer whose progress this
            // machine knows exactly (ProgressFor's isMe arm).
            _selfStatus = UiToolkit.CreateText(_selfCard, "SelfStatus", Vector2.zero,
                new Vector2(LobbyTheme.ScaledRosterNameWidth, lineH), "",
                LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            _selfStatus.color = LobbyTheme.SubText;
            var stle = LE(_selfStatus.gameObject); stle.minHeight = lineH; stle.flexibleHeight = 0;
            _selfStatus.gameObject.SetActive(false);
        }

        // FOOTER, left → right: LEAVE · CHOOSE SAVE… · NEW CAMPAIGN… · «spacer» · the QUEUED line ·
        // «spacer» · READY. Fixed height (LE.minHeight, flexibleHeight 0); everything laid out by a
        // HorizontalLayoutGroup so there is zero fractional-X math. (JOIN lives in the session card.)
        //
        // THE CAMPAIGN CONTROLS ARE HERE BECAUSE THE CAMPAIGN LINE IS. They used to sit in the right-hand
        // session card, a column away from the one sentence that says which campaign is queued; the
        // button that changes that sentence now stands next to it. Nothing about WHO MAY PRESS THEM
        // changed: both are hidden off the host (the SetActive the SaveGroup root used to carry, split
        // into the two cells that group held), and NEW CAMPAIGN keeps its own separate interactable gate
        // on EvaluateNewCampaignGate.
        //
        // WIDTH BUDGET (UiScale 1.4, logical px; the root VLG's 3×22 side padding leaves 1788):
        //   LEAVE 224 + CHOOSE SAVE… 280 + NEW CAMPAIGN… 280 + QUEUED 504 + READY 224 = 1512,
        //   plus 6 gaps × 11 = 66  →  1578 of 1788, i.e. 210 px of slack with both host-only cells shown.
        // Every one of those cells pins minWidth, the QUEUED label included: a cell carrying only
        // preferredWidth has an effective minimum of ZERO and is squeezed to nothing (drawn over by
        // whichever sibling overran) instead of pushing back — the bug this file already fixed once.
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
                var lle = LE(_leaveButton.gameObject);
                lle.minWidth = FooterButtonSize.x; lle.preferredWidth = FooterButtonSize.x;
            }

            // THE CAMPAIGN CONTROLS, host-only, immediately left of the line they change. Both stay
            // separate widgets with separate gating (see the header note) — CHOOSE SAVE… opens the save
            // picker, NEW CAMPAIGN… bootstraps a fresh one, and merging them would have to invent a rule
            // for which of the two a single button means.
            var chooseSaveBtn = AddCardButton(footer, "ChooseSaveBtn", "CHOOSE SAVE…",
                CampaignButtonSize, () => _owner.OnLobbyChooseSave());
            _chooseSaveCell = CellRoot(chooseSaveBtn, footer.transform);

            // P0 new-campaign bootstrap: start a FRESH campaign instead of picking a save (host runs the
            // native new-game flow; clients auto-join at the first geoscape frame).
            _newCampaignBtn = AddCardButton(footer, "NewCampaignBtn", "NEW CAMPAIGN…",
                CampaignButtonSize, () => _owner.OnLobbyNewCampaign());
            _newCampaignCell = CellRoot(_newCampaignBtn, footer.transform);

            // Same one-time controller resolution the READY button does, for the same reason: a cloned
            // native button repaints its greyed visual only through SetInteractable, never off
            // Button.interactable alone.
            if (_newCampaignBtn != null)
            {
                var ncPgb = HarmonyLib.AccessTools.TypeByName(
                    "PhoenixPoint.Common.View.ViewControllers.PhoenixGeneralButton");
                if (ncPgb != null)
                {
                    _newCampaignCtrl = _newCampaignBtn.GetComponentInParent(ncPgb)
                        ?? _newCampaignBtn.GetComponentInChildren(ncPgb);
                    if (_newCampaignCtrl != null)
                        _newCampaignSetInteractable =
                            HarmonyLib.AccessTools.Method(ncPgb, "SetInteractable", new[] { typeof(bool) });
                }
            }

            // Flexible spacer, then the QUEUED CAMPAIGN line, then a second flexible spacer: the label
            // sits in the footer's true centre with the left-hand cluster pinned left and Ready pinned
            // right, and no fractional-X math anywhere. (JOIN moved out of the footer and now sits in the
            // session card's JOIN row.)
            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(footer.transform, false);
            spacer.AddComponent<RectTransform>();
            LE(spacer).flexibleWidth = 1;

            _queuedCampaign = UiToolkit.CreateText(footer, "QueuedCampaign", Vector2.zero,
                new Vector2(LobbyTheme.Scale(360), FooterButtonSize.y), "",
                LobbyTheme.ScaledRowFontSize, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
            _queuedCampaign.color = LobbyTheme.SubText;
            _queuedCampaign.raycastTarget = false;   // a label in the middle of a button row must eat nothing
            _queuedCampaign.horizontalOverflow = HorizontalWrapMode.Wrap;
            var qle = LE(_queuedCampaign.gameObject);
            // minWidth AS WELL AS preferredWidth: the footer now carries two more fixed cells, and a
            // label with preferredWidth alone reports an effective minimum of zero — it would be the
            // first thing crushed to nothing the moment the row got tight, which is exactly the sentence
            // the whole footer exists to show.
            qle.minWidth = LobbyTheme.Scale(360);
            qle.preferredWidth = LobbyTheme.Scale(360);
            qle.flexibleWidth = 0;

            var spacer2 = new GameObject("Spacer2");
            spacer2.transform.SetParent(footer.transform, false);
            spacer2.AddComponent<RectTransform>();
            LE(spacer2).flexibleWidth = 1;

            // READY: a plain cloned MENU BUTTON acting as a toggle — same widget family as Leave/Join,
            // which render cleanly in a LayoutElement slot. (The old approach cloned a
            // GameOptionViewController option-row whose internal layout + "NEEDS TEXT" placeholder leaked
            // OUTSIDE its wrapper; the menu button has no such stray children.)
            //
            // EVERY PLAYER HAS ONE, THE HOST INCLUDED, and it is the footer's only primary control — the
            // PLAY button that used to sit beside it is GONE. When every row in the lobby reads READY the
            // session starts itself on a five-second countdown (LobbyCountdown); nobody presses go. The
            // host's toggle writes SessionManager.HostReady, which has always ridden the roster and which
            // the start gate now reads (LobbyController.PeersReady).
            //
            // Authoritative ready state arrives via the roster and is re-synced into _localReady + the
            // label in RefreshControls, on BOTH sides — the host's own row carries HostReady, so the same
            // one line of code serves host and client.
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
                // READY LOCK repaint: resolve the clone's native PhoenixGeneralButton controller ONCE so
                // the greyed visual appears immediately — interactable alone doesn't repaint a cloned
                // native button. Two things lock this button: a client's parity mismatch, and a HOST with
                // no save chosen (there is nothing to be ready FOR, and SetHostReady refuses it anyway).
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
                var rdle = LE(_readyButton.gameObject);
                rdle.minWidth = FooterButtonSize.x; rdle.preferredWidth = FooterButtonSize.x;
            }
            _readyButtonLabel = _readyButton != null ? _readyButton.GetComponentInChildren<Text>() : null;
            UpdateReadyButtonLabel();

            // NO PLAY BUTTON. There used to be one right here, host-only, greyed on the same gate. It is
            // gone on purpose and must not come back: with a countdown that arms the instant everybody is
            // ready, a second control that starts the session immediately is a way to skip the five
            // seconds the other players were promised to cancel in.
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
        // internal: NetworkGatePanel's friends card is the same kind of card and uses the same skin.
        internal static void AddFramedPanel(GameObject zone)
        {
            AddFramedPanel(zone, LobbyTheme.CardBackground, LobbyTheme.CardBorder);
        }

        // Cloned-button sizing (theme-scaled). The native TemplateMenuButton clone is sized for the
        // full-width main-menu column, so dropped unmodified into a lobby zone it overflows; we
        // constrain every clone to a rect that fits its zone (growing with UiScale) and cap its label
        // font so the text stays inside that rect.
        private static Vector2 SessionButtonSize => new Vector2(LobbyTheme.Scale(200), LobbyTheme.Scale(40));
        private static Vector2 FooterButtonSize => new Vector2(LobbyTheme.Scale(160), LobbyTheme.Scale(44));
        // The footer's campaign controls: FOOTER height (they stand in the footer's button row) but
        // 200 wide rather than 160 — "NEW CAMPAIGN…" is thirteen glyphs plus an ellipsis and the clone's
        // label is capped at ScaledClonedButtonFontCap, not shrunk to fit. See BuildFooter's budget.
        private static Vector2 CampaignButtonSize => new Vector2(LobbyTheme.Scale(200), FooterButtonSize.y);
        private static int ClonedButtonFontCap => LobbyTheme.ScaledClonedButtonFontCap;

        // Get-or-add a LayoutElement on a GameObject (caller sets the size fields). Centralises the
        // null-coalescing pattern used throughout the layout-driven build. internal so the gate panel
        // shares it instead of growing a second copy.
        internal static LayoutElement LE(GameObject go)
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
            // ReferenceEquals on the stop test (L113): "have I reached THAT transform" is identity, and
            // Unity's != would walk past a container whose native half is already gone, off the top of the
            // hierarchy. The `parent != null` arm stays ==-based on purpose — there the question really is
            // liveness, which is what the operator answers.
            while (t.parent != null && !ReferenceEquals(t.parent, container))
                t = t.parent;
            return t;
        }

        // KEEP A CLONE'S LABEL INSIDE THE CELL WE GAVE IT.
        //
        // Capping the font is COSMETIC, and knowing that is the point. A LayoutElement constrains the
        // clone ROOT; it says nothing about the prefab's own label rect inside, which is sized for the
        // full-width main-menu column and does not shrink when the root does — and best-fit and Wrap are
        // both measured against THAT rect, so they settle on a size that fits it and the glyphs still
        // reach past the cell. They keep the label looking right (resizeTextMaxSize is the capped size,
        // so a label that already fits renders identically); the RectMask2D in AddCloneLayoutElement is
        // what keeps it INSIDE. Do not read this as the guard.
        //
        // Same three writes as CountdownPanel.FitInsideRect and TacticalReadySync, for the same reason.
        private static void CapCloneFont(Transform start)
        {
            if (start == null) return;
            var label = start.GetComponentInChildren<Text>(true);
            if (label == null) return;
            if (label.fontSize > ClonedButtonFontCap)
                label.fontSize = ClonedButtonFontCap;
            label.resizeTextForBestFit = true;
            label.resizeTextMaxSize = label.fontSize > 0 ? label.fontSize : ClonedButtonFontCap;
            label.resizeTextMinSize = 8;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        // Size a cloned native button via a LayoutElement on its clone ROOT (the parent LayoutGroup
        // then drives position/size — no anchoredPosition/sizeDelta math). Resolves the OUTERMOST clone
        // root (the GameObject Clone* parented directly under <paramref name="container"/> — the
        // returned Button may be that root or a nested child), resets its localScale to 1 (the prefab
        // can carry a >1 scale), sets min+preferred W/H + flexibleWidth 0, caps the label font and masks
        // the root so the prefab's oversized label cannot draw outside the cell.
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
            // minWidth as well as preferredWidth: a cloned native button is a FIXED cell wherever it is
            // dropped, and preferredWidth alone leaves its effective minimum at zero — so the first time a
            // sibling's preferred width overran the group (a long nickname in a roster row) the button was
            // squeezed to nothing while the text that overran it kept drawing. Same pair, same reason, as
            // every from-code fixed cell in CreateRow.
            le.minWidth = w;
            le.preferredWidth = w;
            le.minHeight = h;        // pair the height too: a row whose floor is taller than h otherwise
            le.preferredHeight = h;  // has nothing from this cell to weigh against the stretch.
            le.flexibleWidth = 0;

            CapCloneFont(start);

            // AND THE HARD STOP. CapCloneFont asks the label to fit; it cannot make it. Best-fit and Wrap
            // both measure the label's OWN RectTransform, which is the prefab's (sized for the full-width
            // main-menu column) and does NOT shrink when this LayoutElement shrinks the clone root — so
            // they settle on a size that fits that wide rect and the glyphs still reach past the cell,
            // over whatever sits beside it. No uGUI Text clips itself. This mask is what actually keeps
            // a clone inside the cell we gave it; the clone root's rect IS layout-driven, so it clips to
            // exactly the cell.
            if (root.GetComponent<RectMask2D>() == null)
                root.gameObject.AddComponent<RectMask2D>();
        }

        // Stretch a cloned native scroller GO (an ancestor of its returned content RectTransform, child
        // of <paramref name="host"/>) to fill the group-sized host: relative 0..1 anchors, zero
        // offsets → rubber, no pixel math. No LayoutElement on it (the host is not a layout group).
        private static void StretchScrollerToHost(RectTransform content, Transform host)
        {
            if (content == null || host == null) return;
            Transform t = content.transform;
            while (t.parent != null && !ReferenceEquals(t.parent, host))   // L113: identity stop test
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
            // END ANY LIVE COPY ACK HERE. Refresh is only called while the lobby is VISIBLE
            // (MultiplayerUI.Update gates it on IsVisible), so an ack armed a frame before this would
            // otherwise stay armed for the whole hidden period and expire on the next Show — restoring a
            // capture from minutes ago onto a cell whose value has moved on. Hiding is the end of the
            // interaction, so it is the end of the acknowledgement.
            RestoreCopiedCell();

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
            // THE COPY ACK EXPIRES FIRST, above the two Hide() returns below, so the 1.5 s is measured
            // even on the frame the lobby is closing. It is NOT the whole story on its own: Refresh only
            // runs while the lobby is visible (MultiplayerUI.Update gates the call on IsVisible), so an
            // ack that outlives the screen is ended by Hide() instead — see the RestoreCopiedCell there.
            // Between the two, no ack can survive into a later Show and restore a stale capture.
            if (_copiedCell != null && Time.realtimeSinceStartup >= _copiedUntil) RestoreCopiedCell();

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

            // FOOTER CENTRE: what this lobby is queued on. Read off the AUTHORITATIVE broadcast value
            // (Session.ChosenSaveName — the same single source of truth the start gate keys on, mirrored
            // onto every client by the 0x43 SetSave packet), so host and clients read one identical
            // sentence and nobody has to ask which campaign the countdown is about to pull them into.
            if (_queuedCampaign != null)
            {
                var queued = engine.Session?.ChosenSaveName;
                _queuedCampaign.text = string.IsNullOrEmpty(queued)
                    ? "NEW CAMPAIGN QUEUED"
                    : "QUEUED: " + queued;
            }

            // THE INVITE KEY — read by BOTH sides, and PUBLISHED by the host. On the host this string is
            // recomputed here every frame anyway (it self-heals as UPnP/STUN discovery and Steam become
            // ready), so handing each new value to the session costs one string compare and is what
            // carries the key to every client on the next roster broadcast.
            if (_inviteCodeValue != null)
            {
                var code = _owner.GetSessionInviteCode();
                if (engine.IsHost) engine.Session?.PublishHostInviteCode(code);
                // THIS is the ack's other half for this cell, and the reason there is no second writer:
                // the per-frame stamp simply STANDS ASIDE while the cell is acknowledging a copy, then
                // resumes on the frame the ack expires above. A "COPIED" written anywhere else would be
                // erased by this line within one frame. Publishing never pauses — only the drawing does,
                // so a client's mirrored code keeps arriving while the host's cell reads COPIED.
                if (!ReferenceEquals(_inviteCodeValue, _copiedCell)) _inviteCodeValue.text = code;
            }

            // THE STEAM INVITE BUTTON IS FOR EVERYONE, NOT JUST THE HOST (see BuildSessionCard) — but it
            // is GREYED, not hidden, when there is no Steam to invite through. A button that vanishes
            // reads as a broken layout; a greyed one reads as "this needs Steam", which is the truth.
            SetCellActive(_inviteBtnCell, true);
            SetInviteButtonEnabled(SteamProbe.IsAlive());
            // HOST-ONLY: the address lines are the host machine's own addresses.
            RefreshAddresses(engine);

            // HOST-ONLY FOOTER CELLS: the two campaign controls. Byte-for-byte the visibility rule the
            // SaveGroup root carried before they moved down here.
            SetCellActive(_chooseSaveCell, engine.IsHost);
            SetCellActive(_newCampaignCell, engine.IsHost);

            // CHAT: Enter sends. Polled here rather than wired to the field, because the only event this
            // uGUI build offers (onEndEdit) also fires on deselect — clicking out of the box used to send.
            if (UiToolkit.SubmittedThisFrame(_chatInput)) SendCurrentChatInput();

            // …and re-render only when the log changed.
            if (_chat.Version != _chatRenderedVersion)
            {
                RenderChat();
                _chatRenderedVersion = _chat.Version;
            }

            // ROSTER + controls. The count still names EVERY player, the local one included — the self
            // card lifted "you" out of the list, not out of the lobby.
            var roster = RefreshRoster(engine);
            if (_connectText != null && roster.Count != _rosterCountShown)
            {
                _rosterCountShown = roster.Count;
                _connectText.text = $"PLAYERS ({roster.Count})";
            }
            RefreshControls(engine, roster);
        }

        // One address line: a key-left / value-right row. Key = the adapter ("Ethernet"), Value = the
        // "192.168.1.23:14242" cell, which is also the copy target — and the COPY is just that endpoint,
        // because the adapter name is for the human choosing a line, not for the parser reading it.
        private sealed class AddressLine
        {
            public GameObject Cell;   // the ROW: one SetActive hides key + value together
            public Text Key;
            public Text Value;
            public string Endpoint;
        }

        /// <summary>
        /// THE HOST'S ADDRESS LIST — every way in that is a typeable address, newest state each frame.
        ///
        ///   • ONE LINE PER LOCAL IPv4, labelled with its adapter. Not just the default-route one: the
        ///     entire point of Radmin / ZeroTier / Hamachi is a peer reachable on an adapter that is not
        ///     the default route, and the old socket-to-8.8.8.8 resolver could only ever return the one
        ///     that was (see LanIpResolver).
        ///   • THE UPnP ENDPOINT, labelled as the internet address, ONLY when the router actually opened
        ///     one. That mapping is a real TCP forward to the direct port — it is exactly the "works
        ///     without touching the router" case — and the line simply does not exist when it is null.
        ///
        /// AND NEVER THE STUN ENDPOINT. It is the obvious third thing to print here and it must not be:
        /// its port is the UDP NAT mapping, NOT the TCP listener. Pasted as "1.2.3.4:PORT" a friend's
        /// SmartJoinParser reads a DirectIp and DirectTransport dials TCP at a UDP port and times out.
        /// The STUN endpoint belongs in the encoded key above, which already carries the flags that say
        /// how to reach it (JoinPlan cascades Steam → hole-punch → direct). Do not add it as a line.
        ///
        /// The port on every line is <see cref="Multiplayer.Util.SmartJoinParser.DefaultDirectPort"/>
        /// because that is literally what DirectTransport binds (it exposes no bound port to ask).
        ///
        /// COST: two reference compares per frame. LanIpResolver hands back the SAME list instance until
        /// it re-enumerates (10 s TTL — a cable plugged in after the lobby opened appears, a per-frame
        /// syscall sweep does not happen), and UpnpPortMapper.Current is a single instance that is set
        /// once; so unless one of those two objects changed there is nothing to rebuild.
        /// </summary>
        private void RefreshAddresses(NetworkEngine engine)
        {
            if (_addressGroup == null) return;
            if (!engine.IsHost) { SetCellActive(_addressGroup, false); return; }

            var addrs = Multiplayer.Util.LanIpResolver.LocalIPv4Addresses();
            var upnp = Multiplayer.Net.UpnpPortMapper.Current;
            if (!ReferenceEquals(addrs, _addressesShownFor) || !ReferenceEquals(upnp, _upnpShownFor))
            {
                _addressesShownFor = addrs;
                _upnpShownFor = upnp;
                PaintAddressLines(addrs, upnp);
            }

            SetCellActive(_addressGroup, CountVisibleAddressLines() > 0);
        }

        // ponytail: at most MaxAddressLines rows. The card sizes itself from its rows and the chat below
        // absorbs what it leaves, so a handful of extra adapters just makes the card taller — but a box
        // with twenty VPN adapters would push the chat off the bottom, and this is the one line that
        // stops it. Raise it (or give the group its own scroller) if a real machine ever hits it.
        private const int MaxAddressLines = 8;

        private void PaintAddressLines(List<Multiplayer.Util.LanIpResolver.LocalAddress> addrs,
                                       Multiplayer.Net.MappedEndpoint upnp)
        {
            var port = Multiplayer.Util.SmartJoinParser.DefaultDirectPort;

            // Key and value are two CELLS now, not one concatenated string with an em-dash between them
            // — the row's own gap is the separator, and the value cell can right-align against the card
            // edge like every other value. (LobbyTheme.EmDash still serves the roster's ping / no-sample
            // columns; it just has no job here any more.)
            var i = 0;
            for (int a = 0; a < addrs.Count && i < MaxAddressLines; a++, i++)
                SetAddressLine(i, addrs[a].Interface, addrs[a].Ip + ":" + port);

            if (upnp != null && i < MaxAddressLines)
            {
                var ep = upnp.ToEndPoint();
                SetAddressLine(i, "Internet (UPnP)", ep.ToString());
                i++;
            }

            // THE SAME-PC LINE, LAST. LanIpResolver deliberately drops 127/8 — "an address nobody outside
            // this box can dial is worse than no line at all" — and that stays right for the LAN lines it
            // returns. But a SECOND INSTANCE on this machine is the one joiner for which loopback is the
            // only address that always works: the WAN/UPnP endpoint has to hairpin through the router and
            // the invite code carries nothing else. DirectTransport binds TcpListener(IPAddress.Any, port)
            // (DirectTransport.cs:141), so this listener really does answer here. Last, not first, so it
            // never crowds out or gets mistaken for an address to send a friend.
            if (i < MaxAddressLines)
            {
                SetAddressLine(i, "This PC (same machine)", "127.0.0.1:" + port);
                i++;
            }

            for (int spare = i; spare < _addressLines.Count; spare++)
                SetCellActive(_addressLines[spare].Cell, false);
        }

        private int CountVisibleAddressLines()
        {
            var n = 0;
            for (int i = 0; i < _addressLines.Count; i++)
                if (_addressLines[i].Cell != null && _addressLines[i].Cell.activeSelf) n++;
            return n;
        }

        // Pooled, exactly like the roster rows: a line is created once and then only re-labelled, so a
        // re-enumeration costs no GameObject churn. The copy closure reads the LINE's current endpoint
        // rather than capturing a value, which is what makes reuse safe.
        private void SetAddressLine(int index, string key, string endpoint)
        {
            while (_addressLines.Count <= index)
            {
                var line = new AddressLine();
                // Sub-size, not row-size: these are secondary lines and eight of them at ScaledRowHeight
                // would be half the card. AddCardRow's floor is the BUTTON height, so override it.
                var h = LobbyTheme.ScaledSubFontSize + LobbyTheme.ScaledPadding / 2;
                var keyW = LobbyTheme.Scale(160);
                var valW = LobbyTheme.Scale(200);   // fits "255.255.255.255:14242" at ScaledSubFontSize

                var row = AddCardRow(_addressGroup, "Address" + _addressLines.Count);
                var rowLe = LE(row);
                rowLe.minHeight = h; rowLe.preferredHeight = h; rowLe.flexibleHeight = 0;

                // KEY cell: fixed (min AND preferred — preferredWidth alone leaves the effective minimum
                // at zero and the cell collapses). Its own RectMask2D because no uGUI Text clips itself
                // and a "ZeroTier One [Virtual Adapter]" would otherwise reach across the value beside it.
                var keyText = UiToolkit.CreateText(row, "Key", Vector2.zero, new Vector2(keyW, h), "",
                    LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
                keyText.color = LobbyTheme.SubText;
                keyText.gameObject.AddComponent<RectMask2D>();
                var keyLe = LE(keyText.gameObject);
                keyLe.minWidth = keyW; keyLe.preferredWidth = keyW; keyLe.flexibleWidth = 0;
                keyLe.minHeight = h; keyLe.preferredHeight = h; keyLe.flexibleHeight = 0;

                // VALUE cell: same font and size as the key, ValueText red, right-aligned, and the click
                // target. It is the cell that flexes, so it is the one that can be handed less than its
                // text needs — MakeCopyableValue's RectMask2D clips it rather than letting it overrun.
                var val = MakeCopyableValue(row, "Value", () => line.Endpoint);
                val.alignment = TextAnchor.MiddleRight;
                val.fontSize = LobbyTheme.ScaledSubFontSize;
                val.fontStyle = FontStyle.Normal;
                var valLe = LE(val.transform.parent.gameObject);
                valLe.minWidth = valW; valLe.preferredWidth = valW; valLe.flexibleWidth = 1;
                valLe.minHeight = h; valLe.preferredHeight = h; valLe.flexibleHeight = 0;

                line.Cell = row;
                line.Key = keyText;
                line.Value = val;
                _addressLines.Add(line);
            }

            var ln = _addressLines[index];
            ln.Endpoint = endpoint;
            if (ln.Key.text != key) ln.Key.text = key;
            // THE SAME STAND-ASIDE THE INVITE-CODE WRITER USES, and it needs the second half as well.
            // This repaint fires when the address list changes under a live ack (LanIpResolver's 10 s TTL
            // hands back a new list instance, or the UPnP mapping lands mid-ack): writing here would wipe
            // COPIED early, and then the restore would put the PRE-CLICK endpoint back over the new one —
            // where the != guard below would keep it, wrong, until the list changed again. So while this
            // cell is acknowledging, re-aim the ACK's capture at the new endpoint instead of drawing it.
            if (ReferenceEquals(ln.Value, _copiedCell)) _copiedText = endpoint;
            else if (ln.Value.text != endpoint) ln.Value.text = endpoint;
            SetCellActive(ln.Cell, true);
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
                // WRAP, not the CreateText default Overflow. Chat lines are arbitrary user text and a
                // system notice is a whole sentence; the chat now lives in a 40%-wide column, so on
                // Overflow a long line drew its tail out through the card frame and across the roster.
                // Wrapping is the right answer here (unlike the roster nickname, which is clipped —
                // there the row height is fixed and the columns beside it must stay aligned): the log
                // stacks top-down with a ContentSizeFitter, so a taller row simply pushes the rest down.
                // LE.preferredHeight is deliberately LEFT UNSET so the Text's own wrapped preferred
                // height (measured against the width the layout pass already gave it) drives the row;
                // minHeight only reserves the single-line case.
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
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
                if (IsMe(p, engine, localGuid)) { me = p; break; }

            // READY: EVERY player has it, the host included — one path, no host branch. The host's own
            // roster self-entry carries SessionManager.HostReady, so `me.Ready` is the authoritative truth
            // on both sides and this single re-sync overrides any optimistic click flip with it (after a
            // reconnect, or when the host refused the ready), without re-invoking OnLobbyToggleReady.
            _localReady = me != null && me.Ready;

            // THE TWO LOCKS.
            //  • a CLIENT whose own roster row carries host-computed parity diffs (label names the reason;
            //    the row's "!" badge click shows the exact diffs). The host ALSO ignores a READY from a
            //    mismatched client (SetClientReady), so this is UX — authority stays host-side.
            //  • the HOST with no save chosen. Readying would arm nothing (CanStart still needs the save)
            //    and SetHostReady refuses it host-authoritatively, so the button says so instead of
            //    looking pressable and doing nothing.
            _parityLocked = !engine.IsHost && me != null
                && !Multiplayer.Network.Parity.ParityComparer.ReadyAllowed(me.ParityDiffs);
            _needsSave = engine.IsHost && string.IsNullOrEmpty(engine.Session?.ChosenSaveName);
            if (_readyButton != null)
            {
                var interactable = !_parityLocked && !_needsSave;
                _readyButton.interactable = interactable;
                var interactableNow = interactable ? 1 : 0;
                // The cloned native button's greyed visual is driven by its PhoenixGeneralButton
                // controller, which repaints ONLY when its own IsEnabled changes (via SetInteractable) —
                // NOT when BaseButton.interactable changes. Fire on a real transition only.
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

            // NEW CAMPAIGN: greys on the SAME lobby readiness gate, minus the chosen-save half (a fresh
            // campaign picks no save). Same press-time projection the button's handler re-checks, so the
            // visual and the press can never disagree.
            if (_newCampaignBtn != null)
            {
                var ncReady = engine.IsHost && (MultiplayerUI.Instance?.EvaluateNewCampaignGate() ?? false);
                _newCampaignBtn.interactable = ncReady;
                var ncNow = ncReady ? 1 : 0;
                if (ncNow != _newCampaignInteractableCache)
                {
                    _newCampaignInteractableCache = ncNow;
                    if (_newCampaignSetInteractable != null && _newCampaignCtrl != null)
                    {
                        try { _newCampaignSetInteractable.Invoke(_newCampaignCtrl, new object[] { ncReady }); }
                        catch (System.Exception e)
                        {
                            UnityEngine.Debug.LogError("[Multiplayer] New-campaign button repaint failed: " + e.Message);
                        }
                    }
                }
            }
        }

        // Reflect the current local ready state on the ready button's label. Call after a click flip and
        // whenever RefreshControls re-syncs _localReady from authoritative roster state. "✓ READY" =
        // readied up (press to take it back); "READY" = press to ready up. A locked button names its own
        // reason: the parity row's "!" badge click shows the exact diff list, and a host with no campaign
        // queued is told what is missing rather than being handed a dead control.
        private void UpdateReadyButtonLabel()
        {
            if (_readyButtonLabel == null) return;
            // SEVEN GLYPHS IS THE CELL, and that is why these two read the way they do. The clone's label
            // keeps the PREFAB's own wide main-menu rect (CapCloneFont's note), so best-fit measures
            // against THAT and the RectMask2D in AddCloneLayoutElement clips whatever overhangs the 160-px
            // footer cell. "✓ READY" is the widest string known to survive that window. "MODS MISMATCH"
            // and "CHOOSE A SAVE" are thirteen, so both were clipped to their middle few glyphs and a
            // greyed button reading "S MISMA" is indistinguishable from no READY button at all — which is
            // exactly what a three-instance test reported on 2026-08-10, with every client parity-locked
            // and the host on the new-campaign route. The reason still has a full-length home: the roster
            // row's "!" badge lists the exact diffs.
            _readyButtonLabel.text = _parityLocked ? "MODS ✗"
                : _needsSave ? "NO SAVE"
                : _localReady ? "✓ READY" : "READY";
        }

        /// <summary>
        /// "IS THIS ROW ME", and the ONLY place that question is answered.
        ///
        /// The two sides ask it differently and must: the host's self-entry is stamped with that machine's
        /// own LocalSteamId and a guid no client can match, so a client matches on <c>PlayerGuid</c> while
        /// the host simply takes the row flagged <c>IsHost</c>. This lived in THREE copies — the rename
        /// gate, the row paint and the display order — which is three chances for a rename to land on the
        /// wrong peer while each copy looks correct on its own.
        /// </summary>
        private static bool IsMe(PeerListEntry p, NetworkEngine engine, System.Guid localGuid)
            => engine.IsHost ? p.IsHost : p.PlayerGuid == localGuid;

        // Rebuild/refresh the player rows from the unified lobby roster (host self-entry + clients).
        // The LOCAL player is NOT emitted into the list — it is the self card above the header. The list
        // is therefore "everyone else", and nothing more.
        private List<PeerListEntry> RefreshRoster(NetworkEngine engine)
        {
            var roster = engine.Session?.GetLobbyRoster() ?? new List<PeerListEntry>();
            var localGuid = ClientIdentity.PlayerGuid;

            // (B) ORDER for display: HOST first, then the LOCAL player ("you"), then everyone else in
            // their existing wire order. Render-only reorder — SessionManager / the wire order is left
            // untouched. "you" is skipped when the rows are painted, but it stays in the ordered list
            // because RefreshControls reads its Ready/parity state off the returned roster.
            roster = OrderForDisplay(roster, engine, localGuid);

            var st = engine.SaveTransfer;
            var transferActive = st != null && st.TransferActive;

            PeerListEntry me = null;
            var slot = 0;
            foreach (var p in roster)
            {
                if (IsMe(p, engine, localGuid)) { me = p; continue; }
                while (_rows.Count <= slot) _rows.Add(CreateRow(_rows.Count));
                PaintRow(_rows[slot], p, engine, st, transferActive);
                slot++;
            }

            // NO OPEN-SEAT PLACEHOLDERS. There used to be a dashed "OPEN SLOT" row for every unfilled
            // seat up to a hard-coded four — a number that was never the session capacity (that is 50)
            // and gated nothing, so the rows were three lines of furniture answering a question nobody
            // asked. The roster shows who is here.
            for (var i = slot; i < _rows.Count; i++)
                if (_rows[i].Go.activeSelf) _rows[i].Go.SetActive(false);

            RefreshSelfCard(me, engine, st, transferActive);
            return roster;
        }

        // Paint one OCCUPIED row from its roster entry. Pure assignment — no allocation beyond the
        // "N ms" string, which PaintPing rebuilds only when the reading actually moves (this runs every
        // frame from MultiplayerUI.Update).
        private static void PaintRow(RosterRow row, PeerListEntry p, NetworkEngine engine,
                                     SaveTransferCoordinator st, bool transferActive)
        {
            if (!row.Go.activeSelf) row.Go.SetActive(true);

            // A peer that stopped answering is HELD, not gone: its seat, slot and guid are all still
            // its own (L84), so the row STAYS — dimmed, wearing SEAT HELD. Removing it would say the
            // player left, which is the one thing that has not happened.
            var held = p.Paused;
            var body = held ? LobbyTheme.MutedText : LobbyTheme.BodyText;

            // WHO THE HOST IS, IN A SECOND PLACE THAN THE CROWN. The wash is a LIGHTENED CardBackground —
            // the old CardBackground tint was the exact colour of the panel behind it, i.e. nothing at all.
            // It stays lit on a HELD host row too: a host that has gone quiet is still the host, and that is
            // precisely the row a reader needs to identify.
            row.Tint.color = p.IsHost ? LobbyTheme.HostRowTint : Transparent;

            row.Crown.text = p.IsHost ? HostCrown : "";
            row.Crown.color = held ? LobbyTheme.MutedText : LobbyTheme.Accent;

            row.NameLabel.text = string.IsNullOrEmpty(p.Nickname)
                ? (p.IsHost ? "Host" : FallbackName(p.SteamId, ref row.FallbackNameFor, ref row.FallbackName))
                : p.Nickname;
            row.NameLabel.color = body;

            // THE STATUS IN WORDS, one cell, in the order the words can be true. A held seat outranks
            // readiness because a peer that has gone silent is not "not ready", it is not here, and its
            // last-known readiness is the one thing this cell must not repeat. A live transfer outranks
            // readiness because it is what is actually happening to that peer right now.
            if (held)
            {
                row.StatusChip.text = "SEAT HELD";
                row.StatusChip.color = LobbyTheme.MutedText;
            }
            else
            {
                var progress = transferActive ? ProgressFor(engine, st, p, false) : null;
                if (progress != null)
                {
                    row.StatusChip.text = progress;
                    row.StatusChip.color = LobbyTheme.SubText;
                }
                else if (!p.IsHost && p.Ready)
                {
                    row.StatusChip.text = "READY";
                    row.StatusChip.color = LobbyTheme.ReadyText;
                }
                else row.StatusChip.text = "";
            }

            // PARITY badge: shown only when this peer's row carries diffs (host sees it on the
            // mismatched client's row; the client sees it on its OWN row). Click → exact diff list.
            row.ParityDiffs = p.ParityDiffs ?? "";
            var warn = row.ParityDiffs.Length > 0;
            if (row.WarnBtn.gameObject.activeSelf != warn) row.WarnBtn.gameObject.SetActive(warn);

            PaintPing(row, PingTable.PingMsFor(engine.Session, engine, p));
        }

        // The meter and the number, side by side. The string is rebuilt ONLY when the reading changes:
        // this runs per row per frame and the sample moves once a cadence (PingTable.CadenceMs), so
        // formatting it every frame would be a garbage tail for a number that is not moving.
        private static void PaintPing(RosterRow row, int ms)
        {
            PlayerPanel.PaintBars(row.Bars, ms);
            if (ms == row.PingShown) return;
            row.PingShown = ms;
            // NO SAMPLE IS SAID OUT LOUD (law L145): an em-dash, never a stale number, and nothing here
            // or anywhere else waits for the sample that did not arrive. Via the theme, because an em-dash
            // the captured font does not carry would render BLANK — i.e. as "nothing to report", the one
            // reading this cell must never give.
            row.PingText.text = ms < 0 ? LobbyTheme.EmDash : ms + " ms";
            row.PingText.color = ms < 0 ? LobbyTheme.MutedText : LobbyTheme.SubText;
        }

        // Repaint the self card from the local player's own roster entry. A null entry means the roster
        // has not arrived yet (a client before its first PEER_LIST) — the card hides rather than invent a
        // name the host has not confirmed.
        private void RefreshSelfCard(PeerListEntry me, NetworkEngine engine,
                                     SaveTransferCoordinator st, bool transferActive)
        {
            if (_selfCard == null) return;

            var show = me != null;
            if (_selfCard.activeSelf != show) _selfCard.SetActive(show);
            if (!show) return;

            _selfName.text = string.IsNullOrEmpty(me.Nickname)
                ? (me.IsHost ? "Host" : FallbackName(me.SteamId, ref _selfFallbackFor, ref _selfFallbackName))
                : me.Nickname;

            if (_selfHostChip.gameObject.activeSelf != me.IsHost)
                _selfHostChip.gameObject.SetActive(me.IsHost);

            var progress = transferActive ? ProgressFor(engine, st, me, true) : null;
            var hasProgress = progress != null;
            if (_selfStatus.gameObject.activeSelf != hasProgress)
                _selfStatus.gameObject.SetActive(hasProgress);
            if (hasProgress) _selfStatus.text = progress;
        }

        // Render-only reorder: HOST first, then the LOCAL player ("you"), then the rest in their original
        // order. Builds a NEW list (the source list from GetLobbyRoster is a fresh copy, but we don't
        // mutate it to keep this side-effect free).
        private static List<PeerListEntry> OrderForDisplay(List<PeerListEntry> roster, NetworkEngine engine, System.Guid localGuid)
        {
            if (roster == null || roster.Count <= 1) return roster ?? new List<PeerListEntry>();

            var ordered = new List<PeerListEntry>(roster.Count);
            PeerListEntry host = null;
            PeerListEntry me = null;

            foreach (var p in roster)
                if (p.IsHost) { host = p; break; }

            foreach (var p in roster)
                if (IsMe(p, engine, localGuid)) { me = p; break; }

            if (host != null) ordered.Add(host);
            if (me != null && !ReferenceEquals(me, host)) ordered.Add(me);
            foreach (var p in roster)
                if (!ReferenceEquals(p, host) && !ReferenceEquals(p, me))
                    ordered.Add(p);

            return ordered;
        }

        // Returns a progress/loading string during an active transfer, or null to fall back to ready
        // status. Reliable subset: my own exact download %, and (on the host) each client's reported
        // download %. Phase-2 game-load % is an OPEN SDK item (not shown).
        //
        // TWO LENGTHS, ON PURPOSE — and the isMe arm is exactly the split. The SELF CARD's second line is
        // a full-width line of its own and keeps the human sentence. A ROW's status chip is
        // ScaledRosterStatusWidth (sized for "SEAT HELD") and is right-aligned with Overflow, so
        // "downloading 100%" there did not shorten the chip, it spilled LEFT across the nickname for the
        // whole duration of every transfer. The percentage and one short word say the same thing inside
        // the chip; widening the chip would have paid for it out of the nickname's width.
        private static string ProgressFor(NetworkEngine engine, SaveTransferCoordinator st,
            PeerListEntry p, bool isMe)
        {
            if (isMe)
            {
                if (st.IsBarrierPending) return $"loaded {LobbyTheme.EmDash} waiting";
                var pct = st.LocalDownloadPercent;
                if (pct >= 0 && pct < 100) return $"downloading {pct}%";
                if (engine.IsHost) return "sending save";
                return null;
            }

            // Host can show each connected client's reported download %.
            if (engine.IsHost && !p.IsHost)
            {
                if (st.TryGetPeerDownloadPercent(p.SteamId, out var cpct))
                    return cpct >= 100 ? "WAITING" : cpct + "%";
            }
            return null;
        }

        // Build ONE structured roster row: an HLG container holding, left→right, (1) the host CROWN cell,
        // (2) the NICKNAME, which FLEXES, (3) the word STATUS chip, (4) the "MODS ≠" parity badge, and
        // (5) the right-aligned PING meter. The row spans the roster column (flexibleWidth 1) so every row
        // is the same width, and the fixed cells keep the right-hand cluster aligned down the list.
        //
        // There is NO rename control here. It moved to the self card, which is always the local player —
        // so the "does this pooled slot still render me?" re-check the old pencil needed on every click
        // has no way left to be got wrong.
        private RosterRow CreateRow(int index)
        {
            var go = new GameObject($"Row{index}");
            go.transform.SetParent(_rosterArea.transform, false);
            go.AddComponent<RectTransform>();

            // Row tint plate, added on the row root BEFORE its children so it sits behind them (the same
            // ordering trick the page backdrop uses). raycastTarget off: a full-width Image across every
            // row would otherwise be a surface that swallows clicks meant for the badge on top of it.
            var tint = go.AddComponent<Image>();
            tint.color = Transparent;
            tint.raycastTarget = false;

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

            // EVERY FIXED CELL BELOW SETS minWidth AS WELL AS preferredWidth, and that pair is the whole
            // of finding 1. A LayoutElement with preferredWidth alone has an effective MINIMUM of zero, so
            // the moment the group's total preferred exceeded the row the crown, the chip, the badge and
            // the meter were all shrunk toward nothing while the nickname — a CreateText label, i.e.
            // horizontalOverflow = Overflow — went on drawing at full length straight over them. minWidth
            // is what makes a fixed cell actually fixed; the nickname is then the only cell with anything
            // to give.

            // (1) CROWN — far-left, the host's marker. Text + colour set per frame in PaintRow. Sized off
            // the RESOLVED marker, because a font with no crown gets the wider ASCII stand-in instead
            // (LobbyTheme.HostCrown), and a stand-in that does not fit its cell is the same bug again.
            var crownW = LobbyTheme.Scale(LobbyTheme.HostCrown.Length > 1 ? 50 : 22);
            var crown = UiToolkit.CreateText(go, "Crown", Vector2.zero, new Vector2(crownW, RowHeight), "",
                LobbyTheme.ScaledBodyFontSize, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
            var crle = LE(crown.gameObject);
            crle.minWidth = crownW; crle.preferredWidth = crownW; crle.flexibleWidth = 0;

            // (2) NICKNAME — the ONLY cell that gives up width, and the only one that has to be clipped.
            // preferredWidth is pinned to the same base minimum so the nickname's LENGTH stops inflating
            // the group's total preferred width (which is what dragged every other cell toward its
            // minimum); flexibleWidth 1 still hands it all the slack the fixed cells leave. The
            // RectMask2D — the only mask in the mod, and deliberately on this one cell rather than on the
            // row — clips the label to its own rect, so a nickname too long for the space it won is
            // TRUNCATED instead of painted over the status chip beside it.
            var nameLabel = UiToolkit.CreateText(go, "Name", Vector2.zero,
                new Vector2(LobbyTheme.ScaledRosterNameWidth, RowHeight), "",
                LobbyTheme.ScaledBodyFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 0.5f));
            nameLabel.gameObject.AddComponent<RectMask2D>();
            var nle = LE(nameLabel.gameObject);
            nle.minWidth = LobbyTheme.ScaledRosterNameWidth;
            nle.preferredWidth = LobbyTheme.ScaledRosterNameWidth;
            nle.flexibleWidth = 1;

            // (3) STATUS CHIP — the row's state in WORDS ("READY" / "SEAT HELD" / a transfer line), not a
            // glyph. The bare "✓" it replaces needed a legend nobody has.
            var chipW = LobbyTheme.ScaledRosterStatusWidth;
            var statusChip = UiToolkit.CreateText(go, "Status", Vector2.zero,
                new Vector2(chipW, RowHeight), "", LobbyTheme.ScaledSubFontSize,
                TextAnchor.MiddleRight, new Vector2(1f, 0.5f));
            statusChip.fontStyle = FontStyle.Bold;
            var scle = LE(statusChip.gameObject);
            scle.minWidth = chipW; scle.preferredWidth = chipW; scle.flexibleWidth = 0;

            // (4) PARITY badge — a small native-cloned button, hidden unless the row's peer has parity
            // diffs (toggled per frame in PaintRow). Click → the EXACT host-computed diff list via the
            // native message box (the lobby has no hover-tooltip mechanism; the message box is this mod's
            // canonical detail surface). Reads the CURRENT pool row's diffs by index so a reused row never
            // shows stale text.
            System.Action showDiffs = () =>
            {
                if (index < _rows.Count) _owner.OnLobbyShowParityDiffs(_rows[index].ParityDiffs);
            };
            // The label goes through the theme: "≠" is not a character the captured menu font is known to
            // carry, and a badge whose whole text is one missing glyph is a blank button.
            var warnW = LobbyTheme.Scale(64);
            var warnLabel = LobbyTheme.ParityBadgeLabel;
            var warnBtn = NativeWidgetFactory.CloneMenuButton(go.transform, "ParityWarnBtn", warnLabel,
                () => showDiffs());
            if (warnBtn != null)
                AddCloneLayoutElement(warnBtn, go.transform, warnW, LobbyTheme.ScaledIconButtonSize);
            else
            {
                warnBtn = UiToolkit.CreateButton(go, "ParityWarnBtn", warnLabel,
                    Vector2.zero, new Vector2(warnW, LobbyTheme.ScaledIconButtonSize), new Vector2(1f, 0.5f),
                    () => showDiffs());
                var wle = LE(warnBtn.gameObject);
                wle.preferredWidth = warnW;
                wle.preferredHeight = LobbyTheme.ScaledIconButtonSize;
                wle.flexibleWidth = 0;
            }
            // Warning tint on the badge so it reads as "attention", not a normal action button.
            var warnTxt = warnBtn.GetComponentInChildren<Text>();
            if (warnTxt != null)
            {
                warnTxt.color = new Color(1f, 0.75f, 0.2f);
                warnTxt.fontSize = LobbyTheme.ScaledSubFontSize;
            }
            warnBtn.gameObject.SetActive(false); // hidden until PaintRow sees diffs

            // (5) PING — the TACTICAL panel's meter, not a second one: PlayerPanel owns the bar geometry
            // and the thresholds law L159 pins, and this cell reuses both rather than growing a copy that
            // could disagree about what a colour means. The number sits beside the bars permanently; the
            // tactical panel swaps it in on hover only because it overhangs a live battle map, and a lobby
            // row has the room for both.
            // THE CELL IS LAID OUT, NOT PLACED ONCE. It used to pin the bars to its bottom-left by
            // anchoredPosition and give the number a sizeDelta of exactly "cell − bars − gap", a sum that
            // consumed the cell whole: any shrink of the cell slid the number left over the meter, and
            // nothing re-ran. It also assumed the two scale together, which they do not — BarsWidth is
            // built from Mathf.Max-floored values that stop shrinking at small UiScale while a Scale(72)
            // cell keeps going, so the number's share collapsed at UiScale 0.5 with the bars unchanged.
            //
            // Now: an HLG with a fixed METER slot (minWidth = BarsWidth, so the bars' own floor IS the
            // slot's floor) and the number beside it, and the cell's width is DERIVED from those two
            // rather than picked — so the two can no longer overlap at any UiScale. The cell pins its own
            // minWidth for the same reason every other fixed cell above does.
            //
            // The number's slot is 4 em wide: the widest thing it ever holds is "9999 ms" (~3.6 em in any
            // proportional face), and expressing it in the FONT SIZE is what keeps it correct at a UiScale
            // the bars' integer floors have stopped tracking.
            var meterW = PlayerPanel.BarsWidth;
            var msW = LobbyTheme.ScaledSubFontSize * 4;
            var gapW = LobbyTheme.Scale(4);
            var pingW = meterW + gapW + msW;

            var cellGo = new GameObject("Ping");
            cellGo.transform.SetParent(go.transform, false);
            cellGo.AddComponent<RectTransform>();
            var ple = LE(cellGo);
            ple.minWidth = pingW; ple.preferredWidth = pingW; ple.flexibleWidth = 0;
            var pinghlg = cellGo.AddComponent<HorizontalLayoutGroup>();
            pinghlg.spacing = gapW;
            pinghlg.childControlWidth = true;
            pinghlg.childControlHeight = true;
            pinghlg.childForceExpandWidth = false;
            pinghlg.childForceExpandHeight = true;
            pinghlg.childAlignment = TextAnchor.MiddleRight;

            // The meter keeps its own absolute bar geometry INSIDE this slot — a layout group sizes its
            // children, not its grandchildren, so PlayerPanel.CreateBars is reused untouched.
            var meterGo = new GameObject("Meter");
            meterGo.transform.SetParent(cellGo.transform, false);
            meterGo.AddComponent<RectTransform>();
            var mle = LE(meterGo);
            mle.minWidth = meterW; mle.preferredWidth = meterW; mle.flexibleWidth = 0;

            var bars = PlayerPanel.CreateBars(meterGo.transform, LobbyTheme.ScaledRowHeight);
            var pingText = UiToolkit.CreateText(cellGo, "Ms", Vector2.zero,
                new Vector2(msW, RowHeight), "",
                LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleRight, new Vector2(1f, 0.5f));
            pingText.color = LobbyTheme.SubText;
            var msle = LE(pingText.gameObject);
            msle.minWidth = msW; msle.preferredWidth = msW; msle.flexibleWidth = 0;

            return new RosterRow
            {
                Go = go, Tint = tint, Crown = crown, NameLabel = nameLabel, StatusChip = statusChip,
                WarnBtn = warnBtn, Bars = bars, PingText = pingText,
                ParityDiffs = "", PingShown = int.MinValue
            };
        }
    }
}
