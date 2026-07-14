using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE wire-codec tests for the throwable/consumable item-destroy rail (tac.item.destroy 0x9F). No engine types —
/// mirrors <see cref="TacticalEquipCodecTests"/>. Covers round-trip fidelity (typical / zero / edge / string-guid),
/// slot values, and truncation rejection.
/// </summary>
public class TacticalItemDestroyCodecTests
{
    [Fact]
    public void ItemDestroy_RoundTrips_Typical()
    {
        var bytes = TacticalLiveCodec.EncodeItemDestroy(seq: 4u, actorNetId: 8, slot: 1, itemDefGuid: "PX_HandGrenade_WeaponDef", defIndex: 2);
        Assert.True(TacticalLiveCodec.TryDecodeItemDestroy(bytes, out var o));
        Assert.Equal(4u, o.Seq);
        Assert.Equal(8, o.ActorNetId);
        Assert.Equal((byte)1, o.Slot);
        Assert.Equal("PX_HandGrenade_WeaponDef", o.ItemDefGuid);
        Assert.Equal(2, o.DefIndex);
    }

    [Fact]
    public void ItemDestroy_RoundTrips_BackpackSlotZeroIndex()
    {
        var bytes = TacticalLiveCodec.EncodeItemDestroy(0u, 0, 0, "Medkit", 0);
        Assert.True(TacticalLiveCodec.TryDecodeItemDestroy(bytes, out var o));
        Assert.Equal(0u, o.Seq);
        Assert.Equal(0, o.ActorNetId);
        Assert.Equal((byte)0, o.Slot);
        Assert.Equal("Medkit", o.ItemDefGuid);
        Assert.Equal(0, o.DefIndex);
    }

    [Fact]
    public void ItemDestroy_RoundTrips_EmptyGuid()
    {
        var bytes = TacticalLiveCodec.EncodeItemDestroy(1u, 5, 1, null, 3);
        Assert.True(TacticalLiveCodec.TryDecodeItemDestroy(bytes, out var o));
        Assert.Equal("", o.ItemDefGuid);   // null encodes as empty, round-trips as empty
        Assert.Equal(3, o.DefIndex);
    }

    [Fact]
    public void ItemDestroy_RoundTrips_LargeValues()
    {
        var bytes = TacticalLiveCodec.EncodeItemDestroy(uint.MaxValue, int.MaxValue, 1, "a-really-long-guid-string-value", int.MaxValue);
        Assert.True(TacticalLiveCodec.TryDecodeItemDestroy(bytes, out var o));
        Assert.Equal(uint.MaxValue, o.Seq);
        Assert.Equal(int.MaxValue, o.ActorNetId);
        Assert.Equal(int.MaxValue, o.DefIndex);
        Assert.Equal("a-really-long-guid-string-value", o.ItemDefGuid);
    }

    [Fact]
    public void ItemDestroy_RejectsTruncated()
    {
        Assert.False(TacticalLiveCodec.TryDecodeItemDestroy(new byte[] { 1, 2, 3 }, out _));
        Assert.False(TacticalLiveCodec.TryDecodeItemDestroy(null, out _));
        Assert.False(TacticalLiveCodec.TryDecodeItemDestroy(new byte[13], out _));   // one byte short of the minimum
    }
}
