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
  - laws: L158 (written 2026-08-07, the same day the hole was ruled real — see Attention). Not "jointly defended with P11": P11 laws assert a repaint is REACHED, which a seam that also writes model state passes.
- **P5 — TACTICAL SCOPE IS SHARED AND CONCURRENT.** Six peers command six soldiers at once. First-to-act-wins, no ownership table, no ledger a reload can lose. Which peer plays a mission = tactical scope, not a permission.
  - laws: L27 L44 L45 L47 L65 L68 L75 L76a L78 L80 L97 L99 L104 L111 L123 L130 L132 L139 L140 L141 L144 L145 L146 L149 L165 L85
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
  - laws: L18 L21 L26 L38 L45 L46 L47 L51 L52 L54 L60 L77 L79 L81 L83 L92 L93 L95 L96 L98 L101 L102 L104 L115 L116 L117 L118 L124 L127 L129 L130 L135 L138 L139 L141 L142 L144 L147 L148 L149 L151 L156 L163 L164 L165 L86
- **P12 — UNIVERSAL-FIRST.** Unnumbered in `ARCHITECTURE.md`, asserted throughout. ONE generic mechanism per layer; the per-subsystem copy is the "macaroni factory" the mandate forbids. One value rail, one intent engine, one nonce allocator, one dedup, one repaint primitive.
  - laws: L32 L50 L99 L143 L155
- **P13 — NO QUORUMS, NOBODY KICKED. PROPOSED — not in `ARCHITECTURE.md`.** Developer mandate, stated only in the corpus (quoted by L84, L91): at any moment ANY player must be able to play everything; with 49 of 50 AFK the one active player still plays a whole campaign. Nobody is kicked. A host-side decision about peer P may read P's own intent + shared game state and nothing else. A wait on something that ends BY ITSELF is allowed; a wait on a PERSON is not.
  - laws: L26 L64 L71 L82 L84 L91 L94 L109 L118 L119 L120 L122 L129 L136 L143 L145 L150 L151 L155 L85 L86
- **P14 — DRIFT IS THE GATE, AND A LAW ASSERTS AN OUTCOME.** `docs/rail-baseline.txt` + `docs/rail-contract.txt` = committed snapshots; ANY drift is harness-RED, so a field moving Excluded↔covered is a reviewable diff, never a side effect. A law asserts the executed OUTCOME, not the presence of a call, and carries its own non-vacuity.
  - laws: L6 L9 L22 L23 L33 L48 L49 L72 L103 L125 L163
- **P15 — DERIVED, NOT MIRRORED.** Unnumbered in `ARCHITECTURE.md`, asserted in the coverage rules. State a peer can recompute closed-form from already-mirrored inputs is RE-DERIVED locally, never shipped: mirror the ORDER, derive the pose. Inverse holds — what cannot be re-derived (mist) MUST ride.
  - laws: L43 L53 L55 L59 L73 L77 L102 L115 L154
- **P16 — FRAME BUDGET.** Unnumbered in `ARCHITECTURE.md`, asserted by the sliced walk. No unbudgeted graph walk on either peer; the rail never spends a frame the player is using; urgency never outbids local input. Also may not IDLE — a sim fact waits no longer than it must.
  - laws: L50 L74 L100 L153 L154 L156

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
| L48 | every window the game pushes has a reviewed answer | P14 | principle | 1 of 9 queue kinds captured, 8 neither mirrored nor declared | neither |
| L49 | the MODAL family: totality over `ModalType` + a non-authoritative client copy | P14 P3 | principle | 43 `ModalType`s behind one view state | POSITIVE CONTROL |
| L50 | the walk cannot go monolithic again | P16 P12 | incident | host log 2026-07-30, 275 ticks: walk p50=40 / p90=60 / max=95 ms; 10-37 fps across forced ticks | neither |
| L51 | a repaint may not take an armed augment selection, nor revert the mirror | P11 P8 | incident | native `OnNewCharacter` stamped the stale visit baseline over mirrored armour; slot locked | neither |
| L52 | click parity — an armed selection leaves its siblings clickable | P11 | incident | ported v1 fix (quarry cbb9b2c): switching part A→B swallowed | neither |
| L53 | site-exploration progress is DERIVED, not mirrored | P15 | incident | before the derivation the client saw NOTHING until the host's counter completed | POSITIVE CONTROL |
| L54 | the host repaints its own applied state; the persistent HUD is part of the screen | P11 | incident | the top-right tracker kept the OLD research text until the player left the screen and came back | POSITIVE CONTROL |
| L55 | the faction objectives list is derived; it must not ride | P15 | principle | the left-hand objectives panel desyncs; abstract element would abort at encode (L-E) — no date, log or measurement | POSITIVE CONTROL |
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
| L85 | a host stream that RESTARTS is applied, not swallowed: `SurfaceSeq.ShouldApply`/`Mark` accept and REWIND on seq 1 against a cursor already past 1, every other backwards seq is still dropped, and `TacticalTurnSync.HandleInbound` names the restart out loud — with both ends of the leave carry path (`OnLocalLeaveBattle`→`HostBroadcastLeave`, inbound→`ApplyLeave`) still wired | P13 P5 | live bug | 2026-08-07 3-peer session: the host pressed RETURN TO GEOSCAPE and parked on "Waiting for players…" for 18 minutes while the ally's mission-results table froze. 0x80 is per-BATTLE and the host's `OpLeave` is by construction sent AFTER the receiver has reset (it exists to reach a peer that has not left yet): client log 21:22:28.540 reset, 21:22:28.658 the trailing leave `Mark`ed the cursor back to the old battle's 12 — so battle 2 (21:35–22:01) applied ZERO 0x80 messages, no turn cursor, no mission end, no leave, and not one log line, while 0x82/0x83/0x84 flowed throughout | both |
| L86 | the peer that ANNOUNCES a load boundary holds its own screen exactly as long as the one it imposed: `CurtainHoldArmed` reads `_loadBoundaryAnnounced`, holds at the real handover row (started FALSE, pending FALSE, announced TRUE), releases with none of the three, and every death of an announced boundary — `Conclude`/`Disarm`NewCampaignBootstrap and a `HostSerializeAndSendCrt` that produced no bytes — reaches `BroadcastLoadBoundaryAbort` | P11 P13 | live bug | 2026-08-07 owner: "creating a new game makes the HOST load twice". `SaveTransferMath` claimed the windows "abut with no gap by construction: LaunchTransfer returns only after Begin() has set the session started" — false: `LaunchTransfer` ends with `timing.Start(HostSerializeAndSendCrt)` + `return true`, and `Begin()` is several yields on, past `ReadSavegameBinary`, `SendBlob` and the host's own `PrepareEntryFromBlobCrt`. `ConcludeNewCampaignBootstrap` runs immediately, so the arm collapses, the fresh geoscape is revealed and interactive, and the blob re-entry drops a second loading screen on it. L94's handover arm was GREEN throughout because it evaluated the ASSUMED row (`sessionStarted: true`) instead of the real one — that arm is repaired, not baselined | both |
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
| L154 | the departure and exploration-start seams are wired to the urgent flush, and a mid-cycle flush is still served urgently | P16 P15 | incident | 3-instance sessions: geoscape flight and exploration spinner trail the host by a steady ~0.25-0.5 s while gesture-driven windows/orders are instant | both |
| L155 | each way of joining keeps the transport it names; a pasted code never silently tries Steam | P13 P12 | incident | the owner's report: pressing "copy code" in the lobby and handing that code to the other player still brought the session up over Steam | both |
| L156 | an in-inventory reorder repaints an ALREADY-OPEN equip screen — order stays rail state, the kind is never declared irrelevant, and the reseed still reaches the widget rebuild | P11 P16 | incident | HANDOFF §5d symptom 1: two peers on the same soldier, one rearranges items inside the inventory, the other "sees NOTHING until he closes and re-opens the screen" | premise-changed |
| L157 | applying a mirrored TACTICAL inventory batch marks the open container screen on both apply paths, and the repaint it asks for never re-commits on the peer that is only watching | P1 P3 | incident | in-mission report: two peers on one soldier (and on the neighbour's panel the trade view shows), a slot↔backpack swap by one leaves the other's open inventory unchanged until it is closed and re-opened | both |
| L158 | a presentation seam never blocks, writes a `[SerializeMember]` leaf, or throws into game code — and a latency reading reaches the panel and NOTHING else: `PingTable` is unreachable from `DiffEngine` / `GenericApplier` / `SurfaceRouter` / `TimeAnchor`, carries no serialized member, and is no rail root. Seam set: `UiEventMap`, `UiNativeRepaint`, `OpenUiRepaint`, `ResearchSync`, `PingTable`, `PlayerPanel`, `PingMarkers` | P4c | principle | P4c was cited by nothing executable (`grep -rn "P4c" tools/RailCheck src` = 0 hits) while HANDOFF §5e loaded it with two new presentation features; the RTT arm pre-empts this repo's dominant failure — a diagnostic number silently becoming replicated state, the shape L153 records as "fixed ten times" while nobody could read it | both |
| L159 | the co-op player panel is FED and admits what it cannot report: the per-peer `Paused` / `TacReady` flags survive the roster round trip, `PingTable.GetPingMs` answers an unmeasured OR a stale peer with a NEGATIVE number (the panel's only "draw the em-dash" signal), and the bar meter's thresholds are the recon's <60 / <120 / <250 / else | P4c | principle | L158 asserts what a presentation seam must not DO; nothing asserted that this one is fed. Both failure modes are silent — a truncated trailing block renders every remote player permanently not-ready with no log line, and a stale SRTT renders as a plausible live number for a peer nobody has heard from (L145) | both |
| L160 | an arriving PING points and does not drive: the ping seam reaches no camera mover (`GeoscapeView.ChaseTarget`, `Hint`, …), no view-state entry (`SwitchToState`, `EnterState`, any `To*State`) and no selection writer (`SetSelectedActor`, `SelectActorAndVehicle`, `TacticalView.set_SelectedActor`) — and the hotkey is still POLLED, so it works in deployment too | P4c P11 | principle | the L97 extension HANDOFF §5e asks for. L158's seam-set arms cannot state it: moving a camera, entering a state and selecting an actor are all things the game's own UI does legitimately, so none writes a `[SerializeMember]` leaf and none is a suppressing prefix — every arm here would be green under L158 while a watcher mid-shot had his camera flown away by somebody else's key press | both |
| L161 | nobody is ever ASKED to end the round while a soldier can still act: `TacticalAutoEndTurn.CanStillAct` answers yes for an alive, on-field, undisabled, off-standby actor holding AP and no for every disqualifier, the fold is over the SHARED `TacticalFaction`, and both halves bind — a postfix on `TacticalFaction.ShouldAutoEndTurn` (the raise) and a bool prefix on `EndTurnPromptActionDef.PromptCallback` (the answer, re-checked because the box is clicked seconds later while every other peer keeps playing) | P3 P13 | live bug | 2026-08-07, 3 peers: the host was shown "everyone is out of action points, end the turn?" while his ally still had AP on two soldiers and a vehicle, pressed OK, and the ALIEN TURN STARTED — nobody pressed End Turn, the box did. The native fold (`TacticalFaction`:521) never reads AP; its whole notion of finished is `HasEndedTurn` = `HasAbilityTrait("terminal")`, the one per-actor turn bit the peers demonstrably disagree about (L137, and the settle reconciler's own warnings). NOT a quorum: a deliberate End Turn press still takes everyone | both |
| L162 | a peer's camera always comes BACK: no type in the Multiplayer assembly patches `CameraDirector.RemoveHint` / `RemoveHints` / `ForcedReset` at all — the three ways to reach `Evaluate()`, which is the only thing that can lift a chase whose `LockCameraMovement` is set — while the push filter `CameraAbilityHintGate` survives | P4c P11 | live bug | 2026-08-07: two players acting at once left one player's camera locked onto a soldier, unmovable. `PlanarCamDef`:22 says it in its own tooltip ("chasing a transform has to be manually deactivated or the camera will be locked"); `PlanarScrollCamera`:468 eats every pan, :838 refuses to stop a locked chase, :747 self-ends only a null `ChaseTransform`. L75 arm C was GREEN throughout because it asserted the pop gate BOUND — binding was the defect; that row is repaired, not baselined | both |

| L163 | a notification is reviewed on the MAP: `WindowOrder.HoldsForOpenScreen` HOLDS every request below `TransitionPriority` (the event family 0/10/15 and the resupply rank) while the local peer's current view state is a screen it opened, DRAINS on every declared map state and on a null state, and never holds a TRANSITION (int.MaxValue squad screen / outcome modal, 100 cutscene) | P14 P11 P13 | live bug | 2026-08-06 owner: "popups now YANK the host out of whatever menu he is in and BLOCK him until he dismisses them — the design is the opposite, popups ACCUMULATE and are reviewed when the player reaches the geoscape". Vanilla needs no such rule because every section screen STOPS THE CLOCK on entry (`UIModuleGeoSectionBar`:119/:135/:148/:160, `UIStateResearch`:22, `UIStateManufacturing`:51); the 2026-08-04 pause rework made resume unconditional from any peer, so another player's play button now runs the sim behind an open screen and `ProcessQueriedStateSwitch`:71 pushes on top of it. The clock used to hold the queue and nothing replaced it. L48/L49 assert WHICH windows cross, never WHEN one may take the screen | premise-changed |
| L164 | the post-mission resupply gate is ASKED AGAIN once the returning peer's own state has arrived: `SyncEngine.Tick` drives `ReplenishSync.ClientArrivalTick`, which re-asks the GAME's `GetMissingItems()` and raises through the GAME's `QueueReplenishState` under a finite named ceiling, armed off `UIStateInitial.EnterState`'s own `_params.LastMission` and released at teardown | P11 P1 | live bug | 2026-08-06 client log: not one `Queuerd state switch … UIStateReplenish` in the whole session while the same branch's custom-mission arm fired. 76980f2's `CompleteSilently` fix was NOT undone ("CLIENT stamped mission outcome" at 21:22:39.119) — what it asserted away is a RACE: a client's post-battle geoscape is the host's MID-TACTICAL save, so at `EnterState` (frame 52446) every soldier still carries the full pre-battle loadout and `GetMissingItems()` is empty; the host's `PostMissionReplenish` writes land on 0xAC at frame 52448. "All already mirrored" was true; ARRIVED was not | premise-changed |
| L165 | one peer's sighting reaches EVERY peer: the host's 0x8A relay is unconditional and reached BEFORE the display choke (IL order, in `HintMirror.HandleInbound`), a `Show` that did not register gives the name back through `Forget`, the choke still dedupes and `Reset` still releases the battle, and the only sender is still `Capture` so an unconditional relay cannot echo | P5 P11 | live bug | 2026-08-06 owner: the elite-spotted panel does not appear on every peer, "fixed several times before". The client log holds ONE `[MP][hint]` line for five battles and zero "triggered on this peer — mirrored to every peer". L130 was GREEN throughout because it executes `ShouldSend` and asserts the DEDUPE — and the dedupe WAS the bug: the same set gated send, display AND relay, so the host's own eyes (it sights first, it runs the sim) swallowed a client's 0x8A and the third peer got nothing; a delivery a peer could not show silenced that peer's own later sighting for the rest of the battle | premise-changed |
| L166 | a CONSUMED marketplace offer is purchasable on NO peer, and a changed offer list repaints the open shop without a re-enter: the buy intent carries the offer KEY and nothing positional, the host RESOLVES it over its own live list (`MarketplaceSync.ResolveOffer`, cheapest row on an ambiguous key so no price rides the wire), `HostBroadcastOffers` is the ONE funnel every host-side list change repaints from, and the "another peer took this" refusal reaches the player instead of the host console | P11 P3 P13 | live bug | 2026-08-07 owner: client bought a soldier, every other good vanished, buttons dead, only re-entering the shop recovered it, and an already-sold offer could be clicked again. Log: three intents for ONE offer id (nonces 143/144/145), then `buy: row 10 is outside the host's 4-offer shop` and `row 0 is 'I:29998034…' here, not the 'U:{3FBC2BB0…}' that was clicked`. L99 was GREEN throughout — it asserts the click is CAPTURED and the mirrored list is repainted, never that the address survives the list it was read off | premise-changed + POSITIVE CONTROL |
- inline (private method in `Program.cs`, no file): L1–L25, L27–L76b, L81, L82, L83

- files (`L<n>_<Name>.cs`): L26, L77, L79, L80, L84, L85, L86, L91–L166

## Rows vs registrations — why 155 rows and 135 registered laws

The two numbers count different things and always will. `tools/law-count.txt` counts REGISTRATIONS
(one `laws.AddRange(...)` line each); this table counts NUMBERED LAWS. The mapping is many-to-many
on the inline side. Nothing is missing and no row is invented — verified 2026-08-07.

- **files: 77 rows ↔ 77 registrations, exactly 1:1.** `L26, L77, L79, L80, L84, L91–L162` — one
  `L<n>_<Name>.cs`, one `AddRange`, one row.
- **inline: 79 rows ↔ 60 registrations.** 60 inline methods emit 78 distinct ids; the catalogue
  splits one of them in two, giving 79 rows.
  - ONE method carrying MANY ids is the whole surplus. `RoundTrip()` alone emits twelve —
    L4 L7 L8 L10 L12 L13 L14 L17 L18 L19 L20 L24. `StructuralDescendLaw` emits L29 + L30.
  - MANY methods sharing ONE id happens too, and the catalogue names the arms itself:
    L76a/L76b = `TacticalFunnelLaw` + `TacticalPayloadUseLaw` (both emit the bare string `"L76"` —
    the `a`/`b` suffix exists only here, never in the code); L69 = `InventoryAndDestructionLaw` +
    `…Part2` + `ValidateProbes`; L15 = `OwnerBackRefCodecLaw` + `OwnerBackRefLaw`;
    L28 = `RootOwnershipLaw` + `SubEntityRefArm`; L49 = `ModalCoverageLaw` + `OneProducerPerWindow`.
- **1 row is not executable at all:** L78, prose-only, no arm ever. See below.
- Arithmetic: 70 file rows + 79 inline rows + L78 = 150 rows. 70 files + 60 inline = 130
  registrations. The 20-row difference is 19 (inline ids beyond their methods) + 1 (L78).
- The `147 vs 127` figure in the earlier handoff was the same accounting one snapshot earlier
  (`files=67`, three fewer rows). The ratio, not the totals, is the fact.
- **Consequence for review:** an inline law's id is NOT a registration. Deleting `RoundTrip()`
  silently retires twelve numbered laws while `inline=` drops by one. `tools/law-integrity.ps1`
  cannot see that. Prefer a new `L<n>_<Name>.cs` file for anything new — it restores 1:1.

## Unassigned / retired numbers

- L78 — prose-only, no executable arm ever. Cited `src/Tactical/TacticalCommandSync.cs:2371/:2434/:3044`, and by L104 + L127. L104's header states it.
- L26 (old) — record-derived event backlog, deleted 2026-07-30 with the engine it asserted; number re-used by `L26_PauseAndOneShot.cs`.
- L85, L86 — ISSUED 2026-08-07 (`L85_RestartedHostStreamIsApplied.cs`, `L86_AnnouncedBoundaryHoldsItsAnnouncer.cs`). Taken from the never-issued block on purpose: five agents were minting laws in one tree that day and three successive max+1 picks collided.
- L87–L90 — never issued. No file, no method, no citation anywhere in the repo.
- inline laws have no `L<n>_<Name>.cs`; grep `Program.cs` for the id string.

## Attention

- rows in table: 150. numbers issued: 155 (L85–L90 never issued; old L26 retired). Registrations: 130 — see "Rows vs registrations".
- origin: incident 96 | principle 53 | unclear 2 (L18, L78).
- guard: premise-changed only 44 | POSITIVE CONTROL only 6 | both 9 | neither 92.
- guard = POSITIVE CONTROL only: L47 L49 L53 L54 L55 L56 — all inline.
- guard = both: L77 L81 L84 L91 L92 L136 L153 L154 L155 — only L81 inline.
- guard = neither (92): L1–L25, L27–L46, L48, L50, L51, L52, L57–L76b, L78, L80, L82, L83, L93, L96, L98, L100, L101, L103–L109, L112, L113, L114, L124, L125, L126.
- guard tokens are matched CASE-INSENSITIVELY (`tools/law-integrity.ps1:77` uses PowerShell `-notmatch`), so `Positive control:` in a header counts. This column follows that same rule — not the literal casing.
- CAVEAT on `neither`: many carry an equivalent arm under a different name — `NON-VACUITY:` (L50 L52 L61), "the law's own PREMISE, executed" (L59), "anti-vacuity" (L55), "non-vacuity, both halves" (L48), "non-vacuous in both directions" (L9 L22 L49). `neither` = the two canonical tokens are absent, NOT "the law can silently pass while checking nothing".
- laws with neither token AND no differently-named equivalent visible in the header: the early codec block L1–L20.
- P orphans: none. No law maps to `P?`.
- P with zero laws: P9 (journal).
- ~~P with no law scoped to it alone: P4c.~~ **CLOSED 2026-08-07 by `L158_PresentationSeamAltersNothing`** — the three clauses below, over the seam set below, plus a fourth arm containing the new RTT reading (`PingTable`) at birth rather than retrofitting it. Two clauses landed narrower than this entry assumed and the law's header says so: clause (b) can only see DIRECT IL writes, because this seam set drives the native model almost entirely through `MethodInfo.Invoke` / `FieldInfo.SetValue`; clause (c) is asserted at the CONTAINMENT POINTS (`UiEventMap.Fire`, `OpenUiRepaint.Repaint`, `ResearchSync.PresentFromMirror`) plus "no uncontained caller of `UiNativeRepaint.TryRepaint`", because the helpers that actually invoke native code deliberately carry no catch of their own — demanding one per leaf would have been a style rule, not P4c. The original argument, kept because it is what the law is for:
  - **DECIDED 2026-08-07: this is a REAL coverage hole, not a principle that is only jointly checkable.**
  - The two are opposite directions, not two halves of one test. P11 asserts the repaint HAPPENS (reach); P4c asserts the seam CHANGES NOTHING (harmlessness). Every P11 law in this table is a reach-law, and a presentation seam that reaches the screen AND writes model state passes all forty of them.
  - P4c is cited by NOTHING executable. `grep -rn "P4c" tools/RailCheck src` = zero hits; the only mentions in the repo are `ARCHITECTURE.md:313` and this file. L126 ("a transpiler substitutes a computation, never writes one down") is the nearest shape and it is scoped P6 P3 and covers transpilers only, not the repaint/present seams.
  - All three of P4c's clauses are statically assertable over a named seam set (`UiEventMap` arms, `UiNativeRepaint.Table` entries, `OpenUiRepaint`, `ResearchSync.PresentFromMirror`): (a) no prefix in the set returns false / suppresses native — never BLOCK; (b) no member of the set writes a `[SerializeMember]` leaf — never WRITE; (c) every entry is exception-contained — never THROW into game code. Clause (c) is already an unasserted written promise at `ARCHITECTURE.md:192` ("a registered screen that throws keeps the screen + logs once").
  - The hole is about to be loaded, which is why it is worth closing now rather than noting again: HANDOFF §5c option (C) moves death presentation onto the mirror's own local playback, and §5e classes both new features (pings, player panel) as presentation. Both are P4c by construction and neither has a law. (The ping half arrived the same day and is arm (d) of L158; the panel arrived the same day too and DID inherit the seam set — `PlayerPanel` is in it, and `PlayerPanel.Sync` is a containment point. What the seam set could not carry is whether the panel is fed at all, which is L159. The ping MARKERS — §5e's other feature, and a different thing from the RTT ping — arrived 2026-08-07 too: `PingMarkers` is in the seam set and `PingMarkers.Show` is a containment point, and what the seam set could not carry there is that an arriving ping moves no camera, enters no view state and changes no selection, which is L160.)
- P13 is PROPOSED: not in `ARCHITECTURE.md`; stated only inside the corpus as a developer mandate quoted by L84 + L91; 18 laws defend it. Recommend writing it into `ARCHITECTURE.md` as a numbered principle.
- P12, P14, P15, P16 asserted by `ARCHITECTURE.md` but never numbered there → no source cites them by id today.
- `law 1` carries ≥3 readings in the corpus: (a) the two primitives Intent+Delta — `src/Rail/IntentRail.cs:11`; (b) join is a save transfer, not a delta — `src/Rail/DiffEngine.cs:33`, `ARCHITECTURE.md` P1 line; (c) never a silent swallow — `src/Rail/RailMeta.cs:1704/:1742`, `src/Rail/GenericApplier.cs:408`, `src/Rail/OpenUiRepaint.cs:331`. P1 states all three; not resolved to one.
- `law 5` and `law 10` are never defined in `ARCHITECTURE.md` — used only from `src/` (34 and 26 citations). P5 and P10 reconstructed from usage.
- HARNESS laws written in the principle spelling in `src/` prose (unfixed, outside this catalog's edit scope): `law 12` → L12 (`src/Rail/TradeSync.cs:32`, `src/Tactical/TacticalInventorySync.cs:762`); `law 19` → L19 (`src/Tactical/TacticalCommandSync.cs:1220`, `src/Tactical/TacticalInventorySync.cs:703/:902`); `law 58` → L58 (`src/Rail/DiffEngine.cs:347/:1077`, `src/Rail/RailMeta.cs:1967`); `law 91` → L91 (`src/Rail/GeoModalMirror.cs:34/:549`, `src/Rail/GeoWindowCoverage.cs:187`, + L99/L107/L109/L110/L111 headers).
- `ARCHITECTURE.md` rename RE-VERIFIED 2026-08-07, line by line. No numbered "law N" survives anywhere in the file; no principle was renumbered. Every `L<n>` in it is a harness law and reads correctly: L17 (:83, duplicate root key), L14 (:140, twin coercions), L11 (:273, LocalizedTextBind static belt), L1–L13 + L20 (:376-424, Verification section). No conflation with old "law 11" — `:273` says "no `LocalizedTextBind` rides covered", which is L11's subject verbatim, not P11's repaint.
- AMBIGUITIES IN `ARCHITECTURE.md`, named rather than silently resolved:
  - The file legitimately uses BOTH numbering spaces over the same digits: P1–P11 as principles and L1–L14/L17/L20 as harness laws, disambiguated only by the `P`/`L` letter and the one-line note at `:9`. That collision is structural and permanent; 11 is merely the loudest case (`P11` repaint vs `L11` text-bind belt). A bare number in any future edit is unresolvable — always write the letter.
  - `:19` "**Implementation of the law:**" — unnumbered and genuinely ambiguous. By context (canonical byte-identical deltas + generalized field enumeration) it reads as P1 or P6, but the original text names neither. Left as-is; not renamed, because picking one would be an invention.
  - `:254`/`:256`/`:260`/`:271` ("Walk-time ownership law", "No static signal carries the law", "law fails OPEN") and `:279` ("Anchor-not-Now law") use "law" as ordinary prose for an unnumbered rule. Not renaming errors.
  - P5, P10, P12, P13, P14, P15, P16 appear NOWHERE in `ARCHITECTURE.md` by number — consistent with the notes below, but it means seven of sixteen principles have no prose source there.
- laws that exist because an earlier law was GREEN through the bug it named: L102 vs L92 | L130 vs L129 | L135 vs L49+L117 | L144 vs L141 | L134 vs L124 | L138 vs L36 | L96 vs L81 | L106+L107 vs L49 | L109 vs L48 | L137 vs L131 | L162 vs L75.
