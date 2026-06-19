using System;
using Multipleer.Network.Sync;
using Xunit;

/// <summary>
/// Pure-predicate + enum-conversion tests for the Case-B mirror-site spawn decision. The client geoscape
/// sim is frozen, so an in-play (Case-B) site the host created is ABSENT on the client; a geoscape-event
/// result page then resolves a null <c>Context.Site</c> and the native UI falls back to the StartingBase
/// ("Точка Феникс") backdrop/subtitle. <see cref="EventReflection.ShouldSpawnMirror"/> is the single tested
/// decision both the client raise handler and the GeoSite channel use: spawn an inert mirror site ONLY when
/// we carry the identity AND the real site did not resolve on this client.
///
/// SpawnMirrorSite itself instantiates a GeoSite MonoBehaviour via game reflection and CANNOT be JIT'd in
/// xUnit, so only the pure decision surface is asserted here (mirrors <see cref="GeoSiteSnapshotTests"/> /
/// <see cref="EventRaisedIdentityTests"/>).
/// </summary>
public class ShouldSpawnMirrorTests
{
    // ─── ShouldSpawnMirror truth table: hasIdentity && !siteResolved ────────────────────────────
    [Fact]
    public void ShouldSpawnMirror_Identity_AndSiteAbsent_True()
        => Assert.True(EventReflection.ShouldSpawnMirror(hasIdentity: true, siteResolved: false));

    [Fact]
    public void ShouldSpawnMirror_Identity_AndSitePresent_False()
        => Assert.False(EventReflection.ShouldSpawnMirror(hasIdentity: true, siteResolved: true));

    [Fact]
    public void ShouldSpawnMirror_NoIdentity_AndSiteAbsent_False()
        => Assert.False(EventReflection.ShouldSpawnMirror(hasIdentity: false, siteResolved: false));

    [Fact]
    public void ShouldSpawnMirror_NoIdentity_AndSitePresent_False()
        => Assert.False(EventReflection.ShouldSpawnMirror(hasIdentity: false, siteResolved: true));

    // ─── GeoSiteType byte<->enum conversion the spawn path uses ─────────────────────────────────
    // The game GeoSiteType enum is sparse and is not referenced at test-compile time; this LOCAL enum
    // mirrors representative sparse values (None=0, PhoenixBase=10, Marketplace=110) to pin the exact
    // conversion SpawnMirrorSite performs: identity.SiteType (byte) → Enum.ToObject(enumType, byte) →
    // back to byte. A wrong cast (e.g. ordinal vs raw value) would break this round-trip.
    private enum SparseSiteType : byte { None = 0, PhoenixBase = 10, Marketplace = 110 }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)10)]
    [InlineData((byte)110)]
    public void SiteType_Byte_To_Enum_RoundTrips(byte raw)
    {
        object asEnum = Enum.ToObject(typeof(SparseSiteType), raw);
        Assert.IsType<SparseSiteType>(asEnum);
        byte back = (byte)Convert.ToInt32(asEnum);
        Assert.Equal(raw, back);
    }

    [Fact]
    public void SiteType_Byte_To_Enum_PreservesNamedValue()
    {
        Assert.Equal(SparseSiteType.Marketplace, (SparseSiteType)Enum.ToObject(typeof(SparseSiteType), (byte)110));
    }
}
