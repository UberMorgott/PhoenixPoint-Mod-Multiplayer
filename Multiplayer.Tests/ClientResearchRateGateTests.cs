using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure-logic tests for the CLIENT research-RATE override (research ETA fix). The ETA the research UI
/// renders (Research.GetTotalTimeLeft = remaining cost / GetHourlyResearchProduction) divides by a rate
/// each peer computes LOCALLY from its own facility production — which diverges on the mirrored client,
/// so host and client showed DIFFERENT completion dates. The host replicates its effective rate in the
/// ch2 snapshot (v3 rate block); ClientResearchRate stores it and ClientResearchRatePatch (postfix,
/// Priority.Last so it runs after TFTV's Void Omen ×1.5 postfix and wins) overrides the native result.
/// These tests pin the store semantics + the override truth table the patch defers to.
/// </summary>
public class ClientResearchRateGateTests
{
    // ─── store: OnSnapshotApplied ───────────────────────────────────────────

    [Fact]
    public void Apply_StoresRate_WhenPresent()
    {
        ClientResearchRate.SyncedRate = null;
        ClientResearchRate.OnSnapshotApplied(37.5f);
        Assert.Equal(37.5f, ClientResearchRate.SyncedRate);
    }

    [Fact]
    public void Apply_KeepsLastRate_WhenPayloadCarriesNone()
    {
        // An old-host payload (no v3 rate block) must NOT clear the last known host rate — a
        // stale-but-real host rate beats falling back to the client's diverged local computation.
        ClientResearchRate.SyncedRate = 12f;
        ClientResearchRate.OnSnapshotApplied(null);
        Assert.Equal(12f, ClientResearchRate.SyncedRate);
    }

    [Fact]
    public void Apply_OverwritesPreviousRate()
    {
        ClientResearchRate.SyncedRate = 12f;
        ClientResearchRate.OnSnapshotApplied(48f);
        Assert.Equal(48f, ClientResearchRate.SyncedRate);
    }

    [Fact]
    public void Apply_StoresZeroRate()
    {
        // 0 is a legitimate host rate (no functioning labs) — distinct from "no value".
        ClientResearchRate.SyncedRate = null;
        ClientResearchRate.OnSnapshotApplied(0f);
        Assert.Equal(0f, ClientResearchRate.SyncedRate);
    }

    [Fact]
    public void Reset_ClearsSyncedRate()
    {
        // Engine teardown (NetworkEngine.Shutdown/TearDown) calls Reset so a fast client→client
        // reconnection can never apply the PREVIOUS session's rate before the new ch2 seed arrives.
        ClientResearchRate.SyncedRate = 99f;
        ClientResearchRate.Reset();
        Assert.Null(ClientResearchRate.SyncedRate);
    }

    // ─── gate: ShouldOverride truth table ───────────────────────────────────

    [Fact]
    public void Overrides_OnActiveClient_WithSyncedRate_ForLocalPhoenixResearch()
    {
        Assert.True(ClientResearchRate.ShouldOverride(
            engineExists: true, isActive: true, isHost: false,
            hasSyncedRate: true, isLocalPhoenixResearch: true));
    }

    [Fact]
    public void NoOverride_OnHost()
    {
        // Host computes the authoritative rate natively — the patch must be inert there.
        Assert.False(ClientResearchRate.ShouldOverride(
            engineExists: true, isActive: true, isHost: true,
            hasSyncedRate: true, isLocalPhoenixResearch: true));
    }

    [Fact]
    public void NoOverride_WithoutSyncedValue()
    {
        // Fresh join: never override before the first synced value arrives (HasValue guard).
        Assert.False(ClientResearchRate.ShouldOverride(
            engineExists: true, isActive: true, isHost: false,
            hasSyncedRate: false, isLocalPhoenixResearch: true));
    }

    [Fact]
    public void NoOverride_ForNonPhoenixResearchInstance()
    {
        // GetAlliesContribution calls the same method on ALLY Research instances — those must keep
        // their client-local rate (accepted edge; the synced rate is Phoenix-faction-only).
        Assert.False(ClientResearchRate.ShouldOverride(
            engineExists: true, isActive: true, isHost: false,
            hasSyncedRate: true, isLocalPhoenixResearch: false));
    }

    [Fact]
    public void NoOverride_InSinglePlayer()
    {
        Assert.False(ClientResearchRate.ShouldOverride(
            engineExists: false, isActive: false, isHost: false,
            hasSyncedRate: true, isLocalPhoenixResearch: true));
    }

    [Fact]
    public void NoOverride_WithoutActiveSession()
    {
        // Engine exists (menus) but no live co-op session → local computation is authoritative.
        Assert.False(ClientResearchRate.ShouldOverride(
            engineExists: true, isActive: false, isHost: false,
            hasSyncedRate: true, isLocalPhoenixResearch: true));
    }
}
