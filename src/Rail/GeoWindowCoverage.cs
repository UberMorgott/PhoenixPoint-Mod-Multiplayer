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
                      "the host's own queue priority; answers ride back as the 0xB4 intent",
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
                Why = "tactical deployment is law 5 quarantine — the mission reaches the other peer through the " +
                      "tactical deploy channel, not as a geoscape window, and the roster picked here is the " +
                      "deploying peer's own",
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
                      "so no button on it can run game logic",
            },
            [typeof(UIStateGeoCutscene)] = new WindowRule
            {
                Sync = WindowSync.Gap,
                Why = "story + game-over cinematics (GeoscapeView.ToCutsceneState:673, ToGameOverState:664). " +
                      "Addressable in principle — a VideoPlaybackSourceDef is a def GUID (law 2) — but the " +
                      "game-over variant also carries an end-of-playback Action closure that ENDS THE LEVEL, and " +
                      "the story variant blocks the peer for minutes on content it can reach on its own. It does " +
                      "NOT fall out of the modal machinery either: different state, different data, different " +
                      "declaration axis. Still deferred, now on its own",
            },
            [typeof(UIStateAssetDeployment)] = new WindowRule
            {
                Sync = WindowSync.LocalOnly,
                Why = "\"where does this newly manufactured vehicle / recruited soldier go\" (GeoscapeView" +
                      ".PrepareDeployAsset:1308, self-gated to faction == ViewerFaction). It is a HOST decision on " +
                      "host-owned assets; the placement it produces reaches the client as ordinary value/structural " +
                      "deltas, so mirroring the prompt would ask the client to decide something it cannot apply",
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
                "both host-authoritative — and behind them a decision this rail has NOT made: LaunchMission walks " +
                "into deployment, and UIStateRosterDeployment is LocalOnly under law 5 quarantine, so \"the client " +
                "clicked Launch\" has no defined meaning yet. Shipping it read-only would put a live LAUNCH MISSION " +
                "button on the client that silently does nothing, which is worse than the gap. Needs the intent " +
                "op + a host→all hide keyed on GeoscapeView.ModalClosed:793 — the next window work",
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
                "no rail identity at all. Air combat needs its own replication before its windows can mean anything",
                ModalType.InterceptionBrief, ModalType.InterceptionOutcome);
            Modal(d, WindowSync.Gap,
                "alien intelligence brief — AlienIntelligenceBriefData carries its OWN DialogCallback (invoked at " +
                "GeoscapeView.cs:2017) plus a live GeoscapeViewContext the raiser injects (:2011) and a " +
                "List<RewardDiplomacyChange>; the data object IS the closure. Describable in principle (faction + " +
                "research ids + the diplomacy deltas), not described yet",
                ModalType.AlienResearchBrief);
            Modal(d, WindowSync.Gap,
                "a faction soldier offers to join — HavenMissionUtil.cs:59, Confirm→reward.Apply(faction, site, " +
                "aircraft), a host-authoritative GRANT. Needs the same intent the brief family does; once the host " +
                "accepts, the soldier itself already reaches the other peer as a structural create",
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

        /// <summary>Full session teardown: a rejoin should re-announce, so a gap is visible in the log of the
        /// session it actually happened in.</summary>
        public static void Reset() { _announced.Clear(); _announcedModals.Clear(); }
    }

    /// <summary>
    /// The coverage gate itself (law 4c, presentation): a POSTFIX on the one queue every PUSHED geoscape
    /// window passes through. It changes nothing — it only makes the answer to "does the other peer see
    /// this?" exist for every window kind, including ones that do not exist yet.
    ///
    /// A postfix, and never a prefix: the window must queue exactly as it always did on both peers whatever
    /// the verdict is. Suppressing an un-mirrored window on the host would hide the host's own game from it
    /// to make two screens match, which is the opposite of the fix.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeViewSwitchQuery), nameof(GeoscapeViewSwitchQuery.QueryStateSwitch))]
    internal static class GeoWindowCoverageGate
    {
        private static void Postfix(GeoscapeViewStateSwitchRequest request)
        {
            if (!EventPopup.InSession) return;   // solo: there is no other peer to be out of sync with
            try { GeoWindowCoverage.Announce(request?.State?.GetType()); }
            catch (Exception ex) { Debug.LogError("[MP][windows] coverage gate threw: " + ex); }
        }
    }
}
