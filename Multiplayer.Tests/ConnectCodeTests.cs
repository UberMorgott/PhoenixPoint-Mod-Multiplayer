using System.Net;
using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
{
    public class ConnectCodeTests
    {
        [Theory]
        [InlineData("192.168.1.5", 7777)]
        [InlineData("8.8.8.8", 65530)]
        [InlineData("0.0.0.0", 0)]
        [InlineData("255.255.255.255", 65535)]
        public void Roundtrip_PreservesEndpoint(string ip, int port)
        {
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var code = ConnectCode.Encode(ep);
            var back = ConnectCode.Decode(code);
            Assert.NotNull(back);
            Assert.Equal(ep.Address, back.Address);
            Assert.Equal(ep.Port, back.Port);
        }

        [Fact]
        public void Decode_TolerantOfDashesCaseWhitespace()
        {
            var ep = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 7777);
            var code = ConnectCode.Encode(ep);
            var noisy = "  " + code.Replace("-", "").ToLowerInvariant() + "  ";
            var back = ConnectCode.Decode(noisy);
            Assert.NotNull(back);
            Assert.Equal(ep, back);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a code")]
        [InlineData("ZZZ")]            // too short
        [InlineData("7F3B-21-K9-EXTRA-LONG")]
        [InlineData("0OIL10")]         // ambiguous chars not in alphabet
        public void Decode_ReturnsNullOnMalformed(string bad)
        {
            Assert.Null(ConnectCode.Decode(bad));
        }
    }
}
