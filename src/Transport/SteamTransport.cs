using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Multiplayer.Transport
{
    public class SteamTransport : ITransport
    {
        public TransportType TransportType => TransportType.SteamP2P;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint { get; private set; } = "";
        public System.Net.IPEndPoint PublicEndPoint => null;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        private ulong _localSteamId;
        // The exact delegate THIS instance installed into the static OnP2PSessionRequest field. Kept so
        // Shutdown() only clears the field when it still holds OUR handler — a newer transport that has
        // already overwritten the field (re-host before this teardown runs) must not be clobbered.
        private Delegate _installedSessionHandler;
        // Same ownership guard for the OnP2PConnectionFailed handler (see _installedSessionHandler).
        private Delegate _installedConnectFailHandler;
        private readonly HashSet<ulong> _connectedPeers = new HashSet<ulong>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();
        // FIX-2: consecutive send-failure count per peer. SendP2PPacket can return false (or throw) when
        // a P2P session is dead; the old catch {} hid this. After N in a row we surface it instead of
        // silently dropping every packet forever.
        private readonly Dictionary<ulong, int> _consecutiveSendFailures = new Dictionary<ulong, int>();
        private const int SendFailureThreshold = 5;
        // The SteamId this CLIENT dialed in Connect(). The only inbound P2P session a client may
        // accept — OnSessionRequest fires for ANY peer that knows our SteamId (it is broadcast in
        // PEER_LIST), and blindly accepting one used to let NetworkEngine's client OnPeerConnected
        // re-point the host link to the stranger (SetHostPeer + JOIN). 0 on the host / before dialing.
        private ulong _dialedHost;
        // RegisterSendFailure's host branch drops a dead peer from inside SendPacket — which Broadcast
        // may be mid-iterating. The peer leaves _connectedPeers immediately (stops further sends), but
        // the OnPeerDisconnected raise is DEFERRED here and drained in Update(), so session logic
        // (RemoveClient → BroadcastPeerList → nested Broadcast) never re-enters from inside a send loop.
        // Mirrors Direct/Stun's _peerEventQueue marshaling.
        private readonly List<ulong> _pendingSendFailureDrops = new List<ulong>();
        // One-shot log gate for CLIENT-side senders that are not the dialed host (see Update's drain
        // loop). Keyed by SteamId so a stranger spraying packets costs one line, not one per packet.
        private readonly HashSet<ulong> _unregisteredSendersLogged = new HashSet<ulong>();
        // Grace before CloseP2PSessionWithUser after a terminal send: Steam DISCARDS queued
        // not-yet-sent reliable packets on session close, so an immediate close right after a
        // SendHostDisconnected / ConnectionRejected silently ate the notice and the peer only found
        // out via its 20 s heartbeat timeout. Kicks (DisconnectPeer/Disconnect) defer the close here
        // and drain it in Update (skipped if the peer reconnected meanwhile); Shutdown — after which
        // Update never runs — closes from a one-shot background thread after the same grace.
        private const int SessionCloseGraceMs = 1000;
        private readonly List<(ulong peerId, int dueTick)> _deferredCloses = new List<(ulong, int)>();
        // Latest initialized transport — the Shutdown grace thread outlives THIS instance, so its
        // reconnect guard must consult whichever transport is live NOW (a rejoin within the grace
        // creates a NEW instance holding a fresh session with the same SteamId).
        private static volatile SteamTransport s_latest;

        // ─── Facepunch.Steamworks reflection handles (runtime-resolved) ───────────────────
        // GROUNDING (verified 2026-06-20 against the SHIPPED Facepunch.Steamworks.Win64.dll that PP
        // loads — NOT Assembly-CSharp). The P2P surface is entirely STATIC and SteamId is a STRUCT:
        //   Steamworks.SteamNetworking (static class):
        //     static Action<SteamId>            OnP2PSessionRequest        (a FIELD, not an event)
        //     static bool AcceptP2PSessionWithUser(SteamId user)
        //     static bool CloseP2PSessionWithUser(SteamId user)
        //     static bool IsP2PPacketAvailable(int channel = 0)
        //     static Nullable<Steamworks.Data.P2Packet> ReadP2PPacket(int channel = 0)
        //     static bool SendP2PPacket(SteamId steamid, byte[] data, int length = -1,
        //                               int nChannel = 0, P2PSend sendType = Reliable)
        //   Steamworks.SteamId (struct): public ulong Value; (build via Activator + set Value)
        //   Steamworks.P2PSend (enum): Unreliable=0, UnreliableNoDelay=1, Reliable=2, ReliableWithBuffering=3
        //   Steamworks.Data.P2Packet (struct): SteamId SteamId; byte[] Data;
        //   Steamworks.SteamClient: static SteamId SteamId; static bool IsValid;
        // The old code targeted the wrong shape (SteamNetworking.Instance + instance binding + the
        // non-existent AcceptP2PSession/CloseP2PSession names) → every call was a silent no-op.
        private static Type _steamClientType;
        private static Type _steamNetworkingType;
        private static Type _steamIdType;
        private static FieldInfo _steamIdValueField;             // SteamId.Value (ulong)
        private static Type _p2pSendType;
        private static object _p2pSendReliable;                  // boxed P2PSend.Reliable (=2)
        private static object _p2pSendUnreliable;                // boxed P2PSend.Unreliable (=0)
        private static FieldInfo _onP2PSessionRequestField;      // static Action<SteamId>
        private static FieldInfo _onP2PConnectionFailedField;    // static Action<SteamId, P2PSessionError> — OPTIONAL
        private static MethodInfo _acceptSessionMethod;          // AcceptP2PSessionWithUser(SteamId)
        private static MethodInfo _closeSessionMethod;           // CloseP2PSessionWithUser(SteamId)
        private static MethodInfo _allowRelayMethod;             // AllowP2PPacketRelay(bool) — OPTIONAL (relay fallback)
        private static MethodInfo _isPacketAvailableMethod;      // IsP2PPacketAvailable(int)
        private static MethodInfo _readPacketMethod;             // ReadP2PPacket(int) -> Nullable<P2Packet>
        private static MethodInfo _sendPacketMethod;             // SendP2PPacket(SteamId,byte[],int,int,P2PSend)
        private static FieldInfo _packetSteamIdField;            // P2Packet.SteamId
        private static FieldInfo _packetDataField;               // P2Packet.Data
        private static bool _apiResolved;

        // Locate the assembly that actually defines the Steamworks types. PP ships them in
        // Facepunch.Steamworks.Win64.dll (loaded at runtime), so scan ALL loaded assemblies for the
        // type rather than assuming a fixed assembly name.
        private static Type ResolveSteamType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        // Resolve every required P2P member up front. Returns false (and leaves a reason in
        // LocalEndpoint) if anything is missing, so callers fail loudly instead of no-op'ing.
        private bool ResolveApi(out string failureReason)
        {
            failureReason = null;
            if (_apiResolved && _steamNetworkingType != null && _onP2PSessionRequestField != null)
                return true;

            _steamClientType = ResolveSteamType("Steamworks.SteamClient");
            _steamNetworkingType = ResolveSteamType("Steamworks.SteamNetworking");
            _steamIdType = ResolveSteamType("Steamworks.SteamId");
            _p2pSendType = ResolveSteamType("Steamworks.P2PSend");

            if (_steamNetworkingType == null) { failureReason = "Steamworks.SteamNetworking not found"; return false; }
            if (_steamIdType == null) { failureReason = "Steamworks.SteamId not found"; return false; }
            if (_p2pSendType == null) { failureReason = "Steamworks.P2PSend not found"; return false; }

            _steamIdValueField = _steamIdType.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
            if (_steamIdValueField == null) { failureReason = "SteamId.Value field not found"; return false; }

            _p2pSendReliable = Enum.ToObject(_p2pSendType, 2);   // Reliable
            _p2pSendUnreliable = Enum.ToObject(_p2pSendType, 0); // Unreliable

            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
            _onP2PSessionRequestField = _steamNetworkingType.GetField("OnP2PSessionRequest", S);
            // OPTIONAL: Steam's authoritative "P2P session died" callback (remote quit / crashed / NAT
            // path lost). Declared next to OnP2PSessionRequest in the same static-field style. Resolved
            // best-effort and NOT part of the required-member gate — without it peer-loss detection
            // falls back to the heartbeat timeout.
            _onP2PConnectionFailedField = _steamNetworkingType.GetField("OnP2PConnectionFailed", S);
            _acceptSessionMethod = _steamNetworkingType.GetMethod("AcceptP2PSessionWithUser", S, null, new[] { _steamIdType }, null);
            _closeSessionMethod = _steamNetworkingType.GetMethod("CloseP2PSessionWithUser", S, null, new[] { _steamIdType }, null);
            // OPTIONAL: relay-fallback toggle. Steam defaults P2P packet relay ON, so this is normally a
            // confirming no-op — resolved best-effort and NOT part of the required-member gate below.
            _allowRelayMethod = _steamNetworkingType.GetMethod("AllowP2PPacketRelay", S, null, new[] { typeof(bool) }, null);

            // IsP2PPacketAvailable has overloads; pick the (int channel) one.
            _isPacketAvailableMethod = _steamNetworkingType.GetMethods(S)
                .FirstOrDefault(m => m.Name == "IsP2PPacketAvailable"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(int));

            // ReadP2PPacket(int) -> Nullable<P2Packet>; pick the one returning a generic Nullable.
            _readPacketMethod = _steamNetworkingType.GetMethods(S)
                .FirstOrDefault(m => m.Name == "ReadP2PPacket"
                    && m.ReturnType.IsGenericType
                    && m.ReturnType.GetGenericTypeDefinition() == typeof(Nullable<>));

            // SendP2PPacket(SteamId, byte[], int, int, P2PSend) — the managed byte[] overload.
            _sendPacketMethod = _steamNetworkingType.GetMethods(S)
                .FirstOrDefault(m => m.Name == "SendP2PPacket"
                    && m.GetParameters().Length >= 2
                    && m.GetParameters()[1].ParameterType == typeof(byte[]));

            if (_onP2PSessionRequestField == null) { failureReason = "SteamNetworking.OnP2PSessionRequest field not found"; return false; }
            if (_acceptSessionMethod == null) { failureReason = "AcceptP2PSessionWithUser not found"; return false; }
            if (_closeSessionMethod == null) { failureReason = "CloseP2PSessionWithUser not found"; return false; }
            if (_isPacketAvailableMethod == null) { failureReason = "IsP2PPacketAvailable(int) not found"; return false; }
            if (_readPacketMethod == null) { failureReason = "ReadP2PPacket(int) not found"; return false; }
            if (_sendPacketMethod == null) { failureReason = "SendP2PPacket(byte[]) not found"; return false; }

            var packetType = Nullable.GetUnderlyingType(_readPacketMethod.ReturnType); // Steamworks.Data.P2Packet
            _packetSteamIdField = packetType?.GetField("SteamId", BindingFlags.Public | BindingFlags.Instance);
            _packetDataField = packetType?.GetField("Data", BindingFlags.Public | BindingFlags.Instance);
            if (_packetSteamIdField == null || _packetDataField == null)
            {
                failureReason = "P2Packet.SteamId/.Data fields not found";
                return false;
            }

            _apiResolved = true;
            return true;
        }

        public void Initialize()
        {
            try
            {
                if (!ResolveApi(out var reason))
                    throw new InvalidOperationException("Steamworks P2P API unavailable: " + reason);

                // PRIMARY FIX for one-way NAT / VPN sessions (live-confirmed: a client behind a VPN could
                // send to the host, but the host→client return died at the VPN exit — inbound UDP filtered).
                // AllowP2PPacketRelay(true) routes P2P through Valve's relay servers when a direct / NAT-punch
                // path is one-way, making BOTH directions outbound-only and thus VPN-transparent. Must be set
                // BEFORE any session is created (Host/Connect run later) → Initialize is the right place. The
                // mod NEVER called this before, so relay was left to the ambient default, which the VPN case
                // proved insufficient. Never-silent: log whether it applied or is absent from the shipped
                // Facepunch build, so the live retest gives clean evidence. (ResetPeer is the resilience layer
                // on top: a one-shot session reset + re-JOIN if a link still wedges.)
                if (_allowRelayMethod != null)
                {
                    try
                    {
                        _allowRelayMethod.Invoke(null, new object[] { true });
                        UnityEngine.Debug.Log("[Multiplayer] SteamTransport: AllowP2PPacketRelay(true) applied — " +
                                              "relay fallback ON (one-way NAT / VPN resilience).");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning("[Multiplayer] SteamTransport: AllowP2PPacketRelay(true) threw: " + ex.Message);
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[Multiplayer] SteamTransport: AllowP2PPacketRelay NOT FOUND in the shipped " +
                                                 "Facepunch build — cannot force relay fallback; one-way NAT / VPN sessions may fail.");
                }

                // Local SteamId (struct) → ulong via its Value field, for diagnostics/endpoint label.
                if (_steamClientType != null)
                {
                    var steamIdProp = _steamClientType.GetProperty("SteamId",
                        BindingFlags.Public | BindingFlags.Static);
                    var localSteamId = steamIdProp?.GetValue(null, null);
                    if (localSteamId != null)
                        _localSteamId = (ulong)_steamIdValueField.GetValue(localSteamId);
                }
                LocalEndpoint = $"Steam({_localSteamId})";

                // Wire the inbound session-request handler. OnP2PSessionRequest is a STATIC FIELD of
                // type Action<SteamId>; we build a matching delegate via a compiled expression that
                // unwraps the SteamId struct to its ulong Value and forwards to OnSessionRequest. The
                // mod assembly can't name SteamId at compile time (no Facepunch reference), so the
                // expression's parameter type is the runtime SteamId Type. (Mono/Reflection.Emit is
                // available here — Harmony depends on it.)
                var sessionHandler = BuildSessionRequestDelegate();
                _installedSessionHandler = sessionHandler;   // remember OUR handler for a guarded Shutdown clear
                _onP2PSessionRequestField.SetValue(null, sessionHandler);
                s_latest = this;   // for the Shutdown grace thread's cross-instance reconnect guard

                // Wire Steam's session-failed callback the same way. Never-silent: log whether it armed —
                // a signature miss must be visible in the Player.log, not a silent detection downgrade.
                if (_onP2PConnectionFailedField != null)
                {
                    try
                    {
                        var failHandler = BuildConnectFailDelegate();
                        _installedConnectFailHandler = failHandler;
                        _onP2PConnectionFailedField.SetValue(null, failHandler);
                        UnityEngine.Debug.Log("[Multiplayer] SteamTransport: OnP2PConnectionFailed hook armed " +
                                              "(instant peer-loss detection).");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError("[Multiplayer] SteamTransport: OnP2PConnectionFailed hook FAILED " +
                                                   $"to build/install ({ex.Message}) — peer loss falls back to the " +
                                                   "heartbeat timeout.");
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[Multiplayer] SteamTransport: OnP2PConnectionFailed field NOT FOUND " +
                                                 "in the shipped Facepunch build — peer loss falls back to the " +
                                                 "heartbeat timeout.");
                }

                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
            }
            catch (Exception ex)
            {
                LocalEndpoint = $"SteamError: {ex.Message}";
                State = ConnectionState.Failed;
                OnStateChanged?.Invoke(State);
            }
        }

        // Build an Action<SteamId> (the field's exact delegate type) that reads SteamId.Value and
        // calls this instance's ulong-typed OnSessionRequest. Uses a compiled expression so the
        // value-type parameter binds correctly (delegate variance does not apply to value types, so
        // a void(object) shim cannot satisfy Action<SteamId>).
        private Delegate BuildSessionRequestDelegate()
        {
            var handlerMi = typeof(SteamTransport).GetMethod("OnSessionRequest",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var param = Expression.Parameter(_steamIdType, "steamId");
            var body = Expression.Call(
                Expression.Constant(this),
                handlerMi,
                Expression.Field(param, _steamIdValueField));   // steamId.Value (ulong)
            return Expression.Lambda(_onP2PSessionRequestField.FieldType, body, param).Compile();
        }

        // Build a delegate matching the OnP2PConnectionFailed field's exact type
        // (Action<SteamId, P2PSessionError>) without naming either Steamworks type at compile time:
        // parameter types come from the field's delegate Invoke signature; SteamId unwraps via .Value
        // and the error enum converts to int for logging. Mirrors BuildSessionRequestDelegate.
        private Delegate BuildConnectFailDelegate()
        {
            var handlerMi = typeof(SteamTransport).GetMethod("OnP2PConnectFail",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var invoke = _onP2PConnectionFailedField.FieldType.GetMethod("Invoke");
            var pars = invoke.GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var body = Expression.Call(
                Expression.Constant(this),
                handlerMi,
                Expression.Field(pars[0], _steamIdValueField),   // steamId.Value (ulong)
                Expression.Convert(pars[1], typeof(int)));       // P2PSessionError → int
            return Expression.Lambda(_onP2PConnectionFailedField.FieldType, body, pars).Compile();
        }

        // Box a ulong into a Steamworks.SteamId struct for passing to the static P2P methods.
        private object MakeSteamId(ulong id)
        {
            var box = Activator.CreateInstance(_steamIdType);
            _steamIdValueField.SetValue(box, id);   // mutates the boxed struct
            return box;
        }

        public void Shutdown()
        {
            // Best-effort: clear the static session-request handler so a torn-down transport can't
            // keep accepting inbound peers, then close every open P2P session. Only clear if the static
            // field STILL holds the delegate WE installed — if a newer transport (re-host before this
            // teardown ran) already overwrote it, nulling here would silently disarm the live handler.
            if (_onP2PSessionRequestField != null && _installedSessionHandler != null)
            {
                try
                {
                    var current = _onP2PSessionRequestField.GetValue(null) as Delegate;
                    if (ReferenceEquals(current, _installedSessionHandler))
                        _onP2PSessionRequestField.SetValue(null, null);
                }
                catch { }
            }
            _installedSessionHandler = null;
            // Same guarded clear for the session-failed handler (never clobber a newer transport's hook).
            if (_onP2PConnectionFailedField != null && _installedConnectFailHandler != null)
            {
                try
                {
                    var current = _onP2PConnectionFailedField.GetValue(null) as Delegate;
                    if (ReferenceEquals(current, _installedConnectFailHandler))
                        _onP2PConnectionFailedField.SetValue(null, null);
                }
                catch { }
            }
            _installedConnectFailHandler = null;
            // Grace-close on a one-shot background thread: Update stops after Shutdown, and closing
            // NOW would drop a just-queued terminal notice (HostDisconnected — see SessionCloseGraceMs).
            // Include any kicks still waiting out their deferred grace. Steamworks' flat API is
            // thread-safe for session close; IsBackground so the thread can never hold the process.
            var toClose = new List<ulong>(_connectedPeers);
            foreach (var d in _deferredCloses) toClose.Add(d.peerId);
            _deferredCloses.Clear();
            _connectedPeers.Clear();
            if (toClose.Count > 0)
            {
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(SessionCloseGraceMs);
                    foreach (var peer in toClose)
                    {
                        // Mirror the Update-drain reconnect guard: if the live transport (possibly a
                        // NEWER instance — rejoin/re-host inside the grace) holds a fresh session with
                        // this SteamId, closing it here would kill that rejoin. Unsynchronized cross-
                        // thread read of _connectedPeers is best-effort by design (try/catch below).
                        bool reconnected = false;
                        try { var live = s_latest; reconnected = live != null && live._connectedPeers.Contains(peer); }
                        catch { }
                        if (!reconnected) CloseSession(peer);
                    }
                }) { IsBackground = true }.Start();
            }
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = 0)
        {
            IsHost = true;
        }

        public void Connect(string address, int port)
        {
            if (ulong.TryParse(address, out ulong targetId))
            {
                State = ConnectionState.Connecting;
                OnStateChanged?.Invoke(State);
                _dialedHost = targetId;
                AcceptSession(targetId);
                _connectedPeers.Add(targetId);
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(targetId, $"Steam({targetId})");
            }
        }

        public void Disconnect()
        {
            foreach (var peer in _connectedPeers)
            {
                // Deferred (not immediate) close so a just-queued graceful-leave notice still flushes
                // out of Steam's send queue — see SessionCloseGraceMs. Drained in Update.
                _deferredCloses.Add((peer, Environment.TickCount + SessionCloseGraceMs));
                OnPeerDisconnected?.Invoke(peer, $"Steam({peer})");
            }
            _connectedPeers.Clear();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        // Per-peer kick (heartbeat timeout / graceful leave / reject): forget the peer so Broadcast
        // stops sending to the dead SteamId forever, but close the P2P session on the DEFERRED grace
        // (see SessionCloseGraceMs) so a terminal notice queued just before the kick — the
        // ConnectionRejected the straggler/transfer/JOIN reject paths send — is not discarded with
        // the session. Raises OnPeerDisconnected inline — same-main-thread precedent as
        // RegisterSendFailure's host branch.
        public bool DisconnectPeer(ulong peerId)
        {
            if (!_connectedPeers.Remove(peerId)) return false;
            _deferredCloses.Add((peerId, Environment.TickCount + SessionCloseGraceMs));
            _consecutiveSendFailures.Remove(peerId);
            OnPeerDisconnected?.Invoke(peerId, $"Steam({peerId})");
            return true;
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            SendPacket(peerId, data, reliable);
        }

        // Answers from the EXACT set Broadcast iterates below — that is the whole point (L136 arm (c)
        // pins the two to the same field, so this can never become a comforting lie).
        public bool CanReach(ulong peerId) => _connectedPeers.Contains(peerId);

        public void Broadcast(byte[] data, bool reliable = true)
        {
            // Snapshot: SendPacket → RegisterSendFailure removes a dead peer from _connectedPeers
            // mid-loop, which threw InvalidOperationException and aborted the REST of the broadcast
            // (CompositeTransport swallows it → silent chunk loss for the remaining peers).
            foreach (var peer in _connectedPeers.ToArray())
            {
                SendPacket(peer, data, reliable);
            }
        }

        public void Update()
        {
            // Deferred session closes that reached their grace: close unless the peer reconnected
            // in the window (a fresh session with the same SteamId must not be killed by a stale kick).
            for (int i = _deferredCloses.Count - 1; i >= 0; i--)
            {
                if (Environment.TickCount - _deferredCloses[i].dueTick < 0) continue;
                var id = _deferredCloses[i].peerId;
                _deferredCloses.RemoveAt(i);
                if (!_connectedPeers.Contains(id)) CloseSession(id);
            }

            while (IsP2PPacketAvailable())
            {
                var packet = ReadP2PPacket();
                if (packet.HasValue)
                {
                    _incomingQueue.Enqueue((packet.Value.SteamId, packet.Value.Data));
                }
            }

            while (_incomingQueue.Count > 0)
            {
                var (steamId, data) = _incomingQueue.Dequeue();

                // ─── THE PEER REGISTRATION STEAM NEVER GIVES US ───────────────────────────────
                //
                // OnP2PSessionRequest is EDGE-triggered: Steam raises it only when NO P2P session with
                // that SteamId exists yet, and the session is PROCESS-global — it outlives our transport.
                // So on any RETRY with the same SteamId (a rejoin after a failed attempt, i.e. exactly
                // what a beta user does) the callback never fires again, OnSessionRequest below never
                // runs, and _connectedPeers — which until now was written ONLY by Connect() and
                // OnSessionRequest — never learns the peer exists.
                //
                // The peer is not silent, though: its packets arrive right here, and every UNICAST reply
                // reaches it, because Send/SendPacket do not consult _connectedPeers. Broadcast does
                // (see the loop above). That split is the whole bug: the host accepted the JOIN and
                // unicast the acceptance, then BroadcastPeerList → FlushPeerList → BroadcastToAll
                // silently skipped the joiner — and PEER_LIST is the ONLY thing that ends the client's
                // "Connecting…" box (MultiplayerUI.Update). No exception, no dropped packet, no log line:
                // the joiner just waited out its heartbeat and left.
                //
                // THE INVARIANT, and it is transport-wide rather than a Steam special case: ANY PEER WE
                // HAVE EVER RECEIVED A PACKET FROM IS A KNOWN PEER. DirectTransport upholds it via
                // accept(), StunTransport via its endpoint table (it discards packets from endpoints it
                // has not registered); Steam's shared per-process read queue is the one place that can
                // hand us bytes from a peer we never registered, so this is where it has to be closed.
                //
                // HOST/CLIENT ASYMMETRY — deliberately the SAME predicate as OnSessionRequest's guard
                // below: a HOST may learn a peer from its packets, a CLIENT may not. A client's only
                // legitimate peer is the host it DIALED, and every peer in the session knows our SteamId
                // (PEER_LIST broadcasts it), so registering an arbitrary sender would hand
                // NetworkEngine's client OnPeerConnected a stranger to re-point the host link at
                // (SetHostPeer + JOIN). Packet DELIVERY is unchanged on both sides — only registration
                // is gated.
                if (IsHost || steamId == _dialedHost)
                {
                    // Raised BEFORE the packet on purpose, so the session layer holds the peer's roster
                    // row before it parses the JOIN riding in this very packet — the "connect precedes
                    // packets" ordering Direct/Stun get for free from their peer-event queues.
                    // HashSet.Add gates this to ONCE per peer, so the downstream one-shots stay one-shot:
                    // SessionManager.AddClient (itself ContainsKey-guarded), the SteamLobbySetJoinable
                    // advertising toggle, and BroadcastPeerList (which only marks a dirty flag that
                    // FlushPeerList collapses to one roster per frame) cannot double-fire.
                    if (_connectedPeers.Add(steamId))
                    {
                        UnityEngine.Debug.LogWarning($"[Multiplayer] SteamTransport: packet from UNREGISTERED peer " +
                                                     $"{steamId} — no P2P session-request fired for it (stale " +
                                                     $"process-global Steam session, typical on a retry). " +
                                                     $"Registering it now so Broadcast reaches it too.");
                        OnPeerConnected?.Invoke(steamId, $"Steam({steamId})");
                    }
                }
                else if (_unregisteredSendersLogged.Add(steamId))
                {
                    // Never-silent, once per stranger: this is the client half of the same invisible event.
                    UnityEngine.Debug.LogWarning($"[Multiplayer] SteamTransport(client): packet from {steamId}, " +
                                                 $"which is not the dialed host ({_dialedHost}) — NOT registering " +
                                                 $"it as a peer (host-link hijack guard).");
                }

                OnPacketReceived?.Invoke(steamId, data);
            }

            // Send-failure drops last, AFTER the packets (deferred out of the send loops — see the
            // field note). Same reason DirectTransport.Update announces its drops last: a peer whose
            // send channel died may still have its own ClientLeave sitting in the P2P queue we just
            // drained, and reporting the death first turns a deliberate quit into "lost connection".
            // NOTE this only orders the drops that funnel through Update. Steam's other disconnect
            // raises (RegisterSendFailure's inline branch, DisconnectPeer, the P2P session-failure
            // callback) fire OUTSIDE this pump and cannot be ordered against packets by moving code
            // here — for those the guarantee is the CompositeTransport one: the peer keeps its id,
            // so the latch still collapses the pair to a single NAMED notice.
            if (_pendingSendFailureDrops.Count > 0)
            {
                var drops = _pendingSendFailureDrops.ToArray();
                _pendingSendFailureDrops.Clear();
                foreach (var peer in drops)
                    OnPeerDisconnected?.Invoke(peer, $"Steam({peer})");
            }
        }

        // ─── Reflection Helpers (all P2P members are STATIC; SteamId args are boxed structs) ──

        private void AcceptSession(ulong peerId)
        {
            try { _acceptSessionMethod?.Invoke(null, new[] { MakeSteamId(peerId) }); } catch { }
        }

        private void CloseSession(ulong peerId)
        {
            try { _closeSessionMethod?.Invoke(null, new[] { MakeSteamId(peerId) }); } catch { }
        }

        // Half-open recovery (client-driven, one-shot): tear down the WEDGED P2P session to this peer so
        // the next SendP2PPacket opens a FRESH session — a new NAT-punch / relay path — which the remote
        // re-accepts via its still-installed OnP2PSessionRequest handler. The caller re-sends the JOIN
        // right after, re-driving the handshake. Keep the peer in _connectedPeers (it is still logically
        // our host) and clear its stale send-failure count. No-op if the API never resolved (test path).
        // Steam-specific by design — reached via a `Transport as SteamTransport` type-check at the single
        // call site (NetworkEngine.RepairHostLink), NOT the ITransport surface.
        public void ResetPeer(ulong peerId)
        {
            CloseSession(peerId);
            _consecutiveSendFailures.Remove(peerId);
        }

        private void SendPacket(ulong peerId, byte[] data, bool reliable)
        {
            if (_sendPacketMethod == null) return;
            try
            {
                var sendType = reliable ? _p2pSendReliable : _p2pSendUnreliable;
                // SendP2PPacket(SteamId, byte[], int length=-1, int nChannel=0, P2PSend sendType)
                var result = _sendPacketMethod.Invoke(null,
                    new object[] { MakeSteamId(peerId), data, -1, 0, sendType });
                // FIX-2: the Facepunch API returns bool (true = queued to the P2P session). A false
                // return OR a thrown exception is a real send failure — stop swallowing it. A non-bool
                // result is treated as success (defensive; the resolved overload returns bool).
                bool ok = !(result is bool b) || b;
                if (ok) { _consecutiveSendFailures.Remove(peerId); return; }
                RegisterSendFailure(peerId, "SendP2PPacket returned false");
            }
            catch (Exception ex)
            {
                RegisterSendFailure(peerId, ex.Message);
            }
        }

        // FIX-2: count consecutive send failures per peer; log the first, and after N in a row surface
        // the dead channel through EXISTING plumbing instead of the old silent catch {}. On a CLIENT
        // (only peer = the host) a dead send channel is session-fatal → State=Failed → OnStateChanged →
        // NetworkEngine.OnConnectionFailed (never-silent dialog + teardown). On the HOST a dead channel
        // to ONE client is just that client dropping → route through the per-peer OnPeerDisconnected path
        // (Session.RemoveClient + the F1 drop notice), never a host-wide failure.
        private void RegisterSendFailure(ulong peerId, string reason)
        {
            _consecutiveSendFailures.TryGetValue(peerId, out var count);
            count++;
            _consecutiveSendFailures[peerId] = count;
            if (count == 1)
                UnityEngine.Debug.LogWarning($"[Multiplayer] SteamTransport: send to {peerId} failed (1st): {reason}");
            if (count < SendFailureThreshold) return;

            _consecutiveSendFailures[peerId] = 0; // reset so we do not re-raise on every subsequent send
            if (IsHost)
            {
                UnityEngine.Debug.LogError($"[Multiplayer] SteamTransport(host): send to client {peerId} failed " +
                                           $"{SendFailureThreshold}x ({reason}) — treating the client as disconnected.");
                // Remove NOW (stops further sends to the dead id) but raise the disconnect from
                // Update(): this runs inside SendPacket, possibly mid-Broadcast — raising inline
                // re-entered session logic (RemoveClient → BroadcastPeerList → nested Broadcast)
                // from inside the send loop.
                if (_connectedPeers.Remove(peerId))
                    _pendingSendFailureDrops.Add(peerId);
            }
            else
            {
                UnityEngine.Debug.LogError($"[Multiplayer] SteamTransport(client): send to host {peerId} failed " +
                                           $"{SendFailureThreshold}x ({reason}) — surfacing as transport failure.");
                LocalEndpoint = $"Steam P2P send channel dead to host: {reason} ({SendFailureThreshold}x)";
                State = ConnectionState.Failed;
                OnStateChanged?.Invoke(State);
            }
        }

        private bool IsP2PPacketAvailable()
        {
            try
            {
                if (_isPacketAvailableMethod != null)
                    return (bool)_isPacketAvailableMethod.Invoke(null, new object[] { 0 }); // channel 0
            }
            catch { }
            return false;
        }

        private P2PPacket? ReadP2PPacket()
        {
            try
            {
                if (_readPacketMethod == null) return null;
                var result = _readPacketMethod.Invoke(null, new object[] { 0 }); // channel 0
                if (result == null) return null; // Nullable<P2Packet> with no value → boxed as null

                // result is a boxed Steamworks.Data.P2Packet struct. Read its SteamId (struct) and
                // unwrap to ulong via SteamId.Value, plus the Data byte[].
                var steamIdBox = _packetSteamIdField.GetValue(result);
                var data = (byte[])_packetDataField.GetValue(result);
                var steamId = (ulong)_steamIdValueField.GetValue(steamIdBox);
                return new P2PPacket { SteamId = steamId, Data = data };
            }
            catch { }
            return null;
        }

        // Steam declared the P2P session to this peer dead (remote quit / crashed / path lost): called
        // from the compiled shim with the unwrapped ulong + raw P2PSessionError. Route through the SAME
        // per-peer drop path as a send-failure: on the CLIENT the dropped peer is the host →
        // NetworkEngine → HostLeaveHandler (F3 menu return, intentional-teardown suppressed); on the
        // HOST it is a client drop (Session purge + F1 notice). A stale callback for an already-dropped
        // peer is a no-op.
        private void OnP2PConnectFail(ulong remoteSteamId, int error)
        {
            if (!_connectedPeers.Remove(remoteSteamId)) return;
            UnityEngine.Debug.LogWarning($"[Multiplayer] SteamTransport: P2P session to {remoteSteamId} failed " +
                                         $"(P2PSessionError={error}) — dropping peer.");
            _consecutiveSendFailures.Remove(remoteSteamId);
            OnPeerDisconnected?.Invoke(remoteSteamId, $"Steam({remoteSteamId})");
        }

        // Inbound peer accepted: called from the compiled Action<SteamId> shim with the unwrapped
        // ulong. Accepts the session and surfaces the new peer.
        private void OnSessionRequest(ulong remoteSteamId)
        {
            // CLIENT: the only legitimate inbound session is the host we DIALED (a fresh NAT path
            // after ResetPeer, or the host's reply leg). Any other SteamId — knowable to every peer
            // via the PEER_LIST broadcast — must not be accepted: NetworkEngine's client
            // OnPeerConnected would re-point the host link to it (SetHostPeer + JOIN).
            if (!IsHost && remoteSteamId != _dialedHost)
            {
                UnityEngine.Debug.LogWarning($"[Multiplayer] SteamTransport(client): ignoring inbound P2P " +
                                             $"session from {remoteSteamId} — not the dialed host ({_dialedHost}).");
                CloseSession(remoteSteamId);
                return;
            }
            AcceptSession(remoteSteamId);
            // Raise only for a NEW peer: a re-request from a known peer (fresh session after a NAT
            // path loss) must not re-fire OnPeerConnected — on a client that meant a duplicate
            // SetHostPeer + JOIN.
            if (_connectedPeers.Add(remoteSteamId))
                OnPeerConnected?.Invoke(remoteSteamId, $"Steam({remoteSteamId})");
        }

        private struct P2PPacket
        {
            public ulong SteamId;
            public byte[] Data;
        }
    }

}
