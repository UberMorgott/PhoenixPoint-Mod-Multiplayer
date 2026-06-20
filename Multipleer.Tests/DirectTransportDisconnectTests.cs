using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Multipleer.Transport;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Symptom A: DirectTransport.Disconnect() used to Close() each peer socket and THEN read
    /// kvp.Value.Client.RemoteEndPoint as the OnPeerDisconnected label — reading RemoteEndPoint on a
    /// just-disposed socket throws ObjectDisposedException. The throw propagated on the UI thread
    /// through NetworkEngine.Disconnect → MultiplayerUI.OnDisconnectClicked and ABORTED the leave
    /// before teardown ran (UI freeze). It was also raised WHILE HOLDING _lock (latent reentrancy).
    ///
    /// The fix captures the endpoint label BEFORE Close() and raises OnPeerDisconnected OUTSIDE the
    /// lock. These use a REAL loopback host+client (DirectTransport is Unity-free).
    /// </summary>
    public class DirectTransportDisconnectTests
    {
        // Pump Update() on both transports until `predicate` holds or the budget expires. Update() is
        // where DirectTransport drains its background-thread peer events onto the (test) main thread.
        private static bool PumpUntil(Func<bool> predicate, params DirectTransport[] transports)
        {
            for (int i = 0; i < 200; i++) // ~2s budget
            {
                foreach (var t in transports) t.Update();
                if (predicate()) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        [Fact]
        public void Disconnect_WithConnectedPeer_DoesNotThrow_RaisesDisconnectOnceWithLabel()
        {
            // Free a loopback port for the host to bind.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var host = new DirectTransport();
            var client = new DirectTransport();
            host.Initialize();
            client.Initialize();

            int hostDisconnects = 0;
            string lastLabel = "SENTINEL";
            host.OnPeerDisconnected += (peerId, label) => { hostDisconnects++; lastLabel = label; };

            try
            {
                host.Host(port);
                Assert.Equal(ConnectionState.Connected, host.State);

                // Loopback client connect (non-blocking; surfaced on the host's accept thread, drained
                // into host.Update). Wait until the host has accepted the peer (OnPeerConnected).
                bool hostSawConnect = false;
                host.OnPeerConnected += (_, __) => hostSawConnect = true;
                client.Connect("127.0.0.1", port);

                var connected = PumpUntil(() => hostSawConnect, host, client);
                Assert.True(connected, "host did not accept the loopback client within the time budget");

                // The actual repro: host tears down while a peer socket is live. Old code Closed the
                // socket then read RemoteEndPoint → ObjectDisposedException; the fix must not throw.
                var ex = Record.Exception(() => host.Disconnect());
                Assert.Null(ex);

                // The disconnect event is raised on the leaving side. It may be surfaced synchronously
                // or drained via Update(); pump to be robust, then assert exactly one with a label.
                PumpUntil(() => hostDisconnects >= 1, host);
                Assert.Equal(1, hostDisconnects);
                Assert.NotNull(lastLabel);
                Assert.NotEqual("SENTINEL", lastLabel); // a real label was passed, not the sentinel
            }
            finally
            {
                try { host.Shutdown(); } catch { }
                try { client.Shutdown(); } catch { }
            }
        }
    }
}
