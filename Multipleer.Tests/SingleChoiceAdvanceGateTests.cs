using Multipleer.Harmony.Sync;
using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// Pure decision gates behind the CLIENT→HOST single-choice PROMPT advance relay (first-wins, host-authoritative).
///
/// Bug (in-game confirmed): a CLIENT OK click on a single-choice prompt mirror only closed the client's own
/// modal (EncounterChoiceClientPatch localClose branch) — the HOST's prompt stayed open until the host clicked.
/// Fix: the client click ALSO sends an EventAdvanceRequest (0x6B, EventDismiss codec) to the host; the host
/// drives its own open native modal through OnChoiceSelected exactly as a local click would, so
/// SingleChoiceAdvancePatch fires and broadcasts EventAdvanceResult to everyone.
///
/// The Harmony/reflection glue (EncounterChoiceClientPatch send, EventReflection.TryHostNativeAdvanceSingleChoice,
/// SyncEngine.OnEventAdvanceRequest) binds live game types and is not JIT-able here — these tests pin the pure
/// decision boundary both sides consult, plus the advanced-occurrence bookkeeping that makes the host handler
/// idempotent under the host-click-vs-client-click race AND the transport's deliberate double-send.
/// </summary>
public class SingleChoiceAdvanceGateTests
{
    // ─── (a) CLIENT: single-choice OK click emits an advance-request ──────────────────────────────

    [Fact]
    public void ClientRelay_SingleChoiceRealHostEvent_Relays()
        => Assert.True(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: "SDI_07", choiceCount: 1, occurrenceId: 7));

    [Fact]
    public void ClientRelay_MultiChoiceEvent_DoesNotRelay_AnswerRelayOwnsIt()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: "PROG_EV_42", choiceCount: 2, occurrenceId: 7));

    [Fact]
    public void ClientRelay_SyntheticResultPage_EmptyEventId_DoesNotRelay()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: "", choiceCount: 1, occurrenceId: 7));

    [Fact]
    public void ClientRelay_NullEventId_DoesNotRelay()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: null, choiceCount: 1, occurrenceId: 7));

    [Fact]
    public void ClientRelay_NoOpenOccurrence_ZeroOccId_DoesNotRelay()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: "SDI_07", choiceCount: 1, occurrenceId: 0));

    [Fact]
    public void ClientRelay_GateOff_DoesNotRelay()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: false, eventId: "SDI_07", choiceCount: 1, occurrenceId: 7));

    [Fact]
    public void ClientRelay_NotClient_DoesNotRelay()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: false, gateEnabled: true, eventId: "SDI_07", choiceCount: 1, occurrenceId: 7));

    [Fact]
    public void ClientRelay_UnreadableChoiceCount_DoesNotRelay_FailsSafe()
        => Assert.False(SingleChoiceAdvanceGate.ShouldRelayClientAdvance(
            isClient: true, gateEnabled: true, eventId: "SDI_07", choiceCount: -1, occurrenceId: 7));

    // ─── (b) HOST: prompt showing → drive the native advance ──────────────────────────────────────

    [Fact]
    public void HostDrive_PromptShowing_CompletedSingleChoice_Drives()
        => Assert.True(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: 1, paging: false, alreadyAdvanced: false));

    // ─── (c) idempotence: second request / host already advanced → no-op (first wins) ─────────────

    [Fact]
    public void HostDrive_AlreadyAdvanced_NoOp()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: 1, paging: false, alreadyAdvanced: true));

    [Fact]
    public void HostDrive_FirstRequestDrives_SecondIsNoOp_ViaAdvancedMark()
    {
        // The transport deliberately double-sends every reliable packet, and the host's own click races the
        // client's request — the advanced-occurrence mark makes whichever lands second a no-op.
        EventOccurrenceIds.ResetForTests();
        var evt = new object();
        ushort occ = EventOccurrenceIds.GetOrAssign(evt);

        // First request: not yet advanced → drives (and the drive path marks the occurrence advanced).
        Assert.True(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            true, true, true, 1, false, EventOccurrenceIds.WasAdvanced(occ)));
        EventOccurrenceIds.MarkAdvanced(occ);

        // Duplicate delivery / late second click: advanced → no-op.
        Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            true, true, true, 1, false, EventOccurrenceIds.WasAdvanced(occ)));
    }

    [Fact]
    public void HostDrive_HostClickedFirst_ClientRequestIsNoOp()
    {
        // Host's own prompt click ran SetClosingEncounter → SingleChoiceAdvancePatch marked the occurrence
        // advanced + broadcast. The client's request arrives after: must NOT re-drive (the module still holds
        // the SAME _geoEvent on its result page, so only the mark can tell prompt from result).
        EventOccurrenceIds.ResetForTests();
        var evt = new object();
        ushort occ = EventOccurrenceIds.GetOrAssign(evt);
        EventOccurrenceIds.MarkAdvanced(occ);   // host click won the race

        Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            true, true, true, 1, false, EventOccurrenceIds.WasAdvanced(occ)));
    }

    // ─── (d) modal not showing / wrong state → no-op ──────────────────────────────────────────────

    [Fact]
    public void HostDrive_ModalNotShowingThisOccurrence_NoOp()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: false, isCompleted: true,
            choiceCount: 1, paging: false, alreadyAdvanced: false));

    [Fact]
    public void HostDrive_EventNotCompleted_NoOp_AnswerRelayOwnsCompletion()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: false,
            choiceCount: 1, paging: false, alreadyAdvanced: false));

    [Fact]
    public void HostDrive_MultiChoice_NoOp_NeverTouchesAnswerPath()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: 2, paging: false, alreadyAdvanced: false));

    [Fact]
    public void HostDrive_UnreadableChoiceCount_NoOp_FailsSafe()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: -1, paging: false, alreadyAdvanced: false));

    [Fact]
    public void HostDrive_StillPagingDescription_NoOp()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: true, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: 1, paging: true, alreadyAdvanced: false));

    [Fact]
    public void HostDrive_NotHost_NoOp()
        => Assert.False(SingleChoiceAdvanceGate.ShouldDriveHostAdvance(
            isHost: false, modalShowingThisOccurrence: true, isCompleted: true,
            choiceCount: 1, paging: false, alreadyAdvanced: false));
}

/// <summary>
/// Advanced-occurrence bookkeeping on the host-side occurrence-id authority (mirrors the dismissed tracking).
/// Serial (Collection) because the authority is process-global static state.
/// </summary>
[Collection("EventOccurrenceIds")]
public class EventOccurrenceIdsAdvancedTrackingTests
{
    [Fact]
    public void MarkAdvanced_ThenWasAdvanced_IsTrue()
    {
        EventOccurrenceIds.ResetForTests();
        var evt = new object();
        ushort occ = EventOccurrenceIds.GetOrAssign(evt);

        Assert.False(EventOccurrenceIds.WasAdvanced(occ));
        EventOccurrenceIds.MarkAdvanced(occ);
        Assert.True(EventOccurrenceIds.WasAdvanced(occ));
    }

    [Fact]
    public void WasAdvanced_UnmarkedOccurrence_IsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        var a = new object();
        var b = new object();
        ushort occA = EventOccurrenceIds.GetOrAssign(a);
        ushort occB = EventOccurrenceIds.GetOrAssign(b);
        EventOccurrenceIds.MarkAdvanced(occA);

        Assert.True(EventOccurrenceIds.WasAdvanced(occA));
        Assert.False(EventOccurrenceIds.WasAdvanced(occB));   // a different occurrence is unaffected
    }

    [Fact]
    public void WasAdvanced_ZeroSentinel_IsFalse()
    {
        EventOccurrenceIds.ResetForTests();
        EventOccurrenceIds.MarkAdvanced(0);                    // no-op for the null sentinel
        Assert.False(EventOccurrenceIds.WasAdvanced(0));
    }

    [Fact]
    public void ResetForTests_ClearsAdvancedTracking()
    {
        EventOccurrenceIds.ResetForTests();
        var evt = new object();
        ushort occ = EventOccurrenceIds.GetOrAssign(evt);
        EventOccurrenceIds.MarkAdvanced(occ);
        Assert.True(EventOccurrenceIds.WasAdvanced(occ));

        EventOccurrenceIds.ResetForTests();
        Assert.False(EventOccurrenceIds.WasAdvanced(occ));
    }
}
