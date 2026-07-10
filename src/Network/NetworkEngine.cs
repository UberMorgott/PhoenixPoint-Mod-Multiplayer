using System;
using System.Collections.Generic;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Multiplayer.Network.TimeSync;
using Multiplayer.Transport;
using UnityEngine;

namespace Multiplayer.Network
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

        // ─── Steam invite-lobby lifecycle hooks ────────────────────────────
        // Wired by MultiplayerUI at startup to SteamInvite.LeaveHostLobby / SetLobbyJoinable; null in
        // unit tests. Delegate fields (not direct calls) so THIS class never references Steamworks
        // types — Shutdown/TearDown must stay JIT-safe without the Facepunch assembly (test runners,
        // non-Steam installs). Invoking HERE makes EVERY teardown path (leave button, smart-join
        // handoff, return-to-menu TearDown patch) drop the published lobby + rich presence — Steam
        // does NOT auto-clear rich presence while the game keeps running.
        public static Action SteamLobbyCleanup;
        public static Action<bool> SteamLobbySetJoinable;

        private static void RunSteamLobbyCleanup()
        {
            try { SteamLobbyCleanup?.Invoke(); } catch { /* never let Steam cleanup break a teardown */ }
        }

        // Parity auto-apply restore hook — same delegate-field pattern (and reason) as SteamLobbyCleanup:
        // ParityConfigSync references Assembly-CSharp types (ModEntry/ModConfigField), so a direct call
        // here would break Shutdown/TearDown JIT in game-free test runners. Wired by MultiplayerMain at
        // mod enable; null in unit tests.
        public static Action ParityConfigRestore;

        private static void RunParityConfigRestore()
        {
            try { ParityConfigRestore?.Invoke(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity config restore failed: " + e.Message); }
        }

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
            Debug.Log($"[Multiplayer] transport initialized: {Transport?.TransportType}");
            // Fresh session: a genuine connect failure from here on must surface to the user.
            _intentionalDisconnect = false;
        }

        /// <summary>
        /// Host-side overload: bind the engine to a pre-constructed transport (e.g. a
        /// <see cref="Multiplayer.Transport.CompositeTransport"/> that listens on Direct +
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
            Debug.Log($"[Multiplayer] transport initialized: {Transport?.TransportType}");
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
            // Drop any published Steam invite lobby + rich presence (no-op on a client / off Steam).
            RunSteamLobbyCleanup();
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
            // Drop the client-synced research rate: a fast client→client reconnection must not apply the
            // PREVIOUS session's rate in the window between join and the new session's first ch2 seed.
            ClientResearchRate.Reset();
            // Parity auto-apply: put the client's ORIGINAL mod settings back (no-op when none applied).
            RunParityConfigRestore();
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
            // Drop any published Steam invite lobby + rich presence (no-op on a client / off Steam).
            RunSteamLobbyCleanup();

            // Tear down transport + all per-session objects (idempotent: each null-guards).
            Transport?.Shutdown();
            Transport = null;
            Session = null;
            SaveTransfer?.Detach();  // unsubscribe from OnClientDisconnected before dropping the coordinator (re-host leak).
            SaveTransfer = null;   // re-created fresh in Initialize() → resets _begun / SessionStarted
            TimeSync = null;
            Sync = null;
            // Mirrors Shutdown(): no cross-session research-rate leak on the full-teardown path either.
            ClientResearchRate.Reset();
            // Parity auto-apply: put the client's ORIGINAL mod settings back (no-op when none applied).
            RunParityConfigRestore();

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

            // DIAGNOSTIC (additive, no behaviour change): the user EXPLICITLY chose to HOST. If the bind
            // failed on EVERY transport the aggregate state reads Failed (e.g. a 2nd PhoenixPoint instance
            // on this machine already holds port 14242 — TCP for DirectIP, UDP for STUN — so both report
            // AddressAlreadyInUse). The per-transport Host() swallows that into a queryable Failed state
            // rather than throwing, so without this shout the dead host looks alive: the user keeps watching
            // a non-hosting instance (no reward credit) and it can read like a silent client demotion
            // ("Connection accepted by host"). Do NOT change the fallback/port/transport selection — just
            // make the all-transports bind failure LOUD and unmissable in the Player.log.
            if (Transport.State == ConnectionState.Failed)
            {
                Debug.LogError(
                    $"[Multiplayer] HOST BIND FAILED on ALL transports (port {port}). This instance is NOT " +
                    "hosting — it did NOT silently become a client. Most likely another PhoenixPoint instance " +
                    "on this machine already holds the port. Close the other instance (or free the port), then " +
                    "host again. Do NOT keep playing here expecting host/reward authority.");
            }

            Session.InitializeAsHost();
            OnHostStarted?.Invoke();
        }

        public void JoinGame(string address, int port)
        {
            if (!IsActive || Transport == null) return;

            IsHost = false;
            // Inc5 part 2 — returning-peer rejoin, CLIENT leg: when this engine PERSISTED across a
            // connection drop (in-place reconnect — Initialize() early-returns on an active engine, so
            // Session/SaveTransfer/Sync were NOT recreated), every in-flight sync-state holder still
            // references the dead session's geoscape. Sweep it through the ONE aggregated rca-3
            // reload-boundary reset (audited list in the SyncEngine ctor; every entry idempotent — a
            // fresh first-join engine's empty state makes this a no-op) so the rejoin rides the
            // on-demand join path with clean local state. Deliberately NOT a second reset aggregation;
            // version/nonce continuity holds by the same rca-3 contract (host counters kept increasing).
            Sync?.ResetForReloadBoundary();
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

        /// <summary>
        /// FIX-4 / never-silent: surface a host ConnectionRejected reason to the joining client through
        /// the SAME channel as a connect failure (the existing OnConnectionFailed dialog + lobby
        /// teardown / return-to-menu). No-op during an intentional local teardown so a leave mid-handshake
        /// does not pop a spurious box.
        /// </summary>
        public void ReportConnectionRejected(string reason)
        {
            if (!_intentionalDisconnect)
                OnConnectionFailed?.Invoke(reason);
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
                OnConnectionFailed?.Invoke(BuildTransportFailureReason());
            }
        }

        // Never-silent diagnostics: name the failing transport (the "stage") + the precise reason the
        // transport already computed (stashed in its LocalEndpoint on failure — e.g. DirectIP's
        // SocketErrorCode or STUN's "no HOLE_PUNCH_ACK…") + an actionable next step, instead of the old
        // opaque "Transport connection failed". The client's Transport is always the single concrete
        // transport it joined with, so TransportType is accurate.
        private string BuildTransportFailureReason()
        {
            var t = Transport;
            if (t == null) return "Transport connection failed";

            var detail = t.LocalEndpoint;
            var stage = string.IsNullOrEmpty(detail) ? t.TransportType.ToString() : detail;

            string hint;
            switch (t.TransportType)
            {
                case TransportType.StunUDP:
                    hint = "The invite code uses NAT hole-punching, which many home routers / CGNAT block. "
                         + "Use Direct IP instead: the HOST forwards TCP 14242 to their PC, then you join with "
                         + "their public IP typed as \"<host-public-ip>:14242\".";
                    break;
                case TransportType.DirectIP:
                    hint = "Check the address and that the HOST forwarded TCP 14242 to their PC "
                         + "(you, the client, need no port-forwarding). ConnectionRefused = wrong port / host "
                         + "not hosting; TimedOut = firewall or port not forwarded.";
                    break;
                case TransportType.SteamP2P:
                    hint = "The Steam P2P link to the host could not be established. Make sure both players "
                         + "are Steam friends and the host is still in the lobby, then accept the invite "
                         + "again — or use Direct IP (host forwards TCP 14242).";
                    break;
                default:
                    hint = null;
                    break;
            }
            return hint == null ? stage : stage + "\n\n" + hint;
        }

        private void OnPeerConnected(ulong peerId, string endpoint)
        {
            if (IsHost)
            {
                Session.AddClient(peerId, endpoint);
                OnClientConnected?.Invoke(peerId);
                // Seed the late joiner with the current authoritative time anchor (reliable, targeted).
                TimeSync?.OnPeerConnectedHost(peerId);
                // Canonical Steam-lobby capacity gate: 2-player co-op (host + 1 client) → the invite
                // lobby stops being joinable the moment the slot fills. No-op when no lobby exists.
                try { SteamLobbySetJoinable?.Invoke(Session.ClientCount == 0); } catch { }
            }
            else
            {
                // Client connected to host: record the host peer and send JOIN carrying our
                // persistent identity (reshaped ConnectionRequest payload, §2 JOIN).
                Session.SetHostPeer(peerId);
                var join = new JoinMessage
                {
                    PlayerGuid = ClientIdentity.PlayerGuid,
                    Nickname = SystemInfo.deviceName,
                    // FIX-4: carry this client's parity manifest (DLC + mods + settings) in the JOIN so
                    // the host can gate the join BEFORE any save transfer.
                    Manifest = ParityManifestCollector.Collect()
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

            // Host still hosting and the slot freed → the Steam invite lobby is joinable again.
            if (IsHost)
                try { SteamLobbySetJoinable?.Invoke(Session.ClientCount == 0); } catch { }

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
                // FIX-1: ANY inbound packet proves the sender is alive. Refresh liveness here at the
                // single receive chokepoint (BEFORE routing) so high-rate non-heartbeat traffic —
                // RosterProgress (routed straight to SaveTransfer, bypassing the Heartbeat handler),
                // SaveChunks, chat, etc. — all count toward the peer's liveness, not just Heartbeat.
                Session?.RefreshLiveness(senderSteamId);
                RouteMessage(msg);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Multiplayer] Failed to deserialize packet: {ex.Message}");
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

                case PacketType.PlayerListUpdate:
                    Session.HandlePeerList(msg);
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

                // ─── Save transfer + barrier (Phase B — SaveTransferCoordinator). ─────
                case PacketType.SaveChunk:
                    SaveTransfer?.OnSaveChunk(msg);
                    break;

                case PacketType.SaveDone:
                    Debug.Log("[Multiplayer] route: SaveDone");
                    SaveTransfer?.OnSaveDone(msg);
                    break;

                case PacketType.LoadProgress:
                    SaveTransfer?.OnLoadProgress(msg);
                    break;

                case PacketType.ClientLoaded:
                    Debug.Log("[Multiplayer] route: ClientLoaded");
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

                case PacketType.ParityUpdate:
                    // Client->host: refreshed parity manifest after the client auto-applied host settings.
                    Session.HandleParityUpdate(msg);
                    break;

                case PacketType.JoinReady:
                    // Client->host: a mid-session on-demand joiner reached the live geoscape; host re-seeds it.
                    SaveTransfer?.OnJoinReady(msg);
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
                // Envelope cutover: the legacy raw 0x60 ActionRequest / 0x61 ActionApply / 0x62 ActionReject inbound
                // routes are RETIRED — the geoscape action relay now rides the unified 0x67 SyncEnvelope rail
                // (GeoIntent 0xA2 / GeoOutcome 0xA3 / GeoReject 0xA4), dispatched by Sync.OnSyncEnvelope ->
                // SurfaceRouter -> HandleGeoscapeEnvelope, which reaches the SAME OnActionRequest / OnActionApply /
                // OnActionReject appliers. Zero senders remain for 0x60/0x61/0x62.

                // Rail-unify phase 1: the legacy raw 0x63 WalletSync / 0x64 StateSync inbound routes are RETIRED —
                // wallet + state now ride the unified 0x67 SyncEnvelope rail (GeoWallet 0xA0 / GeoState 0xA1),
                // dispatched by Sync.OnSyncEnvelope -> SurfaceRouter -> HandleGeoscapeEnvelope, which still reaches
                // the SAME OnWalletSync / OnStateSync appliers. Zero senders remain for 0x63/0x64.

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

                case PacketType.EventAdvanceRequest:
                    // Client->host: a client OK'd its single-choice prompt mirror — drive the host's own open
                    // prompt to its result page as if the host clicked (first wins; idempotent no-op otherwise).
                    Sync?.OnEventAdvanceRequest(msg.Payload);
                    break;

                case PacketType.ReportModalShow:
                    // Host->all report window opened. Clients reconstruct + show the same modal (Phase-A mirror).
                    Sync?.OnReportModalShow(msg.Payload);
                    break;

                case PacketType.ReportModalHide:
                    // Host->all: the blocking report modal (ambush brief) resolved on the host. Clients close
                    // their mirrored view-locked copy (type-matched; idempotent no-op when nothing is open).
                    Sync?.OnReportModalHide(msg.Payload);
                    break;

                case PacketType.GeoLogNotice:
                    // Host->all: a small geoscape LOG toast the frozen client sim never raises locally. Clients
                    // replay it into their own GeoscapeLog (native toast + log panel entry).
                    Sync?.OnGeoLogNotice(msg.Payload);
                    break;

                case PacketType.SyncEnvelope:
                    // Unified surface envelope (actions in Phase 1). One chokepoint routes by surface+kind.
                    // Additive: lives alongside the legacy ActionRequest/ActionApply cases above (Task 6).
                    Sync?.OnSyncEnvelope(msg.SenderSteamId, msg.Payload);
                    break;

                default:
                    Debug.LogWarning($"[Multiplayer] Unrouted packet type: {msg.Type}");
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
