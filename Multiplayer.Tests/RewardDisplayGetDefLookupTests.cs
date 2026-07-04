using System;
using System.Reflection;
using Multiplayer.Network.Sync.State;
using Xunit;

/// <summary>
/// Regression test for the client RESULT-card "zero reward lines" bug.
///
/// The game type <c>Base.Defs.NamedListDef</c> declares BOTH a non-generic <c>GetDef(string)</c>
/// (NamedListDef.cs:24) AND a generic <c>GetDef&lt;T&gt;(string)</c> (NamedListDef.cs:34). The original
/// lookup <c>AccessTools.Method(t, "GetDef", new[]{ typeof(string) })</c> resolves to
/// <c>Type.GetMethod(name, Type[])</c>, which matches BOTH single-<c>string</c> overloads and throws
/// <see cref="AmbiguousMatchException"/>. That throw was swallowed by <c>Render</c>'s outer try/catch,
/// silently killing EVERY reward delta line on the client result page.
///
/// <c>RewardDisplayReflection.EnsureClient</c> needs the GENERIC overload: it later calls
/// <c>.MakeGenericMethod(ViewElementDef)</c> and invokes the closed method to resolve a resource's
/// display name (RewardDisplayReflection.cs:454-455, :651). <see cref="MethodOverloadResolver"/>
/// fetches that open generic definition unambiguously.
/// </summary>
public class RewardDisplayGetDefLookupTests
{
    // Test double mirroring NamedListDef's two single-string GetDef overloads.
    private class FakeNamedList
    {
        public object GetDef(string name) => null;                       // non-generic (NamedListDef.cs:24)
        public T GetDef<T>(string name) where T : class => null;          // generic     (NamedListDef.cs:34)
    }

    private class FakePayload { }

    [Fact]
    public void OldLookup_ByParameterTypes_ThrowsAmbiguousMatch()
    {
        // Documents the bug: matching only by the single string parameter is ambiguous across the two overloads.
        Assert.Throws<AmbiguousMatchException>(() =>
            typeof(FakeNamedList).GetMethod("GetDef", new[] { typeof(string) }));
    }

    [Fact]
    public void Resolver_ReturnsGenericOpenDefinition_WithoutThrowing()
    {
        var m = MethodOverloadResolver.FindGenericSingleStringMethod(typeof(FakeNamedList), "GetDef");

        Assert.NotNull(m);
        Assert.True(m.IsGenericMethodDefinition);          // Render needs the open generic GetDef<T>(string)
        var ps = m.GetParameters();
        Assert.Single(ps);
        Assert.Equal(typeof(string), ps[0].ParameterType);

        // And it must be closable + invocable like Render does (MakeGenericMethod + Invoke).
        var closed = m.MakeGenericMethod(typeof(FakePayload));
        Assert.False(closed.IsGenericMethodDefinition);
        var result = closed.Invoke(new FakeNamedList(), new object[] { "anything" });
        Assert.Null(result);
    }

    [Fact]
    public void Resolver_ReturnsNull_WhenNoGenericSingleStringOverloadExists()
    {
        Assert.Null(MethodOverloadResolver.FindGenericSingleStringMethod(typeof(FakePayload), "GetDef"));
        Assert.Null(MethodOverloadResolver.FindGenericSingleStringMethod(typeof(FakeNamedList), "Nope"));
    }
}
