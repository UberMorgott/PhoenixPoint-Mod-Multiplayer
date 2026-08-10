using System;
using System.Collections.Generic;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using Multiplayer.Transport;
using UnityEngine;

namespace Multiplayer.Network
{
    public class NetworkEngine
    {
        public static NetworkEngine Instance { get; private set; }

        /// <summary>THE CO-OP SEAT COUNT, and the only place it is written down.
        ///
        /// Nothing in this repo actually capped a session at two: <c>SlotAllocator</c> has no ceiling at
        /// all, <c>SteamTransport</c> keys peers in a <c>HashSet</c>, and a join is a DIRECT P2P connect to
        /// the host's SteamID — the Steam lobby is discovery only. DirectIP has been running three for that
        /// reason. What capped it was two numbers written down twice: <c>CreateLobbyAsync(2)</c> in
        /// <c>SteamInvite</c>, and this class's own capacity gate below, which closed the lobby the moment
        /// the FIRST client connected.
        ///
        /// STATIC READONLY, NOT const, and deliberately: a <c>const</c> is inlined at every call site, so
        /// the two places that read it become two magic numbers again the instant the assembly is compiled
        /// — indistinguishable, to any check, from someone typing the digit. A field emits an
        /// <c>ldsfld</c>, which is a REFERENCE, and that is what RailCheck L91's single-source arm asserts.
        /// The runtime cost is one field load per lobby event.</summary>
        ///
        /// EIGHT IS POLICY, NOT STRUCTURE. Nothing in the stack is built around this number and nothing
        /// needs editing beside it — RailCheck L91's seat-count arms assert that mechanically: the three
        /// capacity sites READ this field, and the set of methods that read it is exactly those three, so
        /// there is no second place a bound could hide. Measured headroom, 2026-08-05: slots are a
        /// <c>byte</c> in <c>SlotAllocator</c> and on the wire (ceiling 255), peers are keyed by
        /// <c>ulong</c> SteamID in dictionaries and hash sets with no fixed arrays anywhere, the roster
        /// ships with an <c>Int32</c> count, the lobby roster UI is a <c>List</c> and the load overlay a
        /// <c>Dictionary&lt;byte, Row&gt;</c>, and the Steam invite lobby — which is DISCOVERY ONLY, since a
        /// join is a direct P2P connect — tops out at Steam's own documented 250 members. So the structure
        /// holds ~50 comfortably; the first real ceiling is 255 slots, and the practical one before that is
        /// bandwidth, because the host sends each delta once PER PEER.
        public static readonly int MaxPlayers = 50;

        /// <summary>Seats for CLIENTS — the host holds one. Derived, never typed twice.</summary>
        public static readonly int MaxClients = MaxPlayers - 1;

        public ITransport Transport { get; private set; }
        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalSteamId { get; private set; }
        public SessionManager Session { get; private set; }
        public SaveTransferCoordinator SaveTransfer { get; private set; }
        public SyncEngine Sync { get; private set; }

        /// <summary>
        /// True while a networked co-op session is established (transport up + we are host or have a
        /// host peer). Sync interceptors MUST early-return when this is false so single-player play is
        /// never intercepted. Read-only, derived — no extra state to maintain.
        /// </summary>
        public bool IsActiveSession => IsActive && (IsHost || (Session != null && Session.HostPeerId.HasValue));

        // Armed when THIS peer dials a host (JoinGame) and consumed by the client branch of
        // OnPeerConnected: only a dialed connect may (re)point the host link. Without it, any peer
        // that opens a transport link to a client (e.g. a Steam P2P session — every peer knows our
        // SteamId from PEER_LIST) hijacked the host link via the unconditional SetHostPeer + JOIN.
        // Stays armed until a peer is accepted, so Direct/STUN's async connect surfacing still lands.
        private bool _expectingHostLink;

        // FIX (StunTransport "reliable" = send twice): bounded receive-side dedup by MessageId
        // (a fresh Guid per NetworkMessage, serialized on the wire — a transport-level duplicate
        // datagram carries the SAME id). FIFO window, not per-peer: duplicates arrive back-to-back,
        // so 4096 recent ids is orders of magnitude more than needed. Harmless for TCP/Steam (no
        // duplication → never hits). Never reset: GUIDs do not repeat across sessions.
        private const int DedupWindowSize = 4096;
        private readonly HashSet<(ulong, Guid)> _seenMessages = new HashSet<(ulong, Guid)>();
        private readonly Queue<(ulong, Guid)> _seenMessageOrder = new Queue<(ulong, Guid)>();

        private bool IsDuplicateMessage(ulong senderId, Guid messageId)
        {
            var key = (senderId, messageId);
            if (!_seenMessages.Add(key)) return true;
            _seenMessageOrder.Enqueue(key);
            if (_seenMessageOrder.Count > DedupWindowSize)
                _seenMessages.Remove(_seenMessageOrder.Dequeue());
            return false;
        }

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
            // Same single teardown chokepoint tears down any UPnP port mapping the host opened (no-op on
            // a client / when UPnP was off). Fire-and-forget internally, so it never blocks the teardown.
            try { Multiplayer.Net.UpnpPortMapper.Unmap(); } catch { }
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

        /// <summary>Draws an arriving ping marker (<c>PingMarkers.Show</c>). Same delegate-field pattern and
        /// same reason as <see cref="ParityConfigRestore"/> — the drawing names GeoscapeView/TacticalView,
        /// which must not appear in this file's JIT closure. Null in unit tests: a ping is then routed and
        /// dropped, which is exactly what a presentation packet is allowed to do.</summary>
        public static Action<byte[]> PingMarkerShow;

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
            // Belt for the end-of-session phase (it self-clears on the next level; this only covers a
            // session ended and re-started without a level switch in between).
            SessionEnd.Reset();
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
            // Belt for the end-of-session phase (it self-clears on the next level; this only covers a
            // session ended and re-started without a level switch in between).
            SessionEnd.Reset();
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
            // Drop all state-channel change-event subscriptions on the same path.
            Sync?.DetachAllChannels();
            Sync = null;
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
            Sync = null;
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
            // We are DIALING: the next accepted peer is the host we chose (covers the in-place
            // rejoin, where a stale HostPeerId from the dead connection must not block re-pointing).
            _expectingHostLink = true;
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

        // Build + send the JOIN (ConnectionRequest) to the host, carrying our persistent identity and
        // parity manifest. Used both on first connect (OnPeerConnected) and by RepairHostLink to
        // re-drive the handshake after a half-open session reset.
        private void SendJoinToHost()
        {
            var join = new JoinMessage
            {
                PlayerGuid = ClientIdentity.PlayerGuid,
                Nickname = ClientIdentity.LocalNickname ?? SystemInfo.deviceName,
                // FIX-4: carry this client's parity manifest (DLC + mods + settings) in the JOIN so
                // the host can gate the join BEFORE any save transfer.
                Manifest = ParityManifestCollector.Collect()
            };
            var payload = MessageSerializer.SerializeJoin(join);
            SendToHost(new NetworkMessage(PacketType.ConnectionRequest, payload));
        }

        /// <summary>
        /// CLIENT half-open recovery (one-shot, driven by SessionManager's heartbeat-ack detector): the
        /// transport still reports Connected but our client→host packets stopped landing (host quit
        /// ACKing). Reset the wedged Steam P2P session to the host (a fresh NAT-punch / relay path the
        /// host re-accepts) then re-send the JOIN so the host re-binds us — its stale "unknown" roster
        /// row is re-bound by the same peer id (or times out). No teardown here; the detector gives the
        /// repaired link a fresh window and only tears down if it stays dead. Steam-specific reset via a
        /// type-check (composite is host-only; a client's transport is a bare concrete transport) — a
        /// non-Steam client transport skips the reset but still re-JOINs (harmless if the link is live).
        /// </summary>
        public void RepairHostLink()
        {
            if (IsHost || Transport == null || Session == null || !Session.HostPeerId.HasValue) return;
            (Transport as SteamTransport)?.ResetPeer(Session.HostPeerId.Value);
            SendJoinToHost();
        }

        public void Disconnect()
        {
            // Suppress the transport-failed MessageBox: this teardown is user-initiated. Disconnecting
            // a CompositeTransport child-by-child can flip the aggregate State to Failed mid-teardown.
            _intentionalDisconnect = true;
            // Session ending → drop the host wallet-event subscription (idempotent / no-op on client).
            // Same for state-channel subscriptions (idempotent / no-op on client).
            Sync?.DetachAllChannels();
            Transport?.Disconnect();
            IsHost = false;
        }

        // ─── Send / Broadcast ────────────────────────────────────────────

        /// <summary><paramref name="reliable"/>: false only for loss-tolerant traffic that must NOT be
        /// retransmitted — the heartbeat/RTT probe, where a retransmit would be measured as latency.</summary>
        public void SendToClient(ulong clientId, NetworkMessage msg, bool reliable = true)
        {
            if (IsHost && DurableEffectTransactionBarrier.TryDeferOutbound(() => SendToClient(clientId, msg, reliable))) return;
            var data = msg.Serialize();
            Transport?.Send(clientId, data, reliable);
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

        /// <summary><paramref name="reliable"/>: see <see cref="SendToClient"/>.</summary>
        public void SendToHost(NetworkMessage msg, bool reliable = true)
        {
            if (Transport != null && Session.HostPeerId.HasValue)
            {
                var data = msg.Serialize();
                Transport.Send(Session.HostPeerId.Value, data, reliable);
            }
        }

        public void BroadcastToAll(NetworkMessage msg)
        {
            if (DurableEffectTransactionBarrier.TryDeferOutbound(() => BroadcastToAll(msg))) return;
            var data = msg.Serialize();
            Transport?.Broadcast(data);
        }

        // UNRELIABLE fan-out for high-frequency, loss-tolerant state (co-op load RosterProgress
        // snapshots). Mirrors BroadcastToAll but passes reliable: false so the transport may drop /
        // reorder these — the receiver-side RosterProgressTracker merges monotonic-max, so a lost or
        // late snapshot is harmless. LoadComplete / PEER_LIST stay on the reliable BroadcastToAll path.
        public void BroadcastUnreliable(NetworkMessage msg)
        {
            if (DurableEffectTransactionBarrier.TryDeferOutbound(() => BroadcastUnreliable(msg))) return;
            var data = msg.Serialize();
            Transport?.Broadcast(data, reliable: false);
        }

        public void BroadcastExcept(ulong excludeSteamId, NetworkMessage msg)
        {
            if (DurableEffectTransactionBarrier.TryDeferOutbound(() => BroadcastExcept(excludeSteamId, msg))) return;
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
                         + "not hosting; TimedOut = firewall or port not forwarded. If the host cannot forward "
                         + "the port at all (carrier-grade NAT), no direct link is possible — use a LAN/VPN "
                         + "tunnel (e.g. Radmin, ZeroTier) or the Steam version.";
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
                // Steam-lobby VISIBILITY gate, and only that: it stops the invite lobby advertising once
                // every declared seat is filled. NOT a join refusal — HandleConnectionRequest has no
                // capacity branch at all, so nobody is ever told "server full" (N=50 mandate). Reads the
                // ONE declared number (law L91's reader set). This line WAS the 2-player cap —
                // `ClientCount == 0` closed the lobby on the FIRST connect, so nobody could be third.
                try { SteamLobbySetJoinable?.Invoke(Session.ClientCount < MaxClients); } catch { }
            }
            else
            {
                // Client connected to host: record the host peer and send JOIN carrying our
                // persistent identity (reshaped ConnectionRequest payload, §2 JOIN).
                // Host-link hijack guard: only a connect we DIALED (JoinGame armed the flag), or the
                // current host itself, may (re)point the host link. An unsolicited inbound peer —
                // e.g. a P2P session opened by a stranger who learned our id from PEER_LIST — must
                // never become "the host" via this unconditional SetHostPeer + JOIN.
                bool isCurrentHost = Session.HostPeerId.HasValue && Session.HostPeerId.Value == peerId;
                if (!_expectingHostLink && !isCurrentHost)
                {
                    Debug.LogWarning($"[Multiplayer] Ignoring unsolicited peer connect {peerId} on client — " +
                                     "not the dialed host; host link unchanged.");
                    return;
                }
                _expectingHostLink = false;
                Session.SetHostPeer(peerId);
                SendJoinToHost();
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

            // Teardown window: a transport drop surfacing after Shutdown/TearDown nulled Session
            // (the null-check above only resolved the name) must not NRE the whole event drain.
            //
            // THE funnel for every INVOLUNTARY loss (N=50 mandate): a dead socket, a stalled write, a
            // send channel that failed N times — they all arrive here, and none of them means the player
            // left. PAUSE, do not remove: the roster row, slot, permissions and guid binding survive, so
            // the peer resumes its own seat on reconnect instead of being a stranger who has to fight its
            // way back in. A peer that genuinely LEFT sends ClientLeave, and SessionManager.HandleLeave
            // still removes it there.
            // …via SessionManager.NotePeerLoss, which is the funnel that decides whether there is a seat
            // to hold at all before PausePeer holds it. A row whose JOIN never arrived has none (L180).
            bool ownedByTheFunnel = false;
            if (IsHost && Session != null && Session.Clients.ContainsKey(peerId))
            {
                Session.NotePeerLoss(peerId, endpoint == null ? "connection lost" : "connection lost (" + endpoint + ")");
                ownedByTheFunnel = true;
            }
            else Session?.RemoveClient(peerId); // client side, or a row already reclaimed — nothing to hold
            OnClientDisconnected?.Invoke(peerId);
            // Suppress the "— X left —" notice for anything the funnel above handled: a PAUSED peer did not
            // leave and PausePeer already posted the accurate line, and a row whose JOIN never arrived is
            // nobody the other players ever saw — announcing "Unknown left" for it would be the placeholder
            // name reaching four screens as a departure. HostLeaveHandler still sees the raise (it needs it
            // to detect a host loss on the CLIENT side, where nothing goes through this funnel).
            OnClientDisconnectedNamed?.Invoke(peerId, droppedName, wasKnown && !ownedByTheFunnel);

            // Advertising gate again (see OnPeerConnected). A PAUSED peer still holds its seat, so this
            // deliberately does not free one — it is holding the seat that lets the peer come back.
            if (IsHost)
                try { SteamLobbySetJoinable?.Invoke(Session.ClientCount < MaxClients); } catch { }

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
                // Receive-side dedup (after liveness — a duplicate still proves the sender is alive):
                // StunTransport sends every "reliable" packet twice with no receive filter, so without
                // this every STUN chat/JOIN/chunk could be processed twice. No-op on TCP/Steam.
                if (IsDuplicateMessage(senderSteamId, msg.MessageId)) return;
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

                // Presentation only — relayed and shown, never recorded. The host is the only peer with a
                // link to everyone, so it re-fans the marker to the others (the ChatMessage shape) before
                // showing its own. PingMarkerShow is a delegate for the same JIT-safety reason as
                // ParityConfigRestore below: NetworkEngine must not name the game types the marker draws.
                case PacketType.PingMarker:
                    if (IsHost) BroadcastExcept(msg.SenderSteamId, msg);
                    PingMarkerShow?.Invoke(msg.Payload);
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

                case PacketType.EntryTransferBegin:
                    SaveTransfer?.OnEntryTransferBegin(msg);
                    break;

                case PacketType.EntryTransferAbort:
                    SaveTransfer?.OnEntryTransferAbort(msg);
                    break;

                case PacketType.ParityUpdate:
                    // Client->host: refreshed parity manifest after the client auto-applied host settings.
                    Session.HandleParityUpdate(msg);
                    break;

                case PacketType.JoinReady:
                    // Client->host: a mid-session on-demand joiner reached the live geoscape; host re-seeds it.
                    SaveTransfer?.OnJoinReady(msg);
                    break;

                case PacketType.SyncEnvelope:
                    // THE one sync rail: every intent/delta rides this unified envelope, dispatched by
                    // Sync.OnSyncEnvelope -> SurfaceRouter -> the per-surface hooks (tactical fast-path,
                    // then geoscape 0xA0-0xBF, e.g. ResearchSync). No other sync packet types exist.
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
