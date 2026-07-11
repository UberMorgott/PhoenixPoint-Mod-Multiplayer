using Multiplayer.Util;
using Xunit;

namespace Multiplayer.Tests
{
    public class InviteCodeTests
    {
        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(42u)]
        [InlineData(111222333u)]
        [InlineData(0x7FFFFFFFu)]
        [InlineData(uint.MaxValue)]
        public void Encode_Decode_Roundtrips(uint accountId)
        {
            var code = InviteCode.Encode(accountId);
            Assert.True(InviteCode.TryDecode(code, out var back));
            Assert.Equal(accountId, back);
        }

        [Fact]
        public void Encode_FormatIsEightSymbolsDashGrouped()
        {
            var code = InviteCode.Encode(123456u);
            Assert.Equal(9, code.Length);      // "XXXX-XXXX"
            Assert.Equal('-', code[4]);
            Assert.Equal(InviteCode.TotalSymbols, code.Replace("-", "").Length);
        }

        [Fact]
        public void Decode_TolerantOfDashesCaseWhitespace()
        {
            var code = InviteCode.Encode(9876543u);
            var noisy = "  " + code.Replace("-", "").ToLowerInvariant() + "  ";
            Assert.True(InviteCode.TryDecode(noisy, out var back));
            Assert.Equal(9876543u, back);
        }

        [Fact]
        public void Decode_MapsCrockfordAliases_O_to_0()
        {
            // Encode(0) == all-zero symbols == "0000-0000"; typing 'O' for every '0' must still decode.
            Assert.Equal("0000-0000", InviteCode.Encode(0u));
            Assert.True(InviteCode.TryDecode("OOOO-OOOO", out var back));
            Assert.Equal(0u, back);
        }

        [Fact]
        public void Decode_MapsCrockfordAliases_I_and_L_to_1()
        {
            // Encode(1) ends in data symbol '1'; typing 'I' or 'L' for it must still decode to 1.
            var code = InviteCode.Encode(1u);
            Assert.Contains('1', code);
            Assert.True(InviteCode.TryDecode(code.Replace('1', 'I'), out var viaI));
            Assert.Equal(1u, viaI);
            Assert.True(InviteCode.TryDecode(code.Replace('1', 'L'), out var viaL));
            Assert.Equal(1u, viaL);
        }

        [Fact]
        public void Decode_RejectsWrongCheckSymbol()
        {
            // Corrupt one data symbol → the checksum no longer matches → rejected.
            var raw = InviteCode.Encode(555u).Replace("-", "");
            var corrupt = (raw[0] == '0' ? '1' : '0') + raw.Substring(1);
            Assert.False(InviteCode.TryDecode(corrupt, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ABC")]              // too short
        [InlineData("ABCD-ABCD-ABCD")]   // too long
        [InlineData("ABCD-ABCU")]        // 'U' not in Crockford alphabet
        public void Decode_ReturnsFalseOnMalformed(string bad)
        {
            Assert.False(InviteCode.TryDecode(bad, out var acct));
            Assert.Equal(0u, acct);
        }

        [Fact]
        public void SteamId64Base_IsWellKnownIndividualPublicDesktopBase()
        {
            Assert.Equal(76561197960265728UL, InviteCode.SteamId64Base);
        }

        [Theory]
        [InlineData(0u, 76561197960265728UL)]
        [InlineData(1u, 76561197960265729UL)]
        [InlineData(0x11223344u, 76561197960265728UL + 0x11223344u)]
        public void ToSteamId64_ComposesBasePlusAccountId(uint accountId, ulong expected)
        {
            Assert.Equal(expected, InviteCode.ToSteamId64(accountId));
            // low 32 bits of a real individual SteamID64 == the account id (exact reconstruction).
            Assert.Equal(accountId, (uint)InviteCode.ToSteamId64(accountId));
        }
    }
}
