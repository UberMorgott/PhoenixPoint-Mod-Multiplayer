using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Base.Serialization.General;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>The replicated half, and it is ONE identity: the site whose deployment-prep screen is open
    /// somewhere. Same shape as <see cref="DeployCountdownState"/> ("M#deploy") and for the same reason —
    /// a mod root on the generic value rail (0xAC) needs no surface id of its own, and the geoscape band
    /// 0xA0-0xBF has none free.
    ///
    /// THE ATTRIBUTE IS THE ROOT (2026-08-13). It is the whole mod-root contract
    /// (<see cref="IdentityResolver.RegisterModRoot"/>: "one plain class marked [SerializeType(SerializeAll)]
    /// so RailType.Get classifies it direct") and it was MISSING here while every sibling root
    /// (<c>ScrapCartState</c>, <c>MistState</c>, <c>DeployCountdownState</c>) carried it. Without it
    /// <c>RailMeta.HasPersistentMembers</c> is false, <c>RailType.Source</c> is "none", the walk emits ZERO
    /// fields (DiffEngine.cs:1113-1117) and the host's announcement never crosses the wire — so no client's
    /// button could ever have a site to point at. L424 arm (e) now executes that classification.</summary>
    [SerializeType(SerializeMembersByDefault = SerializeMembersType.SerializeAll)]
    public sealed class DeployPrepState
    {
        public Dictionary<string, string> Records = new Dictionary<string, string>();
    }

    /// <summary>
    /// SOMEBODY IS STANDING IN THE DEPLOYMENT PREP SCREEN, AND EVERY OTHER PEER GETS A DOOR INTO IT.
    ///
    /// THE HOLE THIS FILLS. The mission-start window is UNICAST to the peer that dispatched the aircraft
    /// (<c>EventPopup</c>:1896-1916) — deliberately, so three players do not each answer the same two info
    /// popups. The consequence was that only that peer could ever reach <c>UIStateRosterDeployment</c>: the
    /// others had no gesture at all, and every attempt to invent one ran into
    /// <see cref="MissionSync.Validate"/> refusing a launch built off a screen nobody could legitimately
    /// open. There is no lock and there never was one; there was no ENTRANCE.
    ///
    /// THE ENTRANCE IS A BUTTON, not a second popup: <c>Multiplayer.UI.PrepJoinButton</c> reads
    /// <see cref="State"/> and calls <see cref="Join"/>. <see cref="Join"/> re-issues the GAME's own
    /// <c>GeoscapeView.LaunchMission</c>:1025 — the identical call the dispatching peer's Confirm reaches
    /// — so both info popups are skipped and the joiner lands in the same native screen, built by the same
    /// constructor, with no mod-side duplicate of <c>ToDeploymentState</c> anywhere.
    ///
    /// IT IS NOT A QUORUM AND CANNOT BECOME ONE (P13, L84/L114). Nothing reads this root: no launch, no
    /// countdown, no barrier, no readiness predicate. A peer that never presses the button blocks nothing —
    /// the launch is still whoever presses START MISSION first, and <c>DeployCountdown</c> still drops the
    /// battle on the host's own clock with nobody's permission.
    ///
    /// LIFETIME IS THE DEPLOYMENT'S, NOT THE SCREEN'S — and that distinction IS the 2026-08-13 bug.
    /// <c>UIStateRosterDeployment.EnterState</c> announces; <c>ExitState</c> used to withdraw
    /// UNCONDITIONALLY, so the ordinary "we are not ready to drop yet, let us do other geoscape things
    /// first" Back press took the door away from everybody — including from the peer who had just pressed
    /// it, who then had no gesture left to get back into a screen his own aircraft was still parked for.
    /// The door now outlives the screen and is withdrawn only when the deployment behind it is actually
    /// over: <see cref="StillServable"/> asks the GAME's own question
    /// (<c>DeploymentWindowClose.Servable</c> → <c>GeoMission.GetDeploymentSources</c>), so a launch, a
    /// cancel and the departed-aircraft close all end it, while a mere Back does not.
    /// <see cref="ResetForReloadBoundary"/> is the belt for the one exit that may not run its state
    /// machine — the level teardown.
    ///
    /// A ROOT THAT OUTLIVES ITS LAST <c>ExitState</c> CAN GO STALE (the aircraft flies off while nobody is
    /// standing in the screen), so the DISPLAY asks the same question every frame:
    /// <see cref="LiveSiteRef"/> is what the button reads, and it is "" the moment this peer's own graph
    /// could not serve the deployment. A stale announcement is therefore inert, never a dead button.
    /// </summary>
    internal static class DeployPrep
    {
        internal const string RootKey = "M#prep";

        /// <summary>Host writes it, the value rail mirrors it, every peer's button reads it.</summary>
        internal static readonly DeployPrepState State = new DeployPrepState();

        internal static void Register() => IdentityResolver.RegisterModRoot(RootKey, State);

        /// <summary>The site THIS peer announced. Peer-local by construction — it is the answer to "may I
        /// withdraw the announcement", which is a question about this peer's own screen and nothing a
        /// mirror could get right.
        ///
        /// Keyed per stable mission identity: two aircraft preparing different POIs never overwrite or
        /// withdraw one another.</summary>
        private static readonly Dictionary<string, string> _mine = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>HOST-ONLY: whose announcement the root currently carries (0 = this host's own screen).
        /// The one thing a withdrawal-on-drop needs and nothing else knows — <c>ExitState</c> is the only
        /// other way this root is ever cleared, and a peer that crashed or lost its socket inside the prep
        /// screen will never run it.</summary>
        private static readonly Dictionary<string, ulong> _announcers = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private static readonly HashSet<string> _suppressNextEntitledEnter = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>PURE (RailCheck L424). IS THE DOOR ON SCREEN? Three booleans, and the point is which
        /// three: a session, an announced site, and THIS peer's own idle geoscape. There is no term for
        /// what any other peer has done, pressed or acknowledged — that is the no-quorum mandate expressed
        /// as an arity.</summary>
        internal static bool ShowsButton(bool inSession, string siteRef, bool idleGeoscape) =>
            inSession && !string.IsNullOrEmpty(siteRef) && idleGeoscape;

        /// <summary>PURE (RailCheck L424 arm (f)). IS THIS PEER STANDING ON THE PLANET with nothing else open?
        /// Takes the view state's TYPE, not the instance, so the harness can execute the real answer for the
        /// real classes headlessly — a geoscape view state cannot be constructed outside a running level, and
        /// an `is` chain in the button would have been untestable exactly where it was wrong.
        ///
        /// TWO STATES, and the second one IS the 2026-08-13 bug. The gate used to be
        /// <c>UIStateNothingSelected</c> alone, on the argument that a selected aircraft means the site
        /// context menu is up and a second offer over it is clutter. Measured across the whole announce
        /// window of the failing session (21:20:20-21:21:25), EVERY <c>[MP][uirepaint]</c> line on both
        /// clients read <c>UIStateVehicleSelected</c> or <c>UIStateGeoscapeEvent</c> — <c>NothingSelected</c>
        /// NEVER OCCURRED, because the aircraft the deployment is about is exactly what the player has
        /// selected. The published root was correct for seven minutes and the button was suppressed the
        /// entire time. Both states are the bare globe with a selection marker
        /// (<c>UIStateVehicleSelected</c> owns <c>_selectionMarker</c> + reachable markers, same as
        /// <c>UIStateNothingSelected</c>); every tab, panel, roster, modal and event window is a DIFFERENT
        /// class and still hides the button with no per-screen list to maintain. <c>UIStateViewVehicle</c> is
        /// deliberately NOT here — it drives the roster tabs and character-progression modules, i.e. a
        /// screen.</summary>
        internal static bool IdleGeoscape(Type viewState) =>
            viewState != null && (typeof(UIStateNothingSelected).IsAssignableFrom(viewState) ||
                                  typeof(UIStateVehicleSelected).IsAssignableFrom(viewState));

        /// <summary>PURE (RailCheck L424). MAY THIS WITHDRAWAL CLEAR THE ROOT? Only the peer whose own
        /// announcement is still the live one — otherwise a stale close from a screen that was already
        /// superseded would take somebody else's door away.</summary>
        internal static bool ClearsOnWithdraw(string announced, string live) =>
            !string.IsNullOrEmpty(announced) && string.Equals(announced, live, StringComparison.Ordinal);

        /// <summary>IS THERE STILL A DEPLOYMENT BEHIND THE ANNOUNCEMENT, on THIS peer's own graph? Not a new
        /// predicate: it is <c>DeploymentWindowClose.Servable</c> (<c>DeploymentWindow.cs</c>:521), the same
        /// <c>GeoMission.GetDeploymentSources</c> question that already closes an unservable squad screen and
        /// its brief — so the door and the screen it opens can never disagree about whether there is anything
        /// to enrol.
        ///
        /// FALSE ON A THROW, deliberately: every caller treats false as "withdraw / hide", which is the
        /// pre-fix behaviour, and a door that flickers off is strictly better than one that cannot be
        /// taken down.</summary>
        private static bool StillServable(string siteRef)
        {
            try
            {
                if (string.IsNullOrEmpty(siteRef)) return false;
                var geo = GenericApplier.GeoLevel();
                var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                var mission = site?.ActiveMission;
                return mission != null && DeploymentWindowClose.Servable(mission);
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][deploy] could not ask whether " + (siteRef ?? "S#?") + " is still " +
                               "deployable — treating it as gone, so the join button hides and the next " +
                               "screen exit withdraws the announcement: " + ex);
                return false;
            }
        }

        /// <summary>THE REF THE BUTTON READS, or "" when the door leads nowhere on this peer. Folded into the
        /// siteRef term ON PURPOSE rather than added as a fourth <see cref="ShowsButton"/> parameter: L424
        /// arm (a) pins that arity at three because a fourth term is exactly where "…and peer N has joined"
        /// would one day be written. This one is a local fact about this peer's own graph, and it stays out
        /// of the predicate the law guards.</summary>
        internal static KeyValuePair<string, string>[] LiveRecords() => State.Records
            .Where(x => RecordMatches(x.Key, x.Value) && StillServable(x.Value))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        internal static string LiveSiteRef() => LiveRecords().Select(x => x.Value).FirstOrDefault() ?? "";

        /// <summary>The announcement while its campaign mission still exists, independently of whether a
        /// deployment source is parked there this frame.  A KeepEncounter mission survives its aircraft
        /// departing; retaining this identity makes the door hide while unservable and become live again on
        /// return, without cancelling/re-offering the mission.</summary>
        internal static string PersistentSiteRef()
            => State.Records.OrderBy(x => x.Key, StringComparer.Ordinal).Where(x => RecordMatches(x.Key, x.Value))
                .Select(x => x.Value).FirstOrDefault() ?? "";
        internal static bool HasPersistentMissionAt(string siteRef) => State.Records.Any(x =>
            string.Equals(x.Value, siteRef, StringComparison.Ordinal) && RecordMatches(x.Key, x.Value));

        internal static bool SameMission(string recordedKey, string liveKey) =>
            !string.IsNullOrEmpty(recordedKey) && string.Equals(recordedKey, liveKey, StringComparison.Ordinal);

        private static bool RecordMatches(string missionKey, string siteRef)
        {
            try
            {
                if (string.IsNullOrEmpty(missionKey) || string.IsNullOrEmpty(siteRef)) return false;
                var geo = GenericApplier.GeoLevel();
                var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                return SameMission(missionKey, DurableWindowRegistry.StableMissionSubject(site?.ActiveMission));
            }
            catch { return false; }
        }

        private static bool MissionExists(string siteRef)
        {
            try
            {
                if (string.IsNullOrEmpty(siteRef)) return false;
                var geo = GenericApplier.GeoLevel();
                var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                return site?.ActiveMission != null;
            }
            catch { return false; }
        }

        /// <summary>PURE (RailCheck L424 arm (g)). IS A RE-OFFER OF THIS SITE'S MISSION REDUNDANT, because the
        /// door into it is already standing? The 2026-08-13 report's second half, and it is the SAME root as
        /// the first: a peer that could not see the button reached for the only other entrance it had, the
        /// site's own encounter row — and that row does not re-open a screen, it runs
        /// <c>MissionReoffer.HostReoffer</c>, which CANCELS the live mission and re-triggers the encounter, so
        /// a fresh START/CANCEL dialog was broadcast to every peer for a mission whose start had already been
        /// taken. The peer that answered that stale dialog got "another player already answered this event".
        ///
        /// NOT A BLOCKER (P13). It refuses a re-offer only while <see cref="LiveSiteRef"/> still serves the
        /// SAME site, i.e. exactly while this peer has a working DEPLOYMENT PREP button for it; the moment the
        /// drop launches, is cancelled or its last carrier leaves, the ref goes "" and the ordinary re-offer
        /// is available again. Nothing waits on another human either way.</summary>
        internal static bool ReofferIsRedundant(string liveDoorSiteRef, string siteRef) =>
            !string.IsNullOrEmpty(siteRef) && string.Equals(liveDoorSiteRef, siteRef, StringComparison.Ordinal);

        /// <summary>The only writer of the root. MarkDirty for the same reason
        /// <c>DeployCountdown.ClearPending</c> has it: on the host the write is local, so nothing else
        /// would repaint the screen it changes.</summary>
        private static void Publish(string missionKey, string siteRef)
        {
            if (string.IsNullOrEmpty(missionKey)) return;
            if (string.IsNullOrEmpty(siteRef)) State.Records.Remove(missionKey);
            else State.Records[missionKey] = siteRef;
            OpenUiRepaint.MarkDirty();
        }

        internal static void Announce(GeoMission mission)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                var siteRef = IdentityResolver.RootRef(mission?.Site);
                var missionKey = DurableWindowRegistry.StableMissionSubject(mission);
                if (string.IsNullOrEmpty(siteRef) || string.IsNullOrEmpty(missionKey)) return;
                _mine[missionKey] = siteRef;
                if (engine.IsHost) { _announcers[missionKey] = 0; Publish(missionKey, siteRef); }
                else
                {
                    IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpPrepOpen,
                        "prep open " + missionKey + " at " + siteRef,
                        w => { w.Write(missionKey); w.Write(siteRef); });
                }
                MpLog.Log("[MP][deploy] deployment PREP announced for " + siteRef + " — every other peer on " +
                          "an idle geoscape now has the join button. Nobody has to press it: it is a door, " +
                          "not a gate (P13).");
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][deploy] prep announce failed — the other peers get no join button for " +
                               "this deployment; nothing else is affected: " + ex);
            }
        }

        /// <summary>Leaving the prep screen is NOT the end of the deployment. The one guard, at the one seam
        /// every exit routes through (<see cref="DeployPrepExitPatch"/>): while the site can still be
        /// deployed to, the announcement STAYS — that is the whole "we are not ready yet, let us do other
        /// geoscape things and come back" flow, and it is what puts the door on the leaving peer's own
        /// geoscape too. Only a deployment that is really over (launched, cancelled, its last aircraft
        /// departed) withdraws.</summary>
        internal static void Withdraw(GeoMission mission)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                string key = DurableWindowRegistry.StableMissionSubject(mission);
                string siteRef = IdentityResolver.RootRef(mission?.Site);
                if (string.IsNullOrEmpty(key) || !_mine.ContainsKey(key) || engine == null || !engine.IsActiveSession)
                { if (!string.IsNullOrEmpty(key)) _mine.Remove(key); return; }
                if (RecordMatches(key, siteRef))
                {
                    MpLog.Log("[MP][deploy] left the deployment prep for " + siteRef + " but its ActiveMission " +
                              "still exists — the persistent door is retained. It hides while no deployment " +
                              "source is present and becomes live again when an aircraft returns; no mission " +
                              "Cancel or re-offer is involved.");
                    return;
                }
                _mine.Remove(key);
                if (engine.IsHost)
                {
                    Publish(key, "");
                    return;
                }
                IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpPrepClose,
                    "prep close " + key + " at " + siteRef,
                    w => { w.Write(key); w.Write(siteRef); });
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][deploy] prep withdraw failed — the join button may linger until the next " +
                               "announcement or the reload boundary clears it: " + ex);
            }
        }

        // ─── HOST: the two client intents (ops 5/6 on 0xB8) ─────────────────

        internal static void HandleOpen(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string requestedKey = WireString.ReadKey(r);
            string siteRef = WireString.ReadKey(r);
            var geo = GenericApplier.GeoLevel();
            var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
            if (!OpenMatches(requestedKey, site?.ActiveMission))
            {
                // NEVER SILENT: the sender IS standing in a deployment screen, so a host that cannot see the
                // mission behind it is a rail defect, not a race the player caused.
                MpLog.LogWarning("[MP][deploy] prep-open from peer " + senderPeerId + " for " +
                                 (siteRef ?? "S#?") + " key=" + (requestedKey ?? "<none>") +
                                 " IGNORED — that exact ActiveMission is not current there. No join button is " +
                                 "published; a delayed A announcement can never publish replacement B.");
                return;
            }
            _announcers[requestedKey] = senderPeerId;
            Publish(requestedKey, siteRef);
            MpLog.Log("[MP][deploy] HOST published prep " + requestedKey + " at " + siteRef +
                      " nonce=" + nonce + " peer=" + senderPeerId);
        }

        internal static bool OpenMatches(string requestedKey, GeoMission currentMission) =>
            SameMission(requestedKey, DurableWindowRegistry.StableMissionSubject(currentMission));

        internal static bool CloseExact(IDictionary<string, string> records, string missionKey, string siteRef)
        {
            if (records == null || string.IsNullOrEmpty(missionKey) || string.IsNullOrEmpty(siteRef) ||
                !records.TryGetValue(missionKey, out var recordedSite) ||
                !string.Equals(recordedSite, siteRef, StringComparison.Ordinal)) return false;
            return records.Remove(missionKey);
        }

        /// <summary>THE ANNOUNCING PEER IS GONE. Its <c>ExitState</c> will never run, so without this the door
        /// it left open stays on every other peer's geoscape until the reload boundary and leads nowhere
        /// (<see cref="Join"/> degrades to a warning, so it is a dead button, never a hang).
        ///
        /// NOT A HEARTBEAT AND NOT A QUORUM: it rides the drop the host has ALREADY observed
        /// (<c>SessionManager.PausePeer</c> / <c>RemoveClient</c>), asks nobody for anything, and clears only
        /// the announcement that peer itself made — the peer-scoped rule <see cref="ClearsOnWithdraw"/>
        /// states for the ordinary exit.</summary>
        internal static void OnPeerGone(ulong steamId)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsHost || steamId == 0) return;
            foreach (var key in _announcers.Where(x => x.Value == steamId).Select(x => x.Key).ToArray())
                _announcers.Remove(key); // mission-owned door survives its announcing socket
        }

        internal static void HandleClose(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string missionKey = WireString.ReadKey(r);
            string siteRef = WireString.ReadKey(r);
            if (!CloseExact(State.Records, missionKey, siteRef))
            {
                MpLog.Log("[MP][deploy] stale prep-close from peer " + senderPeerId + " ignored for " +
                          missionKey + " at " + siteRef + " — no exact record pair exists; a delayed close " +
                          "for mission A cannot remove mission B on the same site.");
                return;
            }
            _announcers.Remove(missionKey);
            OpenUiRepaint.MarkDirty();
        }

        /// <summary>Host-authoritative exact publication. This seam never reaches the client intent rail.</summary>
        internal static bool PublishMission(GeoMission mission)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return false;
            string missionKey = DurableWindowRegistry.StableMissionSubject(mission);
            string siteRef = IdentityResolver.RootRef(mission?.Site);
            if (string.IsNullOrEmpty(missionKey) || string.IsNullOrEmpty(siteRef) ||
                !OpenMatches(missionKey, mission?.Site?.ActiveMission)) return false;
            _announcers[missionKey] = 0;
            Publish(missionKey, siteRef);
            MpLog.Log("[MP][deploy] HOST published exact mission door " + missionKey + " at " + siteRef +
                      " without an intent/send path.");
            return true;
        }

        internal static void SuppressNextEntitledEnter(string missionKey)
        {
            if (!string.IsNullOrEmpty(missionKey)) _suppressNextEntitledEnter.Add(missionKey);
        }

        internal static bool ConsumeEntitledEnter(string missionKey)
            => !string.IsNullOrEmpty(missionKey) && _suppressNextEntitledEnter.Remove(missionKey);

        internal static void AnnounceFromEnter(GeoMission mission)
        {
            string key = DurableWindowRegistry.StableMissionSubject(mission);
            if (ConsumeEntitledEnter(key))
            {
                MpLog.Log("[MP][deploy] entitled arrival entered prep for " + key +
                          "; duplicate OpPrepOpen suppressed (host already published the exact door).");
                return;
            }
            Announce(mission);
        }

        // ─── THE DOOR ITSELF ────────────────────────────────────────────────

        /// <summary>
        /// The joining peer's whole gesture, and it is ONE native call.
        ///
        /// The container is not optional and null is not a safe default:
        /// <c>GeoMission.GetDeploymentSources</c>:1479-1486 returns an EMPTY list (plus its own LogError) for
        /// a <c>DeployOnlyFromActiveVehicle</c> mission when <c>priorityContainer</c> is null — which
        /// <c>DeploymentRosterRefresh.IsServable</c> then reads as "nothing to enrol" and closes the screen
        /// again in the same frame. So the pick is the GAME's own, verbatim from
        /// <c>GeoscapeView.LaunchMission</c>:1031-1037: a player vehicle on the site that actually carries
        /// somebody. Null is still legal for a base-defense mission, whose container is the SITE
        /// (<c>GetDeploymentSources</c>:1494).
        ///
        /// NOTHING IS SENT. This is local navigation only — <c>UIStateRosterDeployment</c> is declared
        /// LocalOnly in <see cref="GeoWindowCoverage"/> precisely because every peer navigates its own — and
        /// the launch this peer may go on to press is the ordinary 0xB8 <c>OpLaunch</c> the dispatching peer
        /// uses, validated host-side exactly as before.
        /// </summary>
        internal static void Join(string missionKey)
        {
            try
            {
                var geo = GenericApplier.GeoLevel();
                if (string.IsNullOrEmpty(missionKey) || !State.Records.TryGetValue(missionKey, out var siteRef)) return;
                var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
                var mission = site?.ActiveMission;
                if (geo?.View == null || mission == null ||
                    !SameMission(missionKey, DurableWindowRegistry.StableMissionSubject(mission)))
                {
                    MpLog.LogWarning("[MP][deploy] JOIN pressed for " + (siteRef ?? "S#?") + " but this " +
                                     "peer has no ActiveMission there — it launched, was cancelled, or the site " +
                                     "is not mirrored yet. Nothing opened, nothing was sent; the button clears " +
                                     "when the announcing peer leaves its screen.");
                    return;
                }
                var vehicle = site.GetPlayerVehiclesOnSite()
                                  ?.FirstOrDefault(v => v != null && v.GetCharacterCount() > 0);
                MpLog.Log("[MP][deploy] JOIN pressed — opening the deployment prep screen for " + siteRef +
                          " through the game's own GeoscapeView.LaunchMission, container=" +
                          (vehicle == null ? "<site>" : vehicle.name) + ". Both info popups are skipped and no " +
                          "peer is asked for permission.");
                geo.View.LaunchMission(mission, vehicle);
            }
            catch (Exception ex)
            {
                MpLog.LogError("[MP][deploy] JOIN failed — this peer stays on the geoscape and every other peer " +
                               "is unaffected: " + ex);
            }
        }

        internal static void ResetForReloadBoundary()
        {
            _mine.Clear();
            _announcers.Clear();
            _suppressNextEntitledEnter.Clear();
            State.Records.Clear();
        }
    }

    /// <summary>Announce on the way in. A POSTFIX, so the screen the announcement is about has actually been
    /// built — <see cref="DeploymentRosterRefresh"/>'s own postfix may still close it again as unservable,
    /// and that close runs <c>ExitState</c>, which withdraws.</summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "EnterState")]
    internal static class DeployPrepEnterPatch
    {
        private static void Postfix(UIStateRosterDeployment __instance) => DeployPrep.AnnounceFromEnter(__instance?.Mission);
    }

    /// <summary>Withdraw on the way out — the ONE hook that covers all three ends of a prep window: the
    /// launch (the screen exits into the tactical load), Back, and the unservable close.</summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "ExitState")]
    internal static class DeployPrepExitPatch
    {
        private static void Postfix(UIStateRosterDeployment __instance) => DeployPrep.Withdraw(__instance?.Mission);
    }
}
