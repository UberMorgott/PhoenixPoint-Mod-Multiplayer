using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Base.Core;
using HarmonyLib;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Events.Eventus;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// MISSION-LAUNCH gesture family (surface 0xB8, law 1) — the "squad INTENT" that
    /// <see cref="GeoWindowCoverage"/>'s <c>UIStateRosterDeployment</c> declaration named as the missing
    /// piece: "shared deployment (all peers pick from one roster, host commits)". Without it a client
    /// could open the deployment screen and press Deploy, and the click died silently — the native chain
    /// ends in <c>GeoLevelController.LaunchTacticalGame</c>, which <c>TacticalEntry.TacLaunchGate</c>
    /// BLOCKS on a client by construction (the battle is built once on the host and shipped to every peer
    /// as a mid-tactical save, law 1). The screen closed back to the plain geoscape and the mission never
    /// started for anybody.
    ///
    /// ONE op, at the ONE model funnel: <c>GeoMission.Launch(GeoSquad)</c> (GeoMission.cs:226). Every
    /// route into a battle bottoms out there and the funnel is what makes this generic rather than a
    /// per-screen patch — <c>UIStateRosterDeployment.DeploySquad</c>:331 (the deployment screen's own
    /// button), <c>GeoscapeView.LaunchMission</c>:1043 (its SkipDeploymentSelection / SkipDeploymentScreen
    /// arm, which never opens a screen at all), <c>HavenFacilityController</c>:149,
    /// <c>HavenInteractionController</c>:226, <c>UIModuleSiteEncounters</c>:612 and
    /// <c>StealAircraftAbility</c>:93 all reach it. Capture is block-first
    /// (<see cref="IntentRail.ShouldRunNative"/>): the client never runs <c>PrepareTacticalGame</c>, never
    /// stamps <c>GlobalTime</c> and never rolls <c>GenerateMissionThreatLevel</c> — all three are
    /// authoritative and all three ride back as ordinary state.
    ///
    /// THE WIRE CARRIES IDENTITY ONLY: the mission's SITE root key plus the chosen soldiers' root keys
    /// ("U#&lt;GeoTacUnitId&gt;", IdentityResolver.cs:146). NOT the mission — a client's <c>GeoMission</c>
    /// is a structural mirror of the host's own (log: <c>structural create 'S#388…ActiveMission'</c>), and
    /// the host reads <c>site.ActiveMission</c> off its OWN graph rather than trusting a reference that
    /// could name a mission it already cancelled. NOT the squad object either: <c>GeoSquad.Units</c> holds
    /// live <c>GeoCharacter</c> refs (GeoSquad.cs:11), so the host rebuilds the squad from ITS instances.
    ///
    /// The peer that clicked keeps its deployment screen up and simply waits: the host's launch curtains
    /// every peer through <c>SaveTransferCoordinator</c> within the round trip, and that teardown takes
    /// the screen with it. A REJECT leaves the screen live and closable by its own Back button — which is
    /// why <see cref="MissionCancelGate"/> exists next to this.
    /// </summary>
    public static class MissionSync
    {
        internal const byte OpLaunch = 1;  // [siteRef][n:u16][charRef × n]

        /// <summary>The countdown veto (<see cref="DeployCountdown"/>), on the surface the Deploy press
        /// itself crosses on and in the same direction. EMPTY BODY on purpose: the wire carries WHO by
        /// carrying nothing — a veto names no state, so there is nothing for the host to re-resolve and
        /// nothing a stale mirror could get wrong.</summary>
        internal const byte OpCancelLaunch = 2;  // (no body)

        internal static void RegisterIntents()
        {
            IntentRail.Register(SurfaceIds.GeoMissionIntent, "mission",
                new Dictionary<byte, IntentRail.OpHandler>
                {
                    [OpLaunch] = HandleLaunch,
                    [OpCancelLaunch] = DeployCountdown.HandleCancel,
                });
        }

        private static GeoLevelController GeoLevel()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<GeoLevelController>();
        }

        // NOTIFY: a refused LAUNCH is the one geoscape gesture with no vanilla surface to fall back on. The
        // player picked a squad, pressed Launch and the deployment simply does not happen; the usual cause is
        // co-op arbitration (another peer already took this site / the mission stopped being runnable under
        // them), which nothing in the game's own UI greys out because single-player cannot produce it.
        private static void Reject(ulong peer, string siteRef, string why) =>
            IntentRail.Reject(SurfaceIds.GeoMissionIntent, peer, (siteRef ?? "S#?") + " — " + why,
                              true /* notify */, string.IsNullOrEmpty(siteRef) ? null : siteRef);

        // ─── THE ONE VALIDATOR (pure — host facts only, law 3) ──────────────

        /// <summary>Every fact the launch is allowed to be checked against, all of it read off the HOST's
        /// own graph: a client mirror can be arbitrarily stale, so each gate the game itself applies is
        /// repeated here rather than trusted from the wire.</summary>
        internal struct Facts
        {
            internal bool SiteResolved;      // the site root key resolved to a live GeoSite
            internal bool MissionRunnable;   // site.ActiveMission exists and GeoMission.IsRunnable (LaunchMissionAbility:34)
            internal int UnitsRequested;     // how many soldier refs the wire carried
            internal int UnitsResolved;      // how many of them named a live GeoCharacter on the host
            internal bool AllOwnedByPlayer;  // every resolved unit belongs to the shared player faction
            internal bool HasStandalone;     // at least one IsTacticalStandaloneActor (LaunchMissionAbility:38)
            internal int Volume;             // sum of OccupingSpace (UIStateRosterDeployment.CheckForDeployment:372)
            internal int MaxUnits;           // MissionDef.MaxPlayerUnits (:374-375)
            internal int VehicleCount;       // vehicles + mutogs in the squad (:373, refused at 2+ by :376)
        }

        /// <summary>null = accept, otherwise the human reason the launch was refused. Never blank — a
        /// silently eaten Deploy click is the exact bug this family exists to kill.</summary>
        internal static string Validate(Facts f)
        {
            if (!f.SiteResolved) return "no such site on the host — stale mirror";
            if (!f.MissionRunnable)
                return "that site has no runnable mission any more (already launched, cancelled, or expired)";
            if (f.UnitsRequested == 0) return "the squad is empty — nothing to deploy";
            if (f.UnitsResolved != f.UnitsRequested)
                return "only " + f.UnitsResolved + " of " + f.UnitsRequested + " chosen soldiers exist on the host — " +
                       "stale roster (dismissed, dead, or another peer moved them)";
            if (!f.AllOwnedByPlayer)
                return "a chosen soldier is not on the shared player faction — only Phoenix soldiers deploy from a peer";
            if (!f.HasStandalone)
                return "no soldier in the squad can stand on the battlefield by itself (NotEnoughSoldiersForMission)";
            if (f.Volume > f.MaxUnits)
                return "the squad takes " + f.Volume + " deployment slots but the mission allows " + f.MaxUnits;
            if (f.VehicleCount > 1)
                return "two vehicles/mutogs in one squad — the deployment screen refuses that (:376)";
            return null;
        }

        // ─── CLIENT: the capture seam (law 4a), block-first ─────────────────

        /// <summary>Capture at the MODEL funnel. <c>Launch</c> is a single non-virtual method with one
        /// signature, so one patch covers every caller; the optional parameter is named exactly as the
        /// game names it, since Harmony binds by name.</summary>
        [HarmonyPatch(typeof(GeoMission), nameof(GeoMission.Launch))]
        internal static class LaunchCapturePatch
        {
            private static bool Prefix(GeoMission __instance, GeoSquad squad) => CaptureLaunch(__instance, squad);
        }

        private static bool CaptureLaunch(GeoMission mission, GeoSquad squad)
        {
            // THE FIVE-SECOND DROP, asked FIRST because it is the only gate that can refuse the HOST's own
            // launch. On a client it answers "run native" and the block-first capture below is unchanged;
            // on the host it holds the launch, arms the shared countdown, and re-issues this same call when
            // the count reaches zero. Not a quorum: nobody has to act for it to complete (DeployCountdown).
            if (!DeployCountdown.Gate(mission, squad)) return false;
            if (IntentRail.ShouldRunNative()) return true;
            string siteRef = null;
            try
            {
                siteRef = IdentityResolver.RootRef(mission?.Site);
                // Launch's own default: a null argument means "use the squad the mission already holds"
                // (GeoMission.cs:227-235), so read it the same way rather than refusing a legal call.
                var units = (squad ?? mission?.Squad)?.Units;
                if (siteRef == null || units == null || units.Count == 0)
                {
                    Debug.LogWarning("[MP][mission] CLIENT launch DROPPED — " +
                                     (siteRef == null ? "the mission's site has no rail identity" : "no squad to deploy") +
                                     "; nothing was sent and nothing ran locally");
                    OpenUiRepaint.MarkDirty();
                    return false;
                }
                var refs = new List<string>(units.Count);
                foreach (var c in units)
                {
                    var r = IdentityResolver.RootRef(c);
                    if (r == null)
                    {
                        Debug.LogWarning("[MP][mission] CLIENT launch DROPPED — soldier '" +
                                         (c == null ? "<null>" : c.DisplayName) + "' has no rail identity, so the host " +
                                         "cannot be told who to deploy; reconverging the open screen");
                        OpenUiRepaint.MarkDirty();
                        return false;
                    }
                    refs.Add(r);
                }
                IntentRail.Send(SurfaceIds.GeoMissionIntent, OpLaunch,
                    "launch " + siteRef + " squad=" + refs.Count,
                    w =>
                    {
                        w.Write(siteRef);
                        w.Write((ushort)refs.Count);
                        foreach (var r in refs) w.Write(r);
                    });
            }
            catch (Exception ex)
            {
                // Nothing ran and nothing shipped: no delta will ever repaint the launch the screen already
                // drew, so reconverge from the un-mutated local model exactly as the reject path does.
                Debug.LogError("[MP][mission] CLIENT launch capture failed for " + (siteRef ?? "S#?") +
                               " — reconverging local UI: " + ex);
                OpenUiRepaint.MarkDirty();
            }
            return false;
        }

        // ─── HOST: the applier (decode/dedup/reject discipline = IntentRail) ─────

        private static void HandleLaunch(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = null;
            try
            {
                siteRef = r.ReadString();
                int n = r.ReadUInt16();
                var refs = new List<string>(n);
                for (int i = 0; i < n; i++) refs.Add(r.ReadString());

                var geo = GeoLevel();
                if (geo == null) { Reject(senderPeerId, siteRef, "no geoscape"); return; }

                var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                var mission = site?.ActiveMission;           // the HOST's own mission, never a wire reference
                var units = refs.Select(x => IdentityResolver.Resolve(geo, x, null) as GeoCharacter)
                                .Where(c => c != null).ToList();

                string why = Validate(new Facts
                {
                    SiteResolved = site != null,
                    MissionRunnable = mission != null && mission.IsRunnable,
                    UnitsRequested = n,
                    UnitsResolved = units.Count,
                    AllOwnedByPlayer = units.All(c => ReferenceEquals(c.Faction, geo.PhoenixFaction)),
                    HasStandalone = units.Any(c => c.TemplateDef != null && c.TemplateDef.IsTacticalStandaloneActor),
                    Volume = units.Sum(c => c.OccupingSpace),
                    MaxUnits = mission?.MissionDef == null ? 0 : mission.MissionDef.MaxPlayerUnits,
                    VehicleCount = units.Count(c => c.TemplateDef != null &&
                                                    (c.TemplateDef.IsVehicle || c.TemplateDef.IsMutog)),
                });
                if (why != null) { Reject(senderPeerId, siteRef, "launch: " + why); return; }

                // The host runs the SAME native method the client's click was blocked from, with a squad
                // built out of the host's OWN instances (GeoSquad.cs:23). Everything it produces —
                // GlobalTime, the threat roll, the tactical level itself — is host-computed by construction,
                // and every peer joins through TacticalEntry's save transfer, not through this call.
                mission.Launch(new GeoSquad(units));
                Debug.Log("[MP][mission] HOST intent APPLIED op=launch " + siteRef + " squad=" + units.Count +
                          " nonce=" + nonce + " peer=" + senderPeerId);
            }
            catch (Exception ex) { Reject(senderPeerId, siteRef, "launch (throw) " + ex.Message); }
        }
    }

    /// <summary>
    /// PRESENTATION seam (law 4c) at the encounter that STARTS a mission — the piece without which
    /// <see cref="MissionSync"/> was unreachable on a client, and the reason the 0xB8 family never once
    /// fired in the 2026-08-01 run: the client never got as far as a deployment screen, so nothing ever
    /// called <c>GeoMission.Launch</c> for the capture to see.
    ///
    /// The native route from "answer the mission encounter" to the deployment screen is ONE call inside
    /// <c>UIModuleSiteEncounters.SelectChoice</c>:598-613 — <c>ev.CompleteEvent(...)</c>, then
    /// <c>ChoiceReward.ApplyResult.StartMission</c>, then <c>FinishEncounter()</c> +
    /// <c>Context.View.LaunchMission(startMission, _geoEvent.Context.Vehicle)</c>:612. On a CLIENT the
    /// whole method is unreachable: <c>EventPopup.EventChoiceClientLock</c> blocks
    /// <c>OnChoiceSelected</c> and turns the click into a 0xB4 answer intent, and even if it did run,
    /// <c>EventSync.EventCompleteArbiter</c> skips <c>CompleteEvent</c> and hands back the empty
    /// <c>ChoiceReward</c> stub, so <c>StartMission</c> is null and <c>SelectChoice</c> returns false.
    /// The client therefore fell into the :586-596 arm — result page, OK, back to the plain geoscape —
    /// which is exactly the reported symptom: no briefing, no soldier assignment, host-only launches.
    ///
    /// That lost call is pure LOCAL NAVIGATION, not game logic: <c>LaunchMission</c> either opens
    /// <c>UIStateRosterDeployment</c> (declared LocalOnly in <see cref="GeoWindowCoverage"/> — every peer
    /// navigates its own) or, in the SkipDeploymentSelection arm, calls <c>mission.Launch</c>, which is
    /// the capture above. So the client may simply re-issue it, and every peer holding the window gets the
    /// same screen the host gets. Two peers may deploy the same mission at once; the first 0xB8 to reach
    /// the host wins and the second is refused by <see cref="Validate"/>'s "no runnable mission any more"
    /// arm — law 5's first-to-act-wins, no ownership table.
    ///
    /// AND IT IS NOT A CLIENT SEAM (2026-08-06, law L141). The bail read "solo/host ran SelectChoice
    /// natively" off <c>engine.IsHost</c>, and that is false for a HOST THAT LOST THE EVENT RACE: its click
    /// is REPLAYED (<c>EventPopup.ResolutionIsNotOurs</c> → <c>ReplayResolution</c> → return false), so not
    /// one line of <c>SelectChoice</c>:598-613 ran there either — including :612. Reported live: the host
    /// pressed the mission choice and went into the battle without ever seeing the squad screen. The term is
    /// "this peer's native SelectChoice did not run", carried by <c>EventPopup.ClickWasReplayed</c>, and the
    /// second term <see cref="ShouldOpenDeployment"/> adds keeps the widened predicate from double-queueing
    /// the screen the losing host is usually already headed for.
    ///
    /// Hooked at <c>FinishEncounter</c> (:618) rather than at the click, because that is the one point
    /// BOTH native arms reach and, more importantly, the point by which the host's answer has come back:
    /// the client's result page is drawn from the arriving record delta (<c>EventPopup</c>'s "resolved by
    /// THIS peer's own click"), and the mission rides in as the <c>ActiveMission</c> structural create in
    /// that same stream. The mission is read off the SITE, never off the reward — the client's reward is
    /// the stub — and the choice is read from the HOST's record index, since <c>GeoscapeEvent
    /// .SelectedChoice</c> is only written inside the <c>CompleteEvent</c> the client skipped.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "FinishEncounter")]
    internal static class MissionEncounterNav
    {
        private static readonly FieldInfo GeoEventField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_geoEvent");
        private static readonly FieldInfo SwitchQueryField = AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");

        /// <summary>PURE (RailCheck L141). THE OUTCOME this seam owes every peer is "headed for the
        /// pre-mission squad screen"; this answers the only question left, WHO STILL OWES ITSELF THE CALL.
        ///
        /// "MY NATIVE SelectChoice DID NOT RUN", not "I am a client", is the term — that was the defect. The
        /// old predicate was <c>inSession &amp;&amp; !isHost</c>, commented "solo/host ran SelectChoice
        /// natively", which is false for a HOST THAT LOST THE EVENT RACE: its click is replayed
        /// (<c>EventPopup.ResolutionIsNotOurs</c> → <c>ReplayResolution</c> → return false), so :612's
        /// <c>LaunchMission</c> never ran there either. A client's click is ALWAYS relayed, so
        /// <paramref name="clickWasReplayed"/> is only ever asked of the host.
        ///
        /// <paramref name="alreadyHeaded"/> is the other half, and it is what keeps the widened predicate from
        /// becoming a SECOND bug. A losing host is normally already headed: <c>EventSync.HandleAnswer</c>'s
        /// native tail runs <c>geo.View.LaunchMission</c> the moment it applies the winning answer, and
        /// <c>GeoscapeView.ToDeploymentState</c>:592 QUEUES the state
        /// (<c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>, priority int.MaxValue) rather than entering it —
        /// it is served only when the peer's own encounter window finishes. MEASURED in the reported session
        /// (host log 36869/632,002 s): `Queuerd state switch …UIStateRosterDeployment with priority
        /// 2147483647`, 4 s before that host clicked its stale picker. Re-issuing blind would leave a SECOND
        /// request in a queue that is part of the SAVE (<c>GeoscapeViewSwitchQuery.GetRestorableData</c>) —
        /// a deployment screen for a finished mission, waiting for the player after the battle.</summary>
        internal static bool ShouldOpenDeployment(bool inSession, bool isHost, bool clickWasReplayed, bool alreadyHeaded)
            => inSession && !NativeSelectChoiceRan(isHost, clickWasReplayed) && !alreadyHeaded;

        /// <summary>PURE. Did <c>UIModuleSiteEncounters.SelectChoice</c>:598-613 execute ON THIS PEER for this
        /// resolution — the single fact the old bail got wrong.</summary>
        internal static bool NativeSelectChoiceRan(bool isHost, bool clickWasReplayed) => isHost && !clickWasReplayed;

        /// <summary>Is this peer already in, or queued for, the pre-mission squad screen? Asked of the game's
        /// OWN queue (<c>TryGetStateSwitchRequestForState</c>) and current state, so it stays true for the
        /// whole window between HandleAnswer's queue and the state actually being entered.
        ///
        /// TFTV-SAFE WITHOUT KNOWING TFTV: the screen the player edits his squad on with TFTV installed IS
        /// <c>UIStateRosterDeployment</c> — TFTV adds no view state of its own, it decorates that one
        /// (<c>TFTVUI/Geoscape/MissionDeployment.cs</c>:36/:101 EnterState/ExitState,
        /// <c>TFTVHarmonyGeoscapeUI.cs</c>:249 OnDeploySquad, <c>TFTVAircraftRework/
        /// AircraftReworkMissionDeployment.cs</c>:26/:133 OnEnrollmentChanged/CheckForDeployment) and only
        /// flips <c>SkipDeploymentSelection</c> on some mission defs. So this file names ZERO TFTV types,
        /// needs no <c>TftvLateBinder</c> arm, and behaves identically with and without TFTV loaded.</summary>
        internal static bool AlreadyHeadedForDeployment(GeoscapeView view)
        {
            if (view == null) return false;
            if (view.CurrentViewState is UIStateRosterDeployment) return true;
            return SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q &&
                   q.TryGetStateSwitchRequestForState<UIStateRosterDeployment>(out _);
        }

        /// <summary>PURE (RailCheck L144). May a peer that resolved a mission-start encounter OFF THE RAIL
        /// leave its own encounter window standing? NO — and that, not the <see cref="ShouldOpenDeployment"/>
        /// predicate above, is what the 2026-08-06 host actually hit.
        ///
        /// <c>ToDeploymentState</c>:596 QUEUES the squad screen, and <c>GeoscapeViewSwitchQuery
        /// .ProcessQueriedStateSwitch</c> RETURNS IMMEDIATELY while <c>_currentStateSwitchRequest != null</c>.
        /// The open encounter window IS that request — it was pushed by the same queue (`Queuerd state switch
        /// …UIStateGeoscapeEvent with priority 0`) — so the ONLY thing that ever serves the deployment request
        /// is <c>FinishQueriedState</c>, and the only thing that calls it here is
        /// <c>UIModuleSiteEncounters.FinishEncounter</c>:618. Native knows this and pairs the two in one
        /// breath at <c>SelectChoice</c>:611-612: <c>FinishEncounter(); Context.View.LaunchMission(…)</c>.
        /// <see cref="EventSync"/>'s native tail copied :612 and not :611, so the losing host queued a screen
        /// behind a window nothing would ever close and sat on a stale picker until another peer's launch
        /// curtained it. MEASURED (host log, one clock): 118,239 s `Queuerd state switch
        /// …UIStateRosterDeployment with priority 2147483647` — then NOT ONE view-state line until 121,075 s
        /// `Entering Geoscape UI state: UIStateLoading`, the battle. The client, which reaches :611 through
        /// <see cref="MissionEncounterNav"/>'s own <c>FinishEncounter</c> postfix, entered the screen nine
        /// frames after queueing it.</summary>
        internal static bool MustFinishOwnEncounterWindow(bool missionStarted, bool ownWindowShowsThisEvent)
            => missionStarted && ownWindowShowsThisEvent;

        /// <summary>The missing :611. Calls the GAME's own <c>FinishEncounter</c> rather than
        /// <c>view.FinishQueriedState()</c> directly: that method also runs <c>AudioPlayer.EndEvent()</c>
        /// (:620 → <c>NarrationSound.EndEvent</c>:67), so skipping it would carry the encounter narration into
        /// the squad screen. Guarded on the CURRENT view state showing THIS event, so it can never pop a state
        /// it does not own — <c>FinishCurrentStateSwitch</c> also runs <c>SwitchToPreviousState</c>, and a
        /// blind second call would pop one screen too many.</summary>
        internal static void FinishOwnEncounterWindow(GeoscapeView view, string eventId, bool missionStarted)
        {
            var open = (view?.CurrentViewState as UIStateGeoscapeEvent)?.Event;
            bool mine = !string.IsNullOrEmpty(eventId) &&
                        string.Equals(open?.EventID, eventId, StringComparison.Ordinal);
            if (!MustFinishOwnEncounterWindow(missionStarted, mine)) return;
            var module = view.GeoscapeModules?.SiteEncountersModule;
            if (module == null || FinishEncounterMethod == null) return;
            FinishEncounterMethod.Invoke(module, null);
            Debug.Log("[MP][mission] closing this peer's own encounter window for '" + eventId + "' — the " +
                      "squad screen was QUEUED behind it (ToDeploymentState:596, priority int.MaxValue) and " +
                      "GeoscapeViewSwitchQuery.ProcessQueriedStateSwitch serves nothing while this window is " +
                      "the current request; SelectChoice:611 makes the same call before :612's LaunchMission");
        }

        private static readonly MethodInfo FinishEncounterMethod =
            AccessTools.Method(typeof(UIModuleSiteEncounters), "FinishEncounter");

        /// <summary>PURE (RailCheck L191). WHY there is no squad screen to open — and it is a different
        /// answer on the two roles, which is what this guard got wrong.
        ///
        /// MEASURED, host log 2026-08-07, ONE clock: 23:10:12.981 `HOST answered 'PROG_NJ0_MISS' … peer=1`,
        /// 23:10:13.052 `structural create 'S#104…ActiveMission' sent`, 23:10:26.624 `structural DESTROY
        /// 'S#104…ActiveMission' sent`, 23:10:30.105 the host's own stale picker `REPLAYED`, and 23:10:31.254
        /// the host telling ITSELF "the host's mission has not arrived on this peer yet … reach it from the
        /// aircraft's Launch button once it lands". It had arrived — the host MINTED it 17 s earlier — and it
        /// was gone 4.6 s before the click. Nothing would ever land.
        ///
        /// THE HOST CANNOT BE WAITING FOR STATE IT AUTHORS. On this peer <c>ActiveMission == null</c> has
        /// exactly one meaning — the mission is GONE (launched by whoever won the race, cancelled, or
        /// expired) — and returning without a screen is then CORRECT, not a race to wait out. Only on a
        /// CLIENT is "not arrived yet" a possible reading, because only there is the mission somebody else's
        /// structural create. The guard's behaviour is unchanged; what changes is that it stops sending the
        /// next reader (and the owner) hunting an arrival race that cannot exist on the peer that printed it.
        /// It is the same mistake as making the host discard restored windows only it holds — one peer, one
        /// role, and the answer follows from which one it is.</summary>
        internal static string NoDeploymentReason(bool isHost, bool missionMissing) =>
            isHost
                ? "this peer AUTHORS the mission, so it cannot be waiting for it to arrive: " +
                  (missionMissing ? "the site has no ActiveMission" : "the mission is not runnable") +
                  " means it is GONE — already launched, cancelled or expired — and there is nothing left to " +
                  "open. This is the correct outcome, not a race."
                : "the host's mission has not arrived on this peer yet (ActiveMission " +
                  (missionMissing ? "missing" : "not runnable") + "); reach it from the aircraft's Launch " +
                  "button once it lands";

        /// <summary>EVERY EXIT OF THIS POSTFIX SAYS WHY (RailCheck L197 arm). It had FOUR unlogged returns,
        /// and on the peer that did not answer the 2026-08-07 mission event it emitted NOTHING AT ALL — not
        /// the success, not the warning, not the catch — so which of the four it took was unknowable from the
        /// log and the squad screen simply never appeared. Silent swallow is this repo's dominant bug class;
        /// a bail nobody can see is a bug nobody can find.</summary>
        private static void Postfix(UIModuleSiteEncounters __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession)
            {
                Debug.Log("[MP][mission] deployment not opened because this peer is not in a session — solo, " +
                          "where UIModuleSiteEncounters.SelectChoice:598-613 ran end to end and :612 already " +
                          "opened the squad screen natively.");
                return;
            }
            try
            {
                var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
                var choices = ev?.EventData?.Choices;
                // The LIVE record, not the dialog's own ref: on a mirroring peer ev.Record can still be the
                // placeholder RaiseMirrored minted (EventPopup:568), whose SelectedChoice is nobody's answer.
                var rec = EventPopup.LiveRecord(ev?.EventID, ev?.Record);
                int idx = rec == null ? -1 : rec.SelectedChoice;
                if (choices == null || idx < 0 || idx >= choices.Count)
                {
                    Debug.Log("[MP][mission] deployment for '" + (ev?.EventID ?? "?") + "' not opened because " +
                              "this peer has no answered choice to read (choices=" +
                              (choices == null ? "none" : choices.Count.ToString()) + ", selected=" + idx +
                              ") — the record delta has not landed here yet. MissionArrivalNav still owns it.");
                    return;
                }
                // Unity default-constructs an EMPTY OutcomeStartMission, so non-null is not the signal —
                // MissionTypeDef is (GeoEventChoiceOutcome:315, same test EventPopup.StartsMission uses).
                if (choices[idx].Outcome?.StartMission?.MissionTypeDef == null)
                {
                    Debug.Log("[MP][mission] deployment for '" + ev.EventID + "' not opened because the answered " +
                              "choice (" + idx + ") starts no mission — correct, and the ordinary case for " +
                              "every event window that is not a mission-start.");
                    return;
                }

                var view = __instance.Context?.View;
                if (!ShouldOpenDeployment(true, engine.IsHost, EventPopup.ClickWasReplayed(ev),
                                          AlreadyHeadedForDeployment(view)))
                {
                    Debug.Log("[MP][mission] deployment for '" + ev.EventID + "' not opened because this peer " +
                              "does not owe itself the call — " +
                              (NativeSelectChoiceRan(engine.IsHost, EventPopup.ClickWasReplayed(ev))
                                  ? "its own SelectChoice:612 already ran"
                                  : "it is already in, or queued for, UIStateRosterDeployment"));
                    return;
                }

                var site = ev.Context?.Site;
                var mission = site?.ActiveMission;
                if (mission == null || !mission.IsRunnable)
                {
                    // Never silent, and never role-blind — see NoDeploymentReason.
                    Debug.LogWarning("[MP][mission] deployment for '" + ev.EventID + "' at " +
                                     (IdentityResolver.RootRef(site) ?? "S#?") + " NOT opened — " +
                                     NoDeploymentReason(engine.IsHost, mission == null));
                    return;
                }

                view.LaunchMission(mission, ev.Context.Vehicle);
                Debug.Log("[MP][mission] " + (engine.IsHost ? "HOST" : "CLIENT") + " squad screen opened for '" +
                          ev.EventID + "' at " + (IdentityResolver.RootRef(site) ?? "S#?") + " — re-issuing the " +
                          "LaunchMission call SelectChoice:612 could not make here (this peer's click " +
                          (engine.IsHost ? "was replayed: another peer's answer won the race" : "is always relayed") +
                          "); the squad picked rides out as a 0xB8 launch intent");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][mission] deployment navigation failed — this peer stays on the " +
                               "geoscape and can still reach the mission from the aircraft's Launch button: " + ex);
            }
        }
    }

    /// <summary>
    /// THE SQUAD SCREEN OPENS ON THE MISSION'S ARRIVAL, NOT ON A DIALOG'S TEARDOWN (2026-08-08).
    ///
    /// THE REPORT. Two peers were shown the same mission-start event; one answered, the other did not. The
    /// answering peer reached <c>UIStateRosterDeployment</c>; the other never did, and its
    /// <see cref="MissionEncounterNav"/> postfix emitted NO LINE AT ALL — not the success, not the warning,
    /// not the catch — so it bailed at one of four unlogged returns and the screen was simply gone. Those
    /// four returns now all speak (see there), but LOGGING A BAIL IS NOT A FIX FOR IT.
    ///
    /// WHY THE FUNNEL WAS THE WRONG THING TO DEPEND ON. <c>UIModuleSiteEncounters.FinishEncounter</c> is a
    /// LOCAL DIALOG event: it fires only if this peer's own window was built, was clicked through and tore
    /// down, and only reads state that this peer's own dialog happens to be holding at that instant — a
    /// mirrored instance whose <c>Record</c> may still be the placeholder <c>RaiseMirrored</c> minted, on a
    /// peer whose record delta rides a different surface at a different cadence. Every one of those is a
    /// race, and each one loses the whole screen.
    ///
    /// SO THE TRIGGER IS THE STATE, WHICH ARRIVES ON EVERY PEER BY ITSELF: the event's own record resolving
    /// (whoever answered it) plus the host's <c>S#&lt;id&gt;…ActiveMission</c> structural create landing on
    /// this peer's own site. Both are replicated facts on the rail, both converge without anybody clicking
    /// anything, and the watch is polled from <c>SyncEngine.Tick</c> — the same driver
    /// <c>EventPopup.DrainHeldRaises</c> and <c>ReplenishSync.ClientArrivalTick</c> already run on.
    ///
    /// IT IS NOT A QUORUM AND CANNOT BECOME ONE (P13). Every term is this peer's own: its own record, its
    /// own site graph, its own view queue. It counts no peers, waits for no acknowledgement and asks nobody
    /// to press anything — a peer whose partners are all AFK opens its squad screen exactly as fast.
    ///
    /// IT DOES NOT YANK, AND IT DOES NOT DOUBLE-QUEUE. <c>GeoscapeView.LaunchMission</c> →
    /// <c>ToDeploymentState</c>:596 QUEUES the screen, and <see cref="WindowOrder"/>'s open-screen hold keeps
    /// it in the queue until this peer is back on the map — the owner's 2026-08-07 ruling, unchanged.
    /// <see cref="MissionEncounterNav.ShouldOpenDeployment"/> and
    /// <see cref="MissionEncounterNav.AlreadyHeadedForDeployment"/> are REUSED rather than re-derived, so the
    /// peer that already answered (or already got there through the funnel) is never queued twice.
    ///
    /// AND IT WATCHES ONLY WINDOWS IT WAS SHOWN. <see cref="Watch"/> is armed from the raise itself and only
    /// when some choice can start a mission, so the sites that sprout missions all over the geoscape all game
    /// long can never navigate anybody: a mission this peer was not offered has no watch to fire.
    /// </summary>
    internal static class MissionArrivalNav
    {
        private sealed class Watched
        {
            internal string SiteRef;
            internal string VehicleRef;
            internal float ResolvedAt;   // realtimeSinceStartup of the first tick the record read Completed
        }

        // Bounded like EventPopup._held for the same reason: a peer sees a handful of mission windows, and a
        // map that only grows is a leak wearing a feature's name. Keyed by event id — a re-raise replaces.
        private const int MaxWatched = 32;

        /// <summary>How long a resolved mission event may wait for its <c>ActiveMission</c> to arrive before
        /// the watch gives up LOUDLY. The structural create rides the same diff cycle as the record delta, so
        /// this is orders of magnitude over the real gap — long enough to cover a peer that is mid-load, short
        /// enough that a mission which is never coming does not sit here for the rest of the session.</summary>
        internal const float ArrivalWindowSeconds = 60f;

        private static readonly Dictionary<string, Watched> _watched = new Dictionary<string, Watched>();

        internal static void Reset() { _watched.Clear(); }

        /// <summary>Arm the watch for a window this peer was actually shown. Called from BOTH raise paths —
        /// the host's own (<c>EventPopup.HostBroadcast</c>) and the mirror's
        /// (<c>EventPopup.RaiseMirrored</c>) — because a HOST can lose the answer race too, and then its
        /// native <c>SelectChoice</c>:612 never ran here either.</summary>
        internal static void Watch(string eventId, GeoscapeEventData data, string siteRef, string vehicleRef)
        {
            if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(siteRef)) return;
            if (!EventPopup.AnyChoiceStartsMission(data)) return;
            if (!_watched.ContainsKey(eventId) && _watched.Count >= MaxWatched)
            {
                Debug.LogWarning("[MP][mission] not watching '" + eventId + "' for its squad screen — " +
                                 MaxWatched + " mission windows are already being watched, which means the " +
                                 "watches are not retiring; this peer can still reach the mission from the " +
                                 "aircraft's Launch button");
                return;
            }
            _watched[eventId] = new Watched { SiteRef = siteRef, VehicleRef = vehicleRef, ResolvedAt = 0f };
        }

        /// <summary>PURE (RailCheck L197). Has the mission this window promised ARRIVED on this peer?
        /// Both halves are replicated state that converges on its own — the answer (whoever gave it) and the
        /// mission the host minted from it.</summary>
        internal static bool MissionHasArrived(bool answerResolved, bool choiceStartsMission, bool missionRunnable)
            => answerResolved && choiceStartsMission && missionRunnable;

        /// <summary>PURE (RailCheck L197). Has a resolved window waited too long for a mission that is never
        /// coming? Monotone in <paramref name="now"/> and bounded by a constant, so it cannot become an
        /// unbounded wait — the same argument <c>WindowOrder.SettleExpired</c> rests on.</summary>
        internal static bool ArrivalGivenUp(float resolvedAt, float now)
            => resolvedAt > 0f && now - resolvedAt >= ArrivalWindowSeconds;

        /// <summary>Driven from <c>SyncEngine.Tick</c>. One decision per watched window per frame; every exit
        /// retires the watch or says why it is still waiting.</summary>
        internal static void Tick(NetworkEngine engine)
        {
            if (_watched.Count == 0) return;
            if (engine == null || !engine.IsActiveSession) { _watched.Clear(); return; }
            var geo = GameUtl.CurrentLevel() == null
                ? null : GameUtl.CurrentLevel().GetComponent<GeoLevelController>();
            var view = geo?.View;
            if (view == null) return;   // no geoscape to navigate yet; the watch keeps waiting

            // Snapshot: the loop retires entries as it goes.
            foreach (var id in new List<string>(_watched.Keys))
            {
                try { Step(engine, geo, view, id, _watched[id]); }
                catch (Exception ex)
                {
                    _watched.Remove(id);
                    Debug.LogError("[MP][mission] arrival watch for '" + id + "' failed — this peer can still " +
                                   "reach the mission from the aircraft's Launch button: " + ex);
                }
            }
        }

        private static void Step(NetworkEngine engine, GeoLevelController geo, GeoscapeView view,
                                 string id, Watched w)
        {
            var rec = EventPopup.LiveRecord(id, null);
            var choices = geo.EventSystem?.GetEventByID(id, canFail: true)?.GeoscapeEventData?.Choices;
            int idx = rec == null ? -1 : rec.SelectedChoice;
            bool resolved = rec != null && rec.State == GeoscapeEventRecordState.Completed &&
                            choices != null && idx >= 0 && idx < choices.Count;
            if (!resolved) return;                       // still open on somebody's screen — nothing to open yet
            if (w.ResolvedAt <= 0f) w.ResolvedAt = Time.realtimeSinceStartup;

            if (!EventPopup.StartsMission(choices[idx]))
            {
                _watched.Remove(id);
                Debug.Log("[MP][mission] arrival watch for '" + id + "' retired — the answer (choice " + idx +
                          ") starts no mission, so there is no squad screen owed to anybody.");
                return;
            }

            var site = IdentityResolver.Resolve(geo, w.SiteRef, null) as GeoSite;
            var mission = site?.ActiveMission;
            if (!MissionHasArrived(true, true, mission != null && mission.IsRunnable))
            {
                if (!ArrivalGivenUp(w.ResolvedAt, Time.realtimeSinceStartup)) return;
                _watched.Remove(id);
                Debug.LogWarning("[MP][mission] arrival watch for '" + id + "' at " + w.SiteRef + " GIVEN UP — " +
                                 ArrivalWindowSeconds + "s after the answer resolved the site still has " +
                                 (site == null ? "not resolved at all" : mission == null
                                     ? "no ActiveMission" : "a mission that is not runnable") +
                                 ". " + MissionEncounterNav.NoDeploymentReason(engine.IsHost, mission == null));
                return;
            }

            // clickWasReplayed: TRUE on purpose. This seam holds no GeoscapeEvent instance to ask the memo
            // about, and it does not need one — the ONLY case NativeSelectChoiceRan answers true for is a host
            // whose own SelectChoice:598-613 ran, and :612 inside that same call already queued the screen, so
            // AlreadyHeadedForDeployment answers it one term over. Assuming "native did not run" can therefore
            // only ever cost a redundant question, never a duplicate screen.
            if (!MissionEncounterNav.ShouldOpenDeployment(true, engine.IsHost, true,
                                                          MissionEncounterNav.AlreadyHeadedForDeployment(view)))
            {
                _watched.Remove(id);
                Debug.Log("[MP][mission] arrival watch for '" + id + "' retired — this peer is already in, or " +
                          "queued for, UIStateRosterDeployment; re-issuing LaunchMission would leave a SECOND " +
                          "deployment request in a queue that is part of the save.");
                return;
            }

            _watched.Remove(id);
            view.LaunchMission(mission, IdentityResolver.Resolve(geo, w.VehicleRef, null) as GeoVehicle);
            Debug.Log("[MP][mission] " + (engine.IsHost ? "HOST" : "CLIENT") + " squad screen opened for '" + id +
                      "' at " + w.SiteRef + " from the MISSION'S ARRIVAL — the answer resolved (choice " + idx +
                      ") and this peer's own site now holds a runnable ActiveMission. Nothing here waited on a " +
                      "dialog teardown, and nothing waited on another player.");
        }
    }

    /// <summary>
    /// Sim gating (law 4b) at the mission-CANCEL funnel, the sibling of the launch capture and the reason
    /// a client may now sit on the deployment screen at all. <c>UIStateRosterDeployment.ToPreviousScreen</c>
    /// (:256-268) — the Back button, the Close button and the Cancel key alike — calls
    /// <c>_mission.Cancel()</c> before it pops the screen, and <c>GeoMission.Cancel</c> (GeoMission.cs:253)
    /// writes <c>Site.ActiveMission = null</c> and can <c>Site.DestroySite()</c>. On a projector client
    /// (law 3) that is a structural mutation the diff can NEVER correct — the diff is host-now vs
    /// host-before, so a mission only the client deleted is never mentioned again and the CRC backstop can
    /// only report it (DiffEngine.HandleCrcReport's "still diverged" arm). One peer glancing at the
    /// deployment screen and pressing Back would delete the mission from its own campaign.
    ///
    /// Blocking is also the RIGHT co-op semantic, not merely the safe one: backing out of a screen is a
    /// navigation gesture, and one peer declining to launch must not cancel the mission for the others.
    /// A host that really means to cancel still does, natively, and it reaches every client as the
    /// ordinary structural destroy.
    ///
    /// Discovered, not enumerated: <c>Cancel</c> is VIRTUAL with seven overrides in the shipped assembly
    /// (GeoCustomMission, GeoAncientSiteMission, GeoPhoenixBaseDefenseMission, GeoSabotageZoneMission and
    /// the three Steal* missions), and several do not call <c>base.Cancel()</c> — so a prefix on the base
    /// declaration alone would leave most real missions ungated. Sweeping the type hierarchy covers a DLC's
    /// or a mod's own subclass for free, the same way <c>VehicleGestureGate</c> resolves its rows by name.
    /// Void method; nothing dereferences a blocked call.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionCancelGate
    {
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary><c>AccessTools.GetTypesFromAssembly</c> and not <c>Assembly.GetTypes()</c>: with other
        /// mods loaded the game assembly reliably has a few types that fail to load, and the raw call
        /// throws <c>ReflectionTypeLoadException</c> — which PatchAll turns into one swallowed warning that
        /// kills every LATER patch in the same pass (RailCheck L23, the same trap an unbound
        /// AccessTools.Method sets). Harmony's helper returns the loadable ones instead.</summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(GeoMission).Assembly))
            {
                if (t == null || !typeof(GeoMission).IsAssignableFrom(t)) continue;
                var m = t.GetMethod("Cancel", BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        private static bool Prefix(GeoMission __instance)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true; // solo/host: native
            if (SyncApplyScope.Active) return true;                                      // an apply may reach it

            // Never silent (the dominant bug class): say whose cancel was refused and why. Log-once per
            // site — cancelling is a per-mission gesture, not a per-frame one.
            string who = IdentityResolver.RootRef(__instance?.Site) ?? "S#?";
            if (_logged.Add(who))
                Debug.Log("[MP][mission] CLIENT cancel of the mission at " + who + " BLOCKED — it deletes shared " +
                          "campaign state (Site.ActiveMission/DestroySite) the host owns; backing out of the " +
                          "deployment screen is navigation, not a cancellation for every peer");
            return false;
        }
    }
}
