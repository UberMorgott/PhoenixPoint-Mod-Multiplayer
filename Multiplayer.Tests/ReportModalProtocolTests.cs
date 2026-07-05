using System.Collections.Generic;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure round-trip codec tests for the Phase-A report-window mirror wire format
/// (<c>SyncProtocol.EncodeReportModal</c>/<c>TryDecodeReportModal</c>), one per <see cref="ReportModalVariant"/>.
/// Wire: [modalType:u8][variantTag:u8][siteId:i32][priority:i32][shareLevel:i32][defId:str][extraCount:u16][extras*].
/// </summary>
public class ReportModalProtocolTests
{
    [Fact]
    public void NullData_RoundTrips()
    {
        var p = new ReportModalPayload(6, ReportModalVariant.NullData, -1, 0, 0, "", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(6, d.ModalType);
        Assert.Equal(ReportModalVariant.NullData, d.Variant);
        Assert.Equal(-1, d.SiteId);
        Assert.Equal(0, d.Priority);
        Assert.Equal("", d.DefId);
        Assert.Empty(d.ExtraIds);
    }

    [Fact]
    public void SiteOnly_RoundTrips()
    {
        var p = new ReportModalPayload(25, ReportModalVariant.SiteOnly, 1234, 100, 0, "", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(25, d.ModalType);
        Assert.Equal(ReportModalVariant.SiteOnly, d.Variant);
        Assert.Equal(1234, d.SiteId);
        Assert.Equal(100, d.Priority);
    }

    [Fact]
    public void Research_RoundTrips()
    {
        var p = new ReportModalPayload(14, ReportModalVariant.Research, -1, 99, 0, "PR_ResearchX", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(14, d.ModalType);
        Assert.Equal(ReportModalVariant.Research, d.Variant);
        Assert.Equal("PR_ResearchX", d.DefId);
        Assert.Equal(99, d.Priority);
        Assert.Empty(d.ExtraIds);
    }

    [Fact]
    public void Diplomacy_RoundTrips()
    {
        var extras = new List<string> { "PR_R1", "PR_R2", "PR_R3" };
        var p = new ReportModalPayload(38, ReportModalVariant.Diplomacy, -1, 100, 4, "FACTION_GUID_ABC", extras);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(38, d.ModalType);
        Assert.Equal(ReportModalVariant.Diplomacy, d.Variant);
        Assert.Equal("FACTION_GUID_ABC", d.DefId);   // factionDefGuid folded into defId
        Assert.Equal(4, d.ShareLevel);
        Assert.Equal(100, d.Priority);
        Assert.Equal(extras, d.ExtraIds);
    }

    [Fact]
    public void AmbushBrief_RoundTrips()
    {
        // GeoAmbushBrief (15): siteId = mission.Site.SiteId, defId = mission.MissionDef.Guid, priority 0
        // (ShowMissionBriefing → OpenModalPersistent(missionBriefModal, mission, 0), GeoscapeView.cs:1903).
        var p = new ReportModalPayload(15, ReportModalVariant.AmbushBrief, 777, 0, 0, "AMBUSH_DEF_GUID", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(15, d.ModalType);
        Assert.Equal(ReportModalVariant.AmbushBrief, d.Variant);
        Assert.Equal(777, d.SiteId);
        Assert.Equal(0, d.Priority);
        Assert.Equal("AMBUSH_DEF_GUID", d.DefId);
        Assert.Empty(d.ExtraIds);
    }

    [Theory]
    [InlineData(4)]    // GeoScavengeBrief (resource-site "МЕСТНОСТЬ С РЕСУРСАМИ" deploy brief)
    [InlineData(26)]   // AncientSiteAttackBrief
    [InlineData(28)]   // AncientSiteDefenceBrief
    public void SiteMissionBrief_RoundTrips(int modalType)
    {
        // Same wire shape as AmbushBrief: siteId = mission.Site.SiteId, defId = mission.MissionDef.Guid,
        // priority 0 (ShowMissionBriefing → OpenModalPersistent(missionBriefModal, mission, 0), GeoscapeView.cs:1903).
        var p = new ReportModalPayload((byte)modalType, ReportModalVariant.SiteMissionBrief, 512, 0, 0, "MISSION_DEF_GUID", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(modalType, d.ModalType);
        Assert.Equal(ReportModalVariant.SiteMissionBrief, d.Variant);
        Assert.Equal(512, d.SiteId);
        Assert.Equal(0, d.Priority);
        Assert.Equal("MISSION_DEF_GUID", d.DefId);
        Assert.Empty(d.ExtraIds);
    }

    [Theory]
    [InlineData(0)]    // GeoHavenAttackBrief (haven defense — top user pain)
    [InlineData(2)]    // GeoAlienBaseBrief
    [InlineData(11)]   // GeoPhoenixBaseDefenseBrief (base attack)
    [InlineData(20)]   // GeoPhoenixBaseInfestationBrief
    [InlineData(34)]   // BehemothAttackBrief (fallback family)
    [InlineData(36)]   // InfestedHavenBrief
    public void ActiveMissionBrief_RoundTrips(int modalType)
    {
        // Batch-1 family: same wire shape as the other brief variants (siteId + missionDef guid, priority 0);
        // the runtime bits ride the P1 mission record on channel #5, not this packet.
        var p = new ReportModalPayload((byte)modalType, ReportModalVariant.ActiveMissionBrief, 90, 0, 0, "MISSION_DEF_GUID", null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(modalType, d.ModalType);
        Assert.Equal(ReportModalVariant.ActiveMissionBrief, d.Variant);
        Assert.Equal(90, d.SiteId);
        Assert.Equal(0, d.Priority);
        Assert.Equal("MISSION_DEF_GUID", d.DefId);
        Assert.Empty(d.ExtraIds);
    }

    // ── ReportModalHide (0x6C): the host resolved its BLOCKING modal → clients close the mirror ──
    [Fact]
    public void ReportModalHide_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeReportModalHide(15);
        Assert.True(SyncProtocol.TryDecodeReportModalHide(bytes, out var modalType));
        Assert.Equal(15, modalType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void ReportModalHide_EmptyOrNull_DecodesFalse(byte[] data)
        => Assert.False(SyncProtocol.TryDecodeReportModalHide(data, out _));

    [Fact]
    public void NullDefId_EncodesEmpty()
    {
        var p = new ReportModalPayload(6, ReportModalVariant.NullData, -1, 0, 0, null, null);
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal("", d.DefId);
        Assert.NotNull(d.ExtraIds);
        Assert.Empty(d.ExtraIds);
    }

    [Fact]
    public void UndefinedVariantByte_DecodesFalse()
    {
        // Forward-compat: an unknown variant tag is a graceful drop, never a throw.
        var bytes = new byte[]
        {
            6,                       // modalType
            0x7F,                    // variantTag = 127 (undefined)
            0xFF, 0xFF, 0xFF, 0xFF,  // siteId = -1
            0x00, 0x00, 0x00, 0x00,  // priority = 0
            0x00, 0x00, 0x00, 0x00,  // shareLevel = 0
            0x00, 0x00,              // defId "" (u16 len = 0)
            0x00, 0x00,              // extraCount = 0
        };
        Assert.False(SyncProtocol.TryDecodeReportModal(bytes, out _));
    }

    [Fact]
    public void Truncated_DecodesFalse()
    {
        Assert.False(SyncProtocol.TryDecodeReportModal(new byte[] { 6 }, out _));
    }
}
