using System;
using System.Net;
using System.Net.Sockets;
using Multipleer.Transport;
using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Contract for the DirectTransport host BIND-FAILURE fix (Transport fix #3). Previously
    /// DirectTransport.Host let a bind SocketException (port already in use) propagate, and in the
    /// composite host path it was swallowed per-child with no signal — so the host believed its LAN
    /// path was up when it was not. The fix makes Host() catch the bind failure and surface it as a
    /// QUERYABLE state: ConnectionState.Failed + a "bind failed" endpoint label, without throwing
    /// (StartHost may call this on a bare DirectTransport, so an escaping exception would abort the
    /// whole host bring-up). These use a REAL loopback bind (DirectTransport is Unity-free).
    /// </summary>
    public class DirectTransportHostBindFailureTests
    {
        // Bind a real listener on the SAME endpoint DirectTransport.Host uses (IPAddress.Any) and keep
        // it OPEN so a second bind on the same port reliably clashes with AddressAlreadyInUse on
        // Windows (a loopback-only occupier would NOT clash with an Any bind). Caller must Stop() it.
        private static TcpListener OccupyAnyPort(out int port)
        {
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            return l; // left running → port is in use on 0.0.0.0
        }

        [Fact]
        public void Host_PortInUse_SurfacesFailed_DoesNotThrow()
        {
            var occupier = OccupyAnyPort(out var port);
            var t = new DirectTransport();
            t.Initialize();
            try
            {
                ConnectionState? lastState = null;
                t.OnStateChanged += s => lastState = s;

                // DirectTransport.Host binds TcpListener(IPAddress.Any, port) — the same endpoint the
                // occupier holds → SocketException AddressAlreadyInUse.
                var ex = Record.Exception(() => t.Host(port));

                // The fix must NOT let the bind exception escape Host().
                Assert.Null(ex);
                // Failure must be queryable via State (and the last raised state change).
                Assert.Equal(ConnectionState.Failed, t.State);
                Assert.Equal(ConnectionState.Failed, lastState);
                // Endpoint label reflects the bind failure (not a bogus "host:<port>" listening label).
                Assert.Contains("bind failed", t.LocalEndpoint);
            }
            finally
            {
                try { t.Shutdown(); } catch { }
                occupier.Stop();
            }
        }

        [Fact]
        public void Host_FreePort_SurfacesConnected()
        {
            // Sanity counterpart: a free port still hosts successfully (no regression from the guard).
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop(); // release so the real Host can bind it

            var t = new DirectTransport();
            t.Initialize();
            try
            {
                t.Host(port);

                Assert.Equal(ConnectionState.Connected, t.State);
                Assert.Contains($"host:{port}", t.LocalEndpoint);
            }
            finally
            {
                try { t.Shutdown(); } catch { }
            }
        }
    }
}
