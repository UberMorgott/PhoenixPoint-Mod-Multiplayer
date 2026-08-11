using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Multiplayer.Transport
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

        // ─── Per-peer connection: socket + bounded send queue + ONE writer thread ─────────
        // Send/Broadcast only ENQUEUE — the main thread never blocks on a peer's TCP window (a
        // non-reading peer used to freeze the host frame loop during save fan-out). The single
        // writer per peer preserves per-peer frame ORDER (save chunks + deltas + intents share
        // the stream and rely on it). Lock discipline: the Peer object itself guards its queue
        // (Monitor.Wait/Pulse); _lock guards the registry — never nested one inside the other.
        private class Peer
        {
            public readonly ulong Id;
            public readonly TcpClient Client;
            public readonly Queue<byte[]> Frames = new Queue<byte[]>();
            public long QueuedBytes;
            public Thread Writer;
            public bool CloseRequested; // no new frames; writer drains the queue, then closes (flush-then-close)
            public bool Dead;           // hard drop: writer exits without draining
            public Peer(ulong id, TcpClient client) { Id = id; Client = client; }
        }

        // Send-queue ceiling per peer. Must exceed the save-transfer blob ceiling (64 MB —
        // SaveTransferCoordinator.MaxTransferBytes, not referenced: this file stays game-free) so a
        // whole save fan-out to a slow peer fits. Overflow = the peer stopped reading long ago ⇒ drop.
        private const long MaxQueuedBytesPerPeer = 96L * 1024 * 1024;
        // A single blocking Write may not stall longer than this. A HEALTHY peer's background read
        // loop drains the socket even while its main thread sits in a native load, so a full send
        // buffer for this long means the peer process is dead/frozen ⇒ IOException ⇒ peer drop.
        private const int SendStallTimeoutMs = 30000;
        // Flush-then-close grace on teardown: how long Shutdown waits for writers to drain a
        // just-queued terminal notice (HostDisconnected / ClientLeave) before force-closing sockets.
        private const int FlushCloseGraceMs = 1000;
        // Upper bound on a framed message length read off the wire. The length prefix is
        // attacker-controlled on an internet-facing listen port (scanners send garbage), and an
        // unchecked value allocated up to 2 GB per connection. Mirrors the save-transfer ceiling.
        private const int MaxFrameBytes = 64 * 1024 * 1024;

        private TcpListener _listener;
        private readonly Dictionary<ulong, Peer> _clients = new Dictionary<ulong, Peer>();
        private readonly Queue<(ulong, byte[])> _incomingQueue = new Queue<(ulong, byte[])>();
        // Peer connect/disconnect raised on background socket threads (TCP accept thread / per-peer
        // read thread) are marshalled here and surfaced on the main thread in Update() — mirroring the
        // _incomingQueue packet drain — because their downstream handlers (NetworkEngine.OnPeerConnected
        // → TimeSync.OnPeerConnectedHost / OnPeerDisconnected → TimeSync.ResetClientState) touch Unity
        // APIs and shared clock state that must be touched on the main thread only.
        private readonly Queue<(bool connected, ulong peerId, string endpoint)> _peerEventQueue
            = new Queue<(bool, ulong, string)>();
        private readonly object _lock = new object();
        private long _nextPeerId = 1;
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
        private string _lastConnectError;             // precise connect-fail reason, surfaced via LocalEndpoint
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
            List<Peer> peers;
            lock (_lock)
            {
                peers = new List<Peer>(_clients.Values);
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
            // Flush-then-close: let each writer drain a just-queued terminal notice (HostDisconnected)
            // before its socket dies, bounded by ONE shared grace budget; then force-close — Close
            // aborts a stalled Write/Read, so no thread outlives the grace by more than its current call.
            foreach (var peer in peers) RequestFlushClose(peer);
            var deadline = Environment.TickCount + FlushCloseGraceMs;
            foreach (var peer in peers)
            {
                var left = deadline - Environment.TickCount;
                if (left > 0) peer.Writer?.Join(left);
                try { peer.Client.Close(); } catch { }
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
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                // Bind failed (port already in use / address not available). Surface it: set State to
                // Failed and label the endpoint so the host learns this LAN path is down instead of
                // silently believing it is listening. We do NOT re-throw — StartHost may call this on
                // a bare DirectTransport (not only inside a Composite), so an escaping exception would
                // abort the whole host bring-up; the queryable Failed state is the signal instead, and
                // CompositeTransport.Host inspects each child's State after hosting.
                _running = false;
                _listener = null;
                LocalEndpoint = $"DirectIP(bind failed: {ex.SocketErrorCode})";
                State = ConnectionState.Failed;
                LogError($"[Multiplayer] DirectTransport host bind failed on port {port}: " +
                         $"{ex.Message} (SocketErrorCode={ex.SocketErrorCode})");
                OnStateChanged?.Invoke(State);
                return;
            }
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
                client = new TcpClient { NoDelay = true }; // Nagle off: small intent/delta frames must not wait on ACKs
                // APM BeginConnect + bounded wait = connect with timeout. On an unreachable host the
                // wait returns false at ConnectTimeoutMs and we abort instead of blocking ~20s+.
                var ar = client.BeginConnect(address, port, null, null);
                var done = ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs);
                if (!done || _connectAborted)
                {
                    try { client.Close(); } catch { }
                    if (!_connectAborted)
                    {
                        _lastConnectError = $"connect timed out after {ConnectTimeoutMs / 1000}s "
                            + $"(host {address}:{port} unreachable — TCP {port} not forwarded / firewall)";
                        QueueConnectResult(false, 0, null, null);
                    }
                    return;
                }
                // Completes the connect; throws SocketException if it actually failed
                // (connection refused / host unreachable / reset).
                client.EndConnect(ar);

                var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                var endpoint = $"Host({address}:{port})";
                QueueConnectResult(true, peerId, client, endpoint);
            }
            catch (Exception ex)
            {
                // Diagnostic: surface the real reason the DirectIP connect failed instead of
                // swallowing it silently. For SocketException include the SocketErrorCode.
                var sockEx = ex as SocketException;
                _lastConnectError = sockEx != null
                    ? $"{sockEx.SocketErrorCode} connecting to {address}:{port}"
                    : $"{ex.GetType().Name}: {ex.Message}";
                LogError($"[Multiplayer] DirectTransport connect failed: {_lastConnectError}");
                try { client?.Close(); } catch { }
                if (!_connectAborted) QueueConnectResult(false, 0, null, null);
            }
        }

        // RailCheck EXECUTES this type — L120 constructs a real DirectTransport and drives its pump
        // through a departure — and RailCheck is a plain console host with no Unity player runtime.
        // A compile-time-bound UnityEngine.Debug.LogError dies there, and dies OUTSIDE any try/catch
        // this method could hold, because the failure is at JIT/assembly-resolve time and surfaces at
        // the CALLER: measured 2026-08-11 in a net472 console host, FileNotFoundException on
        // UnityEngine.CoreModule when the dll is not beside the exe, and SecurityException ("ECall
        // methods must be packaged into a system module") when it is. Reaching Debug reflectively keeps
        // that inside the catch below, so the law drives the real transport and the log is simply
        // skipped. In-game the call resolves and lands in the Player.log. Cached after first use.
        private static System.Reflection.MethodInfo _unityLogError;
        private static bool _unityLogErrorResolved;
        private static void LogError(string message)
        {
            try
            {
                if (!_unityLogErrorResolved)
                {
                    _unityLogErrorResolved = true;
                    var debugType = Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule")
                                    ?? Type.GetType("UnityEngine.Debug, UnityEngine");
                    if (debugType != null)
                        _unityLogError = debugType.GetMethod("LogError", new[] { typeof(object) });
                }
                if (_unityLogError != null)
                    _unityLogError.Invoke(null, new object[] { message });
                else
                    Console.WriteLine(message); // fallback: a host with no UnityEngine loaded (RailCheck)
            }
            catch { /* logging must never break the connect path */ }
        }

        private void QueueConnectResult(bool succeeded, ulong peerId, TcpClient client, string endpoint)
        {
            lock (_lock)
            {
                // Shutdown sets _connectAborted before it takes _lock, so a worker that got here after the
                // teardown finished sees it and closes its own socket. Checked in-lock, not at the EndConnect
                // call site: an out-of-lock check only narrows the window that leaks a live connected TcpClient
                // (and leaves the remote host holding a phantom peer until its heartbeat expires). It also
                // latched _pendingConnectResult, which nothing clears, so a later Update() surfaced
                // OnStateChanged/OnPeerConnected on a transport that was already torn down.
                if (_connectAborted) { try { client?.Close(); } catch { } return; }
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
                LocalEndpoint = $"DirectIP(client:{client.Client.LocalEndPoint})";
                var peer = new Peer(peerId, client);
                lock (_lock) { _clients[peerId] = peer; }
                State = ConnectionState.Connected;
                OnStateChanged?.Invoke(State);
                OnPeerConnected?.Invoke(peerId, endpoint);
                StartPeerThreads(peer);
            }
            else
            {
                // Surface the precise reason (SocketErrorCode / timeout) via LocalEndpoint so the UI
                // failure dialog can show WHY, not a generic "connection failed". Mirrors the existing
                // "DirectIP(bind failed: …)" label the Host path already uses.
                LocalEndpoint = $"DirectIP(failed: {_lastConnectError ?? "unknown"})";
                State = ConnectionState.Failed;
                OnStateChanged?.Invoke(State);
            }
        }

        public void Disconnect()
        {
            _running = false;
            // Same abort Shutdown raises: a Disconnect that is NOT followed by Shutdown otherwise leaves an
            // in-flight connect running, and QueueConnectResult would latch a live socket onto a torn-down
            // transport. Connect (:173) clears the flag again, so a later reconnect on this instance is fine.
            _connectAborted = true;
            // Collect (peerId, label) BEFORE the sockets go down, then raise OnPeerDisconnected
            // OUTSIDE the lock (a handler that calls back into Send/Broadcast must not deadlock).
            // Sockets close via flush-then-close: the writer drains a just-queued ClientLeave notice
            // first, then closes — the peer actually receives the graceful leave.
            var dropped = new List<(ulong peerId, string label)>();
            List<Peer> peers;
            lock (_lock)
            {
                peers = new List<Peer>(_clients.Values);
                _clients.Clear();
                // The other half of the abort above, mirroring Shutdown: the flag only stops a result queued
                // AFTER it: one already latched here would be surfaced by the next Update — SurfacePendingConnect
                // is called unconditionally — raising Connected/OnPeerConnected on a transport just disconnected.
                if (_pendingConnectResult && _pendingClient != null)
                {
                    try { _pendingClient.Close(); } catch { }
                }
                _pendingConnectResult = false;
                _pendingClient = null;
            }
            foreach (var peer in peers)
            {
                string label;
                try { label = peer.Client.Client.RemoteEndPoint?.ToString() ?? "unknown"; }
                catch { label = "unknown"; }   // socket already torn down → never throw here
                dropped.Add((peer.Id, label));
                RequestFlushClose(peer);
            }
            foreach (var (peerId, label) in dropped)
                OnPeerDisconnected?.Invoke(peerId, label);
            _listener?.Stop();
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }

        // Per-peer kick (heartbeat timeout / graceful leave / reject): forget the peer so Broadcast
        // stops queueing to the dead id, queue the disconnect event (drained in Update like every
        // other peer event), and flush-then-close — a terminal notice queued just before the kick
        // (ConnectionRejected) still reaches the peer before the writer closes the socket, which in
        // turn aborts the read loop. The read loop's own DropPeer is deduped (peer already gone).
        public bool DisconnectPeer(ulong peerId)
        {
            Peer peer;
            lock (_lock)
            {
                if (!_clients.TryGetValue(peerId, out peer)) return false;
                _clients.Remove(peerId);
                string label;
                try { label = peer.Client.Client.RemoteEndPoint?.ToString() ?? "unknown"; }
                catch { label = "unknown"; }
                _peerEventQueue.Enqueue((false, peerId, label));
            }
            RequestFlushClose(peer);
            return true;
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            Peer peer;
            lock (_lock) { _clients.TryGetValue(peerId, out peer); }
            if (peer != null) EnqueueFrame(peer, data);
        }

        // Same set Broadcast fans out to (_clients), so it answers the real reach.
        public bool CanReach(ulong peerId)
        {
            lock (_lock) { return _clients.ContainsKey(peerId); }
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            List<Peer> peers;
            lock (_lock) { peers = new List<Peer>(_clients.Values); }
            // Build the framed array ONCE and share it across every peer's queue (see EnqueueFrame).
            var frame = new byte[4 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            foreach (var peer in peers) EnqueueBuiltFrame(peer, frame);
        }

        // Frame = 4-byte little-endian length prefix + payload, queued for the peer's writer thread.
        // Never blocks the caller.
        //
        // BACKPRESSURE DEGRADES THE PEER, IT NEVER REMOVES IT (N=50 mandate). A full queue used to mean
        // DropPeer — a player on a bad link was thrown out of the session for being on a bad link, which
        // is precisely the fight-to-be-able-to-play the mandate forbids. The honest bound instead: keep
        // the newest frame and discard the OLDEST queued ones to make room. What is oldest is what is
        // most stale, and the rail is built for exactly this — Apply is idempotent, delivery is seq'd,
        // and a gap drives GenericApplier.RequestResync, so a peer that loses stale deltas is re-stated
        // rather than lost. Ceiling, stated plainly: the queue does not know a stale delta from a save
        // chunk, so a peer far enough behind to overflow can lose a chunk of an in-flight save transfer;
        // that transfer fails its length/crc check and the peer is re-onboarded through the ordinary
        // join path. It stays IN the session either way.
        private void EnqueueFrame(Peer peer, byte[] data)
        {
            // ONE length prefix, ONE array — this used to allocate a fresh byte[4+len] per PEER, which at
            // a 45 KB rail packet and 50 players is 2.25 MB of garbage per packet. The frame is read-only
            // from here on (the writer thread only ever hands it to Stream.Write), so every peer's queue
            // can hold the same instance.
            var frame = new byte[4 + data.Length];
            var lenBytes = BitConverter.GetBytes(data.Length);
            Buffer.BlockCopy(lenBytes, 0, frame, 0, 4);
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            EnqueueBuiltFrame(peer, frame);
        }

        // The shared-frame entry point (see Broadcast): the caller has already prefixed the length.
        private void EnqueueBuiltFrame(Peer peer, byte[] frame)
        {
            int dropped = 0; long droppedBytes = 0;
            lock (peer)
            {
                if (peer.Dead || peer.CloseRequested) return;
                while (peer.QueuedBytes + frame.Length > MaxQueuedBytesPerPeer && peer.Frames.Count > 0)
                {
                    var stale = peer.Frames.Dequeue();
                    peer.QueuedBytes -= stale.Length;
                    dropped++; droppedBytes += stale.Length;
                }
                peer.Frames.Enqueue(frame);
                peer.QueuedBytes += frame.Length;
                Monitor.Pulse(peer);
            }
            if (dropped > 0)
                LogError($"[Multiplayer] DirectTransport: send queue to peer {peer.Id} is full " +
                         $"({MaxQueuedBytesPerPeer} bytes) — discarded {dropped} stale frame(s) " +
                         $"({droppedBytes} bytes) to keep it. The peer stays in the session and reconverges " +
                         "through the rail's resync.");
        }

        // Graceful close: no new frames accepted; the writer drains what is queued, then closes the
        // socket and exits. Used by every teardown path that may have just queued a terminal notice.
        private static void RequestFlushClose(Peer peer)
        {
            lock (peer)
            {
                peer.CloseRequested = true;
                Monitor.Pulse(peer);
            }
        }

        // Hard-drop chokepoint (read failure, write failure/stall, queue overflow): kill the peer's
        // queue + threads, close the socket (aborts a blocked Read/Write on the other loop), and
        // surface the drop — but ONLY if the peer was still registered. A DisconnectPeer kick or a
        // teardown already removed it AND queued its own event, so this never double-fires.
        private void DropPeer(Peer peer, string reason)
        {
            lock (peer)
            {
                peer.Dead = true;
                peer.CloseRequested = true;
                peer.Frames.Clear();
                peer.QueuedBytes = 0;
                Monitor.Pulse(peer);
            }
            try { peer.Client.Close(); } catch { }
            lock (_lock)
            {
                if (_clients.Remove(peer.Id))
                    _peerEventQueue.Enqueue((false, peer.Id, reason));
            }
        }

        public void Update()
        {
            // Surface a completed client-connect on the main thread first (success → Connected +
            // peer; failure/timeout → Failed), then CONNECTS, then received packets, then the
            // DISCONNECTS — all on the main thread.
            //
            // WHY THE DROP IS ANNOUNCED LAST. Connects must still precede packets (a peer's first
            // frame cannot arrive before the peer exists — the composite maps ids on that edge). A
            // DROP is the opposite case: by the time the read loop notices the socket is gone it has
            // ALREADY read and queued everything that peer ever sent, including its own ClientLeave.
            // Announcing the death before delivering those last words made a deliberate quit — the
            // window X and Alt+F4, the two commonest ways anyone leaves — indistinguishable from a
            // crash: the host paused the peer on the drop and told everyone "X lost connection",
            // then read the farewell that said X had chosen to go. Draining packets first lets the
            // voluntary path win the race it should win, so the notice reads "X left the game".
            // Ordering only — no wait, no grace window, no "hold on in case a farewell shows up":
            // an involuntary drop has nothing queued behind it and is announced in this same Update.
            SurfacePendingConnect();

            // Drain peer events under the lock, then raise them WITHOUT the lock so a handler that
            // calls back into Send/Broadcast can't deadlock and shared transport state isn't held
            // while arbitrary handler code runs.
            List<(ulong peerId, string endpoint)> drops = null;
            while (true)
            {
                bool connected;
                ulong peerId;
                string endpoint;
                lock (_lock)
                {
                    if (_peerEventQueue.Count == 0) break;
                    (connected, peerId, endpoint) = _peerEventQueue.Dequeue();
                }
                if (connected) OnPeerConnected?.Invoke(peerId, endpoint);
                else
                {
                    if (drops == null) drops = new List<(ulong, string)>();
                    drops.Add((peerId, endpoint));
                }
            }

            // Same pattern for packets: dequeue under the lock, dispatch outside it — handler code
            // (message routing, session logic) must never run while holding transport state.
            List<(ulong, byte[])> packets = null;
            lock (_lock)
            {
                if (_incomingQueue.Count > 0)
                {
                    packets = new List<(ulong, byte[])>(_incomingQueue.Count);
                    while (_incomingQueue.Count > 0) packets.Add(_incomingQueue.Dequeue());
                }
            }
            if (packets != null)
                foreach (var (peerId, data) in packets)
                    OnPacketReceived?.Invoke(peerId, data);

            // The peer's last words have been delivered; now report that it is gone. Still this same
            // Update — a drop is never held over to a later frame.
            if (drops != null)
                foreach (var (peerId, endpoint) in drops)
                    OnPeerDisconnected?.Invoke(peerId, endpoint);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    client.NoDelay = true; // Nagle off: small intent/delta frames must not wait on ACKs
                    var peerId = (ulong)Interlocked.Increment(ref _nextPeerId);
                    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    // Marshal the connect onto the main thread (drained in Update before packets) so the
                    // Unity-touching OnPeerConnected handler never runs on this accept thread. The read
                    // loop only starts here, so no packet can be enqueued before this connect event.
                    var peer = new Peer(peerId, client);
                    lock (_lock)
                    {
                        // Accept completed while Shutdown is tearing down (it flips _running BEFORE
                        // clearing the registry under this same lock): registering now would leak a
                        // peer nobody flush-closes — its writer would park forever. Close + bail.
                        if (!_running)
                        {
                            try { client.Close(); } catch { }
                            break;
                        }
                        _clients[peerId] = peer;
                        _peerEventQueue.Enqueue((true, peerId, endpoint));
                    }
                    StartPeerThreads(peer);
                }
                catch { break; }
            }
        }

        private void StartPeerThreads(Peer peer)
        {
            peer.Writer = new Thread(() => WriteLoop(peer)) { IsBackground = true };
            peer.Writer.Start();
            new Thread(() => ReadLoop(peer)) { IsBackground = true }.Start();
        }

        // ONE writer per peer: drains the bounded queue in FIFO order (per-peer send order is part
        // of the wire contract). Exits on flush-close (drain, then close), hard drop, or a write
        // failure/stall — and ALWAYS closes the socket on the way out, which also unblocks the
        // peer's read loop, so no thread leaks across sessions.
        private void WriteLoop(Peer peer)
        {
            bool failed = false;
            try
            {
                var stream = peer.Client.GetStream();
                stream.WriteTimeout = SendStallTimeoutMs;
                while (true)
                {
                    byte[] frame = null;
                    lock (peer)
                    {
                        while (peer.Frames.Count == 0 && !peer.CloseRequested && !peer.Dead)
                            Monitor.Wait(peer);
                        if (peer.Dead) break;
                        if (peer.Frames.Count > 0)
                        {
                            frame = peer.Frames.Dequeue();
                            peer.QueuedBytes -= frame.Length;
                        }
                        else break; // CloseRequested + queue drained → flushed
                    }
                    stream.Write(frame, 0, frame.Length);
                }
            }
            catch { failed = true; } // write error, or the SendStallTimeoutMs stall bound fired
            try { peer.Client.Close(); } catch { }
            if (failed) DropPeer(peer, "connection lost (send failed/stalled)");
        }

        private void ReadLoop(Peer peer)
        {
            try
            {
                var stream = peer.Client.GetStream();
                var lenBuf = new byte[4];
                while (_running && peer.Client.Connected)
                {
                    var read = 0;
                    while (read < 4)
                    {
                        var n = stream.Read(lenBuf, read, 4 - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }
                    var msgLen = BitConverter.ToInt32(lenBuf, 0);
                    if (msgLen <= 0 || msgLen > MaxFrameBytes)
                    {
                        // Attacker-controlled length (internet-facing port): never size an alloc
                        // from it unchecked. Oversize/nonpositive ⇒ log + drop the connection.
                        LogError($"[Multiplayer] DirectTransport: dropping peer {peer.Id} — framed " +
                                 $"length {msgLen} out of bounds (0, {MaxFrameBytes}].");
                        throw new InvalidDataException("framed length out of bounds");
                    }
                    var msgBuf = new byte[msgLen];
                    read = 0;
                    while (read < msgLen)
                    {
                        var n = stream.Read(msgBuf, read, msgLen - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }
                    lock (_lock) { _incomingQueue.Enqueue((peer.Id, msgBuf)); }
                }
            }
            catch { }
            // Any exit = this peer's link is done (socket error, malformed frame, remote close, kick,
            // teardown). DropPeer marshals the disconnect onto the main thread — event only if the
            // peer was still registered (a kick/teardown already queued its own) — and wakes/kills
            // the writer so it exits too, never leaking a parked thread.
            DropPeer(peer, "connection lost");
        }
    }
}
