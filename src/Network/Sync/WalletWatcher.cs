using System;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Host-only: subscribes to the player <c>Wallet.ResourcesChanged</c> and marks the
    /// <see cref="SyncEngine"/> wallet dirty (coalesced flush in <see cref="SyncEngine.Tick"/>).
    /// Also pushes a full wallet snapshot when the wallet first becomes available (geoscape active)
    /// and whenever a (new) wallet instance is bound. Idempotent: <see cref="Attach"/> is safe to
    /// call every frame — it no-ops until the wallet exists and while bound to the SAME instance, and
    /// rebinds (unsubscribe stale, subscribe fresh) when the live wallet instance changes, e.g. after a
    /// co-op save-reload spins up a fresh faction/Wallet.
    /// </summary>
    public static class WalletWatcher
    {
        private static Delegate _handler;
        private static object _wallet;   // null = unbound; non-null = the wallet instance we're subscribed to (sole binding state)

        public static void Attach(NetworkEngine engine)
        {
            if (engine == null || !engine.IsHost) return;
            // Resolve the LIVE wallet every frame (host-only reflection, cheap). A co-op save-reload builds a
            // FRESH GeoPhoenixFaction/Wallet with no mid-session Detach, so the old hard `if (_bound) return;`
            // gate left us subscribed to the now-dead wallet → reward grants stopped echoing. Decide off the
            // current-vs-bound instance instead: no-op when unchanged, REBIND (unsubscribe stale, subscribe
            // fresh) when the instance changed.
            var wallet = GeoRuntime.Instance.Wallet();
            var action = WalletBindDecision.Decide(wallet, _wallet);
            if (action == WalletBindAction.None) return;      // mid-load (null) OR already bound to this instance

            // Bind → Detach is a no-op (_wallet null); Rebind → Detach unsubscribes the stale wallet's handler
            // (symmetric AddEventHandler/RemoveEventHandler) before we subscribe the fresh instance — no leak,
            // no double-subscribe.
            Detach();
            _wallet = wallet;
            _handler = WalletReflection.SubscribeResourcesChanged(
                wallet, () => NetworkEngine.Instance?.Sync?.MarkWalletDirty());
            // DIAG (wallet rail): a (re)bind is rare — log which action ran and whether the ResourcesChanged
            // subscribe actually landed. A null handler = event path DEAD (poll backstop only). No behavior change.
            UnityEngine.Debug.Log("[Multipleer] Wallet watcher " + action
                + (_handler == null
                    ? " guard=subscribe-failed (ResourcesChanged event path DEAD; poll backstop only)"
                    : " — ResourcesChanged subscribed")
                + "; seeding full-wallet broadcast");
            // Seed clients with the authoritative wallet the moment we (re)bind.
            engine.Sync?.BroadcastFullWallet();
        }

        public static void Detach()
        {
            if (_wallet != null && _handler != null)
                WalletReflection.UnsubscribeResourcesChanged(_wallet, _handler);
            _wallet = null;                                   // unbound → next Attach re-binds (new session/campaign/reload)
            _handler = null;
        }
    }
}
