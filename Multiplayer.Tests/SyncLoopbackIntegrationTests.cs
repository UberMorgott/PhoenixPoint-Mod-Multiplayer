using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Multiplayer.Network.Sync;
using Multiplayer.Transport;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// END-TO-END integration smoke test (Phase 3): stands up a host + client over the REAL
    /// loopback <see cref="DirectTransport"/> (Unity-free, so it runs here and in CI) and drives the
    /// pure sync primitives (<see cref="SyncProtocol"/> framing, <see cref="IntentDedup"/>,
    /// <see cref="SurfaceSeq"/>) through the actual socket rail — NOT a pure-unit re-test.
    ///
    /// It asserts the two guarantees the pure unit tests can't cover as a system:
    ///   1) CONVERGENCE — client-applied per-surface version reaches the host's latest assigned seq.
    ///   2) DEDUP-ON-DOUBLE-SEND — the reliable transport intentionally double-sends, so:
    ///        • host-side IntentDedup drops the duplicate client intent (3 sent twice → 3 applied),
    ///        • client-side SurfaceSeq (last-writer-wins) drops the duplicate host apply.
    ///
    /// Rail (both directions are the same TCP loopback DirectTransport):
    ///   client → host  : EncodeEnvelope(surface, ActionRequest, EncodeActionRequest(id, nonce, payload))
    ///   host   → all   : EncodeEnvelope(surface, ActionApply,   EncodeActionApply(id, seq, payload))
    ///
    /// Determinism: OnPacketReceived only fires from Update() (DirectTransport marshals every
    /// socket event to the pump thread), so both handlers run on this test thread — no locks, no
    /// real-time sleeps to converge. We poll Update() on both transports with a bounded timeout.
    /// </summary>
    public class SyncLoopbackIntegrationTests
    {
        // Pump BOTH transports' Update() until predicate holds or timeout; returns whether it held.
        private static bool PumpUntil(DirectTransport host, DirectTransport client, Func<bool> predicate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                host.Update();
                client.Update();
                if (predicate()) return true;
                Thread.Sleep(5);
            }
            // Final drain to catch anything queued right at the deadline.
            host.Update();
            client.Update();
            return predicate();
        }

        // Bind to port 0 to get a free ephemeral loopback port, then release it. Mirrors the
        // established GetFreeLoopbackPort pattern in DirectTransportConnectTests (avoids the fixed
        // 14242 port colliding with other real-socket tests in CI).
        private static int GetFreeLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Fact]
        public void HostClientLoopback_SyncLayerConverges_AndDedupsDoubleSend()
        {
            const byte SurfaceId = 7;
            const int IntentCount = 3;

            var host = new DirectTransport();
            host.Initialize();
            var port = GetFreeLoopbackPort();
            host.Host(port);
            Assert.Equal(ConnectionState.Connected, host.State); // host bound + listening

            var client = new DirectTransport();
            client.Initialize();

            // The client learns the host's minted peer id via OnPeerConnected (surfaced in Update()).
            ulong hostPeerId = 0;
            client.OnPeerConnected += (id, ep) => hostPeerId = id;

            // ── Host-side sync state (the shared backbone primitives) ──────────
            var hostDedup = new IntentDedup();   // ONE intent dedup, keyed by (peer, surface, nonce)
            var hostSeq = new SurfaceSeq();       // ONE per-surface monotonic seq source
            int hostApplied = 0;                  // unique intents applied (dedup keeps this == IntentCount)

            host.OnPacketReceived += (peerId, data) =>
            {
                if (!SyncProtocol.TryDecodeEnvelope(data, out var sid, out var kind, out var inner)) return;
                if (kind != SyncKind.ActionRequest) return;
                if (!SyncProtocol.TryDecodeActionRequest(inner, out var actionId, out var nonce, out var payload)) return;
                // The reliable rail can double-send the intent → drop the repeat here.
                if (!hostDedup.IsNew(peerId, sid, nonce)) return;
                hostApplied++;
                // Assign a monotonic per-surface seq and echo the apply to all clients — DOUBLE-SENT
                // on purpose to exercise the client's last-writer-wins guard.
                ulong seq = hostSeq.Next(sid);
                var apply = SyncProtocol.EncodeEnvelope(sid, SyncKind.ActionApply,
                    SyncProtocol.EncodeActionApply(actionId, seq, payload));
                host.Broadcast(apply);
                host.Broadcast(apply);
            };

            // ── Client-side sync state ─────────────────────────────────────────
            var clientSeq = new SurfaceSeq();     // last-writer-wins guard on the apply stream
            int clientApplied = 0;                // unique applies (seq guard keeps this == IntentCount)
            uint clientVersion = 0;               // converged per-surface version

            client.OnPacketReceived += (peerId, data) =>
            {
                if (!SyncProtocol.TryDecodeEnvelope(data, out var sid, out var kind, out var inner)) return;
                if (kind != SyncKind.ActionApply) return;
                if (!SyncProtocol.TryDecodeActionApply(inner, out var actionId, out var sequence, out var payload)) return;
                uint seq = (uint)sequence;
                if (!clientSeq.ShouldApply(sid, seq)) return; // drop stale/duplicate apply
                clientApplied++;
                clientSeq.Mark(sid, seq);
                if (seq > clientVersion) clientVersion = seq;
            };

            try
            {
                client.Connect("127.0.0.1", port);
                var connected = PumpUntil(host, client,
                    () => client.State == ConnectionState.Connected && hostPeerId != 0, 8000);
                Assert.True(connected, "client should connect to the host over loopback");

                // Push N intents through the REAL rail, each DOUBLE-SENT (reliable-transport behaviour).
                for (uint nonce = 1; nonce <= IntentCount; nonce++)
                {
                    var req = SyncProtocol.EncodeEnvelope(SurfaceId, SyncKind.ActionRequest,
                        SyncProtocol.EncodeActionRequest(SurfaceId, nonce, new byte[] { (byte)nonce }));
                    client.Send(hostPeerId, req);
                    client.Send(hostPeerId, req); // duplicate — host IntentDedup must drop it
                }

                // Converge: client applies exactly IntentCount unique outcomes; version == IntentCount.
                var converged = PumpUntil(host, client, () => clientVersion == IntentCount, 8000);
                Assert.True(converged,
                    $"sync did not converge: clientVersion={clientVersion} clientApplied={clientApplied} hostApplied={hostApplied}");

                // Let any in-flight duplicate arrive; the dedup primitives must still drop it (if dedup
                // were broken, a duplicate would bump these counts past IntentCount and fail below).
                PumpUntil(host, client, () => false, 200);

                // DEDUP (host): 3 intents sent twice = 6 request packets, only 3 unique applied.
                Assert.Equal(IntentCount, hostApplied);
                // DEDUP (client): each apply double-sent, seq guard applied each exactly once.
                Assert.Equal(IntentCount, clientApplied);
                // CONVERGENCE: client's applied version == host's latest assigned per-surface seq.
                Assert.Equal((uint)IntentCount, clientVersion);
            }
            finally
            {
                try { client.Shutdown(); } catch { }
                try { host.Shutdown(); } catch { }
            }
        }
    }
}
