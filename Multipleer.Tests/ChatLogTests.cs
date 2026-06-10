using System.Linq;
using Multipleer.UI;
using Xunit;

namespace Multipleer.Tests
{
    public class ChatLogTests
    {
        [Fact]
        public void Append_StoresSenderTextAndUserFlag()
        {
            var log = new ChatLog(8);
            log.Append("Alice", "hello", isSystem: false);
            var line = log.Lines.Single();
            Assert.Equal("Alice", line.Sender);
            Assert.Equal("hello", line.Text);
            Assert.False(line.IsSystem);
        }

        [Fact]
        public void System_FlagPreserved()
        {
            var log = new ChatLog(8);
            log.AppendSystem("Bob joined");
            var line = log.Lines.Single();
            Assert.True(line.IsSystem);
            Assert.Equal("Bob joined", line.Text);
        }

        [Fact]
        public void Cap_DropsOldestBeyondCapacity()
        {
            var log = new ChatLog(3);
            for (int i = 0; i < 5; i++) log.Append("u", "m" + i, false);
            Assert.Equal(3, log.Lines.Count);
            Assert.Equal("m2", log.Lines.First().Text);
            Assert.Equal("m4", log.Lines.Last().Text);
        }

        [Fact]
        public void Version_BumpsOnEachAppend()
        {
            var log = new ChatLog(4);
            var v0 = log.Version;
            log.Append("u", "a", false);
            Assert.NotEqual(v0, log.Version);
        }
    }
}
