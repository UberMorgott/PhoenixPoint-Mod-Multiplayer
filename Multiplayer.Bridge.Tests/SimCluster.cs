using System;
using System.Collections.Generic;
using UnityEngine;
using Multiplayer.Network.Sync;

namespace Multiplayer.Bridge.Tests
{
    /// <summary>
    /// Wires 1 host + N clients onto a shared <see cref="InMemoryBus"/> using REAL engines, and pumps
    /// real <see cref="Multiplayer.Network.MessageLayer.NetworkMessage"/> packets through the REAL
    /// <see cref="SyncEngine"/> relay. No game, no sockets.
    /// </summary>
    public sealed class SimCluster : IDisposable
    {
        public readonly TraceLog Trace = new TraceLog();
        public readonly InMemoryBus Bus;
        public readonly SimPeer Host;
        public readonly List<SimPeer> Clients = new List<SimPeer>();

        private const int MaxPumpRounds = 20;

        public SimCluster(int clientCount)
        {
            NeutralizeUnityRuntime();
            // Register the test action exactly once (idempotent across clusters/tests).
            if (!SyncedActionRegistry.IsRegistered(CounterAction.Id))
                SyncedActionRegistry.Register(CounterAction.Id, CounterAction.Read);

            Bus = new InMemoryBus(Trace);

            // Host first (deterministic peerId = 1), then clients (2,3,…).
            Host = new SimPeer(Bus, SimRole.Host, "host");
            Host.Start();

            for (int i = 0; i < clientCount; i++)
            {
                var c = new SimPeer(Bus, SimRole.Client, "client" + i);
                c.Start();   // Connect → bus raises host-side OnPeerConnected (host AddClient, safe headless)
                Clients.Add(c);
            }

            WireRosterByHand();
        }

        public SimPeer Client(int i) => Clients[i];

        /// <summary>
        /// Authoritative roster wiring, done by hand (the spec's headless-safe path):
        ///  • host: AddClient(clientPeerId) [idempotent] + bind that ClientInfo.PlayerGuid to the
        ///    client's per-peer Guid (the permission key) + grant FullCommander so the gate passes;
        ///  • client: SetHostPeer(hostPeerId) so SendToHost has a target.
        /// This deliberately avoids the engine JOIN/nickname flow (SystemInfo.deviceName ECall throws
        /// headless) and the shared process-global ClientIdentity guid.
        /// </summary>
        private void WireRosterByHand()
        {
            foreach (var c in Clients)
            {
                Host.Engine.Session.AddClient(c.PeerId, $"mem://client/{c.PeerId}");
                if (Host.Engine.Session.Clients.TryGetValue(c.PeerId, out var ci))
                    ci.PlayerGuid = c.PlayerGuid;
                // Co-op default: each client gets FullCommander, keyed by its persistent player guid.
                Multiplayer.Validation.PermissionManager.SetPermission(
                    c.PlayerGuid, Multiplayer.Validation.CampaignPermission.FullCommander, true);

                c.Engine.Session.SetHostPeer(InMemoryBus.HostPeerId);
            }
        }

        /// <summary>
        /// Pump rounds until every bus queue is drained (or the bound is hit). Each round: for each
        /// peer set the apply context to THAT peer, then drive its engine.Update() (flush outbound to
        /// the bus + deliver its own inbound, applying actions into its sink). Repeats because one
        /// peer's delivery can enqueue new outbound (host relay → client applies). Bound asserts the
        /// exchange converges.
        /// </summary>
        public void Pump()
        {
            int round = 0;
            do
            {
                round++;
                if (round > MaxPumpRounds)
                    throw new InvalidOperationException($"Pump exceeded {MaxPumpRounds} rounds (queues not draining).");

                Trace.Add($"--- pump round {round} (pending={Bus.PendingTotal()}) ---");
                DrivePeer(Host);
                foreach (var c in Clients) DrivePeer(c);
            }
            while (Bus.PendingTotal() > 0);

            BridgeContext.CurrentPeer = null;
        }

        private void DrivePeer(SimPeer p)
        {
            BridgeContext.CurrentPeer = p;   // CounterAction.Apply writes into THIS peer's sink

            // Drive the transport directly rather than NetworkEngine.Update(). Transport.Update() raises
            // OnPacketReceived, which IS the engine's real receive path: OnPacketReceived → Deserialize →
            // RouteMessage → SyncEngine.OnActionRequest / OnActionApply / OnActionReject. So the FULL real
            // relay runs. We deliberately skip the other Update() subsystems (TimeSync.Tick,
            // SaveTransfer.Update, Session heartbeat) — they pump unrelated game-bound state that requires
            // the live Unity runtime (TimeSync reflection → native ECall; SaveTransfer → Assembly-CSharp
            // game types) and are NOT part of the action-sync relay under test.
            p.Transport.Update();
        }

        // ─── Unity headless runtime neutralization ────────────────────────
        private static bool _logInstalled;

        /// <summary>
        /// Route UnityEngine.Debug.* to managed code so the engine's Debug.Log/LogWarning/LogError
        /// calls do NOT P/Invoke into the absent Unity native runtime (verified: with this handler
        /// installed, Debug.Log does not throw headless). Also disable logging as belt-and-suspenders.
        /// SystemInfo.deviceName is a native ECall that DOES throw headless — the JOIN/nickname flow
        /// that would call it is avoided entirely (roster wired by hand; client OnPeerConnected not
        /// raised by the bus), so no managed shim is needed for it.
        /// </summary>
        private void NeutralizeUnityRuntime()
        {
            if (_logInstalled) return;
            _logInstalled = true;

            // The engine's Debug.Log/LogWarning/LogError calls P/Invoke into the absent Unity native
            // runtime and throw headless. Routing UnityEngine.Debug.* through a managed ILogHandler makes
            // them inert (verified: with this handler installed, Debug.Log does not throw). We do NOT load
            // the real Assembly-CSharp: the pump drives Transport.Update() (the relay receive path) and
            // never enters TimeSync/SaveTransfer, so GeoRuntime's AccessTools.TypeByName lookups find no
            // game types and return null — every Apply runs against an inert, null GeoRuntime.
            Debug.unityLogger.logHandler = new TraceLogHandler(Trace);
            Debug.unityLogger.logEnabled = true; // keep enabled so log lines still show in the trace
        }

        public void Dispose()
        {
            try { Host?.Engine.Shutdown(); } catch { }
            foreach (var c in Clients) { try { c.Engine.Shutdown(); } catch { } }
            BridgeContext.CurrentPeer = null;
        }
    }

    /// <summary>Managed <see cref="ILogHandler"/> that records engine log lines into the trace.</summary>
    internal sealed class TraceLogHandler : ILogHandler
    {
        private readonly TraceLog _trace;
        public TraceLogHandler(TraceLog trace) => _trace = trace;

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            var msg = (args != null && args.Length > 0) ? string.Format(format, args) : format;
            _trace.Add($"LOG[{logType}] {msg}");
        }

        public void LogException(Exception exception, UnityEngine.Object context)
            => _trace.Add("LOG[Exception] " + exception.Message);
    }
}
