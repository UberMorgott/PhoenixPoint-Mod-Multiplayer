using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Network.Sync.State;
using Multiplayer.Util;
using Xunit;

// Inc5 part 1 — rolling CRC divergence probe: pure pins for the shared CRC-32, the canonical
// (deterministic) subset images, and the versioned round codec that rides the GeoCrcProbe (0xA9)
// envelope surface. Determinism is the load-bearing property: host and client run the SAME code
// over their own live reads, so equal state MUST yield equal bytes/CRC regardless of enumeration
// order or sub-unit float noise.
public class CrcProbeTests
{
    // ─── Crc32 (moved from SaveTransferCoordinator — the ONE shared impl) ───

    [Fact]
    public void Crc32_MatchesStandardCheckVector()
    {
        // IEEE 802.3 reflected CRC-32 of ASCII "123456789" is the canonical check value.
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Crc32_NullAndEmptyAreZero()
    {
        Assert.Equal(0u, Crc32.Compute(null));
        Assert.Equal(0u, Crc32.Compute(new byte[0]));
    }

    // ─── Subset determinism pins (same input → same CRC, order-independent) ───

    [Fact]
    public void WalletCrc_IsDeterministic_AndOrderIndependent()
    {
        var a = new List<(int, float)> { (1, 120f), (2, 45f), (0x100, 7f) };
        var b = new List<(int, float)> { (0x100, 7f), (1, 120f), (2, 45f) };   // shuffled
        Assert.Equal(CrcSubsetCrc.Wallet(a), CrcSubsetCrc.Wallet(a));           // twice → same
        Assert.Equal(CrcSubsetCrc.Wallet(a), CrcSubsetCrc.Wallet(b));           // order-free
    }

    [Fact]
    public void WalletCrc_QuantizesSubUnitFloatNoise_ButSeesWholeUnitChange()
    {
        var exact = new List<(int, float)> { (1, 120f) };
        var noisy = new List<(int, float)> { (1, 120.0001f) };                  // diff-apply float noise
        var moved = new List<(int, float)> { (1, 121f) };                       // a real unit of drift
        Assert.Equal(CrcSubsetCrc.Wallet(exact), CrcSubsetCrc.Wallet(noisy));
        Assert.NotEqual(CrcSubsetCrc.Wallet(exact), CrcSubsetCrc.Wallet(moved));
    }

    [Fact]
    public void SitesCrc_IsOrderIndependent_AndSeesIdentityDrift()
    {
        var a = new List<(int, string, byte)> { (3, "guid-a", 1), (7, "guid-b", 2), (12, "", 0) };
        var shuffled = new List<(int, string, byte)> { (12, "", 0), (3, "guid-a", 1), (7, "guid-b", 2) };
        var drifted = new List<(int, string, byte)> { (3, "guid-a", 1), (7, "guid-b", 3), (12, "", 0) };
        Assert.Equal(CrcSubsetCrc.Sites(a), CrcSubsetCrc.Sites(shuffled));
        Assert.NotEqual(CrcSubsetCrc.Sites(a), CrcSubsetCrc.Sites(drifted));
        // Null owner guid canonicalizes like empty (best-effort owner read degrades to "").
        Assert.Equal(
            CrcSubsetCrc.Sites(new List<(int, string, byte)> { (1, null, 0) }),
            CrcSubsetCrc.Sites(new List<(int, string, byte)> { (1, "", 0) }));
    }

    [Fact]
    public void RosterCrc_IsASetImage_DistinctSorted()
    {
        var a = new long[] { 9, 3, 3, 7 };            // duplicates + unsorted
        var b = new long[] { 3, 7, 9 };
        var c = new long[] { 3, 7 };                  // a soldier went missing
        Assert.Equal(CrcSubsetCrc.Roster(a), CrcSubsetCrc.Roster(b));
        Assert.NotEqual(CrcSubsetCrc.Roster(b), CrcSubsetCrc.Roster(c));
    }

    [Fact]
    public void ResearchCrc_IsASetImage_OrdinalSorted_EmptyIdsDropped()
    {
        var a = new[] { "PX_Alpha", "ANU_Beta", "PX_Alpha", null, "" };
        var b = new[] { "ANU_Beta", "PX_Alpha" };
        var c = new[] { "ANU_Beta" };
        Assert.Equal(CrcSubsetCrc.Research(a), CrcSubsetCrc.Research(b));
        Assert.NotEqual(CrcSubsetCrc.Research(b), CrcSubsetCrc.Research(c));
    }

    [Fact]
    public void SubsetCrcs_NullInputEqualsEmptyInput()
    {
        Assert.Equal(CrcSubsetCrc.Wallet(new List<(int, float)>()), CrcSubsetCrc.Wallet(null));
        Assert.Equal(CrcSubsetCrc.Sites(new List<(int, string, byte)>()), CrcSubsetCrc.Sites(null));
        Assert.Equal(CrcSubsetCrc.Roster(new long[0]), CrcSubsetCrc.Roster(null));
        Assert.Equal(CrcSubsetCrc.Research(new string[0]), CrcSubsetCrc.Research(null));
    }

    // ─── Subset-id space pin (SurfaceIds uniqueness discipline, scoped to CrcSubsetIds) ───

    [Fact]
    public void SubsetIds_AreUnique_AndAllNamed()
    {
        var ids = typeof(CrcSubsetIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .Select(f => (f.Name, Value: (byte)f.GetRawConstantValue()))
            .ToList();
        Assert.True(ids.Count >= 4, "expected at least the four roadmap subsets");
        Assert.Equal(ids.Count, ids.Select(x => x.Value).Distinct().Count());
        foreach (var (name, value) in ids)
            Assert.False(CrcSubsetIds.Name(value).StartsWith("subset-", StringComparison.Ordinal),
                $"subset {name}={value} has no human name");
    }

    // ─── Round codec (versioned, tolerant) ───

    [Fact]
    public void ProbeCodec_RoundTrips()
    {
        var entries = new List<(byte, uint)> { (CrcSubsetIds.Wallet, 0xDEADBEEFu), (CrcSubsetIds.Research, 0x12345678u) };
        var bytes = CrcProbeCodec.Encode(42u, entries);
        Assert.True(CrcProbeCodec.TryDecode(bytes, out var round, out var decoded));
        Assert.Equal(42u, round);
        Assert.Equal(entries, decoded);
    }

    [Fact]
    public void ProbeCodec_EmptyEntriesRoundTrip()
    {
        var bytes = CrcProbeCodec.Encode(7u, new List<(byte, uint)>());
        Assert.True(CrcProbeCodec.TryDecode(bytes, out var round, out var decoded));
        Assert.Equal(7u, round);
        Assert.Empty(decoded);
    }

    [Fact]
    public void ProbeCodec_RejectsNullShortTruncatedAndUnknownVersion()
    {
        Assert.False(CrcProbeCodec.TryDecode(null, out _, out _));
        Assert.False(CrcProbeCodec.TryDecode(new byte[0], out _, out _));
        Assert.False(CrcProbeCodec.TryDecode(new byte[] { CrcProbeCodec.Version, 1, 0 }, out _, out _));

        var good = CrcProbeCodec.Encode(1u, new List<(byte, uint)> { (1, 0xAAAAAAAAu) });
        var truncated = good.Take(good.Length - 2).ToArray();
        Assert.False(CrcProbeCodec.TryDecode(truncated, out _, out _));

        var wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = (byte)(CrcProbeCodec.Version + 1);
        Assert.False(CrcProbeCodec.TryDecode(wrongVersion, out _, out _));
    }
}
