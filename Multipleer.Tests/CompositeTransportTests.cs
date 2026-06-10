using System;
using System.Collections.Generic;
using System.Net;
using Multipleer.Transport;
using Xunit;
using TransportType = Multipleer.Transport.TransportType;

namespace Multipleer.Tests
{
    // Pure-logic tests for CompositeTransport peer-id namespacing + routing.
    // No Unity / no sockets — children are FakeTransport recorders.
    public class CompositeTransportTests
    {
        // Minimal ITransport fake that lets a test drive child callbacks and
        // record outgoing Send/Broadcast/Host calls.
        private sealed class FakeTransport : ITransport
        {
            public TransportType TransportType { get; set; } = TransportType.DirectIP;
            public ConnectionState State { get; set; } = ConnectionState.Disconnected;
            public bool IsHost { get; set; }
            public string LocalEndpoint { get; set; } = "fake";
            public IPEndPoint PublicEndPoint { get; set; }

            public event Action<ConnectionState> OnStateChanged;
            public event Action<ulong, byte[]> OnPacketReceived;
            public event Action<ulong, string> OnPeerConnected;
            public event Action<ulong, string> OnPeerDisconnected;

            // Recorders
            public readonly List<(ulong peerId, byte[] data)> Sent = new List<(ulong, byte[])>();
            public readonly List<byte[]> Broadcasts = new List<byte[]>();
            public int HostCalls;
            public int InitCalls;
            public int UpdateCalls;
            public int ShutdownCalls;
            public bool ThrowOnHost;

            public void Initialize() { InitCalls++; }
            public void Shutdown() { ShutdownCalls++; }
            public void Host(int port = 0)
            {
                HostCalls++;
                if (ThrowOnHost) throw new InvalidOperationException("host boom");
                IsHost = true;
            }
            public void Connect(string address, int port) { }
            public void Disconnect() { }
            public void Send(ulong peerId, byte[] data, bool reliable = true) => Sent.Add((peerId, data));
            public void Broadcast(byte[] data, bool reliable = true) => Broadcasts.Add(data);
            public void Update() { UpdateCalls++; }

            // Test drivers (raise the child-side events the composite subscribes to).
            public void RaiseConnected(ulong rawId, string endpoint = "ep")
                => OnPeerConnected?.Invoke(rawId, endpoint);
            public void RaisePacket(ulong rawId, byte[] data)
                => OnPacketReceived?.Invoke(rawId, data);
            public void RaiseDisconnected(ulong rawId, string endpoint = "ep")
                => OnPeerDisconnected?.Invoke(rawId, endpoint);
            public void RaiseState(ConnectionState s) { State = s; OnStateChanged?.Invoke(s); }
        }

        [Fact]
        public void TwoChildren_SameRawId_YieldDistinctOutwardIds_NoClobber()
        {
            var a = new FakeTransport();
            var b = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a, b });

            var connected = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => connected.Add(id);

            a.RaiseConnected(2);
            b.RaiseConnected(2);

            Assert.Equal(2, connected.Count);
            Assert.NotEqual(connected[0], connected[1]); // no collision despite identical rawId
        }

        [Fact]
        public void Send_RoutesToCorrectChild_WithTranslatedRawId()
        {
            var a = new FakeTransport();
            var b = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a, b });

            var ids = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => ids.Add(id);

            a.RaiseConnected(2);   // ids[0] -> child a, rawId 2
            b.RaiseConnected(2);   // ids[1] -> child b, rawId 2

            var payloadA = new byte[] { 0xAA };
            var payloadB = new byte[] { 0xBB };
            composite.Send(ids[0], payloadA);
            composite.Send(ids[1], payloadB);

            Assert.Single(a.Sent);
            Assert.Equal((ulong)2, a.Sent[0].peerId);
            Assert.Same(payloadA, a.Sent[0].data);

            Assert.Single(b.Sent);
            Assert.Equal((ulong)2, b.Sent[0].peerId);
            Assert.Same(payloadB, b.Sent[0].data);
        }

        [Fact]
        public void PacketReceived_ReEmittedWithOutwardId()
        {
            var a = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a });

            ulong outwardConnected = 0;
            composite.OnPeerConnected += (id, ep) => outwardConnected = id;

            var received = new List<(ulong, byte[])>();
            composite.OnPacketReceived += (id, data) => received.Add((id, data));

            a.RaiseConnected(2);
            var payload = new byte[] { 1, 2, 3 };
            a.RaisePacket(2, payload);

            Assert.Single(received);
            Assert.Equal(outwardConnected, received[0].Item1);
            Assert.Same(payload, received[0].Item2);
        }

        [Fact]
        public void Disconnect_PurgesMaps_SubsequentSendIsSafeNoOp()
        {
            var a = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a });

            ulong id = 0;
            composite.OnPeerConnected += (x, ep) => id = x;
            var disconnected = new List<ulong>();
            composite.OnPeerDisconnected += (x, ep) => disconnected.Add(x);

            a.RaiseConnected(2);
            a.RaiseDisconnected(2);

            Assert.Single(disconnected);
            Assert.Equal(id, disconnected[0]);

            // Send to a now-purged id must NOT throw and must NOT reach the child.
            composite.Send(id, new byte[] { 9 });
            Assert.Empty(a.Sent);
        }

        [Fact]
        public void Reconnect_SameRawId_MintsFreshOutwardId_DeadIdIsNoOp()
        {
            // CRUX: Direct/STUN restart their rawId counter at 1 per child, so a peer that
            // disconnects and a NEW peer that connects can both carry rawId=1. The composite must
            // mint a FRESH outward id on the second connect (no collision with the dead peer), and
            // sending to the stale outward id must be a safe no-op while the new id routes correctly.
            var a = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a });

            var connected = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => connected.Add(id);

            a.RaiseConnected(1);            // peer #1 on rawId 1
            var outwardA = connected[0];

            a.RaiseDisconnected(1);         // peer #1 leaves → maps for rawId 1 purged
            a.RaiseConnected(1);            // peer #2 reuses rawId 1
            var outwardB = connected[1];

            Assert.NotEqual(outwardA, outwardB);          // fresh id, no collision with dead peer

            // Stale outward id is purged → Send is a safe no-op (does not reach the child).
            composite.Send(outwardA, new byte[] { 0xDE });
            Assert.Empty(a.Sent);

            // New outward id routes to the child with the reused rawId 1.
            composite.Send(outwardB, new byte[] { 0xAD });
            Assert.Single(a.Sent);
            Assert.Equal((ulong)1, a.Sent[0].peerId);
        }

        [Fact]
        public void Send_UnknownId_IsSafeNoOp()
        {
            var a = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a });

            var ex = Record.Exception(() => composite.Send(123456, new byte[] { 1 }));
            Assert.Null(ex);
            Assert.Empty(a.Sent);
        }

        [Fact]
        public void Broadcast_FansOutToAllChildren()
        {
            var a = new FakeTransport();
            var b = new FakeTransport();
            var c = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a, b, c });

            var payload = new byte[] { 7 };
            composite.Broadcast(payload);

            Assert.Single(a.Broadcasts);
            Assert.Single(b.Broadcasts);
            Assert.Single(c.Broadcasts);
            Assert.Same(payload, a.Broadcasts[0]);
        }

        [Fact]
        public void SendToAll_PerPeerIteration_RoutesEachToRightChild()
        {
            // Simulates NetworkEngine.BroadcastExcept iterating session peers and
            // calling Send(outwardId) per peer — each must land on its origin child.
            var a = new FakeTransport();
            var b = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a, b });

            var ids = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => ids.Add(id);
            a.RaiseConnected(2);
            a.RaiseConnected(3);
            b.RaiseConnected(2);

            var data = new byte[] { 5 };
            foreach (var id in ids)
                composite.Send(id, data);

            Assert.Equal(2, a.Sent.Count);            // two peers on child a
            Assert.Single(b.Sent);                    // one peer on child b
            Assert.Equal((ulong)2, b.Sent[0].peerId); // child b's raw id preserved
        }

        [Fact]
        public void SteamStyleLargeId_PreservedAndTranslatedBackExactly()
        {
            // Steam child emits a real 64-bit SteamID64. The composite must keep it
            // unique AND translate the outward id back to the EXACT 64-bit raw id on Send,
            // with no bit-truncation (the bit-pack <<56 scheme would corrupt this).
            const ulong steamId = 76561197960287930UL; // > 2^56, top bits set
            var steam = new FakeTransport { TransportType = TransportType.SteamP2P };
            var composite = new CompositeTransport(new ITransport[] { steam });

            ulong outward = 0;
            composite.OnPeerConnected += (id, ep) => outward = id;

            steam.RaiseConnected(steamId);
            composite.Send(outward, new byte[] { 1 });

            Assert.Single(steam.Sent);
            Assert.Equal(steamId, steam.Sent[0].peerId); // EXACT round-trip, no truncation
        }

        [Fact]
        public void Lifecycle_ForwardsToAllChildren()
        {
            var a = new FakeTransport();
            var b = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { a, b });

            composite.Initialize();
            composite.Host(14242);
            composite.Update();
            composite.Shutdown();

            Assert.Equal(1, a.InitCalls);
            Assert.Equal(1, b.InitCalls);
            Assert.Equal(1, a.HostCalls);
            Assert.Equal(1, b.HostCalls);
            Assert.Equal(1, a.UpdateCalls);
            Assert.Equal(1, b.UpdateCalls);
            Assert.Equal(1, a.ShutdownCalls);
            Assert.Equal(1, b.ShutdownCalls);
        }

        [Fact]
        public void Host_OneChildThrows_OthersStillHost_AndIsHostTrue()
        {
            var ok1 = new FakeTransport();
            var bad = new FakeTransport { ThrowOnHost = true };
            var ok2 = new FakeTransport();
            var composite = new CompositeTransport(new ITransport[] { ok1, bad, ok2 });

            var ex = Record.Exception(() => composite.Host(14242));
            Assert.Null(ex);                 // composite swallows child Host failure
            Assert.Equal(1, ok1.HostCalls);
            Assert.Equal(1, bad.HostCalls);
            Assert.Equal(1, ok2.HostCalls);  // continued past the throwing child
            Assert.True(composite.IsHost);   // at least one child hosting
        }
    }
}
