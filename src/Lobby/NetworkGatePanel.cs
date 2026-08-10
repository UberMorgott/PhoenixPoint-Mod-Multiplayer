using System.Collections.Generic;
using Multiplayer.Transport;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.UI
{
    /// <summary>
    /// THE CREATE-OR-JOIN GATE — the two screens NETWORK GAME opens instead of a live host lobby.
    ///
    /// Clicking NETWORK GAME used to run <c>StartHostAndOpenLobby</c> directly, so a player who wanted to
    /// JOIN was dropped into a screen announcing that they were HOSTING, with the join control buried in
    /// a card inside it. Nothing is hosted now until CREATE SESSION is pressed.
    ///
    ///   • <see cref="Screen.Gate"/> — two choices and a back control. No text fields.
    ///   • <see cref="Screen.Join"/> — one connect-target field (everything SmartJoinParser.Parse reads:
    ///     ip[:port], localhost, a DNS host, an 8/9/10/13/19-symbol Crockford key, a raw SteamID64),
    ///     plus the Steam friends who are in a co-op lobby of this game, one click per row.
    ///
    /// LIFECYCLE mirrors <see cref="LobbyPanel"/> exactly, because that is the pattern that already works
    /// here: ONE overlay Canvas built once under ModGO (a root canvas → its CanvasScaler is honoured; a
    /// canvas nested under another canvas collapses to a ~100px box — see LobbyPanel.Build), sortingOrder
    /// 4000, and the canvas GameObject as the SINGLE visibility lever. The two screens are sibling roots
    /// under it; exactly one is active at a time.
    ///
    /// CHROME OWNERSHIP: <see cref="NativeWidgetFactory.HideMenuChrome"/> is a global one-shot with a
    /// paired restore, so exactly one mod screen may own it. <see cref="_ownsChrome"/> makes that safe
    /// from this side — this panel only ever RESTORES chrome it itself hid, so a Hide() called while the
    /// LOBBY owns the chrome cannot pull the menu back up underneath the lobby. Callers hand over by
    /// hiding the outgoing screen before showing the incoming one (Show hides the chrome, Hide restores
    /// it — the reverse order would restore last and leave the native menu drawn over our page).
    /// </summary>
    internal class NetworkGatePanel
    {
        private enum Screen { None, Gate, Join }

        private readonly MultiplayerUI _owner;

        private Canvas _canvas;
        private CanvasScaler _scaler;   // kept so Tick() can re-apply the aspect-adaptive match live
        private GameObject _root;

        private GameObject _gateRoot;
        private GameObject _joinRoot;

        private InputField _target;         // the ONE text field in either screen
        private GameObject _friendsList;    // rows are rebuilt into this on every ShowJoin
        private Text _friendsEmpty;         // shown instead of rows when there is nothing to list

        // WHICH SCREEN IS LOGICALLY OPEN — not the same thing as "visible". HideForNativeScreen() takes
        // the canvas down for a native MessageBox while LEAVING this set, so Reshow() can put the player
        // back exactly where they were once the box is dismissed. Hide() is the real close and clears it.
        private Screen _screen = Screen.None;

        // True only while THIS panel is the one holding the menu chrome down. Without it, a defensive
        // Hide() from a caller that is about to open the LOBBY would call RestoreMenuChrome and re-enable
        // every native menu canvas on top of the lobby.
        private bool _ownsChrome;

        public bool IsVisible => _canvas != null && _canvas.gameObject.activeSelf;

        public NetworkGatePanel(MultiplayerUI owner)
        {
            _owner = owner;
        }

        // ─── Build (once) ──────────────────────────────────────────────────

        /// <summary>Build both screens on this panel's own root overlay Canvas. <paramref name="menuCanvas"/>
        /// is the readiness gate (the native menu must exist before we can clone its widgets), exactly as
        /// in <see cref="LobbyPanel.Build"/>.</summary>
        public void Build(Canvas menuCanvas)
        {
            if (_root != null || menuCanvas == null) return;

            var canvasGo = new GameObject("MultiplayerGateCanvas");
            // Under ModGO, NOT under the menu canvas: a nested canvas has its CanvasScaler ignored and its
            // RectTransform undriven (the cramping bug documented in LobbyPanel.Build).
            canvasGo.transform.SetParent(_owner.transform, false);
            canvasGo.SetActive(false);   // hidden from the first frame, even if a build sub-step throws

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 4000;   // same band as the lobby; the two are never visible together

            _scaler = canvasGo.AddComponent<CanvasScaler>();
            LobbyTheme.ConfigureScaler(_scaler);   // ScaleWithScreenSize @1920×1080, match flips at 16:9

            canvasGo.AddComponent<GraphicRaycaster>();

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

            _root = new GameObject("MultiplayerGatePanel");
            _root.transform.SetParent(_canvas.transform, false);
            var rect = _root.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;

            // Same full-screen page treatment as the lobby, so the two read as one product.
            var pageBg = _root.AddComponent<Image>();
            var pageFrame = _root.AddComponent<Outline>();
            LobbyTheme.ApplyPanelSkin(pageBg, pageFrame, LobbyTheme.PageBackdrop, LobbyTheme.HeaderBorder);

            // try/finally for the same reason LobbyPanel.Build has one: a throw here is swallowed by
            // OnMenuReady's catch, and without this the half-built page would leak onto the main menu.
            try
            {
                _gateRoot = BuildGateScreen();
                _joinRoot = BuildJoinScreen();
            }
            finally
            {
                _root.SetActive(true);
                if (_gateRoot != null) _gateRoot.SetActive(false);
                if (_joinRoot != null) _joinRoot.SetActive(false);
                if (_canvas != null) _canvas.gameObject.SetActive(false);
            }
        }

        // A screen root: full-bleed vertical stack, generous page padding, centred content.
        private GameObject NewScreen(string name, TextAnchor align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var pad = LobbyTheme.ScaledPadding;
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad * 4, pad * 4, pad * 3, pad * 2);
            vlg.spacing = pad;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = align;
            return go;
        }

        private Text AddTitle(GameObject parent, string caption)
        {
            var h = LobbyTheme.ScaledHeaderFontSize + LobbyTheme.ScaledPadding;
            var t = UiToolkit.CreateText(parent, "Title", Vector2.zero,
                new Vector2(LobbyTheme.Scale(600), h), caption,
                LobbyTheme.ScaledHeaderFontSize, TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));
            t.color = LobbyTheme.Accent;
            t.fontStyle = FontStyle.Bold;
            t.raycastTarget = false;
            var le = LobbyPanel.LE(t.gameObject);
            le.minHeight = h;
            le.flexibleHeight = 0;
            return t;
        }

        // A flexible vertical gap. Fixed cells around it carry real minimums, so this only ever eats
        // surplus (see PinColumnWidth's note in LobbyPanel for why a cell without a minimum collapses).
        private static void AddSpacer(GameObject parent)
        {
            var go = new GameObject("Spacer");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            LobbyPanel.LE(go).flexibleHeight = 1;
        }

        // A single-cell row that holds one fixed-size button, so the button keeps its own width inside a
        // childForceExpandWidth stack instead of being stretched across the page.
        private GameObject AddButtonRow(GameObject parent, string name, Vector2 size)
        {
            var row = new GameObject(name);
            row.transform.SetParent(parent.transform, false);
            row.AddComponent<RectTransform>();
            var le = LobbyPanel.LE(row);
            le.minHeight = size.y;
            le.flexibleHeight = 0;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            return row;
        }

        // ─── SCREEN 1: the gate ────────────────────────────────────────────

        private GameObject BuildGateScreen()
        {
            var screen = NewScreen("GateScreen", TextAnchor.UpperCenter);
            AddTitle(screen, "NETWORK GAME");
            AddSpacer(screen);

            var big = new Vector2(LobbyTheme.Scale(420), LobbyTheme.Scale(72));
            LobbyPanel.AddCardButton(AddButtonRow(screen, "CreateRow", big),
                "CreateBtn", "CREATE SESSION", big, () => _owner.OnGateCreate());
            LobbyPanel.AddCardButton(AddButtonRow(screen, "JoinRow", big),
                "JoinBtn", "JOIN SESSION", big, ShowJoin);

            AddSpacer(screen);

            var back = new Vector2(LobbyTheme.Scale(160), LobbyTheme.Scale(44));
            LobbyPanel.AddCardButton(AddButtonRow(screen, "BackRow", back),
                "BackBtn", "BACK", back, Hide);
            return screen;
        }

        // ─── SCREEN 2: join ────────────────────────────────────────────────

        private GameObject BuildJoinScreen()
        {
            var pad = LobbyTheme.ScaledPadding;
            var screen = NewScreen("JoinScreen", TextAnchor.UpperCenter);
            AddTitle(screen, "JOIN A SESSION");

            var labelH = LobbyTheme.ScaledSubFontSize + pad / 2;
            var label = UiToolkit.CreateText(screen, "TargetLabel", Vector2.zero,
                new Vector2(LobbyTheme.Scale(600), labelH),
                "IP ADDRESS, HOSTNAME OR INVITE CODE:",
                LobbyTheme.ScaledSubFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            label.color = LobbyTheme.SubText;
            label.raycastTarget = false;
            var lle = LobbyPanel.LE(label.gameObject); lle.minHeight = labelH; lle.flexibleHeight = 0;

            // Input + JOIN on one row. Same fixed-cell discipline as every other row in this UI: the
            // button pins minWidth AND preferredWidth (preferredWidth alone leaves the effective minimum
            // at zero, and the cell is then the first thing crushed to nothing and overdrawn), the input
            // is the one cell allowed to flex but keeps a floor so it can never become an unusable sliver.
            var rowH = LobbyTheme.ScaledRowHeight;
            var row = new GameObject("TargetRow");
            row.transform.SetParent(screen.transform, false);
            row.AddComponent<RectTransform>();
            var rle = LobbyPanel.LE(row); rle.minHeight = rowH; rle.flexibleHeight = 0;
            var rhlg = row.AddComponent<HorizontalLayoutGroup>();
            rhlg.spacing = pad / 2;
            rhlg.childControlWidth = true;
            rhlg.childControlHeight = true;
            rhlg.childForceExpandWidth = false;
            rhlg.childForceExpandHeight = true;
            rhlg.childAlignment = TextAnchor.MiddleLeft;

            var inputMinW = LobbyTheme.Scale(320);
            // NOTHING IS WIRED TO THE FIELD ITSELF. Joining is a deliberate act: the JOIN button beside
            // it, or Enter while the caret is in it (Tick polls UiToolkit.SubmittedThisFrame). The field
            // used to carry an onEndEdit handler, and Unity raises that on DESELECT too — so clicking the
            // box and then clicking the friends list below dialled a host nobody had chosen. Losing focus
            // now keeps the typed text and does nothing else.
            _target = UiToolkit.CreateInputField(row, "TargetInput", "",
                Vector2.zero, new Vector2(inputMinW, rowH), new Vector2(0f, 0f));
            // UiToolkit's default limit is 24 — sized for a nickname, not a connect target. A 19-symbol
            // Crockford key written with separators, or a long DNS name, runs past that and would be
            // silently truncated into something SmartJoinParser then reads as Invalid.
            _target.characterLimit = 64;
            var tle = LobbyPanel.LE(_target.gameObject);
            tle.minWidth = inputMinW;
            tle.preferredWidth = inputMinW;
            tle.flexibleWidth = 1;

            var joinW = LobbyTheme.Scale(140);
            LobbyPanel.AddCardButton(row, "GateJoinBtn", "JOIN", new Vector2(joinW, rowH), Submit);

            // No spacer between the card and BACK: the card already carries flexibleHeight 1, so it takes
            // ALL the slack down to the button row. A spacer here would only halve it.
            BuildFriendsCard(screen);

            var back = new Vector2(LobbyTheme.Scale(160), LobbyTheme.Scale(44));
            LobbyPanel.AddCardButton(AddButtonRow(screen, "JoinBackRow", back),
                "JoinBackBtn", "BACK", back, ShowGate);
            return screen;
        }

        private void BuildFriendsCard(GameObject parent)
        {
            var pad = LobbyTheme.ScaledPadding;

            var card = new GameObject("FriendsCard");
            card.transform.SetParent(parent.transform, false);
            card.AddComponent<RectTransform>();
            LobbyPanel.AddFramedPanel(card);
            LobbyPanel.LE(card).flexibleHeight = 1;

            var vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(pad, pad, pad / 2, pad / 2);
            vlg.spacing = pad / 4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperLeft;

            var hdrH = LobbyTheme.ScaledSectionHeaderFontSize + pad / 2;
            var hdr = UiToolkit.CreateText(card, "FriendsHdr", Vector2.zero,
                new Vector2(LobbyTheme.Scale(300), hdrH), "STEAM FRIENDS IN A SESSION",
                LobbyTheme.ScaledSectionHeaderFontSize, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            hdr.color = LobbyTheme.Accent;
            hdr.fontStyle = FontStyle.Bold;
            hdr.raycastTarget = false;
            var hle = LobbyPanel.LE(hdr.gameObject); hle.minHeight = hdrH; hle.flexibleHeight = 0;

            var sep = new GameObject("Separator");
            sep.transform.SetParent(card.transform, false);
            sep.AddComponent<RectTransform>();
            sep.AddComponent<Image>().color = LobbyTheme.Separator;
            var sle = LobbyPanel.LE(sep);
            sle.minHeight = LobbyTheme.ScaledSeparatorThickness;
            sle.flexibleHeight = 0;

            _friendsEmpty = UiToolkit.CreateText(card, "FriendsEmpty", Vector2.zero,
                new Vector2(LobbyTheme.Scale(300), LobbyTheme.ScaledRowHeight), "",
                LobbyTheme.ScaledSubFontSize, TextAnchor.MiddleLeft, new Vector2(0f, 1f));
            _friendsEmpty.color = LobbyTheme.MutedText;
            _friendsEmpty.raycastTarget = false;
            var ele = LobbyPanel.LE(_friendsEmpty.gameObject);
            ele.minHeight = LobbyTheme.ScaledRowHeight;
            ele.flexibleHeight = 0;

            _friendsList = new GameObject("FriendsList");
            _friendsList.transform.SetParent(card.transform, false);
            _friendsList.AddComponent<RectTransform>();
            var lvlg = _friendsList.AddComponent<VerticalLayoutGroup>();
            lvlg.spacing = LobbyTheme.ScaledPadding / 4;
            lvlg.childControlWidth = true;
            lvlg.childControlHeight = true;
            lvlg.childForceExpandWidth = true;
            lvlg.childForceExpandHeight = false;
            lvlg.childAlignment = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// Rebuild the friends rows from a fresh Steam snapshot. Everything Facepunch-typed stays inside
        /// <see cref="SteamInvite"/> — this only ever sees a name and a lobby id — which is the boundary
        /// that class exists to hold (its summary explains why: no Steamworks type may reach the mod
        /// assembly's exported types, or the mod loader resolves them at enable time).
        ///
        /// ponytail: a snapshot taken when the screen opens, not a poll. A friend who starts hosting
        /// while this screen is already up appears after BACK → JOIN SESSION; add a timer here if that
        /// turns out to matter in practice.
        /// </summary>
        private void RefreshFriends()
        {
            if (_friendsList == null) return;

            for (int i = _friendsList.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(_friendsList.transform.GetChild(i).gameObject);

            List<SteamInvite.HostingFriend> friends = SteamInvite.HostingFriends();

            if (_friendsEmpty != null)
            {
                _friendsEmpty.text = !SteamInvite.IsSteamAlive()
                    ? "Steam is not running — type an address or invite code above."
                    : friends.Count == 0
                        ? "No Steam friend is in a session right now."
                        : "";
                _friendsEmpty.gameObject.SetActive(friends.Count == 0);
            }

            var rowH = LobbyTheme.ScaledRowHeight;
            for (int i = 0; i < friends.Count; i++)
            {
                var f = friends[i];
                var lobbyId = f.LobbyId;   // captured per row, NOT the loop variable
                var btn = UiToolkit.CreateButton(_friendsList, "Friend" + i,
                    f.Name + "   —   JOIN", Vector2.zero,
                    new Vector2(LobbyTheme.Scale(300), rowH), new Vector2(0f, 1f),
                    () => _owner.OnGateJoinFriend(lobbyId));
                var rle = LobbyPanel.LE(btn.gameObject);
                rle.minHeight = rowH;
                rle.flexibleHeight = 0;
                // A persona name is arbitrary length and the label is an Overflow Text, so without a mask
                // a long one draws straight past the row — the same clip the roster's nickname cell uses.
                btn.gameObject.AddComponent<RectMask2D>();
                var lbl = btn.GetComponentInChildren<Text>();
                if (lbl != null)
                {
                    lbl.alignment = TextAnchor.MiddleLeft;
                    lbl.fontSize = LobbyTheme.ScaledRowFontSize;
                }
            }
        }

        // Both the JOIN button and Enter in the field route here, so the text is read and handed over
        // identically either way. An unreadable target is NOT rejected locally: OnLobbyJoin classifies it
        // and raises its own error box — a second check here would be a second opinion to keep in step.
        private void Submit()
        {
            _owner.OnGateJoin(_target != null ? _target.text : "");
        }

        // ─── Show / Hide / Back ────────────────────────────────────────────

        public void ShowGate() => ShowScreen(Screen.Gate);

        public void ShowJoin()
        {
            if (_target != null) _target.text = _owner.JoinPrefill();
            RefreshFriends();
            ShowScreen(Screen.Join);
        }

        private void ShowScreen(Screen screen)
        {
            if (_canvas == null || _root == null) return;
            _screen = screen;
            if (_gateRoot != null) _gateRoot.SetActive(screen == Screen.Gate);
            if (_joinRoot != null) _joinRoot.SetActive(screen == Screen.Join);

            if (!_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(true);
            _canvas.enabled = true;
            NativeWidgetFactory.HideMenuChrome(_canvas);
            _ownsChrome = true;
            MultiplayerUI.Instance?.HideInGameBar();
            if (_scaler != null) _scaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;
        }

        /// <summary>Close for real: the page goes down, the menu chrome comes back, and the logical screen
        /// is forgotten so a later <see cref="Reshow"/> cannot spring it open again. Safe to call when this
        /// panel was never open — it restores only chrome it hid itself.</summary>
        public void Hide()
        {
            _screen = Screen.None;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            ReleaseChrome();
        }

        /// <summary>Take the page down for a NATIVE screen (the game's MessageBox renders on the menu's own
        /// canvases, which HideMenuChrome disabled) while REMEMBERING which screen was up, so
        /// <see cref="Reshow"/> can restore it when the box is dismissed.</summary>
        public void HideForNativeScreen()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            ReleaseChrome();
        }

        /// <summary>Put back whichever screen <see cref="HideForNativeScreen"/> took down. A no-op when
        /// nothing was open, which is what makes it safe as a blanket "return from a native box" hook.</summary>
        public void Reshow()
        {
            if (_screen == Screen.None) return;
            ShowScreen(_screen);
        }

        /// <summary>Escape / the back gesture: JOIN → gate → main menu, never a half-open state. Returns
        /// false when no gate screen is open, so the caller can fall through to its own handling.</summary>
        public bool Back()
        {
            if (_screen == Screen.Join) { ShowGate(); return true; }
            if (_screen == Screen.Gate) { Hide(); return true; }
            return false;
        }

        /// <summary>Per-frame: keep the aspect-adaptive match live. The canvas outlives every resolution /
        /// windowed / monitor change, and the native rule is a function of the CURRENT aspect — the same
        /// one float write LobbyPanel.Refresh and the status-bar canvas already do. Also the join field's
        /// Enter key, which is polled rather than wired for the reason UiToolkit.CreateInputField gives.</summary>
        public void Tick()
        {
            if (!IsVisible) return;
            if (_scaler != null) _scaler.matchWidthOrHeight = LobbyTheme.CurrentMatch;
            if (_screen == Screen.Join && UiToolkit.SubmittedThisFrame(_target)) Submit();
        }

        // Restore the chrome ONLY if we are the panel that hid it. HideMenuChrome/RestoreMenuChrome are a
        // single global pair, so an unguarded restore from here would re-enable every native menu canvas
        // on top of a lobby that had legitimately taken the chrome over.
        private void ReleaseChrome()
        {
            if (!_ownsChrome) return;
            _ownsChrome = false;
            NativeWidgetFactory.RestoreMenuChrome();
        }
    }
}
