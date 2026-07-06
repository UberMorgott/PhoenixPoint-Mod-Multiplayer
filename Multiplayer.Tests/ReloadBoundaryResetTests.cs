using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;
using Xunit;

/// <summary>
/// The rca-3 reload-boundary sweep aggregation contract: SyncEngine.ResetForReloadBoundary drives ONE
/// ReloadBoundaryReset holding every audited resettable — RunAll must call each exactly once, in
/// registration order, and a throwing entry must never skip the rest (OnMissionExit N1 idiom).
/// </summary>
public class ReloadBoundaryResetTests
{
    [Fact]
    public void RunAll_CallsEveryRegisteredResettableExactlyOnce_InRegistrationOrder()
    {
        var r = new ReloadBoundaryReset();
        var calls = new List<string>();
        r.Register("a", () => calls.Add("a"));
        r.Register("b", () => calls.Add("b"));
        r.Register("c", () => calls.Add("c"));

        r.RunAll();

        Assert.Equal(new[] { "a", "b", "c" }, calls);   // each exactly once, registration order
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void RunAll_ThrowingEntryDoesNotSkipTheRest_AndReportsItsName()
    {
        var r = new ReloadBoundaryReset();
        var calls = new List<string>();
        var errors = new List<string>();
        r.Register("first", () => calls.Add("first"));
        r.Register("boom", () => throw new InvalidOperationException("wedged"));
        r.Register("last", () => calls.Add("last"));

        r.RunAll((name, ex) => errors.Add(name + ":" + ex.Message));

        Assert.Equal(new[] { "first", "last" }, calls); // sweep continued past the throw
        Assert.Equal(new[] { "boom:wedged" }, errors);  // failure surfaced with the entry name
    }

    [Fact]
    public void RunAll_WithoutErrorSink_ThrowingEntryIsStillIsolated()
    {
        var r = new ReloadBoundaryReset();
        int after = 0;
        r.Register("boom", () => throw new Exception());
        r.Register("after", () => after++);

        r.RunAll();   // no sink → must swallow, not propagate

        Assert.Equal(1, after);
    }

    [Fact]
    public void RunAll_SecondBoundaryRunsEveryEntryAgain()
    {
        // The aggregate is re-runnable per boundary (a session can reload many times);
        // idempotency across repeats lives in the ENTRIES, not in the aggregator.
        var r = new ReloadBoundaryReset();
        int n = 0;
        r.Register("counter", () => n++);

        r.RunAll();
        r.RunAll();

        Assert.Equal(2, n);
    }

    [Fact]
    public void Register_NullResetIsIgnored()
    {
        var r = new ReloadBoundaryReset();
        r.Register("null-entry", null);
        Assert.Equal(0, r.Count);
        r.RunAll();   // and never crashes the sweep
    }

    [Fact]
    public void RunAll_ThrowingErrorSinkDoesNotBreakTheSweep()
    {
        var r = new ReloadBoundaryReset();
        int after = 0;
        r.Register("boom", () => throw new Exception());
        r.Register("after", () => after++);

        r.RunAll((name, ex) => throw new Exception("sink broke"));

        Assert.Equal(1, after);
    }
}
