using System;
using System.Collections.Generic;
using Xunit;

// Characterization tests for the reflective-construction trap that aborted the host tactical deploy
// (RCA 2026-06-18, round-4 2-instance log decode). HostCaptureAndBroadcast → SerializeGraph threw:
//   System.MissingMethodException: Default constructor not found for type
//   Base.Utils.ByRef`1[[System.Byte[]…]]
// at Activator.CreateInstance(byRefBytes)  → "native serialize failed — skipping deploy" → no tac.deploy.
//
// Root cause: the game's Base.Utils.ByRef<T> has EXACTLY ONE constructor — `ByRef(T value = default(T))`
// — i.e. a single OPTIONAL parameter, NO parameterless ctor. In C# source `new ByRef<byte[]>()` compiles
// (the compiler injects `default`), but `Activator.CreateInstance(Type)` resolves the *runtime*
// parameterless ctor, which does not exist, so it throws MissingMethodException. The fix is to pass the
// argument explicitly: `Activator.CreateInstance(t, new object[]{ default })`.
//
// These tests pin that .NET reflection invariant against a local generic with the IDENTICAL ctor shape,
// so the deploy-serialization construction pattern can never silently regress to the no-arg form.
public class ByRefActivatorCharacterizationTests
{
    // Mirrors Base.Utils.ByRef<T>: one ctor, a single optional parameter, no parameterless ctor.
    private sealed class ByRefLike<T>
    {
        public T Value;
        public ByRefLike(T value = default(T)) { Value = value; }
    }

    [Fact]
    public void NoArgActivator_OnOptionalParamCtor_Throws_TheDeployBug()
    {
        // The exact failing call shape from the old SerializeGraph: CreateInstance(Type) with no args.
        var t = typeof(ByRefLike<byte[]>);
        Assert.Throws<MissingMethodException>(() => Activator.CreateInstance(t));
    }

    [Fact]
    public void ExplicitNullArg_Constructs_ByRefOfByteArray()
    {
        // The fix: pass the optional argument explicitly (default(byte[]) == null).
        var t = typeof(ByRefLike<byte[]>);
        object inst = Activator.CreateInstance(t, new object[] { null });
        Assert.NotNull(inst);
        Assert.Null(((ByRefLike<byte[]>)inst).Value);
    }

    [Fact]
    public void ExplicitNullArg_Constructs_ByRefOfEnumerable()
    {
        // The sibling site in DeserializeGraph: ByRef<IEnumerable<object>> — same trap, would have blown
        // up on the CLIENT immediately after the host bug was cleared. default(IEnumerable<object>)==null.
        var t = typeof(ByRefLike<IEnumerable<object>>);
        object inst = Activator.CreateInstance(t, new object[] { null });
        Assert.NotNull(inst);
        Assert.Null(((ByRefLike<IEnumerable<object>>)inst).Value);
    }

    [Fact]
    public void ConstructedByRefLike_RoundTripsValue()
    {
        // Sanity: the constructed wrapper actually carries the Value the serializer Write fills in.
        var t = typeof(ByRefLike<byte[]>);
        var inst = (ByRefLike<byte[]>)Activator.CreateInstance(t, new object[] { null });
        inst.Value = new byte[] { 1, 2, 3 };
        Assert.Equal(3, inst.Value.Length);
    }
}
