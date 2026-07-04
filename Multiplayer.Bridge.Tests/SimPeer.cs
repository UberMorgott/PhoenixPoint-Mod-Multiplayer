using System;
using Multiplayer.Network;

namespace Multiplayer.Bridge.Tests
{
    public enum SimRole { Host, Client }

    /// <summary>
    /// One simulated participant: a real <see cref="NetworkEngine"/> bound to its own
    /// <see cref="InMemoryTransport"/> on the shared bus, plus a stable per-peer
    /// <see cref="PlayerGuid"/> (the permission/ownership key) and a headless apply
    /// <see cref="Sink"/> that <see cref="CounterAction"/> writes into.
    /// </summary>
    public sealed class SimPeer
    {
        public readonly NetworkEngine Engine = new NetworkEngine();
        public readonly InMemoryTransport Transport;
        public readonly SimRole Role;
        public readonly Guid PlayerGuid = Guid.NewGuid();
        public readonly PeerSink Sink = new PeerSink();
        public string Name;

        public SimPeer(InMemoryBus bus, SimRole role, string name)
        {
            Transport = new InMemoryTransport(bus);
            Role = role;
            Name = name;
        }

        public ulong PeerId => Transport.PeerId;

        /// <summary>Initialize the engine on the in-memory transport, then start host / join.</summary>
        public void Start()
        {
            Engine.Initialize(Transport);   // injection seam (NetworkEngine.Initialize(ITransport))
            if (Role == SimRole.Host) Engine.StartHost();
            else Engine.JoinGame("mem://host", 0);
        }
    }
}
