# Multipleer — tactical co-op replication — NEXT-SESSION HANDOFF (2026-06-20)

Paste-ready entry prompt + context for continuing the tactical co-op work.

## ENTRY PROMPT (paste this)

> Continue the Multipleer (PhoenixPoint co-op) **tactical battle replication** work.
> Read memory `multipleer-mod-status` (block "CURRENT STATE — 2026-06-20 SESSION-END" first) +
> the mempalace node "Multipleer Tactical Battle Replication" + the spine spec
> `Multipleer\docs\superpowers\specs\2026-06-19-multipleer-tactical-generic-state-spine-design.md`.
> Then work the priority backlog below. Same workflow as last session: ground via Serena, build
> via TDD, opus review before commit, commit by EXPLICIT path to inner `main` (NOT pushed),
> `deploy.ps1`, then I 2-instance in-game test (DirectIP 127.0.0.1, client = D:\PP-Instance2 via
> tools\launch-second-copy.bat, RESTART BOTH after each deploy).

## WHAT WORKS IN-GAME (user-confirmed)
deploy · move · turn-advance · vision (enemy icons/targeting) · weapon-swap · overwatch ARM ·
AP/WP sync · client shoot + grenade + melee DAMAGE (HP drops both sides).

## ARCHITECTURE (decided, user-approved)
Pivoted from per-action cherry-picking → **generic full-state spine** (same as the geoscape
full-state decision). Two generic surfaces: `tac.actorstate` 0x8F (host→all per-actor STATE-DELTA,
extensible fieldMask) + `tac.intent.ability` 0x87 (client→host GENERIC ability-intent, now relays
any allowlisted ability). Command-sync START events kept ONLY for animation (move.start; fire-start
= TODO). `tac.damage` 0x88 stays an event for damage/death/FX. Increment plan: T1 state-delta
(AP/WP, DONE) → T2 generic intent (DONE) → T3 retire subsumed surfaces → T4 fold vision → enable
statuses. Surface map 0x81–0x8F (0x8E reserved for the full intent union).

## PRIORITY BACKLOG (open in-game gaps, in order)

**(A) ENEMY-TURN PRESENTATION on the client.** On HOST, end-turn → player UI hides + an auto-
cinematic camera flies over the Pandoran activity. On CLIENT: UI/camera stay in player mode
(mobs DO move, but no enemy-turn presentation). The client turn-sync (`TacticalTurnSync` /
`ClientOnTurn`) sets the faction index + runs `PlayTurnCrt` but never triggers the enemy-turn
CAMERA/UI state. INVESTIGATE: the native enemy-turn view state (`TacticalView` enemy/non-player
turn mode, UI-hide) + the auto/cinematic camera (CameraDirector / turn-change camera) and why the
client path doesn't enter it. Likely fix: on `tac.turn` for a NON-player faction, drive the client
`TacticalView` into the enemy-turn presentation (hide player HUD + start the follow-camera).

**(B) STATUS EFFECTS + BODY-PART DAMAGE visuals on client.** Mobs show bleeding / broken-disabled
limbs / buffs on host, absent on client. Two parts: (1) ENABLE the T1 status sync — flip
`SyncStatuses` on with the vetted **default-DENY allowlist** (timer/pure-stat-mod/cosmetic only;
keep MindControl/Stun/Panic/Overwatch/all IDamageOverTimeStatus EXCLUDED — their OnApply has host-
only side-effects), apply-order already fixed (statuses before AP/WP), then 2-instance verify
(MindControl + Stun are the must-test cases). (2) BODY-PART / limb disable + bleeding ICON state —
sync the disabled-limb state + the bleeding status VISUAL (bleeding DAMAGE already rides tac.damage;
the missing piece is the visible limb-disable + status icon).

**(C) FIRE/THROW ANIMATION on client (Inc3b).** Client shoot/grenade show HP drop but no shooter
pose / projectile / grenade arc (animates only on host). Command-sync a `tac.fire.start` (mirror
`tac.move.start`) so the client plays the fire/throw animation concurrently; `tac.damage` stays the
reconcile/outcome. Ground the client-safe way to play the shot animation without re-rolling
(FireWeaponAtTargetCrt is suppressed on client — find an animation-only path).

**(D) HEALTH field in the T1 actor-state delta.** Add the reserved HEALTH (and ARMOR) fieldMask bit
to `tac.actorstate` 0x8F so HP mirrors for NON-damage changes (and as a backstop) — this also
RE-ENABLES `HealAbility` in the T2 relay allowlist (heal currently excluded: it uses Health.Add
directly, not tac.damage). Keep absolute values (idempotent, no double-apply vs tac.damage).

**(E) T3 — retire subsumed surfaces into the delta**, one-per-commit, in-game-gated: extend
`tac.actorstate` to carry pos/facing/equip/overwatch-cone, then retire tac.move(END)/tac.equip/
tac.overwatch.state/(vision) surfaces as the delta proves out. Keep move.start + fire.start
(animation) + tac.damage (event).

**(F) Misc:** reload (needs equipment-ref target or rides delta ammo), inventory actions, psychic/
other abilities (extend the relay allowlist as targets become serializable), strip all
`[Multipleer][tac]` DIAG before any publish.

## VERIFY EARLY
- **Overwatch reaction** (commit 809fb09, re-anchor) was NOT re-tested — check the DIAG line
  `[Multipleer][tac] overwatch arm: intent.Tip=… hostVisionPoint=… delta=…`. Large delta → the
  cone re-anchor was the fix (should now reaction-fire). delta ≈ 0 → positions already synced;
  the real gate is weapon/LOS at `TacticalLevelController.cs:1394` — investigate there next.

## KEY FILES (all under `Multipleer\src\Sync\Tactical\` + `Multipleer\src\Harmony\Tactical\`)
TacticalActorStateSync.cs / TacticalActorStateDiff.cs (T1 delta + statuses gated off) ·
TacticalAbilityRelay.cs + CombatSyncPatches.cs + TacticalCombatSync.cs (T2 generic intent +
tac.damage) · TacticalOverwatchSync.cs + OverwatchSyncPatches.cs · TacticalMoveSync.cs +
TacticalTurnSync.cs (turn — start here for gap A) · TacticalVisionSync.cs · TacticalEquipSync.cs ·
TacticalDeploySync.cs (registry/dispatch/lifecycle) · TacticalSyncSurfaces.cs · TacticalLiveCodec.cs.

## COMMITS THIS SESSION (inner main, NOT pushed)
e89e4c4 shoot/damage · e406f8c vision · dbb2668 weapon-swap · 86c9cd8 overwatch ·
51b7f31 spine spec · 6bb6869 T1 AP/WP delta · 29fae97 T2 generic ability-intent ·
809fb09 overwatch re-anchor fix. (Plus the prior move/turn 4e32804.)
