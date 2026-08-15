using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L402 — the owner's deployment rule, second half: a mission-deployment window disappears for
    /// EVERY peer once the LAST aircraft that could deploy to it has flown off the mission's site. The first
    /// half (the mission ended) is <c>WindowOrder.DropResolvedSubjects</c> and is not this law's subject.
    ///
    /// The teardown machinery was complete and wired long before this law existed — capture on
    /// <c>StartTravel</c>, recompute sources, tombstone every copy, <c>RemoveAllCarriers</c>, broadcast —
    /// and L388 guards its ordering. What was missing was SELECTION: the departure bound only occurrences
    /// carrying a bare <c>RootRef(site)</c> subject, which the arrival-watch shape has and the deployment
    /// window does not, so the one window this was built for was never bound and never closed.
    ///
    /// So the arms below are about the OUTCOME on the shapes production actually mints, plus the two things
    /// the owner said must not happen: the window must not go while another aircraft can still deploy, and
    /// the mission itself must survive.</summary>
    internal static class L402_ADepartedLastSourceClosesTheWindowEverywhere
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
                                         BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class Carrier : IDurableOccurrenceCarrier
        {
            internal int Removed; internal TerminalReason? Reason;
            public void RemoveWithoutCallback(TerminalReason reason) { Removed++; Reason = reason; }
        }

        internal static IEnumerable<string> Check()
        {
            // The EXACT subjects production mints. DurableWindowRegistry.CaptureDeployment passes a single
            // StableMissionSubject; the arrival watch passes MissionOccurrenceSubjects' triple.
            const string site = "S#7", other = "S#9", vehicle = "V#1";
            string stable = DurableWindowRegistry.StableMissionSubject(site, "mission-def-guid");
            string elsewhere = DurableWindowRegistry.StableMissionSubject(other, "mission-def-guid");
            if (string.IsNullOrEmpty(stable) || stable == elsewhere)
            { yield return "L402 premise-changed: stable-mission-subject-no-longer-identifies-site-and-def"; yield break; }

            var deployment = new OccurrenceId("DeploymentPreparing", "deployment:t", new[] { stable });
            // STRING LITERAL on purpose (a game type's ToString reaches GameUtl.Game and throws out of game).
            // Guarded, so renaming the family cannot leave the arrival arm passing against nothing.
            var arrival = new OccurrenceId("Modal:GeoAmbushBrief", "t",
                DurableWindowRegistry.MissionOccurrenceSubjects(site, vehicle, stable));
            if (!DurableWindowRegistry.IsMissionOfferOccurrence(arrival))
            { yield return "L402 premise-changed: modal-brief-is-no-longer-a-mission-offer-family"; yield break; }

            // ── selection: the shape the window is actually minted with must bind ──
            if (!MissionSync.BindsDeparture(deployment, InboxLifecycle.Open, stable))
                yield return "L402 deployment-window-not-bound-to-the-departure: the single-subject shape " +
                             "CaptureDeployment mints was skipped, so the aircraft leaves and the window " +
                             "stays open on every peer — the exact gap this law exists for";
            if (!MissionSync.BindsDeparture(arrival, InboxLifecycle.Open, stable))
                yield return "L402 arrival-window-not-bound-to-the-departure: the three-subject shape " +
                             "regressed while widening the filter for the single-subject one";

            // ── and it must stay a MISSION-identity match, not "anything on this site" ──
            if (MissionSync.BindsDeparture(new OccurrenceId("DeploymentPreparing", "t", new[] { elsewhere }),
                    InboxLifecycle.Open, stable))
                yield return "L402 departure-bound-a-window-of-another-site-or-mission";
            if (MissionSync.BindsDeparture(new OccurrenceId("event", "t", new[] { stable }),
                    InboxLifecycle.Open, stable))
                yield return "L402 departure-bound-a-window-that-is-not-a-deployment-or-mission-brief";
            if (MissionSync.BindsDeparture(deployment, InboxLifecycle.Removed, stable))
                yield return "L402 departure-bound-an-already-removed-window";

            // ── outcome: last source gone closes it for EVERY membership, with the carrier torn down ──
            var members = new[] { new MembershipId("host"), new MembershipId("client") };
            var ledger = new HostLedger(new[] {
                new InboxEntry(deployment, members[0], InboxLifecycle.Open, default(CanonicalChoiceId), 2, 0),
                new InboxEntry(deployment, members[1], InboxLifecycle.Open, default(CanonicalChoiceId), 2, 0)
            }, 1, members);
            var onlySource = new DeploymentSourceSnapshot(new Dictionary<string, IEnumerable<string>> {
                [vehicle] = new[] { "U#1" } });
            var noSource = new DeploymentSourceSnapshot(new Dictionary<string, IEnumerable<string>>());

            var store = new DurableInboxStore(ledger);
            var carrier = new Carrier();
            DurableCarrierLease.Bind(store, deployment, DurableCarrierClass.Deployment, carrier.RemoveWithoutCallback);
            ulong terminal; string refusal;
            if (!new DurableSourceRevalidationEngine(store, (o, m) => { })
                    .TryRevalidate(deployment, vehicle, onlySource, noSource, false, out terminal, out refusal) ||
                terminal != 0)
                yield return "L402 last-source-departure-was-refused-or-terminal: " + (refusal ?? "terminal revision minted");
            else
            {
                foreach (var member in members)
                {
                    var entry = store.Ledger.Get(deployment, member);
                    if (entry.Lifecycle == InboxLifecycle.Removed || entry.TerminalReason.HasValue)
                        yield return "L402 unavailable-mission-was-tombstoned-for-member: " + member.PlayerGuid;
                }
                if (carrier.Removed != 0)
                    yield return "L402 availability-change-tore-down-the-persistent-carrier";
            }

            // ── the mission must survive: the whole departure path may never cancel it ──
            var cancel = typeof(PhoenixPoint.Geoscape.Entities.GeoMission).GetMethod("Cancel", All);
            if (cancel == null)
            { yield return "L402 premise-changed: geo-mission-cancel-disappeared"; yield break; }
            var commit = typeof(MissionSync).GetMethod("CommitCapturedVehicleDeparture", All);
            var capture = typeof(MissionSync).GetMethod("CaptureVehicleDeparture", All);
            if (commit == null || capture == null)
            { yield return "L402 premise-changed: departure-seam-disappeared"; yield break; }
            // The GAME assembly, not the mod's: Program.Callees filters to the assembly it is handed, and
            // GeoMission.Cancel lives in the game. Handing it typeof(MissionSync).Assembly filtered the very
            // call this arm is looking for and made the arm unfalsifiable.
            foreach (var seam in new[] { capture, commit })
                if (Program.Callees(seam, typeof(PhoenixPoint.Geoscape.Entities.GeoMission).Assembly)
                        .Any(c => c.Name == "Cancel" &&
                        c.DeclaringType == typeof(PhoenixPoint.Geoscape.Entities.GeoMission)))
                    yield return "L402 departure-cancelled-the-mission-itself: " + seam.Name + " reaches " +
                                 "GeoMission.Cancel — the window is supposed to go, the mission is supposed " +
                                 "to stay on the map for whoever flies back";

            // POSITIVE CONTROL: the selection predicate must still be capable of saying no at all.
            if (MissionSync.BindsDeparture(deployment, InboxLifecycle.Open, elsewhere))
                yield return "L402 control-not-red: the departure filter matched a mission it was not given";
        }
    }
}
