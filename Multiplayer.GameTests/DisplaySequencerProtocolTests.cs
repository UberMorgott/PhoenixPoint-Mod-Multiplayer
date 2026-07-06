using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Batch-3 P4+P5 wire-format tests: the displaySeq/nativePriority stamp on 0x65 (flag bit4 trailing block),
/// the [occId:u16][displaySeq:u32] tail on 0x69, the optional occId on 0x6C, and the PlayCutsceneAction
/// trailing stamp — every one backward-tolerant (an unstamped wire stays byte-identical / a legacy payload
/// decodes to the 0 sentinels), plus the host stamp counters + one-slot stamp handoff.
/// </summary>
public class DisplaySequencerProtocolTests
{
    // ─── 0x65 EventRaised stamp block ────────────────────────────────────────────────────────────

    [Fact]
    public void EventRaised_Stamp_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeEventRaised(7, "EX20", 12, 3, null, singleChoice: true, oneWindow: false,
            wireTitle: "T", wireNarrative: "N", displaySeq: 42, nativePriority: 10);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occId, out var eventId, out var siteId,
            out var vehicleId, out _, out _, out var single, out var oneWin, out var title, out var narrative,
            out var seq, out var prio));
        Assert.Equal(7, occId);
        Assert.Equal("EX20", eventId);
        Assert.Equal(12, siteId);
        Assert.Equal(3, vehicleId);
        Assert.True(single);
        Assert.False(oneWin);
        Assert.Equal("T", title);
        Assert.Equal("N", narrative);
        Assert.Equal(42u, seq);
        Assert.Equal(10, prio);
    }

    [Fact]
    public void EventRaised_Unstamped_IsByteIdenticalToLegacyWire()
    {
        var legacy = SyncProtocol.EncodeEventRaised(7, "EX20", 12, 3, null, true, false, "T", "N");
        var stamped0 = SyncProtocol.EncodeEventRaised(7, "EX20", 12, 3, null, true, false, "T", "N", 0, 99);
        Assert.Equal(legacy, stamped0);   // seq 0 → no bit4, priority ignored → wire pin kept
    }

    [Fact]
    public void EventRaised_LegacyPayload_DecodesSeqZero()
    {
        var legacy = SyncProtocol.EncodeEventRaised(7, "EX20", 12, 3);
        Assert.True(SyncProtocol.TryDecodeEventRaised(legacy, out _, out _, out _, out _, out _, out _, out _,
            out _, out _, out _, out var seq, out var prio));
        Assert.Equal(0u, seq);
        Assert.Equal(0, prio);
    }

    [Fact]
    public void EventRaised_StampWithIdentityAndTexts_KeepsAllBlocks()
    {
        var identity = new GeoSiteState(5, "guid", 2, 1, "name", "enc", inspected: true, visible: true, visited: false);
        var bytes = SyncProtocol.EncodeEventRaised(9, "EV", 5, -1, identity, false, false, "Title", "", 1234567u, 15);
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out _, out _, out _, out _, out var hasIdentity,
            out var id2, out _, out _, out var title, out _, out var seq, out var prio));
        Assert.True(hasIdentity);
        Assert.Equal(5, id2.SiteId);
        Assert.Equal("Title", title);
        Assert.Equal(1234567u, seq);
        Assert.Equal(15, prio);
    }

    [Fact]
    public void EventRaised_OldDecoderShim_IgnoresTheStamp()
    {
        var bytes = SyncProtocol.EncodeEventRaised(7, "EX20", 12, 3, null, false, false, null, null, 42, 10);
        // The pre-Batch-3 10-out shim still decodes every leading field (the stamp block trails).
        Assert.True(SyncProtocol.TryDecodeEventRaised(bytes, out var occId, out var eventId, out var siteId,
            out var vehicleId, out _, out _, out _, out _, out _, out _));
        Assert.Equal(7, occId);
        Assert.Equal("EX20", eventId);
        Assert.Equal(12, siteId);
        Assert.Equal(3, vehicleId);
    }

    // ─── 0x69 ReportModal occId + displaySeq tail ────────────────────────────────────────────────

    [Fact]
    public void ReportModal_Batch3Tail_RoundTrips()
    {
        var p = new ReportModalPayload(14, ReportModalVariant.Research, -1, 100, 2, "RES1", null);
        p.OccId = 77;
        p.DisplaySeq = 424242;
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(14, d.ModalType);
        Assert.Equal("RES1", d.DefId);
        Assert.Equal(77, d.OccId);
        Assert.Equal(424242u, d.DisplaySeq);
    }

    [Fact]
    public void ReportModal_LegacyWire_DecodesTailZero()
    {
        var p = new ReportModalPayload(14, ReportModalVariant.Research, -1, 100, 2, "RES1", null);
        var bytes = SyncProtocol.EncodeReportModal(p);   // OccId/DisplaySeq default 0 → no tail (wire pin)
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(0, d.OccId);
        Assert.Equal(0u, d.DisplaySeq);
    }

    [Fact]
    public void ReportModal_MissionOutcomeTail_CoexistsWithBatch3Tail()
    {
        var p = new ReportModalPayload(5, ReportModalVariant.MissionOutcome, 12, 100, 0, "MDEF",
            new List<string> { "x" }, missionClass: 3, outcomeState: 3, rewardBlob: new byte[] { 1, 2, 3 });
        p.OccId = 9;
        p.DisplaySeq = 100000;
        var bytes = SyncProtocol.EncodeReportModal(p);
        Assert.True(SyncProtocol.TryDecodeReportModal(bytes, out var d));
        Assert.Equal(3, d.MissionClass);
        Assert.Equal(3, d.OutcomeState);
        Assert.Equal(new byte[] { 1, 2, 3 }, d.RewardBlob);
        Assert.Equal(9, d.OccId);
        Assert.Equal(100000u, d.DisplaySeq);
    }

    // ─── 0x6C ReportModalHide occId ──────────────────────────────────────────────────────────────

    [Fact]
    public void ReportModalHide_OccId_RoundTrips()
    {
        var bytes = SyncProtocol.EncodeReportModalHide(15, 300);
        Assert.Equal(3, bytes.Length);
        Assert.True(SyncProtocol.TryDecodeReportModalHide(bytes, out var modalType, out var occId));
        Assert.Equal(15, modalType);
        Assert.Equal(300, occId);
    }

    [Fact]
    public void ReportModalHide_LegacyOneByte_DecodesOccIdZero()
    {
        var legacy = SyncProtocol.EncodeReportModalHide(15);
        Assert.Single(legacy);
        Assert.True(SyncProtocol.TryDecodeReportModalHide(legacy, out var modalType, out var occId));
        Assert.Equal(15, modalType);
        Assert.Equal(0, occId);
        // occId 0 sentinel keeps the zero-stamp encode legacy-shaped too.
        Assert.Equal(legacy, SyncProtocol.EncodeReportModalHide(15, 0));
    }

    // ─── PlayCutsceneAction trailing stamp ───────────────────────────────────────────────────────

    private static byte[] WriteAction(PlayCutsceneAction a)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            a.Write(w);
            return ms.ToArray();
        }
    }

    [Fact]
    public void PlayCutscene_Stamp_RoundTrips()
    {
        var a = new PlayCutsceneAction("guid-1", 100, 55);
        var cs = (PlayCutsceneAction)PlayCutsceneAction.Read(
            new BinaryReader(new MemoryStream(WriteAction(a)), Encoding.UTF8));
        Assert.Equal("guid-1", cs.CutsceneGuid);
        Assert.Equal(100, cs.Priority);
        Assert.Equal(55u, cs.DisplaySeq);
    }

    [Fact]
    public void PlayCutscene_LegacyPayloadWithoutStamp_DecodesSeqZero()
    {
        byte[] legacy;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write("guid-1");   // the pre-Batch-3 wire: guid + priority only
            w.Write(100);
            legacy = ms.ToArray();
        }
        var cs = (PlayCutsceneAction)PlayCutsceneAction.Read(
            new BinaryReader(new MemoryStream(legacy), Encoding.UTF8));
        Assert.Equal("guid-1", cs.CutsceneGuid);
        Assert.Equal(100, cs.Priority);
        Assert.Equal(0u, cs.DisplaySeq);
    }

    // ─── host counters + one-slot stamp handoff ──────────────────────────────────────────────────

    [Fact]
    public void DisplaySequence_Counters_AreMonotonic_SkipZero_AndReset()
    {
        DisplaySequence.Reset();
        Assert.Equal(1u, DisplaySequence.NextSeq());
        Assert.Equal(2u, DisplaySequence.NextSeq());
        Assert.Equal(1, DisplaySequence.NextReportOccId());
        Assert.Equal(2, DisplaySequence.NextReportOccId());
        DisplaySequence.Reset();
        Assert.Equal(1u, DisplaySequence.NextSeq());
        Assert.Equal(1, DisplaySequence.NextReportOccId());
    }

    [Fact]
    public void DisplayStamp_TypeMatchedConsumeOnce()
    {
        DisplayStamp.Reset();
        DisplayStamp.Record("UIStateGeoscapeEvent", 5, 10);
        Assert.False(DisplayStamp.TryTake("UIStateGeoModal", out _, out _));   // type mismatch → left in place
        Assert.True(DisplayStamp.TryTake("GeoscapeEvent", out var seq, out var prio));
        Assert.Equal(5u, seq);
        Assert.Equal(10, prio);
        Assert.False(DisplayStamp.TryTake("GeoscapeEvent", out _, out _));     // consume-once
    }

    [Fact]
    public void DisplayStamp_MarketplaceEventState_MatchesTheEventFragment()
    {
        DisplayStamp.Reset();
        DisplayStamp.Record("UIStateMarketplaceGeoscapeEvent", 8, 15);
        Assert.True(DisplayStamp.TryTake("GeoscapeEvent", out var seq, out var prio));
        Assert.Equal(8u, seq);
        Assert.Equal(15, prio);
    }

    [Fact]
    public void DisplayStamp_OverwriteByNextPush_NeverStale()
    {
        DisplayStamp.Reset();
        DisplayStamp.Record("UIStateReplenish", 3, 0);      // non-mirrored push
        DisplayStamp.Record("UIStateGeoModal", 4, 100);     // the next (mirrored) push overwrites it
        Assert.True(DisplayStamp.TryTake("UIStateGeoModal", out var seq, out var prio));
        Assert.Equal(4u, seq);
        Assert.Equal(100, prio);
    }
}
