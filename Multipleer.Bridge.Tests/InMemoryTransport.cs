using System;
using System.Collections.Generic;
using System.Net;
using Multipleer.Transport;

namespace Multipleer.Bridge.Tests
{
    /// <summary>
    /// One trace line per enqueue / deliver / peer-connect, collected globally and ordered by a
    /// shared monotonic counter so the whole cross-peer exchange can be dumped in arrival order.
    /// </summary>
    public sealed class TraceLog
    {
        private readonly List<string> _lines = new List<string>();
        private int _seq;

        public void Add(string line) => _lines.Add((++_seq).ToString("D3") + "  " + line);
        public IReadOnlyList<string> Lines => _lines;
        public override string ToString() => string.Join(Environment.NewLine, _lines);
    }

    /// <summary>
    /// In-process shared dispatcher. Every <see cref="InMemoryTransport"/> on the same cluster holds
    /// the SAME bus instance. The bus owns peer registration, deterministic peerId assignment
    /// (host=1, clients=2,3,…) and a per-recipient inbound delivery queue. Sends are ENQUEUED (never
    /// delivered synchronously) — delivery happens only when the recipient's transport calls Update(),
    /// which mirrors a real frame pump and avoids re-entrancy through the engine relay.
    /// </summary>
    public sealed class InMemoryBus
    {
        public const ulong HostPeerId = 1;

        private readonly Dictionary<ulong, InMemoryTransport> _peers = new Dictionary<ulong, InMemoryTransport>();
        private readonly Dictionary<ulong, Queue<(ulong sender, byte[] data)>> _inbound =
            new Dictionary<ulong, Queue<(ulong, byte[])>>();
        private ulong _nextClientId = 2;

        public TraceLog Trace { get; }
        public InMemoryBus(TraceLog trace) => Trace = trace;

        /// <summary>Register the host peer (deterministic id = 1).</summary>
        public ulong RegisterHost(InMemoryTransport t)
        {
            _peers[HostPeerId] = t;
            _inbound[HostPeerId] = new Queue<(ulong, byte[])>();
            Trace.Add($"BUS register HOST peer={HostPeerId}");
            return HostPeerId;
        }

        /// <summary>Register a client peer (deterministic ids 2,3,…) and wire both connect events.</summary>
        public ulong RegisterClient(InMemoryTransport t)
        {
            var id = _nextClientId++;
            _peers[id] = t;
            _inbound[id] = new Queue<(ulong, byte[])>();
            Trace.Add($"BUS register CLIENT peer={id}");

            // Tell the host a client joined (host roster wiring), and tell the client it reached the
            // host. The CLIENT-side OnPeerConnected is intentionally NOT raised by the bus: the engine's
            // client handler runs SystemInfo.deviceName (a native ECall that throws headless) on the
            // JOIN/nickname flow. The cluster wires the client roster (SetHostPeer) by hand instead.
            if (_peers.TryGetValue(HostPeerId, out var host))
                host.RaisePeerConnected(id, $"mem://client/{id}");

            return id;
        }

        public void Enqueue(ulong senderId, ulong targetId, byte[] data, string kind)
        {
            if (!_inbound.TryGetValue(targetId, out var q))
            {
                Trace.Add($"BUS DROP {kind} {senderId}->{targetId} (no such peer)");
                return;
            }
            q.Enqueue((senderId, data));
            Trace.Add($"ENQ  {kind} peer{senderId}->peer{targetId} ({data.Length}B)");
        }

        /// <summary>Every connected peer except the sender (used by Broadcast).</summary>
        public IEnumerable<ulong> OtherPeers(ulong self)
        {
            foreach (var id in _peers.Keys)
                if (id != self) yield return id;
        }

        /// <summary>Deliver one peer's pending inbound, raising OnPacketReceived for each. Returns count.</summary>
        public int DeliverInbound(ulong selfId)
        {
            if (!_inbound.TryGetValue(selfId, out var q)) return 0;
            int n = 0;
            while (q.Count > 0)
            {
                var (sender, data) = q.Dequeue();
                Trace.Add($"DELIV peer{sender}->peer{selfId} ({data.Length}B)");
                _peers[selfId].RaisePacketReceived(sender, data);
                n++;
            }
            return n;
        }

        /// <summary>Total queued packets across all peers — the pump loop drains until this is 0.</summary>
        public int PendingTotal()
        {
            int n = 0;
            foreach (var q in _inbound.Values) n += q.Count;
            return n;
        }
    }

    /// <summary>
    /// <see cref="ITransport"/> over a shared in-process <see cref="InMemoryBus"/>. No sockets, no
    /// game. Host/Connect register on the bus; Send/Broadcast enqueue; Update delivers this peer's
    /// pending inbound by raising OnPacketReceived. Single-threaded, deterministic.
    /// </summary>
    public sealed class InMemoryTransport : ITransport
    {
        private readonly InMemoryBus _bus;
        public ulong PeerId { get; private set; }

        public InMemoryTransport(InMemoryBus bus) => _bus = bus;

        public Multipleer.Transport.TransportType TransportType => Multipleer.Transport.TransportType.DirectIP;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint => $"mem://peer/{PeerId}";
        public IPEndPoint PublicEndPoint => null;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        public void Initialize() { /* nothing to init for the in-memory bus */ }

        public void Shutdown()
        {
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = 0)
        {
            IsHost = true;
            PeerId = _bus.RegisterHost(this);
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);
        }

        public void Connect(string address, int port)
        {
            IsHost = false;
            PeerId = _bus.RegisterClient(this);
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);
        }

        public void Disconnect()
        {
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
            => _bus.Enqueue(PeerId, peerId, data, "SEND");

        public void Broadcast(byte[] data, bool reliable = true)
        {
            foreach (var target in _bus.OtherPeers(PeerId))
                _bus.Enqueue(PeerId, target, data, "BCAST");
        }

        public void Update() => _bus.DeliverInbound(PeerId);

        // ─── Bus-driven event raisers (called by the bus, single-threaded) ─────
        public void RaisePacketReceived(ulong sender, byte[] data) => OnPacketReceived?.Invoke(sender, data);
        public void RaisePeerConnected(ulong peerId, string endpoint) => OnPeerConnected?.Invoke(peerId, endpoint);
        public void RaisePeerDisconnected(ulong peerId, string endpoint) => OnPeerDisconnected?.Invoke(peerId, endpoint);
    }
}
