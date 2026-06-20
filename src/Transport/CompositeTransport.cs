using System;
using System.Collections.Generic;
using System.Net;

namespace Multipleer.Transport
{
    /// <summary>
    /// Host-side multiplexer that fans ONE logical session across several child
    /// transports simultaneously (e.g. DirectIP + STUN + Steam). Clients are
    /// unchanged — each reaches the host over a single child transport; the
    /// composite hides the multiplexing behind <see cref="ITransport"/> so
    /// SessionManager / serializers keep keying peers by a plain <c>ulong</c>.
    ///
    /// PEER-ID NAMESPACING (load-bearing):
    /// Children emit their own raw peerIds with overlapping ranges — DirectTransport
    /// and StunTransport both start at 1, while SteamTransport uses real 64-bit
    /// SteamID64 values (&gt; 2^56). A bit-pack scheme such as
    /// <c>(childIndex &lt;&lt; 56) | (rawId &amp; 0x00FFFFFFFFFFFFFF)</c> would TRUNCATE the
    /// top byte of a SteamID64, so Send() could never translate the outward id back
    /// to the exact 64-bit SteamId. We therefore use a pure DICTIONARY allocator: a
    /// single monotonic counter mints a synthetic outward id per (child, rawId) pair
    /// and stores the exact raw id for reverse translation. This guarantees global
    /// uniqueness for ANY id size and a lossless round-trip for Steam ids.
    ///
    /// Thread-safety: DirectTransport/StunTransport raise OnPeerConnected /
    /// OnPeerDisconnected from background socket threads while Send runs on the main
    /// (Update) thread, so every map access is guarded by <c>_lock</c>.
    /// </summary>
    public sealed class CompositeTransport : ITransport
    {
        private readonly List<ITransport> _children;
        private readonly object _lock = new object();

        // outwardId -> child that owns the peer
        private readonly Dictionary<ulong, ITransport> _peerToChild = new Dictionary<ulong, ITransport>();
        // outwardId -> child's exact raw peer id (lossless, incl. 64-bit SteamIds)
        private readonly Dictionary<ulong, ulong> _outwardToRaw = new Dictionary<ulong, ulong>();
        // per-child reverse map: rawId -> outwardId
        private readonly Dictionary<ITransport, Dictionary<ulong, ulong>> _rawToOutward
            = new Dictionary<ITransport, Dictionary<ulong, ulong>>();

        // Holds the exact delegate instances wired to each child so Shutdown can `-=` them
        // (anonymous lambdas are otherwise impossible to unsubscribe → stale-handler double-fire
        // if a child is ever reused).
        private sealed class ChildSubscription
        {
            public Action<ulong, string> Connected;
            public Action<ulong, string> Disconnected;
            public Action<ulong, byte[]> Packet;
            public Action<ConnectionState> StateChanged;
        }
        private readonly Dictionary<ITransport, ChildSubscription> _subscriptions
            = new Dictionary<ITransport, ChildSubscription>();

        private ulong _nextOutwardId = 1;

        public CompositeTransport(IEnumerable<ITransport> children)
        {
            if (children == null) throw new ArgumentNullException(nameof(children));
            _children = new List<ITransport>(children);

            foreach (var child in _children)
            {
                _rawToOutward[child] = new Dictionary<ulong, ulong>();
                var captured = child; // capture for closures

                // Store each handler instance so Shutdown can later `-=` the EXACT delegate
                // (lambdas wired anonymously can't be unsubscribed → stale-handler double-fire
                // if a child is ever reused).
                var sub = new ChildSubscription
                {
                    Connected    = (rawId, ep)   => HandleChildConnected(captured, rawId, ep),
                    Disconnected = (rawId, ep)   => HandleChildDisconnected(captured, rawId, ep),
                    Packet       = (rawId, data) => HandleChildPacket(captured, rawId, data),
                    StateChanged = _             => OnStateChanged?.Invoke(State),
                };
                _subscriptions[child] = sub;

                child.OnPeerConnected    += sub.Connected;
                child.OnPeerDisconnected += sub.Disconnected;
                child.OnPacketReceived   += sub.Packet;
                child.OnStateChanged     += sub.StateChanged;
            }
        }

        /// <summary>The live child transports (host-side), e.g. for the lobby rail to
        /// read the STUN child's PublicEndPoint / hosting flag.</summary>
        public IReadOnlyList<ITransport> Children => _children;

        // ─── ITransport surface ───────────────────────────────────────────

        // No dedicated enum value (TransportType is a byte 0/1/2 consumed by serializers);
        // report the first child's type for diagnostics only — never used for routing.
        public TransportType TransportType => _children.Count > 0 ? _children[0].TransportType : TransportType.DirectIP;

        public ConnectionState State
        {
            get
            {
                bool anyConnecting = false, anyFailed = false;
                foreach (var c in _children)
                {
                    if (c.State == ConnectionState.Connected) return ConnectionState.Connected;
                    if (c.State == ConnectionState.Connecting) anyConnecting = true;
                    if (c.State == ConnectionState.Failed) anyFailed = true;
                }
                if (anyConnecting) return ConnectionState.Connecting;
                if (anyFailed) return ConnectionState.Failed;
                return ConnectionState.Disconnected;
            }
        }

        public bool IsHost
        {
            get
            {
                foreach (var c in _children)
                    if (c.IsHost) return true;
                return false;
            }
        }

        public string LocalEndpoint
        {
            get
            {
                var parts = new List<string>(_children.Count);
                foreach (var c in _children) parts.Add(c.LocalEndpoint);
                return string.Join(" | ", parts.ToArray());
            }
        }

        // First non-null public endpoint (the STUN child, once discovery completes).
        public IPEndPoint PublicEndPoint
        {
            get
            {
                foreach (var c in _children)
                    if (c.PublicEndPoint != null) return c.PublicEndPoint;
                return null;
            }
        }

        public event Action<ConnectionState> OnStateChanged;
        public event Action<ulong, byte[]> OnPacketReceived;
        public event Action<ulong, string> OnPeerConnected;
        public event Action<ulong, string> OnPeerDisconnected;

        public void Initialize()
        {
            foreach (var c in _children)
            {
                try { c.Initialize(); } catch { /* one child failing must not abort others */ }
            }
        }

        public void Shutdown()
        {
            foreach (var c in _children)
            {
                // Unsubscribe the EXACT delegates wired in the ctor so a reused child can never
                // double-fire into this (now dead) composite.
                if (_subscriptions.TryGetValue(c, out var sub))
                {
                    c.OnPeerConnected    -= sub.Connected;
                    c.OnPeerDisconnected -= sub.Disconnected;
                    c.OnPacketReceived   -= sub.Packet;
                    c.OnStateChanged     -= sub.StateChanged;
                }
                try { c.Shutdown(); } catch { }
            }
            lock (_lock)
            {
                _peerToChild.Clear();
                _outwardToRaw.Clear();
                foreach (var m in _rawToOutward.Values) m.Clear();
            }
        }

        public void Host(int port = 0)
        {
            // Per-child try/catch: if one transport can't host (port in use, Steam
            // unavailable, …) the rest still come up. Log which child failed so a down LAN/STUN/Steam
            // path is diagnosable (a silent swallow previously hid e.g. a DirectIP bind clash).
            foreach (var c in _children)
            {
                try
                {
                    c.Host(port);
                    // A child may report a bind/host failure via State rather than throwing
                    // (DirectTransport flips to Failed on a port clash instead of escaping). Log that
                    // too so a down path is visible even when no exception propagates.
                    if (c.State == ConnectionState.Failed)
                        LogError($"[Multipleer] CompositeTransport: child {c.TransportType} reported " +
                                 $"Failed after Host on port {port} ({c.LocalEndpoint}).");
                }
                catch (Exception ex)
                {
                    LogError($"[Multipleer] CompositeTransport: child {c.TransportType} failed to host " +
                             $"on port {port}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // CompositeTransport is intentionally Unity-free at compile time (the test project links it
        // directly without referencing UnityEngine), so we reach UnityEngine.Debug.LogError via
        // reflection. In-game the call resolves and lands in the Player.log; under tests it is a
        // harmless no-op. Resolution is cached after first use.
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
                    Console.WriteLine(message); // fallback (e.g. test host with no UnityEngine loaded)
            }
            catch { /* logging must never break the host path */ }
        }

        // Composite is HOST-side only; clients always use a single concrete transport.
        public void Connect(string address, int port) { /* host-only: no-op */ }

        public void Disconnect()
        {
            foreach (var c in _children)
            {
                try { c.Disconnect(); } catch { }
            }
        }

        public void Send(ulong peerId, byte[] data, bool reliable = true)
        {
            ITransport child;
            ulong rawId;
            lock (_lock)
            {
                if (!_peerToChild.TryGetValue(peerId, out child)) return; // unknown id → safe no-op
                rawId = _outwardToRaw[peerId];
            }
            child.Send(rawId, data, reliable);
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            foreach (var c in _children)
            {
                try { c.Broadcast(data, reliable); } catch { }
            }
        }

        public void Update()
        {
            foreach (var c in _children) c.Update();
        }

        // ─── Child callback handlers ──────────────────────────────────────

        private void HandleChildConnected(ITransport child, ulong rawId, string endpoint)
        {
            var outwardId = MapOrGet(child, rawId);
            OnPeerConnected?.Invoke(outwardId, endpoint);
        }

        private void HandleChildPacket(ITransport child, ulong rawId, byte[] data)
        {
            // Normally the peer is already mapped (connect precedes packets). Lazily map
            // on the off chance a packet races ahead so the payload is never dropped.
            var outwardId = MapOrGet(child, rawId);
            OnPacketReceived?.Invoke(outwardId, data);
        }

        private void HandleChildDisconnected(ITransport child, ulong rawId, string endpoint)
        {
            ulong outwardId;
            lock (_lock)
            {
                var reverse = _rawToOutward[child];
                if (!reverse.TryGetValue(rawId, out outwardId)) return;
                reverse.Remove(rawId);
                _peerToChild.Remove(outwardId);
                _outwardToRaw.Remove(outwardId);
            }
            OnPeerDisconnected?.Invoke(outwardId, endpoint);
        }

        // Returns the existing outward id for (child, rawId) or mints a new one.
        private ulong MapOrGet(ITransport child, ulong rawId)
        {
            lock (_lock)
            {
                var reverse = _rawToOutward[child];
                if (reverse.TryGetValue(rawId, out var existing)) return existing;

                var outwardId = _nextOutwardId++;
                reverse[rawId] = outwardId;
                _peerToChild[outwardId] = child;
                _outwardToRaw[outwardId] = rawId;
                return outwardId;
            }
        }
    }
}
