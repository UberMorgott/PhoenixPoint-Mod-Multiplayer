using System.Collections.Generic;

namespace Multiplayer.Bridge.Tests
{
    /// <summary>
    /// Headless apply sink. <see cref="ISyncedAction.Apply(GeoRuntime)"/> must NOT touch the game
    /// (GeoRuntime is null/inert headless), so <see cref="CounterAction"/> writes into the peer
    /// currently being pumped. The cluster sets <see cref="CurrentPeer"/> right before driving a
    /// peer's Update(), and the single-threaded sequential pump makes this static handoff safe.
    /// </summary>
    public static class BridgeContext
    {
        public static SimPeer CurrentPeer;
    }

    /// <summary>Per-peer apply results that <see cref="CounterAction.Apply"/> mutates.</summary>
    public sealed class PeerSink
    {
        public long Counter;
        public int LastTag;
        /// <summary>One entry per ACTUAL apply (dropped stale/duplicate applies never appear here).</summary>
        public readonly List<int> AppliedTags = new List<int>();
    }
}
