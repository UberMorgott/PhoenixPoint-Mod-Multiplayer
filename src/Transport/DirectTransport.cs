using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Multipleer.Transport
{
    public class DirectTransport : ITransport
    {
        public TransportType TransportType => TransportType.DirectIP;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public bool IsHost { get; private set; }
        public string LocalEndpoint { get; private set; } = "";
        public System.Net.IPEndPoint PublicEndPoint => null;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        private TcpListener _listener;
        private readonly Dictionary<ulong, TcpClient> _clients = new Dictionary<ulong, TcpClient>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();
        private readonly object _lock = new object();
        private long _nextPeerId = 1;
        private ulong _hostPeerId;
        private Thread _listenThread;
        private volatile bool _running;

        // ─── Non-blocking client connect (anti-freeze) ─────────────────────
        // TcpClient.Connect is a BLOCKING call: on an unreachable host (e.g. a valid LAN IP with
        // nothing listening) the OS retransmits the SYN for ~20s+ before throwing. Running that on
        // the Unity main thread (Connect is invoked from the lobby's OnLobbyJoin callback) freezes
        // the whole game ("thought for a long time") and the watchdog/forced-kill that follows reads
        // as a crash. We therefore run the connect on a background worker with a hard timeout and
        // surface the OUTCOME from Update() — which runs on the main thread — so every Unity-facing
        // callback (OnStateChanged → MessageBox) stays on the main thread, mirroring how incoming
        // packets are already drained in Update().
        private const int ConnectTimeoutMs = 10000;
        private Thread _connectThread;
        private volatile bool _connectAborted;
        // Pending connect outcome, surfaced on the main thread in Update(). Guarded by _lock.
        private bool _pendingConnectResult;          // a result is waiting to be surfaced
        private bool _pendingConnectSucceeded;       // true = connected, false = failed/timed out
        private TcpClient _pendingClient;             // the connected client (success only)
        private ulong _pendingPeerId;                 // minted peer id (success only)
        private string _pendingEndpoint;              // endpoint label (success only)

        public void Initialize()
        {
            LocalEndpoint = "DirectIP(initializing)";
        }

        public void Shutdown()
        {
            _running = false;
            // Abort any in-flight client connect: the worker checks this flag after the connect
            // wait and disposes its socket instead of queueing a (now-stale) outcome.
            _connectAborted = true;
            lock (_lock)
            {
                foreach (var kvp in _clients)
                {
                    try { kvp.Value.Close(); } catch { }
                }
                _clients.Clear();
                // Drop any connect outcome that hasn't been surfaced yet, and dispose a socket that
                // succeeded between the last Update and this Shutdown so it doesn't leak.
                if (_pendingConnectResult && _pendingClient != null)
                {
                    try { _pendingClient.Close(); } catch { }
                }
                _pendingConnectResult = false;
                _pendingClient = null;
            }
            _listener?.Stop();
            _listenThread?.Join(1000);
            _connectThread?.Join(1000);
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Host(int port = 14242)
        {
            IsHost = true;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            LocalEndpoint = $"DirectIP(host:{port})";
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);

            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();
        }

        public void Connect(string address, int port)
        {
            IsHost = false;
            _running = true;            // permit the per-peer read loop to run once connected
            _connectAborted = false;
            State = ConnectionState.Connecting;
            OnStateChanged?.Invoke(State);   // main thread (called from OnLobbyJoin) — safe

            // Run the blocking socket connect off the main thread with a hard timeout. The outcome
            // is surfaced from Update() on the main thread (see SurfacePendingConnect), so the UI
            // never freezes and the failure MessageBox is raised on the main thread.
            _connectThread = new Thread(() => ConnectWorker(address, port)) { IsBackground = true };
            _connectThread.Start();
        }

        // Background worker: time-bounded TCP connect. Never raises Unity-facing events directly —
        // it only records the result for Update() to surface on the main thread.
        private void ConnectWorker(string address, int port)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                // APM BeginConnect + bounded wait = connect with timeout. On an unreachable host the
                // wait returns false at ConnectTimeoutMs and we abort instead of blocking ~20s+.
                var ar = client.BeginConnect(address, port, null, null);
                var done = ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs);
                if (!done || _connectAborted)
                {
                    try { client.Close(); } catch { }
                    if (!_connectAborted) QueueConnectResult(false, 0, null, null);
                    return;
                }
                // Completes the connect; throws SocketException if it actually failed
                // (connection refused / host unreachable / reset).
                client.EndConnect(ar);

                var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                var endpoint = $"Host({address}:{port})";
                QueueConnectResult(true, peerId, client, endpoint);
            }
            catch
            {
                try { client?.Close(); } catch { }
                if (!_connectAborted) QueueConnectResult(false, 0, null, null);
            }
        }

        private void QueueConnectResult(bool succeeded, ulong peerId, TcpClient client, string endpoint)
        {
            lock (_lock)
            {
                _pendingConnectResult = true;
                _pendingConnectSucceeded = succeeded;
                _pendingClient = client;
                _pendingPeerId = peerId;
                _pendingEndpoint = endpoint;
            }
        }

        // Surface a completed connect attempt on the MAIN thread (called from Update). Raising the
        // state change here — not from ConnectWorker — keeps OnStateChanged/OnPeerConnected (and the
        // MessageBox they ultimately drive) on Unity's main thread.
        private void SurfacePendingConnect()
        {
            bool succeeded;
            TcpClient client;
            ulong peerId;
            string endpoint;
            lock (_lock)
            {
                if (!_pendingConnectResult) return;
                _pendingConnectResult = false;
                succeeded = _pendingConnectSucceeded;
                client = _pendingClient;
                peerId = _pendingPeerId;
                endpoint = _pendingEndpoint;
                _pendingClient = null;
            }

            if (succeeded && client != null)
            {
                _hostPeerId = peerId;
                LocalEndpoint = $"DirectIP(client:{client.Client.LocalEndPoint})";
                lock (_lock) { _clients[peerId] = client; }
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(peerId, endpoint);
                StartReadLoop(peerId, client);
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
            lock (_lock)
            {
                foreach (var kvp in _clients)
                {
                    try { kvp.Value.Close(); } catch { }
                    OnPeerDisconnected?.Invoke(kvp.Key,
                        kvp.Value.Client.RemoteEndPoint?.ToString() ?? "unknown");
                }
                _clients.Clear();
            }
            _listener?.Stop();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            lock (_lock)
            {
                if (_clients.TryGetValue(peerId, out var client))
                {
                    try
                    {
                        var stream = client.GetStream();
                        var lenBytes = BitConverter.GetBytes(data.Length);
                        stream.Write(lenBytes, 0, 4);
                        stream.Write(data, 0, data.Length);
                    }
                    catch { }
                }
            }
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            lock (_lock)
            {
                foreach (var kvp in _clients)
                {
                    try
                    {
                        var stream = kvp.Value.GetStream();
                        var lenBytes = BitConverter.GetBytes(data.Length);
                        stream.Write(lenBytes, 0, 4);
                        stream.Write(data, 0, data.Length);
                    }
                    catch { }
                }
            }
        }

        public void Update()
        {
            // Surface a completed client-connect on the main thread first (success → Connected +
            // peer; failure/timeout → Failed), then drain received packets.
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

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                    lock (_lock) { _clients[peerId] = client; }
                    OnPeerConnected?.Invoke(peerId,
                        client.Client.RemoteEndPoint?.ToString() ?? "unknown");
                    StartReadLoop(peerId, client);
                }
                catch { break; }
            }
        }

        private void StartReadLoop(ulong peerId, TcpClient client)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var stream = client.GetStream();
                    var lenBuf = new byte[4];
                    while (_running && client.Connected)
                    {
                        var read = 0;
                        while (read < 4)
                        {
                            var n = stream.Read(lenBuf, read, 4 - read);
                            if (n <= 0) throw new EndOfStreamException();
                            read += n;
                        }
                        var msgLen = BitConverter.ToInt32(lenBuf, 0);
                        var msgBuf = new byte[msgLen];
                        read = 0;
                        while (read < msgLen)
                        {
                            var n = stream.Read(msgBuf, read, msgLen - read);
                            if (n <= 0) throw new EndOfStreamException();
                            read += n;
                        }
                        lock (_lock) { _incomingQueue.Enqueue((peerId, msgBuf)); }
                    }
                }
                catch
                {
                    lock (_lock) { _clients.Remove(peerId); }
                    OnPeerDisconnected?.Invoke(peerId, "connection lost");
                }
            }) { IsBackground = true };
            thread.Start();
        }
    }
}
