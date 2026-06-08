using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Multipleer.Transport
{
    public class StunTransport : ITransport
    {
        public TransportType TransportType => TransportType.StunUDP;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint { get; private set; } = "";

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
            _udp?.Close();
            _receiveThread?.Join(1000);
            lock (_lock) { _peers.Clear(); }
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = 14242)
        {
            IsHost = true;
            _listenPort = port;
            StartUdp(port);
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);
        }

        public void Connect(string address, int port)
        {
            IsHost = false;
            State = ConnectionState.Connecting;
            OnStateChanged?.Invoke(State);

            StartUdp(0);

            // Discover public endpoint via STUN
            _publicEndPoint = DiscoverPublicEndpoint();
            LocalEndpoint = _publicEndPoint != null
                ? $"STUN({_publicEndPoint})"
                : "STUN(discovery failed)";

            // Address format: "ip:port"
            if (address.Contains(":"))
            {
                var parts = address.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var remotePort)
                    && IPAddress.TryParse(parts[0], out var ip))
                {
                    var remoteEp = new IPEndPoint(ip, remotePort);
                    var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                    lock (_lock) { _peers[peerId] = remoteEp; }

                    // Hole punching: send dummy packets from different ports
                    SendRaw(remoteEp, Encoding.UTF8.GetBytes("HOLE_PUNCH"));

                    State = ConnectionState.Connected;
                    OnStateChanged?.Invoke(State);
                    OnPeerConnected?.Invoke(peerId, $"STUN({remoteEp})");
                }
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

        private IPEndPoint DiscoverPublicEndpoint()
        {
            foreach (var stunServer in StunServers)
            {
                try
                {
                    var parts = stunServer.Split(':');
                    var stunHost = parts[0];
                    var stunPort = int.Parse(parts[1]);
                    var stunEp = new IPEndPoint(
                        Dns.GetHostAddresses(stunHost)[0], stunPort);

                    var request = CreateStunBindingRequest();
                    _udp.Send(request, request.Length, stunEp);

                    var from = new IPEndPoint(IPAddress.Any, 0);
                    for (int retry = 0; retry < 3; retry++)
                    {
                        if (_udp.Client != null && _udp.Available > 0)
                        {
                            var response = _udp.Receive(ref from);
                            var mapped = ParseStunResponse(response);
                            if (mapped != null)
                                return mapped;
                        }
                        Thread.Sleep(100);
                    }
                }
                catch { continue; }
            }
            return null;
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
