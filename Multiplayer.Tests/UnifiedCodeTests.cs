using System.Net;
using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
{
    public class UnifiedCodeTests
    {
        private static int StrippedLen(string code) => code.Replace("-", "").Length;

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(76561197u)]
        [InlineData(uint.MaxValue)]
        public void SteamOnly_RoundTrips_9Symbols(uint accountId)
        {
            var code = UnifiedCode.Encode(accountId, null);
            Assert.NotNull(code);
            Assert.Equal(9, StrippedLen(code));

            Assert.True(UnifiedCode.TryDecode(code, out var acct, out var hasSteam, out var ep, out var hasEp));
            Assert.True(hasSteam);
            Assert.False(hasEp);
            Assert.Null(ep);
            Assert.Equal(accountId, acct);
        }

        [Theory]
        [InlineData("0.0.0.0", 0)]
        [InlineData("1.2.3.4", 14242)]
        [InlineData("255.255.255.255", 65535)]
        [InlineData("81.2.69.142", 27015)]
        public void EndpointOnly_RoundTrips_13Symbols(string ip, int port)
        {
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var code = UnifiedCode.Encode(null, ep);
            Assert.NotNull(code);
            Assert.Equal(13, StrippedLen(code));

            Assert.True(UnifiedCode.TryDecode(code, out var acct, out var hasSteam, out var got, out var hasEp));
            Assert.False(hasSteam);
            Assert.Equal(0u, acct);
            Assert.True(hasEp);
            Assert.Equal(ip, got.Address.ToString());
            Assert.Equal(port, got.Port);
        }

        [Fact]
        public void Both_RoundTrips_19Symbols()
        {
            var ep = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 14242);
            var code = UnifiedCode.Encode(4242u, ep);
            Assert.NotNull(code);
            Assert.Equal(19, StrippedLen(code));

            Assert.True(UnifiedCode.TryDecode(code, out var acct, out var hasSteam, out var got, out var hasEp));
            Assert.True(hasSteam);
            Assert.True(hasEp);
            Assert.Equal(4242u, acct);
            Assert.Equal("203.0.113.7", got.Address.ToString());
            Assert.Equal(14242, got.Port);
        }

        [Fact]
        public void NeitherField_Encodes_Null()
        {
            Assert.Null(UnifiedCode.Encode(null, null));
        }

        [Fact]
        public void IPv6Endpoint_TreatedAsNoEndpoint()
        {
            var ep = new IPEndPoint(IPAddress.Parse("::1"), 14242);
            // No steam + a non-IPv4 endpoint → nothing encodable.
            Assert.Null(UnifiedCode.Encode(null, ep));
        }

        [Fact]
        public void CaseAndDashInsensitive_AndCrockfordAliases()
        {
            var code = UnifiedCode.Encode(123456u, null);
            var mangled = code.Replace("-", "").ToLowerInvariant();
            Assert.True(UnifiedCode.TryDecode(mangled, out var acct, out _, out _, out _));
            Assert.Equal(123456u, acct);
        }

        [Fact]
        public void FlippedCheckChar_Rejected()
        {
            var stripped = UnifiedCode.Encode(999u, null).Replace("-", "");
            var last = stripped[stripped.Length - 1];
            var swapped = last == '0' ? '1' : '0';
            var bad = stripped.Substring(0, stripped.Length - 1) + swapped;
            Assert.False(UnifiedCode.TryDecode(bad, out _, out _, out _, out _));
        }

        [Fact]
        public void FlippedDataChar_Rejected()
        {
            // Mutate a middle symbol of a real code — the weighted checksum must reject it.
            var code = UnifiedCode.Encode(null, new IPEndPoint(IPAddress.Parse("8.8.8.8"), 14242)).Replace("-", "");
            var chars = code.ToCharArray();
            chars[3] = chars[3] == 'Z' ? 'Y' : 'Z';
            Assert.False(UnifiedCode.TryDecode(new string(chars), out _, out _, out _, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-code")]
        [InlineData("12345")]          // too short
        [InlineData("1234567890ABCDEFGHU")] // 19 chars but 'U' is not a Crockford symbol → illegal
        public void Garbage_Rejected(string input)
        {
            Assert.False(UnifiedCode.TryDecode(input, out _, out _, out _, out _));
        }
    }
}
