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
///   • StartResearchAction      → IHostOnlyApply: ResearchChannel is the single client source of truth for a
///                                 start; suppressing the action replay (it stays the client->host request
///                                 trigger) leaves ONE channel-driven refresh, killing the Available-list flicker.
///   • CancelResearchAction     → IHostOnlyApply: same single-source rule for cancel; the client converges via
///                                 ch2 only, never double-applying the cancel via both action replay and channel.
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
    public void StartResearch_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new StartResearchAction("PX_LaserTech_ResearchDef"));

    [Fact]
    public void CancelResearch_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new CancelResearchAction("PX_LaserTech_ResearchDef"));

    [Fact]
    public void AnswerEvent_IsHostOnlyApply()
        => Assert.IsAssignableFrom<IHostOnlyApply>(new AnswerEventAction(1, "PROG_PX0_Intro_Event", 0));

    [Fact]
    public void AnswerEvent_IsResolvesOutsideScope()
        // The host's authoritative CompleteEvent must run OUTSIDE SyncApplyScope so its
        // CompleteEventDismissPatch.Postfix (which early-returns under SyncApplyScope.IsApplying) still
        // broadcasts the EventDismiss the clients render. The generic OnActionRequest honors this marker.
        => Assert.IsAssignableFrom<IResolvesOutsideScope>(new AnswerEventAction(1, "PROG_PX0_Intro_Event", 0));

    [Fact]
    public void ResearchCompleted_IsNotResolvesOutsideScope()
        // Regression guard: only event-answer resolution opts out of SyncApplyScope; the channelled
        // research/manufacture/facility actions stay inside the scope (interceptor pass-through).
        => Assert.IsNotAssignableFrom<IResolvesOutsideScope>(new ResearchCompletedAction("PX_LaserTech_ResearchDef"));

    [Fact]
    public void ManufactureCompleted_IsNotHostOnlyApply()
        => Assert.IsNotAssignableFrom<IHostOnlyApply>(new ManufactureCompletedAction("itemguid", 3));

    [Fact]
    public void FacilityCompleted_IsNotHostOnlyApply()
        => Assert.IsNotAssignableFrom<IHostOnlyApply>(new FacilityCompletedAction("12", "4001", 5, 6));
}
