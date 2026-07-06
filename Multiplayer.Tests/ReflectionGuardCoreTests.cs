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
}
