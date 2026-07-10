using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    // Pure-logic coverage for the Steam invite/join subsystem: cold-start command-line parse,
    // rich-presence connect-string round-trip, and the Steam-P2P-vs-DirectIP fallback selection.
    public class SteamConnectTests
    {
        private const ulong SampleLobby = 109775242847036928UL;
        private const ulong SampleHost = 76561197960265728UL; // a real-shaped 17-digit SteamID64

        [Fact]
        public void ConnectString_Format()
        {
            Assert.Equal("+connect_lobby 123", SteamConnect.ConnectString(123));
        }

        [Fact]
        public void ParseConnectLobby_FindsIdAfterToken()
        {
            var id = SteamConnect.ParseConnectLobby(new[] { "-batchmode", "+connect_lobby", "456", "-extra" });
            Assert.Equal(456UL, id);
        }

        [Fact]
        public void ParseConnectLobby_CaseInsensitiveToken()
        {
            Assert.Equal(456UL, SteamConnect.ParseConnectLobby(new[] { "+CONNECT_LOBBY", "456" }));
        }

        [Fact]
        public void ParseConnectLobby_NoTokenOrTrailing_ReturnsNull()
        {
            Assert.Null(SteamConnect.ParseConnectLobby(new[] { "-nofx", "-windowed" }));
            Assert.Null(SteamConnect.ParseConnectLobby(new[] { "+connect_lobby" })); // token but no value
            Assert.Null(SteamConnect.ParseConnectLobby(null));
        }

        [Fact]
        public void ParseConnectLobby_ZeroRejected()
        {
            Assert.Null(SteamConnect.ParseConnectLobby(new[] { "+connect_lobby", "0" }));
        }

        [Fact]
        public void ParseConnectString_RoundTrip()
        {
            Assert.Equal(SampleLobby, SteamConnect.ParseConnectString(SteamConnect.ConnectString(SampleLobby)));
        }

        [Fact]
        public void ParseConnectString_JunkReturnsNull()
        {
            Assert.Null(SteamConnect.ParseConnectString(""));
            Assert.Null(SteamConnect.ParseConnectString("+connect 1.2.3.4:14242"));
            Assert.Null(SteamConnect.ParseConnectString(null));
        }

        [Fact]
        public void ResolveJoinString_PrefersSteamId()
        {
            Assert.Equal(SampleHost.ToString(), SteamConnect.ResolveJoinString(SampleHost, "1.2.3.4:14242"));
        }

        [Fact]
        public void ResolveJoinString_FallsBackToIpWhenNoSteamId()
        {
            Assert.Equal("1.2.3.4:14242", SteamConnect.ResolveJoinString(0, "1.2.3.4:14242"));
            Assert.Equal("1.2.3.4:14242", SteamConnect.ResolveJoinString(0, "  1.2.3.4:14242  ")); // trimmed
        }

        [Fact]
        public void ResolveJoinString_NeitherAvailable_ReturnsNull()
        {
            Assert.Null(SteamConnect.ResolveJoinString(0, null));
            Assert.Null(SteamConnect.ResolveJoinString(0, "   "));
        }

        // ─── TryParseLaunch: both canonical Steam launch forms ───────────────

        [Fact]
        public void TryParseLaunch_ConnectLobby_ReturnsLobbyId()
        {
            Assert.True(SteamConnect.TryParseLaunch(new[] { "-nofx", "+connect_lobby", "123" }, out var id, out var js));
            Assert.Equal(123UL, id);
            Assert.Null(js);
        }

        [Fact]
        public void TryParseLaunch_Connect_ReturnsJoinString()
        {
            Assert.True(SteamConnect.TryParseLaunch(new[] { "+connect", "1.2.3.4:14242" }, out var id, out var js));
            Assert.Equal(0UL, id);
            Assert.Equal("1.2.3.4:14242", js);
        }

        [Fact]
        public void TryParseLaunch_Connect_CaseInsensitive_AndTrimmed()
        {
            Assert.True(SteamConnect.TryParseLaunch(new[] { "+CONNECT", " 1.2.3.4:14242 " }, out _, out var js));
            Assert.Equal("1.2.3.4:14242", js);
        }

        [Fact]
        public void TryParseLaunch_LobbyWinsWhenBothPresent()
        {
            Assert.True(SteamConnect.TryParseLaunch(
                new[] { "+connect", "1.2.3.4:14242", "+connect_lobby", "77" }, out var id, out var js));
            Assert.Equal(77UL, id);
            Assert.Null(js);
        }

        [Fact]
        public void TryParseLaunch_NoTarget_ReturnsFalse()
        {
            Assert.False(SteamConnect.TryParseLaunch(new[] { "-windowed", "-nofx" }, out _, out _));
            Assert.False(SteamConnect.TryParseLaunch(new[] { "+connect" }, out _, out _));       // no value
            Assert.False(SteamConnect.TryParseLaunch(new[] { "+connect", "  " }, out _, out _)); // blank value
            Assert.False(SteamConnect.TryParseLaunch((string[])null, out _, out _));
        }

        [Fact]
        public void TryParseLaunch_RawCommandLine_BothForms()
        {
            Assert.True(SteamConnect.TryParseLaunch("game.exe +connect_lobby 42", out var id, out _));
            Assert.Equal(42UL, id);
            Assert.True(SteamConnect.TryParseLaunch("game.exe +connect " + SampleHost, out var id2, out var js2));
            Assert.Equal(0UL, id2);
            Assert.Equal(SampleHost.ToString(), js2);
            Assert.False(SteamConnect.TryParseLaunch("", out _, out _));
            Assert.False(SteamConnect.TryParseLaunch((string)null, out _, out _));
        }

        [Fact]
        public void TryParseLaunch_RichPresenceValue_RoundTrip()
        {
            // The exact string HostPublish sets as rich presence "connect" must resolve back to the lobby.
            Assert.True(SteamConnect.TryParseLaunch(SteamConnect.ConnectString(SampleLobby), out var id, out _));
            Assert.Equal(SampleLobby, id);
        }

        [Fact]
        public void ResolvedSteamId_IsClassifiedAsSteamByJoinParser()
        {
            // The resolve output must round-trip through the existing join classifier as a Steam target
            // (a SteamID64 is 17 digits, so it clears SmartJoinParser's >=15-digit Steam rule).
            var s = SteamConnect.ResolveJoinString(SampleHost, null);
            var target = Util.SmartJoinParser.Parse(s);
            Assert.Equal(Util.JoinKind.SteamId, target.Kind);
            Assert.Equal(SampleHost, target.SteamId);
        }
    }
}
