using System.Linq;
using Multiplayer.Network.CommandSync;
using Xunit;

public class GeoSimProducerTableTests
{
    [Fact]
    public void Producers_HasExactlyThirteenRows()
    {
        Assert.Equal(13, GeoSimProducerTable.Producers.Count);
    }

    [Fact]
    public void Producers_NoBlankFields()
    {
        foreach (var p in GeoSimProducerTable.Producers)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DeclaringTypeName));
            Assert.False(string.IsNullOrWhiteSpace(p.MethodName));
        }
    }

    [Fact]
    public void Producers_NoDuplicateTypeMethodPairs()
    {
        var keys = GeoSimProducerTable.Producers
            .Select(p => p.DeclaringTypeName + "::" + p.MethodName)
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("PhoenixPoint.Geoscape.Levels.GeoLevelController", "LevelHourlyUpdateCrt")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoAlienBase", "ExpandAlienBase")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoBehemothActor", "SubmergeCrt")]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoBehemothActor", "EmergeCrt")]
    [InlineData("PhoenixPoint.Geoscape.MistRendererSystem", "UpdateMist")]
    public void Producers_ContainsAnchor(string type, string method)
    {
        Assert.Contains(GeoSimProducerTable.Producers,
            p => p.DeclaringTypeName == type && p.MethodName == method);
    }

    [Theory]
    [InlineData("PhoenixPoint.Geoscape.Entities.GeoNavComponent", "NavigateRoutine")]
    [InlineData("PhoenixPoint.Geoscape.MistRendererSystem", "FrameUpdate")]
    [InlineData("PhoenixPoint.Geoscape.GeoscapeLog", "ProcessQueuedEvents")]
    public void Producers_ExcludesWhitelist(string type, string method)
    {
        Assert.DoesNotContain(GeoSimProducerTable.Producers,
            p => p.DeclaringTypeName == type && p.MethodName == method);
    }
}
