using System.Net;
using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
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
        [InlineData(1u)]
        [InlineData(111222333u)]
        [InlineData(uint.MaxValue)]
        public void InviteCode_ResolvesToSteamId64(uint accountId)
        {
            var code = InviteCode.Encode(accountId);
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.SteamId, r.Kind);
            Assert.Equal(InviteCode.ToSteamId64(accountId), r.SteamId);

            // Same result dash-stripped + lower-cased (paste tolerance).
            var r2 = SmartJoinParser.Parse(code.Replace("-", "").ToLowerInvariant());
            Assert.Equal(JoinKind.SteamId, r2.Kind);
            Assert.Equal(InviteCode.ToSteamId64(accountId), r2.SteamId);
        }

        [Fact]
        public void InviteCode_DoesNotShadowTenSymbolStunCode()
        {
            // A real 10-symbol STUN code must still classify as STUN, never as an 8-symbol invite.
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 41000));
            Assert.Equal(JoinKind.StunCode, SmartJoinParser.Parse(code).Kind);
        }

        [Fact]
        public void EightSymbolCodeWithBadCheck_IsInvalid_NotMisclassified()
        {
            // 8 Crockford symbols but a corrupted check symbol → not an invite, and (no dots, not 10
            // symbols, not 15+ digits) nothing else claims it → Invalid.
            var raw = InviteCode.Encode(42u).Replace("-", "");
            var bad = (raw[0] == '0' ? '1' : '0') + raw.Substring(1);
            Assert.Equal(JoinKind.Invalid, SmartJoinParser.Parse(bad).Kind);
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

        [Theory]
        [InlineData("127.0.0.1:14242")]   // literal loopback + our port
        [InlineData("localhost")]         // normalizes to 127.0.0.1:14242
        [InlineData("127.5.6.7:14242")]   // anywhere in 127.0.0.0/8 is loopback
        public void IsOwnLoopback_TrueForOwnLoopbackEndpoint(string input)
        {
            Assert.True(SmartJoinParser.IsOwnLoopback(SmartJoinParser.Parse(input), 14242));
        }

        [Theory]
        [InlineData("127.0.0.1:14243")]     // loopback but wrong (not our) port
        [InlineData("192.168.1.5:14242")]   // our port but a LAN address, not loopback
        [InlineData("76561198000000000")]   // not a DirectIP target at all
        public void IsOwnLoopback_FalseForEverythingElse(string input)
        {
            Assert.False(SmartJoinParser.IsOwnLoopback(SmartJoinParser.Parse(input), 14242));
        }
    }
}
