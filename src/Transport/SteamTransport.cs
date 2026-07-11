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
        private readonly HashSet<ulong> _connectedPeers = new HashSet<ulong>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();
        // FIX-2: consecutive send-failure count per peer. SendP2PPacket can return false (or throw) when
        // a P2P session is dead; the old catch {} hid this. After N in a row we surface it instead of
        // silently dropping every packet forever.
        private readonly Dictionary<ulong, int> _consecutiveSendFailures = new Dictionary<ulong, int>();
        private const int SendFailureThreshold = 5;

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

                // Belt for half-open P2P: EXPLICITLY assert Steam relay fallback so a NAT-punch-only session
                // that goes one-way is carried by Steam's relay servers instead. This must be set BEFORE any
                // session is created (Host/Connect run later), so Initialize is the right place. Steam defaults
                // it ON — normally a confirming no-op — but we set it in case a platform/mod turned it off.
                // Null-safe: absent in the shipped Facepunch build → reflection stays null → skipped. NOT the
                // primary half-open fix (that is the client one-shot session-reset + re-JOIN, see ResetPeer).
                try { _allowRelayMethod?.Invoke(null, new object[] { true }); } catch { }

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
            foreach (var peer in _connectedPeers)
            {
                CloseSession(peer);
            }
            _connectedPeers.Clear();
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
                CloseSession(peer);
                OnPeerDisconnected?.Invoke(peer, $"Steam({peer})");
            }
            _connectedPeers.Clear();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            SendPacket(peerId, data, reliable);
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            foreach (var peer in _connectedPeers)
            {
                SendPacket(peer, data, reliable);
            }
        }

        public void Update()
        {
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
                OnPacketReceived?.Invoke(steamId, data);
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
                _connectedPeers.Remove(peerId);
                OnPeerDisconnected?.Invoke(peerId, $"Steam({peerId})");
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

        // Inbound peer accepted: called from the compiled Action<SteamId> shim with the unwrapped
        // ulong. Accepts the session and surfaces the new peer.
        private void OnSessionRequest(ulong remoteSteamId)
        {
            AcceptSession(remoteSteamId);
            _connectedPeers.Add(remoteSteamId);
            OnPeerConnected?.Invoke(remoteSteamId, $"Steam({remoteSteamId})");
        }

        private struct P2PPacket
        {
            public ulong SteamId;
            public byte[] Data;
        }
    }

}
