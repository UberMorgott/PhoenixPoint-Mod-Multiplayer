using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Multipleer.Transport
{
    public class StunTransport : ITransport
    {
        public TransportType TransportType => TransportType.StunUDP;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint { get; private set; } = "";
        public IPEndPoint PublicEndPoint => _publicEndPoint;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        // Google public STUN servers
        private static readonly string[] StunServers = {
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
            "stun2.l.google.com:19302",
            "stun3.l.google.com:19302",
            "stun4.l.google.com:19302"
        };

        private UdpClient _udp;
        private IPEndPoint _publicEndPoint;
        private readonly Dictionary<ulong, IPEndPoint> _peers = new Dictionary<ulong, IPEndPoint>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();
        private readonly object _lock = new object();
        private volatile bool _running;
        private Thread _receiveThread;
        private long _nextPeerId = 1;
        private int _listenPort;

        // ─── Non-blocking client connect (anti-freeze) ─────────────────────
        // STUN discovery + hole-punch block for up to a few seconds; running them on the Unity main
        // thread (Connect is called from the lobby's OnLobbyJoin) freezes the game. The connect runs
        // on this worker and the outcome is surfaced from Update() on the main thread.
        private Thread _connectThread;
        private volatile bool _connectAborted;
        private bool _pendingConnectResult;       // a result is waiting to be surfaced (guarded by _lock)
        private bool _pendingConnectSucceeded;    // true = connected, false = failed
        private ulong _pendingPeerId;             // minted peer id (success only)
        private IPEndPoint _pendingRemoteEp;      // remote endpoint (success only)

        // ─── STUN discovery coordination ──────────────────────────────────
        // The transport binds a SINGLE UdpClient (_udp) and ReceiveLoop is the ONLY thread that
        // calls _udp.Receive on it. Earlier, DiscoverPublicEndpoint ALSO read _udp directly, so the
        // STUN binding RESPONSE was usually swallowed by ReceiveLoop (it is neither HOLE_PUNCH nor a
        // known peer) → discovery returned null → "discovery failed". Now ReceiveLoop recognises a
        // STUN binding response (magic cookie + message type) WHILE discovery is pending and hands
        // the parsed public endpoint to the waiting discovery thread via this signal. Using the same
        // socket is REQUIRED: the NAT mapping the STUN server reports must be the mapping of this
        // transport's own socket, or the ConnectCode would point at the wrong public port and the
        // hole-punch would fail.
        private volatile bool _stunDiscoveryPending;
        private volatile IPEndPoint _stunResult;
        private readonly ManualResetEventSlim _stunSignal = new ManualResetEventSlim(false);

        // Default UDP port the host binds in Host(); also the loopback port a same-machine
        // client redirects to (see Connect).
        private const int DefaultStunPort = 14242;

        private const uint StunMagicCookie = 0x2112A442;
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingResponse = 0x0101;
        private const ushort AttrXorMappedAddress = 0x0020;
        private const ushort AttrMappedAddress = 0x0001;

        public void Initialize()
        {
        }

        public void Shutdown()
        {
            _running = false;
            // Abort any in-flight client connect worker so a late discovery result is not surfaced
            // into a torn-down session.
            _connectAborted = true;
            // Release any discovery thread still blocked on the per-server wait so it can observe
            // _running == false and exit promptly instead of hanging on the timeout.
            _stunDiscoveryPending = false;
            _stunSignal.Set();
            _udp?.Close();
            _receiveThread?.Join(1000);
            _connectThread?.Join(1000);
            lock (_lock)
            {
                _peers.Clear();
                _pendingConnectResult = false;
                _pendingRemoteEp = null;
            }
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = DefaultStunPort)
        {
            IsHost = true;
            _listenPort = port;
            StartUdp(port);
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);

            // Host-side STUN discovery so the host can advertise a public endpoint (R3: may fail
            // → placeholder). Runs on a background thread because DiscoverPublicEndpoint blocks on
            // per-server waits; the rail polls PublicEndPoint and shows "discovering…" until it fills
            // in. RETRY periodically: a transient first-pass failure (DNS hiccup, dropped UDP, NIC
            // not ready) recovers on a later round and the ConnectCode then appears LIVE — the lobby
            // rail re-reads every frame (MultiplayerUI.Update), so no reopen is needed. We stop once
            // resolved or the host shuts down. The state message distinguishes a transient retry
            // ("discovering…") from a definitive give-up ("NAT/firewall") so the UX is accurate.
            const int maxRounds = 6;       // ~ up to a minute of retries before giving up
            const int retryDelayMs = 8000;
            var t = new Thread(() =>
            {
                for (int round = 0; round < maxRounds && _running && _publicEndPoint == null; round++)
                {
                    try
                    {
                        var pub = DiscoverPublicEndpoint();
                        if (pub != null)
                        {
                            _publicEndPoint = pub;
                            LocalEndpoint = $"STUN({pub})";
                            return;
                        }
                    }
                    catch { /* fall through to retry */ }

                    // Not resolved this round. Keep the "still trying" state visible unless this was
                    // the final attempt, in which case mark it unavailable (env-blocked: symmetric
                    // NAT / firewall blocking outbound UDP to the STUN servers).
                    if (round < maxRounds - 1 && _running)
                    {
                        LocalEndpoint = "STUN(discovering…)";
                        Thread.Sleep(retryDelayMs);
                    }
                    else
                    {
                        LocalEndpoint = "STUN(unavailable: NAT/firewall)";
                    }
                }
            }) { IsBackground = true };
            t.Start();
        }

        public void Connect(string address, int port)
        {
            IsHost = false;
            State = ConnectionState.Connecting;
            OnStateChanged?.Invoke(State);   // main thread (called from OnLobbyJoin) — safe

            StartUdp(0);

            // DiscoverPublicEndpoint blocks on per-server STUN waits (~up to 4s) and an invalid/
            // unresolvable target would otherwise stall the Unity main thread → freeze. Run the whole
            // discovery + hole-punch off the main thread and surface the outcome from Update() (main
            // thread) via the pending-connect fields, so OnStateChanged/OnPeerConnected — and the
            // MessageBox they can drive — never fire off-thread.
            _connectAborted = false;
            _connectThread = new Thread(() => ConnectWorker(address)) { IsBackground = true };
            _connectThread.Start();
        }

        // Background worker: STUN discovery + hole-punch. Records the outcome for Update() to surface
        // on the main thread; never raises Unity-facing events itself.
        private void ConnectWorker(string address)
        {
            try
            {
                // Discover public endpoint via STUN (bounded internally by the per-server timeouts).
                _publicEndPoint = DiscoverPublicEndpoint();
                LocalEndpoint = _publicEndPoint != null
                    ? $"STUN({_publicEndPoint})"
                    : "STUN(discovery failed)";

                if (_connectAborted) return;

                // Address format: "ip:port"
                if (address.Contains(":"))
                {
                    var parts = address.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var remotePort)
                        && IPAddress.TryParse(parts[0], out var ip))
                    {
                        // Same-machine fallback: two instances behind one NAT share a single public
                        // endpoint, so hole-punching the public IP can't reach a host on this PC (no
                        // NAT hairpin). If the target IP is our own discovered public address, the
                        // host is local — redirect to loopback on the host's default STUN port.
                        if (_publicEndPoint != null && ip.Equals(_publicEndPoint.Address))
                        {
                            Debug.Log($"[Multipleer] STUN same-machine: target {ip}:{remotePort} == own public " +
                                      $"address; redirecting to loopback 127.0.0.1:{DefaultStunPort}.");
                            ip = IPAddress.Loopback;
                            remotePort = DefaultStunPort;
                        }

                        var remoteEp = new IPEndPoint(ip, remotePort);
                        var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                        lock (_lock) { _peers[peerId] = remoteEp; }

                        // Hole punching: send dummy packets from different ports
                        SendRaw(remoteEp, Encoding.UTF8.GetBytes("HOLE_PUNCH"));

                        if (!_connectAborted)
                            QueueConnectResult(true, peerId, remoteEp);
                        return;
                    }
                }

                // Malformed address → connect failed (surfaced as Failed so the UI returns to lobby).
                if (!_connectAborted) QueueConnectResult(false, 0, null);
            }
            catch
            {
                if (!_connectAborted) QueueConnectResult(false, 0, null);
            }
        }

        private void QueueConnectResult(bool succeeded, ulong peerId, IPEndPoint remoteEp)
        {
            lock (_lock)
            {
                _pendingConnectResult = true;
                _pendingConnectSucceeded = succeeded;
                _pendingPeerId = peerId;
                _pendingRemoteEp = remoteEp;
            }
        }

        // Surface a completed connect attempt on the MAIN thread (called from Update). Keeps
        // OnStateChanged/OnPeerConnected — and any MessageBox they drive — on Unity's main thread.
        private void SurfacePendingConnect()
        {
            bool succeeded;
            ulong peerId;
            IPEndPoint remoteEp;
            lock (_lock)
            {
                if (!_pendingConnectResult) return;
                _pendingConnectResult = false;
                succeeded = _pendingConnectSucceeded;
                peerId = _pendingPeerId;
                remoteEp = _pendingRemoteEp;
                _pendingRemoteEp = null;
            }

            if (succeeded && remoteEp != null)
            {
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(peerId, $"STUN({remoteEp})");
            }
            else
            {
                State = ConnectionState.Failed;
                OnStateChanged?.Invoke(State);
            }
        }

        public void Disconnect()
        {
            _running = false;
            _udp?.Close();
            lock (_lock)
            {
                foreach (var kvp in _peers)
                {
                    OnPeerDisconnected?.Invoke(kvp.Key, kvp.Value.ToString());
                }
                _peers.Clear();
            }
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            lock (_lock)
            {
                if (_peers.TryGetValue(peerId, out var ep))
                {
                    SendRaw(ep, data);
                    if (reliable)
                    {
                        SendRaw(ep, data); // duplicate for reliability
                    }
                }
            }
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            lock (_lock)
            {
                foreach (var kvp in _peers)
                {
                    SendRaw(kvp.Value, data);
                    if (reliable)
                    {
                        SendRaw(kvp.Value, data);
                    }
                }
            }
        }

        public void Update()
        {
            // Surface a completed client-connect on the main thread first, then drain packets.
            SurfacePendingConnect();

            lock (_lock)
            {
                while (_incomingQueue.Count > 0)
                {
                    var (peerId, data) = _incomingQueue.Dequeue();
                    OnPacketReceived?.Invoke(peerId, data);
                }
            }
        }

        // ─── STUN Protocol (RFC 5389) ─────────────────────────────────────

        // Per-server wait for a STUN binding response (delivered by ReceiveLoop, the single socket
        // reader). 800ms comfortably covers an internet RTT to Google's STUN anycast plus parsing,
        // while keeping a full 5-server sweep under ~4s worst case.
        private const int StunPerServerTimeoutMs = 800;

        private IPEndPoint DiscoverPublicEndpoint()
        {
            // Arm the cooperative path: ReceiveLoop only diverts STUN binding responses to us while
            // this flag is set, so normal game traffic is never misrouted once discovery is done.
            _stunResult = null;
            _stunSignal.Reset();
            _stunDiscoveryPending = true;
            try
            {
                foreach (var stunServer in StunServers)
                {
                    try
                    {
                        var parts = stunServer.Split(':');
                        var stunHost = parts[0];
                        var stunPort = int.Parse(parts[1]);
                        var addrs = Dns.GetHostAddresses(stunHost);
                        if (addrs == null || addrs.Length == 0) continue;
                        var stunEp = new IPEndPoint(addrs[0], stunPort);

                        _stunSignal.Reset();
                        var request = CreateStunBindingRequest();
                        _udp.Send(request, request.Length, stunEp);

                        // Block until ReceiveLoop signals a parsed response or we time out. We do NOT
                        // touch _udp here — ReceiveLoop owns the socket and feeds us _stunResult.
                        if (_stunSignal.Wait(StunPerServerTimeoutMs) && _stunResult != null)
                            return _stunResult;
                    }
                    catch { continue; }
                }
                return null;
            }
            finally
            {
                _stunDiscoveryPending = false;
            }
        }

        private static byte[] CreateStunBindingRequest()
        {
            var transId = Guid.NewGuid().ToByteArray();
            var msg = new byte[20];
            msg[0] = 0x00; msg[1] = 0x01; // Binding Request
            msg[2] = 0x00; msg[3] = 0x00; // Length placeholder
            msg[4] = 0x21; msg[5] = 0x12; // Magic cookie (0x2112A442)
            msg[6] = 0xA4; msg[7] = 0x42;
            Array.Copy(transId, 0, msg, 8, 12); // Transaction ID (96 bits)
            return msg;
        }

        private static IPEndPoint ParseStunResponse(byte[] response)
        {
            if (response.Length < 20) return null;

            var msgType = (ushort)((response[0] << 8) | response[1]);
            if (msgType != BindingResponse) return null;

            var magic = (uint)((response[4] << 24) | (response[5] << 16) |
                               (response[6] << 8) | response[7]);
            if (magic != StunMagicCookie) return null;

            int offset = 20;
            while (offset + 4 <= response.Length)
            {
                var attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                var attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                offset += 4;

                if (offset + attrLen > response.Length) break;

                if (attrType == AttrXorMappedAddress || attrType == AttrMappedAddress)
                {
                    if (attrLen < 4) { offset += attrLen; continue; }
                    var family = response[offset + 1];
                    var port = (ushort)((response[offset + 2] << 8) | response[offset + 3]);

                    IPAddress ip;
                    if (family == 0x01 && attrLen >= 8) // IPv4
                    {
                        var addrBytes = new byte[4];
                        Array.Copy(response, offset + 4, addrBytes, 0, 4);

                        if (attrType == AttrXorMappedAddress)
                        {
                            port ^= (ushort)(StunMagicCookie >> 16);
                            for (int i = 0; i < 4; i++)
                                addrBytes[i] ^= (byte)((StunMagicCookie >> (24 - i * 8)) & 0xFF);
                        }

                        ip = new IPAddress(addrBytes);
                        return new IPEndPoint(ip, port);
                    }
                }
                offset += attrLen;
            }
            return null;
        }

        // ─── UDP Helpers ──────────────────────────────────────────────────

        private void StartUdp(int port)
        {
            _running = true;
            _udp = new UdpClient(port) { EnableBroadcast = true };
            _udp.Client.SendTimeout = 1000;
            _udp.Client.ReceiveTimeout = 1000;
            LocalEndpoint = $"STUN({_udp.Client.LocalEndPoint})";
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            _receiveThread.Start();
        }

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref from);

                    // STUN binding response (host- or client-side discovery in flight): hand the
                    // parsed public endpoint to the waiting DiscoverPublicEndpoint thread. ParseStunResponse
                    // self-validates (BindingResponse type + magic cookie), so this can never swallow a
                    // game packet; we only divert while a discovery is actually pending.
                    if (_stunDiscoveryPending)
                    {
                        var mapped = ParseStunResponse(data);
                        if (mapped != null)
                        {
                            _stunResult = mapped;
                            _stunSignal.Set();
                            continue;
                        }
                    }

                    var dataStr = Encoding.UTF8.GetString(data);

                    if (dataStr == "HOLE_PUNCH")
                    {
                        if (IsHost)
                        {
                            var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                            lock (_lock) { _peers[peerId] = from; }
                            OnPeerConnected?.Invoke(peerId, $"STUN({from})");
                            SendRaw(from, Encoding.UTF8.GetBytes("HOLE_PUNCH_ACK"));
                        }
                        continue;
                    }
                    if (dataStr == "HOLE_PUNCH_ACK")
                    {
                        continue;
                    }

                    ulong foundPeerId = 0;
                    lock (_lock)
                    {
                        foreach (var kvp in _peers)
                        {
                            if (kvp.Value.Address.Equals(from.Address)
                                && kvp.Value.Port == from.Port)
                            {
                                foundPeerId = kvp.Key;
                                break;
                            }
                        }
                    }

                    if (foundPeerId != 0)
                    {
                        lock (_lock) { _incomingQueue.Enqueue((foundPeerId, data)); }
                    }
                }
                catch { }
            }
        }

        private void SendRaw(IPEndPoint target, byte[] data)
        {
            try { _udp.Send(data, data.Length, target); } catch { }
        }
    }
}
