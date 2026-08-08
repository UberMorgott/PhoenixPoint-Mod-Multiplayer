using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Serialization.General;
using HarmonyLib;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.Roster;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE DEPLOYMENT SCREEN ASKS THE GAME'S OWN QUESTION WHEN IT OPENS, NOT WHEN IT WAS QUEUED.
    ///
    /// THE REPORT (2026-08-07, after an ambush): the host reached the soldier-deployment screen and THERE WAS
    /// NO START MISSION BUTTON. Not hidden — <c>UIModuleDeploymentMissionBriefing</c> never hides it, the only
    /// write is <c>DeployButton.SetInteractable</c> — DEAD, and with nothing enrolled to make it live again.
    ///
    /// THE MECHANISM IS THE SAME RACE L164 RECORDS ONE SCREEN OVER, and it is not an analogy: the gate is
    /// asked before the state it reads has arrived. <c>UIStateRosterDeployment</c>'s CONSTRUCTOR (:74-82)
    /// computes BOTH inputs the button hangs on —
    ///   <c>_characterContainers = _mission.GetDeploymentSources(forFaction, _initialContainer)</c>
    ///   <c>_selectedDeployment  = _mission.GetDefaultDeploymentSetup(deployment).ToList()</c>
    /// — and the constructor runs inside <c>GeoscapeView.ToDeploymentState</c>:595, i.e. AT QUEUE TIME.
    /// <c>EnterState</c> then feeds those two frozen lists to <c>_geoRosterModule.Init</c> and to
    /// <c>SetUpInitialDeployment</c> → <c>CheckForDeployment</c>:369-378, whose first term is
    /// <c>squad.Any()</c>. An empty snapshot is a dead button over an empty roster: nothing to tick, so
    /// nothing can ever make it live.
    ///
    /// VANILLA NEVER PAYS FOR THIS because the two moments are the same frame — nothing is queued in front of
    /// the squad screen in a solo game, so <c>ProcessQueriedStateSwitch</c> serves it on the next Update. In
    /// co-op the gap is real and it just got LONGER on purpose: <c>WindowOrder.HoldsForOpenScreen</c> now
    /// holds this very screen while the local player is inside a screen he opened (L175), so the snapshot can
    /// be minutes old by the time anyone looks at it. Landing the hold without this would have traded one
    /// defect for a worse one.
    ///
    /// SO THE SNAPSHOT IS RE-TAKEN AT <c>EnterState</c>, FROM THE GAME'S OWN METHODS. Not a local
    /// re-implementation of "who can deploy" — <c>GetDeploymentSources</c> and <c>GetDefaultDeploymentSetup</c>
    /// are called directly, the same discipline <c>a1c11dd</c> imposed on the resupply gate after factoring
    /// its question into a helper drifted it. Both lists are refilled IN PLACE, because
    /// <c>OnEnrollmentChanged</c>:363-365 mutates <c>_selectedDeployment</c> through the reference it was
    /// handed and a swapped list object would silently stop being the one the screen edits.
    ///
    /// AND IT SAYS WHAT IT SAW. The line below prints the gate's own numbers — containers, soldiers, default
    /// squad, cap — exactly as the replenish arming postfix now does, because "the button was dead" is
    /// equally consistent with an empty roster, a zero cap and a state that never opened, and the next reader
    /// must not have to guess which. Co-op only; solo keeps vanilla's own snapshot untouched.
    /// </summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "EnterState")]
    internal static class DeploymentRosterRefresh
    {
        private const BindingFlags All = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static readonly FieldInfo MissionField = AccessTools.Field(typeof(UIStateRosterDeployment), "_mission");
        internal static readonly FieldInfo ContainersField = AccessTools.Field(typeof(UIStateRosterDeployment), "_characterContainers");
        internal static readonly FieldInfo InitialContainerField = AccessTools.Field(typeof(UIStateRosterDeployment), "_initialContainer");
        internal static readonly FieldInfo SelectedField = AccessTools.Field(typeof(UIStateRosterDeployment), "_selectedDeployment");

        private static bool _bindLogged;

        /// <summary>PURE (RailCheck L176). Is a re-ask WORTH LOGGING as a change? Only the shape question —
        /// the re-ask itself is unconditional, because "the queued answer happens to equal the live one" is
        /// not knowable without asking.</summary>
        internal static bool SnapshotWentStale(int queuedSquad, int liveSquad) => queuedSquad != liveSquad;

        /// <summary>PURE (RailCheck L354). MAY THIS SQUAD SCREEN BE SERVED AT ALL? Both numbers, because the
        /// live log shows both shapes: host 22:16:38 opened it with 0 containers / 0 soldiers (the aircraft
        /// had already flown to another site), client 00:19:08 and 00:21:03 with 1 container / 0 soldiers
        /// (the aircraft was in transit and its roster went with it).
        ///
        /// An empty screen is not a cosmetic defect: <c>CheckForDeployment</c>:371 gates the START MISSION
        /// button on <c>squad.Any()</c>, so with nothing to enrol the button is DEAD and there is no gesture
        /// that can ever make it live — the player is parked in a screen whose only exit is Back. The same
        /// question governs the BRIEF that opens this screen, which is why it lives here and not inside the
        /// EnterState prefix: a brief for a mission this peer can no longer deploy to is the same window.
        ///
        /// The question is asked of <c>GeoMission.GetDeploymentSources</c> and NOT of "is there an aircraft
        /// on the site" — a <c>GeoPhoenixBaseDefenseMission</c> deploys from the SITE (GeoMission.cs:1493-1497,
        /// <c>Site.Owner == faction</c>) and has no aircraft to lose, and <c>OnSiteMissionStarted</c>:1706-1712
        /// raises its brief with a null vehicle on purpose.</summary>
        internal static bool IsServable(int containers, int soldiers) => containers > 0 && soldiers > 0;

        /// <summary>The two numbers <see cref="IsServable"/> wants, taken from the GAME's own method on THIS
        /// peer's live graph. Returns false when they cannot be taken at all (no mission, no viewer faction),
        /// which is deliberately NOT "unservable": refusing on a question we could not ask would close
        /// screens for a reason nobody could read.</summary>
        internal static bool TryCount(GeoMission mission, IGeoCharacterContainer priority,
                                      out List<IGeoCharacterContainer> sources, out List<GeoCharacter> pool)
        {
            sources = null;
            pool = null;
            var faction = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.ViewerFaction;
            if (mission == null || mission.Site == null || faction == null) return false;
            sources = mission.GetDeploymentSources(faction, priority) ?? new List<IGeoCharacterContainer>();
            pool = sources.SelectMany(s => s == null
                                               ? Enumerable.Empty<GeoCharacter>()
                                               : (s.GetAllCharacters() ?? Enumerable.Empty<GeoCharacter>()))
                          .ToList();
            return true;
        }

        private static void Prefix(UIStateRosterDeployment __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;   // solo: queue and entry are one frame
            try
            {
                if (MissionField == null || ContainersField == null || InitialContainerField == null ||
                    SelectedField == null)
                {
                    if (_bindLogged) return;
                    _bindLogged = true;
                    Debug.LogError("[MP][deploy] UIStateRosterDeployment fields did not bind — the squad screen " +
                                   "keeps the roster it snapshotted when it was QUEUED, so a peer that opens it " +
                                   "after another peer's state arrived can find the START MISSION button dead " +
                                   "over an empty roster and no way to enrol anybody");
                    return;
                }

                var mission = MissionField.GetValue(__instance) as GeoMission;
                var faction = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.ViewerFaction;
                if (mission == null || faction == null) return;

                var containers = ContainersField.GetValue(__instance) as List<IGeoCharacterContainer>;
                var selected = SelectedField.GetValue(__instance) as List<GeoCharacter>;
                if (containers == null || selected == null) return;

                // THE GAME'S OWN TWO QUESTIONS, asked here and not re-implemented (see the class doc).
                var priority = InitialContainerField.GetValue(__instance) as IGeoCharacterContainer;
                // CALLED HERE, INLINE, and not through TryCount below: RailCheck L176 asserts THIS method
                // reaches GeoMission.GetDeploymentSources itself, because factoring the question into a helper
                // is the drift a1c11dd already paid for once on the resupply gate.
                var sources = mission.GetDeploymentSources(faction, priority) ?? new List<IGeoCharacterContainer>();
                var pool = sources.SelectMany(s => s == null
                                                       ? Enumerable.Empty<GeoCharacter>()
                                                       : (s.GetAllCharacters() ?? Enumerable.Empty<GeoCharacter>()))
                                  .ToList();
                var live = (mission.GetDefaultDeploymentSetup(pool) ?? Enumerable.Empty<GeoCharacter>()).ToList();

                int wasSquad = selected.Count, wasContainers = containers.Count;

                containers.Clear();
                containers.AddRange(sources);
                selected.Clear();
                selected.AddRange(live);

                int cap = mission.MissionDef == null ? 0 : mission.MissionDef.MaxPlayerUnits;
                Debug.Log("[MP][deploy] squad screen opening for " +
                          (IdentityResolver.RootRef(mission.Site) ?? "S#?") + " — the game's own sources say " +
                          sources.Count + " container(s), " + pool.Count + " soldier(s), default squad " +
                          live.Count + " of max " + cap + "; the snapshot taken when this screen was QUEUED " +
                          "said " + wasContainers + "/" + wasSquad +
                          (SnapshotWentStale(wasSquad, live.Count) ? " — IT WENT STALE and has been re-asked"
                                                                   : " — unchanged") +
                          ". A default squad of 0 is what leaves DeployButton.SetInteractable(false) with " +
                          "nothing to enrol (CheckForDeployment:372).");
            }
            catch (Exception ex)
            {
                // Presentation seam (P4c/L158): never block, never throw into game code. Failing here leaves
                // the screen exactly as vanilla built it, which is the pre-fix behaviour and not a worse one.
                Debug.LogError("[MP][deploy] squad-screen re-ask failed — the screen keeps its queue-time " +
                               "roster snapshot: " + ex);
            }
        }

        /// <summary>A SCREEN WITH NOTHING IN IT IS NOT SERVED — it is closed again, in the same frame.
        ///
        /// A POSTFIX and not a bail in the prefix above: refusing <c>EnterState</c> leaves the state CURRENT
        /// but half-built, and its own <c>ExitState</c>:194 then walks a null <c>_deploymentItems</c>. Letting
        /// the game build the screen and closing it through its own door costs one frame and cannot wedge the
        /// queue. <c>FinishQueriedState</c> + <c>ResetViewState</c> is <c>ToPreviousScreen</c>:259-267 MINUS
        /// its first line, <c>_mission.Cancel()</c> — which is the whole point: the mission is not cancelled,
        /// this peer simply has nothing to deploy to it.</summary>
        private static void Postfix(UIStateRosterDeployment __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return;
            try
            {
                var mission = MissionField?.GetValue(__instance) as GeoMission;
                var priority = InitialContainerField?.GetValue(__instance) as IGeoCharacterContainer;
                if (!TryCount(mission, priority, out var sources, out var pool)) return;
                if (IsServable(sources.Count, pool.Count)) return;
                DeploymentWindowClose.CloseCurrent(
                    "the squad screen for " + (IdentityResolver.RootRef(mission.Site) ?? "S#?") + " has " +
                    sources.Count + " container(s) and " + pool.Count + " soldier(s) — there is nothing to " +
                    "enrol, so START MISSION is dead by construction (CheckForDeployment:371 squad.Any()) and " +
                    "no click can revive it. The aircraft that backed this screen left the site while the " +
                    "screen was QUEUED. The mission is untouched: fly an aircraft back and open it again");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][deploy] servability check threw — the squad screen stays open as it was " +
                               "built, which is the pre-fix behaviour: " + ex);
            }
        }
    }

    /// <summary>
    /// THE WINDOW DIES WITH THE AIRCRAFT THAT BACKED IT — current or queued, on every peer.
    ///
    /// FOUR OBSERVED FAILURES, worse than "the window stays open": the squad screen opened EMPTY. Host
    /// 22:04:10 <c>SKIP re-enter of queued window UIStateRosterDeployment</c>; host 22:16:38 opened with 0
    /// containers / 0 soldiers with the aircraft already at another site; client 00:19:08 and 00:21:03 opened
    /// with 1 container / 0 soldiers while the aircraft was in transit. In co-op a window can sit QUEUED for
    /// minutes (<c>WindowOrder.HoldsForOpenScreen</c>), so the aircraft leaving between the queue and the
    /// serve is ordinary, not a race.
    ///
    /// ONE QUESTION FOR BOTH WINDOWS, and it is the game's own: <see cref="DeploymentRosterRefresh.IsServable"/>
    /// over <c>GeoMission.GetDeploymentSources</c>. A brief is the screen's own front door
    /// (<c>ModalResultCallback</c>:825 → <c>LaunchMission</c>:1043 → <c>ToDeploymentState</c>), so a brief for
    /// a mission this peer can no longer deploy to is exactly as dead as the screen behind it.
    ///
    /// MULTI-AIRCRAFT COMES FREE, because the screen is backed by a SET: <c>_characterContainers</c>
    /// (UIStateRosterDeployment.cs:26) is filled from <c>GetDeploymentSources</c>, which is priority container
    /// + <c>Site.Vehicles.Where(Owner == faction)</c> + the Site (GeoMission.cs:1477-1499). Re-asking it on an
    /// OPEN screen therefore makes a departing aircraft and its soldiers vanish from that screen while the
    /// rest stay — and only the LAST departure empties the set and closes it.
    ///
    /// NOT <c>ToPreviousScreen</c>, ever: its first line is <c>_mission.Cancel()</c> (:258), which would
    /// cancel the mission for everybody — the very bug L351 is about. And through
    /// <c>FinishQueriedState</c>:2164 rather than by poking the stack, or <c>L93</c> arm C turns red.
    /// </summary>
    internal static class DeploymentWindowClose
    {
        private const BindingFlags All = BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo ItemsField = AccessTools.Field(typeof(UIStateRosterDeployment), "_deploymentItems");
        private static readonly FieldInfo FilterField = AccessTools.Field(typeof(UIStateRosterDeployment), "_preferableFilterMode");
        private static readonly MethodInfo SetUpInitial = AccessTools.Method(typeof(UIStateRosterDeployment), "SetUpInitialDeployment");
        private static readonly MethodInfo OnEnrollment = AccessTools.Method(typeof(UIStateRosterDeployment), "OnEnrollmentChanged");
        private static readonly MethodInfo ContextGetter = AccessTools.PropertyGetter(typeof(GeoscapeViewState), "Context");

        private static GeoscapeView View() =>
            GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View;

        /// <summary>Close whatever the game's queue is currently showing, through the game's own two calls.
        /// <c>ResetViewState</c> also clears <c>SetUiInDeploymentMode</c> (GeoscapeView.cs:414-416), which
        /// <c>ToDeploymentState</c>:591 set and nothing else would put back.</summary>
        internal static void CloseCurrent(string why, bool resetViewState = true)
        {
            var view = View();
            if (view == null) return;
            Debug.Log("[MP][deploy] closing the current window — " + why);
            // A MODAL closes with FinishQueriedState alone — that is its own OnCancel:96-99. The squad SCREEN
            // additionally needs ResetViewState, which is what ToPreviousScreen:259-261 does and the only
            // writer that clears SetUiInDeploymentMode (GeoscapeView.cs:414-416).
            if (resetViewState) view.ResetViewState();
            view.FinishQueriedState();
        }

        /// <summary>THE BRIEF HALF, run from the <c>UIStateGeoModal</c> repaint entry. A mission brief whose
        /// last deployment container is gone is a window whose only button leads to a screen that cannot be
        /// served — <see cref="MissionBehind"/> keeps this to the per-peer brief/outcome class, so no other
        /// modal in the family can be closed by it.</summary>
        internal static void CloseDepartedBrief(GeoscapeViewState s)
        {
            var mission = MissionBehind(s);
            if (mission == null || Servable(mission)) return;
            CloseCurrent("the mission brief for " + (IdentityResolver.RootRef(mission.Site) ?? "S#?") +
                         " has no deployment container left on this peer — the aircraft it was raised for " +
                         "left the site, so its Confirm would open an empty squad screen. The mission is NOT " +
                         "cancelled (that would be one peer deciding for everyone, L351)", false);
        }

        /// <summary>THE QUEUED HALF. A window that is not on screen yet cannot be closed — it is DROPPED from
        /// the game's own pending list, exactly as <c>WindowQueueSync.AnswerQueued</c> edits it. Without this,
        /// an unservable screen is merely deferred: it opens (empty) the moment the player leaves whatever
        /// screen was holding it.</summary>
        internal static void DropUnservableQueued()
        {
            var view = View();
            var query = WindowQueueSync.SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (query == null || WindowOrder.RequestsField == null) return;
            if (!(WindowOrder.RequestsField.GetValue(query) is IList<GeoscapeViewStateSwitchRequest> pending)) return;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var mission = MissionBehind(pending[i]?.State);
                if (mission == null || Servable(mission)) continue;
                Debug.Log("[MP][deploy] DROPPED the queued " + pending[i].State.GetType().Name + " for " +
                          (IdentityResolver.RootRef(mission.Site) ?? "S#?") + " — this peer has no container " +
                          "left to deploy from (the aircraft left the site while the window waited in the " +
                          "queue), so serving it later would open an empty screen with a dead START MISSION " +
                          "button. The mission itself is untouched");
                pending.RemoveAt(i);
            }
        }

        /// <summary>The mission a queued/open window is ABOUT, or null for a window this rule does not own.
        /// The modal arm is deliberately the per-peer class and not "any modal holding a mission": that class
        /// is the brief/outcome family the deployment flow runs through (GeoWindowCoverage.IsPerPeerAnswer).</summary>
        internal static GeoMission MissionBehind(object state)
        {
            if (state is UIStateRosterDeployment dep) return dep.Mission;
            return state is UIStateGeoModal modal && GeoWindowCoverage.IsPerPeerAnswer(modal.ModalType, modal.ModalData)
                       ? modal.ModalData as GeoMission
                       : null;
        }

        internal static bool Servable(GeoMission mission) =>
            !DeploymentRosterRefresh.TryCount(mission, null, out var sources, out var pool) ||
            DeploymentRosterRefresh.IsServable(sources.Count, pool.Count);

        /// <summary>THE OPEN SCREEN, re-asked (<c>UiNativeRepaint.Table</c> entry). Reached at
        /// <c>OpenUiRepaint</c>:554, BEFORE the <c>PauseHold.IsCurrentQueuedWindow</c> skip at :586 — which is
        /// why a table entry is the only repaint this screen can ever have: it holds the current queued slot
        /// (priority int.MaxValue, GeoscapeView.cs:592-600) so the fallback Exit+Enter declines it.
        ///
        /// The native tail is the game's own, in the game's own order: <c>_geoRosterModule.Init</c> (:90),
        /// rebuild <c>_deploymentItems</c> off the live slots (:118-120), <c>SetUpInitialDeployment</c> (:121
        /// → <c>CheckForDeployment</c>:369-379).
        ///
        /// <c>_selectedDeployment</c> is PRUNED, not regenerated. <c>GetDefaultDeploymentSetup</c> would be
        /// the game's own answer but it is the answer to a different question — "who would we pick if nobody
        /// had picked yet" — and running it once per rail batch would throw away the local player's own
        /// enrolment every time any peer moved anything. Pruning keeps exactly the picks that still have a
        /// soldier behind them, which is what a departing aircraft actually changes. Same list OBJECT
        /// throughout: <c>OnEnrollmentChanged</c>:361-364 mutates it through the reference it was handed.</summary>
        internal static bool RepaintDeploymentScreen(GeoscapeViewState s, GeoscapeView view)
        {
            var state = s as UIStateRosterDeployment;
            if (state == null || ItemsField == null || SetUpInitial == null || ContextGetter == null) return false;
            var mission = state.Mission;
            var priority = DeploymentRosterRefresh.InitialContainerField?.GetValue(state) as IGeoCharacterContainer;
            if (!DeploymentRosterRefresh.TryCount(mission, priority, out var sources, out var pool)) return false;

            var containers = DeploymentRosterRefresh.ContainersField?.GetValue(state) as List<IGeoCharacterContainer>;
            var selected = DeploymentRosterRefresh.SelectedField?.GetValue(state) as List<GeoCharacter>;
            if (containers == null || selected == null) return false;

            if (!DeploymentRosterRefresh.IsServable(sources.Count, pool.Count))
            {
                CloseCurrent("the last container backing the open squad screen for " +
                             (IdentityResolver.RootRef(mission.Site) ?? "S#?") + " is gone (" + sources.Count +
                             " container(s), " + pool.Count + " soldier(s)) — the aircraft left the site. The " +
                             "mission is NOT cancelled; this peer simply has nothing to send to it");
                return true;
            }

            containers.Clear();
            containers.AddRange(sources);
            selected.RemoveAll(c => c == null || !pool.Contains(c));

            var ctx = ContextGetter.Invoke(state, null) as GeoscapeViewContext;
            var roster = view?.GeoscapeModules?.GeneralPersonelRosterModule;
            if (ctx == null || roster == null) return false;
            roster.Init(ctx, containers, priority, (GeoRosterFilterMode)(FilterField?.GetValue(state) ??
                                                                         GeoRosterFilterMode.Deployment));
            var items = (roster.Slots ?? Enumerable.Empty<GeoRosterItem>())
                        .Where(x => x != null && x.gameObject.activeSelf)
                        .Select(x => x.GetComponent<GeoRosterDeploymentItem>())
                        .Where(x => x != null).ToList();
            // EnterState:123-126 subscribes the screen's own handler to the items it built; a slot the re-Init
            // just activated has none, and its checkbox would silently do nothing. Remove-then-Combine so a
            // slot that survived the re-Init keeps exactly one subscription rather than N.
            var handler = OnEnrollment == null
                              ? null
                              : Delegate.CreateDelegate(typeof(Action<GeoRosterDeploymentItem>), state,
                                                        OnEnrollment, false) as Action<GeoRosterDeploymentItem>;
            if (handler != null)
                foreach (var item in items)
                    item.EnrollmentChanged = (Action<GeoRosterDeploymentItem>)
                        Delegate.Combine(Delegate.Remove(item.EnrollmentChanged, handler), handler);

            ItemsField.SetValue(state, items);
            SetUpInitial.Invoke(state, null);
            return true;
        }
    }

    /// <summary>
    /// THE FIVE-SECOND DROP, AND ANY ONE PEER MAY STOP IT.
    ///
    /// THE FEATURE (owner, 2026-08-07): when someone presses Deploy, every peer is thrown into the battle.
    /// Somebody who was still re-arming a soldier has no way to say "wait". So the press now ARMS a countdown
    /// instead of launching: every peer gets a small overlay over whatever is on screen saying the mission
    /// starts in N seconds, with a CANCEL button, and any single peer's cancel stops it for everyone.
    ///
    /// IT IS NOT A QUORUM AND CANNOT BECOME ONE (P13, laws L84/L91/L145). The countdown is driven by
    /// <see cref="HostTick"/> off <c>Time.realtimeSinceStartup</c> on the HOST alone; it reads no roster, no
    /// peer list, no readiness, and NOBODY HAS TO PRESS ANYTHING for it to complete. Fifty AFK peers change
    /// nothing about when the battle starts. Cancel is the opposite of a vote: ONE peer's veto, effective
    /// immediately, requiring no agreement from anyone — and a veto that never comes costs nothing, because
    /// the default is to proceed. The only wait here is on a clock, which is the kind P13 explicitly allows.
    ///
    /// NO NEW SURFACE ID, because there is none to have: the geoscape band 0xA0-0xBF is fully allocated (the
    /// same wall L99 hit for haven trade). Both halves ride mechanisms that already exist:
    ///   • THE ARM is host→all MOD STATE on the generic value rail as root <c>"M#deploy"</c> — the third one,
    ///     after <c>"M#cart"</c> and <c>"M#mist"</c>, with the same <c>IdentityResolver.RegisterModRoot</c>
    ///     contract and no codec, channel or seq of its own. What ships is two fields, and the SECONDS are
    ///     the host's own number rather than a deadline: a deadline needs a shared clock and the game is
    ///     PAUSED behind the deployment screen (<c>ToDeploymentState</c>:598 sets <c>PauseGame</c>), so a
    ///     game-time deadline would never expire. Every peer renders the integer the host is counting.
    ///   • THE CANCEL is op 2 on 0xB8 <c>GeoMissionIntent</c> — the mission-launch gesture family, the same
    ///     surface the Deploy press itself crosses on, and the same direction (client→host).
    ///
    /// THE GATE IS AT THE ONE MODEL FUNNEL <see cref="MissionSync"/> ALREADY OWNS, <c>GeoMission.Launch</c>,
    /// so every route into a battle is covered by construction — the deployment screen's own button, the
    /// SkipDeploymentSelection arm, the haven and steal-aircraft paths, and the host applying a CLIENT's 0xB8
    /// launch intent. A launch arrives, the countdown arms and the native call is REFUSED; when the count
    /// reaches zero the host re-issues that same call with <see cref="_committed"/> set, and it passes
    /// through untouched. The peer that pressed Deploy keeps its deployment screen up and waits, exactly as
    /// it already did for the round trip.
    ///
    /// REACTIVITY (postulate 1): the overlay is <c>Multiplayer.UI.CountdownPanel</c>, which re-reads
    /// <see cref="State"/> every frame from <c>MultiplayerUI.Update</c> — the same construction
    /// <c>PlayerPanel</c> uses, and the reason there is no edge to forget to raise. An arriving countdown
    /// therefore appears on an ALREADY-OPEN screen within one frame, and a cancel clears it the same way,
    /// with no view-state transition anywhere in the path.
    /// </summary>
    internal static class DeployCountdown
    {
        internal const string RootKey = "M#deploy";

        /// <summary>Five, the owner's number. Long enough to reach a cancel button from any screen, short
        /// enough that nobody waits on somebody else's hesitation.</summary>
        internal const int CountdownSeconds = 5;

        /// <summary>Host writes it, the generic value rail mirrors it, every peer's overlay renders it.</summary>
        internal static readonly DeployCountdownState State = new DeployCountdownState();

        internal static void Register() => IdentityResolver.RegisterModRoot(RootKey, State);

        // ─── HOST-ONLY pending launch (never mirrored: it holds live model refs, law 3) ───
        private static GeoMission _pending;
        private static GeoSquad _pendingSquad;
        /// <summary>The mission a countdown already released. Host-local by construction: GeoMission.Launch
        /// writes nothing a peer could mirror, which is exactly why neither Validate nor IsRunnable can answer
        /// "already launched" and why this latch has to exist at all. Cleared at the reload boundary.</summary>
        private static GeoMission _launched;
        private static float _nextDecrementAt;
        private static bool _committed;
        private static int _hostSecondsLeft;

        // ─── PEER-LOCAL display clock (never replicated, never read by the launch) ───
        private static int _shownArm;
        private static float _shownZeroAt;

        /// <summary>What the overlay shows on THIS peer, derived from local realtime.
        ///
        /// THE COUNT MAY NOT RIDE THE RAIL. <see cref="State"/> is replicated mod-state and
        /// <c>UIStateRosterDeployment</c> has no <c>UiNativeRepaint.Table</c> entry, so every rail batch that
        /// touched it fell to the last-resort lifecycle Exit+Enter (<c>OpenUiRepaint</c>:515-527) — which
        /// re-runs <see cref="DeploymentRosterRefresh"/> and RE-SEEDS EVERY SOLDIER'S POSE. A value that
        /// changed once a second therefore reset every client's deployment lineup once a second. The rail now
        /// carries the ARM and the CLEAR and nothing in between (2 writes per countdown, not 6), and each peer
        /// counts down its own copy from the arm it received.
        ///
        /// It is not a shared clock and does not need to be: the LAUNCH is still the host's alone
        /// (<see cref="HostTick"/>), so a peer whose display drifts by a frame shows a number, never a
        /// decision. It holds at 1 rather than reaching 0 by itself — zero is the host's word, and it arrives
        /// as the CLEAR.</summary>
        internal static int DisplaySecondsLeft()
        {
            int armed = State.SecondsLeft;
            if (armed <= 0) { _shownArm = 0; return 0; }
            if (armed != _shownArm)
            {
                _shownArm = armed;
                _shownZeroAt = Time.realtimeSinceStartup + armed;
            }
            int left = Mathf.CeilToInt(_shownZeroAt - Time.realtimeSinceStartup);
            return left < 1 ? 1 : left;
        }

        /// <summary>PURE (RailCheck L177). Does the native <c>GeoMission.Launch</c> run this call, or is it
        /// held for the countdown? The whole decision, with no clock and no model in it:
        ///   • not a co-op session → yes, vanilla is untouched;
        ///   • not the host → yes, and <see cref="MissionSync"/>'s own client capture then blocks it and
        ///     ships the 0xB8 intent (the countdown belongs to the host, which is the only peer that can
        ///     actually start the battle);
        ///   • the countdown already ran and released this launch → yes, exactly once;
        ///   • otherwise → NO, and the caller arms.
        /// </summary>
        internal static bool RunsNative(bool inSession, bool isHost, bool committed) =>
            !inSession || !isHost || committed;

        /// <summary>PURE (RailCheck L355). DOES THIS LAUNCH ARM A COUNTDOWN? The whole decision, and the two
        /// terms <see cref="RunsNative"/> does not have.
        ///
        /// THE RACE IT CLOSES, frame by frame from the host log of 2026-08-08 22:52. The countdown armed at
        /// <c>3569,652</c> on the host's own press. Nonces 213-219 then each hit <c>_pending != null</c> and
        /// were refused — SILENTLY, while <c>MissionSync</c> still printed "HOST intent APPLIED op=launch" for
        /// every one of them. At <c>3574,697</c> the count reached zero and the native launch ran. At
        /// <c>3574,806</c> — 109 ms later — nonce 220 arrived, found <c>_pending</c> empty, and ARMED A SECOND
        /// COUNTDOWN ON AN ALREADY-LAUNCHED MISSION; at <c>3579,979</c> that one reached zero and
        /// <c>GeoMission.Launch_Patch2</c> threw. The same profile at <c>1803,385→1814,413</c> (nonces
        /// 150/151), twice in one session.
        ///
        /// NOTHING CAUGHT IT because nothing could. <c>HostTick</c>'s <c>!mission.IsRunnable</c> guard is
        /// VACUOUS — <c>GeoMission.cs:147 IsRunnable => true</c> with zero overrides — and
        /// <see cref="MissionSync.Validate"/> cannot see "already launched" either, because
        /// <c>GeoMission.Launch</c>:226-245 never touches <c>Site.ActiveMission</c> (only <c>Cancel</c>:253
        /// does). <c>IntentDedup</c> is right to let them through: nine distinct nonces are nine distinct
        /// clicks. The missing fact is a launch that already HAPPENED, and it is host-local, so it is kept
        /// host-local.
        ///
        /// <paramref name="alreadyLaunched"/> is checked AFTER the <see cref="RunsNative"/> early-out, so the
        /// host's own re-issue at the end of the count still passes; the UI grey-out cannot substitute for it,
        /// because a repaint round trip is inherently longer than the 109 ms this window measured.</summary>
        internal static bool ArmsFor(bool inSession, bool isHost, bool committed, bool pendingBusy,
                                     bool alreadyLaunched) =>
            !RunsNative(inSession, isHost, committed) && !pendingBusy && !alreadyLaunched;

        /// <summary>PURE (RailCheck L355). May the START MISSION button be pressed? The native verdict
        /// (<c>CheckForDeployment</c>:377 <c>flag &amp;&amp; flag3 &amp;&amp; flag2</c>) AND no countdown in
        /// flight — a second press during the count is the click the race above is made of, and it is the same
        /// answer on every peer because <see cref="State"/> is replicated.</summary>
        internal static bool ButtonLive(bool nativeVerdict, bool countdownRunning) =>
            nativeVerdict && !countdownRunning;

        /// <summary>Is a countdown in flight ON THIS PEER? Read off the replicated arm, not off the host's
        /// private <see cref="_pending"/>, so a client greys its own button too.</summary>
        internal static bool CountdownRunning => State.SecondsLeft > 0;

        /// <summary>For the applier's log line only (see <see cref="MissionSync"/>): a launch the gate ate is
        /// not an "APPLIED" launch, and printing it as one is what hid seven swallowed intents.</summary>
        internal static bool IsHolding(GeoMission mission) => mission != null && ReferenceEquals(_pending, mission);

        internal static bool WasAlreadyLaunched(GeoMission mission) =>
            mission != null && ReferenceEquals(_launched, mission);

        /// <summary>PURE (RailCheck L177). The tick, with nothing but two numbers in it — this is the
        /// "waits on no human" property in one line: the only input is elapsed local realtime, so the
        /// countdown advances identically whether every other peer is playing or asleep.</summary>
        internal static bool DecrementDue(float now, float nextAt) => now >= nextAt;

        /// <summary>Called from <c>MissionSync.CaptureLaunch</c> BEFORE any other decision. Returns true to
        /// let the native launch run.</summary>
        internal static bool Gate(GeoMission mission, GeoSquad squad)
        {
            var engine = NetworkEngine.Instance;
            bool inSession = engine != null && engine.IsActiveSession;
            if (RunsNative(inSession, inSession && engine.IsHost, _committed))
            {
                if (_committed) _committed = false;   // one release, one launch
                return true;
            }
            try
            {
                // THE ALREADY-LAUNCHED LATCH, and it is checked BEFORE _pending on purpose: the 109 ms window
                // that produced the double launch is exactly the moment _pending is empty again (ClearPending
                // runs before the native call) and the mission is already in the air. Host-local, because a
                // launch that happened is not shared state — the battle itself is.
                if (ReferenceEquals(mission, _launched))
                {
                    Debug.LogWarning("[MP][deploy] launch REFUSED for " +
                                     (IdentityResolver.RootRef(mission?.Site) ?? "S#?") + " — this mission has " +
                                     "ALREADY been launched by a countdown that reached zero. Arming a second " +
                                     "one on it is what threw in GeoMission.Launch on 2026-08-08 (twice): the " +
                                     "gate is host-local because GeoMission.Launch never touches " +
                                     "Site.ActiveMission, so neither Validate nor IsRunnable can see this");
                    return false;
                }
                if (!ArmsFor(inSession, engine.IsHost, _committed, _pending != null, false))
                {
                    // NEVER SILENT (P1): seven refusals rode this line unlogged in the reported session while
                    // MissionSync printed "HOST intent APPLIED" for every one of them.
                    Debug.Log("[MP][deploy] launch of " + (IdentityResolver.RootRef(mission?.Site) ?? "S#?") +
                              " HELD BEHIND the countdown already running for " +
                              (string.IsNullOrEmpty(State.SiteRef) ? "S#?" : State.SiteRef) + " — a second " +
                              "press does not stack a second drop and does not queue one either. Nothing was " +
                              "lost that the running countdown will not do in " + _hostSecondsLeft + " s");
                    return false;
                }

                // Launch's own default (GeoMission.cs:227-235): a null argument means "the squad the mission
                // already holds". Resolve it HERE so the re-issue five seconds later is the same launch.
                _pending = mission;
                _pendingSquad = squad ?? mission?.Squad;
                _nextDecrementAt = Time.realtimeSinceStartup + 1f;
                _hostSecondsLeft = CountdownSeconds;
                State.SiteRef = IdentityResolver.RootRef(mission?.Site) ?? "";
                State.SecondsLeft = CountdownSeconds;   // the ARM — one rail write, then nothing until the CLEAR
                // The host's own arm rides no rail batch, so nothing would re-run CheckForDeployment here and
                // the host's START MISSION button would stay live through its own countdown.
                OpenUiRepaint.MarkDirty();
                Debug.Log("[MP][deploy] launch HELD for " + CountdownSeconds + " s at " +
                          (string.IsNullOrEmpty(State.SiteRef) ? "S#?" : State.SiteRef) + " — every peer is " +
                          "shown the countdown and ANY ONE of them may cancel it for everyone. Nobody has to " +
                          "press anything for it to complete (no quorum, P13): it is a clock.");
            }
            catch (Exception ex)
            {
                // A countdown that cannot arm must never eat the launch: fall through to the native call.
                Debug.LogError("[MP][deploy] countdown could not arm — launching immediately, as before: " + ex);
                ClearPending();
                _committed = true;
                return true;
            }
            return false;
        }

        /// <summary>Host: one decrement per real second, then the release. Driven from <c>SyncEngine.Tick</c>
        /// so it cannot depend on any screen being open on any peer.</summary>
        internal static void HostTick(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (_pending == null) return;
            try
            {
                if (!DecrementDue(Time.realtimeSinceStartup, _nextDecrementAt)) return;
                _nextDecrementAt = Time.realtimeSinceStartup + 1f;

                // HOST-LOCAL, and that is the whole of symptom 2's fix: the count no longer touches the
                // replicated State, so it ships no delta and re-enters no client's deployment screen.
                if (_hostSecondsLeft > 1)
                {
                    _hostSecondsLeft--;
                    return;
                }

                var mission = _pending;
                var squad = _pendingSquad;
                ClearPending();

                if (mission == null || !mission.IsRunnable)
                {
                    // Never silent: five seconds is long enough for another peer's arbitration to take the
                    // mission, and a launch that quietly evaporates is this repo's dominant bug shape.
                    Debug.LogWarning("[MP][deploy] countdown expired but the mission is no longer runnable — " +
                                     "nothing launched. Another peer cancelled it, or it expired while the " +
                                     "countdown ran; the deployment screen is still closable by its Back button.");
                    return;
                }

                _committed = true;
                _launched = mission;   // the latch the 109 ms re-arm window needs (see ArmsFor)
                Debug.Log("[MP][deploy] countdown reached zero — launching " +
                          (IdentityResolver.RootRef(mission.Site) ?? "S#?") + " natively; every peer joins " +
                          "through the save transfer as it always did");
                mission.Launch(squad);
                _committed = false;   // belt: Gate clears it too, but a refused/rebuilt call must not inherit it
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][deploy] countdown tick failed — clearing it so no peer is left staring at " +
                               "a frozen number; the mission can be launched again from the deployment screen: " + ex);
                ClearPending();
                _committed = false;
            }
        }

        /// <summary>ANY peer's veto, applied on the host. <paramref name="who"/> is for the log only — the
        /// decision reads nothing about WHICH peer sent it, because every peer's veto is the same veto.</summary>
        internal static void Cancel(string who)
        {
            if (_pending == null && string.IsNullOrEmpty(State.SiteRef))
            {
                // NEVER SILENT (P1, and the bug class that hid the dead click for a whole session): a veto
                // that lands on nothing still says so, because "the click did nothing" and "the click never
                // arrived" are the same symptom and only a log tells them apart.
                Debug.Log("[MP][deploy] cancel from " + who + " arrived with NO countdown running — nothing to " +
                          "stop. Either it had already reached zero, or another peer's veto got here first.");
                return;
            }
            Debug.Log("[MP][deploy] countdown CANCELLED by " + who + " at " +
                      (string.IsNullOrEmpty(State.SiteRef) ? "S#?" : State.SiteRef) +
                      " — the mission is NOT cancelled, only the drop: the deployment screen is still there " +
                      "and Deploy can be pressed again");
            ClearPending();
        }

        /// <summary>The cancel gesture, from either side of the wire. On the host it IS the decision; on a
        /// client it crosses as op 2 on the mission surface the Deploy press already uses.</summary>
        internal static void RequestCancel()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.LogWarning("[MP][deploy] CANCEL pressed with no active co-op session — the veto goes " +
                                 "nowhere. The panel should not have been on screen at all (SyncCore gates on " +
                                 "IsActiveSession), so this is a stale overlay rather than a lost cancel.");
                return;
            }
            if (engine.IsHost) { Cancel("this host"); return; }
            Debug.Log("[MP][deploy] CANCEL leaving this client as op " + MissionSync.OpCancelLaunch + " on 0x" +
                      SurfaceIds.GeoMissionIntent.ToString("X2") + " — the host owns the countdown, so the veto " +
                      "takes one round trip and then clears the overlay on every peer through the rail.");
            IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpCancelLaunch, "cancel the deployment countdown");
        }

        /// <summary>Host applier for op 2. Deliberately validates NOTHING about the sender: a veto is not a
        /// claim about state, it is "somebody is not ready", and refusing it would be the first step towards
        /// the vote this must never become.</summary>
        internal static void HandleCancel(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
            => Cancel("peer=" + senderPeerId + " nonce=" + nonce);

        private static void ClearPending()
        {
            _pending = null;
            _pendingSquad = null;
            _hostSecondsLeft = 0;
            _shownArm = 0;
            State.SiteRef = "";
            State.SecondsLeft = 0;   // the CLEAR — the second and last rail write of a countdown
            // Same reason as the ARM: on the host the clear is local, and without this the button the arm
            // greyed would never come back after a cancel.
            OpenUiRepaint.MarkDirty();
        }

        /// <summary>Mod-root contract (IdentityResolver.cs:205-206): mod state must be EMPTY at every reload
        /// boundary. It always is between battles — a countdown either launched or was cancelled — but the
        /// host may be torn down mid-count by something else entirely, and a stale number left on the root
        /// would be baselined and then never emitted again.</summary>
        internal static void ResetForReloadBoundary()
        {
            ClearPending();
            _launched = null;
            _committed = false;
            _nextDecrementAt = 0f;
        }

        internal static void Reset() => ResetForReloadBoundary();
    }

    /// <summary>
    /// START MISSION IS DEAD WHILE THE COUNTDOWN RUNS — the other half of the double-launch fix, and the half
    /// the player can see.
    ///
    /// The owner asked for exactly this ("grey out the start button while the countdown runs") and it is
    /// necessary but NOT sufficient: the measured re-arm window was 109 ms (host 2026-08-08 22:52, nonce 220
    /// against a launch at <c>3574,697</c>), and a repaint round trip is longer than that. The latch in
    /// <see cref="DeployCountdown.ArmsFor"/> is what actually closes the race; this is what stops the player
    /// generating the clicks in the first place — nine of them in the reported session, seven swallowed.
    ///
    /// ONE FUNNEL FOR ALL CALLERS. <c>UIStateRosterDeployment.cs:377</c> is the ONLY writer of the button's
    /// interactability (<c>PhoenixGeneralButton.SetInteractable</c>:283-288 greys the visual itself), and a
    /// POSTFIX also runs when TFTV's own Prefix on this method returns false
    /// (<c>TFTVAircraftRework/AircraftReworkMissionDeployment.cs</c>:136-179), so the TFTV branch is covered
    /// without naming TFTV.
    ///
    /// RE-ENABLE NEEDS NO EDGE OF ITS OWN: the countdown's CLEAR is a rail write on <c>"M#deploy"</c>, and
    /// <c>UiEventMap</c>'s new <c>UIStateRosterDeployment</c> arm re-runs <c>SetUpInitialDeployment</c> →
    /// <c>CheckForDeployment</c> → this postfix. On the HOST the arm and the clear are local, so
    /// <see cref="DeployCountdown"/> marks the UI dirty at both.
    /// </summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "CheckForDeployment")]
    internal static class DeployButtonCountdownLock
    {
        private static void Postfix()
        {
            if (!DeployCountdown.CountdownRunning) return;
            try
            {
                var button = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.View?
                             .GeoscapeModules?.DeploymentMissionBriefingModule?.DeployButton;
                if (button == null) return;
                // ButtonLive's false arm, applied: the native verdict has already been written one line
                // earlier (:377) and this only ever takes it AWAY, never grants it.
                button.SetInteractable(DeployCountdown.ButtonLive(true, true));
                button.ResetButtonAnimations();
            }
            catch (Exception ex)
            {
                // Presentation seam (P4c): a failure here leaves the button as the game painted it, which is
                // the pre-fix behaviour — the host-side latch still refuses the second launch.
                Debug.LogError("[MP][deploy] could not grey START MISSION for the running countdown: " + ex);
            }
        }
    }

    /// <summary>
    /// The deployment countdown as mod-state (root <c>"M#deploy"</c>, contract at
    /// <see cref="IdentityResolver.RegisterModRoot"/>). Two fields and no more: WHERE the drop is going, so
    /// a peer can tell one countdown from the next, and HOW LONG THE COUNTDOWN IS — written exactly twice per
    /// countdown, the ARM and the CLEAR, with <see cref="DeployCountdown.DisplaySecondsLeft"/> counting down
    /// from it on each peer locally. It is deliberately NOT ticked here: this is replicated mod-state, and
    /// <c>UIStateRosterDeployment</c> has no native-repaint entry, so a value that changed once a second
    /// re-entered every client's deployment screen once a second and re-seeded every soldier's pose. Nothing
    /// here is a decision — the launch is <see cref="DeployCountdown"/>'s, host-side, off host objects.
    /// </summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class DeployCountdownState
    {
        public string SiteRef = "";
        public int SecondsLeft;
    }
}
