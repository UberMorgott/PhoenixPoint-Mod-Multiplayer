using System;
using System.IO;
using System.Linq;
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
    /// 0xA0-0xBF has none free.</summary>
    public sealed class DeployPrepState
    {
        public string SiteRef = "";
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
    /// LIFETIME IS THE SCREEN'S OWN. <c>UIStateRosterDeployment.EnterState</c> announces and
    /// <c>ExitState</c> withdraws, which is why there is no separate "cleared on launch / cancel /
    /// unservable" bookkeeping: all three END that screen. A launch exits it into the tactical load, Back
    /// exits it, and <c>DeploymentWindowClose.CloseCurrent</c> (the unservable arm,
    /// <c>DeploymentWindow.cs</c>:445) exits it through the game's own door.
    /// <see cref="ResetForReloadBoundary"/> is the belt for the one exit that may not run its state
    /// machine — the level teardown.
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
        /// ponytail: ONE slot, last announcement wins. Two peers prepping two different sites at once
        /// leaves the button pointing at the more recent one, and a joiner leaving the screen clears an
        /// announcement the original peer is still standing in. Upgrade path if that is ever felt: make
        /// <see cref="DeployPrepState"/> carry a set of site refs, not a second mechanism here.</summary>
        private static string _mine;

        /// <summary>HOST-ONLY: whose announcement the root currently carries (0 = this host's own screen).
        /// The one thing a withdrawal-on-drop needs and nothing else knows — <c>ExitState</c> is the only
        /// other way this root is ever cleared, and a peer that crashed or lost its socket inside the prep
        /// screen will never run it.</summary>
        private static ulong _announcer;

        /// <summary>PURE (RailCheck L424). IS THE DOOR ON SCREEN? Three booleans, and the point is which
        /// three: a session, an announced site, and THIS peer's own idle geoscape. There is no term for
        /// what any other peer has done, pressed or acknowledged — that is the no-quorum mandate expressed
        /// as an arity.</summary>
        internal static bool ShowsButton(bool inSession, string siteRef, bool idleGeoscape) =>
            inSession && !string.IsNullOrEmpty(siteRef) && idleGeoscape;

        /// <summary>PURE (RailCheck L424). MAY THIS WITHDRAWAL CLEAR THE ROOT? Only the peer whose own
        /// announcement is still the live one — otherwise a stale close from a screen that was already
        /// superseded would take somebody else's door away.</summary>
        internal static bool ClearsOnWithdraw(string announced, string live) =>
            !string.IsNullOrEmpty(announced) && string.Equals(announced, live, StringComparison.Ordinal);

        /// <summary>The only writer of the root. MarkDirty for the same reason
        /// <c>DeployCountdown.ClearPending</c> has it: on the host the write is local, so nothing else
        /// would repaint the screen it changes.</summary>
        private static void Publish(string siteRef)
        {
            State.SiteRef = siteRef ?? "";
            OpenUiRepaint.MarkDirty();
        }

        internal static void Announce(GeoMission mission)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession) return;
                var siteRef = IdentityResolver.RootRef(mission?.Site);
                if (string.IsNullOrEmpty(siteRef)) return;
                _mine = siteRef;
                if (engine.IsHost) { _announcer = 0; Publish(siteRef); }
                else
                {
                    IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpPrepOpen,
                        "prep open " + siteRef, w => w.Write(siteRef));
                }
                Debug.Log("[MP][deploy] deployment PREP announced for " + siteRef + " — every other peer on " +
                          "an idle geoscape now has the join button. Nobody has to press it: it is a door, " +
                          "not a gate (P13).");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][deploy] prep announce failed — the other peers get no join button for " +
                               "this deployment; nothing else is affected: " + ex);
            }
        }

        internal static void Withdraw()
        {
            string mine = _mine;
            _mine = null;
            try
            {
                var engine = NetworkEngine.Instance;
                if (mine == null || engine == null || !engine.IsActiveSession) return;
                if (engine.IsHost)
                {
                    if (ClearsOnWithdraw(mine, State.SiteRef)) Publish("");
                    return;
                }
                IntentRail.Send(SurfaceIds.GeoMissionIntent, MissionSync.OpPrepClose,
                    "prep close " + mine, w => w.Write(mine));
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][deploy] prep withdraw failed — the join button may linger until the next " +
                               "announcement or the reload boundary clears it: " + ex);
            }
        }

        // ─── HOST: the two client intents (ops 5/6 on 0xB8) ─────────────────

        internal static void HandleOpen(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = WireString.ReadKey(r);
            var geo = GenericApplier.GeoLevel();
            var site = geo == null ? null : IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
            if (site?.ActiveMission == null)
            {
                // NEVER SILENT: the sender IS standing in a deployment screen, so a host that cannot see the
                // mission behind it is a rail defect, not a race the player caused.
                Debug.LogWarning("[MP][deploy] prep-open from peer " + senderPeerId + " for " +
                                 (siteRef ?? "S#?") + " IGNORED — the host has no ActiveMission there. No join " +
                                 "button is published; that peer's own screen is untouched.");
                return;
            }
            _announcer = senderPeerId;
            Publish(siteRef);
            Debug.Log("[MP][deploy] HOST published prep " + siteRef + " nonce=" + nonce + " peer=" + senderPeerId);
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
            if (engine == null || !engine.IsHost || steamId == 0 || _announcer != steamId) return;
            _announcer = 0;
            if (string.IsNullOrEmpty(State.SiteRef)) return;
            Debug.Log("[MP][deploy] the peer standing in the deployment prep for " + State.SiteRef +
                      " is gone — withdrawing its announcement, because it will never run ExitState itself. " +
                      "Every other peer's join button clears; nothing else changes.");
            Publish("");
        }

        internal static void HandleClose(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string siteRef = WireString.ReadKey(r);
            if (!ClearsOnWithdraw(siteRef, State.SiteRef))
            {
                Debug.Log("[MP][deploy] prep-close from peer " + senderPeerId + " for " + (siteRef ?? "S#?") +
                          " landed on '" + State.SiteRef + "' — a newer announcement already took the slot, so " +
                          "the live door stays open.");
                return;
            }
            _announcer = 0;
            Publish("");
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
        internal static void Join()
        {
            try
            {
                var geo = GenericApplier.GeoLevel();
                var site = geo == null ? null : IdentityResolver.Resolve(geo, State.SiteRef, null) as GeoSite;
                var mission = site?.ActiveMission;
                if (geo?.View == null || mission == null)
                {
                    Debug.LogWarning("[MP][deploy] JOIN pressed for " + (State.SiteRef ?? "S#?") + " but this " +
                                     "peer has no ActiveMission there — it launched, was cancelled, or the site " +
                                     "is not mirrored yet. Nothing opened, nothing was sent; the button clears " +
                                     "when the announcing peer leaves its screen.");
                    return;
                }
                var vehicle = site.GetPlayerVehiclesOnSite()
                                  ?.FirstOrDefault(v => v != null && v.GetCharacterCount() > 0);
                Debug.Log("[MP][deploy] JOIN pressed — opening the deployment prep screen for " + State.SiteRef +
                          " through the game's own GeoscapeView.LaunchMission, container=" +
                          (vehicle == null ? "<site>" : vehicle.name) + ". Both info popups are skipped and no " +
                          "peer is asked for permission.");
                geo.View.LaunchMission(mission, vehicle);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][deploy] JOIN failed — this peer stays on the geoscape and every other peer " +
                               "is unaffected: " + ex);
            }
        }

        internal static void ResetForReloadBoundary()
        {
            _mine = null;
            _announcer = 0;
            State.SiteRef = "";
        }
    }

    /// <summary>Announce on the way in. A POSTFIX, so the screen the announcement is about has actually been
    /// built — <see cref="DeploymentRosterRefresh"/>'s own postfix may still close it again as unservable,
    /// and that close runs <c>ExitState</c>, which withdraws.</summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "EnterState")]
    internal static class DeployPrepEnterPatch
    {
        private static void Postfix(UIStateRosterDeployment __instance) => DeployPrep.Announce(__instance?.Mission);
    }

    /// <summary>Withdraw on the way out — the ONE hook that covers all three ends of a prep window: the
    /// launch (the screen exits into the tactical load), Back, and the unservable close.</summary>
    [HarmonyPatch(typeof(UIStateRosterDeployment), "ExitState")]
    internal static class DeployPrepExitPatch
    {
        private static void Postfix() => DeployPrep.Withdraw();
    }
}
