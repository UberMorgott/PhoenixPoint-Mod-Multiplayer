using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure guards for the research-nav-line mirror (the HOST/CLIENT "new research available" button inversion,
/// soak 2026-07-05). The native bind recomputes ResearchElement.UnlocksResearches at show time; only the HOST's
/// recompute runs on authoritative state, so the client must mirror the host's answer — and the HOST must NEVER
/// be overridden (S1 host-transparency: "host keeps its native button").
/// </summary>
public class ResearchNavMirrorTests
{
    public ResearchNavMirrorTests() => ResearchNavMirror.Reset();

    // ── wire flag folding ────────────────────────────────────────────────
    [Fact]
    public void FlagFor_FoldsNativeVisibility()
    {
        Assert.Equal(ResearchNavMirror.NavShown, ResearchNavMirror.FlagFor(true));
        Assert.Equal(ResearchNavMirror.NavHidden, ResearchNavMirror.FlagFor(false));
    }

    // ── decision: the HOST is never overridden, whatever the flag ───────
    [Theory]
    [InlineData(ResearchNavMirror.NavUnknown)]
    [InlineData(ResearchNavMirror.NavHidden)]
    [InlineData(ResearchNavMirror.NavShown)]
    public void ShouldOverride_Host_NeverTrue(int navFlag)
        => Assert.False(ResearchNavMirror.ShouldOverride(isHost: true, isActiveSession: true, navFlag: navFlag));

    // ── decision: client applies only a DEFINITE host answer in an active session ──
    [Theory]
    [InlineData(true, ResearchNavMirror.NavHidden, true)]    // host says hidden → force hidden
    [InlineData(true, ResearchNavMirror.NavShown, true)]     // host says shown → force shown (client nav click stays wired)
    [InlineData(true, ResearchNavMirror.NavUnknown, false)]  // host read miss / legacy → stay native
    [InlineData(false, ResearchNavMirror.NavShown, false)]   // no session → stay native
    [InlineData(false, ResearchNavMirror.NavHidden, false)]
    public void ShouldOverride_Client_TruthTable(bool isActiveSession, int navFlag, bool expected)
        => Assert.Equal(expected, ResearchNavMirror.ShouldOverride(false, isActiveSession, navFlag));

    [Fact]
    public void NavVisible_OnlyShownIsVisible()
    {
        Assert.True(ResearchNavMirror.NavVisible(ResearchNavMirror.NavShown));
        Assert.False(ResearchNavMirror.NavVisible(ResearchNavMirror.NavHidden));
        Assert.False(ResearchNavMirror.NavVisible(ResearchNavMirror.NavUnknown));
    }

    // ── pending store: keyed by researchId, one-shot, unknown-proof ──────
    [Fact]
    public void ArmThenConsume_OneShot()
    {
        ResearchNavMirror.Arm("PR_ResearchX", ResearchNavMirror.NavShown);
        Assert.True(ResearchNavMirror.TryConsume("PR_ResearchX", out var flag));
        Assert.Equal(ResearchNavMirror.NavShown, flag);
        Assert.False(ResearchNavMirror.TryConsume("PR_ResearchX", out _));   // consumed → gone
    }

    [Fact]
    public void Consume_UnknownOrEmptyId_False()
    {
        ResearchNavMirror.Arm("PR_ResearchX", ResearchNavMirror.NavHidden);
        Assert.False(ResearchNavMirror.TryConsume("PR_OtherResearch", out _));
        Assert.False(ResearchNavMirror.TryConsume("", out _));
        Assert.False(ResearchNavMirror.TryConsume(null, out _));
        Assert.True(ResearchNavMirror.TryConsume("PR_ResearchX", out _));    // original untouched by misses
    }

    [Fact]
    public void Arm_UnknownFlagOrEmptyId_NotStored()
    {
        ResearchNavMirror.Arm("PR_ResearchX", ResearchNavMirror.NavUnknown);   // no definite answer → nothing to mirror
        ResearchNavMirror.Arm("", ResearchNavMirror.NavShown);
        ResearchNavMirror.Arm(null, ResearchNavMirror.NavShown);
        Assert.False(ResearchNavMirror.TryConsume("PR_ResearchX", out _));
        Assert.False(ResearchNavMirror.TryConsume("", out _));
    }

    [Fact]
    public void Arm_QueuedPopups_KeepIndependentAnswers()
    {
        // Two researches complete back-to-back (observed: AnuPriest then Berserker) — ids must not cross-wire.
        ResearchNavMirror.Arm("PR_A", ResearchNavMirror.NavHidden);
        ResearchNavMirror.Arm("PR_B", ResearchNavMirror.NavShown);
        Assert.True(ResearchNavMirror.TryConsume("PR_B", out var flagB));
        Assert.Equal(ResearchNavMirror.NavShown, flagB);
        Assert.True(ResearchNavMirror.TryConsume("PR_A", out var flagA));
        Assert.Equal(ResearchNavMirror.NavHidden, flagA);
    }

    [Fact]
    public void Rearm_SameId_LatestWins()
    {
        ResearchNavMirror.Arm("PR_A", ResearchNavMirror.NavHidden);
        ResearchNavMirror.Arm("PR_A", ResearchNavMirror.NavShown);
        Assert.True(ResearchNavMirror.TryConsume("PR_A", out var flag));
        Assert.Equal(ResearchNavMirror.NavShown, flag);
    }

    [Fact]
    public void Reset_DropsAllPending()
    {
        ResearchNavMirror.Arm("PR_A", ResearchNavMirror.NavShown);
        ResearchNavMirror.Reset();
        Assert.False(ResearchNavMirror.TryConsume("PR_A", out _));
    }

    // ── wire: the Research variant carries the flag in ShareLevel (no wire change) ──
    [Theory]
    [InlineData(ResearchNavMirror.NavUnknown)]
    [InlineData(ResearchNavMirror.NavHidden)]
    [InlineData(ResearchNavMirror.NavShown)]
    public void ResearchPayload_NavFlag_RoundTripsInShareLevel(int navFlag)
    {
        var p = new ReportModalPayload(14, ReportModalVariant.Research, -1, 99, navFlag, "PR_ResearchX", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(navFlag, d.ShareLevel);
        Assert.Equal("PR_ResearchX", d.DefId);
    }
}
