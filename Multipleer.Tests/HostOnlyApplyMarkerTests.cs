using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using Xunit;

/// <summary>
/// Guards the client-replay contract for host-driven completion actions (the double-reward CRIT class).
/// Only the <see cref="IHostOnlyApply"/> marker is unit-testable here — the actual Apply paths bind live
/// game types via reflection and cannot be JIT'd in the test process (same constraint noted in
/// <c>SyncActionSerializationTests</c>). The marker is what <c>SyncEngine.OnActionApply</c> reads to decide
/// whether the client suppresses the replay entirely.
///
/// Contract being locked in:
///   • ResearchCompletedAction  → IHostOnlyApply: client SUPPRESSES (state via ResearchChannel + resources
///                                 via wallet echo fully converge it; replaying CompleteResearch double-grants).
///   • AnswerEventAction        → IHostOnlyApply (regression guard for the prior event-answer fix).
///   • ManufactureCompletedAction → NOT IHostOnlyApply: client MUST run a reward-suppressed reduced apply
///                                 (queue removal only; item converges via InventoryChannel). The queue is
///                                 not channelled, so full suppression would leave a stale queue entry.
///   • FacilityCompletedAction  → NOT IHostOnlyApply: client MUST replay the purely-structural CompleteFacility
///                                 (no reward to double-apply; no facility channel to converge it otherwise).
/// </summary>
public class HostOnlyApplyMarkerTests
{
    [Fact]
    public void ResearchCompleted_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new ResearchCompletedAction("PX_LaserTech_ResearchDef"));

    [Fact]
    public void AnswerEvent_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new AnswerEventAction("PROG_PX0_Intro_Event", 0));

    [Fact]
    public void ManufactureCompleted_IsNotHostOnlyApply()
        => Assert.False(new ManufactureCompletedAction("itemguid", 3) is IHostOnlyApply);

    [Fact]
    public void FacilityCompleted_IsNotHostOnlyApply()
        => Assert.False(new FacilityCompletedAction("12", "4001", 5, 6) is IHostOnlyApply);
}
