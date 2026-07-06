using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// PIN (rca-3 version-continuity decision): across a mid-session save-load / co-op save-transfer boundary
/// NEITHER side's version state resets — SYMMETRIC PERSISTENCE. The SyncEngine is not recreated on a
/// mid-session reload on EITHER side, so the host's monotonic counters (_walletVersion, _channelVersion,
/// _geoLiveSeq Next) keep counting and the client's last-seen guards (SequenceTracker, _geoLiveSeq marks)
/// keep their high-water marks; the strictly-greater guards then stay green by construction. Therefore
/// SyncEngine.ResetForReloadBoundary deliberately does NOT register any of them:
///   - resetting HOST counters (while any client's last-seen persists) would drop EVERY subsequent flush
///     ("Wallet sync dropped guard=stale-version") = total sync death;
///   - clearing CLIENT last-seen alone would re-apply a reliable-transport double-send straddling the
///     boundary (the transport sends every reliable packet twice).
/// A first-time on-demand joiner is covered by construction: its fresh engine starts all last-seen at 0,
/// and the host's persisted counters are strictly greater. If ResetForReloadBoundary ever grows a reset
/// for these, this pin must be consciously revisited.
/// </summary>
public class ReloadBoundaryVersionContinuityTests
{
    [Fact]
    public void Wallet_SymmetricPersistence_NextHostFlushStillApplies_StraddlingDuplicateStillDrops()
    {
        var clientLastSeen = new SequenceTracker();
        ulong hostWalletVersion = 0;

        // Pre-reload traffic: host flushed versions 1..5, client applied them all.
        for (int i = 0; i < 5; i++) clientLastSeen.MarkWallet(++hostWalletVersion);

        // ── reload boundary: NO reset on either side (the pinned decision) ──

        Assert.False(clientLastSeen.ShouldApplyWallet(5));          // straddling double-send of v5 still drops
        ulong next = ++hostWalletVersion;                           // host counter persisted → next flush is v6
        Assert.True(clientLastSeen.ShouldApplyWallet(next));        // continuity: post-reload flush applies
    }

    [Fact]
    public void Wallet_HostCounterRestartWouldWedgePersistedClient_WhyHostNeverResets()
    {
        var clientLastSeen = new SequenceTracker();
        clientLastSeen.MarkWallet(5);

        // If the host counter reset across the boundary while the client's last-seen persisted, every
        // subsequent flush (v1, v2, ...) would be dropped as stale forever — the exact asymmetry the
        // symmetric-persistence decision forbids.
        Assert.False(clientLastSeen.ShouldApplyWallet(1));
        Assert.False(clientLastSeen.ShouldApplyWallet(5));
    }

    [Fact]
    public void Channels_1Through10_SymmetricPersistence_PerChannelContinuityHolds()
    {
        var clientLastSeen = new SequenceTracker();
        var hostChannelVersion = new System.Collections.Generic.Dictionary<byte, ulong>();

        for (byte ch = 1; ch <= 10; ch++)
        {
            // Pre-reload: three flushes per channel, all applied.
            for (int i = 0; i < 3; i++)
            {
                hostChannelVersion.TryGetValue(ch, out var v);
                hostChannelVersion[ch] = ++v;
                clientLastSeen.MarkChannel(ch, v);
            }
        }

        // ── reload boundary: NO reset on either side ──

        for (byte ch = 1; ch <= 10; ch++)
        {
            Assert.False(clientLastSeen.ShouldApplyChannel(ch, 3));  // straddling duplicate still drops
            hostChannelVersion.TryGetValue(ch, out var v);
            Assert.True(clientLastSeen.ShouldApplyChannel(ch, v + 1)); // next host flush applies
        }
    }

    [Fact]
    public void SurfaceSeq_GeoOutcomeAndLiveMirrors_SymmetricPersistence()
    {
        // One shared SurfaceSeq instance per side (SyncEngine._geoLiveSeq): host authors Next, client
        // guards ShouldApply/Mark — per surface. Model both sides persisting across the boundary for
        // GeoOutcome (0xA3) + the live mirror surfaces GeoVehiclePos/Travel/Explore (0xA5/0xA6/0xA7).
        ushort[] surfaces = { SurfaceIds.GeoOutcome, SurfaceIds.GeoVehiclePos, SurfaceIds.GeoVehicleTravel, SurfaceIds.GeoVehicleExplore };
        var host = new SurfaceSeq();
        var client = new SurfaceSeq();

        foreach (var s in surfaces)
            for (int i = 0; i < 4; i++) client.Mark(s, host.Next(s));   // pre-reload: seq 1..4 applied

        // ── reload boundary: NO reset on either side ──

        foreach (var s in surfaces)
        {
            Assert.False(client.ShouldApply(s, 4));            // straddling duplicate still drops
            uint next = host.Next(s);                          // host stream persisted → 5
            Assert.Equal(5u, next);
            Assert.True(client.ShouldApply(s, next));          // continuity: post-reload emission applies
        }
    }

    [Fact]
    public void SurfaceSeq_ClientClearAloneWouldReapplyStraddlingDoubleSend_WhyClientKeepsItsMarks()
    {
        var client = new SurfaceSeq();
        client.Mark(SurfaceIds.GeoOutcome, 7);
        Assert.False(client.ShouldApply(SurfaceIds.GeoOutcome, 7));   // duplicate correctly dropped

        client.Reset();   // what a client-only boundary clear would do

        // The second copy of a reliable double-send straddling the boundary would now RE-APPLY a
        // non-idempotent action outcome (double manufacture) — why the client keeps its marks.
        Assert.True(client.ShouldApply(SurfaceIds.GeoOutcome, 7));
    }

    [Fact]
    public void FirstTimeJoiner_FreshLastSeenAcceptsPersistedHostCounters()
    {
        // On-demand join edge: the joiner's fresh engine has all last-seen at 0; the host's persisted
        // (high) counters are strictly greater → everything applies. No reset needed on either side.
        var joiner = new SequenceTracker();
        Assert.True(joiner.ShouldApplyWallet(4711));
        Assert.True(joiner.ShouldApplyChannel(6, 4711));

        var joinerSeq = new SurfaceSeq();
        Assert.True(joinerSeq.ShouldApply(SurfaceIds.GeoVehiclePos, 4711));
    }
}
