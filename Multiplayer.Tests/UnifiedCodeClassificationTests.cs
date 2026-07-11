using System.Net;
using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
{
    // The classification matrix: a unified code (v2) must parse as JoinKind.Unified with the right
    // fields, and — crucially — must NOT poach the older formats, nor be poached by them.
    public class UnifiedCodeClassificationTests
    {
        [Fact]
        public void SteamOnlyCode_Classifies_Unified_WithSteamId()
        {
            var code = UnifiedCode.Encode(1234567u, null);
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.Unified, r.Kind);
            Assert.Equal(InviteCode.ToSteamId64(1234567u), r.SteamId);
            Assert.Null(r.Ip);
        }

        [Fact]
        public void EndpointOnlyCode_Classifies_Unified_WithEndpoint()
        {
            var code = UnifiedCode.Encode(null, new IPEndPoint(IPAddress.Parse("1.2.3.4"), 14242));
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.Unified, r.Kind);
            Assert.Equal(0UL, r.SteamId);
            Assert.Equal("1.2.3.4", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        [Fact]
        public void BothCode_Classifies_Unified_WithBothFields()
        {
            var code = UnifiedCode.Encode(4242u, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 14242));
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.Unified, r.Kind);
            Assert.Equal(InviteCode.ToSteamId64(4242u), r.SteamId);
            Assert.Equal("203.0.113.7", r.Ip);
            Assert.Equal(14242, r.Port);
        }

        // ── Old formats keep classifying correctly (never stolen by the Unified branch) ──

        [Fact]
        public void LegacyInviteCode_StillClassifies_SteamId()
        {
            var r = SmartJoinParser.Parse(InviteCode.Encode(1234567u));
            Assert.Equal(JoinKind.SteamId, r.Kind);
            Assert.Equal(InviteCode.ToSteamId64(1234567u), r.SteamId);
        }

        [Fact]
        public void LegacyConnectCode_StillClassifies_Stun()
        {
            var code = ConnectCode.Encode(new IPEndPoint(IPAddress.Parse("81.2.69.142"), 27015));
            var r = SmartJoinParser.Parse(code);
            Assert.Equal(JoinKind.StunCode, r.Kind);
            Assert.Equal("81.2.69.142", r.Ip);
            Assert.Equal(27015, r.Port);
        }

        [Fact]
        public void Ipv4_StillClassifies_DirectIp()
        {
            var r = SmartJoinParser.Parse("192.168.1.5:7777");
            Assert.Equal(JoinKind.DirectIp, r.Kind);
        }

        [Fact]
        public void BareSteamId64_StillClassifies_SteamId()
        {
            var r = SmartJoinParser.Parse("76561197960265728");
            Assert.Equal(JoinKind.SteamId, r.Kind);
            Assert.Equal(76561197960265728UL, r.SteamId);
        }

        [Fact]
        public void CorruptUnifiedLengthCode_NotMisreadAsUnified()
        {
            // A 19-symbol code with a flipped check char must not classify as Unified (and, since it is
            // 19 non-dotted symbols, must not be silently accepted as anything connectable either).
            var stripped = UnifiedCode.Encode(4242u, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 14242))
                .Replace("-", "");
            var last = stripped[stripped.Length - 1];
            var bad = stripped.Substring(0, stripped.Length - 1) + (last == '0' ? '1' : '0');
            Assert.NotEqual(JoinKind.Unified, SmartJoinParser.Parse(bad).Kind);
        }
    }
}
