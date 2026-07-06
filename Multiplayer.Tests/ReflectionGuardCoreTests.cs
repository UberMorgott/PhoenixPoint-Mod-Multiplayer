using System.Collections.Generic;
using Multiplayer.Network;
using Xunit;

public class ReflectionGuardCoreTests
{
    [Fact]
    public void AllResolved_IsCompatible_NoMissingNoMessage()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("GeoLevelController.OnTurnChanged", true),
            new CriticalBinding("GeoscapeViewState.OnEnter", true),
            new CriticalBinding("TacticalLevelController.Deploy", true),
        };

        var verdict = ReflectionGuardCore.Evaluate(bindings);

        Assert.True(verdict.Compatible);
        Assert.Null(verdict.MissingMember);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public void SingleUnresolved_IsIncompatible_ExactMessageAndMember()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("GeoLevelController.OnTurnChanged", true),
            new CriticalBinding("GeoscapeViewState.OnEnter", false),
        };

        var verdict = ReflectionGuardCore.Evaluate(bindings);

        Assert.False(verdict.Compatible);
        Assert.Equal("GeoscapeViewState.OnEnter", verdict.MissingMember);
        Assert.Equal(
            "Multiplayer mod: incompatible Phoenix Point version (missing GeoscapeViewState.OnEnter). Update the mod.",
            verdict.Message);
    }

    [Fact]
    public void MultipleUnresolved_NamesTheFirstInOrder()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("GeoLevelController.OnTurnChanged", true),
            new CriticalBinding("GeoscapeViewState.OnEnter", false),
            new CriticalBinding("TacticalLevelController.Deploy", false),
        };

        var verdict = ReflectionGuardCore.Evaluate(bindings);

        Assert.False(verdict.Compatible);
        // First unresolved in input order wins — deterministic reporting.
        Assert.Equal("GeoscapeViewState.OnEnter", verdict.MissingMember);
        Assert.Equal(
            "Multiplayer mod: incompatible Phoenix Point version (missing GeoscapeViewState.OnEnter). Update the mod.",
            verdict.Message);
    }

    [Fact]
    public void FirstBindingUnresolved_NamesThatBinding()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("GeoLevelController.OnTurnChanged", false),
            new CriticalBinding("GeoscapeViewState.OnEnter", false),
        };

        var verdict = ReflectionGuardCore.Evaluate(bindings);

        Assert.False(verdict.Compatible);
        Assert.Equal("GeoLevelController.OnTurnChanged", verdict.MissingMember);
        Assert.Equal(
            "Multiplayer mod: incompatible Phoenix Point version (missing GeoLevelController.OnTurnChanged). Update the mod.",
            verdict.Message);
    }

    [Fact]
    public void EmptyInput_IsCompatible()
    {
        var verdict = ReflectionGuardCore.Evaluate(new List<CriticalBinding>());

        Assert.True(verdict.Compatible);
        Assert.Null(verdict.MissingMember);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public void NullInput_IsCompatible()
    {
        var verdict = ReflectionGuardCore.Evaluate(null);

        Assert.True(verdict.Compatible);
        Assert.Null(verdict.MissingMember);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public void BuildMessage_MatchesExactWording()
    {
        Assert.Equal(
            "Multiplayer mod: incompatible Phoenix Point version (missing SomeType.SomeMember). Update the mod.",
            ReflectionGuardCore.BuildMessage("SomeType.SomeMember"));
    }

    // ---- UnresolvedMembers: enumerate EVERY failure (companion to Evaluate's first-only verdict) ----

    [Fact]
    public void UnresolvedMembers_AllResolved_IsEmpty()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", true),
            new CriticalBinding("B.b", true),
        };

        Assert.Empty(ReflectionGuardCore.UnresolvedMembers(bindings));
    }

    [Fact]
    public void UnresolvedMembers_ListsAllUnresolvedInOrder()
    {
        var bindings = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", true),
            new CriticalBinding("B.b", false),
            new CriticalBinding("C.c", true),
            new CriticalBinding("D.d", false),
        };

        Assert.Equal(new[] { "B.b", "D.d" }, ReflectionGuardCore.UnresolvedMembers(bindings));
    }

    [Fact]
    public void UnresolvedMembers_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(ReflectionGuardCore.UnresolvedMembers(null));
        Assert.Empty(ReflectionGuardCore.UnresolvedMembers(new List<CriticalBinding>()));
    }

    // ---- BuildStartupReport: the ONE prominent multi-binding log block ----

    [Fact]
    public void BuildStartupReport_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ReflectionGuardCore.BuildStartupReport(null));
        Assert.Null(ReflectionGuardCore.BuildStartupReport(new List<string>()));
    }

    [Fact]
    public void BuildStartupReport_SingleMember_ExactBlock_Singular()
    {
        string rule = new string('=', 60);
        string expected =
            rule + "\n" +
            "Multiplayer mod: INCOMPATIBLE Phoenix Point version.\n" +
            "1 critical reflection binding failed to resolve - co-op sync WILL break. Update the mod.\n" +
            "Missing:\n" +
            "  - GeoSite.SiteId\n" +
            rule;

        Assert.Equal(expected, ReflectionGuardCore.BuildStartupReport(new[] { "GeoSite.SiteId" }));
    }

    [Fact]
    public void BuildStartupReport_MultipleMembers_ExactBlock_Plural_InOrder()
    {
        string rule = new string('=', 60);
        string expected =
            rule + "\n" +
            "Multiplayer mod: INCOMPATIBLE Phoenix Point version.\n" +
            "2 critical reflection bindings failed to resolve - co-op sync WILL break. Update the mod.\n" +
            "Missing:\n" +
            "  - GeoSite.SiteId\n" +
            "  - GeoLevelController.Map\n" +
            rule;

        Assert.Equal(
            expected,
            ReflectionGuardCore.BuildStartupReport(new[] { "GeoSite.SiteId", "GeoLevelController.Map" }));
    }

    // ---- Phase 5b soft-gate: compat predicate + report reuse the co-op gate latches on ----
    // The startup self-check now decides compatible/incompatible from UnresolvedMembers(...).Count
    // (dropping the Evaluate re-iteration), and persists BuildStartupReport(...) as the gate's reason.
    // These pin that the new predicate agrees with Evaluate and that the persisted report is built
    // from the SAME unresolved list — so the firing simplification is behavior-preserving.

    [Fact]
    public void UnresolvedCount_AgreesWith_EvaluateCompatible()
    {
        var allResolved = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", true),
            new CriticalBinding("B.b", true),
        };
        var someUnresolved = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", true),
            new CriticalBinding("B.b", false),
        };

        // compatible  <=>  no unresolved members
        Assert.Equal(ReflectionGuardCore.Evaluate(allResolved).Compatible,
                     ReflectionGuardCore.UnresolvedMembers(allResolved).Count == 0);
        Assert.Equal(ReflectionGuardCore.Evaluate(someUnresolved).Compatible,
                     ReflectionGuardCore.UnresolvedMembers(someUnresolved).Count == 0);
    }

    [Fact]
    public void GateReport_NullWhenCompatible_BuiltFromUnresolvedWhenNot()
    {
        var allResolved = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", true),
        };
        var someUnresolved = new List<CriticalBinding>
        {
            new CriticalBinding("A.a", false),
            new CriticalBinding("B.b", false),
        };

        // Compatible install → nothing to persist for the gate (no report).
        Assert.Null(ReflectionGuardCore.BuildStartupReport(
            ReflectionGuardCore.UnresolvedMembers(allResolved)));

        // Incompatible install → the gate's persisted reason is the report of the unresolved list.
        var report = ReflectionGuardCore.BuildStartupReport(
            ReflectionGuardCore.UnresolvedMembers(someUnresolved));
        Assert.NotNull(report);
        Assert.Contains("A.a", report);
        Assert.Contains("B.b", report);
    }

    // ---- ValidateLabels: dev-facing integrity check of the curated list itself ----

    [Fact]
    public void ValidateLabels_WellFormedList_NoProblems()
    {
        var labels = new[] { "PhoenixSaveManager.LatestLoad", "GeoSite.SiteId", "Serializer" };

        Assert.Empty(ReflectionGuardCore.ValidateLabels(labels));
    }

    [Fact]
    public void ValidateLabels_NullList_ReportsProblem()
    {
        Assert.Single(ReflectionGuardCore.ValidateLabels(null));
    }

    [Fact]
    public void ValidateLabels_EmptyList_ReportsProblem()
    {
        Assert.Single(ReflectionGuardCore.ValidateLabels(new string[0]));
    }

    [Fact]
    public void ValidateLabels_BlankLabel_ReportsProblem()
    {
        var labels = new[] { "GeoSite.SiteId", "  " };

        var problems = ReflectionGuardCore.ValidateLabels(labels);

        Assert.Contains(problems, p => p.Contains("index 1"));
    }

    [Fact]
    public void ValidateLabels_DuplicateLabel_ReportsProblem()
    {
        var labels = new[] { "GeoSite.SiteId", "GeoSite.SiteId" };

        var problems = ReflectionGuardCore.ValidateLabels(labels);

        Assert.Contains(problems, p => p.Contains("duplicate") && p.Contains("GeoSite.SiteId"));
    }
}
