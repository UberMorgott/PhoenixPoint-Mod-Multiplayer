using System;
using System.Collections.Generic;
using System.Net;
using Multiplayer.Transport;
using Xunit;
using TransportType = Multiplayer.Transport.TransportType;

namespace Multiplayer.Tests
{
    /// <summary>
    /// Arrival-order simulation: proves all THREE transports (Direct + STUN + Steam) feed ONE
    /// logical session simultaneously, and that player slot/name issuance is by ARRIVAL ORDER
    /// (whoever connects first = player 1, …), INDEPENDENT of which transport they came from.
    ///
    /// SLOT/NAME GROUNDING (read-only, Serena):
    ///   - ClientInfo (SessionManager.cs) has NO explicit player-number/slot field. Arrival order
    ///     IS simply the order SessionManager.AddClient is invoked.
    ///   - SessionManager.BuildPeerList() enumerates _clients.Values (an insertion-ordered
    ///     Dictionary&lt;ulong,ClientInfo&gt;) right after the host self-entry, so the roster order =
    ///     AddClient call order = "player 1/2/3".
    ///   - Chain (transport-agnostic, arrival-preserving):
    ///       child.OnPeerConnected(rawId)
    ///         -> CompositeTransport.HandleChildConnected -> MapOrGet mints a MONOTONIC outward id
    ///         -> CompositeTransport re-emits OnPeerConnected(outwardId)
    ///         -> NetworkEngine.OnPeerConnected (NetworkEngine.cs:271)
    ///         -> Session.AddClient(outwardId, endpoint)  (NetworkEngine.cs:275, host only)
    ///     So the order AddClient runs == the order the composite re-emits OnPeerConnected ==
    ///     the order peers physically arrived, regardless of source child.
    ///
    /// WHY SessionManager IS NOT WIRED IN END-TO-END HERE:
    ///   SessionManager pulls in UnityEngine (`using UnityEngine;`, Debug.Log, ClientIdentity,
    ///   PermissionManager) and is NOT linked by Multiplayer.Tests.csproj (only pure, Unity-free
    ///   files are linked). It cannot be unit-instantiated without the game DLLs. We therefore
    ///   assert arrival-order at the COMPOSITE boundary — the exact ordered stream NetworkEngine
    ///   forwards verbatim into AddClient — which is the load-bearing guarantee for slot order.
    /// </summary>
    public class MultiTransportArrivalSimulationTests
    {
        // Self-contained ITransport fake (the one in CompositeTransportTests is private there).
        private sealed class FakeTransport : ITransport
        {
            public TransportType TransportType { get; set; } = TransportType.DirectIP;
            public ConnectionState State { get; set; } = ConnectionState.Disconnected;
            public bool IsHost { get; set; }
            public string LocalEndpoint { get; set; } = "fake";
            public IPEndPoint PublicEndPoint { get; set; }

#pragma warning disable CS0067 // unused events are part of the ITransport surface; this fake only drives OnPeerConnected
            public event Action<ConnectionState> OnStateChanged;
            public event Action<ulong, byte[]> OnPacketReceived;
            public event Action<ulong, string> OnPeerConnected;
            public event Action<ulong, string> OnPeerDisconnected;
#pragma warning restore CS0067

            public readonly List<(ulong peerId, byte[] data)> Sent = new List<(ulong, byte[])>();

            public void Initialize() { }
            public void Shutdown() { }
            public void Host(int port = 0) { IsHost = true; }
            public void Connect(string address, int port) { }
            public void Disconnect() { }
            public void Send(ulong peerId, byte[] data, bool reliable = true) => Sent.Add((peerId, data));
            public void Broadcast(byte[] data, bool reliable = true) { }
            public bool DisconnectPeer(ulong peerId) => false;
            public void Update() { }

            public void RaiseConnected(ulong rawId, string endpoint = "ep")
                => OnPeerConnected?.Invoke(rawId, endpoint);
        }

        [Fact]
        public void ThreeTransports_InterleavedArrivals_SlotsAssignedByArrivalOrder_NotBySource()
        {
            // index0 = Direct, index1 = STUN, index2 = Steam — all multiplexed by ONE composite.
            var direct = new FakeTransport { TransportType = TransportType.DirectIP };
            var stun   = new FakeTransport { TransportType = TransportType.DirectIP };  // STUN reuses DirectIP serializer type
            var steam  = new FakeTransport { TransportType = TransportType.SteamP2P };
            var composite = new CompositeTransport(new ITransport[] { direct, stun, steam });

            // Recorder = the ordered (outwardId) stream NetworkEngine forwards into AddClient.
            // Its index = the player slot ("player 1/2/3 …"); the value = the outward peer id.
            var arrivalOrder = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => arrivalOrder.Add(id);

            const ulong steamId = 76561198000000000UL; // real 64-bit SteamID64 (> 2^56)

            // INTERLEAVED arrivals across all three transports, in this exact physical order.
            // Note Direct & STUN both reuse raw=1 and raw=2 — the namespacing must keep them distinct.
            stun.RaiseConnected(1);          // arrival #1  (player 1) — STUN
            steam.RaiseConnected(steamId);   // arrival #2  (player 2) — Steam, big 64-bit id
            direct.RaiseConnected(1);        // arrival #3  (player 3) — Direct, rawId reused
            direct.RaiseConnected(2);        // arrival #4  (player 4) — Direct
            stun.RaiseConnected(2);          // arrival #5  (player 5) — STUN, rawId reused

            // (a) 5 peers, 5 DISTINCT outward ids despite Direct&STUN reusing raw 1/2 and Steam's 64-bit id.
            Assert.Equal(5, arrivalOrder.Count);
            Assert.Equal(5, new HashSet<ulong>(arrivalOrder).Count);

            // (b) MINT ORDER == ARRIVAL ORDER (strictly monotonic), transport-agnostic.
            // The composite's counter starts at 1 and only increments, so arrival #1 gets the
            // smallest id and #5 the largest — proving the slot key follows arrival, not source.
            for (int i = 1; i < arrivalOrder.Count; i++)
                Assert.True(arrivalOrder[i] > arrivalOrder[i - 1],
                    $"arrival #{i + 1} id {arrivalOrder[i]} must exceed prior id {arrivalOrder[i - 1]}");

            // Concretely: ids are minted 1,2,3,4,5 in arrival order regardless of which child fired.
            Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, arrivalOrder.ToArray());

            // The slot→source mapping is by arrival, NOT by transport index:
            //   player1 = STUN guy, player2 = Steam guy, player3 = Direct guy, …
            // (STUN is child index 1, yet it took slot 1 because it arrived first.)

            // (c) Send to each outward id routes back to the CORRECT child with the CORRECT raw id,
            // including the exact 64-bit SteamId (no truncation).
            var probe = new byte[] { 0x01 };
            foreach (var id in arrivalOrder)
                composite.Send(id, probe);

            // STUN child: raw 1 (slot1) then raw 2 (slot5)
            Assert.Equal(2, stun.Sent.Count);
            Assert.Equal((ulong)1, stun.Sent[0].peerId);
            Assert.Equal((ulong)2, stun.Sent[1].peerId);

            // Steam child: exact 64-bit SteamId preserved on the round-trip
            Assert.Single(steam.Sent);
            Assert.Equal(steamId, steam.Sent[0].peerId);

            // Direct child: raw 1 (slot3) then raw 2 (slot4)
            Assert.Equal(2, direct.Sent.Count);
            Assert.Equal((ulong)1, direct.Sent[0].peerId);
            Assert.Equal((ulong)2, direct.Sent[1].peerId);
        }

        [Fact]
        public void ArrivalOrderStream_IsTheRosterOrder_FedVerbatimIntoAddClient()
        {
            // Mirrors what NetworkEngine.OnPeerConnected does (NetworkEngine.cs:271-276):
            // for each composite OnPeerConnected it calls Session.AddClient(outwardId, ...) in order.
            // SessionManager keeps _clients insertion-ordered, so this captured order IS the lobby
            // roster order (BuildPeerList enumerates _clients.Values after the host self-entry).
            // Since SessionManager itself is Unity-bound (not linkable), we assert the ordered
            // outward-id stream — the sole input that determines slot order — directly.
            var direct = new FakeTransport();
            var stun   = new FakeTransport();
            var steam  = new FakeTransport { TransportType = TransportType.SteamP2P };
            var composite = new CompositeTransport(new ITransport[] { direct, stun, steam });

            // Simulated host-side roster: outward id appended in AddClient (== arrival) order.
            var roster = new List<ulong>();
            composite.OnPeerConnected += (id, ep) => roster.Add(id); // stands in for Session.AddClient

            const ulong steamId = 76561198000000000UL;
            steam.RaiseConnected(steamId); // arrival #1 -> player 1
            direct.RaiseConnected(1);      // arrival #2 -> player 2
            stun.RaiseConnected(1);        // arrival #3 -> player 3

            // Roster slot order follows arrival, independent of source transport:
            Assert.Equal(3, roster.Count);
            Assert.Equal((ulong)1, roster[0]); // slot1 = Steam guy (arrived first)
            Assert.Equal((ulong)2, roster[1]); // slot2 = Direct guy
            Assert.Equal((ulong)3, roster[2]); // slot3 = STUN guy
        }
    }
}
