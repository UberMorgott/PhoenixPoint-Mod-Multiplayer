using System.Collections.Generic;
using Base.Core;
using Base.UI.MessageBox;
using Multipleer.Harmony;
using Multipleer.Network;
using Multipleer.Transport;
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
            ShowMainDialog();
        }

        // Default DirectIP listen port for the host (matches the client connect default).
        private const int DefaultDirectPort = 14242;

        // ═══════════════════════════════════════════════════════════════════
        //  Dialog flow — native game MessageBox
        // ═══════════════════════════════════════════════════════════════════

        private void ShowMainDialog()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            mb.ShowSimplePrompt("", MessageBoxIcon.None, MessageBoxButtons.YesNoCancel,
                new Dictionary<MessageBoxButtons, string>
                {
                    { MessageBoxButtons.Yes, "CREATE GAME" },
                    { MessageBoxButtons.No, "JOIN GAME" },
                    { MessageBoxButtons.Cancel, "CANCEL" }
                },
                OnMainResult, this);
        }

        private void OnMainResult(MessageBoxCallbackResult res)
        {
            if (res.DialogResult == MessageBoxResult.Yes)
                StartHostAndOpenLobby();
            else if (res.DialogResult == MessageBoxResult.No)
                ShowJoinDialog();
        }

        // CREATE (lobby-first): start hosting on DirectIP and open the lobby panel immediately.
        // No campaign is loaded here — the host picks a save only after everyone is ready (Play).
        private void StartHostAndOpenLobby()
        {
            NetworkEngine.Create();
            NetworkEngine.Instance.Initialize(TransportType.DirectIP);
            NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
            NetworkEngine.Instance.StartHost(DefaultDirectPort);
            ShowInGameBar();
            _lobby?.Show();
        }

        private void ShowJoinDialog()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            mb.ShowSimplePrompt("", MessageBoxIcon.None,
                MessageBoxButtons.Yes | MessageBoxButtons.No | MessageBoxButtons.Abort | MessageBoxButtons.Cancel,
                new Dictionary<MessageBoxButtons, string>
                {
                    { MessageBoxButtons.Yes, "DIRECT IP" },
                    { MessageBoxButtons.No, "STUN CODE" },
                    { MessageBoxButtons.Abort, "STEAM" },
                    { MessageBoxButtons.Cancel, "BACK" }
                },
                OnJoinResult, this);
        }

        private void OnJoinResult(MessageBoxCallbackResult res)
        {
            if (res.DialogResult == MessageBoxResult.Yes)
                ShowDirectConnect();
            else if (res.DialogResult == MessageBoxResult.No)
                ShowStunConnect();
            else if (res.DialogResult == MessageBoxResult.Abort)
                ShowSteamConnect();
            else if (res.DialogResult == MessageBoxResult.Cancel)
                ShowMainDialog();
        }

        private void ShowDirectConnect()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            mb.ShowInputPrompt("Enter IP:Port", "192.168.1.100:14242", null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    if (res.DialogResult != MessageBoxResult.OK) { ShowJoinDialog(); return; }
                    var input = res.InputTextResult ?? "";
                    var parts = input.Split(':');
                    var ip = parts[0].Trim();
                    var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 14242;

                    NetworkEngine.Create();
                    NetworkEngine.Instance.Initialize(TransportType.DirectIP);
                    NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                    NetworkEngine.Instance.JoinGame(ip, port);
                    ShowInGameBar();
                    _lobby?.Show();
                }, this);
        }

        private void ShowStunConnect()
        {
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            mb.ShowInputPrompt("Enter STUN connection code", "", null,
                MessageBoxIcon.Question, MessageBoxButtons.OKCancel,
                delegate (MessageBoxCallbackResult res)
                {
                    if (res.DialogResult != MessageBoxResult.OK) { ShowJoinDialog(); return; }
                    var code = res.InputTextResult?.Trim() ?? "";
                    if (string.IsNullOrEmpty(code)) { ShowStunConnect(); return; }

                    NetworkEngine.Create();
                    NetworkEngine.Instance.Initialize(TransportType.StunUDP);
                    NetworkEngine.Instance.OnConnectionFailed += OnConnectionFailed;
                    NetworkEngine.Instance.JoinGame(code, 0);
                    ShowInGameBar();
                    _lobby?.Show();
                }, this);
        }

        private void ShowSteamConnect()
        {
            var steamId = ResolveSteamId();
            var mb = GameUtl.GetMessageBox();
            if (mb == null) return;

            mb.ShowSimplePrompt(
                $"Your Steam ID: {steamId}\n\nChoose action:",
                MessageBoxIcon.Information,
                MessageBoxButtons.YesNoCancel,
                new Dictionary<MessageBoxButtons, string>
                {
                    { MessageBoxButtons.Yes, "HOST GAME" },
                    { MessageBoxButtons.No, "INVITE FRIEND" },
                    { MessageBoxButtons.Cancel, "BACK" }
                },
                delegate (MessageBoxCallbackResult res)
                {
                    if (res.DialogResult == MessageBoxResult.Yes)
                    {
                        NetworkEngine.Create();
                        NetworkEngine.Instance.Initialize(TransportType.SteamP2P);
                        NetworkEngine.Instance.StartHost(0);
                        ShowInGameBar();
                        _lobby?.Show();
                    }
                    else if (res.DialogResult == MessageBoxResult.No)
                    {
                        InvitePlayers();
                        ShowInGameBar();
                    }
                    else
                    {
                        ShowJoinDialog();
                    }
                }, this);
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

        // Host PLAY (gated all-ready by the lobby) → open the save-picker; the chosen save is then
        // serialized + transferred via the coordinator and the LOADED/BEGIN barrier runs.
        public void OnLobbyPlay()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost) return;

            _savePicker?.Show(chosen =>
            {
                NetworkEngine.Instance?.SaveTransfer?.HostStartSession(chosen);
            });
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

                _lobby?.Refresh();
            }
            else
            {
                if (_barStatusText != null)
                    _barStatusText.text = "Not connected";
            }
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

        private static ulong ResolveSteamId()
        {
            try
            {
                var asm = FindSteamworksAssembly();
                if (asm == null) return 0;
                var type = asm.GetType("Steamworks.SteamClient");
                if (type == null) return 0;
                var prop = type.GetProperty("SteamId",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop != null ? (ulong)prop.GetValue(null, null) : 0UL;
            }
            catch { return 0; }
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
                ShowMainDialog();
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
