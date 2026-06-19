using System.Collections.Generic;
using System.Linq;
using Multipleer.Sync.Tactical;
using Xunit;

// Pure status-reconcile diff + include/exclude policy + signature stability (Inc T1 state-spine). Engine-free.
public class TacticalActorStateDiffTests
{
    private static TacticalActorStateDiff.StatusRec S(string guid, int src, float val = 0f)
        => new TacticalActorStateDiff.StatusRec(guid, src, val);

    private static List<TacticalActorStateDiff.StatusRec> Set(params TacticalActorStateDiff.StatusRec[] recs)
        => recs.ToList();

    // ─── reconcile diff ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddNew_ProducesToAdd()
    {
        var diff = TacticalActorStateDiff.Compute(Set(), Set(S("poison", 5), S("frenzy", -1)));
        Assert.Equal(2, diff.ToAdd.Count);
        Assert.Empty(diff.ToRemove);
        Assert.Contains(diff.ToAdd, r => r.DefGuid == "poison" && r.SourceNetId == 5);
        Assert.Contains(diff.ToAdd, r => r.DefGuid == "frenzy" && r.SourceNetId == -1);
    }

    [Fact]
    public void RemoveAbsent_ProducesToRemove()
    {
        var diff = TacticalActorStateDiff.Compute(Set(S("poison", 5), S("shield", -1)), Set(S("poison", 5)));
        Assert.Empty(diff.ToAdd);
        var r = Assert.Single(diff.ToRemove);
        Assert.Equal("shield", r.DefGuid);
    }

    [Fact]
    public void IdenticalSet_NoChanges_Idempotent()
    {
        var set = Set(S("poison", 5), S("frenzy", -1));
        var diff = TacticalActorStateDiff.Compute(set, Set(S("poison", 5), S("frenzy", -1)));
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.ToAdd);
        Assert.Empty(diff.ToRemove);
    }

    [Fact]
    public void SameDef_DifferentSource_AreDistinct()
    {
        // Same def guid but different source actor → distinct identities (add the new, remove the old).
        var diff = TacticalActorStateDiff.Compute(Set(S("poison", 5)), Set(S("poison", 9)));
        Assert.Single(diff.ToAdd);
        Assert.Equal(9, diff.ToAdd[0].SourceNetId);
        Assert.Single(diff.ToRemove);
        Assert.Equal(5, diff.ToRemove[0].SourceNetId);
    }

    [Fact]
    public void ValueDrift_DoesNotForceReadd()
    {
        // Same {defGuid, source}, only the carried value changed → NOT a remove+re-add (would re-run OnApply).
        var diff = TacticalActorStateDiff.Compute(Set(S("poison", 5, 3f)), Set(S("poison", 5, 1f)));
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Mixed_AddAndRemove()
    {
        var diff = TacticalActorStateDiff.Compute(
            Set(S("a", 1), S("b", 2)),
            Set(S("b", 2), S("c", 3)));
        Assert.Single(diff.ToAdd);
        Assert.Equal("c", diff.ToAdd[0].DefGuid);
        Assert.Single(diff.ToRemove);
        Assert.Equal("a", diff.ToRemove[0].DefGuid);
    }

    [Fact]
    public void NullInputs_TreatedAsEmpty()
    {
        var diff = TacticalActorStateDiff.Compute(null, null);
        Assert.False(diff.HasChanges);
    }

    // ─── include/exclude policy ─────────────────────────────────────────────────────────────────────

    // Policy is DEFAULT-DENY (vetted allowlist, empty in T1 — status sync gated off). The KNOWN-UNSAFE types
    // must ALWAYS be denied (and must never be added to the allowlist); re-running their OnApply on the client
    // diverges (faction flip / AP reduction / state machine / stat-mod double-add / DoT double-damage / owned
    // by another surface).
    [Theory]
    [InlineData("MindControlStatus")]          // flips faction + subscribes ActorDeathEvent
    [InlineData("StunStatus")]                 // reduces AP + fires suppression event
    [InlineData("PanicStatus")]                // drives an AI/behaviour state machine
    [InlineData("StatsModifyStatus")]          // re-adds its AP/WP delta on top of the absolute value
    [InlineData("OverwatchStatus")]            // owned by tac.overwatch.state
    [InlineData("DamageOverTimeStatus")]       // DoT family → tac.damage
    [InlineData("FireStatus")]
    [InlineData("AcidStatus")]
    [InlineData("InfectedStatus")]
    [InlineData("BleedStatus")]
    [InlineData("ZombifiedStatus")]
    [InlineData("ParalysisDamageOverTimeStatus")]
    public void KnownUnsafeStatuses_AreNeverSyncable(string typeName)
        => Assert.False(TacticalActorStateDiff.IsSyncableStatusType(typeName));

    // Default-DENY: an unreviewed status (not on the empty T1 allowlist) is NOT syncable — buffs/debuffs/stances
    // included. They become syncable only when explicitly vetted + added to the allowlist (2-instance verified).
    [Theory]
    [InlineData("ParalysedStatus")]
    [InlineData("FrenzyStatus")]
    [InlineData("ShieldDeployedStatus")]
    [InlineData("SilencedStatus")]
    [InlineData("MountedStatus")]
    [InlineData("SomeFutureDlcStatus")]        // an unknown/DLC status must NOT auto-sync
    public void UnvettedStatuses_AreNotSyncableByDefault(string typeName)
        => Assert.False(TacticalActorStateDiff.IsSyncableStatusType(typeName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyTypeName_NotSyncable(string typeName)
        => Assert.False(TacticalActorStateDiff.IsSyncableStatusType(typeName));

    // ─── signature ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_OrderIndependent_ForStatusSet()
    {
        string a = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 5f), S("y", 2, 6f)));
        string b = TacticalActorStateDiff.Signature(3f, 2f, Set(S("y", 2, 6f), S("x", 1, 5f)));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Signature_ChangesWhenApChanges()
    {
        string a = TacticalActorStateDiff.Signature(3f, 2f, Set());
        string b = TacticalActorStateDiff.Signature(2f, 2f, Set());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Signature_ChangesWhenStatusAddedOrValueChanges()
    {
        string baseSig = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 5f)));
        string added = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 5f), S("y", 2, 1f)));
        string valChanged = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 9f)));
        Assert.NotEqual(baseSig, added);
        Assert.NotEqual(baseSig, valChanged);
    }

    [Fact]
    public void Signature_StableForEqualState()
    {
        string a = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 5f)));
        string b = TacticalActorStateDiff.Signature(3f, 2f, Set(S("x", 1, 5f)));
        Assert.Equal(a, b);
    }
}
