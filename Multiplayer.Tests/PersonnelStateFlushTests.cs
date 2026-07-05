using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Network.Sync.State;
using Xunit;

// PS2 host flush core (pure): blob-hash skip (unchanged soldier → zero bytes), dirty coalesce
// (dup ids → one emit), per-flush byte budget with defer-stays-dirty, serialize-failure degrade
// (Failed, NOT deferred — a dead id must not spin the drain loop), oversize drop. The game glue
// (PersonnelChannel Snapshot drain, PersonnelBlob serializer round-trip) is game-bound and
// in-game-gated, mirroring how TacticalDeploySync's serializer path is tested (pure cores only).
public class PersonnelStateFlushTests
{
    private static Dictionary<long, byte[]> Blobs(params (long id, byte[] blob)[] entries)
        => entries.ToDictionary(e => e.id, e => e.blob);

    private static PersonnelStateFlush.Result Run(
        IEnumerable<long> ids, Dictionary<long, byte[]> blobs,
        Dictionary<long, ulong> lastSent, int budget = 24 * 1024)
        => PersonnelStateFlush.Run(ids, id => blobs.TryGetValue(id, out var b) ? b : null, lastSent, budget);

    [Fact]
    public void FirstEmit_ThenHashSkip_UnchangedSoldierShipsZeroRecords()
    {
        var blobs = Blobs((1, new byte[] { 1, 2, 3 }));
        var lastSent = new Dictionary<long, ulong>();

        var first = Run(new long[] { 1 }, blobs, lastSent);
        Assert.Single(first.Emit);
        Assert.Equal(1, first.Emit[0].UnitId);

        var second = Run(new long[] { 1 }, blobs, lastSent);   // re-marked but byte-identical
        Assert.Empty(second.Emit);
        Assert.Equal(1, second.SkippedUnchanged);
    }

    [Fact]
    public void ChangedBlob_EmitsAgainAndRestampsHash()
    {
        var blobs = Blobs((1, new byte[] { 1, 2, 3 }));
        var lastSent = new Dictionary<long, ulong>();
        Run(new long[] { 1 }, blobs, lastSent);

        blobs[1] = new byte[] { 1, 2, 4 };   // host healed the soldier → new bytes
        var res = Run(new long[] { 1 }, blobs, lastSent);

        Assert.Single(res.Emit);
        Assert.Equal(new byte[] { 1, 2, 4 }, res.Emit[0].Blob);
        Assert.Empty(Run(new long[] { 1 }, blobs, lastSent).Emit);   // and skips once stable again
    }

    [Fact]
    public void DuplicateAndZeroIds_CoalesceToSingleEmit()
    {
        // The hourly sweep + a direct seam can both mark one soldier in the same tick; 0 = None sentinel.
        var blobs = Blobs((1, new byte[] { 7 }));
        var res = Run(new long[] { 0, 1, 1, 1, 0 }, blobs, new Dictionary<long, ulong>());
        Assert.Single(res.Emit);
    }

    [Fact]
    public void SerializeFailure_CountsFailed_NotDeferred_KeepsOldHash()
    {
        var lastSent = new Dictionary<long, ulong>();
        var blobs = Blobs((1, new byte[] { 1 }));
        Run(new long[] { 1 }, blobs, lastSent);
        ulong stamped = lastSent[1];

        var res = Run(new long[] { 1, 2 }, Blobs((2, null)), lastSent);   // 1 vanished, 2 unserializable

        Assert.Empty(res.Emit);
        Assert.Equal(2, res.Failed);
        Assert.Empty(res.Deferred);            // dead ids must not spin the drain loop
        Assert.Equal(stamped, lastSent[1]);    // hash untouched → next real blob still diffs correctly
    }

    [Fact]
    public void BudgetOverflow_DefersRemainderWithoutStampingHash()
    {
        // Three 100-byte blobs, budget fits two records (100 + 15 frame overhead each).
        var blobs = Blobs((1, new byte[100]), (2, new byte[100]), (3, new byte[100]));
        blobs[1][0] = 1; blobs[2][0] = 2; blobs[3][0] = 3;
        var lastSent = new Dictionary<long, ulong>();

        var res = Run(new long[] { 1, 2, 3 }, blobs, lastSent, budget: 2 * (100 + PersonnelStateFlush.RecordOverheadBytes));

        Assert.Equal(2, res.Emit.Count);
        Assert.Equal(new long[] { 3 }, res.Deferred);
        Assert.False(lastSent.ContainsKey(3));   // deferred = still dirty, re-blobbed next flush

        var drain = Run(res.Deferred, blobs, lastSent, budget: 2 * (100 + PersonnelStateFlush.RecordOverheadBytes));
        Assert.Single(drain.Emit);
        Assert.Equal(3, drain.Emit[0].UnitId);
    }

    [Fact]
    public void EmitAtLeastOne_SingleBlobLargerThanBudgetStillShips()
    {
        // A lone 5 KB blob against a 1 KB budget must ship alone, else it starves forever.
        var res = Run(new long[] { 1 }, Blobs((1, new byte[5000])), new Dictionary<long, ulong>(), budget: 1024);
        Assert.Single(res.Emit);
    }

    [Fact]
    public void OversizeBlob_DroppedNotDeferred()
    {
        // Beyond the u16 record frame (MaxBlobWireBytes): unshippable → dropped (caller logs), the
        // rest of the flush unaffected.
        var blobs = Blobs((1, new byte[PersonnelStateFlush.MaxBlobWireBytes + 1]), (2, new byte[] { 1 }));
        var res = Run(new long[] { 1, 2 }, blobs, new Dictionary<long, ulong>(), budget: 512 * 1024);

        Assert.Equal(1, res.Oversized);
        Assert.Empty(res.Deferred);
        Assert.Single(res.Emit);
        Assert.Equal(2, res.Emit[0].UnitId);
    }

    [Fact]
    public void Hash_StableAndSensitiveToAnyByte()
    {
        var a = PersonnelStateFlush.Hash(new byte[] { 1, 2, 3 });
        Assert.Equal(a, PersonnelStateFlush.Hash(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(a, PersonnelStateFlush.Hash(new byte[] { 1, 2, 4 }));
        Assert.NotEqual(a, PersonnelStateFlush.Hash(new byte[] { 1, 2 }));
    }
}
