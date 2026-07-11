using System.Collections.Generic;
using System.Linq;
using Multiplayer.Sync.Tactical;
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

    // gap-evac: EvacuatedStatus is force-INCLUDED regardless of the def's healthbar-visibility flag — its
    // whole client-relevant effect IS its InitVisualState (hide + animator off + vision forget), which the
    // engine runs even on the inert/deserialize branch. An evacuated actor draws no healthbar, so the
    // visibility flag is the wrong gate for this stance status.
    [Theory]
    [InlineData(0)]   // Hidden — the case the visibility gate alone would wrongly drop
    [InlineData(1)]   // VisibleWhenSelected
    [InlineData(5)]   // AlwaysVisible
    public void ShouldMirrorStatus_Evacuated_AlwaysMirrored(int visibility)
        => Assert.True(TacticalActorStateDiff.ShouldMirrorStatus(visibility, "EvacuatedStatus"));

    // No-regress sweep around the evac include: hidden UNLISTED statuses stay dropped (default-DENY intact,
    // incl. the mount bookkeeping the evac flow also touches), and the surface-owned/faction-safety excludes
    // still win for every visibility value.
    [Theory]
    [InlineData("MountedStatus")]        // evac applies it host-side; stays visibility-gated (not force-included)
    [InlineData("SomeFutureDlcStatus")]
    [InlineData("StunStatus")]
    public void ShouldMirrorStatus_HiddenUnlisted_StillDropped(string typeName)
        => Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(0, typeName));

    [Theory]
    [InlineData("OverwatchStatus")]
    [InlineData("MindControlStatus")]
    [InlineData("ZombifiedStatus")]
    public void ShouldMirrorStatus_ExcludeStillWins_AtEveryVisibility(string typeName)
    {
        foreach (var vis in new[] { 0, 1, 5 })
            Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(vis, typeName));
    }

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

    [Theory]
    [InlineData(1.0f)]    // walk-band floor — walks normally, but on the deploy path must SNAP
    [InlineData(5f)]
    [InlineData(40f)]     // walk-band ceiling
    public void PositionApply_ForceSnap_SnapsWalkBand(float dist)
    {
        // The DEPLOY path bypasses the walk band: the client's native deploy rolls its own cells a few units
        // off the host's, and soldiers must not RUN to their deploy spot at mission start — they snap.
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Walk,
            TacticalActorStateDiff.DecidePositionApply(dist));                          // normal path still walks
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.Teleport,
            TacticalActorStateDiff.DecidePositionApply(dist, forceSnap: true));         // deploy path snaps
    }

    [Fact]
    public void PositionApply_ForceSnap_ConvergedStillNoOps()
    {
        // Converged (≤ epsilon) stays None even under forceSnap — an idle re-apply must not churn the transform.
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.None,
            TacticalActorStateDiff.DecidePositionApply(0f, forceSnap: true));
        Assert.Equal(TacticalActorStateDiff.PositionApplyMode.None,
            TacticalActorStateDiff.DecidePositionApply(TacticalActorStateDiff.PositionEpsilon * 0.5f, forceSnap: true));
    }

    // ─── Inc2: facing-vector change decision (pure, per-component epsilon) ─────────────────────────────

    [Fact]
    public void FacingChanged_SubEpsilon_False()
        => Assert.False(TacticalActorStateDiff.FacingChanged(0f, 0f, 1f, 0.005f, 0f, 0.999f));

    [Fact]
    public void FacingChanged_OverEpsilon_True()
        => Assert.True(TacticalActorStateDiff.FacingChanged(0f, 0f, 1f, 1f, 0f, 0f));

    // ─── bug C: status display-magnitude → DamageAccumulation.InitialAmount mapping ───────────────────

    [Fact]
    public void Magnitude_Bleed_NoDamagePerTurn_MapsOneToOne()
    {
        // BleedStatus.Value = (int)InitialAmount → InitialAmount = value (Bleed has no DamagePerTurn → pass 0).
        Assert.Equal(20f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(20f, 0f));
    }

    [Fact]
    public void Magnitude_Dot_ScalesByDamagePerTurn()
    {
        // DamageOverTimeStatus.Value = InitialAmount / DamagePerTurn → InitialAmount = value * DamagePerTurn.
        Assert.Equal(150f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(30f, 5f));
    }

    [Fact]
    public void Magnitude_NaNOrNonPositiveDamagePerTurn_MapsOneToOne()
    {
        Assert.Equal(12f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(12f, float.NaN));
        Assert.Equal(12f, TacticalActorStateDiff.StatusMagnitudeToInitialAmount(12f, -3f));
    }

    // ─── Inc2 follow-up: in-place status-magnitude REFRESH decision (present key, host value drift) ─────
    //
    // The reconcile diff identity {DefGuid, SourceNetId} ignores Value, so a magnitude change on an ALREADY-
    // present mirrored status is invisible to ToAdd/ToRemove — left untouched, the client display level goes
    // stale. This pure predicate gates a separate in-place refresh of the mirror's DamageAccumulation.
    // InitialAmount: refresh iff the host magnitude drifted beyond epsilon; converged / sub-epsilon jitter is
    // a no-op (no churn, no re-write).

    [Fact]
    public void ShouldRefreshMagnitude_DriftOverEpsilon_True()
    {
        // Bleed stacked from a 2nd shot (level 5 → 7) — the present-key mirror must refresh in place.
        Assert.True(TacticalActorStateDiff.ShouldRefreshMagnitude(5f, 7f));
        // DoT ticking down each turn (level 30 → 25) — likewise.
        Assert.True(TacticalActorStateDiff.ShouldRefreshMagnitude(30f, 25f));
    }

    [Fact]
    public void ShouldRefreshMagnitude_Equal_NoOp()
        => Assert.False(TacticalActorStateDiff.ShouldRefreshMagnitude(12f, 12f));

    [Fact]
    public void ShouldRefreshMagnitude_SubEpsilonJitter_NoOp()
    {
        // A sub-epsilon float jitter must NOT re-write the accumulator (avoid churn).
        Assert.False(TacticalActorStateDiff.ShouldRefreshMagnitude(
            12f, 12f + TacticalActorStateDiff.StatusMagnitudeEpsilon * 0.5f));
        Assert.False(TacticalActorStateDiff.ShouldRefreshMagnitude(12f, 12.001f));
    }

    // ─── TS5 (a): per-weapon ammo signature fragment ──────────────────────────────────────────────────

    [Fact]
    public void AmmoSignature_EmptyOrNull_IsEmpty()
    {
        Assert.Equal("", TacticalActorStateDiff.AmmoSignature(null));
        Assert.Equal("", TacticalActorStateDiff.AmmoSignature(new List<TacticalLiveCodec.WeaponAmmo>()));
    }

    [Fact]
    public void AmmoSignature_ReflectsAChargeChange()
    {
        // A reload (magazine 0 → 6 on slot 1) MUST drift the signature so the host re-broadcasts the actor.
        string before = TacticalActorStateDiff.AmmoSignature(new List<TacticalLiveCodec.WeaponAmmo>
        { new TacticalLiveCodec.WeaponAmmo(1, 0) });
        string after = TacticalActorStateDiff.AmmoSignature(new List<TacticalLiveCodec.WeaponAmmo>
        { new TacticalLiveCodec.WeaponAmmo(1, 6) });
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AmmoSignature_IsOrderStable()
    {
        // Same multiset in different enumeration order → identical signature (no spurious re-broadcast).
        string a = TacticalActorStateDiff.AmmoSignature(new List<TacticalLiveCodec.WeaponAmmo>
        { new TacticalLiveCodec.WeaponAmmo(2, 12), new TacticalLiveCodec.WeaponAmmo(0, 6) });
        string b = TacticalActorStateDiff.AmmoSignature(new List<TacticalLiveCodec.WeaponAmmo>
        { new TacticalLiveCodec.WeaponAmmo(0, 6), new TacticalLiveCodec.WeaponAmmo(2, 12) });
        Assert.Equal(a, b);
    }

    // ─── TS5 (b): mind-control faction DISPLAY-apply decision (display-only, change-gated) ───────────────

    [Fact]
    public void ShouldApplyFactionDisplay_OnActualChange_True()
    {
        // A mind-control flip (was faction 0, host now says 1) repaints.
        Assert.True(TacticalActorStateDiff.ShouldApplyFactionDisplay(0, 1));
    }

    [Fact]
    public void ShouldApplyFactionDisplay_Unchanged_NoOp()
    {
        // The 4 Hz re-apply of an already-flipped unit must NOT repaint again (no FactionChangedEvent churn).
        Assert.False(TacticalActorStateDiff.ShouldApplyFactionDisplay(1, 1));
    }

    [Fact]
    public void ShouldApplyFactionDisplay_NegativeIncoming_NoOp()
    {
        // No real target faction (-1) → never stamp a display faction.
        Assert.False(TacticalActorStateDiff.ShouldApplyFactionDisplay(0, -1));
        Assert.False(TacticalActorStateDiff.ShouldApplyFactionDisplay(-1, -1));
    }
}
