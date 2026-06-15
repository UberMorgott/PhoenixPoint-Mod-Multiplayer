using System;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Host-only: subscribes to the player <c>Wallet.ResourcesChanged</c> and marks the
    /// <see cref="SyncEngine"/> wallet dirty (coalesced flush in <see cref="SyncEngine.Tick"/>).
    /// Also pushes a full wallet snapshot when the wallet first becomes available (geoscape active)
    /// and whenever a (new) wallet instance is bound. Idempotent: <see cref="Attach"/> is safe to
    /// call every frame — it no-ops until the wallet exists and once it is already bound.
    /// </summary>
    public static class WalletWatcher
    {
        private static Delegate _handler;
        private static object _wallet;
        private static bool _bound;

        public static void Attach(NetworkEngine engine)
        {
            if (_bound) return;                               // cheap per-frame hot-path short-circuit; skips the heavy Wallet() reflection once bound
            if (engine == null || !engine.IsHost) return;
            var wallet = GeoRuntime.Instance.Wallet();
            if (wallet == null) return;                       // not in geoscape yet / mid-load; leave unbound so we keep probing
            if (ReferenceEquals(wallet, _wallet)) return;     // already bound to this instance

            Detach();                                         // drop any stale binding
            _wallet = wallet;
            _handler = WalletReflection.SubscribeResourcesChanged(
                wallet, () => NetworkEngine.Instance?.Sync?.MarkWalletDirty());
            // Seed clients with the authoritative wallet the moment we bind.
            engine.Sync?.BroadcastFullWallet();
            _bound = true;                                    // only after wallet resolved AND handler subscribed
        }

        public static void Detach()
        {
            if (_wallet != null && _handler != null)
                WalletReflection.UnsubscribeResourcesChanged(_wallet, _handler);
            _wallet = null;
            _handler = null;
            _bound = false;                                   // allow a new session/campaign to re-bind
        }
    }
}
