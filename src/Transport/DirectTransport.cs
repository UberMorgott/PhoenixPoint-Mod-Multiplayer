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

        public void Initialize()
        {
            LocalEndpoint = "DirectIP(initializing)";
        }

        public void Shutdown()
        {
            _running = false;
            lock (_lock)
            {
                foreach (var kvp in _clients)
                {
                    try { kvp.Value.Close(); } catch { }
                }
                _clients.Clear();
            }
            _listener?.Stop();
            _listenThread?.Join(1000);
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
            State = ConnectionState.Connecting;
            OnStateChanged?.Invoke(State);

            try
            {
                var client = new TcpClient();
                client.Connect(address, port);
                var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                _hostPeerId = peerId;
                LocalEndpoint = $"DirectIP(client:{client.Client.LocalEndPoint})";
                lock (_lock) { _clients[peerId] = client; }
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(peerId, $"Host({address}:{port})");
                StartReadLoop(peerId, client);
            }
            catch
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
