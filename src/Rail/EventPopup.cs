using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters;
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
    /// Client choice resolution stays FORBIDDEN (host-authoritative), but the click is no longer
    /// swallowed: <see cref="EventChoiceClientLock"/> BLOCKS it and relays it as a 0xB4 answer intent
    /// (<see cref="EventSync"/>), whose host-side arbiter freezes the first answer for everyone.
    /// Esc is still held while the record is unresolved — on BOTH peers.
    ///
    /// A frozen answer is also what the losers SEE: <see cref="EventChoiceFreeze"/> greys every losing
    /// button and marks the winner on every render of the dialog, and <see cref="RepaintDialog"/> flips an
    /// already-open picker in place instead of closing and re-raising it.
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
        private static readonly System.Reflection.FieldInfo ChoiceButtonsField =
            AccessTools.Field(typeof(SiteBaseChoicesController), "Choices");                 // protected, SiteBaseChoicesController.cs:23
        // GeoscapeEvent.IsCompleted / .ChoiceReward are { get; private set; } (GeoscapeEvent.cs:32, :36).
        private static readonly System.Reflection.MethodInfo SetIsCompleted =
            AccessTools.PropertySetter(typeof(GeoscapeEvent), "IsCompleted");
        private static readonly System.Reflection.MethodInfo SetChoiceReward =
            AccessTools.PropertySetter(typeof(GeoscapeEvent), "ChoiceReward");

        /// <summary>Reload/session boundary: forget the in-flight raise set and re-derive the cursor against
        /// the records the new save actually carries. The cursor is NOT dropped here — it is this peer's
        /// private progress through the history and it outlives the process (<see cref="StoreCursor"/>).
        /// Zeroing it was what made an unread OUTCOME window vanish on every reload: the re-seed then fell
        /// back to "max over all RESOLVED records", i.e. exactly the windows the player had not read yet.</summary>
        public static void Reset()
        {
            _inFlight.Clear();
            _loggedSkips.Clear();
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
            RetireClosed(view, records);

            var backlog = Backlog(records, _cursor);
            bool announced = false;
            foreach (var rec in backlog)
            {
                if (_inFlight.Contains(rec.EventId) || _loggedSkips.Contains(rec.EventId)) continue;
                if (IsQueuedNatively(view, rec.EventId)) { _inFlight.Add(rec.EventId); continue; }
                if (!announced)
                {
                    announced = true; // one line per pass that actually raises — the pump itself stays silent
                    Debug.Log("[MP][events] backlog n=" + backlog.Count + " cursor=" + _cursor +
                              " next='" + rec.EventId + "' mode=" + Mode(rec.State));
                }
                Raise(es, geo, q, rec);
            }
        }

        /// <summary>First pass after a reload/join. Two sources, in this order:
        ///   • the PERSISTED cursor, when this peer has one — that is the whole point of persisting it: a
        ///     reload must not replay what the player already clicked through, and must not swallow what they
        ///     had not read yet. CLAMPED to the newest record present, which is what makes the storage key
        ///     safe without a campaign id (see <see cref="CursorKey"/>): a value carried over from another
        ///     campaign can then retire at most the resolved records — exactly the fallback below — and never
        ///     more, because a still-<c>Triggered</c> record rides the backlog regardless of the cursor.
        ///   • otherwise the record seed: the transferred save carries the whole campaign's resolved history,
        ///     which is NOT this player's unseen backlog, so max over the RESOLVED records — an unanswered
        ///     event still rides.
        /// Logged either way, with which branch ran — a boundary that drops work silently is the bug class
        /// this file exists to kill.</summary>
        private static void SeedCursor(IDictionary<string, GeoscapeEventRecord> records)
        {
            if (_cursorSeeded) return;
            _cursorSeeded = true;
            long resolved = 0, newest = 0;
            foreach (var rec in records.Values)
            {
                if (rec == null) continue;
                long t = rec.LastTriggerAt.TimeSpan.Ticks;
                if (t > newest) newest = t;
                if (rec.State != GeoscapeEventRecordState.Triggered && t > resolved) resolved = t;
            }
            long saved = LoadCursor();
            if (saved > 0)
            {
                _cursor = saved < newest ? saved : newest;
                Debug.Log("[MP][events] cursor restored to " + _cursor + " (persisted " + saved + ", newest record " +
                          newest + ") from " + records.Count + " record(s) — a window this peer never read is " +
                          "still owed to it");
            }
            else
            {
                _cursor = resolved;
                Debug.Log("[MP][events] cursor seeded to " + _cursor + " from " + records.Count +
                          " record(s) — first join on this peer, resolved history before that point is not replayed");
            }
        }

        /// <summary>Cursor codec. Invariant BOTH ways, deliberately: the value is a TimeUnit tick count that
        /// crosses a process boundary as a STRING, and a peer whose locale formats or parses integers with
        /// group separators would read back 0 = "replay this peer's entire event history" with nothing logged
        /// as wrong. Pure and PlayerPrefs-free so RailCheck L26 can round-trip it headless — a method that
        /// merely REFERENCES a PlayerPrefs ECall fails to JIT there (ClientIdentity.cs:82-88).</summary>
        internal static string FormatCursor(long ticks) => ticks.ToString(CultureInfo.InvariantCulture);

        internal static long ParseCursor(string stored) =>
            long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ? ticks : 0;

        /// <summary>PER-PEER, not per-machine: two instances on ONE machine (the local co-op test rig,
        /// D:\PP-Instance2) share the PlayerPrefs store, so a single key would let player 1's progress
        /// swallow player 2's backlog. <c>ClientIdentity.PlayerGuid</c> is already per-instance (its own
        /// identity-N.json, plus the MULTIPLAYER_IDENTITY override), so it is the key.
        /// NOT per-campaign: this game exposes no campaign id (no GUID, no seed; Timing.StartTime is the same
        /// def constant for every campaign), so two parallel campaigns DO share one entry. That is made
        /// harmless in <see cref="SeedCursor"/> rather than papered over: clamping the loaded value to the
        /// newest live record degrades a foreign cursor to precisely the record-seed behaviour, which is what
        /// a first join does anyway. ponytail: if a campaign id ever appears, append it here.</summary>
        private static string CursorKey() => "Multiplayer_EventCursor_" + ClientIdentity.PlayerGuid;

        /// <summary>Wrapped like ClientIdentity's own prefs access: PlayerPrefs.* are InternalCall externs
        /// that throw at JIT time in a headless host, BEFORE any try/catch in the referencing method runs —
        /// hence the NoInlining isolation, and hence a caller-side catch. A prefs failure must never break the
        /// pump: worst case the backlog replays after a reload, which is where this started.</summary>
        private static long LoadCursor()
        {
            try { return ParseCursor(ReadCursorPref()); }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][events] cursor load failed (" + ex.Message + ") — falling back to the record seed");
                return 0;
            }
        }

        private static void StoreCursor(long ticks)
        {
            try { WriteCursorPref(FormatCursor(ticks)); }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][events] cursor save failed (" + ex.Message + ") — this peer's backlog will " +
                                 "replay from the record seed after a reload");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadCursorPref() => PlayerPrefs.GetString(CursorKey(), "");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteCursorPref(string value)
        {
            PlayerPrefs.SetString(CursorKey(), value);
            PlayerPrefs.Save();
        }

        /// <summary>A window we pushed is gone from the whole native pipeline ⇒ the player clicked
        /// through it. That is the ONLY thing that advances the cursor, so a crash or quit re-shows it —
        /// and the ONLY thing that writes it out, once per pass rather than once per id.</summary>
        private static void RetireClosed(GeoscapeView view, IDictionary<string, GeoscapeEventRecord> records)
        {
            if (_inFlight.Count == 0) return;
            List<string> closed = null;
            foreach (var id in _inFlight)
                if (!IsQueuedNatively(view, id)) (closed ?? (closed = new List<string>())).Add(id);
            if (closed == null) return;
            foreach (var id in closed)
            {
                _inFlight.Remove(id);
                long t = records.TryGetValue(id, out var rec) && rec != null ? rec.LastTriggerAt.TimeSpan.Ticks : _cursor;
                if (t > _cursor) _cursor = t;
                Debug.Log("[MP][events] cursor advanced to " + _cursor + " after closing '" + id + "'");
            }
            StoreCursor(_cursor);
        }

        /// <summary>Law 11 repaint of the OPEN event dialog — the <c>UiNativeRepaint.Table</c> entry for
        /// <c>UIStateGeoscapeEvent</c>, READ-direction. Two jobs:
        ///   • refresh the dialog's stale <c>Record</c> ref: a blob apply rebuilds the record INSTANCE, and
        ///     <c>UIStateGeoscapeEvent.ExitState</c>:61-65 reads <c>Event.Record.State</c> to decide whether to
        ///     complete the event LOCALLY, so a stale <c>Triggered</c> ref there is a client-side resolution of
        ///     a host-authoritative choice;
        ///   • re-drive the module's own <c>SetChoices</c>, which re-reads each button's affordability from the
        ///     model (<c>SiteBaseChoicesController.SetChoice</c>:60 → <c>choice.PassRequirements(faction,
        ///     context)</c> = the wallet) and, through <see cref="EventChoiceFreeze"/>, re-applies the freeze
        ///     when the record was resolved under an open PICKER — the "you see the outcome and cannot
        ///     re-pick" half of the feature. This REPLACED a forced close+reopen of the window.
        /// Deliberately NOT <c>ShowEncounter</c> (the screen's full re-init): it re-posts
        /// <c>OpenEncounterSoundEvent</c> (UIModuleSiteEncounters.cs:195-198) and resets
        /// <c>_encounterTextIndex</c> to 0 (:220), so on a peer where deltas land continuously it would
        /// machine-gun the encounter sound and make a multi-page description impossible to page through. Same
        /// rule as the <c>UIModuleBaseLayout.Init</c> exclusion in the table: the narrow native refresh.
        /// Returning true even when there was nothing to refresh is the OTHER half of this entry — the
        /// fallback Exit+Enter (OpenUiRepaint.cs:189-206) must never run for this screen, because Exit runs
        /// that ExitState write-back. RailCheck L21 asserts the table key is present.</summary>
        internal static bool RepaintDialog(GeoscapeViewState state, GeoscapeView view)
        {
            var ev = (state as UIStateGeoscapeEvent)?.Event;
            var module = view?.GeoscapeModules?.SiteEncountersModule;
            if (ev == null || module == null || module.Context == null) return true;
            if (!string.IsNullOrEmpty(ev.EventID))
            {
                var rec = LiveRecord(ev.EventID, ev.Record);
                if (rec != null) ev.Record = rec;
            }
            var ctrl = module.ChoicesButtonController;
            if (ctrl != null && ShowingRealChoices(ctrl, ev))
                ctrl.SetChoices(module.Context.ViewerFaction, module.ChoiceButtonsContainer, module.ChoiceButtonPrefab, ev);
            return true;
        }

        /// <summary>Is the dialog showing the REAL event's choice buttons, or one of the module's own
        /// synthetic pages? A paging page (<c>SetPagingEncounter</c>:309-321) and the outcome/closing page
        /// (<c>SetClosingEncounter</c>:326-355) are each built inline over a throwaway
        /// <c>GeoscapeEvent</c> with <c>EventID == ""</c> whose text is already a frozen string and whose
        /// single OK button must stay live — re-running <c>SetChoices</c> with the REAL event there would
        /// replace that button with the picker. The module keeps no flag for which of the three it shows, so
        /// the question is asked of the BUTTONS: <c>SiteBaseChoiceButton.Choice</c> holds the very
        /// <c>GeoEventChoice</c> instance it was set from (SiteBaseChoiceButton.cs:43-47) and the def's list is
        /// the only source of those instances, so reference identity answers it.</summary>
        private static bool ShowingRealChoices(SiteBaseChoicesController ctrl, GeoscapeEvent ev)
        {
            var choices = ev.EventData?.Choices;
            if (choices == null) return false;
            foreach (var btn in ActiveButtons(ctrl))
                for (int i = 0; i < choices.Count; i++)
                    if (ReferenceEquals(btn.Choice, choices[i])) return true;
            return false;
        }

        private static IEnumerable<SiteBaseChoiceButton> ActiveButtons(SiteBaseChoicesController ctrl)
        {
            if (!(ChoiceButtonsField?.GetValue(ctrl) is List<SiteBaseChoiceButton> list)) yield break;
            foreach (var btn in list)
                if (btn != null && btn.Button != null && btn.gameObject.activeSelf) yield return btn;
        }

        /// <summary>"The first choice is frozen for everyone" as the player SEES it: on a dialog whose record
        /// is already resolved, every losing button goes non-interactable and the winner is shown SELECTED.
        /// Native widget API only — <c>PhoenixGeneralButton.SetInteractable(false)</c> clears
        /// <c>BaseButton.interactable</c> + <c>IsEnabled</c>, which <c>OnPointerClick</c>:327 requires, and
        /// <c>ResetButtonAnimations</c>:180-183 paints the greyed state. The winner keeps
        /// <c>interactable</c> and gets <c>IsSelected</c>: with the widget's OWN default
        /// <c>IsNonInteractableWhenSelected</c> (true, PhoenixGeneralButton.cs:37) that is click-dead by the
        /// same :327 test while painting the selected/pressed look (:184-189) — "this is what was chosen",
        /// rather than a live-looking button whose click <see cref="EventChoiceClientLock"/> would silently
        /// drop. (The note's draft forced that flag false to keep the winner clickable; the click cannot be
        /// honoured, so a dead-looking-live button would be the lie.)
        /// <c>IsSelected</c> is written on EVERY active button, not only the winner: the buttons are POOLED
        /// and reused for the next window (<c>AddChoicesButtons</c>:67-75 builds the list once), so a winner
        /// flag left behind would deaden that slot in the next picker — including the OK button of a closing
        /// page, whose click is the only way out.</summary>
        internal static void FreezeChoiceButtons(SiteBaseChoicesController ctrl, GeoscapeEvent displayed)
        {
            if (!InSession) return; // solo: stock behaviour, and nothing outside a session ever sets IsSelected
            bool frozen = IsFrozen(displayed, out var winner);
            foreach (var btn in ActiveButtons(ctrl))
            {
                bool isWinner = frozen && winner != null && ReferenceEquals(btn.Choice, winner);
                btn.Button.IsSelected = isWinner;
                if (frozen && !isWinner) btn.Button.SetInteractable(false);
                else btn.Button.ResetButtonAnimations();
            }
        }

        /// <summary>Is this dialog's answer already decided, and by which choice? The ledger is the
        /// REPLICATED record, never the instance: <c>GeoscapeEvent.IsCompleted</c> is per-INSTANCE
        /// (GeoscapeEvent.cs:36) and a synthesised instance over a resolved record reports false. An empty
        /// <c>EventID</c> is one of the module's own synthetic pages — nothing to freeze. A resolved record
        /// with <c>SelectedChoice</c> outside the def's range (native's -1 "no choice",
        /// UIModuleSiteEncounters.cs:562-566) freezes every button and leaves no winner.</summary>
        private static bool IsFrozen(GeoscapeEvent ev, out GeoEventChoice winner)
        {
            winner = null;
            if (string.IsNullOrEmpty(ev?.EventID)) return false;
            var rec = LiveRecord(ev.EventID, ev.Record);
            if (rec == null || rec.State == GeoscapeEventRecordState.Triggered) return false;
            var choices = ev.EventData?.Choices;
            int i = rec.SelectedChoice;
            if (choices != null && i >= 0 && i < choices.Count) winner = choices[i];
            return true;
        }

        /// <summary>Is a window for this event id anywhere in the native pipeline — waiting in the
        /// switch queue, popped and mid-switch, or on screen? A transferred save restores the HOST's
        /// pending queue (GeoscapeView.cs:349 → GeoscapeViewSwitchQuery.RestoreData:39-56), so without
        /// this a joiner sees each of those windows TWICE. Silent on a hit: "we already have that
        /// window" is the steady state, and the raise that put it there was logged once.</summary>
        private static bool IsQueuedNatively(GeoscapeView view, string eventId) => LiveInstance(view, eventId) != null;

        /// <summary>The live <c>GeoscapeEvent</c> this peer's own view holds for an id — on screen, popped
        /// and mid-switch, or still queued — else null. The HOST answer handler wants it because that
        /// instance carries the real <c>Context</c> (site + vehicle) the reward is applied against; the
        /// client pump wants only whether it exists.</summary>
        internal static GeoscapeEvent LiveInstance(GeoscapeView view, string eventId)
        {
            if (view == null || string.IsNullOrEmpty(eventId)) return null;
            var onScreen = EventOf(view.CurrentViewState);
            if (onScreen != null && onScreen.EventID == eventId) return onScreen;
            if (!(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q)) return null;
            if (CurrentRequestField?.GetValue(q) is GeoscapeViewStateSwitchRequest cur)
            {
                var mid = EventOf(cur.State);
                if (mid != null && mid.EventID == eventId) return mid;
            }
            if (RequestsField?.GetValue(q) is IEnumerable<GeoscapeViewStateSwitchRequest> pending)
                foreach (var r in pending)
                {
                    var queued = r == null ? null : EventOf(r.State);
                    if (queued != null && queued.EventID == eventId) return queued;
                }
            return null;
        }

        private static GeoscapeEvent EventOf(object state) => (state as UIStateGeoscapeEvent)?.Event;

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
        /// <c>ShowEncounter</c> takes the <c>SetEncounter</c> branch and calls nothing that resolves.
        /// Second consumer: <see cref="EventCompleteArbiter"/> refusing a resolution needs the very same
        /// pair of writes — a non-null reward to hand back and an instance that will not try again.</summary>
        internal static void MarkResolvedInstance(GeoscapeEvent ev)
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

        /// <summary>An MP session is live, either side. The choice FREEZE and the Esc guard are peer-symmetric
        /// multiplayer presentation rules — a host holding a dialog another peer already answered is the same
        /// case as a client — while outside a session neither may touch the native dialog at all.</summary>
        internal static bool InSession
        {
            get
            {
                var e = NetworkEngine.Instance;
                return e != null && e.IsActiveSession;
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
    /// Intent-capture seam (law 4a), BLOCK-FIRST: the client never resolves an event choice locally — the
    /// native handler (UIModuleSiteEncounters.OnChoiceSelected:546) would take the choice's cost out of
    /// the local wallet (:573, BEFORE any arbitration can happen) and run CompleteEvent, applying the
    /// whole reward client-side (divergence the rail cannot fully correct — StartMission spawns are
    /// structural). Blocked, the click becomes a 0xB4 <c>answer</c> intent and the HOST resolves it.
    /// Paging clicks (multi-page description text, <c>_pagingEvent</c>:548) stay native — they advance
    /// text only.
    ///
    /// The HOST arm is not a pass-through: a host dialog whose record another peer already answered must
    /// not reach :573 either, or the losing host pays for a choice it did not get.
    /// <see cref="EventCompleteArbiter"/> cannot cover that — the charge happens two calls before the
    /// funnel it guards.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "OnChoiceSelected")]
    internal static class EventChoiceClientLock
    {
        private static readonly System.Reflection.FieldInfo PagingField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_pagingEvent");
        private static readonly System.Reflection.FieldInfo GeoEventField = AccessTools.Field(typeof(UIModuleSiteEncounters), "_geoEvent");

        private static bool Prefix(UIModuleSiteEncounters __instance, GeoEventChoice choice)
        {
            if (PagingField != null && (bool)(PagingField.GetValue(__instance) ?? false)) return true;
            var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
            if (string.IsNullOrEmpty(ev?.EventID)) return true; // not a mirrored host event
            var rec = EventPopup.LiveRecord(ev.EventID, ev.Record);
            bool frozen = rec == null || rec.State != GeoscapeEventRecordState.Triggered;

            if (IntentRail.ShouldRunNative())
            {
                // HOST / solo / inside an apply. Refuse only a click that would CHARGE or RESOLVE on a
                // record that is no longer open. A choice carrying neither Outcome nor Requirments is the
                // closing page's OK button (SetClosingEncounter:346-351 builds it with Text only): it
                // charges nothing (:571 needs Requirments) and it is the ONLY way out of a re-enabled
                // event's page, where the record is already Reset while the instance still reports
                // IsCompleted==false (GeoscapeEvent.cs:112-116 skips the flag on a Reset record).
                if (frozen && !ev.IsCompleted && choice != null && (choice.Outcome != null || choice.Requirments != null))
                {
                    Debug.Log("[MP][events] stale click on '" + ev.EventID + "' ignored — already answered (record=" +
                              (rec == null ? "none" : rec.State.ToString()) + ", choice " +
                              (rec == null ? -1 : rec.SelectedChoice) + ")");
                    return false;
                }
                return true;
            }

            // The OK/Continue button of a CLOSING page carries neither Outcome nor Requirments
            // (SetClosingEncounter:346-351 builds it with Text only), yet _geoEvent still points at the
            // REAL event there — SetEncounter never reassigns it (:265-303). Without this arm the only
            // button on a mirrored OUTCOME window is dead and Esc is the only way out. It resolves
            // nothing: no Wallet.Take (:571-573 needs Requirments) and CompleteEvent is skipped on an
            // instance already marked completed (:562-567 and :580 → SelectChoice:600).
            if (ev.IsCompleted && choice != null && choice.Outcome == null && choice.Requirments == null) return true;
            if (frozen)
            {
                // Not an error — two peers clicking within one RTT is the normal case. Reaching this at all
                // is the one-frame overlap: EventChoiceFreeze has normally already killed these buttons.
                Debug.Log("[MP][events] click on '" + ev.EventID + "' dropped — already answered (record=" +
                          (rec == null ? "none" : rec.State.ToString()) + "), the first choice is frozen");
                return false;
            }
            // The wire carries the INDEX into the def's Choices, which is what CompleteEvent works in
            // (GeoscapeEvent.cs:97); -1 is native's "no choice" resolution (:562-566).
            int index = ev.EventData?.Choices == null ? -1 : ev.EventData.Choices.IndexOf(choice);
            if (choice != null && index < 0)
            {
                Debug.LogWarning("[MP][events] click on '" + ev.EventID + "' dropped — the clicked choice is not in " +
                                 "this peer's def (mod parity), so it cannot be keyed for the host");
                return false;
            }
            string id = ev.EventID;
            IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpAnswer, "answer '" + id + "' choice=" + index,
                            w => { w.Write(id); w.Write(index); });
            return false;
        }
    }

    /// <summary>
    /// Esc/back on a mirrored event dialog, PEER-SYMMETRIC (the name is history — this covers the host too):
    /// while the record is UNRESOLVED the modal must stay open, because the native
    /// OnCancel → FinishQueriedState → ExitState guard answers a still-Triggered event with
    /// <c>Choices.Last()</c> (UIStateGeoscapeEvent.cs:61-65). On a client that is a client-side resolution of
    /// a host-authoritative choice; on the HOST it is worse — it really does resolve, for everyone, with a
    /// choice nobody picked, behind <see cref="EventCompleteArbiter"/>'s back, on the mere press of Esc.
    /// Native intent agrees: that branch logs an ERROR ("UI is in invalid state"). Once the record IS
    /// resolved the native close is allowed through — after refreshing the dialog's stale Record ref so that
    /// same guard reads the resolved state and stays silent. Off outside a session (stock behaviour).
    /// </summary>
    [HarmonyPatch(typeof(UIStateGeoscapeEvent), "OnCancel")]
    internal static class EventCancelClientLock
    {
        private static bool Prefix(UIStateGeoscapeEvent __instance)
        {
            if (!EventPopup.InSession) return true;
            var ev = __instance.Event;
            if (string.IsNullOrEmpty(ev?.EventID)) return true;
            var rec = EventPopup.LiveRecord(ev.EventID, ev.Record);
            if (rec != null && rec.State == GeoscapeEventRecordState.Triggered) return false; // unresolved — answering is the only way out
            if (rec != null) ev.Record = rec;
            return true;
        }
    }

    /// <summary>
    /// Presentation seam (law 4c) for "the first answer is frozen for everyone". EVERY path that (re)builds
    /// the encounter's choice buttons goes through <c>SiteBaseChoicesController.SetChoices</c> — the picker
    /// (<c>UIModuleSiteEncounters.SetEncounter</c>:287), the paging page (:321) and
    /// <see cref="EventPopup.RepaintDialog"/> — so applying the freeze HERE covers the window opening in
    /// outcome mode straight out of the backlog, a picker resolving under the player mid-look, and a history
    /// window opened long after the fact, with one code path instead of one per entry point.
    /// </summary>
    [HarmonyPatch(typeof(SiteBaseChoicesController), "SetChoices")]
    internal static class EventChoiceFreeze
    {
        private static void Postfix(SiteBaseChoicesController __instance, GeoscapeEvent eventData) =>
            EventPopup.FreezeChoiceButtons(__instance, eventData);
    }
}
