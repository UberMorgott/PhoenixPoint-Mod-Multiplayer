# Multipleer — Tactical Generic State-Spine: Design Spec (host↔client full-state replication)

> Architect design doc (read-only investigation; NO production code written). Date 2026-06-19.
> Decompile root: `E:\DEV\PhoenixPoint\decompiled\AssemblyCSharp\Assembly-CSharp\src` (proxy — re-pin vs installed `Assembly-CSharp.dll` before any patch). Real TFTV src preferred where it exists: `E:\DEV\PhoenixPoint\refs\TFTV-src`.
> Mod tactical sync: `Multipleer\src\Sync\Tactical\`. Existing per-action surfaces 0x80–0x8D (`TacticalSyncSurfaces.cs`).
> Consistency reference (geoscape side): `docs\superpowers\specs\2026-06-17-multipleer-full-geoscape-replication-feasibility.md` §5 "RETIRE per-domain channels surface-by-surface".
> Decision owner: the user. Directive: *pivot tactical host↔client from per-action cherry-picking to a GENERIC FULL-STATE SPINE — "всё разом" — same philosophy as geoscape.*

## VERDICT (one line)

**FEASIBLE and the right call.** PP already exposes the entire per-actor mutable tactical surface as ONE serializable DTO (`TacActorBaseInstanceData{ Stats, Statuses, ModelPose, … }`) and ONE generic ability funnel (`TacticalAbility.Activate(object)` + `GetAbilities<T>()` by `Def.Guid`). The existing per-action modules ALREADY hold every read/set helper the spine needs (AP/WP via `CharacterStats`, `ApplyDamage`, `SetSelectedEquipment`, `SetCone`, `KnownActors`, `ApplyStatus`/`UnapplyStatus`). The spine = (1) ONE generic ability-intent (client→host) replacing the 4 per-ability intents, (2) ONE generic per-actor STATE-DELTA (host→all) subsuming move-outcome/equip/overwatch-state/vision/turn, (3) START events kept for animation only. Migrate ADDITIVELY (spine alongside existing surfaces), verify in-game, retire subsumed surfaces ONE at a time (in-game-gated) — exactly the geoscape playbook.

---

## 1. The existing per-action code — what the spine REUSES (do NOT rewrite)

All under `Multipleer\src\Sync\Tactical\`. The spine calls these EXISTING helpers; it does not re-derive engine APIs.

| File | Read helper (already grounded) | Set/apply helper (already grounded) | Role in spine |
|---|---|---|---|
| `TacticalDeploySync.cs` | `Registry` / `ResolveLiveActor(netId)` :85, `NetIdForLiveActor(actor)` :95, `LiveTlc`, `LiveSeq`, `IntentDedup`, `EnumerateActorRefs`, `HandleTacticalEnvelope` :660 (surface router) | `ArmInboundHook`/`DisarmInboundHook` :648, `BroadcastToAll`/`SendToHost` (via `TacticalMoveSync`) | **Spine backbone**: NetId registry + envelope rail + the inbound dispatch switch the two new surfaces hook into. |
| `TacticalMoveSync.cs` | `GetPos(actor)` :616, `ReadStopReason` :605, `TryGetPositionToApply` :431 | `TrySetPosition(actor,Vector3)`→`ActorComponent.SetPosition` :446, `TryAnimatedNavigate`→`TacticalNavigationComponent.Navigate(Vector3,NavigationSettings)` :510, `GetMirrorNavSettings` (CostsAPToActor=false, TriggerOverwatch=false) :482 | Position read + teleport-set for the delta; START-animation (`tac.move.start`) KEPT. |
| `TacticalCombatSync.cs` | `ReadApWp(actor,out ap,out wp)`→`CharacterStats.ActionPoints/.WillPoints` :504, `FlattenDamage` :235 | `SetApWp(actor,ap,wp)`→`StatusStat.Set(float,bool)` :518, `ApplyDamage(DamageResult)` (rebuild) :188 | AP/WP read+set for the delta; `tac.damage` EVENT KEPT (death/FX); delta HP is idempotent backstop. |
| `TacticalEquipSync.cs` | `EquipIndexOf(ec,equip)` :227, `SelectedEquipment` read :153, `ReadEquipmentList`→`EquipmentComponent.Equipments` :221 | `InvokeSetSelectedEquipment(ec,equip)` :256, `TryResolveEquipmentByIndex` :241 | Selected-equip index read+set → folds into the delta. SUBSUMES `tac.equip` 0x8B. |
| `TacticalOverwatchSync.cs` | `FindOverwatchStatus` :375, `FlattenConeStruct`→`Cone{Tip,Height,Radius,Forward}` :291, `ReadConeFromTarget` :265 | `ApplyCosmeticArm`/`RemoveCosmeticArm` :209/:237 (`ApplyStatus`/`UnapplyStatus`+`SetCone`) | Overwatch-armed cone folds into the delta (cosmetic on client). SUBSUMES `tac.overwatch.state` 0x8D. |
| `TacticalVisionSync.cs` + `TacticalVisionDiff.cs` | `GetKnownActorsDict(vision)`→`TacticalFactionVision.KnownActors` :203, `VisionStateForActor` :213 | `SetActorState`/`ForgetActor` :229/:264, `FireFactionKnowledgeChanged` :352, `TacticalVisionDiff.Compute` (reconcile) | Per-faction (not per-actor) — folds into a faction-scoped delta OR stays a sibling spine channel. SUBSUMES `tac.vision` 0x89. |
| `TacticalTurnSync.cs` | `CurrentFaction`, `_currentFactionIndex` (Traverse), `TurnNumber` | `ClientOnTurn`→set `_currentFactionIndex`+`StartPlayTurn`/view-drive | Turn handoff is faction-level + view-state — KEEP as its own surface (`tac.turn`); NOT per-actor state. |
| `TacticalLiveCodec.cs` | pure wire structs + `TacticalLiveSeq` (host monotonic / client last-writer-wins) + `TacticalIntentDedup` (nonce ring) | — | Spine adds two new codec frames here; REUSES `TacticalLiveSeq`/`TacticalIntentDedup` unchanged. |
| `TacticalActorRegistry.cs` | `TryGet`/`NetIdOf`/`Entries`, `MatchAndRegister` | `Register`/`Remove` | NetId ↔ actor; the spine keys every delta/intent on NetId. UNCHANGED. |

**Key reuse insight:** every per-actor field the spine must mirror ALREADY has a proven flatten+apply pair in these files. The spine is a *re-packaging* of those pairs into one surface, not new engine reflection.

---

## 2. INVENTORY — per-actor mutable tactical state (grounded read API → set API → already-handled?)

Engine file:line from the decompile proxy (`decompiled\AssemblyCSharp\…\src`). "Handled" = an existing per-action module already reads/sets it.

| # | State | READ api (engine) | SET/apply api (engine) | Already handled by |
|---|---|---|---|---|
| 1 | **Position** | `ActorComponent.Pos` (`Base.Entities/ActorComponent.cs:41` = `transform.position`) | `ActorComponent.SetPosition(Vector3)` :277 | ✅ MoveSync (`TrySetPosition`); animation via `tac.move.start`. |
| 2 | **Facing / rotation** | `ActorComponent.Rot` :43 (`transform.rotation`) | `SetForward(Vector3)` :284 / `SetRotation(Quaternion)` :307 / `SetTransform(pos,rot)` :314 | ❌ NOT synced today → NEW in delta (8B quaternion or fwd-vector). |
| 3 | **Health / HP** | `TacticalActorBase.Health` (`StatusStat`) :52; `(float)Health`; `GetHealth()` :756; `IsDead => (float)Health<1e-5` :118 | `Health.Set(float)` (`StatusStat.Set(float,bool)` :86); `Subtract`/`SetToMax` :909/:1001 | ✅ via `tac.damage` EVENT (`ApplyDamage` runs `Health.Subtract`). Delta carries ABSOLUTE Health as idempotent backstop only. |
| 4 | **Armor** | `CharacterStats.Armour` :66 (`StatusStat`); per-bodypart `BodypartsHealth` | `StatusStat.Set` | ⚠ partial — `tac.damage` carries `ArmorDamage`. Delta carries absolute armor as backstop. |
| 5 | **Action Points** | `CharacterStats.ActionPoints` (`StatusStat`) (`PhoenixPoint.Common.Entities/CharacterStats.cs:36`) | `StatusStat.Set(float,bool)` :86 | ✅ `ReadApWp`/`SetApWp` (CombatSync) — but ONLY on the shooter, ONLY at fire time. **This is the AP-sync the user wants generalized → delta carries AP for EVERY actor.** |
| 6 | **Will Points** | `CharacterStats.WillPoints` :38 | `StatusStat.Set` | ✅ same as AP, same gap → delta carries WP for every actor. |
| 7 | **Statuses (active list)** | `StatusComponent.Statuses` (`List<Status>`) (`Base.Entities.Statuses/StatusComponent.cs:16`); `GetStatuses<T>()` :111; `Status.Def` (→`StatusDef.Guid`) `Status.cs:12`; `Status.Source` :23; `Status.Duration` :16 | `ApplyStatus(StatusDef,source,target)` :178; `UnapplyStatus(Status)` :192; `UnapplyAllStatusesFiltered` :208 | ⚠ ONLY OverwatchStatus (cosmetic) + damage-borne statuses. **Generic status set = the biggest NEW win** (Poison/Paralysis/Stun/Frenzy/Shield/etc.). See §6 reapply-fidelity. |
| 8 | **Selected equipment** | `EquipmentComponent.SelectedEquipment`; `Equipments` (`List<Equipment>`) ; index via `EquipIndexOf` | `SetSelectedEquipment(Equipment)` (EquipSync `InvokeSetSelectedEquipment` :256) | ✅ EquipSync (`tac.equip`) → folds into delta as `selectedEquipIndex:i32`. |
| 9 | **Vision / KnownActors** | `TacticalFactionVision.KnownActors` (`Dictionary<TacticalActorBase,KnownCounters>`) :115; `IsRevealed`/`IsLocated` :235/:243 | `KnownCounters.IncrementCounterTo`/`ResetCounter`; `KnownActors.Remove` | ✅ VisionSync (`tac.vision`, per-FACTION reconcile). Stays faction-scoped (sibling channel), not per-actor. |
| 10 | **Overwatch armed cone** | `OverwatchStatus` (in `Statuses`); `_cone`/`Cone` via `FlattenConeStruct` | `OverwatchStatus.SetCone(Cone?)`; arm via `ApplyStatus`+`SetCone` | ✅ OverwatchSync (`tac.overwatch.state`). Cone is a per-actor field → folds into delta (cosmetic apply on client). |
| 11 | **Alive / dead** | `TacticalActorBase.IsDead` :118 / `IsAlive` :120 (derived from Health) | death cascade via `ApplyDamage` | ✅ implied by Health (`tac.damage` event drives the death cascade). Delta does NOT re-kill (would double-run death FX) — Health backstop is value-only. |
| 12 | **Disabled / incapacitated** | `TacticalActorBase.IsDisabled` :122 (status-derived: Unconscious/Paralysed etc.) | via the underlying status (#7) | ⚠ derived from #7 statuses → syncs FREE once the generic status set syncs. |
| 13 | **Stance: evacuated / standby / shield / lost-hand** | `TacticalActor.IsEvacuated` :121, `IsOnStandBy` :135, `IsShieldDeployed` :137, `HasLostHandStatus` :1648 — all `HasStatus<…>()` | via the underlying status (#7) | ⚠ ALL status-derived → sync FREE with the generic status set. No dedicated field needed. |
| 14 | **Per-bodypart health / destroyed sub-addons** | `BodypartsHealth` (Stats `OfType<StatusStat>` `_Health`); `TacActorBaseInstanceData.Stats` :34 | `StatusStat.Set` per stat | ❌ NOT synced today → OPTIONAL delta extension (carry the full `Stats` list); low priority (visual mostly). |
| 15 | **AbilityUsesThisTurn / per-turn counters** | `TacActorInstanceData.AbilityUsesThisTurn` :31 | engine-internal | ❌ NOT synced; host-authoritative anyway (host runs the ability) → client rarely needs it. Defer. |

**The unifying DTO (grounds "всё разом"):** `TacActorBaseInstanceData` (`PhoenixPoint.Tactical.Entities/TacActorBaseInstanceData.cs`) already aggregates the live per-actor mutable surface as ONE serializable record: `Stats` (`List<BaseStat>` :34 — ALL StatusStats incl. AP/WP/Health/Armor), `Statuses` (`List<Status>` :36), `ModelPose` (`PoseTransform` :40 = position+rotation), `InventoryItems` :38, `Health`/`WillPoints` accessors :46/:60. The deploy snapshot ALREADY captures+restores this whole record per actor (`RecordInstanceData`/`ProcessInstanceData`). **The spine's job is to mirror the LIVE per-turn changes to this same surface — the snapshot seeds turn-0, the delta carries every subsequent mutation.**

---

## 3. DESIGN — Surface A: generic ability-intent (client→host)

Replaces the 4 per-ability intents (`tac.intent.move` 0x82, `tac.intent.endturn` 0x84 [keep — not an actor ability], `tac.intent.ability` 0x87, `tac.intent.equip` 0x8A, `tac.intent.overwatch` 0x8C) with ONE.

### 3.1 Wire shape — `tac.intent` (NEW surface 0x8E, client→host)
```
[actorNetId:i32]
[abilityDefGuid:string]            // "" → non-ability input (see kind)
[inputKind:i32]                    // 0=Ability, 1=EquipSelect (non-ability setter)
[targetActorNetId:i32]             // -1 sentinel = none
[targetKind:i32]                   // bitmask: 1=Position, 2=Cone, 4=Direction (which fields are valid)
[posX:f32][posY:f32][posZ:f32]     // PositionToApply (valid if targetKind&1)
[coneTipX..FwdZ : 8×f32]           // Cone Tip.xyz,Height,Radius,Forward.xyz (valid if targetKind&2)
[equipIndex:i32]                   // valid only when inputKind==EquipSelect
[nonce:u32]                        // IntentDedup key
```
This is the UNION of the existing 4 intent payloads (move=pos, ability=actor+pos, overwatch=cone, equip=index), tagged by `targetKind`/`inputKind`. It serializes the varied `TacticalAbilityTarget` (actor netId / position / cone / direction) in ONE envelope. Codec lives in `TacticalLiveCodec.cs` (one new struct + Encode/TryDecode), unit-tested like the rest.

### 3.2 Equip-selection decision: EXTEND the generic intent (don't keep a dedicated one)
`SetSelectedEquipment` is a non-ability setter, but it is a discrete client→host input identical in shape (actorNetId + a small payload + nonce). Fold it via `inputKind==EquipSelect` (carries `equipIndex`). One surface, two input kinds. (Reload/throw/heal/melee are all real abilities → they go via `inputKind==Ability`, NO new code per ability.)

### 3.3 Host resolve + execute (REUSE existing pattern, `TacticalCombatSync.HostOnAbilityIntent` :101 is the template)
```
HostOnIntent(payload):
  decode; IntentDedup.IsNew? else drop
  actor = ResolveLiveActor(actorNetId)
  if inputKind==EquipSelect:  ec=actor.Equipments; equip=byIndex; InvokeSetSelectedEquipment(ec,equip)   // EquipSync helper
  else (Ability):
    ability = GetAbilities<TacticalAbility>().First(a => a.Def.Guid == abilityDefGuid)   // CombatSync.ResolveAbilityByGuid :409
    target  = BuildTarget(targetKind: actor→ResolveLiveActor, pos, cone)                  // union of BuildMoveTarget/BuildShootTarget/BuildOverwatchTarget
    ability.Activate(target)                                                              // the ONE generic funnel
```
`TacticalAbility.Activate(object)` is the universal entry every tactical ability overrides (Move/Shoot/Overwatch/Reload/Throw/Heal/Melee). The host runs it authoritatively; all resulting state changes flow back via Surface B (the delta) + the kept events.

### 3.4 Abilities that MUST NOT be relayed
- **Passive / automatic** abilities (no player Activate): `OverwatchAbility`'s reaction FIRE (host-driven), AI abilities (host runs AI), any `TacticalAbilityDef` with no manual target. Filter: only relay an intent when the patched `Activate` is invoked by LOCAL player input on a mirroring client (the existing prefix gate already does this — `IsClientMirroring` + the patch sits on the player-driven Activate path).
- **End-turn** is faction-level, not an actor ability → KEEP its own `tac.intent.endturn` 0x84.

---

## 4. DESIGN — Surface B: generic per-actor STATE-DELTA (host→all)

The spine. Mirrors all mutable per-actor state so any field/stat/status syncs by default.

### 4.1 Wire shape — `tac.actorstate` (NEW surface 0x8F, host→all)
A batch of changed-actor records (one envelope can carry many actors; chunk via the existing `TacticalDeployChunkCodec` pattern if over the 60 KB single-envelope cap):
```
[seq:u32]
[actorCount:i32]  then per actor:
  [netId:i32]
  [posX,posY,posZ : 3×f32][rotFwdX,rotFwdY,rotFwdZ : 3×f32]   // position + facing (absolute)
  [health:f32][armor:f32][ap:f32][wp:f32]                      // absolute StatusStat values (backstop)
  [selectedEquipIndex:i32]                                     // -1 = none
  [overwatchArmed:bool] (+ 8×f32 cone if armed)                // folds tac.overwatch.state
  [statusCount:i32] then per status: [statusDefGuid:string][sourceNetId:i32][value/duration:f32]   // the generic status set
```
**All values ABSOLUTE, never deltas** — re-applying the same record is a no-op; no double-apply with the `tac.damage` event (which subtracts). Idempotent by construction.

### 4.2 DIFF strategy — RECOMMEND: per-actor full-record-on-change, field-equality dirty pre-check
For ~10–30 actors, field-level dirty flags are over-engineering. RECOMMEND the geoscape pattern (`GeoVehicleStateDiffer`): host keeps a per-actor *last-broadcast signature* (cheap struct/string of the fields above, like `TacticalVisionSync._lastBroadcastSig` :46); on a flush tick it recomputes each actor's signature and ships the FULL per-actor record ONLY for actors whose signature changed. Idle actor = 0 bytes. Simple, reliable, already proven in this codebase (vision + geoscape both use signature-skip).

### 4.3 Host CHANGE-DETECTION hook — RECOMMEND a periodic flush on the tactical Timing (NOT per-stat events)
**Grounded investigation:** there is a per-actor `Health.StatChangeEvent` (`TacticalActorBase.cs:616` subscribes `OnHealthChange`) and `StatusComponent.OnStatusesChanged` :48 — but hooking dozens of per-stat events per actor is chatty, ordering-fragile, and re-entrant with the host's own Activate. The CHEAPER, more reliable trigger:
- **Drive a flush coroutine on the live tactical `Timing`** (the SAME `Timing.Start(IEnumerator<NextUpdate>,…)` mechanism every existing module already uses — `TacticalDeploySync.InvokeTimingStart` :268, `TacticalMoveSync.InvokeStart` :576). A tactical update loop DOES exist: the level's `Timing` ticks every frame, and coroutines yielding `NextUpdate.NextFrame` run each frame (proven by the deploy capture/reconcile coroutines). 
- **Flush cadence:** end-of-host-action + a low-rate heartbeat. Concretely: flush (a) immediately after `HostOnIntent` finishes a relayed ability (the host just mutated state), (b) on the host's own `MoveAbility.OnPlayingActionEnd`/`ApplyDamage` postfixes (already wired — they become "mark dirty + flush" instead of bespoke broadcasts), and (c) a ~2–4 Hz safety heartbeat coroutine that flushes any actor whose signature drifted (catches host-AI mutations + DoT ticks + status expiry). The signature pre-check (§4.2) makes the heartbeat ~free when nothing changed.
- This mirrors the geoscape verdict (§4 of the geoscape spec): *"host snapshots each live entity's RecordInstanceData at tick start, diffs at tick end, ships ONLY changed entities."* Same model, per tactical actor.

### 4.4 Client APPLY order + idempotency + suppression
Apply order per actor record (matters for correctness):
1. **Position + facing** → `SetPosition`/`SetForward` (but SKIP if a concurrent `tac.move.start` animation is mid-flight for this netId — let the animation land; the delta reconciles after, exactly as `ClientOnMove` does today).
2. **Statuses** → reconcile (add missing, remove absent) via `ApplyStatus`/`UnapplyStatus` under `_applyingRemote` re-entrancy flag — see §6 fidelity.
3. **AP/WP/Armor** → `StatusStat.Set` (absolute).
4. **Health** → `Health.Set(absolute)` ONLY as a backstop AND ONLY if it does not contradict an in-flight `tac.damage` (guard: don't `Set` Health within N ms of applying a damage event for the same actor; the event already set it). Health delta is the convergence floor, not the primary path.
5. **Selected equipment** → `InvokeSetSelectedEquipment` (under `_applyingRemote`).
6. **Overwatch cone** → cosmetic `ApplyCosmeticArm`/`RemoveCosmeticArm`.
All under one `_applyingRemote`/`SuppressEvents` scope so the delta apply does NOT re-trigger host-only effects (no re-broadcast, no re-roll, no death-cascade re-run). Seq-guarded via `TacticalLiveSeq` (per-surface last-writer-wins).

---

## 5. COEXISTENCE — keep vs subsume (the migration table)

| Existing surface | id | Verdict | Why |
|---|---|---|---|
| `tac.deploy` / `tac.deployChunk` | 0x80/0x81 | **KEEP** | Turn-0 full snapshot seed (the spine mirrors *changes* after this). |
| `tac.intent.move` | 0x82 | **SUBSUME** → `tac.intent` Ability(pos) | One generic intent. |
| `tac.move.start` | 0x86 | **KEEP** | START animation — spine is teleporty without it. |
| `tac.move` (END outcome) | 0x83 | **SUBSUME** → delta position | Final cell is an absolute pos field in the delta. (Keep transitionally for the move-reconcile branch logic until the delta's "skip-if-animating" path is in-game-proven.) |
| `tac.intent.endturn` | 0x84 | **KEEP** | Faction-level input, not an actor ability. |
| `tac.turn` | 0x85 | **KEEP** | Faction handoff + view-state drive (not per-actor state). |
| `tac.intent.ability` (shoot) | 0x87 | **SUBSUME** → `tac.intent` Ability | One generic intent. |
| `tac.damage` | 0x88 | **KEEP** | EVENT for death/knockback/FX + status-on-hit. Delta Health is the idempotent backstop. |
| `tac.vision` | 0x89 | **SUBSUME (later)** | Per-faction reconcile — fold into a faction-scoped delta sibling, or keep as a spine channel. Lowest-risk to retire LAST. |
| `tac.intent.equip` | 0x8A | **SUBSUME** → `tac.intent` EquipSelect | One generic intent (non-ability kind). |
| `tac.equip` (outcome) | 0x8B | **SUBSUME** → delta `selectedEquipIndex` | Field in the delta. |
| `tac.intent.overwatch` | 0x8C | **SUBSUME** → `tac.intent` Ability(cone) | One generic intent. |
| `tac.overwatch.state` (outcome) | 0x8D | **SUBSUME** → delta overwatch cone | Cone field in the delta (cosmetic client apply). |

**Double-apply guards (the two that matter):**
1. **Damage event + Health backstop:** `tac.damage` SUBTRACTS (`Health.Subtract`); delta SETS an absolute. They never both fire for the same change because the delta carries the post-event absolute. Guard: client suppresses a delta Health-`Set` that would contradict a same-actor damage event applied in the same flush window (§4.4 step 4). Never let the delta re-run the death cascade — it only sets the numeric Health.
2. **Client-suppress so delta apply ≠ host-only effect:** every apply runs under `_applyingRemote`/`SuppressEvents` (the existing pattern in CombatSync/EquipSync/OverwatchSync) so a status `ApplyStatus` does not re-run that status's host-only `OnApply` side effects that already replicated (e.g. a Poison's first-tick damage rides `tac.damage`, NOT the status re-apply).

---

## 6. Teleport-vs-animate boundary

| Action | START animation event | Pure delta mirror |
|---|---|---|
| Move | ✅ `tac.move.start` (animated navigate, concurrent) | delta carries final pos as reconcile/backstop |
| Shoot / overwatch-fire | (handled by `tac.damage` FX + native weapon anim on host; client shows impact via damage event) | delta carries post-shot AP/WP/target-HP |
| Reload / equip swap | native holster/draw anim runs locally on `SetSelectedEquipment` apply (no extra event) | delta carries `selectedEquipIndex` |
| Throw / heal / melee | OPTIONAL future `tac.ability.start` for cast anim (defer; damage event + delta cover state) | delta carries resulting state |
| Status gain/loss, AP/WP, facing, overwatch cone | — (no animation needed) | ✅ pure delta |

Rule: an action gets a START event ONLY when the visual is a TRAVELLING/continuous animation the delta can't reproduce (movement). Instant/cosmetic changes ride the delta alone.

---

## 7. Status reapply FIDELITY (the subtle risk — §2 #7)

`ApplyStatus(StatusDef,source,target)` runs the status's `OnApply(this)` (`StatusComponent.cs:224`), which for many statuses has HOST-ONLY side effects (deal damage, spend resources, spawn). On the client mirror we must add the status WITHOUT re-running those effects:
- **Reconcile, don't replay:** the delta carries the CURRENT status SET (defGuid + source netId + value). Client computes a set-diff vs the actor's live `Statuses` (`GetStatuses` by def guid) — `ApplyStatus` only the genuinely-missing ones, `UnapplyStatus` the absent ones. Re-applying a status already present is skipped (no `OnApply`).
- **Suppress side effects on apply:** wrap the client `ApplyStatus` in `_applyingRemote`; the status's *damage* side effect already replicated via `tac.damage`, so the client's `OnApply` must be neutered for the damage path. Two options (pick per-status, default = the cheap one): (a) rely on `_applyingRemote` + the existing `FireWeaponPatch`/ApplyDamage suppression already gating client damage, so a re-fired `OnApply` damage is dropped client-side; (b) for statuses with non-damage host-only effects, prefer reconstructing the status object from `TacActorBaseInstanceData.Statuses` (the serialized form the snapshot uses) which restores state WITHOUT `OnApply` — same path the deploy hydrate already trusts. **RECOMMEND (b) as the fidelity-correct path** (mirror the engine's own load: statuses come back via `ProcessInstanceData`, never via re-`Activate`), with (a) as the fallback for live mid-turn adds.

---

## 8. Risks (top, ranked)

1. **Status reapply fidelity (§7)** — re-running `OnApply` double-applies host-only effects. Mitigation: reconcile-not-replay + `_applyingRemote` + prefer the serialized-status restore path. HIGHEST risk; de-risk in the status increment with a 2-instance Poison/Paralysis test.
2. **Host change-detection reliability (§4.3)** — a missed mutation never broadcasts → silent divergence. Mitigation: the ~2–4 Hz signature-diff heartbeat is the catch-all backstop (any drift heals next tick); the signature pre-check keeps it cheap. This is the geoscape "rolling resync" idea applied per-actor.
3. **Bandwidth / flush rate** — full per-actor record × many actors × heartbeat. Mitigation: signature-skip (idle actor = 0 bytes), chunk only over-cap, absolute-value records compress well. ~10–30 actors at 2–4 Hz with mostly-idle signatures is tiny vs the deploy snapshot. Threading/ordering: all on the tactical `Timing` (single-threaded, same as every existing module) → no `Timing.Current` violations, deltas ordered after START events by apply-order guard (§4.4 step 1).

---

## 9. INCREMENT PLAN (each = a shippable DLL, independently in-game-verifiable; commit to inner `main`, tests-green; ADDITIVE-first then retire surface-by-surface — geoscape playbook)

| Inc | Delivers | Subsumes (only after in-game OK) | In-game test |
|---|---|---|---|
| **Inc T1 — State-delta BASELINE (the spine + biggest "всё разом" payoff)** | NEW `tac.actorstate` 0x8F (host→all) carrying **AP/WP + statuses + overwatch-cone + selected-equip + position/facing + absolute Health/armor backstop**, per-actor signature-diff, ~2–4 Hz Timing heartbeat + flush-on-host-action, client absolute-apply under `_applyingRemote`. Runs ALONGSIDE all existing surfaces (additive — the old outcomes still fire; the delta is a redundant convergence layer). **This already delivers the AP-sync the user asked for, for every actor, plus statuses.** | NOTHING yet (additive) | 2-instance: client soldier AP/WP, an applied status (Poison), overwatch cone, and a weapon swap all mirror within ~1 s of the host change. Existing surfaces still working (no regression). |
| **Inc T2 — Generic ability-intent** | NEW `tac.intent` 0x8E (client→host) with `inputKind`(Ability/EquipSelect) + `targetKind` union; host resolves `GetAbilities<TacticalAbility>` by guid + `Activate`; replaces the 4 per-ability intent SENDERS (the host outcome side still uses old surfaces, now backed by the T1 delta). | (senders only, internally) `tac.intent.move/ability/equip/overwatch` | 2-instance: client move, shoot, equip-swap, overwatch-arm ALL work through the one intent surface; a previously-unsynced ability (e.g. reload/heal) now syncs with ZERO new per-ability code. |
| **Inc T3 — Retire subsumed OUTCOME surfaces (one per commit, in-game-gated)** | Delete `tac.move`(END)/`tac.equip`/`tac.overwatch.state` outcome broadcasts — the T1 delta now carries pos/equip/cone. KEEP `tac.move.start` (anim), `tac.damage` (event), `tac.turn`, `tac.intent.endturn`. | `tac.move`(END) 0x83 → `tac.equip` 0x8B → `tac.overwatch.state` 0x8D, one at a time | After EACH retire: 2-instance confirms the corresponding state still mirrors (via the delta) before the next retire. Revert-to-known-good on 3+ cascading fixes (MEMORY `dont-replace-working-architecture`). |
| **Inc T4 — Fold vision (LAST, optional)** | Move `tac.vision` into a faction-scoped delta sibling OR keep as the one remaining dedicated channel. | `tac.vision` 0x89 | 2-instance: spotted-enemy markers + shoot target-gate still correct. Retire only if it cleanly folds; otherwise leave (it already works). |

**First increment (T1) — fully specified:**
- **New code:** `TacticalActorStateSync.cs` (engine glue) + a `ActorStatePayload` struct + `EncodeActorState`/`TryDecodeActorState` in `TacticalLiveCodec.cs` (pure, unit-tested) + a `ActorStateSigner` (pure per-actor signature, unit-tested) + surface id `TacActorState=0x8F` in `TacticalSyncSurfaces.cs` + one dispatch arm in `TacticalDeploySync.HandleTacticalEnvelope`.
- **Host:** a flush coroutine started on `LiveTlc.Timing` at deploy-capture (alongside the existing vision seed); each flush walks `Registry.Entries`, reads {pos via `GetPos`, facing via `Rot`, AP/WP via `ReadApWp`, Health via `(float)Health`, statuses via `GetStatuses`+`Def.Guid`, selected-equip via `EquipIndexOf`, overwatch cone via `FindOverwatchStatus`+`FlattenConeStruct`}, builds a signature, ships changed actors only (`LiveSeq.Next(TacActorState)`). Plus a flush call at the tail of each existing host outcome postfix (move-end, damage, equip, overwatch) so a change broadcasts immediately, not only on the heartbeat.
- **Client:** `HandleActorState` → `ShouldApply` seq guard → per-actor absolute apply in the §4.4 order under `_applyingRemote`, skipping position if a `tac.move.start` animation is mid-flight for that netId.
- **Reused as-is:** `ResolveLiveActor`/`NetIdForLiveActor`/`Registry`, `TrySetPosition`, `ReadApWp`/`SetApWp`, `InvokeSetSelectedEquipment`/`EquipIndexOf`, `ApplyCosmeticArm`/`RemoveCosmeticArm`/`FlattenConeStruct`, `ApplyStatus`/`UnapplyStatus`, `TacticalLiveSeq`, `BroadcastToAll`, `TacticalDeployChunkCodec` (if over-cap).
- **DEFAULT-additive:** existing surfaces untouched in T1 → DLL behavior-identical except the new redundant convergence layer; safe to ship and verify before retiring anything.

## Cross-refs
- Existing tactical surfaces + rail: `Multipleer\src\Sync\Tactical\TacticalSyncSurfaces.cs`, `TacticalDeploySync.cs` (`HandleTacticalEnvelope`).
- Read/set helpers reused: `TacticalCombatSync.cs` (AP/WP, damage), `TacticalEquipSync.cs` (equip), `TacticalOverwatchSync.cs` (cone/status), `TacticalVisionSync.cs` (KnownActors), `TacticalMoveSync.cs` (pos/nav).
- Pure cores to extend: `TacticalLiveCodec.cs`, `TacticalActorRegistry.cs`.
- Geoscape consistency (retire surface-by-surface): `docs\superpowers\specs\2026-06-17-multipleer-full-geoscape-replication-feasibility.md` §5/§7.
- Roadmap tracker: `Multipleer\docs\COOP-SYNC-ROADMAP.md`.
- Prior tactical replication design (per-action, being pivoted): `docs\superpowers\specs\2026-06-17-multipleer-tactical-replication-design.md`.
- MEMORY rules in force: `multipleer-full-state-replication`, `dont-replace-working-architecture` (minimal targeted change, retire one at a time), `pp-serializer-context-and-pump` (status-restore path uses the configured Serializer if going via InstanceData).
