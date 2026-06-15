using System;
using System.IO;
using Multipleer.Network.Sync;

namespace Multipleer.Bridge.Tests
{
    /// <summary>
    /// Test-only <see cref="ISyncedAction"/>. Payload = (int delta, int tag). Category = Research so the
    /// permission scenarios exercise the real <see cref="PermissionGate"/>. Validate always passes.
    /// Apply NEVER touches the game/<paramref name="rt"/> (null headless): it writes into the
    /// per-peer <see cref="PeerSink"/> via <see cref="BridgeContext.CurrentPeer"/>, which the cluster
    /// sets right before pumping that peer. The seq is read from BridgeContext for the AppliedSeqs log.
    /// </summary>
    public sealed class CounterAction : ISyncedAction
    {
        public const ushort Id = 5000;

        private int _delta;
        private int _tag;

        public CounterAction() { }
        public CounterAction(int delta, int tag) { _delta = delta; _tag = tag; }

        public ushort ActionId => Id;
        public ActionCategory Category => ActionCategory.Research;

        public void Write(BinaryWriter w)
        {
            w.Write(_delta);
            w.Write(_tag);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new CounterAction(r.ReadInt32(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor) => true;

        public void Apply(GeoRuntime rt)
        {
            var peer = BridgeContext.CurrentPeer;
            if (peer == null) return;
            peer.Sink.Counter += _delta;
            peer.Sink.LastTag = _tag;
            // The wire seq is not exposed to Apply through ISyncedAction, so we record one entry per
            // ACTUAL apply invocation (tag). A stale/duplicate ActionApply is dropped upstream in
            // SyncEngine.OnActionApply (SequenceTracker), so it never reaches here → no extra entry.
            peer.Sink.AppliedTags.Add(_tag);
        }
    }
}
