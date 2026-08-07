# Laws — catalog

Two-level scheme + full law index. Lookup by `P<n>` or `L<n>`.

- `P<n>` = ARCHITECTURAL PRINCIPLE. Prose source `ARCHITECTURE.md`. **Its old wording "law N" means `P<N>`** — same numbers, renamed so `L` is the harness alone.
- `L<n>` = EXECUTABLE HARNESS LAW. `tools/RailCheck/L<n>_<Name>.cs`, or an inline method in `Program.cs` (marked *inline*). Run: `cd tools/RailCheck && dotnet run -c Debug` — exit 0 green, 1 red.
- `L-A`…`L-F` = boundary law, `docs/boundary-law.md`, letters, ride/refuse rules. Untouched here.
- NOT interchangeable: harness `L11` (no `LocalizedTextBind` rides covered) ≠ principle `P11` (UiEventMap repaint).
- Origin `incident` = header cites an observed failure (dated session / quoted report / log line / measured number / named breaking commit).
- Origin `principle` = reasoning, decompile, developer mandate or audit; no observed failure. A generic "this repo has paid for this class" is NOT incident evidence.
- Origin `unclear` = header does not say. Listed in Attention.
- Guard = literal tokens in the law's file: `premise-changed`, `POSITIVE CONTROL`, both, neither. See Attention — `neither` ≠ "checks nothing".

## Principles

- **P1 — TWO PRIMITIVES, AND NOTHING IS SILENT.** Intent (client→host gesture) + Delta (host→peers state) are the only two things on the wire; join/reconnect = native save transfer, not a delta; what a frame cannot carry is NAMED, never silently skipped.
  - pending amendment: 0xB6/0xB7 = third category, ephemeral presentation delta — `docs/design-event-windows.md` §4
  - laws: L34 L37 L39 L40 L41 L61 L62 L70 L71 L83 L93 L106 L107 L120 L122 L124 L125 L127 L128 L134 L135 L136 L142 L143
- **P2 — ONE PLACE NAMES THINGS.** `IdentityResolver` only; stable game ids; path grammar `root.Member.Member#elemKey`; resolved symmetrically on the client over its own graph; no stable key → subtree EXCLUDED + reported; never index-addressed.
  - laws: L10 L14 L17 L22 L28 L56 L58 L62 L101 L106 L107 L112 L113 L140
- **P3 — THE IDENTITY BOUNDARY.** Rail projects VALUES. Create/destroy/re-identify = hand-written structural layer, the ONLY bespoke sync code allowed. Authoritative logic behind a native setter (reward chains, campaign writes, `Complete()`) is the host's. A projector may not write what a simulation mints.
  - laws: L2 L3 L11 L29 L30 L31 L35 L36 L49 L66 L67 L69 L77 L81 L96 L110 L121 L126 L128 L131 L137 L147
- **P4a — BLOCK-FIRST INTENT SEAMS.** On a client outside `SyncApplyScope` a native state mutation is BLOCKED and converted to an intent; the HOST runs the native method; result mirrors back on the Delta rail. Presentation staging may proceed, model writes may not. One gate: `IntentRail.ShouldRunNative`.
  - laws: L12 L19 L24 L26 L32 L36 L42 L63 L65 L69 L76b L99 L110 L111 L138
- **P4b — SIM GATING.** Client clock not frozen → host-only sim funnels prefix-skipped on the client (`LevelHourlyUpdateCrt`, `Research.Update`), reschedule preserved.
  - laws: L42 L63
- **P4c — PRESENTATION SEAMS ALTER NOTHING.** Replay what the model already says. Never block, never write, never throw into game code.
  - laws: none scoped to P4c alone (defended jointly with P11)
- **P5 — TACTICAL SCOPE IS SHARED AND CONCURRENT.** Six peers command six soldiers at once. First-to-act-wins, no ownership table, no ledger a reload can lose. Which peer plays a mission = tactical scope, not a permission.
  - laws: L27 L44 L45 L47 L65 L68 L75 L76a L78 L80 L97 L99 L104 L111 L123 L130 L132 L139 L140 L141 L144 L145 L146 L149
- **P6 — DETERMINISM / CANONICALITY.** Sorted roots/children/subkeys, fixed metadata field order, byte-identical deltas on both peers. Serializer blobs licensed ONLY as structural payloads.
  - laws: L2 L4 L5 L6 L7 L10 L13 L15 L16 L20 L34 L35 L43 L66 L73 L76a L103 L126 L128
- **P7 — CONVERGENCE.** Idempotence under redelivery; seq-gap → resync; CRC per path-subtree = drift backstop; reject → scoped `ForceReemit` + the family's reconverge. Never log-only.
  - laws: L1 L7 L8 L12 L13 L25 L27 L41 L44 L57 L58 L98 L100 L105 L123 L131 L133 L137 L146 L150 L152 L153
- **P8 — APPLY SCOPE.** Every client apply wraps itself in `SyncApplyScope`: an apply never echoes back as an intent; apply-driven repaints stay suppressed at intent seams + the storage gates.
  - laws: L21 L51 L65
- **P9 — THE JOURNAL IS OBSERVATIONAL.** Written post-pipeline, never in the decision path; debug builds OK.
  - laws: none
- **P10 — PARITY IS BLOCKING.** Mod set, DLC set, per-mod settings, the GAME build and OUR build must match on every peer — the save-graph shape has to. An unresolvable def "cannot happen" → LOUD, never silent.
  - laws: L108 L114
- **P11 — REPAINT IS THE RAIL'S OTHER HALF.** A mirrored value reaches the screen through the game's OWN read-direction refresh (`UiEventMap`, `UiNativeRepaint`) — never a hand-rolled paint, never a lifecycle transition where the model can express it.
  - laws: L18 L21 L26 L38 L45 L46 L47 L51 L52 L54 L60 L77 L79 L81 L83 L92 L93 L95 L96 L98 L101 L102 L104 L115 L116 L117 L118 L124 L127 L129 L130 L135 L138 L139 L141 L142 L144 L147 L148 L149 L151
- **P12 — UNIVERSAL-FIRST.** Unnumbered in `ARCHITECTURE.md`, asserted throughout. ONE generic mechanism per layer; the per-subsystem copy is the "macaroni factory" the mandate forbids. One value rail, one intent engine, one nonce allocator, one dedup, one repaint primitive.
  - laws: L32 L50 L99 L143
- **P13 — NO QUORUMS, NOBODY KICKED. PROPOSED — not in `ARCHITECTURE.md`.** Developer mandate, stated only in the corpus (quoted by L84, L91): at any moment ANY player must be able to play everything; with 49 of 50 AFK the one active player still plays a whole campaign. Nobody is kicked. A host-side decision about peer P may read P's own intent + shared game state and nothing else. A wait on something that ends BY ITSELF is allowed; a wait on a PERSON is not.
  - laws: L26 L64 L71 L82 L84 L91 L94 L109 L118 L119 L120 L122 L129 L136 L143 L145 L150 L151
- **P14 — DRIFT IS THE GATE, AND A LAW ASSERTS AN OUTCOME.** `docs/rail-baseline.txt` + `docs/rail-contract.txt` = committed snapshots; ANY drift is harness-RED, so a field moving Excluded↔covered is a reviewable diff, never a side effect. A law asserts the executed OUTCOME, not the presence of a call, and carries its own non-vacuity.
  - laws: L6 L9 L22 L23 L33 L48 L49 L72 L103 L125
- **P15 — DERIVED, NOT MIRRORED.** Unnumbered in `ARCHITECTURE.md`, asserted in the coverage rules. State a peer can recompute closed-form from already-mirrored inputs is RE-DERIVED locally, never shipped: mirror the ORDER, derive the pose. Inverse holds — what cannot be re-derived (mist) MUST ride.
  - laws: L43 L53 L55 L59 L73 L77 L102 L115
- **P16 — FRAME BUDGET.** Unnumbered in `ARCHITECTURE.md`, asserted by the sliced walk. No unbudgeted graph walk on either peer; the rail never spends a frame the player is using; urgency never outbids local input. Also may not IDLE — a sim fact waits no longer than it must.
  - laws: L50 L74 L100 L153

## Law index

| id | title | P | origin | evidence | guard |
|---|---|---|---|---|---|
| L1 | every list-classed field has an `ApplyList` strategy | P7 | incident | 2026-07-18 resync storm | neither |
| L2 | no `[SerializeCustomCreate]` param unmatched | P6 P3 | principle | ARCHITECTURE.md Verification | neither |
| L3 | no Unity object reaches the blob codec | P3 | principle | ARCHITECTURE.md Verification | neither |
| L4 | leaf / list / blob codec round-trip | P6 | principle | ARCHITECTURE.md Verification | neither |
| L5 | abstract element types classified once the codec turns polymorphic | P6 | principle | boundary-law L-E | neither |
| L6 | every blob-reconstructed element type survives a REAL round-trip | P6 P14 | principle | `Program.cs:757` — "L4 only ever round-tripped a synthetic local class" | neither |
| L7 | dict-delete tombstone / census stay undecodable as values | P6 P7 | principle | — | neither |
| L8 | `SurfaceSeq` monotonic, idempotent, reorder-safe | P7 | principle | ARCHITECTURE.md P7 | neither |
| L9 | `GeoItemDict` coverage is non-vacuous | P14 | principle | re-inclusion; zero silently kills inventory sync | neither |
| L10 | element ORDER and MEMBERSHIP survive the wire | P6 P2 | principle | ARCHITECTURE.md order-vector | neither |
| L11 | no `LocalizedTextBind` rides covered (static belt for DefOwnership) | P3 | principle | boundary-law L-B | neither |
| L12 | `IntentDedup` + `[nonce][op]` envelope | P7 P4a | principle | ARCHITECTURE.md IntentRail | neither |
| L13 | field-codec CRC(host) == CRC(client) after apply | P6 P7 | principle | ARCHITECTURE.md Verification | neither |
| L14 | the DTO-twin coercions stay wired | P2 | principle | ARCHITECTURE.md "DTO twin resolution" | neither |
| L15 | owner back-ref rewired; nested husk named | P6 | principle | husk gate (boundary-law L-B) | neither |
| L16 | an owner post-read waiver is healed, not inherited | P6 | principle | — | neither |
| L17 | a duplicate ROOT key is a walk incident | P2 | incident | per-faction `VehicleID` collision — one aircraft survived, the rest eaten by first-wins dedup | neither |
| L18 | a stage baseline is bound, and the undo floor survives a repaint | P11 | unclear | "the minus button greys out and the spend cannot be undone" — no session named | neither |
| L19 | a loadout gesture emits exactly once; no result-ship from a model patch | P4a | principle | EquipSync generalized into IntentRail | neither |
| L20 | an init-only / unbuilt container applies or fails loudly | P6 | principle | — | neither |
| L21 | a screen whose `ExitState` writes back must be in `UiNativeRepaint.Table` | P11 P8 | incident | `UIStateVehicleRoster:128-131` → `ReplaceEquipments` undid the delta inside the apply | neither |
| L22 | the ordinal VALUE-RECORD codec + the coverage it exists for | P2 P14 | principle | re-inclusion of `AmmoManager.LoadedMagazines` | neither |
| L23 | every static reflection handle the sync layer holds RESOLVES | P14 | principle | `AccessTools.Method` null-on-miss class | neither |
| L24 | a skill-point refund carries its provenance and stays in range | P4a P7 | principle | — | neither |
| L25 | the CRC drift backstop hashes through THE canonical walk and sees a vanished entry | P7 | incident | RCA B1/A4 — a removed path emits no entry and no tombstone | neither |
| L26 | a blocking window pauses once, nobody vetoes a resume, a one-shot is never replayed | P4a P11 P13 | incident | 2026-08-04 live 1+2: host paused alone (21:20); hold-set veto (22:09); cinematic restarted 7× | premise-changed |
| L27 | the event-answer arbiter — first choice frozen for everyone | P7 P5 | principle | pure validator; the race no manual test reproduces | neither |
| L28 | root ownership: ONE instance, ONE root path | P2 | principle | RCA gap B3 trap T1 | neither |
| L29 | the structural DESCEND shape (null↔non-null) | P3 | incident | a finished mission stayed on the client's map forever, no incident line | neither |
| L30 | a Descend create frame carries its type name and its params | P3 | principle | — | neither |
| L31 | the structural ACTOR shape — never blob a MonoBehaviour-bound root | P3 | principle | `ActorComponent.CreateActor` would spawn a duplicate actor | neither |
| L32 | the AIRCRAFT intent family | P4a P12 | principle | RCA gap A2 | neither |
| L33 | a declared root that ships NOTHING | P14 | principle | RCA prediction about the "ST" statistics root | neither |
| L34 | a member whose CONTENT the rail refuses is NAMED, never counted as carried | P1 P6 | principle | husk gate blind to hollow-by-content | neither |
| L35 | the text-bind codec class (key + `_doNotLocalize`) | P3 P6 | principle | re-inclusion of four subsystems (43ac747) | neither |
| L36 | the GeoscapeEvent completion-funnel SET, not one funnel | P4a P3 | incident | one session with only `CompleteEvent` covered — a client bought from the marketplace out of the shared wallet | neither |
| L37 | the UNGUARDED-DEREF class — a half-built window over baked placeholder text | P1 | incident | `[HavenName]` NRE inside `ReplaceEventTokens`; title over "Fasdasdsadasg…" while our log said success | neither |
| L38 | repaint relevance — both over- and under-repaint | P11 | incident | 4-5 fps on `UIStateEditSoldier`; 15 churning kinds rebuilt the perk tree per batch | neither |
| L39 | the geoscape EVENT-WINDOW raise family (0xB6) | P1 P11 | incident | 54 of 94 replayed raises had `Site == null`; pre-join events re-raised | neither |
| L40 | no value leaves a batch unshipped and unannounced | P1 P7 | incident | `GeoscapeEventSystem.EncounterRecords` at 8636 B — the event ledger stopped mirroring permanently | neither |
| L41 | an apply onto a read-only façade succeeds or fails loudly | P1 P7 | incident | `NotSupportedException` on EVERY delta, deduped to one warning; aircraft routes never landed | neither |
| L42 | a client gesture mutating host state has an intent, or is gated | P4a P4b | incident | `ExploreSiteAbility` → `StartExploringCurrentSite` ran to completion on the client's unfrozen clock | neither |
| L43 | no pose leaf of a rail-mirrored navigating actor rides covered | P15 P6 | principle | `GeoNavComponent.NavigateRoutine` closed-form; shipping it can only make the icon STEP | neither |
| L44 | a window the GAME answered itself is not a lost race | P7 P5 | incident | host picker click-dead; clients' copy auto-dismissed ~0.2 s after opening | neither |
| L45 | a late resolution is REPLAYED, not yanked | P5 P11 | incident | v2 dropped `IsNonInteractableWhenSelected` → a hang instead of a replay | neither |
| L46 | every peer shows the queued windows in the HOST's order | P11 | principle | a mirror queueing everything at priority 0 | neither |
| L47 | the peer whose OWN click resolved it is not asked to click again | P5 P11 | incident | the answerer got the observer replay and needed a second click | POSITIVE CONTROL |
| L48 | every window the game pushes has a reviewed answer | P14 | principle | 1 of 9 queue kinds captured, 8 neither mirrored nor declared | POSITIVE CONTROL |
| L49 | the MODAL family: totality over `ModalType` + a non-authoritative client copy | P14 P3 | principle | 43 `ModalType`s behind one view state | POSITIVE CONTROL |
| L50 | the walk cannot go monolithic again | P16 P12 | incident | host log 2026-07-30, 275 ticks: walk p50=40 / p90=60 / max=95 ms; 10-37 fps across forced ticks | neither |
| L51 | a repaint may not take an armed augment selection, nor revert the mirror | P11 P8 | incident | native `OnNewCharacter` stamped the stale visit baseline over mirrored armour; slot locked | neither |
| L52 | click parity — an armed selection leaves its siblings clickable | P11 | incident | ported v1 fix (quarry cbb9b2c): switching part A→B swallowed | neither |
| L53 | site-exploration progress is DERIVED, not mirrored | P15 | incident | before the derivation the client saw NOTHING until the host's counter completed | POSITIVE CONTROL |
| L54 | the host repaints its own applied state; the persistent HUD is part of the screen | P11 | incident | the top-right tracker kept the OLD research text until the player left the screen and came back | POSITIVE CONTROL |
| L55 | the faction objectives list is derived; it must not ride | P15 | incident | the left-hand objectives panel desyncs; abstract element would abort at encode (L-E) | POSITIVE CONTROL |
| L56 | the haven / base / alien-base status twins stay resolved | P2 | principle | alias rows the name conventions cannot reach; failure is silent | POSITIVE CONTROL |
| L57 | a forced re-emit scope names a path the walk can produce | P7 | incident | `EventSync` shipped `"ES.EncounterRecords#<eventId>"` — the element form for an `EntityList` | neither |
| L58 | peer-local containment (hash and order-vector) | P2 P7 | incident | three permanently diverged base roots S#98 / S#99 / S#170, host alone disagreeing, never healing | neither |
| L59 | mist coverage MUST ride | P15 P1 | principle | `_mistData` is a per-frame GPU accumulator — not re-derivable | neither |
| L60 | the persistent HUD is no screen's to silence | P11 | principle | a per-screen `IgnoredKinds` exclusion buying fps out of an unaudited panel | neither |
| L61 | the tac-entry blob is real and its failure is loud | P1 | incident | v1 post-mortem: "493 KB snapshot deserialized empty on a real client", nothing thrown | neither |
| L62 | surface ids are banded, and the band is load-bearing | P1 P2 | incident | v1 RCA 3ff508d — tactical ids at 0xA0-0xA3 ate geoscape traffic for days | neither |
| L63 | client turn control is host-paced; end-turn is an intent | P4a P4b | incident | A1's instant-return coroutine made the client race a whole faction per frame | neither |
| L64 | mission end reaches every peer; nobody is stranded in tactical | P13 P1 | incident | A1 shipped both peers into one battle with no way out; `OpenReturnBarrier` had zero callers | neither |
| L65 | the per-soldier command seam is generic, arbitrated and contained | P4a P5 P8 | incident | v1's in-memory ownership ledger died on every reload, unnoticed for a month | neither |
| L66 | a resolved attack is the host's, verbatim | P3 P6 | principle | five silent-divergence shapes reasoned from the damage path | neither |
| L67 | an actor's life is the host's | P3 | incident | the v1 destroy regression; arm (e) added 2026-08-05 after TFTV's Umbra passed (a)-(d) | neither |
| L68 | an enemy action is the host's; a client only watches | P5 P3 | incident | per-peer-only reactions measured firing on the host and on neither client | neither |
| L69 | inventory commits as a batch; destruction resolves on a peer | P3 P4a | incident | v1 `6617846` locked the screen; `fc661b7` left the destructible mirror dead mission-wide | neither |
| L70 | a level teardown is safe before it starts and loud if it fails anyway | P1 | incident | 2026-07-31 blocker: `UIStateEditSoldier` `ExitState` after the level switch, NRE killed the coroutine | neither |
| L71 | when the load starts, every peer is behind the curtain | P13 P1 | incident | 2026-07-31: 13.0 s of interactive geoscape on every peer but the host | neither |
| L72 | a declared reason may not rest on a retired law | P14 | incident | 2026-07-31 P5 retired the tactical quarantine; two `GeoWindowCoverage` rules kept citing it | neither |
| L73 | a clock write may not zero the level clock's accrual | P6 P15 | incident | 2026-07-31/08-01, 3 instances: every client's aircraft froze and rubber-banded | neither |
| L74 | no unbudgeted graph walk; urgency never outbids local input | P16 | incident | "host smooth, both clients hitch" — `RootCrc` walking a whole root inside one frame | neither |
| L75 | the camera filter stays narrow, and stays a filter | P5 P11 | principle | shared `CameraDirector`; `AccessTools` exact-signature drift | neither |
| L76a | a dropped payload field may not be one the replay itself reads | P6 P5 | incident | 2026-07-31: `Equipment`/`TacticalItem` dropped → NRE on every mirroring peer | neither |
| L76b | a tactical model funnel the UI can click is seamed or declared local | P4a | incident | 2026-07-31: `EquipmentComponent.SetSelectedEquipment` is not an ability; the host then refused the next order | neither |
| L77 | the site/POI seam on a client: one clock and one notification | P15 P11 P3 | incident | 2026-08-04: Explore button missing until a second click; progress bar host-only. `mirrored start 36.08:16:30 … clock (747322.13:10:33)` | both |
| L78 | *(prose-only — no executable arm; see L104)* | P5 | unclear | cited in `TacticalCommandSync`; forced `AnyAIEvaluationAbilityExecuting` TRUE for mirrors | neither |
| L79 | the bottom bar repaints on the screen Exit+Enter is forbidden on | P11 | incident | two peers on one sniper: one fired, the other's AP and ability availability did not move | premise-changed |
| L80 | the order that crosses is the order the player gave | P5 P2 | incident | 2026-08-04, build 816128 B: one JetJump emitted 4× across two turns; soldier flew back a round | neither |
| L81 | a peer re-runs its own vision at the settle; a mirrored stat write fires the native event | P11 P3 | incident | invisible enemy + missing health bar; `0c54378` shipped the repair with no arm | both |
| L82 | peer autonomy over the window queue | P13 | principle | the queue drains only on a click; unbounded list is O(n²) + save payload | neither |
| L83 | the post-mission screens exist on every peer | P11 P1 | incident | user report 2026-08-01 items 3a+3b — outcome modal and resupply vanished from every client | neither |
| L84 | nobody is kicked, and nobody waits for anybody | P13 | principle | developer mandate 2026-08-05 + three quorums found in `Multiplayer.Network` (ready gate, LOADED barrier, straggler kick) | both |
| L91 | no peer may be left unable to act | P13 | incident | 2026-08-04 live 3-instance: same travel intent accepted 3× while every resume was vetoed | both |
| L92 | the geoscape modules are found by handle, not by scene scan | P11 | incident | 2026-08-04: activity strip missing on one client until it walked into Research and back | both |
| L93 | the window queue is ordered by the host; a queued window survives a battle | P11 P1 | incident | 2026-08-04, DLL 843264 B: host showed an event, both clients the resupply screen — same 3 windows, opposite order | neither |
| L94 | nobody plays until everybody is in; nobody waits on a peer who is never coming | P13 P1 | incident | 2026-08-04: "whoever finishes loading FIRST … can already act"; `OpenReturnBarrier` had no caller | premise-changed |
| L95 | the squad bar is reactive, and making it so moves no state stack | P11 | incident | 2026-08-04: portrait AP kept the pre-order numbers on every peer that did not click | premise-changed |
| L96 | a settle re-tests vision in BOTH directions; a drop list may not swallow an order | P11 P3 | incident | 2026-08-04, build 843264 B: bandit in fog for rounds; Dash `is DECLARED LOCAL` at 22:23:17.758 | neither |
| L97 | the aim POSE crosses, and nothing else about aiming does | P5 P15 | principle | developer decision 2026-08-04 + `FireWeaponAtTargetCrt:1645` skip = a built-in desync on every shot | premise-changed |
| L98 | a stale value may not win over a fresher one, in the model or on the screen | P7 P11 | incident | 2026-08-04, build 842240 B: 94 host settles, 0 applied, 15305 identical NRE traces | neither |
| L99 | the Kaos marketplace is a shop, not a host privilege | P4a P5 P12 | incident | 2026-08-04 live: buying does nothing on a client; the offer list was never replicated | premise-changed |
| L100 | the rail may not idle, and the clock may not arrive stale | P16 P7 | incident | 2026-08-04 23:22, DLL 842240 B, loopback: ~0.5 s lag; intent 22 ms, broadcast 16-24 ms, apply→apply 112 ms | neither |
| L101 | the reward page ships every row the game draws, as addresses | P2 P11 | incident | 2026-08-04, DLL 842240 B: clients saw resources only, the host also saw diplomatic changes | neither |
| L102 | a contextual offer that was taken is CLOSED, not re-derived | P11 P15 | incident | 2026-08-05 00:17, DLL 875520 B: Explore stays lit over the spinner on every other peer | premise-changed |
| L103 | the native tactical entry is an experiment, and one that cannot be turned off is not | P14 P6 | principle | unproven alternative to the save transfer; the RNG bracket must give the stream back | neither |
| L104 | a shared action starts at one moment; a turn does not end over an unplayed order | P5 P11 | incident | 2026-08-05 00:22-00:47: host finished a shot ahead of both watchers; 3 "camera hint suppressed" lines | neither |
| L105 | a stale stat snapshot may never win over a fresher value | P7 | incident | 2026-08-05 host log: settle #231 ap=2,6 wp=8 → damage → #233 wp=10; client showed +2/-2/+2 | neither |
| L106 | a mirrored modal must rebuild to a real data object on the peer | P2 P1 | incident | the mission brief and the soldier join sat declared, reviewed and un-shipped for months, L49 green | neither |
| L107 | a raise that arrives before its entity ends up SHOWN, not dropped | P2 P7 | incident | the soldier-join reward window the peer never saw, logged as a correct refusal | neither |
| L108 | two Phoenix Point builds must not reach one campaign | P10 | principle | `MultiplayerUI.CoopGuardBlocks` returned false unconditionally at two pre-peer call sites | neither |
| L109 | a queued non-modal window is entered, and does not wedge its queue | P13 P11 | principle | `UIStateAssetDeployment` outside 0xB7; its `ExitState` is the only way out of the queue slot | neither |
| L110 | no spend on the resupply screen escapes the rail; the seam sits where every caller passes | P4a P3 | principle | `ReplenishAll:288` calls `SingleItemReload:234` below the seam — same shape as the `RepairItem` leak | premise-changed |
| L111 | a commit by any peer consumes the shared offer for all of them | P4a P5 | incident | user: "people sit in the trade screen, one of them trades, and nothing changes for the other" | premise-changed |
| L112 | two items of the same def in one container resolve to distinct items | P2 | principle | A7's `(actorKey, kind, defGuid)` address; a full and a spent clip share a def | neither |
| L113 | no identity question on the rail is asked with `==` | P2 | incident | found by hand 2026-08-05 (marketplace vehicle pick, tactical inventory address); `BuildBattleKeys` took the Release harness down | neither |
| L114 | two Multiplayer mod versions must not share a campaign | P10 | principle | sibling of L108; the version rode the join as one anonymous "Mod version differs" line | neither |
| L115 | state that says "finished" beats a local animation still playing | P15 P11 | incident | 2026-08-05, 3 instances: host finishes the POI, clients still draw it unexplored; never self-corrected | premise-changed |
| L116 | a repaint rebuilds every panel the open container view shows | P11 | incident | 2026-08-05: item moved between two adjacent soldiers appears on one side only; personnel bar flickering | premise-changed |
| L117 | a restored window whose subject has resolved is not restored | P11 | incident | 2026-08-05: the played mission's START-MISSION window came back with the history | premise-changed |
| L118 | a client's loading bar shows the host's real progress | P11 P13 | incident | 2026-08-05: "on the clients there is an EMPTY loading bar"; P1 of 3 phases had nothing | premise-changed |
| L119 | the ready count is a LABEL, and it must stay one | P13 | principle | developer framing: "ONLY a visual indicator … doesn't oblige anything" | premise-changed |
| L120 | when somebody goes, everyone is told, and told the truth | P13 P1 | principle | developer ask 2026-08-05; `b48756e` made "gone" and "left" two different facts | premise-changed |
| L121 | a contained spawn is inert to the GAME's own enumerator | P3 | incident | 2026-08-05 live 3-instance: FINISH hung forever; `TacticalActor.OnExitPlay` NRE broke the level coroutine | premise-changed |
| L122 | one session entry into a tactical level, one load per peer | P13 P1 | incident | 2026-08-05 live: one 995,978 B blob, then a second ~996 KB transfer reloaded only the clients | premise-changed |
| L123 | an actor the host is not animating is still corrected; a refused order says so | P7 P5 | incident | 2026-08-05 live: cloaked enemy diverged; 5 winds-and-cancels in 45 s with no on-screen reason | premise-changed |
| L124 | two windows caused by two rail messages open in the same order on every peer | P1 P11 | incident | 2026-08-05: research completion + geoscape event 384 ms apart, shown in the OPPOSITE order | neither |
| L125 | a patch we declare is a patch that BINDS | P14 | incident | 2026-08-05: RailCheck green, mod DEAD in the player — `PatchAll failed … <ReturnFire>d__321::MoveNext()` | neither |
| L126 | a transpiler of ours substitutes a computation; it never writes one down | P6 P3 | incident | 2026-08-05 `b3d9269` (reverted): forced `stepOutNeeded` into every mirror; input went dead, harness green | neither |
| L127 | a confirm either activates or says why; nothing we add eats the click | P1 P11 | incident | 2026-08-05: Overwatch confirmed for 32 s on a client, no activation line ever logged | premise-changed |
| L128 | a baseline is a promise about ONE level | P6 P1 | incident | 2026-08-05: site COMPLETED on the host, still an active quest site on the clients; f15969 BASELINE swallowed it | premise-changed |
| L129 | de-blockering a popup must not delete it | P13 P11 | principle | L91's cure also removes `TryShowContextHint`; premise measured on a client 2026-08-05 21:58 | premise-changed |
| L130 | a hint triggered on one peer is displayed on every peer, exactly once | P11 P5 | incident | 2026-08-05 live: Umbra panel on ONE client; elite/gang panel on a DIFFERENT single client | premise-changed |
| L131 | every peer ends with the same status set, and the same passenger roster | P3 P7 | principle | field-class audit: statuses rode nothing; `VehicleComponent.Passengers` was a replay artefact | premise-changed |
| L132 | an order may only name a target the host itself offers | P5 P3 | incident | 2026-08-06: every client overwatch refused for four days; host settle byte-identical to seq=20 | premise-changed |
| L133 | a mirrored ability always reaches a terminal state, and it is the host's | P7 P5 | incident | the 2026-08-01 bash NRE — coroutine chain broken, actor never left `ExecutingAbilities`, soldier bricked | premise-changed |
| L134 | the new-campaign bootstrap's one shot is spent by an outcome, never an evaluation | P1 | incident | 2026-08-06, 3 instances: campaign created then nothing forever; "autosave capture failed" | premise-changed |
| L135 | one producer per mirrored window | P1 P11 | incident | 2026-08-06: three TFTV intro popups + cutscene answered on the host, then again on every client | premise-changed |
| L136 | a peer the session knows is a peer the transport will broadcast to | P13 P1 | incident | open beta 2026-08-06: real remote player on "Connecting…" ~75 s, no exception anywhere | both |
| L137 | "this soldier's turn is over" is the host's answer, on every peer | P3 P7 | incident | the 2026-08-06 overwatch desync (L132) left a permanent one-sided `HasEndedTurn` | premise-changed |
| L138 | a client-writable covered leaf has an intent seam, and what arrives is re-derived | P4a P11 | principle | coverage-gap-iv audit: `CharacterIdentity` 15/15 covered, `grep -ri customiz src/` = one hit | premise-changed |
| L139 | a mirrored order lets go of the local UI that was holding that soldier | P11 P5 | incident | 2026-08-06: 797 NREs then a native `Crash!!!`; plus the 10-second input stall | premise-changed |
| L140 | a mirrored target is resolved, never approximated | P2 P5 | incident | 2026-08-06 crash: `PositionToApply = (-806.8, 66.4, -615.6)` on a ±20 map; NaN tail four times | premise-changed |
| L141 | a resolved mission-start encounter leaves every live peer on the squad screen | P5 P11 | incident | 2026-08-06 two peers: the host that LOST the event race went straight into the battle | premise-changed |
| L142 | after a reveal, no input-set override left by the loading path is still held | P1 P11 | incident | 2026-08-06: full fps, zero exceptions, uGUI clicks — everything else dead. Repair fired once at 21:17:23.187, never again | premise-changed |
| L143 | the curtain-arm broadcast precedes the host's own load on the wire | P13 P1 P12 | incident | 2026-08-06: host curtained 21:16:58.750, client 21:17:10.126 — 11.4 s of one screen loading alone | premise-changed |
| L144 | a peer "headed for" the squad screen has nothing of its own in front of the queue | P5 P11 | incident | 2026-08-06 second session, DLL 998912 B: the host never entered `UIStateRosterDeployment`; client took 9 frames | premise-changed |
| L145 | a peer keeps control of the actors it is not commanding | P13 P5 | incident | 22:22:30 build: `released this peer's UI from Soldier_3 … it was held in UIStateWaiting` | premise-changed |
| L146 | an actor executing an order takes no command from another peer | P5 P7 | incident | report: "the character freezes in place … and then it teleports"; reject reached the log, never the screen | premise-changed |
| L147 | a visual a status owns is torn down on the RECEIVING peer | P11 P3 | incident | 2026-08-06: overwatch cone hangs on the client for the rest of the battle; 3 `GetWeapon()` NREs | premise-changed |
| L148 | a mirrored identity change repaints the open squad/customization screen | P11 | incident | 2026-08-06: rename and recolour needed a full screen exit and re-entry; `no per-kind mapping for CharacterIdentity` | premise-changed |
| L149 | both helmet toggles are the same argument to the same funnel | P11 P5 | principle | the ASK read literally is wrong — neither toggle is replicated state; `CharacterIdentity` has no helmet field | premise-changed |
| L150 | no sequence of concurrent skill confirmations drives the balance below zero | P13 P7 | incident | 2026-08-06 exploit, verbatim: both peers confirmed, BOTH skills learned, points went NEGATIVE | premise-changed |
| L151 | a peer waiting on another peer's load sees that load advance | P11 P13 | incident | 2026-08-06: first `RosterProgress SEND []` 14.2 s after the boundary, and empty | premise-changed |
| L152 | a chat line delivered to a peer appears exactly once | P7 | incident | 2026-08-06: "instead of one message he received TWO … it happened only ONCE" | premise-changed |
| L153 | the host↔client clock phase error has a seam that can read it, and that seam writes nothing | P16 P7 | incident | 2026-08-04 23:22 three-instance session: client-derived aircraft/exploration trail the host, and all three peer logs carry not one `TimeAnchor` line | both |

- inline (private method in `Program.cs`, no file): L1–L25, L27–L76b, L81, L82, L83
- files (`L<n>_<Name>.cs`): L26, L77, L79, L80, L84, L91–L153

## Unassigned / retired numbers

- L78 — prose-only, no executable arm ever. Cited `src/Tactical/TacticalCommandSync.cs:2371/:2434/:3044`, and by L104 + L127. L104's header states it.
- L26 (old) — record-derived event backlog, deleted 2026-07-30 with the engine it asserted; number re-used by `L26_PauseAndOneShot.cs`.
- L85–L90 — never issued. No file, no method, no citation anywhere in the repo.
- inline laws have no `L<n>_<Name>.cs`; grep `Program.cs` for the id string.

## Attention

- rows in table: 147. numbers issued: 152 (L85–L90 never issued; old L26 retired).
- origin: incident 93 | principle 52 | unclear 2 (L18, L78).
- guard: premise-changed only 43 | POSITIVE CONTROL only 7 | both 6 | neither 91.
- guard = POSITIVE CONTROL only: L47 L48 L49 L53 L54 L55 L56 — all inline.
- guard = both: L77 L81 L84 L91 L92 L136 — only L81 inline.
- guard = neither (91): L1–L25, L27–L46, L50, L51, L52, L57–L76b, L78, L80, L82, L83, L93, L96, L98, L100, L101, L103–L109, L112, L113, L114, L124, L125, L126.
- CAVEAT on `neither`: many carry an equivalent arm under a different name — `NON-VACUITY:` (L50 L52 L61), "the law's own PREMISE, executed" (L59), "anti-vacuity" (L55), "non-vacuous in both directions" (L9 L22 L49). `neither` = the two canonical tokens are absent, NOT "the law can silently pass while checking nothing".
- laws with neither token AND no differently-named equivalent visible in the header: the early codec block L1–L20.
- P orphans: none. No law maps to `P?`.
- P with zero laws: P9 (journal).
- P with no law scoped to it alone: P4c (defended jointly with P11).
- P13 is PROPOSED: not in `ARCHITECTURE.md`; stated only inside the corpus as a developer mandate quoted by L84 + L91; 18 laws defend it. Recommend writing it into `ARCHITECTURE.md` as a numbered principle.
- P12, P14, P15, P16 asserted by `ARCHITECTURE.md` but never numbered there → no source cites them by id today.
- `law 1` carries ≥3 readings in the corpus: (a) the two primitives Intent+Delta — `src/Rail/IntentRail.cs:11`; (b) join is a save transfer, not a delta — `src/Rail/DiffEngine.cs:33`, `ARCHITECTURE.md` P1 line; (c) never a silent swallow — `src/Rail/RailMeta.cs:1704/:1742`, `src/Rail/GenericApplier.cs:408`, `src/Rail/OpenUiRepaint.cs:331`. P1 states all three; not resolved to one.
- `law 5` and `law 10` are never defined in `ARCHITECTURE.md` — used only from `src/` (34 and 26 citations). P5 and P10 reconstructed from usage.
- HARNESS laws written in the principle spelling in `src/` prose (unfixed, outside this catalog's edit scope): `law 12` → L12 (`src/Rail/TradeSync.cs:32`, `src/Tactical/TacticalInventorySync.cs:762`); `law 19` → L19 (`src/Tactical/TacticalCommandSync.cs:1220`, `src/Tactical/TacticalInventorySync.cs:703/:902`); `law 58` → L58 (`src/Rail/DiffEngine.cs:347/:1077`, `src/Rail/RailMeta.cs:1967`); `law 91` → L91 (`src/Rail/GeoModalMirror.cs:34/:549`, `src/Rail/GeoWindowCoverage.cs:187`, + L99/L107/L109/L110/L111 headers).
- `ARCHITECTURE.md` harness citations verified correct, no conflation with old "law 11": L17 (:82, duplicate root key), L14 (:139, twin coercions), L11 (:272, LocalizedTextBind static belt), L1–L13 (Verification section).
- laws that exist because an earlier law was GREEN through the bug it named: L102 vs L92 | L130 vs L129 | L135 vs L49+L117 | L144 vs L141 | L134 vs L124 | L138 vs L36 | L96 vs L81 | L106+L107 vs L49 | L109 vs L48 | L137 vs L131.
