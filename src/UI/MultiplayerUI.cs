using System.Collections.Generic;
using Base.Core;
using Base.Serialization;
using Base.UI.MessageBox;
using Multipleer.Harmony;
using Multipleer.Network;
using Multipleer.Transport;
using Multipleer.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Multipleer.UI
{
    public class MultiplayerUI : MonoBehaviour
    {
        public static MultiplayerUI Instance { get; private set; }

        // ─── Minimal mod canvas — IN-GAME STATUS BAR ONLY ──────────────────
        // The lobby + save-picker now clone NATIVE widgets and parent under the game's
        // own main-menu Canvas (captured in OnMenuReady), so they need no overlay canvas.
        // The in-game status bar, however, is an in-GEOSCAPE element: the menu Canvas dies
        // on the menu→geoscape scene load, so the bar keeps a tiny dedicated overlay Canvas
        // that lives with ModGO. This is the only remaining from-code canvas.
        private Canvas _barCanvas;
        private Transform _barRoot;

        // ─── In-game bottom bar ─────────────────────────────────────────────
        private GameObject _inGameBar;
        private Text _barStatusText;

        // ─── Lobby panel (built once the menu Canvas is captured) ───────────
        private LobbyPanel _lobby;
        private SavePickerPanel _savePicker;
        private bool _panelsBuilt;

        // True while the lobby overlay is on-screen. Consumed by the main-menu Escape patch
        // (MainMenuPatches) so Escape leaves the lobby instead of opening the Options menu.
        public bool IsLobbyOpen => _lobby?.IsVisible == true;

        private void Awake()
        {
            Instance = this;

            // Bar canvas first so the in-geoscape status bar has a valid render root.
            EnsureBarCanvas();
            CreateInGameBar();
            _inGameBar.SetActive(false);

            // Panels are built lazily in OnMenuReady (they need the native menu Canvas).
            _lobby = new LobbyPanel(this);
            _savePicker = new SavePickerPanel();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Menu hook — native Canvas captured by InjectNetworkButtonPatch
        // ═══════════════════════════════════════════════════════════════════

        // Called from InjectNetworkButtonPatch.Postfix once the main-menu Canvas + native
        // button template are captured. Builds the lobby + save-picker as cloned NATIVE
        // widgets parented to the menu's own Canvas (native look + correct CanvasScaler/sort).
        public void OnMenuReady(Canvas menuCanvas)
        {
            if (_panelsBuilt || menuCanvas == null) return;
            _lobby.Build(menuCanvas);
            _savePicker.Build(menuCanvas);
            _panelsBuilt = true;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Minimal overlay Canvas for the in-game status bar
        // ═══════════════════════════════════════════════════════════════════

        private Transform EnsureBarCanvas()
        {
            if (_barRoot != null) return _barRoot;

            var go = new GameObject("MultipleerBarCanvas");
            // Parent under ModGO so the Canvas shares the mod's persistent lifetime.
            go.transform.SetParent(transform, false);

            _barCanvas = go.AddComponent<Canvas>();
            _barCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _barCanvas.sortingOrder = 5000;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _barRoot = go.transform;
            return _barRoot;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Main entry — called from button click
        // ═══════════════════════════════════════════════════════════════════

        public void ShowNetworkMenu()
        {
            if (NetworkEngine.Instance?.IsActive == true)
            {
                // Already in a session → just (re)show the live lobby panel.
                _lobby?.Show();
                return;
            }
            StartHostAndOpenLobby();
        }

        // Default DirectIP listen port for the host (matches the client connect default).
        private const int DefaultDirectPort = 14242;

        // CREATE (lobby-first): start hosting on Direct + STUN + Steam SIMULTANEOUSLY in one
        // session and open the lobby panel immediately. A joiner arriving by DirectIP, by STUN
        // invite-code, or by Steam all land in the same peer list (CompositeTransport namespaces
        // their ids). No campaign is loaded here — the host picks a save only after everyone is
        // ready (Play). CompositeTransport.Host wraps each child so one failing transport (e.g.
        // Steam unavailable) does not abort the others.
        private void StartHostAndOpenLobby()
        {
            NetworkEngine.Create();
            var composite = new CompositeTransport(new ITransport[]
            {
                new DirectTransport(),
                new StunTransport(),
                new SteamTransport()
            });
            NetworkEngine.Instance.Initialize(composite);
            NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
            NetworkEngine.Instance.StartHost(DefaultDirectPort);
            ShowInGameBar();
            _lobby?.Show();
        }

        // ═══ Lobby panel callbacks ═════════════════════════════════════════

        // LEAVE button on the lobby panel: tear down the session and hide the lobby + bar.
        public void OnLobbyLeave()
        {
            _lobby?.Hide();
            OnDisconnectClicked();
        }

        // Ready toggle from the lobby panel.
        public void OnLobbyToggleReady()
        {
            var engine = NetworkEngine.Instance;
            if (engine?.Session == null) return;

            if (engine.IsHost)
            {
                // Host toggles its own ready state locally, then re-broadcasts the roster.
                engine.Session.HostReady = !engine.Session.HostReady;
                engine.Session.BroadcastPeerList();
            }
            else
            {
                // Client → host ClientReady (foundation keys by sender; host broadcasts updated roster).
                engine.Session.SetClientReady(engine.LocalSteamId);
            }
        }

        // Nickname edit committed in the lobby panel.
        public void OnLobbyRename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            NetworkEngine.Instance?.Session?.SendRename(newName.Trim());
        }

        // Host PLAY now transfers the already-chosen save (rail-selected); if none chosen, open
        // the picker as a last-chance fallback.
        public void OnLobbyPlay()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            if (_pendingChosenSave != null)
            {
                engine.SaveTransfer?.HostStartSession(_pendingChosenSave);
                return;
            }
            // No save chosen in the rail yet → open the picker as a fallback.
            _savePicker?.Show(chosen =>
            {
                _pendingChosenSave = chosen;
                engine.Session?.SetChosenSave(SaveDisplayName(chosen), SaveDisplayMeta(chosen));
                NetworkEngine.Instance?.SaveTransfer?.HostStartSession(chosen);
            });
        }

        // ─── Smart-Join (footer Join…) ─────────────────────────────────────

        // Native ShowInputPrompt → classify → drop our own host lobby → join as client.
        public void OnLobbyJoin(string input)
        {
            var target = SmartJoinParser.Parse(input ?? "");
            if (target.Kind == JoinKind.Invalid)
            {
                var mbErr = GameUtl.GetMessageBox();
                mbErr?.ShowSimplePrompt("Could not read that address or code.",
                    MessageBoxIcon.Error, MessageBoxButtons.OK, null, this);
                return;
            }

            // Tear down the auto-hosted session, then connect as a client (overlay stays open;
            // Update's _lobby.Refresh() re-binds it to the new client session).
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();

            NetworkEngine.Create();
            switch (target.Kind)
            {
                case JoinKind.DirectIp:
                    NetworkEngine.Instance.Initialize(TransportType.DirectIP);
                    NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                    NetworkEngine.Instance.JoinGame(target.Ip, target.Port);
                    break;
                case JoinKind.StunCode:
                    NetworkEngine.Instance.Initialize(TransportType.StunUDP);
                    NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                    NetworkEngine.Instance.JoinGame($"{target.Ip}:{target.Port}", 0);
                    break;
                case JoinKind.SteamId:
                    NetworkEngine.Instance.Initialize(TransportType.SteamP2P);
                    NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                    NetworkEngine.Instance.JoinGame(target.SteamId.ToString(), 0);
                    break;
            }
            ShowInGameBar();
            _lobby?.Show();
        }

        // Footer Join… button → native modal input → OnLobbyJoin.
        public void OnLobbyJoinPrompt()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;
            mb.ShowInputPrompt("Paste IP or code… (auto-detect)", "", null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    if (res.DialogResult != MessageBoxResult.OK) return;
                    OnLobbyJoin(res.InputTextResult ?? "");
                }, this);
        }

        // ─── Nickname rename (own roster row pencil) ───────────────────────

        public void OnLobbyRenamePrompt()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;
            var current = NetworkEngine.Instance?.IsHost == true
                ? NetworkEngine.Instance.Session?.HostNickname ?? ""
                : SystemInfo.deviceName;
            mb.ShowInputPrompt("New nickname", current, null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    if (res.DialogResult != MessageBoxResult.OK) return;
                    OnLobbyRename(res.InputTextResult ?? "");
                }, this);
        }

        // ─── Chat send ─────────────────────────────────────────────────────

        public void OnLobbyChatSend(string text)
        {
            NetworkEngine.Instance?.Session?.SendChat(text);
        }

        // ─── Choose save (host only) ───────────────────────────────────────

        // Host rail "Choose save…": pick a save NOW (before Play). Record + broadcast it.
        public void OnLobbyChooseSave()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            _savePicker?.Show(chosen =>
            {
                _pendingChosenSave = chosen;
                var name = SaveDisplayName(chosen);
                var meta = SaveDisplayMeta(chosen);
                engine.Session?.SetChosenSave(name, meta);
            });
        }

        // The save the host chose in the rail (consumed by PLAY); null until a pick is made.
        public SavegameMetaData PendingChosenSave => _pendingChosenSave;
        private SavegameMetaData _pendingChosenSave;

        private static string SaveDisplayName(SavegameMetaData meta)
        {
            if (meta == null) return "(none)";
            var name = meta.Name;
            if (meta is PPSavegameMetaData pp
                && !string.IsNullOrEmpty(pp.UserSetName))
                name = pp.UserSetName;
            return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }

        private static string SaveDisplayMeta(SavegameMetaData meta)
        {
            try { return meta?.SaveCreated?.DisplayDateTime ?? ""; }
            catch { return ""; }
        }

        // ─── Connect rail values + clipboard ───────────────────────────────

        // Host STUN short code (or a status placeholder while discovering / on failure).
        // The host's CompositeTransport always includes a hosting StunTransport, so the code
        // shows whenever discovery succeeds — alongside the DirectIP and Steam rail values.
        public string GetRailStunCode()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return "(host on STUN to share)";
            var stun = FindHostingChild(TransportType.StunUDP);
            if (stun == null) return "(host on STUN to share)";
            if (stun.PublicEndPoint == null)
            {
                // Mirror the StunTransport discovery thread's state messages (it retries across a
                // pool of Google STUN servers and only marks itself unavailable after exhausting all
                // rounds). "unavailable" = environmentally blocked (symmetric NAT / firewall); the
                // rail re-reads every frame, so a late success still appears live without reopening.
                if (stun.LocalEndpoint.Contains("unavailable"))
                    return "unavailable (NAT/firewall)";
                return "discovering…";
            }
            var code = ConnectCode.Encode(stun.PublicEndPoint);
            return code ?? "unavailable (NAT/firewall)";
        }

        // Resolve the live hosting child transport of a given type from the host's transport,
        // whether that transport is a CompositeTransport (multi-listen) or a single transport.
        private static ITransport FindHostingChild(TransportType type)
        {
            var t = NetworkEngine.Instance?.Transport;
            if (t == null) return null;
            if (t is CompositeTransport composite)
            {
                foreach (var child in composite.Children)
                    if (child.TransportType == type && child.IsHost) return child;
                return null;
            }
            return t.TransportType == type && t.IsHost ? t : null;
        }

        public void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIUtility.systemCopyBuffer = text;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Connection events
        // ═══════════════════════════════════════════════════════════════════

        private void OnConnectionFailed(string reason)
        {
            var mb = GameUtl.GetMessageBox();
            if (mb != null)
            {
                mb.ShowSimplePrompt($"Connection failed: {reason}",
                    MessageBoxIcon.Error, MessageBoxButtons.OK, null, this);
            }
        }

        private void OnDisconnectClicked()
        {
            NetworkEngine.Instance?.Disconnect();
            NetworkEngine.Instance?.Shutdown();
            _inGameBar.SetActive(false);
            _lobby?.Hide();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  In-game bar
        // ═══════════════════════════════════════════════════════════════════

        public void ShowInGameBar()
        {
            _inGameBar.SetActive(true);
        }

        public void HideInGameBar()
        {
            _inGameBar.SetActive(false);
        }

        private void CreateInGameBar()
        {
            var bar = new GameObject("MultipleerStatusBar");
            bar.transform.SetParent(EnsureBarCanvas(), false);
            var rect = bar.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.sizeDelta = new Vector2(0, 28);
            rect.anchoredPosition = new Vector2(0, 0);

            var bg = bar.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.7f);

            _barStatusText = CreateText(bar, "Status",
                new Vector2(10, 2), new Vector2(500, 24),
                "Not connected");

            _inGameBar = bar;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Update
        // ═══════════════════════════════════════════════════════════════════

        private void Update()
        {
            var engine = NetworkEngine.Instance;
            if (engine?.IsActive == true)
            {
                engine.Update();

                if (_barStatusText != null)
                {
                    _barStatusText.text = $"MP: {engine.Transport?.State ?? ConnectionState.Disconnected}" +
                        $" | {(engine.IsHost ? "Host" : "Client")}" +
                        $" | {engine.Session?.ClientCount ?? 0} player(s)";
                }
            }
            else
            {
                if (_barStatusText != null)
                    _barStatusText.text = "Not connected";
            }

            // Refresh the lobby panel EVERY frame while it is visible — UNCONDITIONALLY, i.e. not
            // gated on engine.IsActive. This is the fix for the "CONNECT rail empty on first open"
            // bug: the rail values (IP / Same-PC / STUN code) are re-pushed by LobbyPanel.Refresh,
            // so they fill within one frame of the host becoming ready on the FIRST open (no need to
            // reopen the lobby). It also makes a late-arriving STUN code appear LIVE once background
            // discovery resolves. Refresh() self-guards (hides itself if the engine is null/inactive
            // or the session has started), so calling it outside the IsActive branch is safe; it only
            // does real work while the panel is actually on screen.
            if (_lobby != null && _lobby.IsVisible)
                _lobby.Refresh();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════════

        private static Text CreateText(GameObject parent, string name,
            Vector2 pos, Vector2 size, string content)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 12;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            return text;
        }

        public void InvitePlayers()
        {
            try
            {
                var asm = FindSteamworksAssembly();
                if (asm == null)
                {
                    ShowSteamNotAvailable();
                    return;
                }

                var utilsType = asm.GetType("Steamworks.SteamUtils");
                if (utilsType != null)
                {
                    var overlayProp = utilsType.GetProperty("IsOverlayEnabled",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (overlayProp != null)
                    {
                        var enabled = (bool)overlayProp.GetValue(null, null);
                        if (!enabled)
                        {
                            ShowSteamOverlayUnavailable();
                            return;
                        }
                    }
                }

                var friendsType = asm.GetType("Steamworks.SteamFriends");
                if (friendsType == null)
                {
                    ShowSteamNotAvailable();
                    return;
                }

                var method = friendsType.GetMethod("OpenOverlay",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(string) }, null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { "friends" });
                    return;
                }

                ShowSteamNotAvailable();
            }
            catch { ShowSteamNotAvailable(); }
        }

        private static void ShowSteamNotAvailable()
        {
            var mb = GameUtl.GetMessageBox();
            mb?.ShowSimplePrompt("Steam overlay is not available.",
                MessageBoxIcon.Error, MessageBoxButtons.OK, null, null);
        }

        private static void ShowSteamOverlayUnavailable()
        {
            var mb = GameUtl.GetMessageBox();
            mb?.ShowSimplePrompt("Steam overlay is disabled.\nEnable it in Steam settings.",
                MessageBoxIcon.Warning, MessageBoxButtons.OK, null, null);
        }

        private static System.Reflection.Assembly FindSteamworksAssembly()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name == "Facepunch.Steamworks.Win64")
                    return asm;
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pause menu — shown when NETWORK is clicked during gameplay
        // ═══════════════════════════════════════════════════════════════════

        public void ShowPauseMenu()
        {
            var engine = NetworkEngine.Instance;
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            if (engine == null || !engine.IsActive)
            {
                StartHostAndOpenLobby();
                return;
            }

            var info = BuildConnectionInfo(engine);
            var labels = new Dictionary<MessageBoxButtons, string>();

            labels[MessageBoxButtons.Yes] = "DISCONNECT";
            labels[MessageBoxButtons.Cancel] = "CLOSE";

            var transport = engine.Transport;
            if (transport != null)
            {
                if (transport.TransportType == TransportType.SteamP2P)
                    labels[MessageBoxButtons.No] = "COPY STEAM ID";
                else if (transport.TransportType == TransportType.DirectIP)
                    labels[MessageBoxButtons.No] = "COPY IP:PORT";
                else
                    labels[MessageBoxButtons.No] = "COPY STUN CODE";
            }
            else
            {
                labels[MessageBoxButtons.No] = "COPY INFO";
            }

            labels[MessageBoxButtons.Abort] = "INVITE FRIEND";

            mb.ShowSimplePrompt(info, MessageBoxIcon.Information,
                MessageBoxButtons.Yes | MessageBoxButtons.No | MessageBoxButtons.Abort | MessageBoxButtons.Cancel,
                labels, OnPauseMenuResult, this);
        }

        private static string BuildConnectionInfo(NetworkEngine engine)
        {
            var transport = engine.Transport;
            var transportName = transport != null ? transport.TransportType.ToString() : "None";
            var endpoint = transport?.LocalEndpoint ?? "N/A";
            var role = engine.IsHost ? "HOST" : "CLIENT";
            var steamId = engine.LocalSteamId != 0 ? engine.LocalSteamId.ToString() : "N/A";

            var clientsStr = "";
            if (engine.Session != null)
            {
                var count = engine.Session.ClientCount;
                clientsStr = $"\n  Players: {count + 1} (including you)";
                if (count > 0)
                {
                    clientsStr += "\n  Connected:";
                    foreach (var c in engine.Session.GetConnectedClients())
                        clientsStr += $"\n    \u2022 {c}";
                }
            }

            var copyable = GetCopyableInfo(engine);

            return $"Network Session\n" +
                   $"\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n" +
                   $"Transport: {transportName}\n" +
                   $"Role: {role}\n" +
                   $"Steam ID: {steamId}\n" +
                   $"Endpoint: {endpoint}" +
                   clientsStr +
                   $"\n\nCopyable: {copyable}";
        }

        private static string GetCopyableInfo(NetworkEngine engine)
        {
            if (engine.Transport == null) return engine.LocalSteamId.ToString();

            switch (engine.Transport.TransportType)
            {
                case TransportType.SteamP2P:
                    return engine.LocalSteamId.ToString();
                case TransportType.DirectIP:
                    return engine.Transport.LocalEndpoint
                        .Replace("DirectIP(host:", "")
                        .Replace("DirectIP(client:", "")
                        .Replace(")", "");
                case TransportType.StunUDP:
                    return engine.Transport.LocalEndpoint
                        .Replace("STUN(", "")
                        .Replace(")", "");
                default:
                    return engine.Transport.LocalEndpoint;
            }
        }

        private void OnPauseMenuResult(MessageBoxCallbackResult res)
        {
            if (res.DialogResult == MessageBoxResult.Yes)
            {
                OnDisconnectClicked();
            }
            else if (res.DialogResult == MessageBoxResult.No)
            {
                var engine = NetworkEngine.Instance;
                if (engine != null)
                {
                    var text = GetCopyableInfo(engine);
                    GUIUtility.systemCopyBuffer = text;
                    var mb = GameUtl.GetMessageBox();
                    if (mb != null)
                        mb.ShowSimplePrompt($"Copied to clipboard:\n{text}",
                            MessageBoxIcon.Information, MessageBoxButtons.OK, null, this);
                }
            }
            else if (res.DialogResult == MessageBoxResult.Abort)
            {
                InvitePlayers();
            }
        }
    }
}
