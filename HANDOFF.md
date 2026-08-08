# Handoff — session 2026-08-08 (night, autonomous)

Previous handoff is OBSOLETE. Nine one-shot agents. Commits on `main` in `Multiplayer2`, local, published as **v0.9.5-beta**. Final state: `dotnet build` 0 errors/0 warnings, `law-integrity OK — 134 file(s) + 60 inline = 194`, `RAILCHECK GREEN — laws-run=194/194 known-violations=1` (the one is the pre-existing baselined L62 ClockPhaseProbe).

## 1. What this session did

### Round 1 — campaign start (OWNER-CONFIRMED IN GAME)

| sha | what |
|---|---|
| `d0a97ae` | fix(campaign): window order inverted — host raised `IntroBetterGeo_0/1/2` behind its OWN curtain; all 4 view requests priority 0, native `GeoscapeViewSwitchQuery` FIFO → host got dialogs→cutscene, clients cutscene→dialogs. Harmony PREFIX parks the host's own raise while `SaveTransferCoordinator.Revealed` is false, re-invokes the game's own handler after reveal (`src/Rail/EventPopup.cs:86-165`, `:509`, `:1449-1463`). Prefix+postfix share `__state` because Harmony runs postfixes even when the prefix skips |
| `64b170a` | laws L194-L197 |
| (same round) | fix(campaign): cutscene skip silently dead on one client — `CanCarryWindow` only checked the event system had loaded, so 3 modals released 0-130 ms after intro started; a stale input override made `UIStateGeoCutscene.EnterState`'s `SetInputState("Cutscene")` be stored and discarded (documented at `RevealInputLock.cs:20-23`). One client sat 121.83 s, the other skipped in 5.26 s. Fix: one term in `CanCarryWindow` (`EventPopup.cs:577-579`) |
| (same round) | fix(campaign): non-answering peer never reached the squad screen — opened ONLY from `MissionEncounterNav.Postfix` on `UIModuleSiteEncounters.FinishEncounter`, a local dialog funnel, bailing at one of four unlogged returns; the postfix also read `ev.Record` which on a mirroring peer can be the placeholder `RaiseMirrored:568` mints (now `EventPopup.LiveRecord`). Fix: new `MissionArrivalNav` (`src/Rail/MissionSync.cs:487-640`) opens on mission ARRIVAL, bounded by `ArrivalGivenUp` (60 s, drops loudly) |

### Round 2 — built + deployed, NOT confirmed in game

| sha | what |
|---|---|
| `dc7e9d9` | fix(ui): countdown overlay Cancel dead — `MultiplayerUI.EnsureBarCanvas` (`src/Lobby/MultiplayerUI.cs:163-182`) builds the mod overlay root as `Canvas`+`CanvasScaler` with **no `GraphicRaycaster`** — every widget on it was decorative by construction; hidden for months because the bar and `PlayerPanel` set `raycastTarget=false` deliberately. Label overflow fixed with best-fit + Wrap + Truncate. DEBT: raycaster repair lives in `src/Lobby/CountdownPanel.cs`, its real home is `EnsureBarCanvas`; `UiToolkit.CreateButton` has the same null-`targetGraphic` gap for every from-code lobby button |
| `8e62eac` | fix(tactical): client free-aim never fired — host called `ability.GetDisabledState()` UNFILTERED → `NoValidTarget` → `Validate` rejected → forced settle's `CancelActions` tore out the wind-up. Host's own clicks bypassed via the `RelayMirror` branch and never reached `Validate` — that asymmetry was the defect. Fix passes `IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter` INTO the call, narrowed by `ability is ShootAbility`, mirroring `UIStateShoot.ConfirmShoot:1295` / `UIStateFreeCam.ConfirmShoot:466`. SEPARATE root from `7b75de9`: that commit pulled the sweep out of `TargetIsOffered` but left `GetDisabledState`, whose `NoValidTarget` arm IS `ShootAbility.HasValidTargets` = the same sweep. Grenade/cone hit the same arm and were probably broken the whole time |
| `f9ecd0e` | fix(tactical): animation start desync — every peer plays from the host's mirror, including the actor. Seam = `TacticalViewState.ActivateAbility` (decompile `PhoenixPoint.Tactical.View/TacticalViewState.cs:259`), the game's ONE player-click funnel; `UIStateShoot:1385` is its only override and calls base. One layer down impossible: `TacticalAbility.Activate` is virtual and `ShootAbility.Activate:165-174` runs its own `PlayAction(Shoot)`. Acting peer activates nothing locally; host's own click serialised → `HandleActivate(engine, peer 0, …)`. Mirror `excludePeer` no longer skips the origin. Bound `EchoCeilingFrames = 720` (12 s), deliberately longer than host's `DeferCeilingSeconds = 10f`; on trip `ECHO LOST`, order NOT replayed locally (would be a second shot, L83). Generic via `OrderWaitsForTheEcho`, one exclusion by the game's marker interface `IMoveAbility` (not `typeof(MoveAbility)`) because `FollowupAbility` is in `TacAbilityTargetCodec.Dropped` — deferring a move would DELETE the follow-up attack |
| `8e44c2e` | fix(tactical): broken-arm weapon resolution — host resolved by def guid alone and `ActorComponent.GetAbilityFiltered:211-221` returns the FIRST match, but `Overwatch_AbilityDef`/`Reload_AbilityDef` mint one instance PER WEAPON — host always took the primary rifle's, `PreSelectSourceEquipment` published that rifle on the 0x82 settle (L186). Fix: `ResolveAbility` prefers the instance whose source == `SelectedEquipment`, falling back to plain def match, at BOTH host-validate and mirror-replay sites (mirror half predicted, not observed). Separately per-turn use counter `TacticalActor._abilityUsesThisTurn` keyed by `TacticalAbilityDef` (one key for every weapon's overwatch, cleared only at turn edge) — client's speculative activate spent it while host kept 0; now carried on the 0x82 settle via the game's own `TacActorInstanceData.AbilityUsesThisTurn` |
| `a2b61f2` | fix(geo): haven infiltration / steal aircraft from a client — `StealAircraftAbility.ActivateInternal` → `GeoHaven.PrepareHavenMission` (`GeoHaven.cs:1054`, `wireMission:true`) writes `Site.SetActiveMission` at `:1091`; nothing gated it → client minted mission on its own graph, host rejected launch (`GeoMission.IsRunnable` has ZERO overrides, `MissionRunnable=false` = null). Same funnel serves `HavenFacilityController.cs:110` and `HavenInteractionController.cs:219`. Fix gates the FUNNEL, host re-runs the game's own gates, zone rides as INDEX into `haven.Zones` (tag re-derivation unsafe — `GeoHavenZoneDef.AvailableMissionsTags` is a list, several zones share a tag). Widening L42 exposed two further client-local root mints, `GeoFaction.CreateScanner` / `CreateAncientSiteProbe`, now gated (`ClientSimGate.cs:407`) |
| `4fbd250` | fix(geo): client exploration in a foreign epoch — see durable finding #1 below |
| `2f29baf` | fix(tactical): move-overlay coroutine crash — withhold answered an EMPTY array from `MoveAbility.GetTargetsData`, and that empty array IS the null — `MoveAbility.cs:26 HasValidTargets => GetTargetsData().Any()` → `NoValidTarget` → `IsEnabled()` false → `MoveAbilitySceneViewElement.ValidMoves:69-79` returns null, which `UpdateMoveAreas` re-reads after yields (`:237/:243/:253/:259`) though it guards once at `:223`. Stopping impossible: `ValidMoves:73` evaluates `IsEnabled()` two instructions from the dereference. Fix FEEDS an empty list via a postfix on the getter; sited on the property so it covers every mid-sweep UI release |
| `3224cfc` | fix(ui): ready button — see section below |
| `6d38ff4` | feat(ui): ping colours + arrow |
| `6e64842` | chore(diag): keyless-text-bind diagnostic damping |

### The ready button — three wrong diagnoses

Symptom: no hover frame, no click, host AND clients. It WORKED several major updates ago.

- `82e039c` layer theory (`new GameObject` starts on layer 0, undrawn, `depth == -1`): REFUTED — live log `MP_ReadyHit(layer=5 depth=258)`, and its `LogError` never fired.
- `10dfda8` archaeology blamed `30a64e3` ("stop the ready button eating the map"), which silenced the clone's twelve NATIVE graphics and substituted one hand-built face; the clone is `Object.Instantiate` of the native End Turn button so its own graphics ARE its clickable face. `TrimRaycast` deleted (-107 lines) — correct hygiene, KEEP it deleted, but not why the button was dead. **Two laws MANDATED that defect**: `L127` arm (d) and `L182` arm (d) both REQUIRED a tactical clone to silence its raycast and name a built face; both withdrawn in place with the reason recorded.
- Container-overhang theory: also refuted — module has no `Mask`/`RectMask2D` (proof: native button's own glow draws 6.67 world units past the same edge, uncut).
- `3224cfc` **ACTUAL fix**: the drop compounds to 110 parent-local units (60 height + 30 gap + 20 glow clearance) below a button already near the bottom of the HUD, putting the rect OFF SCREEN — no pointer position maps to it, on any peer, with every wire correct. Clamp onto `canvas.rootCanvas`, same bound/reason as `src/Lobby/PlayerPanel.cs:305`. No reparent: `UIModuleBehavior.SetStateID` (`Base.UI/UIModuleBehavior.cs:21-56`) hides the module through its Animator, so leaving it strips visibility inheritance. STILL UNOBSERVED — grep `ready button footprint` for `-> ON SCREEN`, then `ready button IS REACHABLE`; `ON SCREEN` + `UNREACHABLE` means the new `ancestors=` text names the blocker.

### Ping paint — REFUSED with reasons (do not retry)

The owner asked for the full-body ability flash (the white stamina-restore / heal paint) to mark pinged actors. It is `IHighlightable.Highlight(highlight, friendly, ability: true)`. Its branch clones per-actor materials (`HighlightControllerComponent.cs:179-181`) and skips the global shader colour, BUT the entry is the same latched `if (_isHighlighted == highlight) return false;` (`:144-153`), one bool per body part, driven by the game's own targeting with `ability: true` (`UIStateShoot.cs:1445`, cleared `:1463`) — so a ping on an actor being aimed at is swallowed, and the ping's expiry lowers the latch under the game, leaving paint nobody removes. Independently fatal: the ability look is one scene-wide shader asset (`LightingSettingsCharacters.cs:42`) with NO per-call colour, so green/blue is impossible. Actor pings keep the beacon shaft, which follows the actor and IS tintable per instance.

Shipped instead: own-green/others-blue decided at the VIEWER (`PingMarkers.cs:276` vs `:290`, nothing on the wire carries a sender); arrow half-extent `max(30, height*0.05)` = 2.5x; click → `CameraDirector.Hint(CameraHint.ChaseTarget, …)` per `TacticalActorViewBase.cs:491-501` and `GeoscapeView.cs:1109-1113`, with `ChaseTransform` deliberately null and `GeoscapeView.ChaseTarget` deliberately NOT called (a transform-named chase never self-ends, `PlanarScrollCamera:747/:838`; `ChaseTarget` installs the globe's sticky snap — would glue a watcher's camera to someone else's soldier, the L162 defect).

---

## 2. Law-quality crisis

NINE laws caught this week asserting a CALL rather than an OUTCOME, or actively obstructing a fix: **L169, L177, L182, L175 (inert), L220 (twice), L117, L42, L80, L168, L186, L115, L127**.

- **L127**(d) and **L182**(d) — MANDATED the ready-button defect and were withdrawn.
- **L80** and **L168** — demanded a shape a correct fix had to change; amended in place.
- **L42** — receiver filter `c.DeclaringType == vehicle` WAS its declaration despite its own doc claiming the set is "DISCOVERED, never declared" — widened to `RailCoveredRoots()` and shared with L270 via `Program.AbilityGestures` so the two cannot drift.

Also: **a law can be unfalsifiable by accident.** `L210` as first written called `AbilityDisabledState.ToString()`, a CLASS whose `ToString()` resolves a loc key via `GameUtl.Game()` and throws `SecurityException` outside a running game — the throw sat on the FAILING branch of two arms, so the law was green while the waiver worked and turned the run into `GameUtl HARNESS-CRASH` the moment it regressed. Keep every state/reason name a STRING LITERAL in laws.

Standing rule: falsify every ARM for real (defect edit → that arm's RED string → restore → green), include an executed positive control (`FakeSeam`), and report any arm that cannot be turned red as DECORATIVE.

Anti-quorum law **L119** fired correctly on this session's own work — a ready-button press log that merely read `ReadyCount`/`TotalCount` turned RailCheck RED. Counters dropped, press log kept.

### Law count at HEAD

- `tools/law-count.txt`: `files=134`, `inline=60` = **194 registrations**
- Known violations: 1 (baselined L62 ClockPhaseProbe)

---

## 3. Still open / not verified in game

NOTHING from tonight is confirmed in game except the round-1 campaign-start fixes and item transfer. Animation timing changes the path of EVERY combat action — test it first; it is a single-argument revert if it misbehaves.

### Greps

- `ECHO wait` / `ECHO host` / `ECHO LOST` / `ECHO busy` — animation echo lifecycle
- `ready button footprint` + `IS REACHABLE` — ready-button position proof
- `[MP][windows] queue DROPS` and `window-queue restore: … Kept:` — window queue lifecycle
- `[MP][mission] CLIENT haven mission BLOCKED at S#` then `HOST intent APPLIED op=prepare-haven` — haven infiltration gate
- `exploration NOT re-seeded` / `a STALE start` — should trend to zero after the epoch fix
- `Broken coroutine call chain` in every `Player.log` (Unity-only)
- `[Multiplayer] ping arrow CLICKED`
- `CRC backstop:` — serious variant is `STILL diverged … after a forced re-emit`

### CRC

Fired exactly ONCE all session, `root 'S#79' DIVERGED on peer 1`, explained by the haven bug. The serious variant did not appear, but the session ended 6 s later — weak evidence, not a clearance of the accepted host-keeps-its-own-graph risk.

### Untouched backlog from the log sweep

- **Destructible guid collisions** — 60 collisions + 119 objects within 0.1 unit, all peers; damage to one of a colliding pair can be silently dropped.
- **Two mirrored abilities never played on one client** — `RecoverWill`, `StandBy`, queued 10 s behind `IdleAbility`, law L78. Plus 27 deferred mirrors — re-measure after the animation change.
- **Host publishes tactical one-shots before any client has a map at mission entry** — self-heals via settle.
- **60 s liveness alarm is a false positive** — timer is not reset when a new barrier arms.
- **`GeoMissionGenerator`** — the session's only PERMANENT `DiffEngine` exclusion.
- **`TacticalItem`-target activation with no shared address** — cannot ride (1 occurrence).

### Environment

Session ran with mismatched mod sets (`Mod missing on client: com.kumarin.Resource_Replacer v2.0.0.0` on the join soft-gate), and two equipment-parity failures followed. Install the same mods on every instance.

---

## 4. Superseded from the previous handoff

**Shot-animation desync: CLOSED by `f9ecd0e`.** The owner chose: every peer plays from the host's mirror, including the acting peer. The three decompile questions from the previous handoff were already answered; this session shipped the implementation. `AccumulationClientGate` untouched (TFTV replaces the DamageResult factory). The root — one event going two channels (`TacticalCommandSync.cs:750-753`, native `Activate` inside `SyncApplyScope` + host resolution on the rail) — is resolved by deferring the local activation and waiting for the echo.

**Ready button: CLOSED by `3224cfc`.** Was OFF SCREEN, not a wiring/layer/raycast problem. `TrimRaycast` deletion kept as correct hygiene. Two laws that mandated the defect withdrawn.

**Lobby ready gate, phantom peer, geoscape-return hang, vehicle inventory, Kaos shop, post-mission replenish, player panel, ping markers, deployment countdown**: all from the previous handoff, all re-shipped or extended this session. Verification status per item in section 3.

---

## 5. Rules that bit us, for the next session

- **Laws that assert shape, not outcome, are actively harmful.** L127(d)/L182(d) MANDATED the ready-button defect. L80/L168 had to be amended to allow a correct fix. Falsify every arm before trusting it; report unfalsifiable arms as DECORATIVE.
- **`AbilityDisabledState.ToString()` (or any game type's `ToString`) in a law** → `GameUtl.Game()` → `SecurityException` outside a running game. The throw landed on the failing branch of two arms, so the law was green while valid and crashed the harness when it regressed. Use STRING LITERALS for state/reason names.
- **Shared-tree discipline with N agents** — still applies. Explicit pathspecs only, never `git add -A`.
- **Law numbers must be ASSIGNED, not max+1** — still applies. Coordinate via `tools/law-count.txt` and `docs/laws.md`.
- **A commit that registers a law must include the law FILE** — still applies.
- **Mismatched mod sets between instances** produce equipment-parity failures that look like sync bugs. Install the same mods on every test instance.
