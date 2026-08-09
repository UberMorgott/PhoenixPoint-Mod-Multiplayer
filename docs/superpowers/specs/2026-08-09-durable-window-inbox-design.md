# Durable per-player window inbox — approved design

## Status and scope

- Design only; no implementation decisions below authorize code changes.
- Replaces the ephemeral/no-backlog limitation in `docs/design-event-windows.md` while retaining native window rendering and host-authoritative campaign effects.
- Governing product constraints:
  - Every player may progress the campaign while every other player is AFK.
  - No ready vote, confirmation quorum, or wait for a human acknowledgement.
  - Replicated changes repaint every already-open affected UI immediately.

## Goals

- Deliver every eligible campaign window durably to every session player, including windows raised while that player is tactical, disconnected, loading, or viewing a non-Geoscape tab.
- Show inbox windows only while that player is on Geoscape.
- Preserve local reading pace without duplicating shared campaign effects.
- Make priority mission/deployment flow interrupt ordinary reading safely, then resume the suspended window unchanged.
- Keep mission offers and preparation screens consistent with live vehicle, mission, soldier, and equipment state.
- Survive reconnect and save/load without treating a transport ACK as delivery, viewing, or dismissal.

## Non-goals

- No custom history browser, inbox panel, notification centre, or replacement event renderer.
- No tactical presentation of Geoscape windows.
- No consensus, quorum, peer-ready gate, or requirement that every player reads or answers.
- No attempt to replay arbitrary historical `GeoscapeEventRecord` entries that predate this engine; only captured durable occurrences are eligible.
- No redesign of native mission, reward, choice, or deployment rules beyond authority and lifetime rules stated here.
- No implementation, wire schema, storage format, patch list, or rollout code in this document.

## Vocabulary

- **Campaign backlog** — host-owned ordered set of durable occurrences that still have at least one player entitlement or unresolved shared effect. It is authoritative campaign/session state, not native UI state.
- **Native pending** — the game's `_viewStateSwitchRequests` queue. It remains an execution detail and is never the durable source of truth.
- **Current open** — the native window presently displayed on one peer.
- **Mod inbox** — one player's durable lifecycle records over campaign-backlog occurrences: queued, open, suspended, deferred, read, dismissed, or removed.
- **Occurrence** — one captured presentation/effect instance, distinct from the reusable event definition.
- **Entitlement** — the fact that a player must retain an occurrence until that player's terminal local state or a global invalidation removes it.
- **Durable player identity** — campaign-scoped player identity, independent of connection id or roster slot, paired with a membership epoch for each join/rejoin tenure.
- **Shared choice** — one campaign-affecting answer chosen once for the occurrence; first accepted answer wins.
- **Canonical choice identity** — host-minted immutable identity bound, inside one occurrence, to one normalized captured answer/result; it is never an event id, list index, localized text, or runtime object identity.
- **Canonical reward item identity** — host-minted immutable identity bound, inside one occurrence, to one normalized captured reward item and subject; it is never inferred from event id, mutable reward order, display text, or runtime object identity.
- **Local answer** — a response in an explicitly local-only native window; its effect and lifecycle belong only to the answering player and it never enters the campaign backlog.
- **Carrier** — any live native window, native pending request, wire message, or mod record currently capable of advancing an occurrence. A carrier is not the occurrence itself.
- **DeploymentPreparing** — shared mission-preparation occurrence created by Start and inserted at the head of every then-enrolled durable player/membership epoch's inbox.
- **Deferred** — nonterminal per-player suppression after Back from deployment preparation; shared preparation continues, but the same screen cannot auto-open again until explicit local re-entry or a new qualifying priority revision.

## Window taxonomy

| Kind | Canonical identity | Owner / priority | Delivery and display | Decision and local terminal event | Global invalidation | Restore and teardown |
|---|---|---|---|---|---|---|
| Ordinary notice | full occurrence tuple; captured notice/result phase | host / ordinary | entitlement to each player in the creation membership set; Geoscape gate only | no shared decision; successful native presentation marks `Read`; native acknowledge/close then marks `Dismissed` and releases that entitlement | explicit subject unservability or explicit predecessor supersession only | queued/read survive reconnect; open becomes `Suspended(LevelTeardown)` before level loss and resumes from checkpoint; terminal teardown removes every carrier without callbacks |
| Shared-choice | full occurrence tuple plus canonical choice and reward-item identities scoped to it | host; first host-serialized valid answer wins / ordinary unless explicitly registered priority | creation-set entitlement; Geoscape gate only; every entitled player retains the locked result | answer controls may act only while `Unanswered`; no dismiss before the locked result is successfully presented; presenting it marks `Read`, native acknowledge/close marks `Dismissed` and releases only that player's entitlement | explicit subject unservability or explicit predecessor supersession; never another player's answer/read | queued/open/suspended result and lock restore exactly; teardown removes carriers without applying choice/reward/callback |
| Registered priority native interrupt | full occurrence tuple and an exact priority-registry match below | host / priority interrupt | creation-set entitlement; Geoscape gate only; may preempt only an ordinary window already open there | successful native content/result presentation marks `Read`; the registered family's native acknowledge/Cancel then marks `Dismissed`; a supported Start follows the exceptional-offer transition below | authoritative subject expiry/destruction or loss of its last valid source | queued/read restore; open becomes suspended before preemption/level teardown and restores its checkpoint; invalidation removes queued/open/suspended/read/tactical-held carriers and closes native UI without Cancel callback |
| Exceptional mission offer | full occurrence tuple plus mission and live deployment-source identities | host / priority interrupt | creation-set entitlement; Geoscape gate only | successful brief presentation marks `Read`; Cancel marks only that player `Dismissed` and releases its entitlement; first valid Start creates a successor occurrence and is the only shared decision; no generic dismiss path | mission expiry/destruction, or revalidation proving no valid source remains (including a mission uniquely bound to a departed source) | queued/read restore; open becomes suspended before preemption/level teardown and restores its checkpoint; successor commit or invalidation removes every obsolete offer carrier without `GeoMission.Cancel` |
| DeploymentPreparing | successor occurrence identity plus mission and current live-source set | host / highest deployment interrupt | new entitlement for every durable player/membership epoch enrolled when Start is serialized, including players who dismissed the predecessor offer; Geoscape gate only | edits and first valid launch are host-serialized; Back enters `Deferred` and is nonterminal; successful launch or global invalidation marks `Removed` and releases every entitlement | mission expiry/destruction/completion, successful launch, or revalidation proving no valid source remains | queued/open/suspended/deferred/tactical-held state restores from current campaign facts; source loss prunes/repaints; terminal teardown closes every carrier without Cancel/back callbacks |
| Local-only native window (including local-answer windows) | native local window/raiser identity; no durable occurrence | local player / outside inbox priority | native local delivery/display only; never mirrored or placed in campaign backlog | local native answer/acknowledge/close owns the effect and terminal lifecycle; no campaign entitlement or shared decision | closing its local owning context invalidates only that native instance | native owner restores/tears down it under existing local rules; DWI never intercepts or persists it |

- This taxonomy is explicit and exhaustive. Classification is keyed by registered `(native state type, modal type, raiser identity)`.
- Priority is granted only by the exact native registry below. Anything classified but not matched is ordinary; an unknown mirrored family is loud-unclassified under DWI-18. Neither case may be silently inferred as priority or dropped to an ephemeral fallback.
- A single-choice auto-completed event may still be a durable notice/result occurrence; native record state alone never proves local delivery.

### Exact native priority registry

| Registered family | Exact native identity and raiser | Classification seam and exclusions |
|---|---|---|
| Urgent ambush brief | `UIStateGeoModal`; modal `GeoAmbushBrief`; subject runtime type `GeoAmbushMission`; `GeoscapeView.ShowMissionBriefing(GeoMission, GeoVehicle) -> OpenModalPersistent(..., priority 0)` | Require the conjunction: state is `UIStateGeoModal`, `modalData` is `GeoAmbushMission`, and modal equals the live/patched `GeoscapeView.GetMissionBriefModal(modalData)`, captured at `ShowMissionBriefing`/`OpenModalPersistent`. Do not use `priority=0`, `IsMandatoryMission`, name/text, `AmbushMissionTag`, or `UIStateGeoModal` alone. Priority inserts at inbox head but never forces navigation out of another Geoscape tab/screen. |
| Exceptional native mission brief / preparation entry | `UIStateGeoModal`; `modalData` is a live `GeoMission`; modal is exactly the live/patched `GeoscapeView.GetMissionBriefModal(mission)`: `GeoHavenAttackBrief`, `GeoAlienBaseBrief`, `GeoScavengeBrief`, `GeoPhoenixBaseDefenseBrief`, `GeoPhoenixBaseInfestationBrief`, `AncientSiteAttackBrief`, `AncientSiteDefenceBrief`, `BehemothAttackBrief`, or `InfestedHavenBrief`; raiser `ShowMissionBriefing -> OpenModalPersistent` | Exclude the separately registered `GeoAmbushMission` arm and all explicit local-only/custom/non-`GeoMission` families. Capture raiser identity. Do not infer from semantic naming. Confirm may route through `ModalResultCallback -> LaunchMission`; priority still does not yank an unrelated open tab. |
| `DeploymentPreparing` / squad preparation | `UIStateRosterDeployment`; no modal; non-null live `GeoMission` from `state.Mission`/`_mission`; raiser `GeoscapeView.ToDeploymentState(GeoMission, IGeoCharacterContainer, bool)` reached from `LaunchMission`; native queue priority `int.MaxValue` | Require exact state type (or an assignable native subclass only after explicit registration), live mission subject, and raiser. Do not classify by `int.MaxValue` or generic deployment naming. Insert the successor at inbox head; rendering remains Geoscape-gated. |
| Asset destination forced-front answer | `UIStateAssetDeployment`; no modal; `GeoDeployAssetFactionCharacterBind`; raiser `GeoscapeView.PrepareDeployAsset`; native request default priority `0` | Require exact state and raiser, with stable asset/definition identities. This is the existing `WindowOrder.NeverHeldAnswerStates` equivalence and must not generalize to answers, purchases, priority `0`, or all asset/recruit windows. Priority insertion does not authorize forced navigation; rendering remains Geoscape-gated. |

- `InterceptionBrief`/`InterceptionOutcome`, mission-outcome modals, `UIStateGeoCutscene`, and game-over remain loud-unclassified for priority until explicitly registered. `HavenInfiltrateBrief` and `_CustomMission` remain excluded local-only/custom families. `MandatoryMission`, modal/native numeric priority, names, tags, and unknown patched mission/modal/state triples are never priority seams.

## Identity and authoritative model

- Stable occurrence identity is the normalized tuple:
  - `event identity` — stable definition/modal family identity;
  - `trigger identity` — host-minted stable identity for this raise/trigger, persisted across reconnect and save/load;
  - `subject identity` — stable referenced mission/site/vehicle/faction/character/reward subject set.
- Presentation sequence, connection id, native object identity, queue index, timestamps alone, and transport nonce are not occurrence identity.
- Repeated raises of the same event for the same subject require distinct trigger identities.
- Retransmission or reconstruction of the same trigger must resolve to the same occurrence.
- The normalized full occurrence identity is the namespace for every answer, result, reward item, persisted lock, lifecycle record, and message. No such record may be keyed by `eventId`, trigger count, index, text, current list order, or runtime object identity alone.
- At capture, the host binds each answer/result to one canonical choice identity and each reward payload member to one canonical reward item identity. Retransmission and save/load preserve those bindings byte-for-byte/logically unchanged.
- Host-owned occurrence state:
  - identity and immutable captured presentation/context identity;
  - taxonomy and priority;
  - total order key;
  - entitled durable-player/membership-epoch set;
  - shared-choice state and canonical confirmed choice/result/reward-item identities;
  - mission/deployment linkage and explicit predecessor/successor generation links;
  - global invalidation/completion tombstone.
- Peer-owned durable lifecycle state:
  - queued, open, suspended, deferred, read, dismissed;
  - local presentation checkpoint needed to restore an interrupted native window unchanged;
  - local Cancel for an exceptional mission offer;
  - monotonically increasing lifecycle revision under the player's current membership/session epoch.
- The host replicates peer-owned lifecycle records durably for reconnect/save continuity but does not choose them on the peer's behalf.
- The host serializes and validates each `(durable player, membership epoch, occurrence)` transition. Duplicate revisions are idempotent; stale epochs/revisions and illegal regressions are rejected; `Dismissed`/`Removed` never regress; a global tombstone always wins over any peer update.
- A transport ACK proves only receipt of bytes. It never means queued, opened, read, answered, or dismissed.

## State machines

### Host occurrence

- `Created -> Active` after identity, context, taxonomy, entitlement, and order are durably recorded.
- `Active -> ChoiceLocked` on the first valid shared-choice answer.
- `Active|ChoiceLocked -> Transitioning` before a sole carrier is consumed to create a successor occurrence or campaign effect.
- `Transitioning -> Active|ChoiceLocked` only after the successor/effect and required entitlements are durably committed.
- `Active|ChoiceLocked|Transitioning -> Invalidated` when authoritative revalidation proves the occurrence unservable.
- `Active|ChoiceLocked|Transitioning -> Completed` when an explicit successor link or underlying mission/event completion makes this occurrence obsolete.
- `Invalidated` and `Completed` are terminal tombstones. A tombstone may be compacted only after no campaign save, durable journal segment, peer snapshot/cursor, or wire replay source can still name the occurrence. If that absence cannot be proven, the terminal identity remains in campaign save state without a time-based TTL.

### Per-player lifecycle

- `Absent -> Queued` when entitlement is durably granted.
- `Queued -> Open` only when the Geoscape display gate admits it and native presentation succeeds.
- `Open -> Suspended(reason=PriorityPreemption)` when a higher-priority occurrence preempts it.
- `Open -> Suspended(reason=LevelTeardown)` after its checkpoint is durably committed and before Geoscape native teardown for tactical/load/level loss.
- `Suspended -> Open` only after all higher-priority work is terminal or, for `LevelTeardown`, after this peer's started-Geoscape reconciliation; restore the same occurrence and unchanged local checkpoint.
- `Open -> Read` only at the exact presentation event specified by the taxonomy row.
- `Read|Open -> Dismissed` only at the exact local event allowed by the taxonomy row.
- `Open -> Deferred` on Back from `DeploymentPreparing`; shared preparation remains active and the display gate must not reopen it in the same tick.
- `Deferred -> Queued` only on explicit local re-entry to that preparation or a newer host priority revision that materially changes its live preparation facts; reconnect alone does not clear deferral.
- `Queued|Open|Suspended|Deferred|Read -> Removed` on host global invalidation, successful launch, or explicit obsolete completion.
- `Dismissed` and `Removed` are terminal for that occurrence/player pair.
- Native suspension/terminal teardown never invokes choice, dismiss, Cancel/back, completion, reward, or launch callbacks. The durable checkpoint/terminal transition commits before the carrier closes.
- Reconnect and save/load restore these states idempotently; they do not infer a terminal state from absence of a native window.

### Shared choice

- `Unanswered -> Locked(canonicalChoiceIdentity, canonicalResultIdentity, canonicalRewardItemIdentities, winnerPlayer)` by one host-serialized compare-and-set keyed by the full occurrence identity.
- Every answer/result/reward message and persisted lock carries that full occurrence identity plus its occurrence-scoped canonical identities.
- Later answers are rejected as stale and receive the locked result.
- The accepted answer's campaign effect/reward applies exactly once.
- Locking does not dismiss anybody's entitlement:
  - winner sees the confirmed result and retains it until local result presentation/read and native acknowledgement/dismissal;
  - losers' controls lock immediately, repaint to the confirmed answer/result, and remain until each local result presentation/read and acknowledgement/dismissal.
- Confirmed identities are immutable captured data, never reconstructed from event id, trigger count, mutable list position, text, or current definition ordering.

### Exceptional mission offer

- `OfferActive -> LocallyDismissed(player)` on Cancel; mission and other peers are untouched.
- `OfferActive -> DeploymentPreparing` on the first valid Start from any peer.
- Start atomically creates one new shared `DeploymentPreparing` occurrence at the head of every then-enrolled durable player/membership epoch's inbox before consuming any offer carrier.
- The successor grants a fresh entitlement to every epoch enrolled when Start is host-serialized, including a player whose predecessor offer is already locally terminal; a later enrollment receives none, and no offer lifecycle state is inherited by the new occurrence.
- Concurrent Starts deduplicate by full offer occurrence and mission identity.
- Back from deployment preparation commits that player's `Deferred` state and returns to the previous screen without cancelling the shared mission; the display gate cannot reopen the preparation in the same tick.

### Deployment preparation

- `Preparing -> Preparing` for valid soldier/equipment edits by any peer; host serializes campaign state, all already-open preparation UIs repaint immediately.
- `Preparing -> Launching` on the first valid launch from any peer.
- `Launching -> Launched` exactly once; duplicate launch commands are refused or idempotently report the existing transition.
- Successful launch is a global terminal transition: durably commit the `Launched` tombstone, atomically remove every offer/preparation carrier and every queued/open/suspended/deferred/tactical-held lifecycle, safely close open native screens without Cancel/back/callback, then announce the load boundary and enter tactical.
- No peer readiness is read. No human action by another peer can block launch.
- Vehicle/source departure while `Preparing|Launching` triggers source revalidation, not unconditional mission invalidation: remove the departed source and its occupants from all live source sets and immediately repaint every open preparation UI. Globally invalidate and remove the offer/preparation only when no valid source remains, or when authoritative mission linkage proves the mission was uniquely bound to that departed source.

## Ordering, priority, and preemption

- Host assigns a deterministic total order at occurrence creation. Reconnect, retransmit, and save/load preserve it.
- Normal selection is the earliest eligible queued occurrence by `(priority class, host order key)`.
- Only exact matches in the native priority registry are priority interrupts; `DeploymentPreparing` is the highest deployment interrupt. They sit ahead of ordinary windows in each entitled player's inbox, but never override the Geoscape display gate or force navigation from another tab/screen.
- On Geoscape, a priority interrupt may preempt an ordinary current-open window:
  - record the ordinary occurrence as `Suspended` before replacing its native carrier;
  - do not run its choice, dismissal, callback, or completion path;
  - show the interrupt;
  - restore the suspended occurrence after priority work ends, with the same occurrence identity, content/result phase, selection, and local read checkpoint.
- Preemption is not allowed to reorder two ordinary windows or to manufacture a second occurrence.
- A globally invalidated suspended occurrence is removed rather than resumed.
- Host compaction may discard an occurrence only after every entitlement is locally terminal or the host has issued a terminal global tombstone, and only after the tombstone-retention proof above succeeds.

## Display gate

- An inbox occurrence may enter native pending/current-open state only when all are true:
  - this peer has a live, fully started Geoscape level;
  - the active view is Geoscape, not tactical, loading, lobby, or another level;
  - no higher-priority occurrence must run first;
  - the occurrence is still servable and not terminal;
  - no equivalent native carrier for the same occurrence is already pending/open.
- Non-Geoscape Geoscape tabs do not consume the inbox. Occurrences continue accruing while personnel, research, manufacturing, diplomacy, base, aircraft, or other non-Geoscape views are open.
- Ordinary windows do not forcibly close a non-Geoscape tab; they wait for return to the Geoscape display surface.
- Registered priority ambush/deployment interrupts may interrupt an ordinary open event window on Geoscape, not arbitrary non-Geoscape work.
- Tactical accrual is unconditional; tactical never renders or dismisses a Geoscape occurrence.

## Reconnect, tactical, and save/load

- At occurrence creation, the entitlement set is the durable player identities and membership epochs already enrolled in the campaign session, including disconnected/loading/tactical peers. A later connection id or roster slot never creates a new identity.
- A newly joined durable player/new membership epoch receives no occurrence created before successful enrollment, regardless of whether that occurrence is active, unread, a locked shared result, a mission offer, or `DeploymentPreparing`. Entitlement begins exclusively with occurrences created after enrollment; no join path scans or grants the pre-existing campaign backlog.
- Enrollment and occurrence creation share one host-serialized transaction order. Enrollment becomes effective only when the host durably commits the new `(durable player identity, membership epoch)` to the session membership set. An occurrence transaction snapshots that committed set when it durably creates the occurrence and entitlements. Therefore create-before-enroll excludes the new epoch, while enroll-before-create includes it; retries preserve the committed order and identities. This boundary requires no quorum or human acknowledgement.
- Reconnect/resume of the same durable player identity and same membership epoch is not enrollment or late join. It restores that epoch's existing queued/open/suspended/deferred/read lifecycle and inbox entitlements idempotently, without creating a new epoch or granting historical occurrences that were never entitled to it.
- Authoritative campaign removal ends a membership epoch without human acknowledgement: the host marks that epoch's nonterminal entitlements `Removed(reason=MembershipEnded)` and excludes it from compaction blockers. Disconnect, AFK, pause, load, tactical play, or ordinary reconnect never constitute permanent removal.
- Reconnect requests an inbox snapshot/delta keyed by stable player/membership epoch and occurrence identities; delivery is idempotent and lifecycle revisions reject stale prior-session updates.
- A reconnecting peer resumes its exact queued/open/suspended/deferred/read state. If native UI cannot be restored immediately, the durable state remains and is served when the display gate opens.
- Tactical entry commits `Open -> Suspended(reason=LevelTeardown)` and its exact checkpoint before native Geoscape teardown. Native teardown invokes no choice/dismiss/Cancel/back/completion/reward/launch callback.
- Tactical return waits only for this peer's own level/load readiness, then reconciles host backlog plus peer lifecycle. Only after a fully started Geoscape exists may `Suspended(reason=LevelTeardown) -> Open` and native restoration occur. This is a load barrier, never a quorum.
- Save data persists active occurrences, terminal tombstones needed for dedupe, total order, shared locks, mission/preparation linkage, player membership epochs/entitlements, monotonic lifecycle revisions, and peer lifecycle/checkpoints.
- Load reconstruction resolves stable subject/choice/result/reward-item identities against the loaded campaign. Failure to resolve is explicit and non-destructive; it never silently marks delivered or invents a new occurrence.

## Mission invalidation and teardown

- Vehicle departure from the mission site, loss of a deployment container, mission cancellation/expiry/destruction by authoritative game rules, or mission completion triggers host revalidation.
- Departure of a linked deployment container atomically:
  - removes only that container and its occupants from live deployment sources;
  - prunes invalid local enrolment/selection and immediately repaints every open preparation UI from remaining sources;
  - preserves queued/open/suspended/deferred/tactical-held offer/preparation occurrences while any valid source remains;
  - emits a global terminal tombstone and removes every such occurrence/carrier only when the last valid source disappears or authoritative mission linkage proves the mission was uniquely bound to the departed container;
  - force-closes terminally invalidated open UI without native Cancel/back callbacks, with the tombstone committed before or with teardown so late carriers cannot reopen it.
- Mission completion cleanup is generation-linked and ordered:
  - first mark only explicit predecessor offer/preparation/outcome occurrences terminal via immutable `supersededBy` links;
  - atomically remove every queued/open/suspended/deferred/tactical-held and native-pending/open carrier of those predecessors;
  - then create any completion outcome with a new trigger/order and generation, outside the predecessor removal set.
- Cleanup by mission identity alone is forbidden: it cannot remove the new completion outcome or a lawful late result that lacks an explicit predecessor link.
- Successful launch, terminal vehicle/source invalidation, and mission completion globally remove all obsolete queued/open/suspended/deferred/native-pending/native-open/tactical-held occurrences they explicitly terminalize.
- Teardown is mirrored and idempotent: duplicate departure/completion signals produce no callback, reward, launch, or reopen.

## Native UI and reactivity

- Reuse the game's native `UIStateGeoscapeEvent`, `UIStateGeoModal`, and deployment roster/preparation UI. The mod inbox owns durability and scheduling, not rendering.
- Captured presentation/context must remain sufficient to rebuild native state without deriving missing site/vehicle text from historical records.
- Already-open shared-choice windows repaint immediately when the host locks an answer: winner/result shown, losing controls disabled, local window retained.
- Already-open preparation screens repaint immediately after any peer edits soldiers or equipment:
  - re-query live deployment sources and soldier/equipment state;
  - preserve valid local enrolment/selection and prune only invalid entries;
  - re-run the native deployment validity/button path;
  - close only when globally invalidated or genuinely unservable.
- Queued and suspended copies receive the same state update before they are displayed/resumed.
- Universal open-UI repaint remains the default; a native per-screen refresh is required where Exit/Enter would run destructive callbacks or lose local checkpoint state.
- Force-close paths never call native mission Cancel merely to remove UI.

## Failure semantics

- **No sole-carrier deletion before successful transition:** the last carrier of an occurrence is not removed until the successor occurrence/effect, entitlements, order, and tombstone/transition state are durably committed.
- If native presentation creation fails, keep the lifecycle `Queued` and retry after a bounded backoff; log occurrence/player/class/reason.
- If suspension capture fails, do not preempt the ordinary window; keep priority work queued and report the failure.
- If shared-answer validation fails, leave `Unanswered`; do not charge, grant, lock, or dismiss.
- If the shared effect succeeds but response delivery fails, persist `ChoiceLocked` and resend the confirmed result; never apply twice.
- If Start cannot create `DeploymentPreparing`, retain the mission offer and report failure; do not consume its sole carrier.
- If launch fails validation, remain `Preparing`, repaint current facts, and expose the refusal. Never wait for another player.
- If subject identity cannot resolve after load/reconnect, quarantine the occurrence as unservable and request authoritative reconciliation; do not bind by display text or list index.
- Unknown taxonomy, impossible state transition, order collision, or entitlement loss is loud telemetry plus reconciliation, never silent dismissal.

## Executable law/test matrix

| ID | Executable assertion | Required positive/negative controls |
|---|---|---|
| DWI-01 | One host raise grants one stable occurrence entitlement to every durable player/membership epoch enrolled when creation is serialized, including tactical/disconnected/non-Geoscape peers. | retransmit dedupes; a new trigger for same event+subject remains distinct; epochs enrolled later receive none |
| DWI-02 | Transport ACK changes no queued/open/read/dismissed state. | explicit lifecycle message does change only that peer |
| DWI-03 | Native presentation is impossible outside a fully started Geoscape display gate. | tactical and personnel-tab accrual survives; Geoscape return serves it |
| DWI-04 | Host order survives reconnect and save/load byte-for-byte/logically unchanged. | out-of-order delivery still displays host order |
| DWI-05 | Priority ambush/deployment preempts an ordinary open Geoscape window without invoking its callback. | ordinary resumes same occurrence/checkpoint; invalidated suspended window does not resume |
| DWI-06 | Shared choice accepts exactly one first valid answer and applies one effect/reward. | concurrent loser cannot charge/grant; all open copies repaint locked result |
| DWI-07 | Choice lock does not terminate another player's entitlement. | winner and losers independently retain result until local read/dismiss |
| DWI-08 | Confirmed answer and reward resolve by stable identity after save/load, never current list index/object identity. | reordered definition choices still show/grant the confirmed item; unknown identity fails closed |
| DWI-09 | Mission-offer Cancel is local on host and clients and never calls `GeoMission.Cancel` for that gesture. | authoritative expiry/destruction still cancels globally |
| DWI-10 | Any peer Start atomically creates one new `DeploymentPreparing` and fresh entitlement at every then-enrolled durable player/membership epoch's inbox head, including players whose offer is terminal. | concurrent Starts dedupe; failed creation leaves offer intact; predecessor Cancel state is not inherited |
| DWI-11 | Any peer soldier/equipment edit becomes host state and immediately repaints every already-open preparation UI. | queued/suspended preparation later opens with latest state; invalid local selection is pruned only as needed |
| DWI-12 | Any peer launch commits `Launched`, removes all offer/preparation carriers, closes native UI safely, and transitions exactly once without readiness/quorum before the load boundary. | AFK peers never block; repeat clicks cannot arm a second launch/countdown; no preparation restores after tactical |
| DWI-13 | Linked vehicle departure prunes that source and repaints every open preparation; only loss of the last valid/uniquely bound source globally removes queued, open, suspended, deferred, and tactical-held copies without Cancel. | another valid source preserves the occurrence; unrelated departure leaves it unchanged |
| DWI-14 | Mission completion terminalizes and removes only explicitly linked predecessor generations before creating any new outcome. | cleanup by mission id alone fails; a new completion outcome remains deliverable |
| DWI-15 | No last carrier is deleted before successor/effect persistence succeeds. | injected persistence failure preserves retryable offer/window and causes no duplicate effect |
| DWI-16 | Reconnect restores each player's own lifecycle under the durable identity/membership epoch and accepts only monotonic host-validated revisions. | stale epoch/revision cannot regress `Dismissed`/`Removed`; tombstone beats peer update; ACK changes nothing |
| DWI-17 | Tactical entry durably commits `Open -> Suspended(LevelTeardown)` before callback-free native teardown; started-Geoscape reconciliation restores the exact checkpoint. | late pre-save wire replay cannot reopen invalidated/completed occurrence |
| DWI-18 | Every mirrored campaign-window family is classified by registered native state/modal/raiser; local-answer windows are explicitly local-only. | unknown family fails coverage loudly rather than inferring priority, dropping, or displaying ephemerally; classified nonmatches remain ordinary |
| DWI-19 | Compaction occurs only when entitlements are terminal and no save/journal/cursor/replay source can name a tombstoned occurrence. | one unread disconnected player keeps it durable; unprovable replay absence preserves terminal identity without TTL |
| DWI-20 | Teardown and repaint cover native current, native pending, mod queued/suspended/deferred, and tactical-held carriers. | deleting only current-open fails the law |
| DWI-21 | Back from preparation commits `Deferred` without mission Cancel and cannot reopen in the same tick. | explicit local re-entry or a newer qualifying priority revision may requeue it |
| DWI-22 | Membership creation, enrollment, reconnect, and authoritative removal use durable identity plus membership epoch without human ACK or quorum. | disconnect/AFK never ends membership; removed epoch cannot block compaction |
| DWI-23 | A newly enrolled durable player/new membership epoch receives no pre-enrollment occurrence of any category or lifecycle, including active/unread notices, locked shared results, mission offers, and `DeploymentPreparing`; only later creations grant entitlement. | joining with a nonempty backlog grants zero historical entitlements; the next creation includes the epoch |
| DWI-24 | Reconnect/resume of the same durable identity and membership epoch restores exactly that player's existing inbox and lifecycle rather than taking the late-join path. | reconnect creates no epoch, duplicates no occurrence, and grants no occurrence that never entitled that epoch |
| DWI-25 | Membership enrollment and occurrence creation are host-serialized at the durable enrollment commit boundary. | create-before-enroll excludes the epoch; enroll-before-create includes it; concurrent retries preserve that committed order without ACK/quorum |
| DWI-26 | Priority is assigned only by an exact native-registry state/modal/subject/raiser match and honors its exclusions. | all four registered families classify priority; `priority=0`, `int.MaxValue`, `UIStateGeoModal`, `MandatoryMission`, names/tags, mission outcomes/interceptions/cutscenes/game-over, and unknown patched triples do not silently classify priority |

- Each law executes production decision functions/state reducers where constructible and structurally asserts live seams only where Unity objects cannot be built in RailCheck.
- Every structural law includes a positive control proving the scanner/walker sees an intentionally bad seam.
- Falsification must reproduce the named defect, turn RED, then restore GREEN before implementation can ship.

## Migration constraints

- Existing ephemeral presentation raises may seed durable occurrences only at a verified transition boundary; never infer a campaign backlog by scanning historical `GeoscapeEventRecord` rows.
- Migration dedupe requires the complete stable identity tuple. Records lacking a stable trigger identity remain legacy/non-replayable rather than collapsing repeated events.
- A currently open legacy window is adopted only if event, trigger, and subject can be proven; otherwise it completes under the legacy path and no durable duplicate is minted.
- Existing confirmed choices migrate only when the full occurrence identity and occurrence-scoped canonical choice/result/reward-item identities are explicit:
  - never persist or key by only `eventId`, trigger count, `choiceIndex`, button index, localized text, runtime object reference, or mutable definition-list position;
  - verify every referenced answer/result/reward item belongs to that occurrence and captured subject;
  - unresolved identity produces a visible reconciliation failure and no second reward.
- Carrier-loss migration is transactional:
  - create/persist occurrence, entitlements, order, and lifecycle first;
  - bind/adopt native pending/current carriers second;
  - remove legacy/sole carriers last;
  - rollback or retry leaves at least one carrier and never marks read/dismissed.
- Persisted old transport ACKs, sequence cursors, or queue absence cannot initialize read/dismissed state.
- Existing mission briefs/preparation screens must be revalidated against current vehicle/site/mission state before adoption; unservable ones receive a global tombstone and safe force-close.
- Rollout must tolerate mixed in-memory legacy carriers within one load boundary without allowing double answer, double reward, double Start, or double launch.

## Scenarios

### Tactical accrual and reconnect

- Alice and Bob are tactical; Carol remains on Geoscape.
- Host raises research completion, a shared-choice event, then an ambush offer.
- All three receive ordered durable entitlements. Carol may see them; Alice and Bob render nothing tactical.
- Bob disconnects before any presentation; ACK history is irrelevant.
- After tactical return Alice sees the priority ambush first, then the ordinary sequence. Bob reconnects later and receives his own unread lifecycle in the same host order.

### Priority preemption and unchanged resume

- Alice is reading page/result phase of ordinary occurrence O-17 on Geoscape.
- Host creates urgent ambush A-4.
- Alice's O-17 checkpoint is persisted as suspended before A-4 opens. O-17 callback does not run.
- Alice locally Cancels the offer; only her A-4 copy dismisses.
- O-17 resumes at the same phase/selection and remains unread until Alice dismisses it.

### Shared first answer wins, per-player result retention

- Alice and Bob have the same shared-choice occurrence open; Carol is offline.
- Bob answers B milliseconds before Alice answers A.
- Host locks B once, applies its cost/reward once, and rejects A as stale.
- Alice and Bob immediately repaint to locked result B but dismiss independently.
- Carol reconnects and receives result B as unread; Bob's dismissal cannot consume Carol's entitlement.

### Mission Start, collaborative preparation, Back, launch, no quorum

- Alice previously Cancels offer M-9 locally; Bob leaves his offer untouched; Carol is AFK.
- Bob presses Start. Host creates one new `DeploymentPreparing(M-9)` and fresh entitlements at all three inbox heads, including Alice's; predecessor Cancel state is not inherited.
- Alice and Bob both open preparation. Bob changes a soldier's weapon; host accepts state and Alice's already-open roster/equipment view repaints immediately.
- Alice presses Back. Her lifecycle becomes `Deferred`, the previous screen returns, `GeoMission.Cancel` does not run, and preparation cannot auto-open again in the same tick.
- Bob launches. Host commits the `Launched` tombstone, removes all offer/preparation carriers including Alice's deferred copy and Carol's tactical/queued copy, safely closes open screens, then enters the load boundary once without Alice or Carol confirming.

### Vehicle departure with another valid source

- Alice has M-9 preparation open from vehicles V-1 and V-2; Bob has its offer queued; Carol has preparation suspended.
- V-1 leaves the site. Host removes only V-1 and its occupants from live sources and immediately repaints Alice; V-2 keeps every offer/preparation occurrence servable.
- V-2 then leaves. Host commits the invalidation tombstone, globally removes every queued/open/suspended/deferred/tactical-held offer/preparation carrier, and force-closes open screens without `GeoMission.Cancel`.
- Late Start/edit/launch messages are rejected against the tombstone and cannot reopen M-9.

### Mission completion overtakes unread windows

- Bob never read a mission brief; Alice launched and the team completes the mission.
- Completion first terminalizes Bob's obsolete brief and every stale preparation predecessor by explicit `supersededBy`/generation links, then removes all of their carriers globally.
- Only afterward does it create any completion outcome with a new trigger/order and generation; mission-identity-only cleanup is forbidden, so the new outcome remains unread for Bob until local dismissal.

### Failed transition keeps the sole carrier

- Carol presses Start while persistence of `DeploymentPreparing` is fault-injected to fail.
- Host applies no launch/preparation transition, retains Carol's offer and every entitlement, and reports a retryable failure.
- Retrying after storage recovery creates one preparation occurrence; no offer is lost and no duplicate preparation is created.
