using Multiplayer.Network.MessageLayer;
using Xunit;

namespace Multiplayer.Tests
{
    public class ChatSerializerTests
    {
        [Fact]
        public void Chat_Roundtrip()
        {
            var src = new ChatMessageData
            { SenderSteamId = 42, SenderNick = "Alice", Text = "gg", IsSystem = false };
            var back = MessageSerializer.DeserializeChat(MessageSerializer.SerializeChat(src));
            Assert.Equal(src.SenderSteamId, back.SenderSteamId);
            Assert.Equal(src.SenderNick, back.SenderNick);
            Assert.Equal(src.Text, back.Text);
            Assert.False(back.IsSystem);
        }

        [Fact]
        public void SetSave_Roundtrip()
        {
            var (n, m) = MessageSerializer.DeserializeSetSave(
                MessageSerializer.SerializeSetSave("Campaign 3", "saved 2h ago / 3.1 MB"));
            Assert.Equal("Campaign 3", n);
            Assert.Equal("saved 2h ago / 3.1 MB", m);
        }
    }
}
