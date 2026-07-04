using Multiplayer.Network.Sync;
using Xunit;

// Pure rebind decision behind WalletWatcher.Attach. The live watcher used to hard-gate on `if (_bound) return;`,
// so it bound the GeoPhoenixFaction Wallet ONCE and never rebound — a co-op save-reload creates a FRESH
// faction/Wallet instance, leaving the watcher subscribed to the dead wallet (reward grants stopped echoing).
// Decide() drives the new rebind-on-fresh-instance behavior: no-op when unchanged, rebind when the ref changes.
public class WalletBindDecisionTests
{
    [Fact]
    public void NullCurrent_NoOp()
    {
        // Wallet not resolvable yet (mid-load / pre-geoscape) — keep probing, do not touch the binding.
        Assert.Equal(WalletBindAction.None, WalletBindDecision.Decide(current: null, bound: null));
        Assert.Equal(WalletBindAction.None, WalletBindDecision.Decide(current: null, bound: new object()));
    }

    [Fact]
    public void FirstBind_WhenNothingBound()
    {
        var wallet = new object();
        Assert.Equal(WalletBindAction.Bind, WalletBindDecision.Decide(current: wallet, bound: null));
    }

    [Fact]
    public void SameInstance_NoOp()
    {
        var wallet = new object();
        // Already bound to this exact instance — must NOT re-subscribe (would double-subscribe / leak handlers).
        Assert.Equal(WalletBindAction.None, WalletBindDecision.Decide(current: wallet, bound: wallet));
    }

    [Fact]
    public void DifferentInstance_Rebind()
    {
        // The post-reload case: a fresh Wallet object replaces the bound one → unsubscribe stale, subscribe fresh.
        var oldWallet = new object();
        var newWallet = new object();
        Assert.Equal(WalletBindAction.Rebind, WalletBindDecision.Decide(current: newWallet, bound: oldWallet));
    }

    [Fact]
    public void EqualButNotSameReference_StillRebind()
    {
        // Reference-identity only: two distinct objects that compare "equal" by value are still a rebind, because
        // the native Wallet has no value-equality and a reload always yields a brand-new instance.
        var a = "WALLET";
        var b = new string("WALLET".ToCharArray()); // distinct reference, value-equal
        Assert.False(ReferenceEquals(a, b));
        Assert.Equal(WalletBindAction.Rebind, WalletBindDecision.Decide(current: b, bound: a));
    }
}
