using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure pins for the campaign-end sync (feat-campaign-end): the geoscape campaign conclusion notice on the
/// 0x69 report rail (NEW <see cref="ReportModalVariant.CampaignEnd"/> variant — WA-3 InterceptionNotice
/// precedent) + the synchronized client teardown ordering (notice-before-teardown). The reflection executors
/// (TriggerGameOver patch, native outro replay) cannot be JIT'd here — what IS unit-testable is every
/// decision that drives them: payload shape, wire roundtrip, sentinel isolation from the OpenModal
/// whitelist, host/client gates, and the pinned client step ORDER.
/// </summary>
public class CampaignEndFlowTests
{
    // ── payload shape ─────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(true, "PX_FACTION_GUID")]
    [InlineData(false, "ALN_FACTION_GUID")]
    public void BuildPayload_Shape(bool victory, string guid)
    {
        var p = CampaignEndFlow.BuildPayload(victory, guid);
        Assert.Equal(CampaignEndFlow.SentinelModalType, p.ModalType);
        Assert.Equal(ReportModalVariant.CampaignEnd, p.Variant);
        Assert.Equal(-1, p.SiteId);
        Assert.Equal(int.MaxValue, p.Priority);   // endgame surface priority (post-tac rail precedent)
        Assert.Equal(guid, p.DefId);
        Assert.Equal(victory, CampaignEndFlow.IsVictory(p.ShareLevel));
    }

    [Fact]
    public void BuildPayload_NullGuid_EncodesEmpty()
    {
        var p = CampaignEndFlow.BuildPayload(victory: false, victorFactionGuid: null);
        Assert.Equal("", p.DefId);   // ctor sanitizes — the wire never carries null
    }

    // ── wire roundtrip on the EXISTING 0x69 codec (no new packet family; the CampaignEnd variant uses
    // only the generic leading fields — no outcome tail is ever written for it) ──
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CampaignEnd_RoundTrips(bool victory)
    {
        var p = CampaignEndFlow.BuildPayload(victory, "VICTOR_FACTION_GUID");
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(CampaignEndFlow.SentinelModalType, d.ModalType);
        Assert.Equal(ReportModalVariant.CampaignEnd, d.Variant);
        Assert.Equal(victory, CampaignEndFlow.IsVictory(d.ShareLevel));
        Assert.Equal("VICTOR_FACTION_GUID", d.DefId);
        Assert.Empty(d.ExtraIds);
        Assert.Equal((byte)0, d.MissionClass);   // no MissionOutcome tail on this variant
        Assert.Empty(d.RewardBlob);
    }

    // ── sentinel isolation: the synthetic 255 must NEVER enter the OpenModal rail's classifications —
    // it is not a native ModalType, so the OpenModal chokepoint patches (suppress/broadcast/gate/lock/
    // hide) must never claim it. Extends the WA-3 whole-enum no-regress sweep to the sentinel. ──
    [Fact]
    public void Sentinel_IsOutsideEveryOpenModalClassification()
    {
        int s = CampaignEndFlow.SentinelModalType;
        Assert.False(ReportModalClassifier.IsReportModal(s));
        Assert.False(ReportModalClassifier.IsBlockingModal(s));
        Assert.False(ReportModalClassifier.IsMandatoryBrief(s));
        Assert.False(ReportModalClassifier.ShouldDeferHostBroadcast(s));
    }

    [Fact]
    public void CampaignEndVariant_NeverReplaysANativeOpenModal()
    {
        // Like InterceptionNotice: the client never pushes a UIStateGeoModal for it (it replays the native
        // outro / a notify prompt instead) — IsPersistent must stay false so no code path treats it as a
        // replayable persistent window.
        Assert.False(ReportModalClassifier.IsPersistent(ReportModalVariant.CampaignEnd));
        // And no native ModalType maps to the variant (it is built ONLY by CampaignEndFlow.BuildPayload).
        for (int modalType = -1; modalType <= 40; modalType++)
            Assert.NotEqual(ReportModalVariant.CampaignEnd, ReportModalClassifier.VariantFor(modalType));
    }

    // ── host broadcast gate (one-shot, authority-only, rail kill-switch respected) ──
    [Theory]
    [InlineData(true, true, true, false, false, true)]    // the ONE broadcasting combination
    [InlineData(false, true, true, false, false, false)]  // client never broadcasts
    [InlineData(true, false, true, false, false, false)]  // no session → vanilla
    [InlineData(true, true, false, false, false, false)]  // mirror rail off → vanilla (single kill-switch)
    [InlineData(true, true, true, true, false, false)]    // native latch already set → re-entrant no-op
    [InlineData(true, true, true, false, true, false)]    // engine replay → never re-broadcast
    public void HostShouldBroadcast_TruthTable(bool isHost, bool active, bool mirror,
                                               bool alreadyTriggered, bool applying, bool expected)
        => Assert.Equal(expected, CampaignEndFlow.HostShouldBroadcast(isHost, active, mirror,
                                                                      alreadyTriggered, applying));

    // ── client native-trigger suppress gate (pure mirror: only the host ends the campaign) ──
    [Theory]
    [InlineData(false, true, true, false, true)]    // client in an active session → suppress local ending
    [InlineData(false, true, true, true, false)]    // engine replay (our own outro replay) → native runs
    [InlineData(true, true, true, false, false)]    // host is the authority → native runs
    [InlineData(false, false, true, false, false)]  // no session → vanilla
    [InlineData(false, true, false, false, false)]  // mirror rail off → vanilla both directions
    public void ClientShouldSuppressNativeTrigger_TruthTable(bool isHost, bool active, bool mirror,
                                                             bool applying, bool expected)
        => Assert.Equal(expected, CampaignEndFlow.ClientShouldSuppressNativeTrigger(isHost, active,
                                                                                    mirror, applying));

    // ── teardown-ordering pins (the notice-before-teardown contract) ──────────────────────────────
    [Fact]
    public void ClientSteps_MidTactical_QueuesOnly()
    {
        // Queue-don't-drop (Batch-2 outcome-queue precedent): a client still in tactical defers the whole
        // reaction — including the latch pre-consume, so a host crash while we are still in tactical keeps
        // the F3 host-left menu return (otherwise the client would be stranded in a dead session).
        Assert.Equal(new[] { CampaignEndFlow.ClientStep.QueueUntilGeoscape },
                     CampaignEndFlow.ClientSteps(geoViewLive: false, outroReplayable: true));
        Assert.Equal(new[] { CampaignEndFlow.ClientStep.QueueUntilGeoscape },
                     CampaignEndFlow.ClientSteps(geoViewLive: false, outroReplayable: false));
    }

    [Fact]
    public void ClientSteps_Replayable_SuppressThenUnlockThenOutro()
        => Assert.Equal(new[]
           {
               CampaignEndFlow.ClientStep.SuppressHostLeaveNotice,
               CampaignEndFlow.ClientStep.ReleaseViewLocks,
               CampaignEndFlow.ClientStep.ReplayNativeOutro,
           },
           CampaignEndFlow.ClientSteps(geoViewLive: true, outroReplayable: true));

    [Fact]
    public void ClientSteps_Degraded_NoticeAlwaysPrecedesTeardown()
    {
        var steps = CampaignEndFlow.ClientSteps(geoViewLive: true, outroReplayable: false);
        Assert.Equal(new[]
        {
            CampaignEndFlow.ClientStep.SuppressHostLeaveNotice,
            CampaignEndFlow.ClientStep.ReleaseViewLocks,
            CampaignEndFlow.ClientStep.ShowEndNotice,
            CampaignEndFlow.ClientStep.ReturnToMainMenu,
        }, steps);
        // The explicit ordering invariants (survive any future re-plan):
        int notice = System.Array.IndexOf(steps, CampaignEndFlow.ClientStep.ShowEndNotice);
        int teardown = System.Array.IndexOf(steps, CampaignEndFlow.ClientStep.ReturnToMainMenu);
        Assert.True(notice >= 0 && teardown >= 0 && notice < teardown,
            "the end notice must be shown BEFORE the menu-return teardown");
        Assert.Equal(CampaignEndFlow.ClientStep.SuppressHostLeaveNotice, steps[0]);
    }

    [Fact]
    public void ClientSteps_LatchSuppression_AlwaysFirst_WheneverGeoscapeReacts()
    {
        // Whatever the rebuild fate, the F3 latch pre-consume leads: the host's own teardown can land any
        // moment after its notice, and a raced "Host ended the session" prompt over the outro is the bug.
        foreach (var replayable in new[] { true, false })
        {
            var steps = CampaignEndFlow.ClientSteps(geoViewLive: true, outroReplayable: replayable);
            Assert.Equal(CampaignEndFlow.ClientStep.SuppressHostLeaveNotice, steps[0]);
            Assert.True(System.Array.IndexOf(steps, CampaignEndFlow.ClientStep.ReleaseViewLocks) == 1,
                "view-locks release before any outro/notice display");
        }
    }

    // ── ShareLevel encoding pins ──
    [Fact]
    public void VictoryFlag_EncodesOnShareLevel()
    {
        Assert.Equal(CampaignEndFlow.ShareLevelVictory, CampaignEndFlow.BuildPayload(true, "").ShareLevel);
        Assert.Equal(CampaignEndFlow.ShareLevelDefeat, CampaignEndFlow.BuildPayload(false, "").ShareLevel);
        Assert.True(CampaignEndFlow.IsVictory(CampaignEndFlow.ShareLevelVictory));
        Assert.False(CampaignEndFlow.IsVictory(CampaignEndFlow.ShareLevelDefeat));
    }
}
