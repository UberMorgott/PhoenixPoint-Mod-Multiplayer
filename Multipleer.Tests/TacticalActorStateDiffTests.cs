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

    // ─── Feature B: visual-only status mirror decision (VisibleOnHealthbar != Hidden) ─────────────────

    [Fact]
    public void ShouldMirrorStatus_Hidden_False()
        => Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(TacticalActorStateDiff.HealthBarVisibilityHidden));

    [Theory]
    [InlineData(1)]   // VisibleWhenSelected
    [InlineData(5)]   // AlwaysVisible
    public void ShouldMirrorStatus_NonHidden_True(int visibility)
        => Assert.True(TacticalActorStateDiff.ShouldMirrorStatus(visibility));

    [Theory]
    [InlineData("BleedStatus")]    // DoT icon IS mirrored (made inert by guards) — the headline Feature B case
    [InlineData("FireStatus")]
    [InlineData("ParalysedStatus")]
    [InlineData("FrenzyStatus")]
    public void ShouldMirrorStatus_VisibleNonSurfaceOwned_True(string typeName)
        => Assert.True(TacticalActorStateDiff.ShouldMirrorStatus(5, typeName));

    [Fact]
    public void ShouldMirrorStatus_OverwatchStatus_Excluded_EvenWhenVisible()
        => Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(5, "OverwatchStatus"));

    // Faction-changer statuses are excluded even when visible: applying them live (even "inert") risks flipping
    // the actor's faction on the client (MindControl leaks an unconditional ActorDeathEvent sub; Zombified flips
    // if the Applied pre-set fails). Faction-safety > the badge.
    [Theory]
    [InlineData("MindControlStatus")]
    [InlineData("ZombifiedStatus")]
    public void ShouldMirrorStatus_FactionChanger_Excluded_EvenWhenVisible(string typeName)
        => Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(5, typeName));

    [Fact]
    public void ShouldMirrorStatus_HiddenWins_OverTypeName()
        => Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(0, "BleedStatus"));

    // ─── Feature B PART 1: per-bodypart-HP reconcile diff ─────────────────────────────────────────────

    private static TacticalActorStateDiff.BodyPartHpRec B(string slot, float hp)
        => new TacticalActorStateDiff.BodyPartHpRec(slot, hp);

    private static System.Collections.Generic.Dictionary<string, float> Cur(
        params (string slot, float hp)[] parts)
    {
        var d = new System.Collections.Generic.Dictionary<string, float>();
        foreach (var p in parts) d[p.slot] = p.hp;
        return d;
    }

    [Fact]
    public void BodyPartHpDiff_NewPart_IsApplied()
    {
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(Cur(), new[] { B("Torso", 100f) });
        var r = Assert.Single(toApply);
        Assert.Equal("Torso", r.SlotName);
        Assert.Equal(100f, r.Hp);
    }

    [Fact]
    public void BodyPartHpDiff_ChangedHp_IsApplied()
    {
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(
            Cur(("LeftArm", 80f)), new[] { B("LeftArm", 0f) });   // limb knocked out
        var r = Assert.Single(toApply);
        Assert.Equal("LeftArm", r.SlotName);
        Assert.Equal(0f, r.Hp);
    }

    [Fact]
    public void BodyPartHpDiff_SameHp_NoOp_Idempotent()
    {
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(
            Cur(("Torso", 100f), ("Head", 50f)),
            new[] { B("Torso", 100f), B("Head", 50f) });
        Assert.Empty(toApply);
    }

    [Fact]
    public void BodyPartHpDiff_SubEpsilonJitter_NoOp()
    {
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(
            Cur(("Torso", 100f)), new[] { B("Torso", 100.001f) });
        Assert.Empty(toApply);
    }

    [Fact]
    public void BodyPartHpDiff_HostSilentOnAPart_LeavesItUntouched()
    {
        // The host mentions only Torso; the client's LeftArm (already 0 / disabled) is NOT in the incoming set
        // → it must NOT be re-applied (absent ≠ restore). Only the mentioned changed part is returned.
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(
            Cur(("Torso", 100f), ("LeftArm", 0f)), new[] { B("Torso", 70f) });
        var r = Assert.Single(toApply);
        Assert.Equal("Torso", r.SlotName);
        Assert.Equal(70f, r.Hp);
    }

    [Fact]
    public void BodyPartHpDiff_NullInputs_Empty()
    {
        Assert.Empty(TacticalActorStateDiff.ComputeBodyPartHpDiff(null, null));
        Assert.Empty(TacticalActorStateDiff.ComputeBodyPartHpDiff(Cur(("a", 1f)), null));
    }

    [Fact]
    public void BodyPartHpDiff_BlankSlotName_Skipped()
    {
        var toApply = TacticalActorStateDiff.ComputeBodyPartHpDiff(Cur(), new[] { B("", 100f), B(null, 5f) });
        Assert.Empty(toApply);
    }

    // ─── Feature D: death-safe actor-HP mirror decision ───────────────────────────────────────────────

    [Fact]
    public void HealthMirror_IncomingPositive_Drifted_AppliesIncoming()
    {
        // Host healed the actor 50 → 80; the client at 50 must SET 80 (heal converges).
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(50f, 80f, out float v);
        Assert.True(apply);
        Assert.Equal(80f, v);
    }

    [Fact]
    public void HealthMirror_IncomingDownButPositive_AppliesIncoming()
    {
        // Drift correction downward but still ALIVE (>0) → applied (it can never cross to <threshold).
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(80f, 30f, out float v);
        Assert.True(apply);
        Assert.Equal(30f, v);
    }

    [Fact]
    public void HealthMirror_IncomingZero_Skips_DeathOwnedByDamage()
    {
        // Incoming <= 0 → DO NOT apply (death owned by tac.damage; setting through 0 would fire Die()).
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(40f, 0f, out _);
        Assert.False(apply);
    }

    [Fact]
    public void HealthMirror_IncomingNegative_Skips()
    {
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(40f, -5f, out _);
        Assert.False(apply);
    }

    [Fact]
    public void HealthMirror_IncomingBelowDeathThreshold_Skips()
    {
        // A tiny sub-threshold positive (≤ 1E-05) is treated as death → skip (death-safe).
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(40f, 1e-06f, out _);
        Assert.False(apply);
    }

    [Fact]
    public void HealthMirror_EqualWithinEpsilon_NoOp()
    {
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(100f, 100f, out _);
        Assert.False(apply);
    }

    [Fact]
    public void HealthMirror_SubEpsilonJitter_NoOp()
    {
        bool apply = TacticalActorStateDiff.ShouldApplyHealthMirror(100f, 100.001f, out _);
        Assert.False(apply);
    }

    // ─── Inc1 full-state: position-delta WALK-vs-TELEPORT decision (pure, distance-driven) ─────────────
    //
    // The client receives an actor's absolute position in the 0x8F delta. The engine glue computes the
    // distance from the actor's current mirror pos to the incoming pos and asks this pure function how to
    // present it: snap nothing (None, already converged), drive the NATIVE walk animation (Walk), or snap
    // instantly (Teleport — a sub-cell nudge OR a large/disconnected jump that should not animate a long walk).

    [Fact]
    public void PositionApply_SubEpsilon_None()
    {
        // Already converged (≤ epsilon) → no-op: do not re-trigger Navigate or SetPosition (avoid churn).
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.None,
            TacticalActorStateDiff.DecidePositionApply(0f));
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.None,
            TacticalActorStateDiff.DecidePositionApply(TacticalActorStateDiff.PositionEpsilon * 0.5f));
    }

    [Fact]
    public void PositionApply_SubCell_Teleport()
    {
        // Above epsilon but below one grid cell → a degenerate path (0/1 node) would OOR in the native
        // ExecutePoints, so SNAP instantly instead of animating.
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Teleport,
            TacticalActorStateDiff.DecidePositionApply(0.5f));
    }

    [Theory]
    [InlineData(1.0f)]    // exactly the walk floor (one cell) → walk
    [InlineData(5f)]
    [InlineData(20f)]
    [InlineData(40f)]     // exactly the teleport ceiling → still walk (inclusive upper bound)
    public void PositionApply_PlausibleMove_Walk(float dist)
        => Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Walk,
            TacticalActorStateDiff.DecidePositionApply(dist));

    [Fact]
    public void PositionApply_LargeJump_Teleport()
    {
        // Beyond the ceiling (a disconnected jump / first-seen pos / post-loss catch-up) → snap, never
        // animate an absurd cross-map walk.
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Teleport,
            TacticalActorStateDiff.DecidePositionApply(TacticalActorStateDiff.PositionTeleportMaxDist + 1f));
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Teleport,
            TacticalActorStateDiff.DecidePositionApply(500f));
    }

    [Fact]
    public void PositionApply_WalkFloor_IsOneCell_MatchesMoveRail()
    {
        // The walk floor must equal one grid cell (≈ the move rail's MoveAnimateMinDist = 1.0) so the delta
        // path and the tac.move.start rail make the SAME walk/teleport choice for the same distance.
        Assert.Equal(1.0f, TacticalActorStateDiff.PositionWalkMinDist);
    }

    [Fact]
    public void PositionApply_BandBoundaries_AreOrdered()
    {
        Assert.True(TacticalActorStateDiff.PositionEpsilon < TacticalActorStateDiff.PositionWalkMinDist);
        Assert.True(TacticalActorStateDiff.PositionWalkMinDist < TacticalActorStateDiff.PositionTeleportMaxDist);
    }

    // ─── Inc2: facing-vector change decision (pure, per-component epsilon) ─────────────────────────────

    [Fact]
    public void FacingChanged_SubEpsilon_False()
        => Assert.False(TacticalActorStateDiff.FacingChanged(0f, 0f, 1f, 0.005f, 0f, 0.999f));

    [Fact]
    public void FacingChanged_OverEpsilon_True()
        => Assert.True(TacticalActorStateDiff.FacingChanged(0f, 0f, 1f, 1f, 0f, 0f));
}
