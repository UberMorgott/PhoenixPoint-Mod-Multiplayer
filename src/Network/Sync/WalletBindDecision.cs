namespace Multipleer.Network.Sync
{
    /// <summary>What <see cref="WalletWatcher.Attach"/> must do this frame for the resolved wallet.</summary>
    public enum WalletBindAction
    {
        /// <summary>Wallet not resolvable yet (mid-load), or already bound to this exact instance — no-op.</summary>
        None,
        /// <summary>First bind (nothing currently bound) — subscribe the new wallet.</summary>
        Bind,
        /// <summary>A DIFFERENT wallet instance is live (e.g. after a save-reload created a fresh faction/Wallet)
        /// — unsubscribe the stale instance, then subscribe the new one.</summary>
        Rebind,
    }

    /// <summary>
    /// Pure, Unity-free rebind decision for <see cref="WalletWatcher"/>. Extracted so the
    /// host-only reflection/Unity glue stays out of the unit test: given the currently-resolved
    /// wallet and the currently-bound wallet, decide whether to no-op, first-bind, or rebind.
    /// Reference-identity only (a save-reload yields a fresh Wallet object → different reference).
    /// </summary>
    public static class WalletBindDecision
    {
        public static WalletBindAction Decide(object current, object bound)
        {
            if (current == null) return WalletBindAction.None;            // not in geoscape yet / mid-load
            if (ReferenceEquals(current, bound)) return WalletBindAction.None; // already bound to this instance
            if (bound == null) return WalletBindAction.Bind;             // first bind — nothing to unsubscribe
            return WalletBindAction.Rebind;                              // instance changed — drop stale, bind fresh
        }
    }
}
