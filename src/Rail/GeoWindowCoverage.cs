using System;
using System.Collections.Generic;
using HarmonyLib;
using PhoenixPoint.Common.Utils;
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
                Why = "the marketplace is a LOCAL gesture on either peer (MarketplaceAbility.ActivateInternal:43 → " +
                      "GeoscapeView.ToMarketplace:734-738 calls the view directly) and its offer list is not " +
                      "replicated (docs/rail-baseline.txt:14, GeoMarketplace.MarketplaceOptions EXCLUDED) — " +
                      "mirroring it would open a shop over rows the other peer does not have. EventPopup" +
                      ".HostBroadcast declines it and MarketplaceChoiceClientLock blocks a client purchase",
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
                      "(MissionCancelGate) so backing out stays navigation and never deletes a shared mission. " +
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
                Sync = WindowSync.Gap,
                Why = "story + game-over cinematics (GeoscapeView.ToCutsceneState:673, ToGameOverState:664). " +
                      "Addressable in principle — a VideoPlaybackSourceDef is a def GUID (law 2) — but the " +
                      "game-over variant also carries an end-of-playback Action closure that ENDS THE LEVEL, and " +
                      "the story variant blocks the peer for minutes on content it can reach on its own. It does " +
                      "NOT fall out of the modal machinery either: different state, different data, different " +
                      "declaration axis. STILL TERMINAL for the other peers, and the amendment that said " +
                      "otherwise is WITHDRAWN (2026-08-01, same day): a cutscene holds the queue slot until " +
                      "OnCancel:92, so on an idle host it stops the campaign for everybody, and 0xB9's non-modal " +
                      "arm did NOT fix that — it matched on \"is not a modal\", which is true of the host's " +
                      "cutscene and of every peer's own tutorial alike, so it dismissed unrelated windows and was " +
                      "removed. It could not have helped here in any case: this window reaches ONE screen, and a " +
                      "peer cannot close what it does not have. Draining it needs the RAISE half first",
            },
            [typeof(UIStateAssetDeployment)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "\"where does this newly manufactured vehicle / recruited soldier go\" (GeoscapeView" +
                      ".PrepareDeployAsset:1308, self-gated to faction == ViewerFaction). It is a HOST decision on " +
                      "host-owned assets; the placement it produces reaches the client as ordinary value/structural " +
                      "deltas, so mirroring the prompt would ask the client to decide something it cannot apply. " +
                      "AMENDED 2026-08-01: LocalOnly used to imply the window was also HARMLESS to the other peer, " +
                      "and it is not — this state sets no answer of its own but it does hold the queue slot " +
                      "(ExitState:66 is its only way out), so left up on an idle host it blocks every later window " +
                      "and pauses the shared clock. That is a REAL and still-OPEN hole. The same-day amendment " +
                      "claiming 0xB9's advance op closed it is WITHDRAWN: that op's non-modal arm identified a " +
                      "window only by \"is not a modal\", which is equally true of every peer's own tutorial and " +
                      "replenish screen, so it dismissed unrelated windows on other peers and was removed. Since " +
                      "this prompt reaches ONE screen, no peer holds a copy to close and no capture on a peer's " +
                      "own FinishQueriedState could ever have named it — draining it needs a host→all raise",
            },
            [typeof(UIStateReplenish)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "the post-mission replenish screen, raised by the RETURNING peer's own UIStateInitial:127 as " +
                      "it comes back from tactical — per-peer arrival UI over an aircraft that peer just flew; the " +
                      "restocking it performs rides the value rail like any other",
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
                "research completed — raised HOST-side by GeoscapeView.OnFactionResearchCompleted:1975 off the " +
                "faction's own event, which a client's gated sim never fires (research reaches it as 0xAC value " +
                "deltas, not as Research.CompleteResearch), so today it is the single most frequent window in a " +
                "campaign that exists on one screen only. Data = GeoResearchCompleteData{ResearchElement, bool}: " +
                "shipped as the element's faction root ref + its ResearchID, re-fetched LIVE on the client " +
                "(Research.GetResearchById:762) so the renderer's own walk over UnlocksResearches/ManufactureRewards " +
                "answers from this peer's mirror. Callback is local navigation only " +
                "(ResearchCompleteModalHandler:2107 ignores the ModalResult entirely)",
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

            // ── GAP: should reach the other peer, and does not yet ──
            Modal(d, WindowSync.Gap,
                "mission BRIEF — raised host-side at GeoscapeView.cs:1903 (OpenModalPersistent). The DATA is " +
                "already there: the mission is site.ActiveMission and the rail structurally creates it on the " +
                "client (docs/rail-baseline.txt, GeoSite twin table `Descend ActiveMission`). The BUTTONS are what " +
                "is missing — ModalResultCallback:798 maps Confirm to LaunchMission and Cancel to mission.Cancel(), " +
                "both host-authoritative — and behind them a decision this rail has NOW made, but only half of. " +
                "NARROWED 2026-08-01: the squad INTENT the previous wording named as this entry's prerequisite " +
                "SHIPPED as MissionSync (surface 0xB8), so Confirm no longer has an undefined meaning — " +
                "GeoMission.Launch is captured block-first on a client and the host commits it, and Cancel is " +
                "gated (MissionCancelGate). The ANSWER half (WindowQueueSync's 0xB9 advance op) carries a " +
                "clicked ModalResult back and the host runs UIStateGeoModal.FinishDialog:82 natively — but " +
                "CORRECTED 2026-08-01, same day: that op reaches only windows the HOST RAISED to the answering " +
                "peer, i.e. the Mirrored notification modals, and it never reached a brief. It cannot: the " +
                "identity both peers agree on is a re-description of the 0xB7 payload, a brief's modalData is a " +
                "live GeoMission that no DataShape describes, and the peer has no copy to click in the first " +
                "place. The claim that the answer half was done here was written on an arbiter that matched " +
                "TYPE + ModalType, which is what let one peer's window dismiss another's. So the hole is " +
                "unchanged and singular — a DataShape for the mission brief on 0xB7 — and until it exists a " +
                "client starts the mission from the site and aircraft UI instead. Superseded reasoning, kept " +
                "for the record: " +
                "\"what is still missing is only this WINDOW's own two halves: the modal intent op that " +
                "carries the clicked ModalResult back, and the host→all hide keyed on " +
                "GeoscapeView.ModalClosed:793\"; and before that: " +
                "Confirm → LaunchMission:1043-1050 walks straight into the DEPLOYMENT screen, and " +
                "deployment is host-only for an entry-mechanism reason law 5's rewrite did not touch: the host " +
                "builds the battle and ships it as a save, so a client's squad pick has nowhere to be committed. " +
                "So \"the client clicked Launch\" still has no defined meaning, and shipping this read-only would " +
                "put a live LAUNCH MISSION button on the client that silently does nothing, which is worse than " +
                "the gap. Needs a squad INTENT (client picks → host commits) before the modal intent op + the " +
                "host→all hide keyed on GeoscapeView.ModalClosed:793 are worth building",
                ModalType.GeoHavenAttackBrief, ModalType.GeoAlienBaseBrief, ModalType.GeoScavengeBrief,
                ModalType.GeoPhoenixBaseDefenseBrief, ModalType.GeoAmbushBrief,
                ModalType.GeoPhoenixBaseInfestationBrief, ModalType.AncientSiteAttackBrief,
                ModalType.AncientSiteDefenceBrief, ModalType.BehemothAttackBrief, ModalType.InfestedHavenBrief);
            Modal(d, WindowSync.Gap,
                "mission OUTCOME — TWO raisers with OPPOSITE verdicts under one ModalType, which is why this stays " +
                "a declared hole instead of a half-answer. UIStateInitial.cs:112 raises it on the peer RETURNING " +
                "from tactical off its own _params.LastMission — per-peer arrival UI that fires natively on every " +
                "peer that played the mission, so mirroring THAT would show the window twice (the reason " +
                "UIStateReplenish is LocalOnly). But GeoscapeView.OnSiteMissionCancelled:1930 raises the " +
                "base-defence / ancient-site variants off a HOST sim event alone, which no client ever fires — a " +
                "real hole. Splitting them needs a RAISER-aware seam; a per-ModalType verdict cannot express it",
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
                "a faction soldier offers to join — HavenMissionUtil.cs:59, Confirm→reward.Apply(faction, site, " +
                "aircraft), a host-authoritative GRANT. 2026-08-01: 0xB9's advance op is the shape the answer " +
                "will take — the host runs its OWN DialogCallback, so the grant stays host-side exactly as it " +
                "must — but it does NOT reach this window today, and the same-day claim that it did is " +
                "withdrawn: the op only advances a window the host RAISED to the answering peer, and the RAISE " +
                "(a 0xB7 DataShape for this modalData) is precisely what is missing. Ordering matters and was " +
                "got backwards: the raise is not the leftover, it is the prerequisite. Once the host accepts, " +
                "the soldier itself already reaches the other peer as a structural create",
                ModalType.FactionSoldierJoin);
            Modal(d, WindowSync.Gap,
                "NO caller anywhere in the shipped assembly (full sweep 2026-07-31) — declared so this table stays " +
                "TOTAL over the enum. Gap rather than LocalOnly on purpose: nobody has reviewed what these should " +
                "do because nothing raises them, so if a patch, a DLC or a mod ever does, the runtime announce is " +
                "LOUD at the moment it happens instead of silently deciding it does not matter",
                ModalType.LoadPrompt, ModalType.SiteEncounter, ModalType.SiteRecruit, ModalType.ResourcePayment);

            // ── LOCALONLY: correct NOT to replicate ──
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
                Debug.LogError("[MP][windows] UNDECLARED geoscape window '" + stateType.FullName + "' was queued at " +
                               "GeoscapeViewSwitchQuery.QueryStateSwitch — nothing in GeoWindowCoverage.Declared says " +
                               "whether it should reach the other peer, so it is almost certainly showing on ONE " +
                               "screen only. Declare it (Mirrored / LocalOnly / Gap) with a reason; RailCheck L48 " +
                               "fails on it until someone does");
            else if (rule.Sync == WindowSync.Gap)
                Debug.LogWarning("[MP][windows] '" + stateType.Name + "' is a KNOWN un-mirrored window — the other " +
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
                Debug.LogError("[MP][windows] UNDECLARED modal '" + modal + "' (" + (int)modal + ") was opened at " +
                               "GeoscapeView.OpenModal/OpenModalPersistent — nothing in GeoWindowCoverage" +
                               ".DeclaredModals says whether it should reach the other peer, so it is almost " +
                               "certainly showing on ONE screen only. Declare it (Mirrored / LocalOnly / Gap) with " +
                               "a reason; RailCheck L49 fails on it until someone does");
            else if (rule.Sync == WindowSync.Gap)
                Debug.LogWarning("[MP][windows] modal '" + modal + "' is a KNOWN un-mirrored window — the other " +
                                 "peer does not get it: " + rule.Why);
        }

        // ─── The BOUND: a queue nobody drains must not grow without limit ───

        /// <summary>How many pending windows the queue may hold. The game shows ONE at a time and a player
        /// clears a handful a minute, so anything past this is the runaway, not a backlog.</summary>
        private const int QueueCap = 64;

        private static readonly System.Reflection.FieldInfo RequestsField =
            HarmonyLib.AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_viewStateSwitchRequests"); // :15

        private static int _dropped;

        /// <summary>
        /// Cap the pending-window list, because on this rail an UNDRAINED queue is the normal state, not a
        /// pathology: <c>ProcessQueriedStateSwitch</c>:58-63 dequeues only while
        /// <c>_currentStateSwitchRequest == null</c> and only a peer's click clears that
        /// (<c>FinishCurrentStateSwitch</c>:116), so an idle host accumulates every window its sim raises for
        /// as long as it stays idle. <see cref="WindowQueueSync"/> lets another peer drain the ONE family the
        /// host raised to it (the Mirrored modals); for everything else — its own briefs, its own cutscene,
        /// its own asset-deployment prompt — nobody can, so this is what keeps the game alive meanwhile.
        ///
        /// Unbounded growth is not merely memory. <c>QueryStateSwitch</c>:77-82 does an O(n)
        /// <c>FindIndex</c> plus an O(n) <c>Insert</c> per push — O(n²) over a session — and
        /// <c>GetRestorableData</c>:25-37 walks the WHOLE list on every save, so every queued
        /// <c>UIStateGeoModal</c> (it IS an <c>IGeoscapeRestorableViewState</c>) rides into every autosave
        /// and, since join and reconnect ARE a native save transfer (law 1), into every join. Capping n is
        /// the one fix for all three: the scan, the insert and the save payload all stop growing.
        ///
        /// The TAIL is what goes. The list is priority-DESCENDING by construction (insert before the first
        /// strictly-lower priority) and <c>GetNextQueriedStateSwitch</c>:111-113 always takes index 0, so the
        /// tail is by the game's own ordering the least important thing pending.
        ///
        /// LOUD, never silent — a dropped window is a window a player will never be asked about, and a cap
        /// nobody can see in the log is indistinguishable from the bug it prevents. The first drop and every
        /// 32nd after it are errors (a runaway pushes thousands; one line each would cost more than it tells).
        /// </summary>
        internal static void TrimQueue(GeoscapeViewSwitchQuery query)
        {
            var list = RequestsField?.GetValue(query) as System.Collections.IList;
            while (list != null && list.Count > QueueCap)
            {
                int last = list.Count - 1;
                var dropped = (list[last] as GeoscapeViewStateSwitchRequest)?.State;
                list.RemoveAt(last);
                _dropped++;
                if (_dropped == 1 || _dropped % 32 == 0)
                    Debug.LogError("[MP][windows] window queue OVERFLOW — dropped the lowest-priority pending " +
                                   "window '" + (dropped == null ? "<null>" : dropped.GetType().Name) + "' to hold " +
                                   "the queue at " + QueueCap + " (" + _dropped + " dropped this session). The queue " +
                                   "only drains when a peer answers the current window (ProcessQueriedStateSwitch:60), " +
                                   "so this peer has not answered one in a very long time — every window past the cap " +
                                   "is LOST, and the uncapped list is also an O(n²) insert and a payload in every " +
                                   "save and every join transfer");
            }
        }

        /// <summary>Full session teardown: a rejoin should re-announce, so a gap is visible in the log of the
        /// session it actually happened in.</summary>
        public static void Reset() { _announced.Clear(); _announcedModals.Clear(); _dropped = 0; }
    }

    /// <summary>
    /// The coverage gate itself (law 4c, presentation): a POSTFIX on the one queue every PUSHED geoscape
    /// window passes through. Two jobs, both of them about the queue as a whole and neither of them about
    /// any single window kind — it makes the answer to "does the other peer see this?" exist for every kind
    /// including ones that do not exist yet (<see cref="GeoWindowCoverage.Announce"/>), and it holds the
    /// pending list to its bound (<see cref="GeoWindowCoverage.TrimQueue"/>).
    ///
    /// AMENDED 2026-08-01: this used to say "it changes nothing", and the trim makes that false, so the
    /// claim is replaced rather than left standing. What survives is the part that was load-bearing — the
    /// gate never SUPPRESSES a window and never changes which one is shown next: the trim only ever removes
    /// from the TAIL, i.e. below everything the queue would reach first, and only once the list is past a
    /// bound no live game reaches.
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
                // Same postfix, same chokepoint, and the same reason it is a postfix: the native insert must
                // happen exactly as it always did, and only THEN is the list trimmed back to its bound.
                GeoWindowCoverage.TrimQueue(__instance);
            }
            catch (Exception ex) { Debug.LogError("[MP][windows] coverage gate threw: " + ex); }
        }
    }
}
