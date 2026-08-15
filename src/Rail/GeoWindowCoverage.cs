using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>How a window the GAME pushes at the player relates to the rail.</summary>
    internal enum WindowSync
    {
        /// <summary>Replicated host→client with its own payload; both peers get the window.</summary>
        Mirrored,
        /// <summary>Deliberately NOT replicated, and that is correct — per-peer presentation, a local
        /// navigation gesture, or a decision that belongs to one peer alone. Silent by design.</summary>
        LocalOnly,
        /// <summary>A known, reviewed HOLE: this window SHOULD reach the other peer and does not yet.
        /// Announced once per type per session — a gap the player can see must never be a gap only the
        /// player can see.</summary>
        Gap,
    }

    internal sealed class WindowRule
    {
        public WindowSync Sync;
        public string Why;
    }

    /// <summary>
    /// COVERAGE for every window the game PUSHES at the player on the geoscape (law 11, universal seam).
    ///
    /// THE CHOKEPOINT. <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>
    /// (GeoscapeViewSwitchQuery.cs:75) is the one queue a pushed window goes through, and every caller in
    /// the shipped game is a method on <c>GeoscapeView</c> itself — nine of them
    /// (:596 deployment, :666 game-over, :675 cutscene, :861 modal-persistent, :881 modal, :1321 asset
    /// deploy, :1617 tutorial, :2062 event window, :2071 replenish). That partition is the useful one and
    /// it is not cosmetic: a state pushed HERE is the game interrupting the player, while a state pushed
    /// through <c>_statesStack.SwitchToState</c> directly (ToGeoRosterState:591, ToVehicleRosterState:602,
    /// SetNothingState:661, OpenModal's own forceOnTop/replaceTop branches:885-893 …) is the LOCAL player
    /// navigating their own geoscape, which must never be replicated.
    ///
    /// It is NOT, however, a single REPLICATION point, and that is the finding that decides this file's
    /// shape. <c>GeoscapeViewStateSwitchRequest</c> carries a LIVE <c>IState</c> instance
    /// (GeoscapeViewStateSwitchRequest.cs:7) and nothing else — no ids, no defs. The states behind it hold
    /// exactly what law 2 addressing cannot reach: <c>UIStateGeoModal</c> is built from a
    /// <c>DialogCallback</c> CLOSURE over the caller's own locals (GeoscapeView.cs:849-852, :1987-1990) plus
    /// an arbitrary <c>object modalData</c> that is a different class per <c>ModalType</c> — 41 of them
    /// (ModalType.cs), from a <c>GeoResearchCompleteData</c> built inline at :1984 to a live
    /// <c>GeoMission</c>, <c>GeoSite</c> or ability context. So "capture at the chokepoint and ship the
    /// request" cannot exist; each kind needs its own payload, exactly as <see cref="EventPopup"/>'s 0xB6
    /// raise does.
    ///
    /// What the chokepoint CAN be — and is, here — is the one place that makes coverage TOTAL and LOUD.
    /// Every kind that reaches the queue must be DECLARED below with a reviewed reason; an undeclared kind
    /// (a game update, a DLC, a mod's own view state) is announced as an ERROR instead of quietly appearing
    /// on one peer's screen and nowhere else, and RailCheck L48 fails the build for it rather than waiting
    /// for someone to notice in a co-op session. That is the mandate's rule applied to windows: a swallow
    /// becomes a falsified law.
    /// </summary>
    internal static class GeoWindowCoverage
    {
        /// <summary>Keyed on the view-state type; a SUBCLASS inherits its base's rule (a mod that derives
        /// from <c>UIStateGeoModal</c> is the same window with the same reason, while a genuinely new
        /// <c>GeoscapeViewState</c> still has to be reviewed). RailCheck L48 asserts this table covers every
        /// type the game can actually queue, and that it holds nothing it cannot.</summary>
        internal static readonly Dictionary<Type, WindowRule> Declared = new Dictionary<Type, WindowRule>
        {
            [typeof(UIStateGeoscapeEvent)] = new WindowRule
            {
                Sync = WindowSync.Mirrored,
                Why = "the event picker — captured at GeoscapeView.OnGeoscapeEventRaised:2034 (EventRaiseBroadcast) " +
                      "and shipped as surface 0xB6 with the site/vehicle root refs, the host-resolved texts and " +
                      "the host's own queue priority; answers ride back as the 0xB4 intent, keyed on the event's " +
                      "own EventID. That key is the ONLY thing that resolves this window across peers: closing " +
                      "the picker locally advances nobody else's queue (WindowQueueSync.IdentityOf refuses every " +
                      "non-modal state), because for one build on 2026-08-01 it did — matched on the view-state " +
                      "TYPE, one client's close dismissed the host's unrelated event, and the host's own " +
                      "UIStateGeoscapeEvent.ExitState:61-65 then answered it with Choices.Last()",
            },
            [typeof(UIStateMarketplaceGeoscapeEvent)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "VERDICT UNCHANGED, GROUND REPLACED 2026-08-05: opening the shop is still a LOCAL gesture on " +
                      "either peer (MarketplaceAbility.ActivateInternal:43 → GeoscapeView.ToMarketplace:734-738 " +
                      "calls the view directly), so the WINDOW is never raised for anyone else. The old rationale " +
                      "— \"its offer list is not replicated\" — is stale: the offers now ride their own host→all " +
                      "surface 0xBF (MarketplaceSync; the host owns the roll, the client's UpdateOptions is " +
                      "blocked) and a purchase rides the 0xBE intent, so both peers DO hold the same rows. What " +
                      "stays local is only the act of looking at them: mirroring the window would yank a peer's " +
                      "screen into a shop it never opened (the A6 InventoryAbility lesson). EventPopup" +
                      ".HostBroadcast declines it; repaint is driven from MarketplaceSync.RepaintOpenMarketplace, " +
                      "since a queued window is skipped by the universal Exit+Enter repaint",
            },
            [typeof(UIStateRosterDeployment)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                // VERDICT UNCHANGED, GROUND REPLACED 2026-08-01 (second time today): the squad INTENT the
                // previous wording named as the missing arc SHIPPED as MissionSync (0xB8), so "a client's
                // squad pick has nowhere to be committed" is no longer true and may not be the reason.
                // The screen stays LocalOnly on the ordinary per-peer-navigation ground instead.
                Why = "each peer opens its OWN deployment screen by its OWN click, so there is nothing to " +
                      "mirror: the screen is local navigation over a roster both peers already hold, exactly " +
                      "like UIStateGeoRoster. What it COMMITS is what crosses — the Deploy button ends in " +
                      "GeoMission.Launch(GeoSquad):226, captured block-first as the 0xB8 launch intent " +
                      "(MissionSync), and the host's launch then pulls EVERY peer into the battle through the " +
                      "native save transfer (TacticalEntry + SaveTransferCoordinator), which is why no peer " +
                      "needs the other's screen. The Back button's GeoMission.Cancel is gated on a client " +
                      "(MissionCancelGate) so backing out stays navigation and never deletes a shared mission; " +
                      "the BRIEF that opens this screen is answered per peer for the same reason since " +
                      "2026-08-09 (MissionSync.PerPeerModalAnswer), so one player declining leaves everybody " +
                      "else's brief live. " +
                      "Mirroring the screen itself would drag a peer into a squad pick it did not ask for",
            },
            [typeof(UIStateGeoscapeTutorial)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "tutorial steps are per-PEER progress, not campaign state (GeoscapeView.OnShowTutorialStep" +
                      ":1614 fires off the local tutorial system) — a peer that already knows the game must not be " +
                      "stopped by the other's tutorial",
            },
            [typeof(UIStateGeoModal)] = new WindowRule
            {
                Sync = WindowSync.Mirrored,
                Why = "the modal family — 43 ModalTypes ride this ONE state, so the verdict is not per view-state " +
                      "but per ModalType and lives in DeclaredModals below (RailCheck L49 keeps THAT table total " +
                      "over the enum). Captured at the two openers GeoscapeView.OpenModal:867 / " +
                      "OpenModalPersistent:848 and shipped as surface 0xB7 by GeoModalMirror, which describes the " +
                      "`object modalData` by its RUNTIME shape (rail root ref + the game's own string ids) and " +
                      "drops the DialogCallback closure entirely — the client's copy is built with a null handler, " +
                      "so no button on it can run game logic. What a button on that copy DOES do since 2026-08-01 " +
                      "is emit the 0xB9 advance intent (WindowQueueSync), which makes the HOST run its own " +
                      "FinishDialog:82 for the same window — the answer crosses as one byte and the logic stays " +
                      "host-side, which is the only reading of law 3 under which a peer can resolve anything at " +
                      "all. This MIRRORED family is the whole reach of that op and the reason is exactly this " +
                      "entry: the copy was built out of the host's own 0xB7 payload, so re-describing it " +
                      "(GeoModalMirror.Describe over the live ModalData) yields the same identity string on both " +
                      "peers for the same window INSTANCE and a different one for every other — which a " +
                      "type-plus-ModalType pair did not, and that was the 2026-08-01 regression",
            },
            [typeof(UIStateGeoCutscene)] = new WindowRule
            {
                Sync = WindowSync.Mirrored,
                // THE RAISE HALF SHIPPED 2026-08-01 (surface 0xBA, CutsceneMirror), which is what the previous
                // wording named as the missing piece: "a cutscene holds the queue slot until OnCancel:92, so on
                // an idle host it stops the campaign for everybody ... draining it needs the RAISE half first".
                // Live that day a story cinematic played on the host and reached NO other peer at all — not the
                // screen, not the window history — which is the complaint the Gap verdict was recording.
                Why = "story cinematics — captured at the ONE funnel GeoscapeView.ToCutsceneState:672 and " +
                      "shipped as surface 0xBA with the VideoPlaybackSourceDef's GUID (law 2) and the host's own " +
                      "queue priority. The video is REPLAYED, never shipped: each peer runs the same native " +
                      "ToCutsceneState off its OWN assets, so nothing about playback crosses and each peer's " +
                      "queue orders it exactly as the host's did. Every geoscape cinematic reaches that funnel " +
                      "(research TriggerCutscene GeoscapeView:2114, GeoFactionReward.Cinematic:264 = the " +
                      "GeoEventChoiceOutcome field, GeoMarketplace:165/:175, the campaign intro " +
                      "GeoLevelController:743). ToGameOverState:662 is EXCLUDED BY CONSTRUCTION and that is why " +
                      "the seam is this method and not the UIStateGeoCutscene ctor the two share: game-over " +
                      "builds its state with an EndFromGameOver callback that ENDS THE LEVEL (:666), a host " +
                      "outcome which reaches every peer as the native teardown rather than as a video. Each peer " +
                      "closes its own copy (OnCancel:92) — there is no dismiss message, same contract as 0xB6 " +
                      "and 0xB7, and 0xB9's withdrawn non-modal arm is not needed to drain it any more now that " +
                      "every peer holds one",
            },
            [typeof(UIStateAssetDeployment)] = new WindowRule
            {
                Sync = WindowSync.Mirrored,
                // VERDICT REVERSED 2026-08-05. The old LocalOnly reasoning — "a HOST decision on host-owned
                // assets, mirroring would ask the client to decide something it cannot apply" — was answering
                // the wrong question: a client cannot APPLY the placement, but it can NAME it, and the host
                // applies. The 2026-08-01 amendment had already recorded the cost of getting it wrong ("left
                // up on an idle host it blocks every later window ... a REAL and still-OPEN hole"), and the
                // fix it named — "draining it needs a host→all raise" — is what shipped here.
                Why = "\"where does this newly manufactured vehicle / recruited soldier go\" (GeoscapeView" +
                      ".PrepareDeployAsset:1308, self-gated to faction == ViewerFaction). HOST-RAISED BY " +
                      "CONSTRUCTION and that is why it was a one-screen window: both raisers sit behind gates a " +
                      "client never passes — a manufacture completion (ItemManufacturing.FinishManufactureItem" +
                      ":498 -> OnManufacture, reached only from Manufacture.Update() inside LevelHourlyUpdateCrt, " +
                      "which ClientSimGate blocks WHOLE) and GeoPhoenixFaction.AddRecruit:708. Since 2026-08-05 " +
                      "it rides the GENERIC non-modal arm of surface 0xB7 (GeoModalMirror.StateKind" +
                      ".AssetDeployment): the payload names the asset by its own rail root ref, the aircraft and " +
                      "related item by def guid, and the two flags as bits, and the peer rebuilds the " +
                      "GeoDeployAssetFactionCharacterBind off ITS OWN graph and stamps its OWN faction exactly as " +
                      "the game's save-restore does (RestoreContext:35) — nothing but addresses crosses. It parks " +
                      "in the SAME ModalParkQueue when the asset has not landed yet (a manufactured ground " +
                      "vehicle is minted by CreateCharacterFromTemplate in the same call stack as the prompt, so " +
                      "the raise always beats its own structural create) and refuses LOUDLY when it never does. " +
                      "The ANSWER is host-authoritative and travels back: DeployAtSite:69 is blocked on a client " +
                      "and becomes the 0xB9 deploy intent, which the host validates against its OWN prompt's " +
                      "DeploySites and then answers by calling the game's own DeployAtSite — so law 91 holds " +
                      "(any peer can drain this window for everyone) and law 3 holds (only the host runs " +
                      "GeoPhoenixFaction.DeployAsset). DECLARED REMAINDER: a client that cannot resolve the asset " +
                      "gets NO copy and the host's own prompt still needs a click on the host — the raise is " +
                      "refused loudly rather than rendered over a null, and the window is never replayed",
            },
            [typeof(UIStateReplenish)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "the post-mission replenish screen, raised by the RETURNING peer's own UIStateInitial:127 as " +
                      "it comes back from tactical — per-peer arrival UI over an aircraft that peer just flew; the " +
                      "restocking it performs rides the value rail like any other. VERDICT UNCHANGED, PREMISE " +
                      "REPAIRED 2026-08-01 (user report item 3b: the resupply panel appeared on the host only): " +
                      "\"the RETURNING peer's own\" describes what the game does, not what this mod did. " +
                      "QueueReplenishState sits INSIDE the same UIStateInitial:101 branch as the mission-outcome " +
                      "modal, and ClientMissionResultGate's whole-method block on GeoMission.Complete kept that " +
                      "branch false on every client, so no client ever raised it. Nothing here needs the wire — " +
                      "its gate is this peer's own GeoPhoenixFaction.GetMissingItems() over its own aircraft and " +
                      "its own storage, all mirrored — it only needed the completion flag the block had taken " +
                      "away; the client now sets it with the game's own CompleteSilently:284",
            },
        };

        /// <summary>The rule for a state type, inherited from the nearest declared base. Null = undeclared.</summary>
        internal static WindowRule RuleFor(Type stateType)
        {
            for (var t = stateType; t != null; t = t.BaseType)
                if (Declared.TryGetValue(t, out var rule)) return rule;
            return null;
        }

        // ─── The SECOND axis: UIStateGeoModal is 43 windows wearing one type ───

        /// <summary>
        /// COVERAGE for the modal family, keyed on <c>ModalType</c> because that — and not the view-state type
        /// — is what decides which window the player is looking at (<c>UIModuleModal.Show</c>:46 picks the
        /// prefab off <c>AvailableModals</c> by it). TOTAL over the enum by law: RailCheck L49 derives the
        /// universe from <c>typeof(ModalType)</c>'s own members, so a modal added by a patch, a DLC or a mod
        /// fails the build instead of appearing on one screen and nowhere else.
        ///
        /// THE VERDICT RULE, and it is the GAME's code that decides it, not ours: a modal can mirror when its
        /// host-side <c>DialogCallback</c> does nothing AUTHORITATIVE. Read
        /// <c>GeoscapeView.ModalResultCallback</c>:798 top to bottom and the whole partition falls out — a
        /// mission brief resolves to <c>LaunchMission</c>/<c>Cancel</c>, an ability confirmation to
        /// <c>GeoAbility.Activate</c>, a soldier join to <c>reward.Apply</c>, while an outcome hits an early
        /// return and a research-complete only navigates. What is left over is the campaign-progress
        /// NOTIFICATION, and that is exactly the Mirrored set.
        /// </summary>
        internal static readonly Dictionary<ModalType, WindowRule> DeclaredModals = BuildModalRules();

        private static void Modal(Dictionary<ModalType, WindowRule> d, WindowSync sync, string why,
                                  params ModalType[] types)
        {
            foreach (var t in types) d[t] = new WindowRule { Sync = sync, Why = why };
        }

        private static Dictionary<ModalType, WindowRule> BuildModalRules()
        {
            var d = new Dictionary<ModalType, WindowRule>();

            // ── MIRRORED: host-side campaign-progress notifications, acknowledgement-only ──
            Modal(d, WindowSync.Mirrored,
                "research completed — VERDICT RESTORED 2026-08-15 (LocalOnly 2026-08-05 → 2026-08-15). The " +
                "2026-08-05 reversal rested entirely on there being a SECOND producer on the client: " +
                "ResearchSync.PresentFromMirror, which reflected into the game's own " +
                "GeoscapeView.OnFactionResearchCompleted:1980 off the mirrored research state and made the " +
                "window arrive TWICE (two UIStateGeoModal entries 225 ms apart on Instance2). That producer " +
                "is DELETED (§A.9, L520: the journal is the single entrance and no mod-served replay may " +
                "raise a window beside it), so the premise is gone and LocalOnly — 'each peer raises its " +
                "own' — became a claim about a raiser that no longer exists. MEASURED 2026-08-15: the host " +
                "journalled pos=1 and pos=2 UIStateGeoModal, both clients only ever received pos=3 " +
                "UIStateGeoscapeEvent, and the two research windows appeared on the host alone. A client's " +
                "game NEVER runs the native handler: the rail writes ViewerFaction.Research directly and " +
                "does not travel the completion path that fires ResearchCompletedEventHandler. The payload " +
                "needs no work — DataShape.ResearchComplete already ships Ref = the research's faction, " +
                "Keys[0] = ResearchID, Num = SwitchToResearchState, and GeoModalMirror.BuildData rebuilds " +
                "the GeoResearchCompleteData off THIS peer's own live ResearchElement, so no wire schema " +
                "moves. dialogHandler stays null and loses nothing: " +
                "ResearchCompleteModalHandler:2107 ignores the ModalResult entirely, and the game's own " +
                "raiser passes SwitchToResearchState: false (GeoscapeView.cs:1985). ponytail: the " +
                "SURVIVING half of the 2026-08-05 finding is a COSMETIC ceiling, not a reason to withhold " +
                "the window — 0xB7 is on the wire before the 0xAC deltas that fill " +
                "ResearchElement.UnlocksResearches (DiffEngine.HostTick, 0.1 s), so a client's copy may " +
                "draw with NewResearchesGroup hidden (GeoReseatchCompleteDataBind.SetResearchRewards" +
                ":171-181). A missing blink beats a missing window; if it is ever worth fixing, the lever " +
                "is GeoModalMirror.NeedsPark, not a second producer",
                ModalType.GeoResearchComplete);
            Modal(d, WindowSync.Mirrored,
                "diplomacy research share — raised HOST-side by GeoscapeView.PxFaction_ResearchShared:1990 with a " +
                "NULL DialogCallback in the shipped game, so there is nothing authoritative to lose. Data = " +
                "DiplomacyResearchRewardData{Faction, Researches, DiplomacyShareLevel}: faction root ref + the " +
                "ResearchIDs + the level, all three already replicated, so both peers see the same gift from the " +
                "same faction",
                ModalType.DiplomacyResearchBrief);
            Modal(d, WindowSync.Mirrored,
                "a Phoenix base was activated from exploration — GeoscapeView.PxFaction_OnBaseActivated:1963, " +
                "modalData NULL (nothing to resolve, nothing that can fail to resolve) and an empty `case` in " +
                "ModalResultCallback:798. Shared campaign truth about a base BOTH peers now own",
                ModalType.GeoPhoenixBaseOutcome);
            Modal(d, WindowSync.Mirrored,
                "mission BRIEF — raised host-side at GeoscapeView.cs:1903 (OpenModalPersistent), modalData = " +
                "the live GeoMission. SHIPPED 2026-08-05 as the GENERIC 0xB7 EntityRef shape and not as a " +
                "brief-specific payload: the mission is named by the rail path its own site already addresses " +
                "it with (S#<id>.SerializationData.ActiveMission — the Descend field the value rail " +
                "structurally creates on the client, docs/rail-baseline.txt), and the client's copy is built " +
                "from what THAT path resolves to on its own graph, never from a wire reference the host may " +
                "already have cancelled. Ten ModalTypes ride it and none of them cost a line: " +
                "GetMissionBriefModal:1724 only picks the prefab. ANSWERED PER PEER SINCE 2026-08-09, and the " +
                "previous wording is what broke: it said \"the click crosses as the 0xB9 advance intent and the " +
                "HOST runs its own FinishDialog:82, because the copy here carries a null DialogCallback\". That " +
                "host-side FinishDialog runs ModalResultCallback:825-826, whose Cancel arm is GeoMission.Cancel" +
                ":253 (Site.ActiveMission = null, DestroySite, Reward wiped) — so ONE player declining deleted " +
                "the mission for the whole team and left everybody else a window whose Confirm the host then " +
                "refused. Now the copy carries the game's OWN callback (verbatim from UIStateGeoModal" +
                ".RestoreContext:36-39, GeoModalMirror), emits NO 0xB9 (WindowQueueSync.SendAdvance), and " +
                "MissionSync.PerPeerModalAnswer refuses every non-Confirm answer of this class on BOTH roles — " +
                "declining is \"I am busy building\", never \"cancelled for everyone\". Confirm still goes where " +
                "it always did: LaunchMission:1043 is pure view and its SkipDeploymentScreen arm's " +
                "mission.Launch:1046 is captured block-first as the 0xB8 launch intent (MissionSync, which " +
                "likewise reads the host's OWN site.ActiveMission); MissionCancelGate still holds the model " +
                "funnel on a client. Law 91 holds and is stronger than before — a brief on an idle host holds " +
                "nobody, because no peer is waiting on its answer at all. If the mission has not reached this " +
                "peer yet the raise is REFUSED loudly (DataRefusal) — no window beats a window over " +
                "placeholder text",
                ModalType.GeoHavenAttackBrief, ModalType.GeoAlienBaseBrief, ModalType.GeoScavengeBrief,
                ModalType.GeoPhoenixBaseDefenseBrief, ModalType.GeoAmbushBrief,
                ModalType.GeoPhoenixBaseInfestationBrief, ModalType.AncientSiteAttackBrief,
                ModalType.AncientSiteDefenceBrief, ModalType.BehemothAttackBrief, ModalType.InfestedHavenBrief);
            Modal(d, WindowSync.Mirrored,
                "a faction soldier offers to join — HavenMissionUtil.cs:59, modalData = the offered " +
                "GeoCharacter (reward.Units.First()). SHIPPED 2026-08-05 by the SAME EntityRef shape with " +
                "zero type-specific code, which is the point: a GeoCharacter is a rail ROOT, so " +
                "IdentityResolver.RootRef names it \"U#<id>\" on rung one and the peer resolves it out of its " +
                "own _tacUnits. It is a real root and not a detached reward object — " +
                "GenerateHavenMissionRecruitmentReward:93 spawns it through " +
                "GeoLevelController.CreateCharacterFromDescriptor, whose last line is " +
                "`_tacUnits[geoCharacter.Id] = geoCharacter`:1597, so the walk creates it structurally on the " +
                "peer like any other unit. Confirm stays HOST-authoritative: reward.Apply(faction, site, " +
                "aircraft) runs on the host through the 0xB9 advance intent's native FinishDialog, and what " +
                "it grants was already replicated. KNOWN ORDERING CEILING: the raise is broadcast in the same " +
                "breath as the spawn, so a peer whose structural create has not landed yet refuses it loudly " +
                "(DataRefusal) instead of rendering an empty prefab",
                ModalType.FactionSoldierJoin);

            // ── GAP: should reach the other peer, and does not yet ──
            Modal(d, WindowSync.Gap,
                "mission OUTCOME — TWO raisers with OPPOSITE verdicts under one ModalType, which is why this stays " +
                "a declared hole instead of a half-answer. UIStateInitial.cs:112 raises it on the peer RETURNING " +
                "from tactical off its own _params.LastMission — per-peer arrival UI, so mirroring THAT would show " +
                "the window twice. FALSE PREMISE CORRECTED 2026-08-01 (measured, user report item 3a): the words " +
                "\"fires natively on every peer that played the mission\" were never true in this repo, and this " +
                "entry rested on them. It fires on the HOST only, because ClientMissionResultGate blocked " +
                "GeoMission.Complete WHOLE and Complete:267/:275 is the sole writer of both flags the raising " +
                "branch tests (UIStateInitial:101 `IsCompleted || GetMissionOutcomeState() != Playing`) — so on a " +
                "client the branch was permanently false and the panel simply never existed. The client now calls " +
                "the game's own CompleteSilently:284 instead (see ClientMissionResultGate) and the raise is " +
                "genuinely per-peer, as this verdict always claimed; the CONTENT the panel draws — " +
                "reward.ApplyResult, which every one of the eleven outcome data binds reaches through the same " +
                "RewardsController.SetReward(mission.Reward) call — rides 0xBB MissionOutcomeMirror, since it is " +
                "host-computed and on no other rail. STILL A GAP, and now for ONE reason only: " +
                "GeoscapeView.OnSiteMissionCancelled:1930 raises the base-defence / ancient-site variants off a " +
                "HOST sim event alone, which no client ever fires. Splitting the two raisers needs a RAISER-aware " +
                "seam; a per-ModalType verdict cannot express it. 0xBB's declared remainder rides here too: it " +
                "carries Resources + Items, not SetReward's stolen-aircraft / stolen-research / new-unit / " +
                "captured-alien rows (:104-114)",
                ModalType.GeoHavenAttackOutcome, ModalType.GeoAlienBaseOutcome, ModalType.GeoScavengeOutcome,
                ModalType.GeoPhoenixBaseDefenseOutcome, ModalType.GeoAmbushOutcome, ModalType.HavenInfiltrateOutcome,
                ModalType.GeoPhoenixBaseInfestationOutcome, ModalType.AncientSiteAttackOutcome,
                ModalType.AncientSiteDefenceOutcome, ModalType.BehemothAttackOutcome, ModalType.InfestedHavenOutcome);
            Modal(d, WindowSync.Gap,
                "pandoran reveal result — UIStateInitial.cs:118/:122 over Reward.ApplyResult.RevealedSites[0]: " +
                "arrival UI on the returning peer, same shape and same split as the outcome family, and the sites " +
                "it reveals are shared campaign truth the other peer is simply not told about. Same hole, same fix",
                ModalType.PandoranRevealResult);
            Modal(d, WindowSync.Gap,
                "interception — InterceptionInfoData holds LIVE aircraft LISTS (player/enemy/disengaged), not one " +
                "rail ref, and the brief sets DisableCancel (InterceptionBriefDataBind.cs:83) which " +
                "UIStateGeoModal.OnCancel:96 honours, so a mirrored copy could not even be closed; Confirm reads a " +
                "TOGGLE off the host's own prefab and calls LaunchInterceptionGame. The outcome's GeoAirMission has " +
                "no rail identity at all. Air combat needs its own replication before its windows can mean anything. " +
                "AMENDED 2026-08-01: a brief that DisableCancel has wedged on an idle host wedges its whole " +
                "queue and the shared clock with it, so this Gap is a campaign-stopper and not only a missing " +
                "window. The same-day claim that 0xB9's advance op resolves it host-side \"without a mirrored " +
                "copy existing at all\" is WITHDRAWN and was the regression: an answer with no copy to name it " +
                "from could only be matched on the window's KIND, so it landed on whatever the host had up. " +
                "0xB9 now reaches only what the host itself raised to the answering peer, which is nothing here",
                ModalType.InterceptionBrief, ModalType.InterceptionOutcome);
            Modal(d, WindowSync.Gap,
                "alien intelligence brief — AlienIntelligenceBriefData carries its OWN DialogCallback (invoked at " +
                "GeoscapeView.cs:2017) plus a live GeoscapeViewContext the raiser injects (:2011) and a " +
                "List<RewardDiplomacyChange>; the data object IS the closure. Describable in principle (faction + " +
                "research ids + the diplomacy deltas), not described yet",
                ModalType.AlienResearchBrief);
            Modal(d, WindowSync.Gap,
                "NO caller anywhere in the shipped assembly (full sweep 2026-07-31) — declared so this table stays " +
                "TOTAL over the enum. Gap rather than LocalOnly on purpose: nobody has reviewed what these should " +
                "do because nothing raises them, so if a patch, a DLC or a mod ever does, the runtime announce is " +
                "LOUD at the moment it happens instead of silently deciding it does not matter",
                ModalType.LoadPrompt, ModalType.SiteEncounter, ModalType.SiteRecruit, ModalType.ResourcePayment);

            // ── LOCALONLY: correct NOT to replicate ──
            // Every entry below is raised by THIS peer's own gesture. Nothing raised by the host's own
            // simulation may live here — GeoWindowCoverage.HostAuthoritativeRaisers names that class and
            // L546 keeps it out, because a LocalOnly declaration over it mints a journal position and
            // silently destroys it (research-completed did exactly that, 2026-08-05 → 2026-08-15).
            Modal(d, WindowSync.LocalOnly,
                "ability CONFIRMATION — raised by the clicking peer's own ability view " +
                "(ActivateBaseAbilityView.cs:19/:28, ExcavateAbilityView.cs:16, AncientGuardianGuardAbilityView.cs:16) " +
                "to confirm a gesture that peer just made; Confirm→GeoAbility.Activate. A prompt belongs to whoever " +
                "is being asked, and the ability's EFFECT reaches the other peer as ordinary value/structural deltas",
                ModalType.ActivateBase, ModalType.ActivatedBaseInfested, ModalType.AncientSiteExcavate,
                ModalType.AncientSiteGuardiansPurchase);
            Modal(d, WindowSync.LocalOnly,
                "soldier-edit picker — pushed with forceOnTop from UIStateEditSoldier (:670 dual class, :707 buy " +
                "ability), i.e. straight onto _statesStack and never through the queue at all: the LOCAL player " +
                "editing their own soldier. Its data is a view-model struct over UI stage values " +
                "(ConfirmBuyAbilityDataBind.Data / SelectSpecializationDataBind.Data), and what it COMMITS rides " +
                "the 0xAF personnel intent rail",
                ModalType.DualClassPicker, ModalType.CharacterProgressionConfirmCharacter);
            Modal(d, WindowSync.LocalOnly,
                "haven infiltration brief — raised by the clicking peer's own haven panel or steal-aircraft ability " +
                "(HavenFacilityController.cs:101/:142, StealAircraftAbility.cs:88); Confirm launches an infiltration " +
                "mission from THAT peer's own aircraft. Same class as the ability confirmations above",
                ModalType.HavenInfiltrateBrief);
            Modal(d, WindowSync.LocalOnly,
                "demo build only — GeoscapeView.OnTimeLimitReached:1909, opened with replaceTop so it CLEARS the " +
                "local state stack to end that peer's session. Not campaign state and not a window the other peer " +
                "should be dragged into",
                ModalType.GameDemoEnd);
            Modal(d, WindowSync.LocalOnly,
                "the game itself never opens it: OpenModalPersistent:850 early-returns for _CustomMission, and the " +
                "brief chain maps GeoCustomMission to exactly this value. A custom mission prompts through the " +
                "KeepEncounterID geoscape EVENT it triggers instead (GeoscapeView.cs:1888-1892), which rides the " +
                "0xB6 raise",
                ModalType._CustomMission);
            Modal(d, WindowSync.LocalOnly,
                "the sentinel GetMissionBriefModal:1723 / GetMissionOutcomeModal:1799 return for a null or " +
                "unmapped mission, and :1897 refuses to open on it. Not a window",
                ModalType.None);

            return d;
        }

        /// <summary>The rule for one <c>ModalType</c>. Null = undeclared, which RailCheck L49 makes
        /// impossible to commit and <see cref="AnnounceModal"/> makes impossible to miss at runtime.</summary>
        internal static WindowRule RuleForModal(ModalType modal) =>
            DeclaredModals.TryGetValue(modal, out var rule) ? rule : null;

        /// <summary>
        /// THE HOST-AUTHORITATIVE RAISERS (L546): modals the host's OWN SIMULATION raises by reaching a
        /// milestone, never a peer clicking. Every entry is a <c>GeoscapeView</c> method that exists only as
        /// a faction/site EVENT SUBSCRIPTION and opens a modal from inside it — verified against the shipped
        /// assembly 2026-08-15: <c>OnFactionResearchCompleted</c>:1980 → GeoResearchComplete,
        /// <c>PxFaction_ResearchShared</c>:1994 → DiplomacyResearchBrief, <c>PxFaction_OnBaseActivated</c>:1962
        /// → GeoPhoenixBaseOutcome, <c>OnSiteMissionCancelled</c>:1934/:1938 →
        /// GeoPhoenixBaseDefenseOutcome / AncientSiteDefenceOutcome, <c>OnNewAlienIntlligence</c>:2010 →
        /// AlienResearchBrief.
        ///
        /// WHY THE SET IS A DECLARATION AND NOT A VERDICT: a client's own game NEVER runs these handlers.
        /// The rail writes the mirrored state directly (0xAC values, structural creates); it does not travel
        /// the native completion path that fires <c>ResearchCompletedEventHandler</c>. So on a client there
        /// is NO local producer at all, and §A.9/L520 deleted the last mod-served replay that pretended
        /// otherwise. <see cref="WindowSync.LocalOnly"/> — "each peer raises its own" — is therefore a
        /// FALSE STATEMENT about any member of this set, and a false one that fails SILENTLY: the seam mints
        /// a journal position for the window either way and
        /// <see cref="GeoModalMirror.PublishRefusal"/> then throws it away, so the host's journal holds a
        /// position no peer will ever receive. That is exactly what GeoResearchComplete did between
        /// 2026-08-05 and 2026-08-15 (host pos=1 and pos=2, both clients only ever saw pos=3).
        ///
        /// <see cref="WindowSync.Gap"/> STAYS LEGAL HERE and Gap alone: a gap is an ANNOUNCED hole
        /// (<see cref="AnnounceModal"/> warns once per session with its reason), which is a reviewed
        /// decision rather than a silent one. L546 forbids only the silent claim.
        /// </summary>
        internal static readonly ModalType[] HostAuthoritativeRaisers =
        {
            ModalType.GeoResearchComplete,
            ModalType.DiplomacyResearchBrief,
            ModalType.GeoPhoenixBaseOutcome,
            ModalType.GeoPhoenixBaseDefenseOutcome,
            ModalType.AncientSiteDefenceOutcome,
            ModalType.AlienResearchBrief,
        };

        // ─── The THIRD axis: WHO the answer belongs to ──────────────────────

        /// <summary>PURE (RailCheck L351/L352). Is this window one EVERY PEER ANSWERS FOR ITSELF?
        ///
        /// THE REPORT (2026-08-08): one player declined a deployment brief and the mission vanished for the
        /// whole team. <c>GeoscapeView.ModalResultCallback</c>:825-826 answers a brief's Cancel with
        /// <c>geoMission.Cancel()</c>, and <c>GeoMission.Cancel</c>:253-265 nulls <c>Site.ActiveMission</c>,
        /// may <c>DestroySite()</c> and wipes <c>Reward</c>. So a gesture that means "I am busy building"
        /// deleted shared campaign state, and every other peer was left holding a window over a mission the
        /// host no longer had — its Confirm then refused by <see cref="MissionSync.Validate"/>.
        ///
        /// THE CLASS IS THE GAME'S, NOT A LIST OF OURS. A hand-written set of ModalTypes would be stale the
        /// day a DLC or a mod adds one — and TFTV adds no ModalType, it PATCHES
        /// <c>GetMissionBriefModal</c> itself, so keying on the METHOD inherits TFTV and DLC content for
        /// free. Anything the game would answer with a mission's own Launch/Cancel is in;
        /// <c>InterceptionBrief</c>/<c>HavenInfiltrateBrief</c> carry no <c>GeoMission</c> and drop out
        /// naturally. The 10 briefs and their 11 <c>*Outcome</c> siblings all ride one state
        /// (<c>UIStateGeoModal</c>) and one raiser (<c>ShowMissionBriefing</c>:1903).</summary>
        internal static bool IsPerPeerAnswerClass(bool hasMission, bool isBrief, bool isOutcome)
            => IsMissionStartConfirmationClass(hasMission, isBrief) || (hasMission && isOutcome);

        /// <summary>PURE (RailCheck L403). THE MISSION-START CONFIRMATION FAMILY, named apart from its
        /// <c>*Outcome</c> sibling — owner decision 2026-08-10: "для каждого своё окно ТОЛЬКО У ОКОН У
        /// КОТОРЫХ ИДЁТ ПОДТВЕРЖДЕНИЕ НАЧАЛА МИССИИ, без блокировки выбора".
        ///
        /// MATCHED ON THE NATIVE RAISER, not on a name: <c>GeoscapeView.ShowMissionBriefing</c>:1903 pushes
        /// a <c>UIStateGeoModal</c> whose <c>ModalType</c> IS <c>GeoscapeView.GetMissionBriefModal(mission)</c>
        /// (:1724) and whose <c>ModalData</c> is the live <c>GeoMission</c>; its Confirm arm is
        /// <c>ModalResultCallback</c>:825 → <c>LaunchMission</c>:1043, i.e. the one native window that asks a
        /// player to confirm ENTERING a mission. The outcome sibling (<c>GetMissionOutcomeModal</c>:1800)
        /// answers the same class but starts nothing, so it is deliberately NOT in here.
        ///
        /// WHAT THE SEPARATION BUYS: this family is the only one that may be answered by several peers at
        /// once without any of them being locked read-only, which means the LAUNCH behind it has to be
        /// idempotent — <see cref="MissionSync.MissionStartAlreadyCommitted"/> and
        /// <see cref="MissionSync.RunNativeMissionOfferAnswer"/>. Every other family keeps shared first-wins.</summary>
        internal static bool IsMissionStartConfirmationClass(bool hasMission, bool isBrief)
            => hasMission && isBrief;

        /// <summary>The live question for <see cref="IsMissionStartConfirmationClass"/>, asked of the game
        /// itself. Never throws into game code: an unanswerable question means "not a start confirmation",
        /// which leaves the caller on the pre-2026-08-10 path.</summary>
        internal static bool IsMissionStartConfirmation(ModalType modalType, object modalData)
        {
            var mission = modalData as GeoMission;
            if (mission == null || BriefModalMethod == null) return false;
            var view = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;
            if (view == null) return false;
            try
            {
                // OUTCOME FIRST, same reason as IsPerPeerAnswer: GetMissionBriefModal LogErrors for a mission
                // type it cannot place, so an outcome-only mission must never reach it.
                if (modalType == view.GetMissionOutcomeModal(mission)) return false;
                return IsMissionStartConfirmationClass(true,
                    modalType == (ModalType)BriefModalMethod.Invoke(view, new object[] { mission }));
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][windows] could not ask the game whether '" + modalType + "' is a mission " +
                               "START confirmation — treating it as NOT one, which leaves the launch on its " +
                               "pre-2026-08-10 path: " + ex);
                return false;
            }
        }

        /// <summary><c>GetMissionBriefModal</c> is private (GeoscapeView.cs:1724); its outcome sibling is
        /// public (:1800). Reflection reaches the PATCHED method, so TFTV's own arm answers here too.</summary>
        private static readonly MethodInfo BriefModalMethod =
            AccessTools.Method(typeof(GeoscapeView), "GetMissionBriefModal", new[] { typeof(GeoMission) });

        private static bool _briefBindLogged;

        /// <summary>The live question, asked of the game itself. Never throws into game code: an
        /// unanswerable question means "not in the class", which is the pre-fix behaviour.</summary>
        internal static bool IsPerPeerAnswer(ModalType modalType, object modalData)
        {
            var mission = modalData as GeoMission;
            if (mission == null) return false;
            var view = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;
            if (view == null) return false;
            if (BriefModalMethod == null)
            {
                if (_briefBindLogged) return false;
                _briefBindLogged = true;
                MpLog.LogError("[MP][windows] GeoscapeView.GetMissionBriefModal did not bind, so the per-peer " +
                               "answer class is EMPTY — one peer declining a mission brief will cancel the " +
                               "mission for every peer again (GeoMission.Cancel:253 nulls Site.ActiveMission)");
                return false;
            }
            try
            {
                // OUTCOME FIRST, and deliberately: it is the public one, and GetMissionBriefModal's own
                // fallthrough LogErrors for a mission type it cannot place — asking it about an outcome-only
                // mission would print the game's error for a question the game never asked.
                if (modalType == view.GetMissionOutcomeModal(mission))
                    return IsPerPeerAnswerClass(true, false, true);
                return IsPerPeerAnswerClass(true, modalType == (ModalType)BriefModalMethod.Invoke(
                                                      view, new object[] { mission }), false);
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][windows] could not ask the game whether '" + modalType + "' is a mission " +
                               "brief/outcome — treating it as NOT per-peer, which is the pre-fix behaviour " +
                               "(one peer's Cancel can then delete the mission for everyone): " + ex);
                return false;
            }
        }

        // Once per TYPE per session: these fire on a queue that runs all game long, and a line the player
        // scrolls past a hundred times is a line nobody reads.
        private static readonly HashSet<Type> _announced = new HashSet<Type>();

        /// <summary>Announce what this queued window means for the OTHER peer — the whole point of the gate.
        /// Silent for a Mirrored or a reviewed LocalOnly kind; loud, once, for a known Gap; louder for a kind
        /// nobody has reviewed at all.</summary>
        internal static void Announce(Type stateType)
        {
            if (stateType == null || !_announced.Add(stateType)) return;
            var rule = RuleFor(stateType);
            if (rule == null)
                MpLog.LogError("[MP][windows] UNDECLARED geoscape window '" + stateType.FullName + "' was queued at " +
                               "GeoscapeViewSwitchQuery.QueryStateSwitch — nothing in GeoWindowCoverage.Declared says " +
                               "whether it should reach the other peer, so it is almost certainly showing on ONE " +
                               "screen only. Declare it (Mirrored / LocalOnly / Gap) with a reason; RailCheck L48 " +
                               "fails on it until someone does");
            else if (rule.Sync == WindowSync.Gap)
                MpLog.LogWarning("[MP][windows] '" + stateType.Name + "' is a KNOWN un-mirrored window — the other " +
                                 "peer does not get it: " + rule.Why);
        }

        private static readonly HashSet<ModalType> _announcedModals = new HashSet<ModalType>();

        /// <summary><see cref="Announce"/>'s second axis: the same question asked of the MODAL kind, because
        /// every modal wears the one <c>UIStateGeoModal</c> type and the state-level announce can therefore
        /// only ever say "modals are handled". Called from BOTH native openers (queued or not) so a new kind
        /// cannot hide behind whichever one it happened to pick.</summary>
        internal static void AnnounceModal(ModalType modal)
        {
            if (!EventPopup.InSession || !_announcedModals.Add(modal)) return;
            var rule = RuleForModal(modal);
            if (rule == null)
                MpLog.LogError("[MP][windows] UNDECLARED modal '" + modal + "' (" + (int)modal + ") was opened at " +
                               "GeoscapeView.OpenModal/OpenModalPersistent — nothing in GeoWindowCoverage" +
                               ".DeclaredModals says whether it should reach the other peer, so it is almost " +
                               "certainly showing on ONE screen only. Declare it (Mirrored / LocalOnly / Gap) with " +
                               "a reason; RailCheck L49 fails on it until someone does");
            else if (rule.Sync == WindowSync.Gap)
                MpLog.LogWarning("[MP][windows] modal '" + modal + "' is a KNOWN un-mirrored window — the other " +
                                 "peer does not get it: " + rule.Why);
        }

        // ─── NO BOUND (§A.6, L524) ──────────────────────────────────────────
        //
        // QueueCap = 64 and TrimQueue lived here and are DELETED. The trim removed from the TAIL, i.e. it
        // dropped the NEWEST pending window — the exact opposite of accumulating what the local player has
        // not yet looked at, and a window a player is never asked about is the defect the cap claimed to
        // prevent. Retention is now one rule and it lives in WindowJournal: an entry is removed when THIS
        // peer reads it, or by an explicit host-minted void. No cap, no trim, no staleness, no LRU. The
        // 4096 line in WindowJournal.Append is a runaway-raiser CANARY: it logs once and keeps appending.
        //
        // The O(n²)-insert / save-payload argument the cap rested on dies with the queue's role: the
        // native pending list is no longer the ordering authority, the journal is, and the journal is
        // never persisted (§A.2b). The inline RailCheck law L82's `bound-*` arms were amputated in the
        // same commit for the same reason — their subject no longer exists.

        /// <summary>Full session teardown: a rejoin should re-announce, so a gap is visible in the log of the
        /// session it actually happened in.</summary>
        public static void Reset() { _announced.Clear(); _announcedModals.Clear(); }
    }

    /// <summary>
    /// The coverage gate itself (law 4c, presentation): a POSTFIX on the one queue every PUSHED geoscape
    /// window passes through. Its jobs are about the queue as a whole and none of them about any single
    /// window kind — it makes the answer to "does the other peer see this?" exist for every kind including
    /// ones that do not exist yet (<see cref="GeoWindowCoverage.Announce"/>), and it is the ONE seam that
    /// mints a journal position and publishes.
    ///
    /// AMENDED 2026-08-15: THIS GATE CHANGES NOTHING ABOUT THE NATIVE QUEUE AGAIN, and that claim is once
    /// more literally true. It was falsified in 2026-08-01 by the tail trim; the trim is deleted (§A.6,
    /// L524) because it dropped the NEWEST pending window, so the gate now only observes and publishes.
    ///
    /// A postfix, and never a prefix: the window must queue exactly as it always did on both peers whatever
    /// the verdict is. Suppressing an un-mirrored window on the host would hide the host's own game from it
    /// to make two screens match, which is the opposite of the fix.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.QueryStateSwitch))]
    internal static class GeoWindowCoverageGate
    {
        private static void Postfix(GeoscapeViewSwitchQuery __instance, GeoscapeViewStateSwitchRequest request)
        {
            if (!EventPopup.InSession) return;   // solo: there is no other peer to be out of sync with
            try
            {
                GeoWindowCoverage.Announce(request?.State?.GetType());
                // THE SINGLE ENTRANCE (§A.1, L520/L521). The host claims the next journal position HERE and
                // nowhere else, then publishes. This is a Harmony postfix on the queue itself, so it is
                // reachable with GeoLevel() == null — creation, queueing and publication never depend on
                // which screen the host is in (§A.4); only DISPLAY is postponed. The mint is handed to the
                // family's own publisher through WindowJournal.TakeHostPending: every publisher is a postfix
                // on the native method that just queued this very window, so it runs later in THIS call
                // stack (see the SetHostPending doc).
                if (GeoModalMirror.HostMayPublish())
                {
                    uint pos = WindowJournal.MintHostPosition();
                    string family = WindowJournal.FamilyOf(request?.State?.GetType());
                    WindowJournal.SetHostPending(pos, family);
                    // The host's own copy: the live request IS the payload on this peer.
                    WindowJournal.Append(pos, family, null);
                }
                // THE NON-MODAL RAISE, at the one queue every pushed window passes: a state that reached here
                // is one the game is interrupting the player with, it carries its own priority and its own
                // live data, and the modal family is already captured at its openers. Host-gated inside.
                GeoModalMirror.HostBroadcastQueued(request);
            }
            catch (Exception ex) { MpLog.LogError("[MP][windows] coverage gate threw: " + ex); }
        }
    }
}
