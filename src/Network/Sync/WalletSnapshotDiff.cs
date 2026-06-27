using System;
using System.Collections.Generic;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free) absolute wallet snapshot comparator. Backs the binding-independent
    /// snapshot-diff POLL in <see cref="SyncEngine.Tick"/>: did the live wallet drift from the last
    /// snapshot the host actually broadcast? The event path (<see cref="WalletWatcher"/> →
    /// <c>Wallet.ResourcesChanged</c> → <see cref="SyncEngine.MarkWalletDirty"/>) catches the common
    /// case instantly; this comparator is the convergence backstop for a host wallet change that
    /// misses <c>ResourcesChanged</c> or fires on a stale-bound instance (the binding has bitten us
    /// before). Compares the same 11-slot absolute snapshot shape <see cref="WalletApplier.Snapshot"/>
    /// produces; eps mirrors <see cref="WalletApplier"/>.
    /// </summary>
    public static class WalletSnapshotDiff
    {
        /// <summary>True if the wallet drifted from <paramref name="last"/> to <paramref name="current"/>:
        /// <paramref name="last"/> is null (never broadcast yet → must seed), the slot count differs, the
        /// type-set differs, or any slot's absolute value moved by more than <paramref name="eps"/>. A
        /// null <paramref name="current"/> counts as empty. Last-value-wins on a duplicated type (snapshots
        /// have none). PURE + Unity-free so the host poll decision is unit-tested.</summary>
        public static bool Changed(
            IReadOnlyList<(int type, float value)> last,
            IReadOnlyList<(int type, float value)> current,
            float eps = 0.0001f)
        {
            if (last == null) return true;                       // never broadcast yet → seed
            if (current == null) return last.Count != 0;         // wallet gone vs a non-empty last
            if (last.Count != current.Count) return true;        // a slot appeared/disappeared
            var lastByType = new Dictionary<int, float>(last.Count);
            foreach (var (t, v) in last) lastByType[t] = v;
            foreach (var (t, v) in current)
            {
                if (!lastByType.TryGetValue(t, out float lv)) return true;   // type-set differs
                if (Math.Abs(v - lv) > eps) return true;                     // slot drifted beyond eps
            }
            return false;
        }
    }
}
