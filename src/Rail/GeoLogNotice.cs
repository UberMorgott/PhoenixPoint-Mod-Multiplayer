using System;
using System.Collections.Generic;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE SECOND PRESENTATION CHANNEL, and the declaration that makes it countable.
    ///
    /// The window journal (WindowJournal / GeoWindowCoverage) owns everything that mints a MODAL. It is
    /// not the only way the game speaks: the geoscape also talks through the centre-top status bar —
    /// "haven destroyed", "site captured", alien raids, behemoth events — and that channel lives entirely
    /// OUTSIDE the journal, which is why L546 (whose domain is GeoWindowCoverage.HostAuthoritativeRaisers)
    /// could not see it going missing for the whole life of the mod.
    ///
    /// MEASURED (2026-08-15): every status-bar notice appeared on the HOST and on no client. The state
    /// travels — GeoscapeLog rides the rail (UiEventMap WorldLayerKinds, RailMeta has no opt-out for
    /// GeoLevelController.Log) — but the generic applier WRITES the keyless List&lt;GeoscapeLogEntry&gt;
    /// _entries field directly, so GeoscapeLog.AddEntry never runs, OnNewEntry never fires, and
    /// GeoscapeView.OnLogNewEntry (decompile GeoscapeView.cs:1571-1577) — the only caller of
    /// ShowEventMessage in the game — is never reached on a peer that did not simulate the event.
    ///
    /// <see cref="HostOnlyChannels"/> is the general statement, not a note about this one bug: every
    /// presentation channel whose raiser is a host-only sim event is DECLARED, and its declaration says
    /// how a client gets it. RailCheck L550 executes the declaration — it asks the shipped assembly
    /// whether a ClientDriven channel really reaches its native presenter, and asks RailMeta whether the
    /// carrier really rides — so a row cannot be a comforting label.
    /// </summary>
    public static class GeoLogNotice
    {
        // ─── THE DECLARATION ───────────────────────────────────────────────

        /// <summary>How a host-only presentation channel reaches the peers that never simulated it.</summary>
        internal enum Served
        {
            /// <summary>The mod itself drives the native presenter from the mirrored state. The
            /// declaration names the presenter, and L550 proves the mod actually calls it.</summary>
            ClientDriven,
            /// <summary>The window journal carries it: the host mints a position, every peer drains it.
            /// GeoWindowCoverage + L546 own this half.</summary>
            JournalMirrored,
            /// <summary>Genuinely per-peer — each peer's own screen owns it and mirroring it would hijack
            /// another player's UI. The verdict that keeps L550 from meaning "replicate everything".</summary>
            LocalOwn,
            /// <summary>An ANNOUNCED hole: not served, with a reason, on purpose.</summary>
            AnnouncedGap,
        }

        internal sealed class Channel
        {
            internal string Name;
            /// <summary>The native widget entry point a peer must reach to see this channel.</summary>
            internal Type PresenterType;
            internal string PresenterMethod;
            /// <summary>The rail member that carries the channel's state, as RailMeta.OptOutReason keys
            /// it: a channel served from mirrored state is a lie if the state does not ride.</summary>
            internal Type CarrierOwner;
            internal string CarrierMember;
            internal Served Verdict;
            internal string Reason;
        }

        /// <summary>Every geoscape presentation channel whose raiser is a HOST-ONLY sim event — the peer
        /// that did not simulate it never runs the handler, so the channel is silent there unless
        /// something on this list says otherwise.</summary>
        internal static readonly Channel[] HostOnlyChannels =
        {
            new Channel
            {
                Name = "status-bar event message",
                PresenterType = typeof(UIModuleStatusBarMessages),
                PresenterMethod = "ShowEventMessage",
                CarrierOwner = typeof(GeoLevelController),
                CarrierMember = "Log",
                Verdict = Served.ClientDriven,
                Reason = "GeoscapeLog.AddEntry:255 raises OnNewEntry, GeoscapeView.cs:292 subscribes and " +
                         ":1576 shows it. The rail writes _entries directly, so no client ever runs that " +
                         "chain; this class re-raises from the mirrored list behind a cursor.",
            },
            new Channel
            {
                Name = "status-bar timed event",
                PresenterType = typeof(UIModuleStatusBarMessages),
                PresenterMethod = "ShowTimedEventMessage",
                CarrierOwner = typeof(GeoLevelController),
                CarrierMember = "Log",
                Verdict = Served.AnnouncedGap,
                Reason = "GeoscapeLog._timedEvents carries NO [SerializeMember] (decompile " +
                         "GeoscapeLog.cs:38), so the timed-event list is not a rail leaf and there is " +
                         "nothing mirrored to re-raise from. Announced, not fixed: the countdown ribbons " +
                         "(interception windows, event timers) show on the simulating peer only.",
            },
            new Channel
            {
                Name = "geoscape modal window",
                PresenterType = typeof(GeoModalMirror),
                PresenterMethod = "PublishRefusal",
                CarrierOwner = typeof(GeoLevelController),
                CarrierMember = "Log",
                Verdict = Served.JournalMirrored,
                Reason = "The window journal owns it end to end (GeoWindowCoverage, L520/L521/L546): the " +
                         "host mints a position at the one capture seam and every peer drains its own " +
                         "cursor. Listed so the two channels are countable side by side.",
            },
            new Channel
            {
                Name = "context help / tutorial hint",
                PresenterType = typeof(UIModuleStatusBarMessages),
                PresenterMethod = "ShowPauseGameMessage",
                CarrierOwner = typeof(GeoLevelController),
                CarrierMember = "ContextHelpData",
                Verdict = Served.LocalOwn,
                Reason = "Per-peer hint/tutorial progress, opted OUT of the rail deliberately " +
                         "(RailMeta.cs, GeoLevelController.cs:318). Mirroring the host's would hijack each " +
                         "player's own context help. The negative control for L550.",
            },
        };

        // ─── THE CURSOR ────────────────────────────────────────────────────
        //
        // WHY NOT A COUNT. The blob REBUILDS the whole list on every apply and a joining peer receives the
        // entire log at once, so "present what is new" needs a cursor that survives a full rewrite. A
        // count cannot be it: GeoscapeLog.AddEntry:266-269 trims from the FRONT at 50 entries, so once the
        // log saturates the count stops moving while entries keep arriving.
        //
        // WHAT IT KEYS ON. The entries are keyless, but each carries EventDate — stamped by AddEntry from
        // the geoscape clock at APPEND time (decompile :258-261) — so the list is ordered by a
        // non-decreasing instant. The cursor is that instant plus how many entries already carried it:
        // (lastTicks, seenAtLast). An entry is NEW when its instant is LATER than the cursor, or equal to
        // it and beyond the count already presented at that instant.
        //
        // WHY IT CANNOT AVALANCHE. Presenting an already-presented entry needs its instant to be GREATER
        // than the cursor, i.e. needs EventDate to go BACKWARDS in list order — which AddEntry cannot
        // produce. The failure direction is therefore always "skip", never "repeat": in the one corner
        // where the tie-break degrades (the 50-cap trims entries that shared the cursor's exact instant) a
        // same-instant new entry is skipped, never re-shown. The first sync after join or load presents
        // NOTHING at all, because the cursor is SEEDED from the mirror BEFORE the first batch applies
        // (GenericApplier), exactly like ResearchSync.SeedLatchFromMirror.

        private const long Unseeded = long.MinValue;

        private static bool _seeded;
        private static long _lastTicks = Unseeded;
        private static int _seenAtLast;
        private static readonly List<GeoscapeLogEntry> _fresh = new List<GeoscapeLogEntry>();

        /// <summary>
        /// THE PURE CURSOR STEP, executed by L550 in both directions with no game running. Fills
        /// <paramref name="into"/> with the entries of <paramref name="entries"/> that this cursor has not
        /// seen, and re-keys the cursor onto the list AS IT IS NOW. An UNSEEDED cursor selects nothing —
        /// that is the anti-avalanche arm, and the reason a join presents no history.
        /// </summary>
        internal static int SelectNew(IList<GeoscapeLogEntry> entries, bool seeded, ref long lastTicks,
                                      ref int seenAtLast, List<GeoscapeLogEntry> into)
        {
            if (into != null) into.Clear();
            int n = entries == null ? 0 : entries.Count;
            int selected = 0;
            if (seeded)
            {
                int atCursorInstant = 0;
                for (int i = 0; i < n; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    long t = e.EventDate.TimeSpan.Ticks;
                    if (t < lastTicks) continue;                                  // behind the cursor
                    if (t == lastTicks && ++atCursorInstant <= seenAtLast) continue; // same instant, already shown
                    selected++;
                    if (into != null) into.Add(e);
                }
            }
            if (n > 0)
            {
                long last = entries[n - 1] == null ? lastTicks : entries[n - 1].EventDate.TimeSpan.Ticks;
                int tail = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e == null || e.EventDate.TimeSpan.Ticks != last) break;
                    tail++;
                }
                lastTicks = last;
                seenAtLast = tail;
            }
            return selected;
        }

        // ─── LIFECYCLE ─────────────────────────────────────────────────────

        /// <summary>Full session teardown.</summary>
        public static void Reset() => ResetForReloadBoundary();

        /// <summary>Mid-session reload boundary: the transferred save replaced the log this cursor
        /// pointed into, so drop it and let the next seed re-key silently — same contract as
        /// ResearchSync.ResetForReloadBoundary, and the reason a reload shows no backlog.</summary>
        public static void ResetForReloadBoundary()
        {
            _seeded = false;
            _lastTicks = Unseeded;
            _seenAtLast = 0;
            _fresh.Clear();
        }

        /// <summary>
        /// SEED FROM THE MIRROR AS IT IS *NOW* — called by <see cref="GenericApplier"/> BEFORE a batch
        /// applies, for the same reason ResearchSync.SeedLatchFromMirror is: seeded lazily from the
        /// post-batch present, the first batch after a join or a reload is both the seed AND the
        /// transition, and the transition loses. Idempotent and free once seeded.
        /// </summary>
        internal static void SeedFromMirror(GeoLevelController geo)
        {
            if (_seeded) return;
            var log = geo == null ? null : geo.Log;
            if (log == null) return;
            var entries = Snapshot(log);
            if (entries == null) return;
            SelectNew(entries, false, ref _lastTicks, ref _seenAtLast, null);
            _seeded = true;
        }

        /// <summary>
        /// RE-RAISE THE HOST-ONLY CHANNEL ON THIS PEER. Called from the rail's post-apply seam
        /// (<see cref="UiEventMap"/>) with the mirrored log already written.
        ///
        /// NOT AddEntry: calling the native adder would re-run the host-only path and re-dirty the rail
        /// (it appends to the very list the batch just wrote). The presenter is the widget entry point
        /// GeoscapeView.cs:1576 uses and nothing deeper.
        ///
        /// ACTOR = null, deliberately. GeoActor is an argument of the OnNewEntry EVENT, not a member of
        /// GeoscapeLogEntry, so it is not in the mirrored payload and cannot be resolved from it. null is
        /// a supported native call — UIModuleReplenish.cs:628/639 passes null itself — and the widget
        /// guards it (ShowHighPriorityEventMessage:145 only adds the chase marker `if (actor != null)`).
        /// Cost: no globe point-of-interest marker and no POI sound on a client. The PRIORITY split is
        /// untouched: DisplayMessage:102 branches on entry.HighPriority, which IS mirrored.
        ///
        /// The native handler also calls RequestGamePause() on a high-priority entry. Not done here: the
        /// geoscape clock is host-authoritative (TimeSync) and a client pausing itself would desync the
        /// clock rather than present a message.
        /// </summary>
        internal static void PresentFromMirror(GeoLevelController geo)
        {
            var log = geo == null ? null : geo.Log;
            if (log == null) return;
            var entries = Snapshot(log);
            if (entries == null) return;

            int selected = SelectNew(entries, _seeded, ref _lastTicks, ref _seenAtLast, _fresh);
            _seeded = true;
            if (selected == 0) return;

            var module = geo.View == null || geo.View.GeoscapeModules == null
                ? null : geo.View.GeoscapeModules.StatusBarMessagesModule;
            if (module == null)
            {
                // ponytail: no ticker on screen (tactical, loading) -> the notices are DROPPED, loudly, and
                // the cursor still advances. Holding them would replay a battle's worth of messages in one
                // frame the moment the geoscape came back, which is the avalanche this whole class exists
                // to prevent. The permanent record is the geoscape log screen, which is mirrored already.
                // Upgrade path if it ever matters: keep the last N and present them on the next map entry.
                MpLog.Log("[Multiplayer][rail] GeoLogNotice dropped " + selected + " status-bar notice(s): " +
                          "no geoscape status bar on this peer right now");
                _fresh.Clear();
                return;
            }

            for (int i = 0; i < _fresh.Count; i++)
            {
                try { module.ShowEventMessage(_fresh[i], null); }
                catch (Exception ex)
                { MpLog.LogError("[Multiplayer][rail] GeoLogNotice present failed: " + ex); }
            }
            MpLog.Log("[Multiplayer][rail] GeoLogNotice presented " + _fresh.Count + " status-bar notice(s)");
            _fresh.Clear();
        }

        /// <summary>The mirrored list, as an indexable snapshot. GeoscapeLog exposes it only as
        /// IEnumerable (decompile :53).</summary>
        private static List<GeoscapeLogEntry> Snapshot(GeoscapeLog log)
        {
            try
            {
                var src = log.Entries;
                if (src == null) return null;
                var list = new List<GeoscapeLogEntry>();
                foreach (var e in src) list.Add(e);
                return list;
            }
            catch (Exception ex)
            {
                MpLog.LogError("[Multiplayer][rail] GeoLogNotice snapshot failed: " + ex);
                return null;
            }
        }
    }
}
