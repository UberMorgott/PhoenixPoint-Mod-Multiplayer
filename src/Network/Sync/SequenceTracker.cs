namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Last-writer-wins ordering for the action relay + currency echo. Drops stale/duplicate
    /// action sequences and old wallet versions. The host assigns strictly-increasing sequence /
    /// version numbers; clients apply in arrival order and discard anything not strictly newer.
    /// </summary>
    public sealed class SequenceTracker
    {
        private ulong _lastApplied;     // actions
        private ulong _lastWallet;      // wallet versions

        public bool ShouldApply(ulong seq) => seq > _lastApplied;
        public void Mark(ulong seq) { if (seq > _lastApplied) _lastApplied = seq; }

        public bool ShouldApplyWallet(ulong ver) => ver > _lastWallet;
        public void MarkWallet(ulong ver) { if (ver > _lastWallet) _lastWallet = ver; }
    }
}
