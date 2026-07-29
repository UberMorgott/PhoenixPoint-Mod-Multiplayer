using System;
using System.Collections.Generic;
using HarmonyLib;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 presentation for the GeoscapeEventSystem kind ("ES"): geoscape EVENT WINDOWS on the
    /// client, as a QUEUED HISTORY derived purely from mirrored STATE. The rail ships
    /// <c>_records</c> (the EncounterRecords twin, RailMeta.cs:726); every window this peer still owes
    /// the player is a pure function of those records and ONE local number:
    ///   • <c>_cursor</c> = the newest <c>LastTriggerAt</c> this player has actually clicked through.
    ///   • <see cref="Backlog"/> = records past the cursor, plus every still-<c>Triggered</c> record
    ///     (an open decision is not history), oldest first.
    ///   • <see cref="Mode"/> = picker or outcome, read off <c>GeoscapeEventRecord.State</c>.
    /// This REPLACED a transition latch, which could not work: the whole quest/narrative class is
    /// already <c>Completed</c> when it reaches a client (the host auto-completes
    /// <c>HasSingleChoice</c> at trigger, GeoscapeEventSystem.cs:651-656), so there is no
    /// →Triggered transition to observe; and any observation gap (tactical mission, reload, join,
    /// disconnect) loses transitions while never losing records. The latch also SILENTLY re-seeded
    /// itself at every reload boundary, which is what ate a joining client's entire backlog.
    ///
    /// Raising reuses the game's own queue and dialog — no custom UI: <c>GeoscapeViewSwitchQuery</c>
    /// (priority-ordered insert :75-84, popped one at a time :58-73, and restored across save/load
    /// :39-56) gives click-through-one-at-a-time and reload persistence for free, exactly as
    /// <c>GeoscapeView.OnGeoscapeEventRaised</c>:2034-2066 does it, with <c>PauseGame=false</c>
    /// (pause is host-authoritative and arrives via the TimeAnchor).
    ///
    /// Client choice resolution is still FORBIDDEN (host-authoritative): the lock patches below
    /// swallow a real choice click and hold Esc while the record is unresolved. Relaying the client's
    /// pick host-ward through IntentRail is the next batch.
    /// </summary>
    internal static class EventPopup
    {
        private static long _cursor;                 // newest LastTriggerAt ticks this peer clicked through
        private static bool _cursorSeeded;
        private static readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _loggedSkips = new HashSet<string>(StringComparer.Ordinal);
        private static float _nextPumpAt;
        private const float PumpInterval = 1f;

        private static readonly System.Reflection.FieldInfo RecordsField =
            AccessTools.Field(typeof(GeoscapeEventSystem), "_records");                 // GeoscapeEventSystem.cs:92
        private static readonly System.Reflection.FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                  // GeoscapeView.cs:138 (game typo)
        private static readonly System.Reflection.FieldInfo RequestsField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_viewStateSwitchRequests"); // GeoscapeViewSwitchQuery.cs:15
        private static readonly System.Reflection.FieldInfo CurrentRequestField =
            AccessTools.Field(typeof(GeoscapeViewSwitchQuery), "_currentStateSwitchRequest"); // :17
        // GeoscapeEvent.IsCompleted / .ChoiceReward are { get; private set; } (GeoscapeEvent.cs:32, :36).
        private static readonly System.Reflection.MethodInfo SetIsCompleted =
            AccessTools.PropertySetter(typeof(GeoscapeEvent), "IsCompleted");
        private static readonly System.Reflection.MethodInfo SetChoiceReward =
            AccessTools.PropertySetter(typeof(GeoscapeEvent), "ChoiceReward");

        /// <summary>Reload/session boundary: forget the in-flight raise set and re-seed the cursor from
        /// whatever the transferred save already carries.</summary>
        public static void Reset()
        {
            _inFlight.Clear();
            _loggedSkips.Clear();
            _cursor = 0;
            _cursorSeeded = false;
        }

        /// <summary>Display mode for one record, read off the record's OWN state — never off a
        /// transition. Null = not displayable. Pure; RailCheck L26 calls it directly.</summary>
        internal static string Mode(GeoscapeEventRecordState state)
        {
            switch (state)
            {
                case GeoscapeEventRecordState.Triggered: return "picker";
                case GeoscapeEventRecordState.SelectedChoice:
                case GeoscapeEventRecordState.Completed:
                case GeoscapeEventRecordState.MigratedCompleted: return "outcome";
                default: return null; // Reset — ReEneableEvent (GeoscapeEvent.cs:103-106) put it back in the pool
            }
        }

        /// <summary>THE derivation: every window this peer has not clicked through yet, oldest first.
        /// Ordered by (LastTriggerAt, EventId) ascending — deterministic, never dictionary order
        /// (law 6). A still-<c>Triggered</c> record rides regardless of the cursor: it is an OPEN
        /// decision, and the cursor only records what was clicked through — the host's currently-open
        /// window is also the one thing the save transfer does NOT carry
        /// (GeoscapeViewSwitchQuery.GetRestorableData:28 walks only the pending list). Pure — no seed,
        /// no latch, no I/O; RailCheck L26 calls it directly.</summary>
        internal static List<GeoscapeEventRecord> Backlog(IDictionary<string, GeoscapeEventRecord> records, long cursor)
        {
            var list = new List<GeoscapeEventRecord>();
            if (records == null) return list;
            foreach (var rec in records.Values)
                if (rec != null && Mode(rec.State) != null &&
                    (rec.LastTriggerAt.TimeSpan.Ticks > cursor || rec.State == GeoscapeEventRecordState.Triggered))
                    list.Add(rec);
            list.Sort(ByTriggerThenId);
            return list;
        }

        private static int ByTriggerThenId(GeoscapeEventRecord a, GeoscapeEventRecord b)
        {
            int c = a.LastTriggerAt.TimeSpan.Ticks.CompareTo(b.LastTriggerAt.TimeSpan.Ticks);
            return c != 0 ? c : string.CompareOrdinal(a.EventId, b.EventId);
        }

        /// <summary>Second pump site, ~1 Hz from SyncEngine.Tick. A late joiner's records arrive WITH
        /// THE SAVE, not as a delta, so a delta-driven pump alone would never fire for its backlog;
        /// this also self-heals a pass that ran before <c>GeoscapeView</c> existed. Cost = one scan of
        /// ≤ |event defs| entries. ponytail: 1 Hz scan, make it change-driven only if it profiles.</summary>
        public static void ClientTick(NetworkEngine engine)
        {
            if (engine == null || engine.IsHost || !engine.IsActiveSession) return;
            if (Time.realtimeSinceStartup < _nextPumpAt) return;
            _nextPumpAt = Time.realtimeSinceStartup + PumpInterval;
            var geo = GeoLevel();
            if (geo?.EventSystem != null) Sync(geo.EventSystem, geo);
        }

        /// <summary>Client-only (host windows are raised natively). Derives the backlog from the live
        /// records and pushes whatever the native pipeline is not already holding.</summary>
        public static void Sync(GeoscapeEventSystem es, GeoLevelController geo)
        {
            if (!IsClient) return;
            if (!(RecordsField?.GetValue(es) is IDictionary<string, GeoscapeEventRecord> records)) return;
            var view = geo?.View;
            if (view == null || !(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q)) return;

            SeedCursor(records);
            FlipResolvedOpenWindow(view, records);
            RetireClosed(q, view, records);

            var backlog = Backlog(records, _cursor);
            bool announced = false;
            foreach (var rec in backlog)
            {
                if (_inFlight.Contains(rec.EventId) || _loggedSkips.Contains(rec.EventId)) continue;
                if (IsQueuedNatively(q, view, rec.EventId)) { _inFlight.Add(rec.EventId); continue; }
                if (!announced)
                {
                    announced = true; // one line per pass that actually raises — the pump itself stays silent
                    Debug.Log("[MP][events] backlog n=" + backlog.Count + " cursor=" + _cursor +
                              " next='" + rec.EventId + "' mode=" + Mode(rec.State));
                }
                Raise(es, geo, q, rec);
            }
        }

        /// <summary>First pass after a reload/join: the transferred save carries the whole campaign's
        /// resolved history, which is NOT this player's unseen backlog. Seeded from the resolved
        /// records only, so an unanswered event still rides. Logged — a boundary that drops work
        /// silently is the bug class this file exists to kill.</summary>
        private static void SeedCursor(IDictionary<string, GeoscapeEventRecord> records)
        {
            if (_cursorSeeded) return;
            _cursorSeeded = true;
            foreach (var rec in records.Values)
                if (rec != null && rec.State != GeoscapeEventRecordState.Triggered)
                {
                    long t = rec.LastTriggerAt.TimeSpan.Ticks;
                    if (t > _cursor) _cursor = t;
                }
            Debug.Log("[MP][events] cursor seeded to " + _cursor + " from " + records.Count +
                      " record(s) at the reload/join boundary — resolved history before that point is not replayed");
        }

        /// <summary>A window we pushed is gone from the whole native pipeline ⇒ the player clicked
        /// through it. That is the ONLY thing that advances the cursor, so a crash or quit re-shows it.</summary>
        private static void RetireClosed(GeoscapeViewSwitchQuery q, GeoscapeView view, IDictionary<string, GeoscapeEventRecord> records)
        {
            if (_inFlight.Count == 0) return;
            List<string> closed = null;
            foreach (var id in _inFlight)
                if (!IsQueuedNatively(q, view, id)) (closed ?? (closed = new List<string>())).Add(id);
            if (closed == null) return;
            foreach (var id in closed)
            {
                _inFlight.Remove(id);
                long t = records.TryGetValue(id, out var rec) && rec != null ? rec.LastTriggerAt.TimeSpan.Ticks : _cursor;
                if (t > _cursor) _cursor = t;
                Debug.Log("[MP][events] cursor advanced to " + _cursor + " after closing '" + id + "'");
            }
        }

        /// <summary>The host resolved the choice while this peer's PICKER was open. Refresh the
        /// dialog's stale Record ref (the blob apply rebuilt the record instance) so the ExitState
        /// guard (UIStateGeoscapeEvent.cs:61-65, which locally completes a still-Triggered event) reads
        /// the resolved state and stays silent, then close: the next pass re-raises the SAME record in
        /// outcome mode, because the id leaves the in-flight set WITHOUT advancing the cursor — the
        /// outcome has not been seen yet. B3 replaces this close+reopen with an in-place re-render.</summary>
        private static void FlipResolvedOpenWindow(GeoscapeView view, IDictionary<string, GeoscapeEventRecord> records)
        {
            if (!(view.CurrentViewState is UIStateGeoscapeEvent st)) return;
            var ev = st.Event;
            if (string.IsNullOrEmpty(ev?.EventID) || ev.IsCompleted) return;   // synthetic page, or already an outcome window
            if (!records.TryGetValue(ev.EventID, out var rec) || rec == null) return;
            if (rec.State == GeoscapeEventRecordState.Triggered) return;        // still an open decision
            try
            {
                ev.Record = rec;
                _inFlight.Remove(ev.EventID);
                view.FinishQueriedState();
                Debug.Log("[MP][events] '" + ev.EventID + "' resolved by the host while open (→ " + rec.State +
                          ") — reopening in outcome mode");
            }
            catch (Exception ex)
            { Debug.LogError("[MP][events] flip of open '" + ev.EventID + "' failed: " + ex.Message); }
        }

        /// <summary>Is a window for this event id anywhere in the native pipeline — waiting in the
        /// switch queue, popped and mid-switch, or on screen? A transferred save restores the HOST's
        /// pending queue (GeoscapeView.cs:349 → GeoscapeViewSwitchQuery.RestoreData:39-56), so without
        /// this a joiner sees each of those windows TWICE. Silent on a hit: "we already have that
        /// window" is the steady state, and the raise that put it there was logged once.</summary>
        private static bool IsQueuedNatively(GeoscapeViewSwitchQuery q, GeoscapeView view, string eventId)
        {
            if (EventIdOf(view.CurrentViewState) == eventId) return true;
            if (CurrentRequestField?.GetValue(q) is GeoscapeViewStateSwitchRequest cur && EventIdOf(cur.State) == eventId) return true;
            if (RequestsField?.GetValue(q) is IEnumerable<GeoscapeViewStateSwitchRequest> pending)
                foreach (var r in pending)
                    if (r != null && EventIdOf(r.State) == eventId) return true;
            return false;
        }

        private static string EventIdOf(object state) => (state as UIStateGeoscapeEvent)?.Event?.EventID;

        private static void Raise(GeoscapeEventSystem es, GeoLevelController geo, GeoscapeViewSwitchQuery q, GeoscapeEventRecord rec)
        {
            string eventId = rec.EventId;
            string mode = Mode(rec.State);
            try
            {
                var data = es.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;
                if (data == null) { Skip(eventId, "no def on this peer"); return; }
                if (es.IsEventTheMarketplace(data))
                { Skip(eventId, "marketplace event — UIStateMarketplaceGeoscapeEvent not wired yet"); return; }
                // Same synthetic-context shape the game uses for its own re-entry (GeoscapeView.
                // ToMarketplace:735-738); the site is legitimately null for site-less events and for a
                // completed exploration event whose site was destroyed (GeoscapeEvent.cs:108-111) —
                // logged, because GeoscapeEventContext's token table dereferences Site unguarded
                // (GeoscapeEventContext.cs:22-39, :224-239) and that would throw inside EnterState.
                var site = es.FindEventLocation(eventId);
                var geoEvent = new GeoscapeEvent(data, new GeoscapeEventContext(site, geo.ViewerFaction)) { Record = rec };
                if (mode == "outcome") MarkResolvedInstance(geoEvent);
                q.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent))
                { PauseGame = false }); // pause mirrors from the host via the TimeAnchor
                _inFlight.Add(eventId);
                Debug.Log("[MP][events] raised '" + eventId + "' state=" + rec.State + " triggerCount=" + rec.TriggerCount +
                          " mode=" + mode + " site=" + (site == null ? "null" : site.SiteId.ToString()));
            }
            catch (Exception ex) { Skip(eventId, "raise threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>One line per event id, then that id is not retried — a 1 Hz pump must never log
        /// per pass, and every reason here is a permanent property of the id (no def, marketplace) or a
        /// throw the next pass would only repeat.</summary>
        private static void Skip(string eventId, string reason)
        {
            if (_loggedSkips.Add(eventId))
                Debug.Log("[MP][events] skipped '" + eventId + "' — " + reason);
        }

        /// <summary>An OUTCOME window must not resolve anything. <c>ShowEncounter</c> takes the
        /// single-choice branch for a narrative/quest event (UIModuleSiteEncounters.cs:239-241) →
        /// <c>SetSingleChoiceEncounter</c>:251 → <c>SelectChoice</c>:598 →
        /// <c>if (!ev.IsCompleted) ev.CompleteEvent(...)</c>, and <c>GeoscapeEvent.IsCompleted</c> is
        /// per-INSTANCE (GeoscapeEvent.cs:36) — a fresh instance over an already-Completed record says
        /// "not completed" and would re-grant the ENTIRE reward client-side. Marking the instance
        /// completed skips that branch; the empty reward stub keeps <c>SelectChoice</c>:604 and
        /// <c>SetClosingEncounter</c>:357 from NRE-ing on a null <c>ChoiceReward</c>, while
        /// <c>HasRewards()</c>==false (GeoFactionRewardApplyResult.cs:69) makes <c>ShowReward</c>:363
        /// return at once, so the native page renders outcome TEXT only.
        /// PICKER windows need none of this: a <c>Triggered</c> record always has ≥2 choices, because
        /// the host auto-completes <c>HasSingleChoice</c> (<c>Choices.Count &lt;= 1</c>,
        /// GeoscapeEventData.cs:65) at trigger (GeoscapeEventSystem.cs:651-656), so
        /// <c>ShowEncounter</c> takes the <c>SetEncounter</c> branch and calls nothing that resolves.</summary>
        private static void MarkResolvedInstance(GeoscapeEvent ev)
        {
            SetIsCompleted?.Invoke(ev, new object[] { true });
            SetChoiceReward?.Invoke(ev, new object[] { new GeoFactionReward { ApplyResult = new GeoFactionRewardApplyResult() } });
        }

        internal static bool IsClient
        {
            get
            {
                var e = NetworkEngine.Instance;
                return e != null && e.IsActiveSession && !e.IsHost;
            }
        }

        private static GeoLevelController GeoLevel()
        {
            try { return Base.Core.GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>(); }
            catch { return null; }
        }

        /// <summary>The live (post-apply) record for an event id, falling back to the dialog's own ref.</summary>
        internal static GeoscapeEventRecord LiveRecord(string eventId, GeoscapeEventRecord fallback)
        {
            try { return GeoLevel()?.EventSystem?.GetEventRecord(eventId) ?? fallback; }
            catch { return fallback; }
        }
    }

    /// <summary>
    /// Intent-capture seam (law 4a): the client never resolves an event choice locally. The choice-button
    /// handler (UIModuleSiteEncounters.OnChoiceSelected:546) would run CompleteEvent → rewards applied
    /// client-side (divergence the rail cannot fully correct — StartMission spawns are structural).
    /// Swallow it on the client for any real host event; paging clicks (multi-page description text,
    /// _pagingEvent:157) stay native — they advance text only. Relaying the pick host-ward = follow-up.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "OnChoiceSelected")]
    internal static class EventChoiceClientLock
    {
        private static readonly System.Reflection.FieldInfo PagingField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_pagingEvent");
        private static readonly System.Reflection.FieldInfo GeoEventField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_geoEvent");

        private static bool Prefix(UIModuleSiteEncounters __instance, GeoEventChoice choice)
        {
            if (!EventPopup.IsClient) return true;
            if (PagingField != null && (bool)(PagingField.GetValue(__instance) ?? false)) return true;
            var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
            if (string.IsNullOrEmpty(ev?.EventID)) return true; // not a mirrored host event
            // The OK/Continue button of a CLOSING page carries neither Outcome nor Requirments
            // (SetClosingEncounter:346-351 builds it with Text only), yet _geoEvent still points at the
            // REAL event there — SetEncounter never reassigns it (:265-303). Without this arm the only
            // button on a mirrored OUTCOME window is dead and Esc is the only way out. It resolves
            // nothing: no Wallet.Take (:571-573 needs Requirments) and CompleteEvent is skipped on an
            // instance already marked completed (:562-567 and :580 → SelectChoice:600).
            if (ev.IsCompleted && choice != null && choice.Outcome == null && choice.Requirments == null) return true;
            Debug.Log("[MP][events] choice click swallowed on client for '" + ev.EventID + "' — the host decides");
            return false;
        }
    }

    /// <summary>
    /// Esc/back on the client's mirrored dialog: while the record is UNRESOLVED the modal must stay open
    /// (the native OnCancel → FinishQueriedState → ExitState guard would locally complete a Triggered
    /// event, UIStateGeoscapeEvent.cs:61-65). Once the host resolved it, allow the native close — after
    /// refreshing the dialog's stale Record ref so that same guard stays silent.
    /// </summary>
    [HarmonyPatch(typeof(UIStateGeoscapeEvent), "OnCancel")]
    internal static class EventCancelClientLock
    {
        private static bool Prefix(UIStateGeoscapeEvent __instance)
        {
            if (!EventPopup.IsClient) return true;
            var ev = __instance.Event;
            if (string.IsNullOrEmpty(ev?.EventID)) return true;
            var rec = EventPopup.LiveRecord(ev.EventID, ev.Record);
            if (rec != null && rec.State == GeoscapeEventRecordState.Triggered) return false; // unresolved — the host closes it
            if (rec != null) ev.Record = rec;
            return true;
        }
    }
}
