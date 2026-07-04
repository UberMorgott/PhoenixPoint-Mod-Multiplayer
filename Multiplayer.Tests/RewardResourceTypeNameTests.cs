using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// FIX #1: the client RESULT card dropped every reward RESOURCE line ("unresolved type 2", "unresolved type 1")
/// even though the wire delivered the correct <c>ResourceType</c> integer (2 = Materials, 1 = Supplies/Provisions).
///
/// Root: <c>RewardDisplayReflection.ResourceDisplayName</c> mapped the raw enum int → the enum NAME via a runtime
/// <c>AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType")</c> + <c>Enum.GetName</c>. When that fuzzy
/// type lookup returned null (load-order / cache miss), the code fell back to <c>resourceTypeRaw.ToString()</c> —
/// i.e. the LITERAL "2" / "1" — and the native <c>ResourcesList.GetDef&lt;ViewElementDef&gt;("2")</c> has no such
/// item → null def → line dropped. Native (UIModuleSiteEncounters.cs:417) keys the list by <c>Type.ToString()</c>
/// (the enum NAME "Materials"/"Supplies"), never the number.
///
/// Fix: a COMPILE-TIME stable map (<see cref="RewardResourceTypes"/>) from the stable enum integer to the enum
/// NAME, mirroring PhoenixPoint.Common.Core.ResourceType (a compile-time enum, identical host/client). The lookup
/// can no longer return a numeric string that the NamedListDef can't resolve. These pure tests lock the map; the
/// Unity-bound def lookup in RewardDisplayReflection is exercised in-game.
/// </summary>
public class RewardResourceTypeNameTests
{
    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "Supplies")]      // displayed "Provisions" / "Провиант"
    [InlineData(2, "Materials")]     // displayed "Materials" / "Материалы"
    [InlineData(4, "Tech")]
    [InlineData(8, "AICore1")]
    [InlineData(0x10, "AICore2")]
    [InlineData(0x20, "AICore3")]
    [InlineData(0x40, "Research")]
    [InlineData(0x80, "Production")]
    [InlineData(0x100, "Mutagen")]
    [InlineData(0x200, "LivingCrystals")]
    [InlineData(0x400, "Orichalcum")]
    [InlineData(0x800, "ProteanMutane")]
    public void ResolvesEnumName_ForEveryDefinedResourceType(int raw, string expectedName)
        => Assert.Equal(expectedName, RewardResourceTypes.NameForRaw(raw));

    [Fact]
    public void Materials_And_Supplies_ResolveToEnumNames_NotNumericStrings()
    {
        // The exact in-game regression: type 2 + type 1 must map to the NamedListDef keys, never "2"/"1".
        Assert.Equal("Materials", RewardResourceTypes.NameForRaw(2));
        Assert.Equal("Supplies", RewardResourceTypes.NameForRaw(1));
        Assert.NotEqual("2", RewardResourceTypes.NameForRaw(2));
        Assert.NotEqual("1", RewardResourceTypes.NameForRaw(1));
    }

    [Fact]
    public void UnknownRaw_ReturnsNull_SoLineDropsLoudly_NotAsNumericKey()
    {
        // An undefined value (e.g. a flag combination or future enum member) must NOT degrade to its number
        // (which the NamedListDef can never resolve); return null so the caller drops the single line cleanly.
        Assert.Null(RewardResourceTypes.NameForRaw(3));     // 1|2 — not a single named member
        Assert.Null(RewardResourceTypes.NameForRaw(99999));
        Assert.Null(RewardResourceTypes.NameForRaw(-1));
    }

    [Fact]
    public void Map_RoundTripsThroughWireCodec_MaterialsAndProvisions()
    {
        // End-to-end: a Materials(+500) + Supplies(+80) reward set encodes, decodes, and each decoded raw type
        // resolves back to its NamedListDef key — the precise host→client path that was rendering 0 lines.
        var snap = new RewardDisplaySnapshot();
        snap.Resources.Add(new RewardResourceLine(2, 500));   // Materials +500
        snap.Resources.Add(new RewardResourceLine(1, 80));    // Supplies/Provisions +80
        var rt = RewardDisplaySnapshot.Decode(RewardDisplaySnapshot.Encode(snap));

        Assert.NotNull(rt);
        Assert.Equal(2, rt.Resources.Count);
        Assert.Equal("Materials", RewardResourceTypes.NameForRaw(rt.Resources[0].ResourceType));
        Assert.Equal(500, rt.Resources[0].RoundedValue);
        Assert.Equal("Supplies", RewardResourceTypes.NameForRaw(rt.Resources[1].ResourceType));
        Assert.Equal(80, rt.Resources[1].RoundedValue);
    }
}
