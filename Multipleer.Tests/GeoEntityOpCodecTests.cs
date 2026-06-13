using Multipleer.Network.CommandSync;
using Xunit;

public class GeoEntityOpCodecTests
{
    [Fact]
    public void VehicleCreated_RoundTrips_AllFields()
    {
        var src = new GeoEntityOp
        {
            OpType = GeoEntityOpType.VehicleCreated,
            DefGuid = "CSD_DEF_GUID", // ComponentSetDef guid (the create method's def arg), not GeoVehicleDef
            OwnerFactionGuid = "FAC_PHX_GUID",
            SiteId = 7,
            PosX = 1.5f, PosY = -2.25f, PosZ = 3.75f,
            EntityId = 3
        };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.VehicleCreated, back.OpType);
        Assert.Equal("CSD_DEF_GUID", back.DefGuid);
        Assert.Equal("FAC_PHX_GUID", back.OwnerFactionGuid);
        Assert.Equal(7, back.SiteId);
        Assert.Equal(1.5f, back.PosX);
        Assert.Equal(-2.25f, back.PosY);
        Assert.Equal(3.75f, back.PosZ);
        Assert.Equal(3, back.EntityId);
    }

    [Fact]
    public void VehicleRemoved_RoundTrips()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.VehicleRemoved, EntityId = 5 };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.VehicleRemoved, back.OpType);
        Assert.Equal(5, back.EntityId);
    }

    [Fact]
    public void SiteRemoved_RoundTrips()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.SiteRemoved, SiteId = 42 };
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal(GeoEntityOpType.SiteRemoved, back.OpType);
        Assert.Equal(42, back.SiteId);
    }

    [Fact]
    public void OpTypeBytes_AreStable()
    {
        Assert.Equal((byte)1, (byte)GeoEntityOpType.VehicleCreated);
        Assert.Equal((byte)2, (byte)GeoEntityOpType.VehicleRemoved);
        Assert.Equal((byte)3, (byte)GeoEntityOpType.SiteCreated);
        Assert.Equal((byte)4, (byte)GeoEntityOpType.SiteRemoved);
    }

    [Fact]
    public void NullStrings_EncodeAsEmpty_NoThrow()
    {
        var src = new GeoEntityOp { OpType = GeoEntityOpType.VehicleRemoved, EntityId = 1 };
        // DefGuid / OwnerFactionGuid left null on purpose.
        var back = GeoEntityOpCodec.Decode(GeoEntityOpCodec.Encode(src));
        Assert.Equal("", back.DefGuid);
        Assert.Equal("", back.OwnerFactionGuid);
    }
}
