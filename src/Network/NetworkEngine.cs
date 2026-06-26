using System;
using System.Collections.Generic;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.Sync;
using Multipleer.Network.TimeSync;
using Multipleer.Transport;
using UnityEngine;

namespace Multipleer.Network
{
    public class NetworkEngine
    {
        public static NetworkEngine Instance { get; private set; }

        public ITransport Transport { get; private set; }
        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalSteamId { get; private set; }
        public SessionManager Session { get; private set; }
        public SaveTransferCoordinator SaveTransfer { get; private set; }
        public TimeSyncManager TimeSync { get; private set; }
        public SyncEngine Sync { get; private set; }

        /// <summary>
        /// True while a networked co-op session is established (transport up + we are host or have a
        /// host peer). Sync interceptors MUST early-return when this is false so single-player play is
        /// never intercepted. Read-only, derived — no extra state to maintain.
        /// </summary>
        public bool IsActiveSession => IsActive && (IsHost || (Session != null && Session.HostPeerId.HasValue));

        // Set true at the START of every intentional teardown (Disconnect/Shutdown). Tearing a
        // CompositeTransport down disconnects its children one-by-one; once the hosting child goes
        // Disconnected the aggregate State can read Failed (e.g. a Steam child that never came up),
        // which would otherwise surface as a bogus "Transport connection failed" MessageBox on a
        // user-initiated leave. Checked in OnTransportStateChanged to suppress that box. Reset in
        // Initialize() so the next genuine connect attempt still reports real failures.
        private bool _intentionalDisconnect;

        /// <summary>
        /// True once an intentional local teardown (Disconnect/Shutdown/TearDown) has begun — i.e. THIS
        /// peer initiated the leave. Consulted by <see cref="HostLeaveHandler"/> via
        /// <see cref="SessionLifecycle.ShouldNotifyHostLeft"/> so a client's VOLUNTARY lobby LEAVE
        /// (which closes its only peer, the host) is not mistaken for a genuine host drop and does not
        /// raise the false "Host ended the session" toast + forced reload. Reset in Initialize().
        /// </summary>
        public bool IsIntentionalDisconnect => _intentionalDisconnect;

        // ─── Events ───────────────────────────────────────────────────────

        public event Action OnHostStarted;
        public event Action<ulong> OnClientConnected;
        public event Action<ulong> OnClientDisconnected;
        // F1: same drop event, but carrying the resolved player NAME captured BEFORE RemoveClient
        // purges the roster, plus whether the peer was still known (false for a transport drop that
        // arrives after a graceful leave already removed it → subscriber suppresses a duplicate
        // notice). Additive alongside OnClientDisconnected so existing id-only subscribers are intact.
        public event Action<ulong, string, bool> OnClientDisconnectedNamed;
        public event Action<string> OnConnectionFailed;

        // ─── Initialization ───────────────────────────────────────────────

        public static void Create()
        {
            if (Instance == null)
                Instance = new NetworkEngine();
        }

        public void Initialize(TransportType transportType)
        {
            if (IsActive) return;

            Transport = CreateTransport(transportType);
            Session = new SessionManager(this);
            SaveTransfer = new SaveTransferCoordinator(this);
            TimeSync = new TimeSyncManager(this);
            Sync = new SyncEngine(this);
            LocalSteamId = ResolveLocalSteamId();

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnPeerConnected += OnPeerConnected;
            Transport.OnPeerDisconnected += OnPeerDisconnected;
            Transport.OnStateChanged += OnTransportStateChanged;

            Transport.Initialize();
            IsActive = true;
            // F1: wire the disconnect/connect notifier once per (re)init. Re-attach is safe — it
            // detaches any prior engine first, so handlers never stack across host/join/leave cycles.
            SessionNotifier.AttachTo(this);
            // F3: wire the host-leave handler (client drops to menu on host quit/crash); re-arms its latch.
            HostLeaveHandler.AttachTo(this);
            Debug.Log($"[Multipleer] transport initialized: {Transport?.TransportType}");
            // Fresh session: a genuine connect failure from here on must surface to the user.
            _intentionalDisconnect = false;
        }

        /// <summary>
        /// Host-side overload: bind the engine to a pre-constructed transport (e.g. a
        /// <see cref="Multipleer.Transport.CompositeTransport"/> that listens on Direct +
        /// STUN + Steam at once). Mirrors <see cref="Initialize(TransportType)"/> exactly,
        /// only the transport source differs — clients keep using Initialize(TransportType).
        /// </summary>
        public void Initialize(ITransport transport)
        {
            if (IsActive) return;
            if (transport == null) throw new ArgumentNullException(nameof(transport));

            Transport = transport;
            Session = new SessionManager(this);
            SaveTransfer = new SaveTransferCoordinator(this);
            TimeSync = new TimeSyncManager(this);
            Sync = new SyncEngine(this);
            LocalSteamId = ResolveLocalSteamId();

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnPeerConnected += OnPeerConnected;
            Transport.OnPeerDisconnected += OnPeerDisconnected;
            Transport.OnStateChanged += OnTransportStateChanged;

            Transport.Initialize();
            IsActive = true;
            // F1: wire the disconnect/connect notifier once per (re)init (host composite-transport path).
            SessionNotifier.AttachTo(this);
            // F3: wire the host-leave handler (inert on the host; arms for the client crash/quit path).
            HostLeaveHandler.AttachTo(this);
            Debug.Log($"[Multipleer] transport initialized: {Transport?.TransportType}");
            // Fresh session: a genuine connect failure from here on must surface to the user.
            _intentionalDisconnect = false;
        }

        public void Shutdown()
        {
            if (!IsActive) return;

            // Suppress the transport-failed MessageBox: this teardown is user-initiated.
            _intentionalDisconnect = true;
            // F1/F3: drop the lifecycle subscriptions before the engine objects go away.
            SessionNotifier.Detach();
            HostLeaveHandler.Detach();
            Transport?.Shutdown();
            Transport = null;
            Session = null;
            SaveTransfer?.Detach();  // unsubscribe from OnClientDisconnected before dropping the coordinator (re-host leak).
            SaveTransfer = null;
            TimeSync = null;
            // Drop the host wallet-event subscription before the engine goes away (session end).
            WalletWatcher.Detach();
            // Drop all state-channel change-event subscriptions on the same path.
            Sync?.DetachAllChannels();
            Sync = null;
            IsActive = false;
            IsHost = false;

            // Singleton persists across host/join/leave cycles (Instance is never nulled),
            // so clear UI-facing subscriptions here to prevent handler stacking on reconnect.
            OnConnectionFailed = null;
        }

        /// <summary>
        /// Idempotent FULL teardown of the networked session. Unlike <see cref="Shutdown"/> this also
        /// NULLS the <see cref="Instance"/> singleton so the next <see cref="Create"/> yields a fresh
        /// engine (the old engine "was never nulled", which left a stale lobby reopenable forever after
        /// a return-to-menu — Bug A). Latched onto the native return-to-menu chokepoint
        /// (PhoenixGame.FinishLevelAndGoToLobby) by FinishLevelAndGoToLobbyTearDownPatch, so EVERY
        /// path back to the main menu (pause-exit, mission end, game-over, error-yank) funnels here.
        /// Safe to call when already idle and safe to call twice.
        /// </summary>
        public void TearDown()
        {
            // Suppress the transport-failed MessageBox: this teardown is intentional, not a real error.
            _intentionalDisconnect = true;

            // Drop subscriptions BEFORE the transport goes away (no-op / idempotent on a client or when
            // already detached). Mirrors Disconnect()/Shutdown().
            WalletWatcher.Detach();
            Sync?.DetachAllChannels();
            SessionNotifier.Detach();  // F1: drop connect/disconnect notifier subscription on full teardown.
            HostLeaveHandler.Detach(); // F3: drop host-leave subscriptions on full teardown.

            // Tear down transport + all per-session objects (idempotent: each null-guards).
            Transport?.Shutdown();
            Transport = null;
            Session = null;
            SaveTransfer?.Detach();  // unsubscribe from OnClientDisconnected before dropping the coordinator (re-host leak).
            SaveTransfer = null;   // re-created fresh in Initialize() → resets _begun / SessionStarted
            TimeSync = null;
            Sync = null;

            IsActive = false;
            IsHost = false;

            // Clear UI-facing subscriptions so handlers never stack across host/join/leave cycles.
            OnConnectionFailed = null;

            // The whole point of TearDown (vs Shutdown): drop the singleton so a return-to-menu does
            // not leave a stale, half-dead engine that ShowNetworkMenu would re-show a dead lobby from.
            if (Instance == this)
                Instance = null;
        }

        // ─── Transport Selection ──────────────────────────────────────────

        private static ITransport CreateTransport(TransportType type)
        {
            switch (type)
            {
                case TransportType.SteamP2P: return new SteamTransport();
                case TransportType.DirectIP: return new DirectTransport();
                case TransportType.StunUDP: return new StunTransport();
                default: throw new ArgumentException($"Unknown transport: {type}");
            }
        }

        // ─── Host / Join ──────────────────────────────────────────────────

        public void StartHost(int port = 0)
        {
            if (!IsActive || Transport == null) return;

            IsHost = true;
            Transport.Host(port);
            Session.InitializeAsHost();
            OnHostStarted?.Invoke();
        }

        public void JoinGame(string address, int port)
        {
            if (!IsActive || Transport == null) return;

            IsHost = false;
            // Defense-in-depth: the transport connect is non-blocking and catches its own socket
            // errors, but ANY exception escaping here (e.g. a malformed address before the socket
            // layer) must surface as a clean connection failure, never an unhandled crash.
            try
            {
                Transport.Connect(address, port);
            }
            catch (Exception ex)
            {
                if (!_intentionalDisconnect)
                    OnConnectionFailed?.Invoke(ex.Message);
            }
        }

        public void Disconnect()
        {
            // Suppress the transport-failed MessageBox: this teardown is user-initiated. Disconnecting
            // a CompositeTransport child-by-child can flip the aggregate State to Failed mid-teardown.
            _intentionalDisconnect = true;
            // Session ending → drop the host wallet-event subscription (idempotent / no-op on client).
            WalletWatcher.Detach();
            // Same for state-channel subscriptions (idempotent / no-op on client).
            Sync?.DetachAllChannels();
            Transport?.Disconnect();
            IsHost = false;
        }

        // ─── Send / Broadcast ────────────────────────────────────────────

        public void SendToClient(ulong clientId, NetworkMessage msg)
        {
            var data = msg.Serialize();
            Transport?.Send(clientId, data);
        }

        public void SendToHost(NetworkMessage msg)
        {
            if (Transport != null && Session.HostPeerId.HasValue)
            {
                var data = msg.Serialize();
                Transport.Send(Session.HostPeerId.Value, data);
            }
        }

        public void BroadcastToAll(NetworkMessage msg)
        {
            var data = msg.Serialize();
            Transport?.Broadcast(data);
        }

        // UNRELIABLE fan-out for high-frequency, loss-tolerant state (co-op load RosterProgress
        // snapshots). Mirrors BroadcastToAll but passes reliable: false so the transport may drop /
        // reorder these — the receiver-side RosterProgressTracker merges monotonic-max, so a lost or
        // late snapshot is harmless. LoadComplete / PEER_LIST stay on the reliable BroadcastToAll path.
        public void BroadcastUnreliable(NetworkMessage msg)
        {
            var data = msg.Serialize();
            Transport?.Broadcast(data, reliable: false);
        }

        public void BroadcastExcept(ulong excludeSteamId, NetworkMessage msg)
        {
            var data = msg.Serialize();
            // Transport layer doesn't support exclude — send individually
            foreach (var client in Session.GetConnectedClients())
            {
                if (client != excludeSteamId)
                    Transport?.Send(client, data);
            }
        }

        // ─── Update Loop (call every frame) ──────────────────────────────

        public void Update()
        {
            Transport?.Update();
            Session?.Update();
            SaveTransfer?.Update();
            TimeSync?.Tick();
            Sync?.Tick();
        }

        // ─── Internal Handlers ────────────────────────────────────────────

        private void OnTransportStateChanged(ConnectionState state)
        {
            if (state == ConnectionState.Failed)
            {
                // A Failed state raised while we are intentionally tearing the session down (leave /
                // host-stop / smart-join handoff) is not a real connection error — swallow it so no
                // bogus error box pops on leave. Genuine join/connect failures arrive with the flag
                // cleared (reset in Initialize) and still surface.
                if (_intentionalDisconnect) return;
                OnConnectionFailed?.Invoke("Transport connection failed");
            }
        }

        private void OnPeerConnected(ulong peerId, string endpoint)
        {
            if (IsHost)
            {
                Session.AddClient(peerId, endpoint);
                OnClientConnected?.Invoke(peerId);
                // Seed the late joiner with the current authoritative time anchor (reliable, targeted).
                TimeSync?.OnPeerConnectedHost(peerId);
            }
            else
            {
                // Client connected to host: record the host peer and send JOIN carrying our
                // persistent identity (reshaped ConnectionRequest payload, §2 JOIN).
                Session.SetHostPeer(peerId);
                var join = new JoinMessage
                {
                    PlayerGuid = ClientIdentity.PlayerGuid,
                    Nickname = SystemInfo.deviceName
                };
                var payload = MessageSerializer.SerializeJoin(join);
                SendToHost(new NetworkMessage(PacketType.ConnectionRequest, payload));
            }
        }

        private void OnPeerDisconnected(ulong peerId, string endpoint)
        {
            // Capture the player NAME (+ whether the peer is still known) BEFORE RemoveClient purges
            // the roster, so F1 can show the real name on a crash/timeout drop. A transport drop that
            // arrives AFTER a graceful leave already removed the peer reports wasKnown=false, letting
            // SessionNotifier suppress a duplicate "-- X left --" (HandleLeave posted that one).
            string droppedName = null;
            var wasKnown = Session != null && Session.TryGetClientName(peerId, out droppedName);
            if (!wasKnown) droppedName = null;

            Session.RemoveClient(peerId);
            OnClientDisconnected?.Invoke(peerId);
            OnClientDisconnectedNamed?.Invoke(peerId, droppedName, wasKnown);

            // Client lost its host peer on an IN-PLACE transport drop (no Shutdown→Initialize, the manager
            // persists). Clear stale time-sync derive state so the next OnPeerConnected re-seeds the offset
            // (ping burst) and the next anchor hard-sets the clock (spec §8 reconnect). RISK-1/RISK-2.
            // No-op on the host (ResetClientState self-guards) — host keeps its monotonic anchor version.
            if (!IsHost)
                TimeSync?.ResetClientState();
        }

        private void OnPacketReceived(ulong senderSteamId, byte[] data)
        {
            try
            {
                var msg = NetworkMessage.Deserialize(data);
                msg.SenderSteamId = senderSteamId;
                RouteMessage(msg);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multipleer] Failed to deserialize packet: {ex.Message}");
            }
        }

        private void RouteMessage(NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case PacketType.ConnectionRequest:
                    Session.HandleConnectionRequest(msg);
                    break;

                case PacketType.ConnectionAccepted:
                    Session.HandleConnectionAccepted(msg);
                    break;

                case PacketType.ConnectionRejected:
                    Session.HandleConnectionRejected(msg);
                    break;

                case PacketType.ClientDisconnected:
                    Session.HandleClientDisconnected(msg);
                    break;

                case PacketType.HostDisconnected:
                    Session.HandleHostDisconnected(msg);
                    break;

                case PacketType.Heartbeat:
                case PacketType.HeartbeatAck:
                    Session.HandleHeartbeat(msg);
                    break;

                case PacketType.ClientLeave:
                    Session.HandleLeave(msg);
                    break;

                case PacketType.PlayerRename:
                    Session.HandleRename(msg);
                    break;

                case PacketType.PermissionUpdate:
                    Session.HandlePermissionUpdate(msg);
                    break;

                case PacketType.PlayerListUpdate:
                    Session.HandlePeerList(msg);
                    break;

                case PacketType.SoldierAssignment:
                    Session.HandleAssignOwner(msg);
                    break;

                case PacketType.ChatMessage:
                    Session.HandleChat(msg);
                    break;

                case PacketType.SetSave:
                    Session.HandleSetSave(msg);
                    break;

                case PacketType.ClientReady:
                case PacketType.ClientUnready:
                case PacketType.AllClientsReady:
                    Session.HandleReadyState(msg);
                    break;

                case PacketType.InitialGameState:
                    Session.HandleInitialGameState(msg);
                    break;

                case PacketType.StateSyncRequest:
                case PacketType.StateSyncResponse:
                    Session.HandleStateSync(msg);
                    break;

                // ─── Save transfer + barrier (Phase B — SaveTransferCoordinator). ─────
                case PacketType.SaveChunk:
                    SaveTransfer?.OnSaveChunk(msg);
                    break;

                case PacketType.SaveDone:
                    Debug.Log("[Multipleer] route: SaveDone");
                    SaveTransfer?.OnSaveDone(msg);
                    break;

                case PacketType.LoadProgress:
                    SaveTransfer?.OnLoadProgress(msg);
                    break;

                case PacketType.ClientLoaded:
                    Debug.Log("[Multipleer] route: ClientLoaded");
                    SaveTransfer?.OnClientLoaded(msg);
                    break;

                case PacketType.SessionBegin:
                    SaveTransfer?.OnBegin(msg);
                    break;

                case PacketType.RevealAll:
                    SaveTransfer?.OnRevealAll(msg);
                    break;

                case PacketType.RosterProgress:
                    SaveTransfer?.OnRosterProgress(msg);
                    break;

                case PacketType.LoadComplete:
                    SaveTransfer?.OnLoadComplete(msg);
                    break;

                // ─── Geoscape time sync (host-authoritative anchor clock + offset ping/pong). ─
                case PacketType.TimeAnchor:
                    // Host->all authoritative anchor. Clients derive (host ignores its own).
                    TimeSync?.OnAnchorReceived(msg.Payload);
                    break;

                case PacketType.TimeRequest:
                    // Client->host time-control request. Host permission-gates (ControlTime) the sender,
                    // then applies last-writer-wins + re-anchors.
                    TimeSync?.OnClientRequestReceived(msg.SenderSteamId, msg.Payload);
                    break;

                case PacketType.TimeClockPing:
                    // Client->host NTP ping. Host stamps its receive time and pongs back to the sender.
                    TimeSync?.OnClockPingReceived(msg.SenderSteamId, msg.Payload);
                    break;

                case PacketType.TimeClockPong:
                    // Host->client NTP pong. Client completes the 3-stamp and updates its clock offset.
                    TimeSync?.OnClockPongReceived(msg.Payload);
                    break;

                // ─── Action-sync engine (discrete-command relay + currency echo). ────
                case PacketType.ActionRequest:
                    // Client->host discrete action request. Host validates + sequences + broadcasts apply.
                    Sync?.OnActionRequest(msg.SenderSteamId, msg.Payload);
                    break;

                case PacketType.ActionApply:
                    // Host->all authoritative apply. Clients replay under the re-entrancy guard.
                    Sync?.OnActionApply(msg.Payload);
                    break;

                case PacketType.ActionReject:
                    // Host->originator rejection (permission / validation). v1: log + drop pending.
                    Sync?.OnActionReject(msg.Payload);
                    break;

                case PacketType.WalletSync:
                    // Host->all versioned full-wallet snapshot. Clients apply as signed diffs.
                    Sync?.OnWalletSync(msg.Payload);
                    break;

                case PacketType.StateSync:
                    // Host->all per-channel versioned state echo. Clients overwrite + refresh UI.
                    Sync?.OnStateSync(msg.Payload);
                    break;

                case PacketType.EventRaised:
                    // Host->all geoscape event raised. Clients reconstruct + show the dialog (no local pause).
                    Sync?.OnEventRaised(msg.Payload);
                    break;

                case PacketType.EventDismiss:
                    // Host->all answer applied. Clients close their open geoscape-event dialog.
                    Sync?.OnEventDismiss(msg.Payload);
                    break;

                case PacketType.EventAdvanceResult:
                    // Host->all single-choice PROMPT->RESULT advance. A client mirroring the host prompt jumps
                    // to its result page (no native CompleteEvent/EventDismiss fires on that host click).
                    Sync?.OnEventAdvanceResult(msg.Payload);
                    break;

                case PacketType.ReportModalShow:
                    // Host->all report window opened. Clients reconstruct + show the same modal (Phase-A mirror).
                    Sync?.OnReportModalShow(msg.Payload);
                    break;

                case PacketType.SyncEnvelope:
                    // Unified surface envelope (actions in Phase 1). One chokepoint routes by surface+kind.
                    // Additive: lives alongside the legacy ActionRequest/ActionApply cases above (Task 6).
                    Sync?.OnSyncEnvelope(msg.SenderSteamId, msg.Payload);
                    break;

                // ─── STUB + TODO: members no longer silently fall through. ───────────
                case PacketType.GameStateDelta:
                    // TODO(delta-sync): geoscape/tactical delta application.
                    break;

                case PacketType.PauseRequest:
                case PacketType.PauseAccepted:
                    // TODO(pause): cooperative pause feature.
                    break;

                default:
                    Debug.LogWarning($"[Multipleer] Unrouted packet type: {msg.Type}");
                    break;
            }
        }

        private static ulong ResolveLocalSteamId()
        {
            try
            {
                var type = typeof(UnityEngine.Application).Assembly
                    .GetType("Steamworks.SteamClient");
                if (type == null) return 0;
                var prop = type.GetProperty("SteamId",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop != null ? (ulong)prop.GetValue(null, null) : 0UL;
            }
            catch { return 0; }
        }
    }
}
