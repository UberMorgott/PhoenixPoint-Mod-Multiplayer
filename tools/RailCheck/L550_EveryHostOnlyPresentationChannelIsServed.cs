using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;

namespace RailCheck
{
    /// <summary>
    /// L550 — EVERY HOST-ONLY PRESENTATION CHANNEL IS MIRRORED OR LOCALLY RE-RAISED.
    ///
    /// THE REPORT (2026-08-15): the centre-top status-bar notices — "haven destroyed", "site captured",
    /// alien raids, behemoth events — appeared on the HOST and on NO client, for the whole life of the
    /// mod. The state was never missing: GeoscapeLog rides the rail and the mirrored log screen was
    /// right. What was missing was the RAISE. GeoscapeLog.AddEntry:255 fires OnNewEntry,
    /// GeoscapeView.cs:292 subscribes and :1576 calls UIModuleStatusBarMessages.ShowEventMessage — and the
    /// generic applier writes the keyless List&lt;GeoscapeLogEntry&gt; _entries field DIRECTLY, so AddEntry
    /// never runs on a peer that did not simulate the event.
    ///
    /// WHY NO EXISTING LAW COULD SEE IT. L546 asks the same question one level up, but its domain is
    /// GeoWindowCoverage.HostAuthoritativeRaisers — ModalType windows that mint a journal position. The
    /// status bar is a SECOND presentation channel that mints nothing and lives entirely outside the
    /// journal, so it was in no coverage table at all. Every law in the repo asked about state travelling
    /// or about windows; none asked whether a CHANNEL was still speaking.
    ///
    /// THE INVARIANT, GENERAL AND EXECUTED — not a note about the status bar. Every channel whose raiser
    /// is a host-only sim event is declared in GeoLogNotice.HostOnlyChannels with a verdict, and the
    /// verdict is asked of the SHIPPED ASSEMBLY rather than believed:
    ///   (a) client-driven-unreached — a ClientDriven row must actually reach its declared native
    ///       presenter: some method in the mod assembly calls it. This is the arm that was RED before the
    ///       fix, with ZERO calls to ShowEventMessage anywhere in src/.
    ///   (b) carrier-does-not-ride — a ClientDriven or JournalMirrored row is served FROM MIRRORED STATE,
    ///       so the member it names must not be opted out of the rail (RailMeta.OptOutReason). A row that
    ///       promises to re-raise from state that does not travel is worse than an announced gap.
    ///   (c) the CURSOR, executed in four directions with no game running: a join (unseeded, 30 entries)
    ///       presents NOTHING; one appended entry presents ONE; re-applying the identical rebuilt list
    ///       presents NOTHING; and the 50-cap case — the list trimmed from the FRONT and appended to —
    ///       presents exactly the appended entry. This is the anti-avalanche claim, and it is the half a
    ///       declaration table can never carry.
    ///   (d) host-raiser-reentered — nothing in the mod calls GeoscapeLog.AddEntry. Re-raising through
    ///       the native adder would re-run the host-only path and append to the very list the batch just
    ///       wrote, re-dirtying the rail.
    ///   (e) POSITIVE CONTROLS — the table is non-empty, names the status-bar channel, and holds at least
    ///       one LocalOwn row. Without the last one arm (a) would be asserting that every channel in the
    ///       game must be replicated, and would be green for the wrong reason.
    ///
    /// Falsify (compile-valid src mutations, each named): delete the module.ShowEventMessage(...) loop in
    /// GeoLogNotice.PresentFromMirror — the exact pre-fix state — → (a); opt GeoLevelController.Log out
    /// in RailMeta → (b); make SelectNew select on an UNSEEDED cursor (drop the `if (seeded)` guard) →
    /// (c), the avalanche; call log-AddEntry from the presenter → (d); empty HostOnlyChannels → (e).
    /// </summary>
    internal static class L550_EveryHostOnlyPresentationChannelIsServed
    {
        internal static IEnumerable<string> Check()
        {
            var table = GeoLogNotice.HostOnlyChannels;

            Type[] modTypes;
            try { modTypes = typeof(GeoLogNotice).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { modTypes = ex.Types.Where(t => t != null).ToArray(); }
            var modMethods = modTypes
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly).Cast<MethodBase>())
                .ToList();

            // (a) THE VERDICT, ASKED OF THE ASSEMBLY.
            foreach (var ch in table)
            {
                if (ch.Verdict != GeoLogNotice.Served.ClientDriven) continue;
                var presenter = ch.PresenterType == null ? null : ch.PresenterType.GetMethod(
                    ch.PresenterMethod, BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.Static);
                if (presenter == null)
                {
                    yield return "L550 presenter-gone: channel '" + ch.Name + "' is declared ClientDriven " +
                                 "against " + (ch.PresenterType == null ? "<null>" : ch.PresenterType.Name) +
                                 "." + ch.PresenterMethod + ", which does not exist in this game build. A " +
                                 "channel cannot be served through a presenter that is not there.";
                    continue;
                }
                if (!modMethods.Any(m => Il.References(m, presenter)))
                    yield return "L550 client-driven-unreached: channel '" + ch.Name + "' is declared " +
                                 "ClientDriven, and NO method in the mod assembly calls " +
                                 ch.PresenterType.Name + "." + ch.PresenterMethod + ". The raiser is a " +
                                 "host-only sim event, so a peer that did not simulate it reaches the " +
                                 "presenter through this mod or not at all — this is the measured defect: " +
                                 "the centre-top status bar spoke on the host and on no client. Reason on " +
                                 "the row: " + ch.Reason;
            }

            // (b) A ROW SERVED FROM MIRRORED STATE NEEDS THE STATE TO RIDE.
            foreach (var ch in table)
            {
                if (ch.Verdict != GeoLogNotice.Served.ClientDriven &&
                    ch.Verdict != GeoLogNotice.Served.JournalMirrored) continue;
                string optOut = RailMeta.OptOutReason(ch.CarrierOwner, ch.CarrierMember);
                if (optOut != null)
                    yield return "L550 carrier-does-not-ride: channel '" + ch.Name + "' is declared " +
                                 ch.Verdict + " — i.e. served FROM MIRRORED STATE — but its carrier " +
                                 ch.CarrierOwner.Name + "." + ch.CarrierMember + " is opted out of the " +
                                 "rail: " + optOut + ". A re-raise reading state that never arrives " +
                                 "presents nothing and reports success.";
            }

            // (c) THE CURSOR, EXECUTED. No game, no rail — the real selection function, four directions.
            foreach (var failure in CursorArms()) yield return failure;

            // (d) NO RE-ENTRY INTO THE HOST-ONLY RAISER.
            var addEntry = typeof(GeoscapeLog).GetMethod("AddEntry", BindingFlags.Instance |
                                                                     BindingFlags.NonPublic | BindingFlags.Public);
            if (addEntry != null)
                foreach (var m in modMethods)
                    if (Il.References(m, addEntry))
                    {
                        yield return "L550 host-raiser-reentered: " + m.DeclaringType.Name + "." + m.Name +
                                     " calls GeoscapeLog.AddEntry. A client re-raising through the native " +
                                     "adder appends to the very list the rail batch just wrote — it " +
                                     "re-dirties the rail and re-runs the host-only path. The re-raise " +
                                     "goes to the WIDGET (ShowEventMessage), never to the model.";
                        break;
                    }

            // (e) POSITIVE CONTROLS.
            bool hasStatusBar = table.Any(c => c.PresenterType == typeof(
                PhoenixPoint.Geoscape.View.ViewModules.UIModuleStatusBarMessages) &&
                c.PresenterMethod == "ShowEventMessage" && c.Verdict == GeoLogNotice.Served.ClientDriven);
            if (table.Length == 0 || !hasStatusBar)
                yield return "L550 positive-control: GeoLogNotice.HostOnlyChannels holds " + table.Length +
                             " row(s) and " + (hasStatusBar ? "does" : "does NOT") + " declare the " +
                             "status-bar event message as ClientDriven, so arms (a) and (b) ran over a set " +
                             "that cannot contain the reported defect and proved nothing.";
            if (!table.Any(c => c.Verdict == GeoLogNotice.Served.LocalOwn))
                yield return "L550 localown-verdict-gone: no row is declared LocalOwn. Some geoscape " +
                             "presentation genuinely belongs to each peer alone (context help, tutorial " +
                             "hints — opted out of the rail on purpose); with that verdict gone this law " +
                             "reads as 'replicate every channel in the game' and arm (a) stops " +
                             "distinguishing a real hole from any channel at all.";
        }

        /// <summary>Arm (c): the real cursor, four directions, no game. Ticks are geoscape instants —
        /// what AddEntry:258-261 stamps from the clock.</summary>
        private static IEnumerable<string> CursorArms()
        {
            long ticks = long.MinValue;
            int seen = 0;
            var into = new List<GeoscapeLogEntry>();

            // The history a joining peer receives in one blob: 30 entries across 10 instants.
            var history = new List<GeoscapeLogEntry>();
            for (int i = 0; i < 30; i++) history.Add(Entry(1000 + i / 3));

            int n = GeoLogNotice.SelectNew(history, false, ref ticks, ref seen, into);
            if (n != 0)
                yield return "L550 cursor-avalanche-on-join: an UNSEEDED cursor selected " + n + " of the " +
                             "30 entries a joining peer receives in its first blob. The rail REBUILDS the " +
                             "whole log on every apply, so the first sync after a join or a load must " +
                             "present NOTHING — otherwise every peer that joins replays the entire " +
                             "campaign's notices into the status bar in one frame.";

            // The same list arriving again, rebuilt entry-for-entry (every apply does this).
            var rebuilt = history.Select(e => Entry(e.EventDate.TimeSpan.Ticks)).ToList();
            n = GeoLogNotice.SelectNew(rebuilt, true, ref ticks, ref seen, into);
            if (n != 0)
                yield return "L550 cursor-refires-on-rebuild: re-applying the identical log selected " + n +
                             " entr(ies). The blob rebuilds every entry with Activator, so object identity " +
                             "is worthless here and the cursor must key on content; refiring on a rebuild " +
                             "would repeat the whole status bar on every rail batch.";

            // One genuinely new entry, at a later instant.
            var appended = rebuilt.Select(e => Entry(e.EventDate.TimeSpan.Ticks)).ToList();
            appended.Add(Entry(2000));
            n = GeoLogNotice.SelectNew(appended, true, ref ticks, ref seen, into);
            if (n != 1 || into.Count != 1 || into[0].EventDate.TimeSpan.Ticks != 2000)
                yield return "L550 cursor-misses-the-new-entry: one entry appended at a later instant " +
                             "selected " + n + " (carried " + into.Count + "). A cursor that cannot see a " +
                             "genuinely new entry is the original defect with extra steps.";

            // The 50-cap: AddEntry:266-269 trims from the FRONT, so a COUNT cursor stops moving here.
            var trimmed = appended.Skip(5).Select(e => Entry(e.EventDate.TimeSpan.Ticks)).ToList();
            trimmed.Add(Entry(3000));
            n = GeoLogNotice.SelectNew(trimmed, true, ref ticks, ref seen, into);
            if (n != 1 || into.Count != 1 || into[0].EventDate.TimeSpan.Ticks != 3000)
                yield return "L550 cursor-breaks-on-the-cap: after the log trimmed 5 entries off the FRONT " +
                             "(GeoscapeLog.MaxLogSize=50, AddEntry:266-269) and appended one, the cursor " +
                             "selected " + n + " instead of exactly the appended entry. A count-based " +
                             "cursor fails here — the count stops moving once the log saturates — which is " +
                             "why the cursor keys on the entry INSTANT and the tally at that instant.";

            // A shorter list at an EARLIER instant (a reload replacing the mirror without a reset) must
            // never make the cursor go backwards and replay.
            var older = new List<GeoscapeLogEntry> { Entry(500), Entry(600) };
            n = GeoLogNotice.SelectNew(older, true, ref ticks, ref seen, into);
            if (n != 0)
                yield return "L550 cursor-walks-backwards: a log whose entries are all BEHIND the cursor " +
                             "selected " + n + " entr(ies). The failure direction of this cursor must " +
                             "always be 'skip', never 'repeat' — the only way to present twice is for " +
                             "EventDate to go backwards, which AddEntry cannot produce.";
        }

        private static GeoscapeLogEntry Entry(long ticks)
        {
            return new GeoscapeLogEntry { EventDate = TimeUnit.FromTimeSpan(TimeSpan.FromTicks(ticks)) };
        }
    }
}
