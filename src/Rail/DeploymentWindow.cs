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

        private static readonly FieldInfo MissionField = AccessTools.Field(typeof(UIStateRosterDeployment), "_mission");
        private static readonly FieldInfo ContainersField = AccessTools.Field(typeof(UIStateRosterDeployment), "_characterContainers");
        private static readonly FieldInfo InitialContainerField = AccessTools.Field(typeof(UIStateRosterDeployment), "_initialContainer");
        private static readonly FieldInfo SelectedField = AccessTools.Field(typeof(UIStateRosterDeployment), "_selectedDeployment");

        private static bool _bindLogged;

        /// <summary>PURE (RailCheck L176). Is a re-ask WORTH LOGGING as a change? Only the shape question —
        /// the re-ask itself is unconditional, because "the queued answer happens to equal the live one" is
        /// not knowable without asking.</summary>
        internal static bool SnapshotWentStale(int queuedSquad, int liveSquad) => queuedSquad != liveSquad;

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
        private static float _nextDecrementAt;
        private static bool _committed;

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
                if (_pending != null) return false;   // already counting for somebody; a second press waits

                // Launch's own default (GeoMission.cs:227-235): a null argument means "the squad the mission
                // already holds". Resolve it HERE so the re-issue five seconds later is the same launch.
                _pending = mission;
                _pendingSquad = squad ?? mission?.Squad;
                _nextDecrementAt = Time.realtimeSinceStartup + 1f;
                State.SiteRef = IdentityResolver.RootRef(mission?.Site) ?? "";
                State.SecondsLeft = CountdownSeconds;
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

                if (State.SecondsLeft > 1)
                {
                    State.SecondsLeft--;
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
            State.SiteRef = "";
            State.SecondsLeft = 0;
        }

        /// <summary>Mod-root contract (IdentityResolver.cs:205-206): mod state must be EMPTY at every reload
        /// boundary. It always is between battles — a countdown either launched or was cancelled — but the
        /// host may be torn down mid-count by something else entirely, and a stale number left on the root
        /// would be baselined and then never emitted again.</summary>
        internal static void ResetForReloadBoundary()
        {
            ClearPending();
            _committed = false;
            _nextDecrementAt = 0f;
        }

        internal static void Reset() => ResetForReloadBoundary();
    }

    /// <summary>
    /// The deployment countdown as mod-state (root <c>"M#deploy"</c>, contract at
    /// <see cref="IdentityResolver.RegisterModRoot"/>). Two fields and no more: WHERE the drop is going, so
    /// a peer can tell one countdown from the next, and HOW MANY SECONDS ARE LEFT, which is the host's own
    /// counter rather than a deadline (the game clock is paused behind the deployment screen). Nothing here
    /// is a decision — the launch is <see cref="DeployCountdown"/>'s, host-side, off host objects.
    /// </summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class DeployCountdownState
    {
        public string SiteRef = "";
        public int SecondsLeft;
    }
}
