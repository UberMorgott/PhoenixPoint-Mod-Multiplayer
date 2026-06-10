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

        private GameObject _root;
        private Text _roleText;
        private Text _connectText;

        // Interactive controls (T3). _nameField is no longer built into the full-screen tree
        // (rename moved to the native prompt in Task 14); RefreshControls still reads it until
        // Task 14 rewires it, so the now-unassigned field is intentionally tolerated here.
#pragma warning disable CS0649 // _nameField assigned nowhere after the layout rewrite (Task 14 drops it)
        private InputField _nameField;
#pragma warning restore CS0649
        private Button _leaveButton;
        private Button _readyButton;
        private Text _readyButtonLabel;
        private Toggle _readyToggle;
        private Button _playButton;
        private bool _nameInitialized;

        // ─── Full-screen 5-zone layout ─────────────────────────────────────
        private Text _railIpValue;
        private Text _railLocalValue;       // 127.0.0.1:<port> for same-PC two-instance play
        private Text _railStunValue;
        private Text _railSaveValue;
        private Button _chooseSaveBtn;
        private Button _inviteBtn;

        // Chat zone. The render/subscribe bookkeeping fields are consumed by Refresh in Task 14;
        // they are declared here (alongside the zone they belong to) but not yet read.
        private readonly ChatLog _chat = new ChatLog(100);
#pragma warning disable CS0414, CS0169 // consumed by chat Refresh wiring in Task 14
        private int _chatRenderedVersion = -1;
        private bool _chatSubscribed;
#pragma warning restore CS0414, CS0169
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
        }

        public bool IsVisible => _root != null && _root.activeSelf;

        public LobbyPanel(MultiplayerUI owner)
        {
            _owner = owner;
        }

        // ─── Build (once) ──────────────────────────────────────────────────

        // Build under the native main-menu Canvas. Buttons are cloned native widgets
        // (TemplateMenuButton via NativeWidgetFactory) so they match the game's look; the
        // panel background, roster rows and nickname field stay from-code (documented
        // per-widget fallbacks — no cheap native template at the menu for these).
        public void Build(Canvas menuCanvas)
        {
            if (_root != null || menuCanvas == null) return;

            _root = new GameObject("MultipleerLobbyPanel");
            _root.transform.SetParent(menuCanvas.transform, false);

            var rect = _root.AddComponent<RectTransform>();
            // Full-screen: stretch anchors 0..1, zero offsets (was ~85% inset).
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // Background: native panel sprite if capturable, else a solid dark fill.
            var bg = _root.AddComponent<Image>();
            var nativeSprite = NativeWidgetFactory.TryGetPanelBackgroundSprite();
            if (nativeSprite != null) { bg.sprite = nativeSprite; bg.type = Image.Type.Sliced; bg.color = Color.white; }
            else bg.color = new Color(0.05f, 0.06f, 0.08f, 0.96f);

            BuildTopBar();
            BuildConnectRail();
            BuildChatZone();
            BuildRosterZone();
            BuildFooter();

            _root.SetActive(false);
        }

        // TOP BAR: title + subtitle (no X/4 counter — small count lives in the roster header).
        private void BuildTopBar()
        {
            UiToolkit.CreateText(_root, "Title", new Vector2(0, -24),
                new Vector2(600, 36), "CO-OP LOBBY", 24, TextAnchor.MiddleCenter,
                new Vector2(0.5f, 1f));
            _roleText = UiToolkit.CreateText(_root, "Subtitle", new Vector2(0, -60),
                new Vector2(700, 24), "your lobby", 14, TextAnchor.MiddleCenter,
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

            UiToolkit.CreateText(rail, "ConnectHdr", new Vector2(0, 0),
                new Vector2(260, 24), "CONNECT", 16, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            UiToolkit.CreateText(rail, "IpLabel", new Vector2(0, -34),
                new Vector2(260, 20), "Your IP (LAN):", 12, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railIpValue = MakeCopyableValue(rail, "IpValue", new Vector2(0, -54),
                () => _owner.GetRailIp());

            // Same-PC value: 127.0.0.1:<port> so a second instance on THIS machine can connect.
            UiToolkit.CreateText(rail, "LocalLabel", new Vector2(0, -82),
                new Vector2(260, 20), "Same PC (2nd instance):", 12, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railLocalValue = MakeCopyableValue(rail, "LocalValue", new Vector2(0, -102),
                () => _owner.GetRailLocalIp());

            UiToolkit.CreateText(rail, "StunLabel", new Vector2(0, -130),
                new Vector2(260, 20), "STUN code:", 12, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railStunValue = MakeCopyableValue(rail, "StunValue", new Vector2(0, -150),
                () => _owner.GetRailStunCode());

            UiToolkit.CreateText(rail, "SaveLabel", new Vector2(0, -190),
                new Vector2(260, 20), "Save to load:", 12, TextAnchor.UpperLeft, new Vector2(0f, 1f));
            _railSaveValue = UiToolkit.CreateText(rail, "SaveValue", new Vector2(0, -210),
                new Vector2(260, 40), "(none)", 13, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            _chooseSaveBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "ChooseSaveBtn",
                "CHOOSE SAVE…", () => _owner.OnLobbyChooseSave());
            if (_chooseSaveBtn != null) AnchorButton(_chooseSaveBtn, new Vector2(0f, 1f), new Vector2(0, -254));
            else _chooseSaveBtn = UiToolkit.CreateButton(rail, "ChooseSaveBtn", "CHOOSE SAVE…",
                new Vector2(0, -254), new Vector2(220, 36), new Vector2(0f, 1f),
                () => _owner.OnLobbyChooseSave());

            _inviteBtn = NativeWidgetFactory.CloneMenuButton(rail.transform, "InviteBtn",
                "INVITE VIA STEAM", () => _owner.InvitePlayers());
            if (_inviteBtn != null) AnchorButton(_inviteBtn, new Vector2(0f, 1f), new Vector2(0, -298));
            else _inviteBtn = UiToolkit.CreateButton(rail, "InviteBtn", "INVITE VIA STEAM",
                new Vector2(0, -298), new Vector2(220, 36), new Vector2(0f, 1f),
                () => _owner.InvitePlayers());
        }

        // Click-to-copy value text: a button whose label is the live value; click copies it.
        private Text MakeCopyableValue(GameObject parent, string name, Vector2 pos, System.Func<string> getValue)
        {
            var btn = UiToolkit.CreateButton(parent, name, "", pos, new Vector2(240, 22),
                new Vector2(0f, 1f), () => _owner.CopyToClipboard(getValue()));
            var label = btn.GetComponentInChildren<Text>();
            if (label != null) { label.alignment = TextAnchor.MiddleLeft; label.fontSize = 13; }
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

            UiToolkit.CreateText(chat, "ChatHdr", new Vector2(0, 0),
                new Vector2(300, 24), "CHAT", 16, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            // Scroll area (native scroller content if capturable, else a plain stacked rect).
            var scrollHost = new GameObject("ChatScrollHost");
            scrollHost.transform.SetParent(chat.transform, false);
            var shrt = scrollHost.AddComponent<RectTransform>();
            shrt.anchorMin = new Vector2(0f, 0f);
            shrt.anchorMax = new Vector2(1f, 1f);
            shrt.offsetMin = new Vector2(0, 40);
            shrt.offsetMax = new Vector2(0, -28);

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

            _connectText = UiToolkit.CreateText(players, "PlayersHdr", new Vector2(0, 0),
                new Vector2(300, 24), "PLAYERS (0)", 16, TextAnchor.UpperLeft, new Vector2(0f, 1f));

            _rosterArea = new GameObject("RosterArea");
            _rosterArea.transform.SetParent(players.transform, false);
            var rosterRect = _rosterArea.AddComponent<RectTransform>();
            rosterRect.anchorMin = new Vector2(0f, 1f);
            rosterRect.anchorMax = new Vector2(1f, 1f);
            rosterRect.pivot = new Vector2(0.5f, 1f);
            rosterRect.sizeDelta = new Vector2(0, 400);
            rosterRect.anchoredPosition = new Vector2(0, -34);
        }

        // FOOTER: Leave / Join… / Ready / Play (host).
        private void BuildFooter()
        {
            _leaveButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "LeaveBtn", "LEAVE",
                () => _owner.OnLobbyLeave());
            if (_leaveButton != null) AnchorButton(_leaveButton, new Vector2(0f, 0f), new Vector2(24, 20));
            else _leaveButton = UiToolkit.CreateButton(_root, "LeaveBtn", "LEAVE",
                new Vector2(24, 20), new Vector2(140, 40), new Vector2(0f, 0f),
                () => _owner.OnLobbyLeave());

            _joinButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "JoinBtn", "JOIN…",
                () => _owner.OnLobbyJoinPrompt());
            if (_joinButton != null) AnchorButton(_joinButton, new Vector2(0.35f, 0f), new Vector2(0, 20));
            else _joinButton = UiToolkit.CreateButton(_root, "JoinBtn", "JOIN…",
                new Vector2(0, 20), new Vector2(140, 40), new Vector2(0.35f, 0f),
                () => _owner.OnLobbyJoinPrompt());

            // Ready: native toggle if capturable, else label-flip menu button (fallback).
            _readyToggle = NativeWidgetFactory.CloneReadyToggle(_root.transform, "READY",
                _ => _owner.OnLobbyToggleReady());
            if (_readyToggle != null)
            {
                var trt = _readyToggle.transform as RectTransform;
                if (trt != null) { trt.anchorMin = new Vector2(0.6f, 0f); trt.anchorMax = new Vector2(0.6f, 0f);
                    trt.pivot = new Vector2(0.5f, 0f); trt.anchoredPosition = new Vector2(0, 24); }
            }
            else
            {
                _readyButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "ReadyBtn", "READY",
                    () => _owner.OnLobbyToggleReady());
                if (_readyButton != null) AnchorButton(_readyButton, new Vector2(0.6f, 0f), new Vector2(0, 20));
                else _readyButton = UiToolkit.CreateButton(_root, "ReadyBtn", "READY",
                    new Vector2(0, 20), new Vector2(160, 40), new Vector2(0.6f, 0f),
                    () => _owner.OnLobbyToggleReady());
                _readyButtonLabel = _readyButton != null ? _readyButton.GetComponentInChildren<Text>() : null;
            }

            _playButton = NativeWidgetFactory.CloneMenuButton(_root.transform, "PlayBtn", "PLAY ▸",
                () => _owner.OnLobbyPlay());
            if (_playButton != null) AnchorButton(_playButton, new Vector2(1f, 0f), new Vector2(-24, 20));
            else _playButton = UiToolkit.CreateButton(_root, "PlayBtn", "PLAY ▸",
                new Vector2(-24, 20), new Vector2(140, 40), new Vector2(1f, 0f),
                () => _owner.OnLobbyPlay());
        }

        // Position a cloned native button: anchor its root RectTransform to a corner of the
        // panel without resizing it (the native prefab carries its own size/visuals).
        private static void AnchorButton(Button btn, Vector2 anchor, Vector2 offset)
        {
            var rt = btn.transform as RectTransform;
            if (rt == null) return;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = offset;
        }

        // ─── Show / Hide ───────────────────────────────────────────────────

        public void Show()
        {
            if (_root == null) return;
            _root.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        // ─── Per-frame refresh (driven from MultiplayerUI.Update) ──────────

        public void Refresh()
        {
            if (_root == null || !_root.activeSelf) return;

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

            var role = engine.IsHost ? "HOST" : "CLIENT";
            var count = (engine.Session?.ClientCount ?? 0) + (engine.IsHost ? 1 : 0);
            if (_roleText != null)
                _roleText.text = $"You are: {role}    Players connected: {count}";

            if (_connectText != null)
            {
                var ep = engine.Transport?.LocalEndpoint ?? "";
                var tt = engine.Transport?.TransportType.ToString() ?? "None";
                _connectText.text = $"{tt}   {ep}";
            }

            var roster = RefreshRoster(engine);
            RefreshControls(engine, roster);
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

            // One-time: seed the nickname field from my current roster nickname so edits start
            // from the real value (avoid clobbering whatever the player is mid-typing afterwards).
            if (!_nameInitialized && me != null && _nameField != null)
            {
                if (!string.IsNullOrEmpty(me.Nickname))
                    _nameField.text = me.Nickname;
                _nameInitialized = true;
            }

            // Ready button label reflects my own readiness.
            if (_readyButtonLabel != null)
                _readyButtonLabel.text = (me != null && me.Ready) ? "READY ✓" : "NOT READY";

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

                row.Label.text = $"{name}{tags}    -    {status}";
                row.Label.color = color;
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
                new Vector2(RowWidth - 20, RowHeight), "", 14, TextAnchor.MiddleLeft,
                new Vector2(0f, 0.5f));

            return new RosterRow { Go = go, Label = label };
        }
    }
}
