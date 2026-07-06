using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

/// <summary>
/// Facility DEMOLITION sync (fix: host demolish left a ghost facility on the client — no synced action
/// existed for <c>GeoPhoenixBase.RemoveFacility</c>). Pure wire round-trip + contract markers + the
/// wallet one-writer replay decision; Apply/Validate bind live game types (in-game verified, not
/// unit-testable here — idempotency lives in the null-resolve no-op inside <c>BaseReflection.Remove</c>).
/// </summary>
public class RemoveFacilityActionTests
{
    private static byte[] Write(ISyncedAction a)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            a.Write(w);
            w.Flush();
            return ms.ToArray();
        }
    }

    private static ISyncedAction RoundTrip(ISyncedAction a)
    {
        var bytes = Write(a);
        using (var ms = new MemoryStream(bytes))
        using (var r = new BinaryReader(ms, Encoding.UTF8))
            return SyncedActionRegistry.Read(a.ActionId, r);
    }

    [Fact]
    public void Registered_AfterRegisterAll()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.RemoveFacility));
    }

    [Fact]
    public void ActionId_Is23_OnTheBaseFamilyRail_AndSurfaceIdMirrors()
    {
        // Base family 20-29: 20 construct / 21 repair / 22 completed / 23 remove. Never reuse.
        Assert.Equal((ushort)23, SyncedActionIds.RemoveFacility);
        Assert.Equal(SyncedActionIds.RemoveFacility, new RemoveFacilityAction("12", "4001", 5, 6, true).ActionId);
        // SurfaceIds action constants mirror SyncedActionIds byte-for-byte (envelope-cutover invariant).
        Assert.Equal((byte)23, SurfaceIds.RemoveFacility);
    }

    [Fact]
    public void Category_IsBaseConstruction()
        => Assert.Equal(ActionCategory.BaseConstruction, new RemoveFacilityAction("12", "4001", 5, 6, true).Category);

    [Fact]
    public void RoundTrip_PreservesPayload_ScrapTrue()
    {
        SyncRegistration.RegisterAll();
        var original = new RemoveFacilityAction("12", "4001", 5, 6, true);
        var a = RoundTrip(original);
        Assert.IsType<RemoveFacilityAction>(a);
        Assert.True(((RemoveFacilityAction)a).Scrap);
        Assert.Equal(Write(original), Write(a));   // byte-identical re-serialize
    }

    [Fact]
    public void RoundTrip_PreservesPayload_ScrapFalse()
    {
        // scrap:false = the internal site-destroyed facility drain (no refund).
        SyncRegistration.RegisterAll();
        var original = new RemoveFacilityAction("12", "4001", 5, 6, false);
        var a = RoundTrip(original);
        Assert.IsType<RemoveFacilityAction>(a);
        Assert.False(((RemoveFacilityAction)a).Scrap);
        Assert.Equal(Write(original), Write(a));
    }

    [Fact]
    public void WireBytes_AreStable()
    {
        // Pin the exact on-wire layout:
        // [len:7bit][utf8 baseId] [len:7bit][utf8 facilityId] [gridX:i32 LE] [gridY:i32 LE] [scrap:u8].
        var bytes = Write(new RemoveFacilityAction("12", "7", 5, 6, true));
        var expected = new byte[]
        {
            0x02, 0x31, 0x32,        // "12"
            0x01, 0x37,              // "7"
            0x05, 0x00, 0x00, 0x00,  // gridX = 5
            0x06, 0x00, 0x00, 0x00,  // gridY = 6
            0x01,                    // scrap = true
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void NotHostOnlyApply_ClientMustReplayStructuralRemoval()
        // No facility state channel exists → suppressing the replay would leave a ghost facility on the
        // client (the original bug). Same contract as FacilityCompletedAction.
        => Assert.IsNotAssignableFrom<IHostOnlyApply>(new RemoveFacilityAction("12", "4001", 5, 6, true));

    [Fact]
    public void NotResolvesOutsideScope_ReplayRunsInsideSyncApplyScope()
        // The replay must run INSIDE SyncApplyScope so RemoveFacilityPatch passes it through
        // (no re-relay / no re-broadcast loop).
        => Assert.IsNotAssignableFrom<IResolvesOutsideScope>(new RemoveFacilityAction("12", "4001", 5, 6, true));

    [Theory]
    [InlineData(true, true, true)]    // authoritative host apply (client-relayed demolish) → refund
    [InlineData(true, false, false)]  // host apply of a no-refund drain → no refund
    [InlineData(false, true, false)]  // CLIENT replay: wallet one-writer → structural only, 0xA0 converges refund
    [InlineData(false, false, false)] // client replay of a drain → structural only
    public void ReplayScrap_OnlyAuthoritativeHostRefunds(bool isHost, bool wireScrap, bool expected)
        => Assert.Equal(expected, RemoveFacilityAction.ReplayScrap(isHost, wireScrap));
}
