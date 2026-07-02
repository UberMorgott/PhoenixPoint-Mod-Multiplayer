using Multipleer.Network.Sync.State;
using Xunit;

/// <summary>
/// The static <see cref="EventDisplay"/> open-occurrence record must be cleared by the save-load /
/// session boundary reset (SyncEngine.ResetEventMirror → <see cref="EventDisplay.ResetOpenOccurrence"/>).
/// It previously survived the reset (self-healing only on the next Show with a non-zero occId): a stale
/// non-zero record could make a post-boundary Dismiss for a NEW occurrence be refused by the occId
/// close-guard until the next Show overwrote it.
/// </summary>
public class EventDisplayResetTests
{
    [Fact]
    public void ResetOpenOccurrence_ClearsRecordedOpenOccurrence()
    {
        EventDisplay.SetOpenOccurrenceForTests(42);
        Assert.Equal(42, EventDisplay.OpenOccurrenceId);

        EventDisplay.ResetOpenOccurrence();
        Assert.Equal(0, EventDisplay.OpenOccurrenceId);
    }
}
