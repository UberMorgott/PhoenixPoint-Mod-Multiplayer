using System.Net;
using Multipleer.Util;
using Xunit;

namespace Multipleer.Tests
{
    // Fix #1 coverage: a DNS hostname[:port] must classify as DirectHost (DirectTransport
    // resolves it via BeginConnect) instead of being silently rejected as Invalid, WITHOUT
    // misreading a Crockford STUN code or a bare SteamID as a hostname. These extend — and do
    // not replace — SmartJoinParserTests.
    public class SmartJoinParserHostnameTests
    {
        [Fact]
        public void Hostname_NoPort_DirectHostWithDefaultPort()
        {
            var r = SmartJoinParser.Parse("myhost.ddns.net");
            Assert.Equal(JoinKind.DirectHost, r.Kind);
            Assert.Equal("myhost.ddns.net", r.Ip);
            Assert.Equal(SmartJoinParser.DefaultDirectPort, r.Port);
        }

        [Fact]
        public void Hostname_WithPort_DirectHostPreservesPort()
        {
            var r = SmartJoinParser.Parse("host.com:9000");
            Assert.Equal(JoinKind.DirectHost, r.Kind);
            Assert.Equal("host.com", r.Ip);
            Assert.Equal(9000, r.Port);
        }

        [Fact]
        public void LiteralIPv4_StaysDirectIp_NotDirectHost()
        {
            var r = SmartJoinParser.Parse("10.0.0.2");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("10.0.0.2", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        [Fact]
        public void Localhost_StaysDirectIpLoopback()
        {
            var r = SmartJoinParser.Parse("localhost");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("127.0.0.1", r.Ip);
        }

        [Fact]
        public void CrockfordCode_NotMisreadAsHostname()
        {
            // A real round-tripped STUN code must remain StunCode, never DirectHost.
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 50000));
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.StunCode, r.Kind);
        }

        [Fact]
        public void BareSteamId_NotMisreadAsHostname()
        {
            var r = SmartJoinParser.Parse("76561198000000000");
            Assert.Equal(JoinKind.SteamId, r.Kind);
            Assert.Equal(76561198000000000UL, r.SteamId);
        }

        [Theory]
        [InlineData("999.999.999.999")] // all-numeric TLD -> not a host, stays Invalid (existing contract)
        [InlineData("hello world")]      // space -> outside hostname char-class
        [InlineData("..")]               // empty labels
        [InlineData("nodot")]            // no label separator (LAN NetBIOS names unsupported)
        public void NonHostnameGarbage_StaysInvalid(string bad)
        {
            Assert.Equal(JoinKind.Invalid, SmartJoinParser.Parse(bad).Kind);
        }
    }
}
