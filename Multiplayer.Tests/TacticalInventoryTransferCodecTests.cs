using System.Collections.Generic;
using Multiplayer.Sync.Tactical;
using Xunit;
using Move = Multiplayer.Sync.Tactical.TacticalInventoryTransferCodec.Move;

/// <summary>
/// PURE wire tests for the mid-mission inventory-transfer codec (surfaces 0x9A <c>tac.intent.inventory</c> +
/// 0x9B <c>tac.inventory</c>). Covers:
///   (a) intent + apply round-trip — every move field (both endpoints, def guid, source index) + the header
///       (actingNetId/applyCost) + trailing nonce/seq survive byte-identically, including a multi-move batch,
///   (b) an EMPTY batch round-trips (count 0),
///   (c) truncation / a corrupt (over-cap) count → clean <c>false</c> (no partial accept),
///   (d) forward-tolerance: extra trailing bytes past the fixed trailer are ignored.
/// The engine glue (TacticalInventorySync capture/apply + the InventoryTransferPatches Harmony patch) binds game
/// types and is in-game verified.
/// </summary>
public class TacticalInventoryTransferCodecTests
{
    private static Move Mk(int s, byte ss, int d, byte ds, string g, int idx) => new Move(s, ss, d, ds, g, idx);

    // ─── (a) round-trip ─────────────────────────────────────────────────
    [Fact]
    public void Intent_MultiMove_RoundTrips()
    {
        var moves = new List<Move>
        {
            // crate → soldier backpack
            Mk(1001, TacticalInventoryTransferCodec.SlotInventory, 5, TacticalInventoryTransferCodec.SlotInventory, "medkit-guid", 0),
            // soldier equipments → crate (put back), 2nd item of that def
            Mk(5, TacticalInventoryTransferCodec.SlotEquipments, 1001, TacticalInventoryTransferCodec.SlotInventory, "ammo-guid", 1),
        };
        var bytes = TacticalInventoryTransferCodec.EncodeIntent(actingNetId: 5, applyCost: true, moves, nonce: 777u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeIntent(bytes, out var i));
        Assert.Equal(5, i.ActingNetId);
        Assert.True(i.ApplyCost);
        Assert.Equal(777u, i.Nonce);
        Assert.Equal(2, i.Moves.Count);

        Assert.Equal(1001, i.Moves[0].SrcNetId);
        Assert.Equal(TacticalInventoryTransferCodec.SlotInventory, i.Moves[0].SrcSlot);
        Assert.Equal(5, i.Moves[0].DstNetId);
        Assert.Equal(TacticalInventoryTransferCodec.SlotInventory, i.Moves[0].DstSlot);
        Assert.Equal("medkit-guid", i.Moves[0].ItemDefGuid);
        Assert.Equal(0, i.Moves[0].SrcDefIndex);

        Assert.Equal(5, i.Moves[1].SrcNetId);
        Assert.Equal(TacticalInventoryTransferCodec.SlotEquipments, i.Moves[1].SrcSlot);
        Assert.Equal(1001, i.Moves[1].DstNetId);
        Assert.Equal("ammo-guid", i.Moves[1].ItemDefGuid);
        Assert.Equal(1, i.Moves[1].SrcDefIndex);
    }

    [Fact]
    public void Intent_ApplyCostFalse_RoundTrips()
    {
        var bytes = TacticalInventoryTransferCodec.EncodeIntent(
            TacticalInventoryTransferCodec.NoActor, applyCost: false,
            new List<Move> { Mk(7, 0, 8, 1, "g", 3) }, nonce: 1u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeIntent(bytes, out var i));
        Assert.Equal(TacticalInventoryTransferCodec.NoActor, i.ActingNetId);
        Assert.False(i.ApplyCost);
        Assert.Single(i.Moves);
    }

    [Fact]
    public void Move_EmptyItemDefGuid_RoundTrips()
    {
        // A move whose def guid is "" (unreadable def defensively encoded) must survive the wire as "".
        var iBytes = TacticalInventoryTransferCodec.EncodeIntent(5, true, new List<Move> { Mk(1, 0, 2, 1, "", 0) }, nonce: 1u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeIntent(iBytes, out var i));
        Assert.Single(i.Moves);
        Assert.Equal("", i.Moves[0].ItemDefGuid);
        Assert.NotNull(i.Moves[0].ItemDefGuid);   // "" not null — the ctor coalesces null → ""

        var aBytes = TacticalInventoryTransferCodec.EncodeApply(new List<Move> { Mk(1, 0, 2, 1, "", 4) }, seq: 2u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(aBytes, out var a));
        Assert.Equal("", a.Moves[0].ItemDefGuid);
        Assert.Equal(4, a.Moves[0].SrcDefIndex);
    }

    [Fact]
    public void Apply_RoundTrips()
    {
        var moves = new List<Move> { Mk(1001, 0, 5, 1, "weapon-guid", 0) };
        var bytes = TacticalInventoryTransferCodec.EncodeApply(moves, seq: 42u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(bytes, out var a));
        Assert.Equal(42u, a.Seq);
        Assert.Single(a.Moves);
        Assert.Equal("weapon-guid", a.Moves[0].ItemDefGuid);
        Assert.Equal(TacticalInventoryTransferCodec.SlotEquipments, a.Moves[0].DstSlot);
    }

    // ─── (b) empty batch ────────────────────────────────────────────────
    [Fact]
    public void Intent_Empty_RoundTrips()
    {
        var bytes = TacticalInventoryTransferCodec.EncodeIntent(9, false, new List<Move>(), nonce: 3u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeIntent(bytes, out var i));
        Assert.Empty(i.Moves);
        Assert.Equal(3u, i.Nonce);
    }

    [Fact]
    public void Apply_Empty_RoundTrips()
    {
        var bytes = TacticalInventoryTransferCodec.EncodeApply(new List<Move>(), seq: 5u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(bytes, out var a));
        Assert.Empty(a.Moves);
        Assert.Equal(5u, a.Seq);
    }

    // ─── (c) truncation / corrupt count ─────────────────────────────────
    [Fact]
    public void Intent_Truncated_ReturnsFalse()
    {
        var bytes = TacticalInventoryTransferCodec.EncodeIntent(5, true,
            new List<Move> { Mk(1, 0, 2, 1, "g", 0) }, nonce: 1u);
        int tailLen = 2 + 2 * 1;                            // optional cell tail (tolerated when cut — see (e))
        var cut = new byte[bytes.Length - tailLen - 3];     // ALSO drop part of the trailing nonce → must fail
        System.Array.Copy(bytes, cut, cut.Length);
        Assert.False(TacticalInventoryTransferCodec.TryDecodeIntent(cut, out _));
    }

    [Fact]
    public void Apply_CorruptOverCapCount_ReturnsFalse()
    {
        // count = 0xFFFF (> MaxMoves) then a seq → must be rejected before allocating/looping.
        byte[] bytes = { 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00 };
        Assert.False(TacticalInventoryTransferCodec.TryDecodeApply(bytes, out _));
    }

    [Fact]
    public void Intent_Null_ReturnsFalse()
    {
        Assert.False(TacticalInventoryTransferCodec.TryDecodeIntent(null, out _));
        Assert.False(TacticalInventoryTransferCodec.TryDecodeApply(new byte[] { 0x00 }, out _));
    }

    // ─── (d) forward-tolerance ──────────────────────────────────────────
    [Fact]
    public void Apply_TrailingBytes_Ignored()
    {
        var moves = new List<Move> { Mk(1, 0, 2, 0, "g", 0) };
        var bytes = TacticalInventoryTransferCodec.EncodeApply(moves, seq: 9u);
        var extended = new byte[bytes.Length + 3];
        System.Array.Copy(bytes, extended, bytes.Length);   // 3 unknown future bytes appended past the seq
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(extended, out var a));
        Assert.Equal(9u, a.Seq);
        Assert.Single(a.Moves);
    }

    // ─── (e) destination UI-cell tail ───────────────────────────────────
    [Fact]
    public void CellTail_RoundTrips_OnIntentAndApply()
    {
        var moves = new List<Move>
        {
            new Move(1001, 0, 5, 0, "medkit-guid", 0, dstUiCell: 3),
            new Move(5, 1, 1001, 0, "ammo-guid", 1),               // cell not captured → -1
        };
        var iBytes = TacticalInventoryTransferCodec.EncodeIntent(5, true, moves, nonce: 7u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeIntent(iBytes, out var i));
        Assert.Equal(3, i.Moves[0].DstUiCell);
        Assert.Equal(-1, i.Moves[1].DstUiCell);
        Assert.Equal(7u, i.Nonce);

        var aBytes = TacticalInventoryTransferCodec.EncodeApply(moves, seq: 8u);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(aBytes, out var a));
        Assert.Equal(3, a.Moves[0].DstUiCell);
        Assert.Equal(-1, a.Moves[1].DstUiCell);
        Assert.Equal(8u, a.Seq);
    }

    [Fact]
    public void CellTail_AbsentOnOldPeerBytes_DefaultsMinusOne()
    {
        // Old-peer bytes = new bytes with the tail cut off right after the trailer (seq).
        var moves = new List<Move> { new Move(1, 0, 2, 0, "g", 0, dstUiCell: 5) };
        var bytes = TacticalInventoryTransferCodec.EncodeApply(moves, seq: 4u);
        int tailLen = 2 + 2 * moves.Count;                  // [count:u16] + count × i16
        var legacy = new byte[bytes.Length - tailLen];
        System.Array.Copy(bytes, legacy, legacy.Length);
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(legacy, out var a));
        Assert.Equal(4u, a.Seq);
        Assert.Equal(-1, a.Moves[0].DstUiCell);             // tail absent → unknown cell, decode still succeeds
    }

    [Fact]
    public void CellTail_CountMismatch_IgnoredCleanly()
    {
        var moves = new List<Move> { Mk(1, 0, 2, 0, "g", 0) };
        var bytes = TacticalInventoryTransferCodec.EncodeApply(moves, seq: 6u);
        bytes[bytes.Length - 4] = 9;                        // corrupt the tail count (u16 LE low byte)
        Assert.True(TacticalInventoryTransferCodec.TryDecodeApply(bytes, out var a));
        Assert.Equal(-1, a.Moves[0].DstUiCell);             // mismatched tail → ignored, never a decode failure
    }

    // ─── (f) reorder classification ─────────────────────────────────────
    [Fact]
    public void IsReorder_TrueOnlyForIdenticalEndpoints()
    {
        Assert.True(TacticalInventoryTransferCodec.IsReorder(new Move(5, 0, 5, 0, "g", 0, 2)));
        Assert.False(TacticalInventoryTransferCodec.IsReorder(new Move(5, 0, 5, 1, "g", 0)));   // backpack → ready
        Assert.False(TacticalInventoryTransferCodec.IsReorder(new Move(5, 0, 6, 0, "g", 0)));   // other soldier
    }
}
