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
        private Text _titleText;
        private Text _roleText;
        private Text _connectText;

        // Interactive controls (T3).
        private InputField _nameField;
        private Button _readyButton;
        private Text _readyButtonLabel;
        private Button _playButton;
        private bool _nameInitialized;

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

        public void Build(Transform parent)
        {
            if (_root != null) return;

            _root = new GameObject("MultipleerLobbyPanel");
            _root.transform.SetParent(parent, false);

            var rect = _root.AddComponent<RectTransform>();
            // Centered panel, fixed size.
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(560, 420);
            rect.anchoredPosition = Vector2.zero;

            var bg = _root.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.06f, 0.08f, 0.92f);

            _titleText = UiToolkit.CreateText(_root, "Title",
                new Vector2(0, -28), new Vector2(540, 30), "MULTIPLAYER LOBBY",
                18, TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));

            _roleText = UiToolkit.CreateText(_root, "Role",
                new Vector2(0, -64), new Vector2(540, 24), "",
                14, TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));

            _connectText = UiToolkit.CreateText(_root, "Connect",
                new Vector2(0, -94), new Vector2(540, 24), "",
                12, TextAnchor.MiddleCenter, new Vector2(0.5f, 1f));

            // Roster area: rows are created lazily in RefreshRoster, anchored top-center.
            _rosterArea = new GameObject("RosterArea");
            _rosterArea.transform.SetParent(_root.transform, false);
            var rosterRect = _rosterArea.AddComponent<RectTransform>();
            rosterRect.anchorMin = new Vector2(0.5f, 1f);
            rosterRect.anchorMax = new Vector2(0.5f, 1f);
            rosterRect.pivot = new Vector2(0.5f, 1f);
            rosterRect.sizeDelta = new Vector2(RowWidth, 260);
            rosterRect.anchoredPosition = new Vector2(0, -RosterTop);

            // Nickname label + edit field (just under the connect line, top area).
            UiToolkit.CreateText(_root, "NameLabel", new Vector2(20, -110),
                new Vector2(90, 24), "Nickname:", 12, TextAnchor.MiddleLeft, new Vector2(0f, 1f));
            _nameField = UiToolkit.CreateInputField(_root, "NameField", "",
                new Vector2(110, -110), new Vector2(220, 26), new Vector2(0f, 1f),
                v => _owner.OnLobbyRename(v));

            // Leave button (bottom-left).
            UiToolkit.CreateButton(_root, "LeaveBtn", "LEAVE",
                new Vector2(20, 20), new Vector2(140, 40), new Vector2(0f, 0f),
                () => _owner.OnLobbyLeave());

            // Ready toggle (bottom-center).
            _readyButton = UiToolkit.CreateButton(_root, "ReadyBtn", "READY",
                new Vector2(0, 20), new Vector2(160, 40), new Vector2(0.5f, 0f),
                () => _owner.OnLobbyToggleReady());
            _readyButtonLabel = _readyButton.GetComponentInChildren<Text>();

            // Play button (bottom-right, host only — gated on all-ready).
            _playButton = UiToolkit.CreateButton(_root, "PlayBtn", "PLAY",
                new Vector2(-20, 20), new Vector2(140, 40), new Vector2(1f, 0f),
                () => _owner.OnLobbyPlay());

            _root.SetActive(false);
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
            if (_playButton != null)
            {
                var playable = engine.IsHost && AllReady(roster);
                if (_playButton.gameObject.activeSelf != engine.IsHost)
                    _playButton.gameObject.SetActive(engine.IsHost);
                UiToolkit.SetButtonInteractable(_playButton, playable);
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
