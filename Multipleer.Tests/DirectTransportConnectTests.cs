using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Multipleer.Transport;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Anti-freeze / anti-crash contract for DirectTransport.Connect (the bug: joining an
    /// unreachable IP froze the game on a blocking socket connect on the Unity main thread, then
    /// crashed). These use REAL loopback sockets (DirectTransport is Unity-free) and assert:
    ///   1) Connect() RETURNS IMMEDIATELY (non-blocking) — it must not block the caller while the
    ///      socket connect happens, so the UI thread never freezes.
    ///   2) A FAILED connect surfaces as ConnectionState.Failed, but ONLY when Update() is pumped
    ///      (i.e. the failure callback is marshaled to the main/Update thread, not raised off-thread).
    ///   3) A SUCCESSFUL connect surfaces as Connected + OnPeerConnected, also only via Update().
    ///   4) Shutdown() during an in-flight connect does not throw / leaves no surfaced outcome.
    /// </summary>
    public class DirectTransportConnectTests
    {
        // Pump Update() until predicate holds or timeout; returns whether predicate was met.
        private static bool PumpUntil(DirectTransport t, Func<bool> predicate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                t.Update();
                if (predicate()) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        // Grab a port that is reachable but has NO listener → connection refused (fast RST on
        // loopback) so the failure path is exercised quickly and deterministically.
        private static int GetClosedLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop(); // closed: nothing listening on this port now
            return port;
        }

        [Fact]
        public void Connect_ReturnsImmediately_DoesNotBlockCaller()
        {
            var t = new DirectTransport();
            t.Initialize();
            var port = GetClosedLoopbackPort();

            try
            {
                var sw = Stopwatch.StartNew();
                t.Connect("127.0.0.1", port);
                sw.Stop();

                // The blocking-connect bug would hold the caller here for ~20s on an unreachable host.
                // The fix runs the connect on a worker, so Connect() must return effectively instantly.
                Assert.True(sw.ElapsedMilliseconds < 500,
                    $"Connect blocked the caller for {sw.ElapsedMilliseconds}ms (must be non-blocking)");
            }
            finally
            {
                try { t.Shutdown(); } catch { }
            }
        }

        [Fact]
        public void Connect_Unreachable_SurfacesFailed_OnlyViaUpdate()
        {
            var t = new DirectTransport();
            t.Initialize();
            var port = GetClosedLoopbackPort();

            ConnectionState? lastState = null;
            t.OnStateChanged += s => lastState = s;

            try
            {
                t.Connect("127.0.0.1", port);
                // Immediately after Connect we are Connecting, NOT yet Failed — the outcome is surfaced
                // only when Update() runs (main-thread marshaling).
                Assert.Equal(ConnectionState.Connecting, t.State);

                var failed = PumpUntil(t, () => t.State == ConnectionState.Failed, 8000);
                Assert.True(failed, "connect to a closed port should surface Failed via Update");
                Assert.Equal(ConnectionState.Failed, lastState);
            }
            finally
            {
                try { t.Shutdown(); } catch { }
            }
        }

        [Fact]
        public void Connect_LiveHost_SurfacesConnectedAndPeer_ViaUpdate()
        {
            // Host on a real loopback listener, then connect a second DirectTransport to it.
            // DirectTransport.Host's LocalEndpoint label is built from the port ARG (not the bound
            // socket), so we must hand it a concrete free port rather than 0 — grab a fresh ephemeral
            // one per test so two real-socket tests never bind the same port.
            var freePort = GetFreeLoopbackPort();
            var host = new DirectTransport();
            host.Initialize();
            host.Host(freePort);

            var client = new DirectTransport();
            client.Initialize();

            ulong connectedPeer = 0;
            client.OnPeerConnected += (id, ep) => connectedPeer = id;

            try
            {
                client.Connect("127.0.0.1", freePort);
                Assert.Equal(ConnectionState.Connecting, client.State);

                var connected = PumpUntil(client, () => client.State == ConnectionState.Connected, 8000);
                Assert.True(connected, "connect to a live host should surface Connected via Update");
                Assert.NotEqual((ulong)0, connectedPeer);
            }
            finally
            {
                try { client.Shutdown(); } catch { }
                try { host.Shutdown(); } catch { }
            }
        }

        [Fact]
        public void Shutdown_DuringInFlightConnect_NoThrow_NoSurfacedOutcome()
        {
            var t = new DirectTransport();
            t.Initialize();
            // 198.51.100.1 is RFC5737 TEST-NET-2 (non-routable) → connect hangs until timeout, so the
            // worker is reliably mid-connect when we Shutdown.
            t.Connect("198.51.100.1", 14242);

            var ex = Record.Exception(() => t.Shutdown());
            Assert.Null(ex);

            // After an aborted connect, pumping Update must not surface a stale Connected/Failed.
            ConnectionState? surfaced = null;
            t.OnStateChanged += s => surfaced = s;
            for (int i = 0; i < 5; i++) { t.Update(); Thread.Sleep(10); }
            Assert.Null(surfaced); // no outcome surfaced post-shutdown
        }

        private static int GetFreeLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
