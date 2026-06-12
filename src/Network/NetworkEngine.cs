using System;
using System.Collections.Generic;
using Multipleer.Network.MessageLayer;
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

        // Set true at the START of every intentional teardown (Disconnect/Shutdown). Tearing a
        // CompositeTransport down disconnects its children one-by-one; once the hosting child goes
        // Disconnected the aggregate State can read Failed (e.g. a Steam child that never came up),
        // which would otherwise surface as a bogus "Transport connection failed" MessageBox on a
        // user-initiated leave. Checked in OnTransportStateChanged to suppress that box. Reset in
        // Initialize() so the next genuine connect attempt still reports real failures.
        private bool _intentionalDisconnect;

        // ─── Events ───────────────────────────────────────────────────────

        public event Action OnHostStarted;
        public event Action<ulong> OnClientConnected;
        public event Action<ulong> OnClientDisconnected;
        public event Action<string> OnConnectionFailed;
        public event Action<ulong, TacticalActionMessage> OnTacticalActionRequest;
        public event Action<ulong, CampaignActionMessage> OnCampaignActionRequest;
        public event Action<TacticalActionMessage> OnHostTacticalActionResult;
        public event Action<CampaignActionMessage> OnHostCampaignActionResult;
        public event Action<CampaignActionMessage> OnHostCampaignActionRejected;

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
            LocalSteamId = ResolveLocalSteamId();

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnPeerConnected += OnPeerConnected;
            Transport.OnPeerDisconnected += OnPeerDisconnected;
            Transport.OnStateChanged += OnTransportStateChanged;

            Transport.Initialize();
            IsActive = true;
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
            LocalSteamId = ResolveLocalSteamId();

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnPeerConnected += OnPeerConnected;
            Transport.OnPeerDisconnected += OnPeerDisconnected;
            Transport.OnStateChanged += OnTransportStateChanged;

            Transport.Initialize();
            IsActive = true;
            // Fresh session: a genuine connect failure from here on must surface to the user.
            _intentionalDisconnect = false;
        }

        public void Shutdown()
        {
            if (!IsActive) return;

            // Suppress the transport-failed MessageBox: this teardown is user-initiated.
            _intentionalDisconnect = true;
            Transport?.Shutdown();
            Transport = null;
            Session = null;
            SaveTransfer = null;
            IsActive = false;
            IsHost = false;

            // Singleton persists across host/join/leave cycles (Instance is never nulled),
            // so clear UI-facing subscriptions here to prevent handler stacking on reconnect.
            OnConnectionFailed = null;
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

        // ─── Tactical Action Flow ─────────────────────────────────────────

        public void SendTacticalAction(TacticalActionMessage action)
        {
            var payload = MessageSerializer.SerializeTacticalAction(action);
            var msg = new NetworkMessage(PacketType.TacticalActionRequest, payload);
            SendToHost(msg);
        }

        public void ApproveTacticalAction(ulong clientId, TacticalActionMessage action, byte[] resultData)
        {
            action.Timestamp = DateTime.UtcNow.Ticks;
            var payload = MessageSerializer.SerializeTacticalAction(action);

            // Send approval + result to requesting client
            var resultPayload = new byte[payload.Length + (resultData?.Length ?? 0) + 4];
            Array.Copy(payload, 0, resultPayload, 0, payload.Length);
            var resultLen = BitConverter.GetBytes(resultData?.Length ?? 0);
            Array.Copy(resultLen, 0, resultPayload, payload.Length, 4);
            if (resultData != null)
                Array.Copy(resultData, 0, resultPayload, payload.Length + 4, resultData.Length);

            var msg = new NetworkMessage(PacketType.TacticalActionApproved, resultPayload);
            SendToClient(clientId, msg);

            // Broadcast action to other clients (without result data, just for state tracking)
            var broadcastMsg = new NetworkMessage(PacketType.TacticalActionBroadcast, payload);
            BroadcastExcept(clientId, broadcastMsg);
        }

        public void RejectTacticalAction(ulong clientId, TacticalActionMessage action, string reason)
        {
            var payload = MessageSerializer.SerializeTacticalAction(action);
            var reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason ?? "denied");
            var rejectPayload = new byte[payload.Length + reasonBytes.Length + 4];
            Array.Copy(payload, 0, rejectPayload, 0, payload.Length);
            var reasonLen = BitConverter.GetBytes(reasonBytes.Length);
            Array.Copy(reasonLen, 0, rejectPayload, payload.Length, 4);
            Array.Copy(reasonBytes, 0, rejectPayload, payload.Length + 4, reasonBytes.Length);

            var msg = new NetworkMessage(PacketType.TacticalActionRejected, rejectPayload);
            SendToClient(clientId, msg);
        }

        // ─── Campaign Action Flow ─────────────────────────────────────────

        public void SendCampaignAction(CampaignActionMessage action)
        {
            var payload = MessageSerializer.SerializeCampaignAction(action);
            var msg = new NetworkMessage(PacketType.CampaignActionRequest, payload);
            SendToHost(msg);
        }

        public void ApproveCampaignAction(ulong clientId, CampaignActionMessage action)
        {
            var payload = MessageSerializer.SerializeCampaignAction(action);
            var msg = new NetworkMessage(PacketType.CampaignActionApproved, payload);
            SendToClient(clientId, msg);
        }

        public void RejectCampaignAction(ulong clientId, CampaignActionMessage action, string reason)
        {
            var payload = MessageSerializer.SerializeCampaignAction(action);
            var reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason ?? "denied");
            var rejectPayload = new byte[payload.Length + reasonBytes.Length + 4];
            Array.Copy(payload, 0, rejectPayload, 0, payload.Length);
            var reasonLen = BitConverter.GetBytes(reasonBytes.Length);
            Array.Copy(reasonLen, 0, rejectPayload, payload.Length, 4);
            Array.Copy(reasonBytes, 0, rejectPayload, payload.Length + 4, reasonBytes.Length);

            var msg = new NetworkMessage(PacketType.CampaignActionRejected, rejectPayload);
            SendToClient(clientId, msg);
        }

        public void BroadcastCampaignState(byte[] stateData)
        {
            var payload = MessageSerializer.SerializeGameState("campaign", stateData);
            var msg = new NetworkMessage(PacketType.CampaignStateUpdate, payload);
            BroadcastToAll(msg);
        }

        // ─── Update Loop (call every frame) ──────────────────────────────

        public void Update()
        {
            Transport?.Update();
            Session?.Update();
            SaveTransfer?.Update();
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
            Session.RemoveClient(peerId);
            OnClientDisconnected?.Invoke(peerId);
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

                case PacketType.TacticalActionRequest:
                    var tacAction = MessageSerializer.DeserializeTacticalAction(msg.Payload);
                    OnTacticalActionRequest?.Invoke(msg.SenderSteamId, tacAction);
                    break;

                case PacketType.TacticalActionApproved:
                    var approvedAction = MessageSerializer.DeserializeTacticalAction(
                        ExtractActionPayload(msg.Payload));
                    OnHostTacticalActionResult?.Invoke(approvedAction);
                    break;

                case PacketType.TacticalActionRejected:
                    var rejectedAction = MessageSerializer.DeserializeTacticalAction(
                        ExtractActionPayload(msg.Payload));
                    OnHostTacticalActionResult?.Invoke(rejectedAction);
                    break;

                case PacketType.CampaignActionRequest:
                    var campAction = MessageSerializer.DeserializeCampaignAction(msg.Payload);
                    OnCampaignActionRequest?.Invoke(msg.SenderSteamId, campAction);
                    break;

                case PacketType.CampaignActionApproved:
                    var campResult = MessageSerializer.DeserializeCampaignAction(msg.Payload);
                    OnHostCampaignActionResult?.Invoke(campResult);
                    break;

                case PacketType.CampaignActionRejected:
                    // Rejected actions go to a SEPARATE, non-applying channel. Firing the result/apply
                    // event here would make the originating client perform the exact action the host
                    // refused (its local exec was blocked by the Prefix). Feedback path only.
                    var campRejected = MessageSerializer.DeserializeCampaignAction(msg.Payload);
                    OnHostCampaignActionRejected?.Invoke(campRejected);
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
                case PacketType.AllClientsReady:
                    Session.HandleReadyState(msg);
                    break;

                case PacketType.EndTurnRequest:
                case PacketType.EndTurnAccepted:
                    // Handled by turn sync
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
                    SaveTransfer?.OnSaveDone(msg);
                    break;

                case PacketType.LoadProgress:
                    SaveTransfer?.OnLoadProgress(msg);
                    break;

                case PacketType.ClientLoaded:
                    SaveTransfer?.OnClientLoaded(msg);
                    break;

                case PacketType.SessionBegin:
                    SaveTransfer?.OnBegin(msg);
                    break;

                case PacketType.RosterProgress:
                    SaveTransfer?.OnRosterProgress(msg);
                    break;

                case PacketType.LoadComplete:
                    SaveTransfer?.OnLoadComplete(msg);
                    break;

                // ─── STUB + TODO: members no longer silently fall through. ───────────
                case PacketType.TacticalActionBroadcast:
                    // TODO(tactical-sync): apply broadcast on clients (latent: sent but never received-routed).
                    break;

                case PacketType.GameStateDelta:
                    // TODO(delta-sync): geoscape/tactical delta application.
                    break;

                case PacketType.TurnStateUpdate:
                    // TODO(tactical-turn): turn-state sync.
                    break;

                case PacketType.PauseRequest:
                case PacketType.PauseAccepted:
                    // TODO(pause): cooperative pause feature.
                    break;

                case PacketType.TacticalActionResult:
                    // TODO(tactical-sync): not currently emitted; reserved.
                    break;

                case PacketType.CampaignActionResult:
                    // TODO(campaign-sync): host emits via Approved/Rejected today; reserved.
                    break;

                case PacketType.CampaignStateUpdate:
                    // TODO(geoscape-sync): campaign state-sync application.
                    break;

                default:
                    Debug.LogWarning($"[Multipleer] Unrouted packet type: {msg.Type}");
                    break;
            }
        }

        private static byte[] ExtractActionPayload(byte[] fullPayload)
        {
            if (fullPayload == null || fullPayload.Length < 29) return null;

            // Parse TacticalActionMessage size: Guid(16) + byte(1) + int(4) + string prefix(4) + int(4) + long(8) = 37
            // We just need the action portion. Return full payload — the deserializer knows its format.
            return fullPayload;
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
