using System;
using System.Collections.Generic;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reload-boundary reset aggregator (rca-3). The mid-session save-load / co-op save-transfer boundary
    /// (<c>SaveTransferCoordinator.PrepareEntryFromBlobCrt</c> — the SHARED host+client reload-entry hook,
    /// incl. the on-demand join path) must sweep EVERY in-flight engine-state holder that references the
    /// dying geoscape, because the <c>SyncEngine</c> is NOT recreated on a mid-session reload (only on full
    /// session teardown). This pure (BCL-only) aggregator makes that sweep a single audited list instead of
    /// scattered inline resets: <see cref="RunAll"/> invokes every registered resettable EXACTLY ONCE per
    /// run, in registration order, with per-entry exception isolation (one throwing reset never skips the
    /// rest — the <c>TacticalDeploySync.OnMissionExit</c> N1 idiom).
    ///
    /// Entry contract: each registered reset must itself be IDEMPOTENT and safe for a first-time on-demand
    /// joiner (empty state → no-op). What is deliberately NOT registered (version/nonce continuity across
    /// the boundary) is pinned by <c>ReloadBoundaryVersionContinuityTests</c>.
    /// </summary>
    public sealed class ReloadBoundaryReset
    {
        private readonly List<(string Name, Action Reset)> _entries = new List<(string, Action)>();

        /// <summary>Number of registered resettables (test/diag).</summary>
        public int Count => _entries.Count;

        /// <summary>Register one named resettable. A null reset is ignored (never crashes the sweep).</summary>
        public void Register(string name, Action reset)
        {
            if (reset == null) return;
            _entries.Add((name ?? string.Empty, reset));
        }

        /// <summary>Run every registered resettable exactly once, in registration order. A throwing entry is
        /// reported to <paramref name="onError"/> (if any) and the sweep CONTINUES with the next entry.</summary>
        public void RunAll(Action<string, Exception> onError = null)
        {
            foreach (var (name, reset) in _entries)
            {
                try { reset(); }
                catch (Exception ex)
                {
                    try { onError?.Invoke(name, ex); } catch { /* error sink must never break the sweep */ }
                }
            }
        }
    }
}
