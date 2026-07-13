using Multiplayer.Network.MessageLayer;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Round-trip pins for the EntryTransferAbort (0x47) codec — host tells clients the
    /// tac-entry save transfer will never complete (reason string, diagnostics only).</summary>
    public class EntryTransferAbortCodecTests
    {
        [Theory]
        [InlineData("no save bytes (mid-tactical save write failed)")]
        [InlineData("transfer failed to start")]
        [InlineData("")]
        public void Reason_Roundtrip(string reason)
            => Assert.Equal(reason, MessageSerializer.DeserializeEntryTransferAbort(
                MessageSerializer.SerializeEntryTransferAbort(reason)));

        [Fact]
        public void NullReason_SerializesAsEmpty()
            => Assert.Equal("", MessageSerializer.DeserializeEntryTransferAbort(
                MessageSerializer.SerializeEntryTransferAbort(null)));
    }
}
