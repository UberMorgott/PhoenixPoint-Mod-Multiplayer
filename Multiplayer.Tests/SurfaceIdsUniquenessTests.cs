using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Xunit;

// Guard: SurfaceIds ids are scoped PER KIND (see the SurfaceIds <remarks>). A value may repeat
// across kinds (the wire discriminates by kind); a value repeated WITHIN one kind silently
// mis-routes sync. These tests reflect over the const bytes, group by kind, and fail on any
// same-kind collision — so a future colliding id is caught at build time, not in the field.
public class SurfaceIdsUniquenessTests
{
    enum SurfaceKind { Action, StateChannel, GeoEnvelope }

    // Derive the kind from the naming convention the file already uses. Order matters:
    // GeoSiteChannel / GeoVehicleChannel are prefixed "Geo" but are STATE CHANNELS, so the
    // "Channel" suffix must win over the "Geo" prefix.
    static SurfaceKind KindOf(string name)
    {
        if (name.EndsWith("Channel", StringComparison.Ordinal)) return SurfaceKind.StateChannel;
        if (name.StartsWith("Geo", StringComparison.Ordinal)) return SurfaceKind.GeoEnvelope;
        return SurfaceKind.Action;
    }

    static IReadOnlyList<(string Name, byte Value)> AllIds()
    {
        return typeof(SurfaceIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(byte))
            .Select(f => (f.Name, (byte)f.GetRawConstantValue()))
            .ToList();
    }

    [Fact]
    public void NoDuplicateIdWithinAnyKind()
    {
        var collisions = AllIds()
            .GroupBy(id => KindOf(id.Name))
            .SelectMany(kind => kind
                .GroupBy(id => id.Value)
                .Where(g => g.Count() > 1)
                .Select(g => $"{kind.Key} id {g.Key} shared by: {string.Join(", ", g.Select(x => x.Name))}"))
            .ToList();

        Assert.True(collisions.Count == 0,
            "SurfaceIds has values that collide WITHIN a kind (each would silently mis-route sync). "
            + "Give the new surface an id unused in its kind, or move it to a distinct kind:\n  "
            + string.Join("\n  ", collisions));
    }

    [Fact]
    public void EveryKindIsPopulated_AndClassifierRespectsPrecedence()
    {
        var byKind = AllIds()
            .GroupBy(id => KindOf(id.Name))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Reflection really found the fields and the classifier drops none: all three kinds present.
        Assert.All(new[] { SurfaceKind.Action, SurfaceKind.StateChannel, SurfaceKind.GeoEnvelope },
            k => Assert.True(byKind.TryGetValue(k, out var v) && v.Count > 0, $"kind {k} has no ids"));

        // Known INTENTIONAL cross-kind overlap: value 1 lives in both Action and StateChannel.
        Assert.Contains(byKind[SurfaceKind.Action], x => x.Value == 1);        // StartResearch
        Assert.Contains(byKind[SurfaceKind.StateChannel], x => x.Value == 1);  // InventoryChannel

        // Precedence rule (Channel suffix beats Geo prefix): GeoSiteChannel is a StateChannel, not GeoEnvelope.
        Assert.Contains(byKind[SurfaceKind.StateChannel], x => x.Name == "GeoSiteChannel");
        Assert.DoesNotContain(byKind[SurfaceKind.GeoEnvelope], x => x.Name == "GeoSiteChannel");
    }
}
