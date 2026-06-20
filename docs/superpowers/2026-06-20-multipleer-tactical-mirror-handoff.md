# Multipleer Tactical Full-Mirror Arc — Session Handoff 2026-06-20 NIGHT

## Next session: start here

1. **In-game 2-instance test** (DirectIP 127.0.0.1, restart both after deploy) per the watch-lists below — all 4 increments need verification.
2. **Read the overwatch delta** in client log: `[Multipleer][tac] overwatch arm: ... delta=` — large value = re-anchor fix `809fb09` confirmed working; ~0 = dig LOS/weapon gate `TacticalLevelController.cs:1394`.
3. **Resume backlog** at E (retire subsumed surfaces into 0x8F) / long-tail sync items.

---

## Shipped tonight (4 increments, all committed inner main NOT pushed, deployed, 702 tests green)

- **Directive:** sync EVERYTHING tactical host<->client; client = PURE MIRROR (displays host state, NEVER re-runs authoritative logic); host authoritative. ONE camera exception preserved: remote player's shot must NOT fly THIS client's camera while controlling another char (already correct).
- **Workflow:** Serena-ground -> TDD pure seams -> opus-review BEFORE commit -> commit explicit-path inner main (NO push) -> deploy.ps1 -> user tests 2-instance.

### A `83b95e6` — client enemy-turn presentation

- On `tac.turn` to non-player faction, client sets enemy faction `IsPlayingTurn=true` + re-enters `UIStateInitial` (`EnsureClientViewDown`) -> native `UIStateInitial.InitialStateUpdateCrt` re-dispatches to `UIStateOtherFactionTurn` (action bar hidden, "<faction> turn" banner, camera follows mobs free via their local ability CameraDirector hints).
- Viewer faction unchanged (fog ok); no `PlayTurnCrt`/AI on client; outgoing enemy `IsPlayingTurn` cleared on handoff.
- Pure seam: `ClientEnemyTurnPresentationGate`.

### B `4053418` — visual-only status + limb-disable mirror

- Mirror every status w/ `Def.VisibleOnHealthbar!=Hidden` as INERT icon — pre-set `Status.Applied=true` before `StatusComponent.ApplyStatus` (subclasses take already-applied/deserialize branch -> skip OnApply side-effects).
- Client-only Harmony guards (`ClientStatusMirrorGuards.cs`) skip `StartTurn`/`EndTurn`/`ApplyEffect`/`OnUnapply` for mirrored instances. No double-dmg / AP-drain / stat-double-apply / faction-flip; damage stays `tac.damage`.
- Faction-changers `MindControlStatus`+`ZombifiedStatus` EXCLUDED; abort if `SetApplied` fails.
- Limb-disable: new `ActorFieldBodyPartHp` (0x0200) carries per-bodypart `{slotName,hp}` -> client `ItemSlot.GetHealth().Set` -> native `SlotState.Disabled` UI free (NOT a status).

### D `324ef69` — actor HP mirror

- `ActorFieldHealth` (0x0020) in 0x8F delta carries absolute HP (heal + drift).
- Death-safe: write only HP>1e-5 via `BaseStat.Set`; host omits bit when HP<=0; native `Die()` fires only on a `Value` crossing below 1e-5; `tac.damage` owns death. Double-guarded.

### C `08c25b0` — client attack animation (surface `TacFireStart=0x90`)

- Host broadcasts `tac.fire.start` at shoot start (`ShootAbility` = shoot+grenade) from `TacticalCombatSync.ClientInterceptAbility` host choke.
- Client replays native `FireWeaponAtTargetCrt` (TLC.cs:1538) to drive the REAL animator, projectile-free + camera-silent.
- `AttackType.Synced` gates native Shoot/ProjectileFired hints + return-fire.
- Client-only guards (`FireAnimSyncPatches.cs`; ref-counted `ReplayActive` flag + `!IsHost`; try/finally + mission-exit reset):
  - `CameraDirector.Hint` no-op (kills non-Synced ShootingStarted fly)
  - `Weapon.FireProjectile`->null (no dmg/ammo)
  - `WaitForProjectilesToHit`->empty (no NPE/casualty re-raise)
  - `TacticalItem.Destroy`->no-op (grenade not yanked from client inv — host reconcile authoritative)
- `FireWeaponPatch` lifts client suppression only while `ReplayActive` (anim only).
- Payload: `{seq, shooterNetId, abilityDefGuid(=Ability.BaseDef), targetNetId/-1+xyz, shotCount}`.
- Option 1 (direct `ActivateShootingClips`) was unreachable (only swaps clips).
- Melee DEFERRED (`BashAbility` -> own `BashCrt` path); muzzle-flash VFX skipped (inside `FireProjectile`; body anim only).

---

## In-game watch-lists (user 2-instance)

- **A:** client enemy turn -> bar hides, banner, camera follows mobs; handoff back -> HUD returns; fog ok; log `[Multipleer][tac] CLIENT entered ENEMY-TURN presentation`.
- **B:** bleed/fire icon shows but HP drops ONCE; stun/buff icon shows but client AP/stats unchanged; disabled limb shows; host removes buff -> icon clears (MindControl/Zombify icons intentionally NOT synced).
- **C:** shoot + grenade-throw anim play on client; camera does NOT fly when remote player attacks while you control another unit; damage once; melee = no anim yet; no muzzle flash.
- **D:** host heal -> client HP-bar rises; lethal kills once; no HP drift.

---

## Open / next

- **(overwatch)** client log `[Multipleer][tac] overwatch arm: ... delta=` — big = re-anchor fix `809fb09` worked, ~0 = dig LOS-gate `TacticalLevelController.cs:1394`.
- **(E/T3)** retire subsumed surfaces into 0x8F one-by-one, in-game-gated: 0x82/0x83 move, 0x8A/0x8B equip, 0x8C/0x8D overwatch, 0x89 vision.
- **(LONG TAIL "sync everything")** actor faction-change replication (mind-control flips faction host-side — likely client gap), spawned entities (turrets/summons), destructible environment, mission objectives, ammo/reload state, melee anim, muzzle VFX.
- **(C cosmetic MINOR)** global `CameraSilent` during a fire replay can briefly drop a concurrent mirrored move-follow's tracking (accepted).

---

## Commit hygiene

- All 4 commits explicit-path to inner main, NOT pushed.
- Tree has MUCH unrelated uncommitted concurrent work (Network/Transport/UI/SessionLifecycle + ~10 new test files) from other arcs — do NOT sweep into tactical commits.
- deploy.ps1 -> `D:\Steam\steamapps\common\Phoenix Point\Mods\Multipleer`.

---

## Key files

All under `Multipleer\src\Sync\Tactical\` unless noted:

- `TacticalDeploySync.cs` — registry + envelope dispatch `HandleTacticalEnvelope`
- `TacticalSyncSurfaces.cs` — opcodes 0x80-0x90
- `TacticalLiveCodec.cs` — codecs + `ActorStateRecord` fields AP0x01/WP0x02/Statuses0x04/Pos0x08/Facing0x10/Health0x20/Armor0x40/Equip0x80/Overwatch0x100/BodyPartHp0x200
- `TacticalTurnSync.cs` — turn handoff + A
- `ClientEnemyTurnPresentationGate.cs` — A seam
- `TacticalActorStateSync.cs` — 0x8F read/reconcile B+D
- `TacticalActorStateDiff.cs` — pure seams
- `src\Harmony\Tactical\ClientStatusMirrorGuards.cs` — B inert-status guards
- `TacticalFireAnimSync.cs` — C host/client
- `src\Harmony\Tactical\FireAnimSyncPatches.cs` — C guards
- `src\Harmony\TacticalPatches.cs` — `FireWeaponPatch` lift
- `TacticalCombatSync.cs` — damage/AP/WP + C host choke
- `TacticalMoveSync.cs`, `VisionSync.cs`, `OverwatchSync.cs`, `EquipSync.cs`

---

## Increment (F) — client melee attack-animation replay (tac.melee.start 0x91)

- **Goal:** client replays the `BashAbility` swing as a PURE DISPLAY MIRROR; host-authoritative
  effects (damage / return-fire / vision known-counter / ammo) suppressed on the client. Mirrors
  the proven `tac.fire.start` 0x90 pipeline.
- **Wire:** surface `TacMeleeStart=0x91` (`TacticalSyncSurfaces.cs`); `EncodeMeleeStart` /
  `TryDecodeMeleeStart` (`TacticalLiveCodec.cs`) layout
  `[seq:u32][attackerNetId:i32][abilityDefGuid:string][targetNetId:i32][tx/ty/tz:f32]` — mirrors
  `FireStart` MINUS `shotCount` (a melee is one swing). `TargetNetIdNone` sentinel reused.
- **Host emit:** `ShouldBroadcastMeleeStart` (`BashAbility` only, DISJOINT from
  `ShouldBroadcastFireStart={"ShootAbility"}`) + `HostBroadcastMeleeStart` at the
  `TacticalCombatSync` ability-relay choke; reuses the existing `AbilityActivateRelayPatch`
  (`BashAbility` already in the relayable set) — NO new host Harmony patch.
- **Client replay:** `TacticalMeleeAnimSync.ClientOnMeleeStart` resolves attacker + `BashAbility`
  by id, builds a `TacticalAbilityTarget`, drives the PRIVATE `BashAbility.BashCrt` via reflection
  (bypasses `Activate` → no AP/WP double-spend, no `AbilityActivated` camera hint); manual
  `IEnumerator` pump in `ReplayMeleeCrt`; ref-counted `_replayDepth` (leak-safe try/finally);
  client-gated (`!IsHost`). Dispatch + `Reset` wired in `TacticalDeploySync.cs`.
- **Neuter guards** (`MeleeAnimSyncPatches.cs`, gated `FireReplayGate.MeleeReplay = replay &&
  !IsHost`): `MeleeDamageNeuterPatch` → skip `BashAbility.ApplyPayloadEffects`
  (`BashAbility.cs:582`, single overload, covers normal + ProjectileVisuals);
  `MeleeReturnFireNeuterPatch` → empty-crt `TacticalLevelController.ReturnFire`
  (`IEnumerator<NextUpdate>`, `:1490`); `MeleeKnownCounterNeuterPatch` → skip
  `TacticalFactionVision.IncrementKnownCounterToAll` (`:508`, single overload) so no vision
  known-counter double-count vs host 0x89. Ammo: existing `FireAmmoChargeNeuterPatch` gate widened
  `ClientReplay`→`AnyReplay` (`FireReplay||MeleeReplay`) covers `BashCrt`
  `CommonItemData.ModifyCharges` — NO duplicate patch.
- **NRE guard:** bare-position target (`TargetNetId<0`) AND `MultipleTargetSimulation==false` →
  SKIP replay + log (averts an unguarded `target.DamageReceiver` deref at `BashAbility.cs:472`).
- **Tests:** `TacticalMeleeStartCodecTests.cs` (round-trip actor/position-sentinel, empty-guid,
  truncated, chopped-mid-field, `ShouldBroadcastMeleeStart` matrix, subset-of-relayable). Also
  closed a prior gap: +5 `FireStart` round-trip/truncation tests in `TacticalLiveCodecTests.cs`.
- **Decompile-grounded:** `BashCrt` reads ONLY `action.Param`; `PlayingAction` public ctor;
  reviewed against the decompile, 2 findings fixed (vision neuter + NRE guard).
- **Status:** build 0 warn / 0 err, 732 tests green. ⚠ UNVERIFIED in-game (2-instance: host melee
  → client swing, no double damage / vision-flicker, ammo intact).
