using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE policy tests for the generic ability-intent relay allowlist (state-spine §3, Inc T2). No engine
/// types. Asserts the relayable set (shoot/grenade=ShootAbility, melee=BashAbility) and that
/// Move/Overwatch/Reload/Heal — which keep dedicated paths or have no outcome surface yet — are NEVER relayed
/// (so the generic Harmony patch can't double-handle them), plus null/empty/unknown safety.
/// </summary>
public class TacticalAbilityRelayTests
{
    [Theory]
    [InlineData("ShootAbility")]   // shoot AND grenade throw
    [InlineData("BashAbility")]    // melee
    public void Relayable_GameplayAbilities_AreRelayed(string typeName)
    {
        Assert.True(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData("MoveAbility")]       // dedicated move command-sync + animation
    [InlineData("OverwatchAbility")]  // dedicated 0x8C/0x8D arm + cone
    [InlineData("ReloadAbility")]     // equipment/ammo-clip target — rides the actor-state delta
    [InlineData("HealAbility")]       // Health.Add directly (no tac.damage); no health surface yet
    public void Excluded_DedicatedPathAbilities_AreNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData("ApplyStatusAbility")]
    [InlineData("EndTurnAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    public void Unknown_Abilities_AreNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsRelayable(typeName));
    }

    [Fact]
    public void RelayableAndExcluded_DoNotOverlap()
    {
        foreach (var ex in TacticalAbilityRelay.ExcludedAbilityTypeNames)
            Assert.False(TacticalAbilityRelay.IsRelayable(ex),
                "Excluded ability " + ex + " must never be relayable (would risk a Harmony double-prefix).");
    }

    // ── IsClientSuppressedActivation: the predicate SuppressedAbilityViewClearPatch reuses to decide whether the
    //    native post-Activate ClearStackAndPush must be skipped (only for abilities the client actually suppresses).
    //    The view-effect of the prefix (skip the clear, keep UIStateCharacterSelected) is engine-only → in-game test.

    [Theory]
    [InlineData("MoveAbility")]       // MoveAbilityActivatePatch suppresses Activate (dedicated move-sync)
    [InlineData("OverwatchAbility")]  // OverwatchAbilityActivatePatch suppresses Activate (dedicated arm-sync)
    [InlineData("ShootAbility")]      // AbilityActivateRelayPatch suppresses Activate (generic relay)
    [InlineData("BashAbility")]       // AbilityActivateRelayPatch suppresses Activate (generic relay)
    [InlineData("ReloadAbility")]     // TS5b: now on the 0x8E generic relay → client-suppressed
    [InlineData("InteractWithObjectAbility")] // TS5b: now on the 0x8E generic relay → client-suppressed
    [InlineData("ExitMissionAbility")]            // gap-evac: 0x8E generic relay → client-suppressed
    [InlineData("EvacuateMountedActorsAbility")]  // gap-evac: 0x8E generic relay → client-suppressed
    [InlineData("EnterVehicleAbility")]           // wave-2: 0x8E generic relay → client-suppressed
    [InlineData("ExitVehicleAbility")]            // wave-2
    [InlineData("MindControlAbility")]            // wave-2
    [InlineData("JetJumpAbility")]                // wave-2
    [InlineData("RepositionAbility")]             // wave-2 (direct Dash relayed; reactions no-op'd by BUG3b patch)
    public void Suppressed_Activations_AreDetected(string typeName)
    {
        Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Theory]
    [InlineData("InventoryAbility")]  // NOT relayed — its Activate is the host view switch; client entry is view-guarded instead
    [InlineData("ApplyStatusAbility")]
    [InlineData("EndTurnAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    public void NonSuppressed_Activations_AreNotDetected(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Suppressed_NullOrEmpty_IsFalse(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsClientSuppressedActivation(typeName));
    }

    [Fact]
    public void Suppressed_Superset_Includes_AllRelayable()
    {
        // Every relayed ability is, by construction, a suppressed activation (the relay patch returns false).
        foreach (var r in TacticalAbilityRelay.RelayableAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation(r));
    }

    // ── NeedsFullStackRecovery: PURE decision for SuppressedAbilityViewClearPatch — when the client suppresses a
    //    ClearStackAndPush arm confirmed from a PUSHED aim sub-state (overwatch/bash on top of CharacterSelected),
    //    the guarded ResetCharacterSelectedState NO-OPs → the sub-state never exits → HUD/camera wedge, so the
    //    recovery must instead force-exit it by ClearStackAndPush-ing a fresh UIStateCharacterSelected. The
    //    imperative stack-driving itself is engine-only → in-game verified; only this decision is unit-tested.

    [Theory]
    [InlineData("UIStateOverwatchAbilitySelected")] // overwatch arm — the reported critical wedge
    [InlineData("UIStateAbilitySelected")]          // bash (co-affected by the same latent bug)
    public void AimSubState_NeedsFullStackRecovery(string currentViewStateName)
    {
        Assert.True(TacticalAbilityRelay.NeedsFullStackRecovery(currentViewStateName));
    }

    [Theory]
    [InlineData("UIStateCharacterSelected")] // Move confirms from here directly → guarded reset works → no full clear
    [InlineData("UIStateWaiting")]
    [InlineData("UIStateInitial")]
    [InlineData("SomeFutureUnknownState")]
    // SHOOT aim states are NOT per-confirm recovered: the suppressed shoot ReplaceTop stays on the native follow-up
    // loop so a multi-round volley keeps firing; the single-shot exit is driven by the authoritative terminal
    // condition (SuppressedAbilityViewClearPatch.TryExitClientShootAimIfTerminal), not this per-confirm gate.
    [InlineData("UIStateFreeCam")]           // shoot: first-person manual aim
    [InlineData("UIStateShoot")]             // shoot: third-person orbit aim
    public void NonAimSubState_DoesNotNeedFullStackRecovery(string currentViewStateName)
    {
        Assert.False(TacticalAbilityRelay.NeedsFullStackRecovery(currentViewStateName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NeedsFullStackRecovery_NullOrEmpty_IsFalse(string currentViewStateName)
    {
        Assert.False(TacticalAbilityRelay.NeedsFullStackRecovery(currentViewStateName));
    }

    [Fact]
    public void AimSubStates_AreNotConfusedWith_CharacterSelected()
    {
        // The whole bug: the aim sub-states are distinct from the control state the guarded reset checks for.
        foreach (var s in TacticalAbilityRelay.AimSubStateViewNames)
            Assert.NotEqual("UIStateCharacterSelected", s);
    }

    // ── TS2: GENERIC (non shoot/melee) ability-intent relay on 0x8E ─────────────────────────────────

    [Theory]
    [InlineData("HealAbility")]           // actor target → HP via 0x8F Health
    [InlineData("RecoverWillAbility")]    // self → WP via 0x8F Wp
    [InlineData("RallyAbility")]          // self/squad → WP via 0x8F Wp
    [InlineData("PsychicScreamAbility")]  // self AoE → damage via tac.damage 0x88
    [InlineData("ReloadAbility")]         // TS5b: equip-slot target → ammo via 0x8F (self) / actor (reload-others)
    [InlineData("InteractWithObjectAbility")] // TS5b: ground-object target → console status via 0x8F + objective via TS4
    [InlineData("ExitMissionAbility")]           // gap-evac: self (zone self-derived) → EvacuatedStatus via 0x8F + TS4 end
    [InlineData("EvacuateMountedActorsAbility")] // gap-evac: self (vehicle + host-derived passengers) → same surfaces
    [InlineData("DeployTurretAbility")]          // gap-turret-crate-loot: pos → turret actor via TS1 0x92
    [InlineData("ThrowTurretAbility")]           // gap-turret-crate-loot: pos (parabolic) → same TS1 spawn
    [InlineData("DeployShieldAbility")]          // gap-turret-crate-loot: pos/dir → ShieldDeployedStatus via 0x8F
    [InlineData("RetrieveDeployedItemAbility")]  // gap-turret-crate-loot: actor (the turret) → 0x93 despawn sweep
    [InlineData("RetrieveShieldAbility")]        // gap-turret-crate-loot: self (own status) → 0x8F status unapply
    [InlineData("OpenCrateAbility")]             // gap-turret-crate-loot: object (CrateComponent) → host-authoritative open
    [InlineData("DropItemAbility")]              // gap-turret-crate-loot: own equipped item (slot) → container via 0x92
    [InlineData("EnterVehicleAbility")]          // wave-2 D19: actor (the vehicle) → MountedStatus via 0x8F
    [InlineData("ExitVehicleAbility")]           // wave-2 D19: pos (dismount cell) → status unapply + Pos via 0x8F
    [InlineData("MindControlAbility")]           // wave-2 D10: actor → host faction AUTHORITY + TS5 0x0800 display flip
    [InlineData("InstilFrenzyAbility")]          // wave-2 D10: self (ignores param) → FrenzyStatus via 0x8F
    [InlineData("JetJumpAbility")]               // wave-2 D3: pos (landing) → end position via 0x8F Pos
    [InlineData("CaterpillarMoveAbility")]       // wave-2 D3: pos; own Activate → no double with the move patch
    [InlineData("RepositionAbility")]            // wave-2 D3: pos (Dash); direct-activation only
    [InlineData("RamAbility")]                   // wave-2 D3: pos (charge dir); damage via host tac.damage
    public void GenericRelayable_ActiveSet_IsRelayed(string typeName)
    {
        Assert.True(TacticalAbilityRelay.IsGenericRelayable(typeName));
    }

    [Theory]
    [InlineData("InventoryAbility")]      // never relayed: its Activate IS a local view switch (guard, not relay)
    [InlineData("MorphIntoActorAbility")] // off-list SpawnActorAbility sibling: shares the patched base Activate but runs native
    [InlineData("SpawnMistAbility")]      // off-list TacticalHurtReactionAbility sibling (shares Reposition's base Activate)
    [InlineData("StartPreparingAbility")] // off-list TacticalHurtReactionAbility sibling
    [InlineData("YuggothShieldsAbility")] // off-list TacticalHurtReactionAbility sibling
    [InlineData("ShootAbility")]          // 0x87 damage relay, not the 0x8E generic relay
    [InlineData("MoveAbility")]           // dedicated move-sync (CaterpillarMoveAbility is relayed, its BASE is not)
    [InlineData("SomeFutureUnknownAbility")]
    [InlineData(null)]
    [InlineData("")]
    public void GenericRelayable_OffList_IsNotRelayed(string typeName)
    {
        Assert.False(TacticalAbilityRelay.IsGenericRelayable(typeName));
    }

    [Fact]
    public void GenericRelay_IsDisjointFrom_ShootMeleeRelay_And_DedicatedPaths()
    {
        // No ability is handled by BOTH the 0x87 shoot/melee relay AND the 0x8E generic relay (a Harmony
        // double-prefix / double-handle would result). Also disjoint from the dedicated Move/Overwatch patches.
        foreach (var g in TacticalAbilityRelay.RelayableGenericAbilityTypeNames)
        {
            Assert.False(TacticalAbilityRelay.IsRelayable(g),
                "Generic-relay ability " + g + " must not also ride the 0x87 shoot/melee relay.");
            Assert.NotEqual("MoveAbility", g);
            Assert.NotEqual("OverwatchAbility", g);
        }
    }

    [Fact]
    public void GenericRelaySet_IsSuppressedActivation()
    {
        // Every generic-relayed ability is client-suppressed (so the view-freeze recovery covers it).
        foreach (var g in TacticalAbilityRelay.RelayableGenericAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation(g));
    }

    // ── GenericTargetKindFor: the ability→target-KIND map (client writes / host reads the discriminator) ──

    [Theory]
    [InlineData("HealAbility", TacticalGenericIntentCodec.KindActor)]         // needs a specific target actor
    [InlineData("MindControlAbility", TacticalGenericIntentCodec.KindActor)]
    [InlineData("EnterVehicleAbility", TacticalGenericIntentCodec.KindActor)] // wave-2: target = the vehicle actor
    [InlineData("InstilFrenzyAbility", TacticalGenericIntentCodec.KindNone)]  // wave-2 RE-MAP: Crt ignores param, self-derives (was wrongly "actor" while deferred)
    [InlineData("RecoverWillAbility", TacticalGenericIntentCodec.KindNone)]   // self-derives
    [InlineData("RallyAbility", TacticalGenericIntentCodec.KindNone)]         // self-derives its squad (spec "actor-or-self" → none)
    [InlineData("PsychicScreamAbility", TacticalGenericIntentCodec.KindNone)] // self AoE
    [InlineData("ExitMissionAbility", TacticalGenericIntentCodec.KindNone)]           // gap-evac: self-derives its exit zone
    [InlineData("EvacuateMountedActorsAbility", TacticalGenericIntentCodec.KindNone)] // gap-evac: zone + passengers host-derived
    [InlineData("DeployTurretAbility", TacticalGenericIntentCodec.KindPos)]
    [InlineData("ThrowTurretAbility", TacticalGenericIntentCodec.KindPos)]
    [InlineData("DeployShieldAbility", TacticalGenericIntentCodec.KindPos)]        // facing folds into pos (ChoosePosEncoding)
    [InlineData("RetrieveDeployedItemAbility", TacticalGenericIntentCodec.KindActor)] // target = the deployed turret actor
    [InlineData("RetrieveShieldAbility", TacticalGenericIntentCodec.KindNone)]     // ignores its param (Source = own status)
    [InlineData("ExitVehicleAbility", TacticalGenericIntentCodec.KindPos)]      // wave-2: dismount cell (PositionToApply)
    [InlineData("JetJumpAbility", TacticalGenericIntentCodec.KindPos)]          // wave-2: landing cell
    [InlineData("CaterpillarMoveAbility", TacticalGenericIntentCodec.KindPos)]  // wave-2: move destination
    [InlineData("RepositionAbility", TacticalGenericIntentCodec.KindPos)]       // wave-2: Dash destination
    [InlineData("RamAbility", TacticalGenericIntentCodec.KindPos)]              // wave-2: charge direction anchor
    [InlineData("ReloadAbility", TacticalGenericIntentCodec.KindSlot)]
    [InlineData("DropItemAbility", TacticalGenericIntentCodec.KindSlot)]           // own equipped item — was WRONGLY grouped "object"
    [InlineData("InteractWithObjectAbility", TacticalGenericIntentCodec.KindObject)]
    [InlineData("OpenCrateAbility", TacticalGenericIntentCodec.KindObject)]
    public void GenericTargetKindFor_MapsAbilityToKind(string typeName, byte expectedKind)
    {
        Assert.Equal(expectedKind, TacticalAbilityRelay.GenericTargetKindFor(typeName));
    }

    [Theory]
    [InlineData("ShootAbility")]   // not a generic ability — has no generic target-kind
    [InlineData("EndTurnAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    [InlineData(null)]
    [InlineData("")]
    public void GenericTargetKindFor_UnknownAbility_IsKindUnknown(string typeName)
    {
        Assert.Equal(TacticalGenericIntentCodec.KindUnknown, TacticalAbilityRelay.GenericTargetKindFor(typeName));
    }

    [Fact]
    public void GenericActiveSet_AllHaveAKnownTargetKind()
    {
        // Guard: an ability on the ACTIVE relay allowlist must resolve to a real (non-unknown) target kind,
        // else the client would send an unresolvable intent instead of relaying.
        foreach (var g in TacticalAbilityRelay.RelayableGenericAbilityTypeNames)
            Assert.NotEqual(TacticalGenericIntentCodec.KindUnknown, TacticalAbilityRelay.GenericTargetKindFor(g));
    }

    [Fact]
    public void GenericRelayAllowlist_AddOnly_ExactMembershipPin()
    {
        // ADD-ONLY pin (gap-evac): silently DROPPING a shipped entry would make a mirroring client run that
        // ability locally on the frozen sim (divergence), so the exact membership is asserted. New abilities
        // APPEND here deliberately (with their own kind-map + suppression rows above).
        Assert.Equal(
            new[]
            {
                "HealAbility", "RecoverWillAbility", "RallyAbility", "PsychicScreamAbility",
                "ReloadAbility", "InteractWithObjectAbility",
                "ExitMissionAbility", "EvacuateMountedActorsAbility",
                "DeployTurretAbility", "ThrowTurretAbility", "DeployShieldAbility",
                "RetrieveDeployedItemAbility", "RetrieveShieldAbility",
                "OpenCrateAbility", "DropItemAbility",
                // gap-ability-allowlist-wave2 (audit D19 + D10 + D3):
                "EnterVehicleAbility", "ExitVehicleAbility",
                "MindControlAbility", "InstilFrenzyAbility",
                "JetJumpAbility", "CaterpillarMoveAbility", "RepositionAbility", "RamAbility",
            },
            TacticalAbilityRelay.RelayableGenericAbilityTypeNames);
    }

    // ── gap-turret-crate-loot: host pre-Activate ENABLED gate (stale-client duplicate/NRE protection) ──

    [Theory]
    [InlineData("DeployTurretAbility")]          // item consumed on deploy → stale client UI can re-send
    [InlineData("ThrowTurretAbility")]
    [InlineData("DeployShieldAbility")]
    [InlineData("RetrieveDeployedItemAbility")]  // inventory-full / turret already gone
    [InlineData("RetrieveShieldAbility")]
    [InlineData("OpenCrateAbility")]
    [InlineData("DropItemAbility")]              // item already dropped → SelectedEquipment deref hazard
    [InlineData("EnterVehicleAbility")]          // wave-2: vehicle full / already mounted
    [InlineData("ExitVehicleAbility")]           // wave-2: NotMounted gate
    [InlineData("MindControlAbility")]           // wave-2: WP re-spend on stale re-send
    [InlineData("InstilFrenzyAbility")]          // wave-2: WP cost
    [InlineData("JetJumpAbility")]               // wave-2: AP prerequisites
    [InlineData("CaterpillarMoveAbility")]       // wave-2: AP / immobilized
    [InlineData("RepositionAbility")]            // wave-2: uses-per-turn / disabled statuses
    [InlineData("RamAbility")]                   // wave-2: AP / immobilized
    public void HostEnabledCheck_RequiredFor_NewLootDeploySet(string typeName)
    {
        Assert.True(TacticalAbilityRelay.RequiresHostEnabledCheck(typeName));
    }

    [Theory]
    [InlineData("HealAbility")]        // shipped relay set stays byte-identical (no new gate)
    [InlineData("RecoverWillAbility")]
    [InlineData("RallyAbility")]
    [InlineData("PsychicScreamAbility")]
    [InlineData("ReloadAbility")]
    [InlineData("InteractWithObjectAbility")]
    [InlineData("ExitMissionAbility")]
    [InlineData("EvacuateMountedActorsAbility")]
    [InlineData("ShootAbility")]
    [InlineData("SomeFutureUnknownAbility")]
    [InlineData(null)]
    [InlineData("")]
    public void HostEnabledCheck_NotRequired_ForShippedOrUnknown(string typeName)
    {
        Assert.False(TacticalAbilityRelay.RequiresHostEnabledCheck(typeName));
    }

    [Fact]
    public void HostEnabledCheckSet_IsSubsetOf_GenericRelaySet()
    {
        // The gate only ever applies to abilities the host actually relays — a name here that is NOT relayed
        // would be dead policy (or worse, a typo silently disabling the gate for the real ability).
        foreach (var n in TacticalAbilityRelay.HostEnabledCheckAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsGenericRelayable(n),
                "Host-enabled-check ability " + n + " must be on the generic relay allowlist.");
    }

    // ── gap-turret-crate-loot: KindPos intent encoding decision (deploy cell vs shield facing) ──

    [Theory]
    [InlineData(true,  false, TacticalAbilityRelay.PosEncodeUsePosition)]          // deploy cell → verbatim
    [InlineData(true,  true,  TacticalAbilityRelay.PosEncodeUsePosition)]          // pos wins — never displace the exact cell
    [InlineData(false, true,  TacticalAbilityRelay.PosEncodeCasterPlusDirection)]  // shield facing-only target
    [InlineData(false, false, TacticalAbilityRelay.PosEncodeCasterPos)]            // nothing usable → native forward fallback
    public void ChoosePosEncoding_DecisionTable(bool hasPos, bool hasDir, byte expected)
    {
        Assert.Equal(expected, TacticalAbilityRelay.ChoosePosEncoding(hasPos, hasDir));
    }

    // ── gap-ability-allowlist-wave2: direct-activation-only relay gate (Dash vs damage-triggered reactions) ──

    [Theory]
    [InlineData("RepositionAbility")]  // Dash (direct) relays; Umbra Vanish / PainChameleon (TriggerOnDamage) never
    public void DirectActivationOnly_RequiredFor_HurtReactionShapedRelays(string typeName)
    {
        Assert.True(TacticalAbilityRelay.RelaysOnlyDirectActivation(typeName));
    }

    [Theory]
    [InlineData("EnterVehicleAbility")]   // plain wave-2 relays carry no reaction shape → no extra gate
    [InlineData("ExitVehicleAbility")]
    [InlineData("MindControlAbility")]
    [InlineData("InstilFrenzyAbility")]
    [InlineData("JetJumpAbility")]
    [InlineData("CaterpillarMoveAbility")]
    [InlineData("RamAbility")]
    [InlineData("HealAbility")]           // shipped set stays ungated
    [InlineData("SpawnMistAbility")]      // off-list sibling is excluded by the allowlist gate, not this one
    [InlineData("SomeFutureUnknownAbility")]
    [InlineData(null)]
    [InlineData("")]
    public void DirectActivationOnly_NotRequired_ForOthers(string typeName)
    {
        Assert.False(TacticalAbilityRelay.RelaysOnlyDirectActivation(typeName));
    }

    [Fact]
    public void DirectActivationOnlySet_IsSubsetOf_GenericRelaySet()
    {
        // The gate refines abilities the relay actually carries — an entry that is NOT relayed would be dead
        // policy (or a typo silently disabling the double-fire protection for the real ability).
        foreach (var n in TacticalAbilityRelay.DirectActivationOnlyAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsGenericRelayable(n),
                "Direct-activation-only ability " + n + " must be on the generic relay allowlist.");
    }

    [Fact]
    public void CanonPin_FactionFlippers_StayHardExcluded_FromStatusMirror()
    {
        // gap-ability-allowlist-wave2 HARD CONSTRAINT (sync canon §5.4): relaying MindControlAbility moves the
        // ACTION to the host, but faction AUTHORITY never rides the status mirror — the faction-flipper
        // hard-exclude must survive this and every future allowlist widening (the client sees only the TS5
        // display flip, 0x8F ActorFieldFaction). Duplicates the TacticalActorStateDiffTests pin ON PURPOSE:
        // this file is where the allowlist grows, so the constraint fails loudly next to the change that broke it.
        foreach (var vis in new[] { 0, 1, 5 })
        {
            Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(vis, "MindControlStatus"));
            Assert.False(TacticalActorStateDiff.ShouldMirrorStatus(vis, "ZombifiedStatus"));
        }
    }

    [Fact]
    public void OriginNativeShoot_FireStartGate_OverridesViewClearIntercept()
    {
        // rca-grenade-ui LAYERING PIN: SuppressedAbilityViewClearPatch early-outs (native view path) for any
        // ability in the fire-start set BEFORE the suppressed-set intercept — a ShootAbility (shoot + grenade)
        // runs NATIVELY on the origin client (815634b), so intercepting its ClearStackAndPush confirm (the
        // ground-targeted grenade path, UIStateShoot default stateStackAction) skipped the native
        // SwitchToState(UIStateWaiting) and left the trajectory ribbon stuck. The pin: ShootAbility must be in
        // BOTH sets (suppressed → the patch sees it; fire-start → the patch lets it through), while BashAbility
        // (melee, still fully suppressed + relayed) must stay intercepted (in suppressed, NOT in fire-start).
        Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation("ShootAbility"));
        Assert.True(TacticalAbilityRelay.ShouldBroadcastFireStart("ShootAbility"));   // → native view path wins
        Assert.True(TacticalAbilityRelay.IsClientSuppressedActivation("BashAbility"));
        Assert.False(TacticalAbilityRelay.ShouldBroadcastFireStart("BashAbility"));   // → intercept still applies
    }

    // ─── rca-jetjump: origin-native special-move set ───────────────────────────────────────────
    [Fact]
    public void OriginNativeMove_IsSubsetOf_GenericRelay_AndPinned()
    {
        // Every origin-native move MUST stay on the generic relay (the intent is still sent; the host still
        // executes authoritatively) — an off-relay entry would run native-only with no host counterpart.
        foreach (var n in TacticalAbilityRelay.OriginNativeMoveAbilityTypeNames)
            Assert.True(TacticalAbilityRelay.IsGenericRelayable(n),
                "Origin-native move " + n + " must be on the generic relay allowlist.");

        // Membership pin: JetJump is origin-native; the deliberately excluded moves (Ram = client-side collision
        // damage would double-apply; Caterpillar = MoveAbility-subclass wall destruction + move-rail overlap;
        // Reposition = shares the sealed hurt-reaction base Activate with a second suppressing prefix) are NOT.
        Assert.True(TacticalAbilityRelay.IsOriginNativeMove("JetJumpAbility"));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove("RamAbility"));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove("CaterpillarMoveAbility"));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove("RepositionAbility"));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove("ShootAbility"));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove(null));
        Assert.False(TacticalAbilityRelay.IsOriginNativeMove(""));
    }
}
