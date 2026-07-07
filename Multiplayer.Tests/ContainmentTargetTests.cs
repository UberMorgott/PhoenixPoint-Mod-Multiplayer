using System;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Xunit;

// Containment intent targeting (feat-containment-actions): pure ordinal+fingerprint resolution against
// the host's live _capturedUnits (the #10 full-set mirror preserves order), plus the wire-id pins for the
// two new containment intents (personnel 60-79 family, tombstone convention — never reuse a retired id).
public class ContainmentTargetTests
{
    private static readonly string[] Pool = { "arthron-def", "triton-def", "arthron-def", "chiron-def" };
    private static string GuidAt(int i) => Pool[i];

    [Fact]
    public void Resolve_OrdinalAndFingerprintMatch_ReturnsOrdinal()
        => Assert.Equal(1, ContainmentTarget.Resolve(Pool.Length, GuidAt, 1, "triton-def"));

    [Fact]
    public void Resolve_DuplicateTemplates_OrdinalHitWinsOverFirstScanHit()
        // Two same-template captives: an in-range ordinal whose fingerprint matches is preferred, so the
        // client kills exactly the slot it clicked (index 2, not the first arthron at index 0).
        => Assert.Equal(2, ContainmentTarget.Resolve(Pool.Length, GuidAt, 2, "arthron-def"));

    [Fact]
    public void Resolve_OrdinalDriftedAfterTrim_FallsBackToFingerprintScan()
        // Host capacity trim removed an earlier unit → the client's stale ordinal now points at a
        // different template → fall back to the first fingerprint match (same template ⇒ same yield).
        => Assert.Equal(3, ContainmentTarget.Resolve(Pool.Length, GuidAt, 1, "chiron-def"));

    [Fact]
    public void Resolve_OrdinalOutOfRange_StillResolvesByFingerprint()
        => Assert.Equal(1, ContainmentTarget.Resolve(Pool.Length, GuidAt, 17, "triton-def"));

    [Fact]
    public void Resolve_UnknownCaptive_ReturnsMinusOne_RejectAsNoOp()
        // The captive the client targeted is GONE on the authority (trimmed/killed) → -1: the host
        // rejects the intent as a logged no-op instead of killing a different-template unit.
        => Assert.Equal(-1, ContainmentTarget.Resolve(Pool.Length, GuidAt, 0, "queen-def"));

    [Fact]
    public void Resolve_EmptyPoolOrEmptyFingerprint_FailsClosed()
    {
        Assert.Equal(-1, ContainmentTarget.Resolve(0, GuidAt, 0, "arthron-def"));
        Assert.Equal(-1, ContainmentTarget.Resolve(Pool.Length, GuidAt, 0, null));
        Assert.Equal(-1, ContainmentTarget.Resolve(Pool.Length, GuidAt, 0, ""));
        Assert.Equal(-1, ContainmentTarget.Resolve(Pool.Length, null, 0, "arthron-def"));
    }

    // ─── wire-id pins ─────────────────────────────────────────────────────

    [Fact]
    public void ContainmentActionIds_PinnedInPersonnelBlock_AndMirrorSurfaceIds()
    {
        Assert.Equal((ushort)66, SyncedActionIds.KillCapturedUnit);
        Assert.Equal((ushort)67, SyncedActionIds.HarvestCapturedUnit);
        Assert.InRange(SyncedActionIds.KillCapturedUnit, (ushort)60, (ushort)79);
        Assert.InRange(SyncedActionIds.HarvestCapturedUnit, (ushort)60, (ushort)79);
        // Surface ids mirror the action ids (byte-stable migration — the PS4 convention).
        Assert.Equal(SyncedActionIds.KillCapturedUnit, SurfaceIds.KillCapturedUnit);
        Assert.Equal(SyncedActionIds.HarvestCapturedUnit, SurfaceIds.HarvestCapturedUnit);
    }

    [Fact]
    public void SyncedActionIds_AllUnique_NoCollisionWithExistingIntents()
    {
        var values = typeof(SyncedActionIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
            .Select(f => (ushort)f.GetRawConstantValue())
            .ToList();
        Assert.Equal(values.Count, values.Distinct().Count());   // any duplicate = silent action mis-route
    }
}
