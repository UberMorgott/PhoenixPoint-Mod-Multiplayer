using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// Pure state/decision tests for the HOST-side blocking-prompt intent gate (ambush brief). Native semantics
/// under the fullscreen ambush prompt: NOTHING may happen until it resolves — so while armed, the host rejects
/// every in-flight client ActionRequest (client UI lock alone is raceable). The gate is a process-wide static;
/// each test starts from a clean Reset (xunit runs a class's facts sequentially).
/// </summary>
public class HostBlockingPromptGateTests
{
    public HostBlockingPromptGateTests() => HostBlockingPromptGate.Reset();

    private const int AmbushBrief = 15;   // ModalType.GeoAmbushBrief

    [Fact]
    public void Idle_NeverRejects()
    {
        Assert.False(HostBlockingPromptGate.IsArmed);
        Assert.False(HostBlockingPromptGate.ShouldRejectIntent(isHost: true, isActiveSession: true));
    }

    [Fact]
    public void Armed_RejectsOnActiveHost()
    {
        HostBlockingPromptGate.Arm(AmbushBrief);
        Assert.True(HostBlockingPromptGate.IsArmed);
        Assert.Equal(AmbushBrief, HostBlockingPromptGate.ArmedModalType);
        Assert.True(HostBlockingPromptGate.ShouldRejectIntent(isHost: true, isActiveSession: true));
    }

    [Theory]
    [InlineData(false, true)]    // not host (stale arm on an ex-host client) → never reject
    [InlineData(true, false)]    // session gone (stale arm past teardown) → never reject
    [InlineData(false, false)]
    public void Armed_OffHostOrNoSession_NeverRejects(bool isHost, bool isActiveSession)
    {
        HostBlockingPromptGate.Arm(AmbushBrief);
        Assert.False(HostBlockingPromptGate.ShouldRejectIntent(isHost, isActiveSession));
    }

    [Fact]
    public void Release_MatchingModal_Unblocks()
    {
        HostBlockingPromptGate.Arm(AmbushBrief);
        HostBlockingPromptGate.Release(AmbushBrief);
        Assert.False(HostBlockingPromptGate.IsArmed);
        Assert.False(HostBlockingPromptGate.ShouldRejectIntent(isHost: true, isActiveSession: true));
    }

    [Fact]
    public void Release_DifferentModal_StaysBlocked()
    {
        // A stray resolve of some OTHER modal must never release the ambush lock.
        HostBlockingPromptGate.Arm(AmbushBrief);
        HostBlockingPromptGate.Release(14);   // GeoResearchComplete closing elsewhere
        Assert.True(HostBlockingPromptGate.ShouldRejectIntent(isHost: true, isActiveSession: true));
    }

    [Fact]
    public void Rearm_IsIdempotent_SingleReleaseUnblocks()
    {
        // The same prompt re-arming (e.g. duplicate open postfix) must not need two releases.
        HostBlockingPromptGate.Arm(AmbushBrief);
        HostBlockingPromptGate.Arm(AmbushBrief);
        HostBlockingPromptGate.Release(AmbushBrief);
        Assert.False(HostBlockingPromptGate.IsArmed);
    }

    [Fact]
    public void Arm_NegativeModalType_Ignored()
    {
        HostBlockingPromptGate.Arm(-1);   // ModalType.None can never lock the relay
        Assert.False(HostBlockingPromptGate.IsArmed);
    }

    [Fact]
    public void Reset_DropsAnyArm()
    {
        // Boundary belt (save-transfer/reload via SyncEngine.ResetEventMirror): never inherit a stale arm.
        HostBlockingPromptGate.Arm(AmbushBrief);
        HostBlockingPromptGate.Reset();
        Assert.False(HostBlockingPromptGate.IsArmed);
        Assert.Equal(-1, HostBlockingPromptGate.ArmedModalType);
    }

    [Fact]
    public void ReleaseWhileIdle_IsNoOp()
    {
        HostBlockingPromptGate.Release(AmbushBrief);
        Assert.False(HostBlockingPromptGate.IsArmed);
    }
}
