using Multiplayer.Network.Sync.State;
using Xunit;
using Mode = Multiplayer.Network.Sync.State.StatSpendHarvest.Mode;

// Commit-seam HARVEST decision (stat-sync race RCA 2026-07-10): a ConflictRepaint is about to
// SetCharacterProgression the OPEN soldier's panel, which resets _current*Stat → _starting*Stat and would
// DISCARD the local pending allocation before the deferred CommitStatChanges seam relays it. So the pending
// spend is committed FIRST — the host natively, a co-op client by relaying a SpendStatPoints intent.
public class StatSpendHarvestTests
{
    // ── No active session: never harvest (single-player has no conflict repaints) ──

    [Fact]
    public void NoActiveSession_None_EvenWithDeltas()
    {
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(activeSession: false, isHost: false, pandoran: false, 3, 0, 0));
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(activeSession: false, isHost: true, pandoran: false, 3, 0, 0));
    }

    // ── No pending delta: nothing to commit (an ability-only pending edit lands here) ──

    [Fact]
    public void NoDelta_None_ClientAndHost()
    {
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(true, isHost: false, pandoran: false, 0, 0, 0));
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(true, isHost: true, pandoran: false, 0, 0, 0));
    }

    [Fact]
    public void NonPositiveDelta_None()
    {
        // Native clamps current ≤ starting, so a delta is never negative — but guard it anyway.
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(true, isHost: false, pandoran: false, -2, 0, 0));
    }

    // ── Client: relay positive deltas; mutoid is out of the SP intent family ──

    [Fact]
    public void Client_PositiveDelta_Relays()
    {
        Assert.Equal(Mode.ClientRelay, StatSpendHarvest.Decide(true, isHost: false, pandoran: false, 1, 0, 0));
        Assert.Equal(Mode.ClientRelay, StatSpendHarvest.Decide(true, isHost: false, pandoran: false, 0, 0, 5));
    }

    [Fact]
    public void Client_Pandoran_None_MutoidNotRelayed()
    {
        // Mutoid (mutagen-cost) progression: a client suppresses it without relay — discarded by the repaint.
        Assert.Equal(Mode.None, StatSpendHarvest.Decide(true, isHost: false, pandoran: true, 4, 2, 0));
    }

    // ── Host: still decides HostCommit; the caller now applies via re-priced SpendStatPoints (not native) ──

    [Fact]
    public void Host_PositiveDelta_Commits()
    {
        Assert.Equal(Mode.HostCommit, StatSpendHarvest.Decide(true, isHost: true, pandoran: false, 2, 0, 0));
    }

    [Fact]
    public void Host_Pandoran_StillCommits()
    {
        // On the host the mutoid path still decides HostCommit; the caller's SpendStatPoints apply skips it (no-op).
        Assert.Equal(Mode.HostCommit, StatSpendHarvest.Decide(true, isHost: true, pandoran: true, 3, 0, 0));
    }

    // ── Exhaustive: every input yields a defined mode (no gaps) ──

    [Fact]
    public void EveryInput_ProducesADefinedMode()
    {
        foreach (bool active in new[] { false, true })
            foreach (bool host in new[] { false, true })
                foreach (bool pand in new[] { false, true })
                    foreach (int d in new[] { -1, 0, 1 })
                    {
                        var m = StatSpendHarvest.Decide(active, host, pand, d, d, d);
                        Assert.Contains(m, new[] { Mode.None, Mode.HostCommit, Mode.ClientRelay });
                    }
    }
}
