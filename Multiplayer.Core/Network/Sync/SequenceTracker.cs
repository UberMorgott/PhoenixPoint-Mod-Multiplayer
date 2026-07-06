using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Last-writer-wins ordering for the currency echo + per-channel state echo. Drops old wallet
    /// versions and stale per-channel state versions. The host assigns strictly-increasing version
    /// numbers; clients apply in arrival order and discard anything not strictly newer.
    /// </summary>
    public sealed class SequenceTracker
    {
        private ulong _lastWallet;      // wallet versions
        // Per-channel last-applied version (StateChannel infra). Independent monotonic series per id.
        private readonly Dictionary<byte, ulong> _lastChannel = new Dictionary<byte, ulong>();

        public bool ShouldApplyWallet(ulong ver) => ver > _lastWallet;
        public void MarkWallet(ulong ver) { if (ver > _lastWallet) _lastWallet = ver; }

        public bool ShouldApplyChannel(byte channelId, ulong ver)
            => !_lastChannel.TryGetValue(channelId, out var last) || ver > last;

        public void MarkChannel(byte channelId, ulong ver)
        {
            if (!_lastChannel.TryGetValue(channelId, out var last) || ver > last)
                _lastChannel[channelId] = ver;
        }
    }
}
