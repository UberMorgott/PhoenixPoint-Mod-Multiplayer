using System.Collections.Generic;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Wire round-trip + drift-signature tests for the inventory state channel (#1) codec: per-def
/// (guid, count, charges) entries. Charges parity is the post-mission replenish desync fix — the
/// native pipeline leaves PARTIAL stacks in faction storage, so the mirror must carry per-stack
/// CurrentCharges. Only the pure encode/decode/signature path is exercised; Snapshot/Apply bind live
/// game types and are not unit-testable. Mirrors <see cref="UnlockChannelTests"/>.
/// </summary>
public class InventorySnapshotTests
{
    private static List<(string guid, int count, int charges)> RoundTrip(
        List<(string guid, int count, int charges)> items)
        => InventorySnapshot.Decode(InventorySnapshot.Encode(items));

    [Fact]
    public void Snapshot_RoundTrips_CountsAndCharges()
    {
        var items = new List<(string guid, int count, int charges)>
        {
            ("AmmoDef_Guid", 2, 3),     // partial stack: 1 full mag + top mag at 3 charges
            ("MedkitDef_Guid", 5, 10),  // full stack
            ("ArmourDef_Guid", 1, 0),   // chargeless def (ChargesMax=0 → CurrentCharges=0)
        };

        var rt = RoundTrip(items);

        Assert.NotNull(rt);
        Assert.Equal(items, rt);
    }

    [Fact]
    public void Snapshot_RoundTrips_Empty()
    {
        var rt = RoundTrip(new List<(string guid, int count, int charges)>());
        Assert.NotNull(rt);
        Assert.Empty(rt);
    }

    [Fact]
    public void Snapshot_RoundTrips_NegativeCharges()
    {
        // -1 = the "unreadable → keep ctor full-charges default" sentinel; must survive the wire.
        var rt = RoundTrip(new List<(string guid, int count, int charges)> { ("G", 1, -1) });
        Assert.Equal(new[] { ("G", 1, -1) }, rt);
    }

    [Fact]
    public void Encode_Null_ReturnsNull() => Assert.Null(InventorySnapshot.Encode(null));

    [Fact]
    public void Decode_Null_ReturnsNull() => Assert.Null(InventorySnapshot.Decode(null));

    [Fact]
    public void Decode_RejectsGarbage_ReturnsNullSafely()
    {
        // Claims an entry count but no payload follows.
        Assert.Null(InventorySnapshot.Decode(new byte[] { 0xFF }));
    }

    [Fact]
    public void Decode_RejectsTruncatedGuid_ReturnsNull()
    {
        // count=1, guidLen=5, but only 2 of the 5 guid bytes follow → rejected (not garbage).
        var truncated = new byte[]
        {
            0x01, 0x00,             // entry count = 1
            0x05, 0x00,             // guidLen = 5
            0x41, 0x42,             // only "AB" — 3 short
        };
        Assert.Null(InventorySnapshot.Decode(truncated));
    }

    [Fact]
    public void Decode_RejectsMissingCharges_ReturnsNull()
    {
        // Entry with guid + count but the i32 charges field cut off → rejected.
        var truncated = new byte[]
        {
            0x01, 0x00,             // entry count = 1
            0x01, 0x00, 0x41,       // guidLen=1, "A"
            0x02, 0x00, 0x00, 0x00, // count = 2
            // (charges missing)
        };
        Assert.Null(InventorySnapshot.Decode(truncated));
    }

    [Fact]
    public void Encode_StableWireBytes_Pinned()
    {
        // Pin the EXACT wire layout so an accidental format change is caught:
        // [u16 count]{[u16 guidLen][guid utf8][i32 count][i32 charges]}*.
        var bytes = InventorySnapshot.Encode(
            new List<(string guid, int count, int charges)> { ("AB", 2, 3) });

        var expected = new byte[]
        {
            0x01, 0x00,             // entry count = 1
            0x02, 0x00, 0x41, 0x42, // guidLen=2, "AB"
            0x02, 0x00, 0x00, 0x00, // count = 2
            0x03, 0x00, 0x00, 0x00, // charges = 3
        };
        Assert.Equal(expected, bytes);
    }

    // ─── drift signature (host poll backstop decision) ──────────────────────

    [Fact]
    public void Signature_Null_ReturnsNull()
        => Assert.Null(InventorySnapshot.Signature(null));

    [Fact]
    public void Signature_OrderInsensitive()
    {
        // Dictionary iteration order can shuffle on remove+re-add without content change —
        // that must NOT read as drift.
        var a = new List<(string guid, int count, int charges)> { ("G1", 2, 3), ("G2", 1, 7) };
        var b = new List<(string guid, int count, int charges)> { ("G2", 1, 7), ("G1", 2, 3) };
        Assert.Equal(InventorySnapshot.Signature(a), InventorySnapshot.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnCountDrift()
    {
        var a = new List<(string guid, int count, int charges)> { ("G1", 2, 3) };
        var b = new List<(string guid, int count, int charges)> { ("G1", 1, 3) };
        Assert.NotEqual(InventorySnapshot.Signature(a), InventorySnapshot.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnChargesDrift()
    {
        // The post-mission replenish case: same def, same count, PARTIAL top mag consumed.
        var a = new List<(string guid, int count, int charges)> { ("G1", 2, 30) };
        var b = new List<(string guid, int count, int charges)> { ("G1", 2, 27) };
        Assert.NotEqual(InventorySnapshot.Signature(a), InventorySnapshot.Signature(b));
    }

    [Fact]
    public void Signature_ChangesOnDefAppearedOrGone()
    {
        var a = new List<(string guid, int count, int charges)> { ("G1", 2, 3) };
        var b = new List<(string guid, int count, int charges)> { ("G1", 2, 3), ("G2", 1, 1) };
        Assert.NotEqual(InventorySnapshot.Signature(a), InventorySnapshot.Signature(b));
        Assert.NotEqual(InventorySnapshot.Signature(b), InventorySnapshot.Signature(new List<(string guid, int count, int charges)>()));
    }

    [Fact]
    public void Signature_EmptyStorage_IsStableNonNull()
    {
        var empty = InventorySnapshot.Signature(new List<(string guid, int count, int charges)>());
        Assert.NotNull(empty);
        Assert.Equal(empty, InventorySnapshot.Signature(new List<(string guid, int count, int charges)>()));
    }
}
