using System.IO;
using System.Text;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Xunit;

/// <summary>
/// Wire round-trip tests for every Task 8-10 action payload (Write → bytes → Read). Only the pure
/// serialization path is exercised; Apply/Validate bind live game types and are not unit-testable.
/// </summary>
public class SyncActionSerializationTests
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
    public void Registration_RegistersAllEightReaders()
    {
        SyncRegistration.RegisterAll();
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.StartResearch));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ResearchCompleted));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.CancelResearch));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ReorderResearch));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.QueueManufacture));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ManufactureCompleted));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.ConstructFacility));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.RepairFacility));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.FacilityCompleted));
        Assert.True(SyncedActionRegistry.IsRegistered(SyncedActionIds.AnswerEvent));
    }

    [Fact]
    public void StartResearch_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new StartResearchAction("PX_LaserTech_ResearchDef"));
        Assert.IsType<StartResearchAction>(a);
        Assert.Equal(SyncedActionIds.StartResearch, a.ActionId);
        Assert.Equal(ActionCategory.Research, a.Category);
        // re-serialize and compare bytes to confirm the payload survived intact.
        Assert.Equal(Write(new StartResearchAction("PX_LaserTech_ResearchDef")), Write(a));
    }

    [Fact]
    public void ResearchCompleted_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new ResearchCompletedAction("PX_LaserTech_ResearchDef"));
        Assert.IsType<ResearchCompletedAction>(a);
        Assert.Equal(SyncedActionIds.ResearchCompleted, a.ActionId);
    }

    [Fact]
    public void CancelResearch_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new CancelResearchAction("PX_LaserTech_ResearchDef"));
        Assert.IsType<CancelResearchAction>(a);
        Assert.Equal(SyncedActionIds.CancelResearch, a.ActionId);
        Assert.Equal(ActionCategory.Research, a.Category);
        Assert.Equal(Write(new CancelResearchAction("PX_LaserTech_ResearchDef")), Write(a));
    }

    [Fact]
    public void QueueManufacture_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new QueueManufactureAction("PX_AssaultRifle_WeaponDef-guid"));
        Assert.IsType<QueueManufactureAction>(a);
        Assert.Equal(ActionCategory.Manufacturing, a.Category);
    }

    [Fact]
    public void ManufactureCompleted_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new ManufactureCompletedAction("itemguid", 3));
        Assert.IsType<ManufactureCompletedAction>(a);
        Assert.Equal(Write(new ManufactureCompletedAction("itemguid", 3)), Write(a));
    }

    [Fact]
    public void ConstructFacility_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new ConstructFacilityAction("12", "Facility_LivingQuarters-guid", 2, -1, 3));
        Assert.IsType<ConstructFacilityAction>(a);
        Assert.Equal(ActionCategory.BaseConstruction, a.Category);
        Assert.Equal(
            Write(new ConstructFacilityAction("12", "Facility_LivingQuarters-guid", 2, -1, 3)),
            Write(a));
    }

    [Fact]
    public void RepairFacility_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new RepairFacilityAction("12", "4001", 5, 6));
        Assert.IsType<RepairFacilityAction>(a);
        Assert.Equal(ActionCategory.BaseRepair, a.Category);
        Assert.Equal(Write(new RepairFacilityAction("12", "4001", 5, 6)), Write(a));
    }

    [Fact]
    public void FacilityCompleted_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        var a = RoundTrip(new FacilityCompletedAction("12", "4001", 5, 6));
        Assert.IsType<FacilityCompletedAction>(a);
        Assert.Equal(SyncedActionIds.FacilityCompleted, a.ActionId);
    }

    [Fact]
    public void AnswerEvent_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        // Wire = [occId:u16][eventId:string][choiceIndex:i32]: occId now rides the action wire (replacing the
        // bespoke ChoiceClaim transport) so the host resolves the LIVE event by occurrence id.
        var a = (AnswerEventAction)RoundTrip(new AnswerEventAction(4242, "PROG_PX0_Intro_Event", 2));
        Assert.IsType<AnswerEventAction>(a);
        Assert.Equal(ActionCategory.Dialogs, a.Category);
        Assert.Equal((ushort)4242, a.OccurrenceId);
        Assert.Equal(2, a.ChoiceIndex);
        Assert.Equal(Write(new AnswerEventAction(4242, "PROG_PX0_Intro_Event", 2)), Write(a));
    }

    [Fact]
    public void AnswerEvent_NegativeChoiceIndex_RoundTrips()
    {
        SyncRegistration.RegisterAll();
        // -1 = the null "decline" choice.
        var a = (AnswerEventAction)RoundTrip(new AnswerEventAction(7, "PROG_PX0_Intro_Event", -1));
        Assert.Equal((ushort)7, a.OccurrenceId);
        Assert.Equal(-1, a.ChoiceIndex);
        Assert.Equal(
            Write(new AnswerEventAction(7, "PROG_PX0_Intro_Event", -1)),
            Write(a));
    }

    [Fact]
    public void AnswerEvent_WireBytes_AreStable()
    {
        SyncRegistration.RegisterAll();
        // Pin the exact on-wire layout: [occId:u16 LE][len:7bit][utf8 eventId][choiceIndex:i32 LE].
        // occId 5 = 05 00; "AB" = 0x02 length prefix + 0x41 0x42; choiceIndex 2 = 02 00 00 00.
        var bytes = Write(new AnswerEventAction(5, "AB", 2));
        var expected = new byte[]
        {
            0x05, 0x00,              // occurrenceId = 5
            0x02, 0x41, 0x42,        // "AB"
            0x02, 0x00, 0x00, 0x00,  // choiceIndex = 2
        };
        Assert.Equal(expected, bytes);
    }
}
