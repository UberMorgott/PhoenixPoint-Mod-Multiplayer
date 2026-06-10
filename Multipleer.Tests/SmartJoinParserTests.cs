using System.Net;
using Multipleer.Util;
using Xunit;

namespace Multipleer.Tests
{
    public class SmartJoinParserTests
    {
        [Fact]
        public void DirectIp_WithPort()
        {
            var r = SmartJoinParser.Parse("192.168.1.5:7777");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("192.168.1.5", r.Ip);
            Assert.Equal(7777, r.Port);
        }

        [Fact]
        public void DirectIp_NoPort_UsesDefault()
        {
            var r = SmartJoinParser.Parse("10.0.0.2");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("10.0.0.2", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        [Fact]
        public void Localhost_NoPort_MapsToLoopbackDefaultPort()
        {
            var r = SmartJoinParser.Parse("localhost");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("127.0.0.1", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        [Fact]
        public void Localhost_WithPort_PreservesPort()
        {
            var r = SmartJoinParser.Parse("localhost:7000");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("127.0.0.1", r.Ip);
            Assert.Equal(7000, r.Port);
        }

        [Fact]
        public void Localhost_CaseInsensitive()
        {
            var r = SmartJoinParser.Parse("LOCALHOST");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
            Assert.Equal("127.0.0.1", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        [Fact]
        public void StunCode_RoundTripsThroughConnectCode()
        {
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 50000));
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.StunCode, r.Kind);
            Assert.Equal("203.0.113.7", r.Ip);
            Assert.Equal(50000, r.Port);
        }

        [Fact]
        public void SteamId_BareLongRoutesToSteam()
        {
            var r = SmartJoinParser.Parse("76561198000000000");
            Assert.Equal(JoinKind.SteamId, r.Kind);
            Assert.Equal(76561198000000000UL, r.SteamId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("hello world")]
        [InlineData("999.999.999.999")]
        public void Invalid_Rejected(string bad)
        {
            Assert.Equal(JoinKind.Invalid, SmartJoinParser.Parse(bad).Kind);
        }
    }
}
