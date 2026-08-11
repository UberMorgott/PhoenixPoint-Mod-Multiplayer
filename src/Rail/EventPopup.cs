using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Events.Eventus;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewControllers.SiteEncounters;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 presentation for geoscape EVENT WINDOWS on the client: the host ships ONE PRESENTATION
    /// PAYLOAD per raise (surface <see cref="SurfaceIds.GeoEventRaise"/> 0xB6) and the client rebuilds the
    /// REAL native window from it. There is no history, no cursor and no backlog — a window is a live
    /// host→client message, so a peer that was not in the session when an event fired simply never sees it.
    ///
    /// THIS REPLACED a record-derived queue (deleted 2026-07-30), which was structurally impossible.
    /// <c>GeoscapeEventRecord</c> persists EventId/timestamps/state/_selectedChoice/_triggerCount and
    /// NOTHING else (GeoscapeEventRecord.cs:9-36): no site, no vehicle, no context. The native
    /// <c>GeoscapeEventContext</c> — which every <c>[HavenName]</c>/<c>[HavenLeader]</c>/<c>[AircraftName]</c>
    /// token dereferences UNGUARDED (GeoscapeEventContext.cs:20-40) — is built fresh per raise and is not in
    /// the save graph, so a record can never reconstitute one. Rebuilding windows from records therefore
    /// produced context-LESS windows: measured 54 of 94 replayed raises had <c>site=null</c>, each rendering
    /// the raw <c>[HavenName]</c> token over the scene's baked dev placeholder text, with a live
    /// "START MISSION" button underneath. The replay itself was the second half of the bug: the persisted
    /// per-peer cursor made every joining peer re-raise the campaign's entire event history.
    ///
    /// The payload carries what the RECORD cannot: the site + vehicle the host's own context held (as rail
    /// root refs, law 2 — <c>IdentityResolver.RootRef</c>/<c>Resolve</c>, the same addressing the value rail
    /// uses) plus the two strings the host actually resolved. The client resolves those refs against its own
    /// live graph, builds a REAL <c>GeoscapeEventContext</c>, and — if it cannot (<see cref="ContextRefusal"/>)
    /// — raises NOTHING and says so. A half-built window is worse than no window.
    ///
    /// Raising reuses the game's own queue and dialog — no custom UI: <c>GeoscapeViewSwitchQuery</c> gives
    /// click-through-one-at-a-time and reload persistence for free, exactly as
    /// <c>GeoscapeView.OnGeoscapeEventRaised</c>:2034-2066 does it, with <c>PauseGame=false</c> (pause is
    /// host-authoritative and arrives via the TimeAnchor).
    ///
    /// Client choice resolution stays FORBIDDEN (host-authoritative): <see cref="EventChoiceClientLock"/>
    /// BLOCKS the click and relays it as a 0xB4 answer intent (<see cref="EventSync"/>), whose host-side
    /// validator freezes the first answer for everyone; <see cref="EventCompleteArbiter"/> is the model-funnel
    /// backstop. The answer comes back as the record's own 0xAC leaves, and THAT delta is the dismiss signal:
    /// <see cref="RepaintDialog"/> closes an open picker whose record has been resolved by someone else.
    /// </summary>
    internal static class EventPopup
    {
        private const byte DurableDecisionPayload = 0xD8;
        private const byte DurableRewardPayload = 0xD7;
        private static float _nextDurableDecisionReplay;
        internal static DurableCarrierLease BindDeferredCarrier(DurableInboxStore store,
            OccurrenceId occurrence, Action<TerminalReason> silentRemove) =>
            DurableCarrierLease.Bind(store, occurrence, DurableCarrierClass.ModDeferred, silentRemove);

        internal static bool TryFindDurableOccurrence(string eventId, out OccurrenceId occurrence)
        {
            occurrence = default(OccurrenceId);
            var store = DurableInboxSession.ActiveStore;
            if (store == null || string.IsNullOrEmpty(eventId)) return false;
            var found = store.Ledger.AllEntries.Where(x => x.Occurrence.EventId == "EventPopup" &&
                x.Occurrence.SubjectIds.Contains("event:" + eventId, StringComparer.Ordinal) &&
                x.Lifecycle != InboxLifecycle.Removed).Select(x => x.Occurrence).Distinct().ToArray();
            if (found.Length != 1) return false;
            occurrence = found[0]; return true;
        }

        internal static bool TryGetDurableOccurrence(GeoscapeEvent ev, out OccurrenceId occurrence)
        {
            occurrence = default(OccurrenceId); RaiseBinding binding;
            return ev != null && Bound.TryGetValue(ev, out binding) && binding.Occurrence.EventId != null &&
                ((occurrence = binding.Occurrence).EventId != null);
        }

        internal static void RepaintDurableChoice(OccurrenceId occurrence)
        {
            var geo = GeoLevel();
            UiEventMap.FireDurableChoice(geo);
        }
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

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
        // The module's OWN outcome page (UIModuleSiteEncounters.cs:324, private) — reused rather than
        // redrawn, so the replay a peer sees is the same native page the answering peer got.
        private static readonly System.Reflection.MethodInfo SetClosingEncounter =
            AccessTools.Method(typeof(UIModuleSiteEncounters), "SetClosingEncounter",
                               new[] { typeof(GeoscapeEvent), typeof(GeoEventChoice), typeof(bool) });

        /// <summary>FULL session teardown only (SyncEngine.DetachAllChannels) — the raise seq is a host
        /// monotonic stream and a client last-writer guard, so it must NOT be touched at a mid-session
        /// reload boundary (rca-3 contract: a host counter that restarts mid-session makes every following
        /// raise look stale to a client that kept its own high-water mark, and the windows vanish silently).</summary>
        public static void Reset()
        {
            _nextDurableDecisionReplay = 0f;
            Seq.Reset(); lock (_durableHeld) { foreach (var lease in _durableHeld.Values.ToArray()) lease.Dispose();
            _durableHeld.Clear(); } _held.Clear(); _hostCurtained.Clear(); _unanswered.Clear(); _rewards.Clear();
            _durableRewards.Clear();
        }
        internal static void ClearDurableCarrierState()
        {
            lock (_durableHeld)
            { foreach (var lease in _durableHeld.Values.ToArray()) lease.Dispose(); _durableHeld.Clear(); _held.Clear(); }
        }

        /// <summary>The HOST's OWN raises, parked behind the HOST's OWN curtain, in the order its sim made
        /// them. See <see cref="HostRaiseWaitsForReveal"/> for why this list has to exist at all.</summary>
        private static readonly List<GeoscapeEvent> _hostCurtained = new List<GeoscapeEvent>();

        // GeoscapeView.OnGeoscapeEventRaised:2034 — the game's own handler, private, and re-invoked (not
        // re-implemented) when the curtain lifts. The game itself calls it out of band at ToMarketplace:737,
        // so a second manual call is a shape the class already supports.
        private static readonly System.Reflection.MethodInfo NativeRaiseMethod =
            AccessTools.Method(typeof(GeoscapeView), "OnGeoscapeEventRaised", new[] { typeof(GeoscapeEvent) });

        /// <summary>PURE (RailCheck L194). MUST A HOST-LOCAL RAISE WAIT FOR THIS HOST'S OWN REVEAL?
        ///
        /// THE REPORT (2026-08-08): the host got three intro dialogs and THEN the intro cutscene; both
        /// clients got the cutscene and THEN the three dialogs. All four requests are priority 0, so
        /// <c>GeoscapeViewSwitchQuery</c> serves them in INSERT order and insert order was the only thing
        /// that differed. It differed for one reason: a client cannot carry a window while it is still at the
        /// loading screen, so its three raises sat in <see cref="_held"/> and were replayed AFTER the reveal,
        /// i.e. after the cutscene had been queued — while the host, whose own geoscape is fully live behind
        /// the curtain, queued and SERVED all three before the cutscene existed. <c>GeoscapeView.Update</c>
        /// keeps calling <c>ProcessQueriedStateSwitch</c> throughout a curtain; the curtain hides the screen,
        /// it does not stop the queue.
        ///
        /// So the host is given the SAME wait it already imposes on every client, out of the state it already
        /// owns: <c>SaveTransferCoordinator.Revealed</c>. This is NOT a quorum and cannot become one — the
        /// predicate reads this peer's session flag, this peer's role and this peer's own reveal, and that
        /// reveal is released by the load barrier (<c>AllDone(GetRosterSlots())</c> → <c>RevealAll</c>), which
        /// ends by itself and SHRINKS when a peer drops. Nobody waits on a human ACTING.
        ///
        /// A client answers FALSE here on purpose. Its raises are mirrored, not local, and they already have
        /// their own wait in <see cref="CanCarryWindow"/>; adding a second one would hold a window that the
        /// drain is the only thing able to release.</summary>
        internal static bool HostRaiseWaitsForReveal(bool inSession, bool isHost, bool revealed)
            => inSession && isHost && !revealed;

        /// <summary>Has this peer revealed? TRUE when there is no coordinator at all (solo, or a session that
        /// never ran a transfer), because "no curtain" must never read as "curtained forever".</summary>
        private static bool Revealed(NetworkEngine engine)
            => engine?.SaveTransfer == null || engine.SaveTransfer.Revealed;

        /// <summary>Park the host's own raise until its curtain lifts, so every peer inserts this window
        /// AFTER its own reveal. Returns true when the native raise must be SKIPPED this time — the caller is
        /// the prefix on the game's own handler, and <see cref="DrainHostCurtained"/> re-invokes that same
        /// handler later, so nothing about the window is re-implemented here.</summary>
        internal static bool HoldHostRaiseBehindCurtain(GeoscapeEvent ev)
        {
            if (ev == null) return false;
            var engine = NetworkEngine.Instance;
            if (!HostRaiseWaitsForReveal(engine != null && engine.IsActiveSession,
                                         engine != null && engine.IsHost, Revealed(engine))) return false;
            if (_hostCurtained.Count >= MaxHeld)
            {
                Debug.LogError("[MP][events] HOST raise of '" + ev.EventID + "' queued BEHIND ITS OWN CURTAIN — " +
                               MaxHeld + " are already parked, so this host is not 'loading', and holding more " +
                               "would trade a window-order defect for a lost window. It keeps the host's order.");
                return false;
            }
            _hostCurtained.Add(ev);
            Debug.LogWarning("[MP][events] HOST raise of '" + ev.EventID + "' PARKED — this host has not revealed " +
                             "yet, and a window queued behind its own curtain is served before the cutscene every " +
                             "client queues at ITS reveal (" + _hostCurtained.Count + " parked)");
            return true;
        }

        /// <summary>Replay ONE parked host raise per frame, through the game's own handler, once this host has
        /// revealed and its geoscape can carry a window. One per frame for the same reason the client drain
        /// is: each raise inserts into the game's priority queue and draining one at a time keeps that queue's
        /// insert order the order the sim produced.</summary>
        private static void DrainHostCurtained(NetworkEngine engine)
        {
            if (_hostCurtained.Count == 0) return;
            if (HostRaiseWaitsForReveal(true, true, Revealed(engine))) return;
            var geo = GeoLevel();
            if (!CanCarryWindow(geo) || NativeRaiseMethod == null) return;

            var ev = _hostCurtained[0];
            _hostCurtained.RemoveAt(0);
            try
            {
                Debug.Log("[MP][events] replaying PARKED host raise of '" + ev.EventID + "' — this host has " +
                          "revealed, so its window now enters the queue behind the same cutscene every client " +
                          "queued at its own reveal (" + _hostCurtained.Count + " still parked)");
                NativeRaiseMethod.Invoke(geo.View, new object[] { ev });
            }
            catch (Exception ex)
            { Debug.LogError("[MP][events] parked host raise replay failed for '" + ev.EventID + "': " + ex); }
        }

        /// <summary>Raises that ARRIVED but had nowhere to go yet, oldest first. See
        /// <see cref="ClientTick"/> for why this exists and why it is bounded.</summary>
        private static readonly List<KeyValuePair<uint, Raise>> _held = new List<KeyValuePair<uint, Raise>>();
        private static readonly Dictionary<uint, DurableCarrierLease> _durableHeld =
            new Dictionary<uint, DurableCarrierLease>();

        /// <summary>The last raise this peer was given per event id — the carrier a window needs to exist at
        /// all (a mirrored <c>GeoscapeEventRecord</c> holds no site and no context). Keyed by event id, so it
        /// is bounded by the campaign's event catalogue and a re-raise overwrites rather than accumulates.
        /// Read once, by <see cref="RequeueUnanswered"/>, to carry a peer's unread windows across a battle —
        /// see <c>ReplenishSync.CarryUnreadWindowsPatch</c> for why the save cannot do it on a client.</summary>
        private static readonly Dictionary<string, KeyValuePair<uint, Raise>> _unanswered =
            new Dictionary<string, KeyValuePair<uint, Raise>>();

        /// <summary>The host's resolved CHOICE REWARD per event id (0xBD). A mirroring peer cannot compute
        /// this: <c>GeoscapeEvent.ChoiceReward</c> is written ONLY inside <c>CompleteEvent</c>
        /// (GeoscapeEvent.cs:101-102) and the very next line GRANTS it, so a client running it would MINT
        /// resources (law 3). Keyed by event id and last-write-wins, which is why it needs no SurfaceSeq: an
        /// event resolves once and a re-delivered payload is the SAME reward, so applying it twice is applying
        /// it once. Consumed by <see cref="MarkResolvedInstance"/>.</summary>
        private static readonly Dictionary<string, MissionOutcomeMirror.RewardWire> _rewards =
            new Dictionary<string, MissionOutcomeMirror.RewardWire>();
        private static readonly Dictionary<OccurrenceId, MissionOutcomeMirror.RewardWire> _durableRewards =
            new Dictionary<OccurrenceId, MissionOutcomeMirror.RewardWire>();

        /// <summary>The bound. A peer that is loading a geoscape holds windows for SECONDS, not minutes, so
        /// this is a wide margin over a legitimate burst (the host raised two the frame after a mission ended
        /// on 2026-08-01); past it the peer is not "loading", it is somewhere this seam does not understand,
        /// and an unbounded list would then grow for the rest of the session. Same posture as
        /// <c>GeoWindowCoverage.TrimQueue</c>'s 64: drop LOUDLY rather than leak.</summary>
        private const int MaxHeld = 32;

        // ─── THE PAYLOAD (host→all, surface 0xB6) ──────────────────────────

        /// <summary>One raise, as the wire carries it. Refs are rail ROOT REFS (law 2 hybrid addressing —
        /// "S#&lt;siteId&gt;" / "V#&lt;id&gt;@&lt;ownerFactionGuid&gt;"), "" when the host's own context had
        /// none. Texts are what the HOST resolved and they WIN on the client — applied to a private copy of
        /// the event data, never to the shared def (see <see cref="WithWireTexts"/>). They are the only thing
        /// that renders a def whose text exists solely as a host-side RUNTIME mutation (TFTV VoidOmen_{0..19}:
        /// empty loc keys + LocalizedTextBind(text, doNotLocalize:true) rewritten PER ROLL,
        /// TFTVODIandVoidOmenRoll.cs:638-639) or one the host composed over a valid static key
        /// (TFTVBaseDefenseGeoscape.cs:1227 then :1250).</summary>
        internal struct Raise
        {
            public string EventId;
            public string SiteRef;
            public string VehicleRef;
            public string Title;
            public string Narrative;
            /// <summary>The host's own DISPLAY priority for this window, read back off the queue entry the
            /// native raise just inserted. Windows are shown one at a time out of a PRIORITY-ORDERED queue —
            /// <c>GeoscapeView.OnGeoscapeEventRaised</c>:2044 gives an event-triggered raise 10 and everything
            /// else 0, then :2049/:2057 bump a completed window that supersedes a queued one to 15, and
            /// <c>GeoscapeViewSwitchQuery.QueryStateSwitch</c>:77-82 inserts before the first LOWER-priority
            /// entry while <c>GetNextQueriedStateSwitch</c>:111 pops [0]. Mirroring the raise without it
            /// queued every window at 0 on the client, so the moment two windows were pending the peers were
            /// looking at DIFFERENT events. TIES ARE NOT COVERED, AND THIS DOC USED TO CLAIM THEY WERE:
            /// "the client's inserts are the host's own order" is true only among MIRRORED raises (the raise
            /// <c>seq</c> is strictly increasing and <see cref="SurfaceSeq"/> drops anything out of order) and
            /// FALSE the moment the peer raises a window of its OWN in between — which the post-mission
            /// arrival batch does every single mission (<c>UIStateInitial.EnterState</c>:112-127 queues the
            /// outcome modal, the pandoran reveal and the resupply screen locally, AFTER the host has already
            /// queued the events that mission triggered). Measured 2026-08-04: host queued
            /// [RE26, PROG_AN2_WIN, UIStateReplenish] in one frame, the client queued
            /// [UIStateReplenish, RE26, PROG_AN2_WIN] with the two raises landing 103 ms later — all three at
            /// priority 0, so priority cannot separate them and no re-sort can either, because a locally-raised
            /// window carries no host ordering key at all. Closing it needs a host→client ordering signal that
            /// does not exist yet; RailCheck <b>L93 client-raise-not-host-ordered</b> holds the bug open.</summary>
            public int Priority;
        }

        /// <summary>[seq:u32][eventId][siteRef][vehicleRef][title][narrative][priority:i32]. Pure both ways so
        /// RailCheck L39/L46 can round-trip it headless — a wire that drops a field silently is a window
        /// rendered against the wrong context or shown in the wrong order, and both are invisible in a log.</summary>
        internal static byte[] Encode(uint seq, Raise p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(p.EventId ?? "");
                w.Write(p.SiteRef ?? "");
                w.Write(p.VehicleRef ?? "");
                w.Write(p.Title ?? "");
                w.Write(p.Narrative ?? "");
                w.Write(p.Priority);
                return ms.ToArray();
            }
        }

        internal static Raise Decode(byte[] payload, out uint seq)
        {
            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                seq = r.ReadUInt32();
                return new Raise
                {
                    EventId = WireString.ReadKey(r),
                    SiteRef = WireString.ReadKey(r),
                    VehicleRef = WireString.ReadKey(r),
                    Title = WireString.ReadText(r),
                    Narrative = WireString.ReadProse(r),
                    Priority = r.ReadInt32(),
                };
            }
        }

        // ─── HOST: capture at the native raise seam ────────────────────────

        /// <summary>Host-side broadcast of ONE window, called from the postfix on the native seam. Reads the
        /// live event's OWN context — the only place site + vehicle exist — and the two strings the native
        /// header/body render, then ships them. Never throws into game code: a raise this fails on is a
        /// window the client does not get, and it says so.</summary>
        internal static void HostBroadcast(GeoscapeView view, GeoscapeEvent ev)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            if (SyncApplyScope.Active) return;         // law 8: an apply that reaches the view never re-broadcasts
            var data = ev?.EventData;
            if (data == null || string.IsNullOrEmpty(ev.EventID)) return;
            try
            {
                var geo = GeoLevel();
                // The marketplace window is a purely LOCAL gesture on either peer (MarketplaceAbility
                // .ActivateInternal:43 → GeoscapeView.ToMarketplace:734-738 calls the view DIRECTLY) and its
                // offer list is not replicated (docs/rail-baseline.txt:14) — mirroring it would open a shop
                // over rows the other peer does not have. MarketplaceChoiceClientLock owns the client side.
                if (geo?.EventSystem != null && geo.EventSystem.IsEventTheMarketplace(data)) return;
                // v1's IsMissionDeployEvent, same reason: a PURE "Deploy / Leave" arrival prompt is a
                // host-LOCAL pre-decision window. The mission itself reaches the other peer through the
                // tactical deploy channel (law 5), so mirroring the prompt only produces a second window
                // whose Leave the host already took (in-game leak: PROG_AN2_MISS).
                if (IsMissionDeployEvent(data))
                {
                    Debug.Log("[MP][events] HOST raise of '" + ev.EventID + "' NOT mirrored — pure mission-deploy " +
                              "prompt (host-local arrival decision; the mission rides the tactical deploy channel)");
                    return;
                }
                var p = new Raise
                {
                    EventId = ev.EventID,
                    SiteRef = IdentityResolver.RootRef(ev.Context?.Site) ?? "",
                    VehicleRef = IdentityResolver.RootRef(ev.Context?.Vehicle) ?? "",
                    Title = LiveTitle(data),
                    Narrative = LiveNarrative(data, ev.Context),
                    Priority = QueuedPriority(view, ev),
                };
                uint seq = Seq.Next(SurfaceIds.GeoEventRaise);
                var store = DurableInboxSession.ActiveStore;
                if (store != null)
                {
                    var occurrence = new OccurrenceId("EventPopup", "raise:" + seq,
                        new[] { "event:" + p.EventId,
                                string.IsNullOrEmpty(p.SiteRef) ? "site:none" : p.SiteRef,
                                string.IsNullOrEmpty(p.VehicleRef) ? "vehicle:none" : p.VehicleRef });
                    DurableWindowRegistry.EnqueuePriorityOccurrence(store, occurrence);
                    Bound.Remove(ev); Bound.Add(ev, new RaiseBinding { Seq = seq, TriggerCount =
                        RaiseTriggerCount(ev.Record == null ? GeoscapeEventRecordState.Triggered : ev.Record.State,
                            ev.Record == null ? 0 : ev.Record.TriggerCount), Occurrence = occurrence });
                    var request = DurableWindowRegistry.LastQueuedRequest(r =>
                        r.State is UIStateGeoscapeEvent ui && ReferenceEquals(ui.Event, ev));
                    if (request != null) WindowOrder.BindDurable(request, occurrence);
                }
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, Encode(seq, p));
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][events] HOST raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority +
                          " site=" + (p.SiteRef == "" ? "none" : p.SiteRef) + " vehicle=" +
                          (p.VehicleRef == "" ? "none" : p.VehicleRef) + " titleLen=" + p.Title.Length +
                          " narrLen=" + p.Narrative.Length);
                // The host watches its own mission windows for the same reason a client does — it can LOSE
                // the race, and then its native SelectChoice:612 never ran here either (MissionArrivalNav).
                MissionArrivalNav.Watch(p.EventId, data, p.SiteRef, p.VehicleRef);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][events] HOST raise broadcast FAILED for '" + ev.EventID + "' — no peer will see " +
                               "this window: " + ex);
            }
        }

        /// <summary>The DISPLAY priority the host's own queue just recorded for this window — READ BACK from
        /// the request the native raise inserted a moment ago, never re-derived. Native's rule is not a pure
        /// function of the event (:2049/:2057 bump to 15 only if a NOT-yet-completed window for the same
        /// state type is already queued), so a second implementation of it would drift from the real queue
        /// the first time either side changed.</summary>
        private static int QueuedPriority(GeoscapeView view, GeoscapeEvent ev)
        {
            if (view != null && SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q &&
                RequestsField?.GetValue(q) is IEnumerable<GeoscapeViewStateSwitchRequest> pending)
                foreach (var r in pending)
                    if (r != null && ReferenceEquals(EventOf(r.State), ev)) return r.Priority;
            Debug.LogWarning("[MP][events] could not read the queued DISPLAY priority for '" + ev?.EventID +
                             "' — mirroring it at 0, so this window may sort differently on the client than " +
                             "on the host and the peers can end up looking at different events");
            return 0;
        }

        /// <summary>The title exactly as the native header renders it (UIModuleSiteEncounters
        /// .ShowEncounter:199 — <c>EventData.Title?.Localize()</c>).</summary>
        private static string LiveTitle(GeoscapeEventData data)
        {
            try { return data.Title?.Localize() ?? ""; }
            catch { return ""; }
        }

        /// <summary>The narrative exactly as the native body resolves it for the last page
        /// (<c>Description.Last().GetText(context)</c>, UIModuleSiteEncounters:335). Tokens are NOT baked —
        /// the client render runs <c>ReplaceEventTokens</c> itself against its OWN rebuilt context, so a
        /// haven name is the one the client's site carries, not a string frozen on the host.</summary>
        private static string LiveNarrative(GeoscapeEventData data, GeoscapeEventContext context)
        {
            try
            {
                var desc = data.Description;
                if (desc == null || desc.Count == 0) return "";
                return desc[desc.Count - 1]?.GetText(context) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>PURE classifier (RailCheck L39): a mission-deploy prompt is one where at least one choice
        /// LAUNCHES a mission and every other choice is a bare decline. Mixed story events — a mission choice
        /// alongside real rewarded alternatives — are NOT deploy prompts and do mirror (v1 9e80b24 regression).
        /// Fail OPEN (false) on anything unreadable: never suppress a legitimate window.</summary>
        internal static bool IsPureDeployPrompt(bool[] startsMission, bool[] declineOnly)
        {
            if (startsMission == null || declineOnly == null || startsMission.Length != declineOnly.Length) return false;
            bool anyMission = false;
            for (int i = 0; i < startsMission.Length; i++)
            {
                if (startsMission[i]) { anyMission = true; continue; }
                if (!declineOnly[i]) return false;   // a non-mission choice with a real payload ⇒ story event
            }
            return anyMission;
        }

        internal static bool IsMissionDeployEvent(GeoscapeEventData data)
        {
            var choices = data?.Choices;
            if (choices == null || choices.Count == 0) return false;
            var startsMission = new bool[choices.Count];
            var declineOnly = new bool[choices.Count];
            for (int i = 0; i < choices.Count; i++)
            {
                startsMission[i] = StartsMission(choices[i]);
                declineOnly[i] = startsMission[i] || !HasOutcomePayload(choices[i]);
            }
            return IsPureDeployPrompt(startsMission, declineOnly);
        }

        /// <summary>Does this choice launch a tactical mission? Unity default-constructs an EMPTY
        /// <c>OutcomeStartMission</c>, so non-null is not the signal — <c>MissionTypeDef</c> is
        /// (GeoEventChoiceOutcome:315 → RewardStartCustomMission).</summary>
        internal static bool StartsMission(GeoEventChoice choice) => choice?.Outcome?.StartMission?.MissionTypeDef != null;

        /// <summary>Can ANY answer to this window end in a tactical mission? The one question
        /// <see cref="MissionArrivalNav"/> asks before it watches an event at all, so a peer never watches
        /// (and can never be navigated by) a window that has no mission behind any of its buttons.</summary>
        internal static bool AnyChoiceStartsMission(GeoscapeEventData data)
        {
            var choices = data?.Choices;
            if (choices == null) return false;
            for (int i = 0; i < choices.Count; i++) if (StartsMission(choices[i])) return true;
            return false;
        }

        /// <summary>PURE (RailCheck L404 arm (e)). THE MISSION-START CONFIRMATION AS THE GAME ACTUALLY
        /// RAISES IT — the seam b63e1aa missed.
        ///
        /// That commit carved the family out of <c>UIStateGeoModal</c>
        /// (<see cref="GeoWindowCoverage.IsMissionStartConfirmationClass"/>, matched on
        /// <c>GeoscapeView.GetMissionBriefModal</c>:1724). MEASURED in the 22:42:36-22:45:38 playtest that
        /// ran that very build: NOT ONE <c>UIStateGeoModal</c> was entered on any of the three peers, while
        /// the window the owner was actually looking at — "НАЧАТЬ / ОТМЕНА" for <c>PROG_AN0_MISS</c> — came
        /// up as a GEOSCAPE EVENT (host raise, its log 5924; client mirrors, theirs 4287/4266). So the
        /// carve-out was never reached and this window kept shared first-wins: the answering peer's choice
        /// greyed every other peer's buttons (<see cref="FreezeChoiceButtons"/>) and their own click was
        /// replayed instead of answered. The second client sat in <c>UIStateGeoscapeEvent</c> from 121,0 s
        /// to 179,2 s and only left it when the host quit.
        ///
        /// THE CLASS IS THE GAME'S, NOT A LIST OF OURS, exactly as in the modal twin: any window a peer can
        /// answer with "enter this mission" is one. TFTV and DLC content inherit it for free because the
        /// question is asked of the def's own <c>OutcomeStartMission.MissionTypeDef</c>
        /// (<see cref="StartsMission"/>) — the same test <see cref="MissionArrivalNav"/> already arms on.</summary>
        internal static bool NeverFrozenClass(bool anyChoiceStartsMission) => anyChoiceStartsMission;

        /// <summary>The live question. <see cref="NeverFrozenClass"/> over this window's own choices.</summary>
        internal static bool IsMissionStartConfirmationEvent(GeoscapeEventData data) =>
            NeverFrozenClass(AnyChoiceStartsMission(data));

        // DIAGNOSTIC, 2026-08-10 — REMOVE WHEN THE MISSION-BRIEF REPORT IS CLOSED.
        // Same convention as RevealInputLock.ProbeInputDispatch: static verification passed twice while the
        // game stayed broken, so the runtime says which family a window matched, whether the per-peer
        // carve-out applied and how a click ended. Deduped per (event, phase) — these seams are polled.
        private static readonly HashSet<string> _briefProbed = new HashSet<string>();

        internal static void BriefProbe(string eventId, string phase, string detail)
        {
            var key = (eventId ?? "?") + "|" + phase;
            if (_briefProbed.Count > 256) _briefProbed.Clear();
            if (!_briefProbed.Add(key)) return;
            Debug.Log("[MP][brief] " + phase + " '" + (eventId ?? "?") + "' — " + detail);
        }

        /// <summary>Does this choice carry a REAL outcome beyond declining? Walked GENERICALLY over the
        /// outcome's public fields rather than against a hand-listed set: <c>GeoEventChoiceOutcome</c> has
        /// ~25 payload fields and DLCs add more, and a list that silently goes stale would start classifying
        /// story events as deploy prompts and suppressing them. Unity default-constructs empty lists/packs/
        /// texts, so non-null alone means nothing — payload is a non-empty string / non-zero int / true bool /
        /// any-element IEnumerable / live UnityEngine.Object / a set MissionTypeDef. Unknown non-null object
        /// field ⇒ payload (fail toward mirroring).</summary>
        private static bool HasOutcomePayload(GeoEventChoice choice)
        {
            var outcome = choice?.Outcome;
            if (outcome == null) return false;      // the game keys a bare decline as the null outcome
            try
            {
                if (!string.IsNullOrEmpty(outcome.OutcomeText?.General?.LocalizationKey)) return true;
                foreach (var f in outcome.GetType().GetFields(System.Reflection.BindingFlags.Public |
                                                              System.Reflection.BindingFlags.Instance))
                {
                    if (f.Name == "OutcomeText") continue;                 // handled above (empty-key aware)
                    var v = f.GetValue(outcome);
                    if (v == null) continue;
                    if (v is OutcomeStartMission sm) { if (sm.MissionTypeDef != null) return true; }
                    else if (v is bool b) { if (b) return true; }
                    else if (v is int n) { if (n != 0) return true; }
                    else if (v is string s) { if (!string.IsNullOrEmpty(s)) return true; }
                    else if (v is UnityEngine.Object uo) { if (uo != null) return true; }
                    else if (v is IEnumerable e) { foreach (var _ in e) return true; }
                    else return true;
                }
                return false;
            }
            catch { return true; }                  // unreadable ⇒ story choice ⇒ the event mirrors
        }

        internal static bool ChoiceHasOutcomePayload(GeoEventChoice choice) => HasOutcomePayload(choice);

        // ─── CLIENT: rebuild the REAL context and push the NATIVE window ───

        /// <summary>THE validity decision, pure so RailCheck L39 can falsify it headless: may this payload
        /// become a window on this peer? Null = yes. A ref the host SHIPPED but this peer cannot resolve is
        /// the killer — the rebuilt context would carry <c>Site</c>/<c>Vehicle</c> null while the def's text
        /// still holds <c>[HavenName]</c>/<c>[AircraftName]</c>, every replacer dereferences those unguarded
        /// (GeoscapeEventContext.cs:20-40), and the throw lands INSIDE <c>UIStateGeoscapeEvent.EnterState</c>,
        /// leaving the window half-built over the scene's baked placeholder text with live buttons. An EMPTY
        /// ref is not a failure: a site-less event legitimately has no site and the host rendered it that way
        /// too, so the mirror matches.</summary>
        internal static string ContextRefusal(string siteRef, bool siteResolved, string vehicleRef, bool vehicleResolved)
        {
            if (!string.IsNullOrEmpty(siteRef) && !siteResolved)
                return "the host raised it at site " + siteRef + ", which does not resolve on this peer — the " +
                       "rebuilt context would have Site==null and every [HavenName]/[HavenLeader] token would " +
                       "deref null inside EnterState, leaving a half-built window over placeholder text";
            if (!string.IsNullOrEmpty(vehicleRef) && !vehicleResolved)
                return "the host raised it for aircraft " + vehicleRef + ", which does not resolve on this peer — " +
                       "the rebuilt context would have Vehicle==null and an [AircraftName] token would deref null " +
                       "inside EnterState, leaving a half-built window over placeholder text";
            return null;
        }

        /// <summary>Returns true when the surface was consumed. Client-only: the host never applies its own
        /// raise (it already showed the window natively).</summary>
        public static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId == SurfaceIds.GeoEventReward)
            {
                if (engine == null || engine.IsHost) return true;
                if (!IsAuthoritativeRewardSender(engine.Session.HostPeerId, senderPeerId))
                {
                    Debug.LogError("[MP][events] rejected decision/reward payload from non-host peer " + senderPeerId);
                    return true;
                }
                try
                {
                    using (var ms = new MemoryStream(payload))
                    using (var r = new BinaryReader(ms, Encoding.UTF8))
                    {
                        int marker = r.ReadByte();
                        if (marker == DurableDecisionPayload)
                        {
                            var decision = EventSync.ReadDecision(r);
                            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("decision trailing bytes");
                            var store = DurableInboxSession.ActiveStore;
                            if (store == null || !store.InstallAuthoritativeDecision(decision))
                                throw new InvalidDataException("authoritative decision could not install");
                            UiEventMap.FireDurableChoice(GeoLevel());
                            Debug.Log("[MP][events] durable result installed for " + decision.Occurrence.TriggerId);
                            return true;
                        }
                        if (marker == DurableRewardPayload)
                        {
                            var occurrence = EventSync.ReadOccurrence(r);
                            var durableWire = MissionOutcomeMirror.DecodeRaw(r);
                            if (r.BaseStream.Position != r.BaseStream.Length)
                                throw new InvalidDataException("durable reward trailing bytes");
                            _durableRewards[occurrence] = durableWire;
                            string named = occurrence.SubjectIds.FirstOrDefault(x =>
                                x.StartsWith("event:", StringComparison.Ordinal));
                            if (named != null) RestampLiveInstance(named.Substring(6), durableWire);
                            return true;
                        }
                        r.BaseStream.Position = 0;
                        string eventId = WireString.ReadKey(r);
                        var wire = MissionOutcomeMirror.DecodeRaw(r);
                        _rewards[eventId] = wire;
                        Debug.Log("[MP][events] reward for '" + eventId + "' received (rows=" + wire.Rows.Count +
                                  ") — the outcome page will list what the host actually granted");
                        RestampLiveInstance(eventId, wire);
                    }
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer][rail] EventPopup reward inbound failed: " + ex); }
                return true;
            }
            if (surfaceId != SurfaceIds.GeoEventRaise) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                var p = Decode(payload, out uint seq);
                // A re-delivered raise is a SECOND window, not a stale value — the strictly-greater guard is
                // what makes this surface idempotent (law 7), and it is marked only after the window is up.
                if (!Seq.ShouldApply(SurfaceIds.GeoEventRaise, seq)) return true;
                if (RaiseMirrored(p, seq)) Seq.Mark(SurfaceIds.GeoEventRaise, seq);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] EventPopup inbound failed: " + ex); }
            return true;
        }

        internal static bool IsAuthoritativeRewardSender(ulong? hostPeerId, ulong senderPeerId) =>
            hostPeerId.HasValue && hostPeerId.Value == senderPeerId;

        /// <summary>
        /// Replay every raise that arrived while this peer had no geoscape, in the HOST's own arrival order.
        /// Driven from <c>SyncEngine.Tick</c> (client-only inside), because there is no single native edge
        /// that means "this peer now has a live GeoscapeView": the view is rebuilt by a whole level load, and
        /// the seam that used to DROP here was reached from the network callback, i.e. always at the one
        /// moment the answer was "not yet".
        ///
        /// This is item 2 of the 2026-08-01 report and it is the SAME defect as the late leave-battle
        /// (<c>TacticalTurnSync.ApplyLeave</c>), one layer down: fixing the leave makes every peer return
        /// together so the window normally lands on a live view — but "normally" is a race, and the raise is
        /// the ONLY carrier a window has (a mirrored <c>GeoscapeEventRecord</c> holds no site and no context,
        /// which is why 0xB6 ships the raise at all). A held raise costs nothing and closes the race for
        /// good; a dropped one is a window that exists on some screens and not others, forever.
        ///
        /// A replay that fails for a REAL reason (unknown def, unresolvable ref) drops the entry exactly as a
        /// live raise would — <see cref="RaiseMirrored"/> says why in both cases. One per frame is deliberate:
        /// each raise inserts into the game's own priority queue, and draining them one at a time keeps that
        /// queue's insert order identical to the host's.
        /// </summary>
        internal static void DrainHeldRaises(NetworkEngine engine)
        {
            if (engine == null || !engine.IsActiveSession) { _held.Clear(); _hostCurtained.Clear(); return; }
            if (engine.IsHost) { EventSync.RecoverPendingDurableChoices(); HostRebroadcastLockedDecisions(); }
            // The host no longer walks away from this drain: it has a hold of its OWN now (the curtain), and
            // exempting it here is what let its intro dialogs run ahead of every client's cutscene.
            if (engine.IsHost) { DrainHostCurtained(engine); return; }
            if (_held.Count == 0) return;
            if (!CanCarryWindow(GeoLevel())) return;

            var entry = _held[0];
            _held.RemoveAt(0);
            DurableCarrierLease heldLease;
            lock (_durableHeld) if (_durableHeld.TryGetValue(entry.Key, out heldLease))
            { _durableHeld.Remove(entry.Key); heldLease.Dispose(); }
            try
            {
                Debug.Log("[MP][events] replaying HELD raise of '" + entry.Value.EventId + "' seq=" + entry.Key +
                          " — this peer's geoscape can carry a window again (" + _held.Count + " still waiting)");
                RaiseMirrored(entry.Value, entry.Key);
            }
            catch (Exception ex)
            { Debug.LogError("[MP][events] held raise replay failed for '" + entry.Value.EventId + "': " + ex); }
        }

        /// <summary>Push every raise this peer was given and has NOT resolved back into the held list, so the
        /// existing drain replays it onto the geoscape that just came back. Called once, from
        /// <c>ReplenishSync.CarryUnreadWindowsPatch</c>, right after the native
        /// <c>GeoscapeView.RestoreState</c>: on a client that restore came out of the HOST's save, so this
        /// peer's own unread windows were never in it.
        ///
        /// "Unresolved" is the game's OWN record state, not a guess about what was on screen: still
        /// <c>Triggered</c> means nobody — not this peer, not another — has answered it, which is exactly the
        /// set that should come back. Anything else is pruned, so a window answered while we were in the
        /// battle never reappears. Returns how many were re-held.</summary>
        internal static int RequeueUnanswered()
        {
            if (_unanswered.Count == 0) return 0;
            var es = GeoLevel()?.EventSystem;
            int carried = 0;
            // Snapshot: the loop removes from _unanswered as it goes (this file has no System.Linq).
            foreach (var kv in new List<KeyValuePair<string, KeyValuePair<uint, Raise>>>(_unanswered))
            {
                _unanswered.Remove(kv.Key);
                // No record at all = the event system does not know it any more; re-raising would build a
                // window over nothing. Same drop a live raise takes (RaiseMirrored says why in both cases).
                var state = es?.GetEventRecord(kv.Key)?.State;
                if (state != GeoscapeEventRecordState.Triggered) continue;
                if (_held.Count >= MaxHeld) break;
                _held.Add(kv.Value);
                carried++;
            }
            return carried;
        }

        // GeoscapeEventSystem._events is the def list GetEventByID:282 dereferences with NO null test
        // (GeoscapeEventSystem.cs:84, filled at :135).
        private static readonly System.Reflection.FieldInfo EventsField =
            AccessTools.Field(typeof(GeoscapeEventSystem), "_events");

        /// <summary>Can this peer carry a mirrored window YET? A live <c>GeoscapeView</c> is NOT the same
        /// readiness, and that gap cost a whole window on every client of the 2026-08-04 session: the host
        /// showed four post-mission screens and the clients three. Measured, both clients identically —
        /// <c>RE26</c>'s held replay at 23:45:33.839 threw <c>ArgumentNullException</c> out of
        /// <c>GeoscapeEventSystem.GetEventByID</c> (<c>_events</c> still null: the view exists before the
        /// event system is initialised), <c>DrainHeldRaises</c> had already popped the entry, and the catch
        /// logged it and moved on — the window was gone for good. <c>PROG_AN2_WIN</c>'s replay 340 ms later on
        /// the same peer succeeded, which is the whole proof that this is a TIMING gate and not a verdict.
        ///
        /// So the readiness test is the thing the raise actually needs, asked of the event system itself.
        /// Both callers use it, which is what makes this a fix and not a patch on one path: a LIVE raise
        /// arriving in the same window is HELD by <see cref="RaiseMirrored"/> instead of thrown away, and the
        /// drain does not pop an entry it cannot place (popping and re-appending would reorder the queue,
        /// and the host's arrival order is the only ordering these windows have).</summary>
        private static bool CanCarryWindow(GeoLevelController geo)
        {
            // THE RESUPPLY VERDICT IS STILL OUT — THE THIRD TERM (2026-08-09). Same shape as the cutscene
            // above and the same remedy: HOLD. A returning client reaches UIStateInitial before the host's
            // post-mission writes land, so its resupply screen can only be queued a beat later — and the
            // held-window drain starts replaying about a second BEFORE that beat. Rank 20 cannot rescue the
            // order once a window is CURRENT: ProcessQueriedStateSwitch:58-63 dequeues only while
            // _currentStateSwitchRequest is null, so a mirrored event that got in first owns the screen and
            // the resupply screen queues behind it. Keeping the order therefore has to happen before the
            // queue, which is here. Not a quorum — see ReplenishSync.ResupplyVerdictPending for why (a
            // monotone local countdown with a 180-frame (3 s) ceiling that reads no peer and waits on no human).
            if (ReplenishSync.ResupplyVerdictPending) return false;
            var view = geo?.View;
            if (view == null || !(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery)) return false;
            var current = view.CurrentViewState;
            if (OneShotHoldsWindow(current == null ? null : current.GetType())) return false;
            var es = geo.EventSystem;
            return es != null && EventsField?.GetValue(es) != null;
        }

        /// <summary>PURE (RailCheck L195). IS A FULL-SCREEN ONE-SHOT PLAYING RIGHT NOW?
        ///
        /// THE SECOND HALF OF THE 2026-08-08 REPORT. The gate above asked only whether the EVENT SYSTEM had
        /// finished loading; it knew nothing about what is on screen, so the drain released all three intro
        /// dialogs 0-130 ms after the intro cutscene started. That alone is only a cosmetic pile-up — what
        /// made it unrecoverable is that the cutscene's own <c>SetInputState("Cutscene")</c>
        /// (<c>UIStateGeoCutscene.EnterState</c>) is STORED AND DISCARDED while a stale loading override is
        /// still held (<c>RevealInputLock</c>:19-23, <c>InputController.SetInputSets</c>:520-527), so Escape
        /// does nothing and the peer sits through the entire video with three modals stacked behind it.
        ///
        /// ONE TERM, and it is a HOLD rather than a drop: the drain retries every tick, so a window that
        /// arrives during a cinematic is served the moment the cinematic ends — the shape L189 already
        /// blesses ("held, not lost"). Keyed on <c>UIStateGeoCutscene</c> and assignability, which is the same
        /// state name <see cref="WindowOrder"/> and <c>GeoWindowCoverage</c> already declare on, so a mod
        /// subclassing it is covered without naming the mod. The input latch itself is NOT re-fixed here —
        /// <c>RevealInputLock</c> owns it, and L196 asserts the override is gone before any queued state
        /// enters.</summary>
        internal static bool OneShotHoldsWindow(Type currentViewState)
            => currentViewState != null && typeof(UIStateGeoCutscene).IsAssignableFrom(currentViewState);

        /// <summary>Rebuild the host's window here: resolve the shipped refs, build the REAL context, apply
        /// the wire texts to a PRIVATE copy of the event data, and push the NATIVE view state through the
        /// game's own switch query — the same call the host's <c>OnGeoscapeEventRaised</c> makes.
        /// Returns false when nothing was raised (and always says why).</summary>
        private static bool RaiseMirrored(Raise p, uint seq)
        {
            var geo = GeoLevel();
            var view = geo?.View;
            var q = view == null ? null : SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            var durableStore = DurableInboxSession.ActiveStore;
            var durableOccurrence = new OccurrenceId("EventPopup", "raise:" + seq,
                new[] { "event:" + p.EventId,
                        string.IsNullOrEmpty(p.SiteRef) ? "site:none" : p.SiteRef,
                        string.IsNullOrEmpty(p.VehicleRef) ? "vehicle:none" : p.VehicleRef });
            if (q == null || !CanCarryWindow(geo))
            {
                // HELD, not dropped (2026-08-01). "This peer has no geoscape yet" is a TIMING fact, not a
                // verdict about the window: measured live, a peer still on its battle summary when the host
                // raised the two post-mission events got NEITHER of them, and once it returned there was
                // nothing left to show — the seq had been consumed and the raise is the only carrier (the
                // mirrored record cannot rebuild a window, which is why 0xB6 exists at all). The peer is
                // seconds away from a geoscape, so the raise waits for it; ClientTick replays it in arrival
                // order the moment the view is live. Still bounded and still loud. "Ready" is
                // CanCarryWindow's question, not "is there a view" — see there for the window this cost.
                if (_held.Count >= MaxHeld)
                {
                    Debug.LogError("[MP][events] raise of '" + p.EventId + "' DROPPED — this peer's geoscape " +
                                   "still cannot carry a window and " + MaxHeld + " raises are already waiting; " +
                                   "it is not loading a geoscape, so the oldest are no longer worth holding");
                    return true; // consumed: the seq must still advance or every later raise is refused
                }
                _held.Add(new KeyValuePair<uint, Raise>(seq, p));
                TrackHeldCarrier(durableStore, durableOccurrence, seq);
                Debug.LogWarning("[MP][events] raise of '" + p.EventId + "' HELD — this peer has no geoscape " +
                                 "ready to carry a window yet (no view, or its event system is still loading); " +
                                 "it will be replayed when one is (" + _held.Count + " waiting)");
                return true;
            }

            // THE ARRIVING RAISE *IS* THE HOST'S AUTHORITY — THERE IS NO SECOND MESSAGE TO WAIT FOR.
            // The durable ledger is per-peer SESSION state: DurableInboxSession.OpenSessionStore:30 mints it
            // EMPTY on every peer and NO rail surface carries it (there is no SurfaceIds entry for the inbox
            // at all, and DurableInboxEngine contains no networking), so only the peer that enrols an
            // occurrence ever has one. The host enrols at :372, on its own broadcast path; this method only
            // ever runs on a CLIENT. Waiting here for the host's entry to "arrive" therefore waited for a
            // message that does not exist, and EVERY mirrored geoscape event window was held forever — live
            // 2026-08-10: UIStateGeoscapeEvent entered 4x on the host and 0x on both clients, with the wait
            // line repeating every frame for the whole session. Enrol it instead, from the raise, using the
            // SAME OccurrenceId the host builds from the same seq and subjects, so everything downstream
            // (WindowOrder.BindDurable at :741, RestoreSuspended, the carrier leases) gets the entry it
            // expects. Idempotent by construction: EnqueuePriorityOccurrence:243 returns false when the
            // occurrence is already present, so a re-delivered raise adds nothing.
            if (durableStore == null)
            {
                if (_held.All(x => x.Key != seq)) _held.Add(new KeyValuePair<uint, Raise>(seq, p));
                Debug.Log("[MP][events] raise of '" + p.EventId + "' HELD — this peer's durable inbox store " +
                          "is not open yet (it is minted when the co-op geoscape is built); the drain " +
                          "replays it the moment it is");
                return true;
            }
            DurableWindowRegistry.EnqueuePriorityOccurrence(durableStore, durableOccurrence);

            var es = geo.EventSystem;
            var data = es?.GetEventByID(p.EventId, canFail: true)?.GeoscapeEventData;
            if (data == null)
            {
                Debug.LogError("[MP][events] raise of '" + p.EventId + "' DROPPED — no such event def on this peer " +
                               "(mod parity break: law 10 should have blocked the join)");
                return false;
            }

            var site = IdentityResolver.Resolve(geo, p.SiteRef, null) as GeoSite;
            var vehicle = IdentityResolver.Resolve(geo, p.VehicleRef, null) as GeoVehicle;
            string refusal = ContextRefusal(p.SiteRef, site != null, p.VehicleRef, vehicle != null);
            if (refusal != null)
            {
                Debug.LogError("[MP][events] raise of '" + p.EventId + "' REFUSED — " + refusal);
                return false;
            }

            var shown = WithWireTexts(data, p.Title, p.Narrative);
            var context = vehicle != null
                ? new GeoscapeEventContext(site, geo.ViewerFaction, vehicle)
                : new GeoscapeEventContext(site, geo.ViewerFaction);
            // The record rides the 0xAC value rail and normally lands AFTER this raise (separate surfaces;
            // the diff flushes on its own cycle). A dialog with a null Record is an NRE waiting in
            // UIStateGeoscapeEvent.ExitState:61 (it reads Event.Record.State on every close), so an OPEN
            // placeholder stands in until the real one arrives — RepaintDialog swaps the live record in on the
            // first ES delta. It is presentation only: it is never inserted into GeoscapeEventSystem._records,
            // so the client mints no authoritative state (law 3).
            var rec = es.GetEventRecord(p.EventId) ?? new GeoscapeEventRecord(p.EventId, geo.Timing.Now);
            var geoEvent = new GeoscapeEvent(shown, context) { Record = rec };
            int raiseTrigger = RaiseTriggerCount(rec.State, rec.TriggerCount);
            Bound.Add(geoEvent, new RaiseBinding { Seq = seq, TriggerCount = raiseTrigger, Occurrence = durableOccurrence });

            // The host's OWN priority, so this peer's queue orders the window exactly as the host's did.
            var request = new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent), p.Priority)
            // PauseGame = the GAME'S OWN flag, and it must be true on a mirror too. "Pause mirrors from the
            // host via the TimeAnchor" (what this said until 2026-08-04) is false whenever the host is
            // ALREADY paused: its re-write is swallowed by the change-gated Timing.Paused setter, no delta
            // is emitted, and this peer reads the popup while its aircraft keeps flying. True here is what
            // makes ProcessQueriedStateSwitch:67-70 call RequestGamePause:1269 on THIS peer — a ONE-SHOT
            // pause issued by the game itself, captured by TimeSync at SetGamePauseState. Nothing holds it:
            // any peer may resume at any time, first-to-act-wins, and no peer's window vetoes that.
            { PauseGame = true };
            q.QueryStateSwitch(request);
            WindowOrder.BindDurable(request, durableOccurrence);
            Debug.Log("[MP][events] raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority + " site=" +
                      (site == null ? "none" : site.SiteId.ToString()) +
                      " vehicle=" + (vehicle == null ? "none" : vehicle.VehicleID.ToString()) +
                      " record=" + rec.State + "#" + rec.TriggerCount +
                      (es.GetEventRecord(p.EventId) == null ? " (placeholder)" : "") +
                      " answeredFrom=#" + raiseTrigger);
            // The window now exists on this peer; keep its carrier so a battle cannot delete it (see
            // RequeueUnanswered). Overwrites any earlier raise of the same id — the newest is the live one.
            _unanswered[p.EventId] = new KeyValuePair<uint, Raise>(seq, p);
            // The squad screen must not depend on this peer's own dialog teardown running to completion —
            // see MissionArrivalNav. Armed from the RAISE, fired from the mission's ARRIVAL.
            MissionArrivalNav.Watch(p.EventId, data, p.SiteRef, p.VehicleRef);
            return true;
        }

        private static void TrackHeldCarrier(DurableInboxStore store, OccurrenceId occurrence, uint seq)
        {
            if (store == null || !store.Ledger.Contains(occurrence)) return;
            lock (_durableHeld)
            {
                if (_durableHeld.ContainsKey(seq)) return;
                var lease = BindDeferredCarrier(store, occurrence, _ =>
                {
                    lock (_durableHeld) { _held.RemoveAll(x => x.Key == seq); _durableHeld.Remove(seq); }
                });
                if (!lease.IsFinished) _durableHeld.Add(seq, lease);
            }
        }

        /// <summary>The host-resolved title/narrative applied to a PRIVATE COPY of the event data — never to
        /// the def. A def is SHARED state on this peer (<see cref="DefOwnership"/> exists to stop the rail
        /// from descending into exactly this), and a direct write there is session-permanent with no undo.
        /// It also cannot be made safe by "stamp only when the local def resolves EMPTY":
        /// <c>LocalizedTextBind(text, doNotLocalize:true).Localize()</c> returns the LITERAL
        /// (LocalizedTextBind.cs:37-41), so one stamp leaves the def permanently non-empty — a def the host
        /// REWRITES per roll (TFTV VoidOmen_{0..19}, TFTVODIandVoidOmenRoll.cs:638-639) would keep the first
        /// roll's text forever, while a def with a valid static key the host rewrote at runtime
        /// (TFTVBaseDefenseGeoscape.cs:1227 then :1250) would never take the wire text at all. With a private
        /// copy the rule is simply: whatever the host resolved wins.
        ///
        /// The copy is SHALLOW and deliberately so — <c>Choices</c> stays the DEF'S OWN list instance, because
        /// choice identity is load-bearing: the buttons hold those <c>GeoEventChoice</c> references
        /// (SiteBaseChoiceButton.cs:43-47, our <see cref="ShowingRealChoices"/>) and <c>CompleteEvent</c>
        /// validates with <c>EventData.Choices.Contains(choice)</c> (GeoscapeEvent.cs:92). Only
        /// <c>Description</c> is re-listed, so the replaced LAST variation — the entry the host resolved
        /// (UIModuleSiteEncounters:335) — is ours and the def's own stays untouched. Nothing native
        /// identity-compares <c>GeoscapeEventData</c> or <c>EventTextVariation</c>: the marketplace test is on
        /// <c>EventID</c> (GeoscapeEventSystem.cs:409), the art lookup on the <c>Leader</c>/<c>Flavour</c>
        /// STRINGS (SiteEncountersArtCollectionDef.cs:74-96), the narration on <c>Voiceover</c>
        /// (NarrationSound.cs:100-107), and <c>EventData</c> is not serialized at all (GeoscapeEvent.cs:17-30
        /// marks EventID/Context/_record only — a restored window re-fetches the def by id), so no copy can
        /// leak into a save. Returns <paramref name="data"/> UNCHANGED when there is nothing to apply.</summary>
        // ponytail: the copy carries the HOST's locale for these two fields, so a client running a different
        // game language reads them in the host's language. Accepted v1 behaviour — upgrade path is to ship the
        // loc KEY and let each peer localize it, keeping the resolved text only as the runtime-mutation
        // fallback. Not built until someone actually plays cross-locale.
        internal static GeoscapeEventData WithWireTexts(GeoscapeEventData data, string title, string narrative)
        {
            if (data == null) return null;
            var desc = data.Description;
            bool wantTitle = !string.IsNullOrEmpty(title);
            bool wantBody = !string.IsNullOrEmpty(narrative) && desc != null && desc.Count > 0;
            if (!wantTitle && !wantBody) return data;
            try
            {
                var copy = ShallowCopy(data);
                if (wantTitle) copy.Title = new LocalizedTextBind(title, doNotLocalize: true);
                if (wantBody)
                {
                    var last = ShallowCopy(desc[desc.Count - 1]);
                    last.General = new LocalizedTextBind(narrative, doNotLocalize: true);
                    // Alt is dropped on OUR copy: GetText prefers Alt for a female haven leader
                    // (EventTextVariation.cs:21-27) and the host already resolved THAT pick into the one
                    // string on the wire, so leaving Alt would silently override it on half the havens.
                    last.Alt = null;
                    copy.Description = new List<EventTextVariation>(desc);
                    copy.Description[desc.Count - 1] = last;
                }
                return copy;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][events] wire-text copy failed (" + ex.Message + ") — the window renders " +
                                 "this peer's own def text, and the shared def stays untouched");
                return data;
            }
        }

        /// <summary>Field-for-field copy, subclass-preserving (a modded subtype survives, and no field
        /// silently stops being copied when the game or a mod adds one — including the ones this file must
        /// not name, e.g. EventTextVariation.Voiceover, whose AK.Wwise assembly the mod does not reference).
        /// Every reference stays SHARED: that is the point — the identity-bearing members must remain the
        /// def's own.</summary>
        private static T ShallowCopy<T>(T src) where T : class
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly;
            var copy = (T)Activator.CreateInstance(src.GetType());
            for (var t = src.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(F))
                    f.SetValue(copy, f.GetValue(src));
            return copy;
        }

        // ─── Presentation: repaint / freeze / close of an OPEN dialog ──────

        /// <summary>Law 11 repaint of the OPEN event dialog — the <c>UiNativeRepaint.Table</c> entry for
        /// <c>UIStateGeoscapeEvent</c>, READ-direction. Three jobs:
        ///   • refresh the dialog's stale <c>Record</c> ref: a blob apply rebuilds the record INSTANCE, and
        ///     <c>UIStateGeoscapeEvent.ExitState</c>:61-65 reads <c>Event.Record.State</c> to decide whether to
        ///     complete the event LOCALLY, so a stale <c>Triggered</c> ref there is a client-side resolution of
        ///     a host-authoritative choice. This is also what swaps out the placeholder record a mirrored raise
        ///     opened with (<see cref="RaiseMirrored"/>);
        ///   • CLOSE a picker somebody else already answered — THE dismiss. The record delta IS the dismiss
        ///     signal, so this family ships no dismiss message of its own: once the record is resolved the
        ///     picker's buttons are all dead (the losers greyed by <see cref="EventChoiceFreeze"/>, the winner
        ///     click-dead by IsSelected), and leaving that on screen strands the player behind a window whose
        ///     only exit is Esc. The resolution must belong to the raise THIS window was opened by
        ///     (<see cref="IsResolvedForRaise"/>), or a re-triggered event's PREVIOUS answer — which is what
        ///     <c>GetEventRecord</c> still returns for the ~0.25-0.75 s until the re-trigger delta lands —
        ///     closes the fresh picker under the player. Guarded by <see cref="ShowingRealChoices"/> so it
        ///     never closes an OUTCOME page:
        ///     the winner's own result page is built over a SYNTHETIC event (EventID "") whose OK button is the
        ///     way out. The close is safe because the Record ref was refreshed one line above — ExitState's
        ///     force-complete guard reads the RESOLVED state and stays silent;
        ///   • otherwise re-drive the module's own <c>SetChoices</c>, which re-reads each button's
        ///     affordability from the model (<c>SiteBaseChoicesController.SetChoice</c>:60 →
        ///     <c>choice.PassRequirements(faction, context)</c> = the wallet).
        /// Deliberately NOT <c>ShowEncounter</c> (the screen's full re-init): it re-posts
        /// <c>OpenEncounterSoundEvent</c> (UIModuleSiteEncounters.cs:195-198) and resets
        /// <c>_encounterTextIndex</c> to 0 (:220), so on a peer where deltas land continuously it would
        /// machine-gun the encounter sound and make a multi-page description impossible to page through.
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
            if (ctrl == null || !ShowingRealChoices(ctrl, ev)) return true;
            if (InSession && IsFrozen(ev, out var winner))
            {
                if (!DurableChoiceLocked(ev) && DismissOnResolution(winner != null, winner?.Outcome != null))
                {
                    Debug.Log("[MP][events] closing the open picker for '" + ev.EventID + "' — the record is resolved " +
                              "with no choice this peer can click through (winner=" +
                              (winner == null ? "none" : "no Outcome") + "), so every button on it is dead");
                    view.FinishQueriedState();
                    return true;
                }
                // THE ANSWERING PEER: this resolution is the answer to THIS peer's own click, which was
                // blocked and relayed as a 0xB4 intent. It already made its choice, so the replay paint below
                // would ask it to confirm its own click — go straight to the result page instead. Charge-free
                // and resolution-free by construction (ReplayResolution): the winner's cost came out of the
                // HOST's wallet in EventSync.HandleAnswer and reaches this peer as an ordinary wallet delta.
                if (ConsumeOwnAnswer(ev) && ReplayResolution(module, ev, winner))
                {
                    Debug.Log("[MP][events] '" + ev.EventID + "' resolved by THIS peer's own click (choice " +
                              (ev.Record == null ? -1 : ev.Record.SelectedChoice) + ") — showing its result page " +
                              "directly, with no second click and no local charge");
                    return true;
                }
            }
            // Otherwise REPAINT, decided or not: SetChoices re-reads affordability and its EventChoiceFreeze
            // postfix repaints the replay (winner selected + still clickable, losers greyed). The player's
            // click on the winner then walks to the native result page instead of the window vanishing.
            ctrl.SetChoices(module.Context.ViewerFaction, module.ChoiceButtonsContainer, module.ChoiceButtonPrefab, ev);
            return true;
        }

        internal static bool RestoreSuspended(GeoscapeViewStateSwitchRequest request, GeoscapeView view,
            InboxWindowCheckpoint checkpoint)
        {
            var state = request?.State as GeoscapeViewState;
            var ui = state as UIStateGeoscapeEvent;
            var module = view?.GeoscapeModules?.SiteEncountersModule;
            if (checkpoint == null || ui?.Event == null || module == null) return false;
            var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            if (!IsExpectedCurrent(query, request)) return false;
            int page; if (!int.TryParse(checkpoint.ReadCheckpoint, out page) || page < 0) return false;
            var pageField = AccessTools.Field(module.GetType(), "_encounterTextIndex");
            var pagingField = AccessTools.Field(module.GetType(), "_pagingEvent");
            if (pageField == null || pagingField == null) return false;
            pageField.SetValue(module, page);
            if (checkpoint.ContentPhase == "choices")
            {
                AccessTools.Method(module.GetType(), "SetEncounter")?.Invoke(module,
                    new object[] { ui.Event, false, null });
                return RepaintDialog(state, view);
            }
            if (checkpoint.ContentPhase == "paging")
            {
                AccessTools.Method(module.GetType(), "SetEncounter")?.Invoke(module,
                    new object[] { ui.Event, true, null });
                return true;
            }
            int selected;
            if (checkpoint.ContentPhase != "result" || !int.TryParse(checkpoint.Selection, out selected) ||
                selected < 0 || selected >= ui.Event.EventData.Choices.Count) return false;
            AccessTools.Method(module.GetType(), "SetClosingEncounter")?.Invoke(module,
                new object[] { ui.Event, ui.Event.EventData.Choices[selected], false });
            return true;
        }

        internal static bool IsExpectedCurrent(GeoscapeViewSwitchQuery query,
            GeoscapeViewStateSwitchRequest request) => request != null &&
            ReferenceEquals(WindowOrder.CurrentRequest(query), request);

        internal static InboxWindowCheckpoint CaptureCheckpoint(GeoscapeViewState state, GeoscapeView view)
        {
            var ui = state as UIStateGeoscapeEvent;
            var module = view?.GeoscapeModules?.SiteEncountersModule;
            if (ui?.Event == null || module == null) return null;
            var pageField = AccessTools.Field(module.GetType(), "_encounterTextIndex");
            var pagingField = AccessTools.Field(module.GetType(), "_pagingEvent");
            if (pageField == null || pagingField == null) return null;
            int page = (int)pageField.GetValue(module);
            bool paging = (bool)pagingField.GetValue(module);
            int selected = ui.Event.Record == null ? -1 : ui.Event.Record.SelectedChoice;
            bool showingChoices = module.ChoicesButtonController != null &&
                ShowingRealChoices(module.ChoicesButtonController, ui.Event);
            var query = SwitchQueryField?.GetValue(view) as GeoscapeViewSwitchQuery;
            int nativePriority = WindowOrder.CurrentRequest(query)?.Priority ?? 0;
            return NativeCheckpoint(page, paging, showingChoices, selected, ui.Event.EventID,
                module.EncounterTitle?.text, module.EncounterDescriptionText?.text, nativePriority);
        }

        internal static InboxWindowCheckpoint NativeCheckpoint(int page, bool paging, int selected)
            => NativeCheckpoint(page, paging, !paging && selected < 0, selected);

        internal static InboxWindowCheckpoint NativeCheckpoint(int page, bool paging, bool showingChoices, int selected)
            => NativeCheckpoint(page, paging, showingChoices, selected, "", "", "", 0);

        internal static InboxWindowCheckpoint NativeCheckpoint(int page, bool paging, bool showingChoices, int selected,
            string eventId, string title, string narrative, int nativePriority)
        {
            if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
            string phase = paging ? "paging" : showingChoices ? "choices" : "result";
            return new InboxWindowCheckpoint(phase, selected.ToString(), page.ToString(), eventId, title,
                narrative, nativePriority);
        }

        internal static GeoscapeViewStateSwitchRequest ReconstructCarrier(OccurrenceId occurrence,
            InboxWindowCheckpoint checkpoint)
        {
            if (occurrence.EventId != "EventPopup" || checkpoint == null) return null;
            var geo = GeoLevel(); if (geo?.EventSystem == null) return null;
            string eventId = !string.IsNullOrEmpty(checkpoint.EventId) ? checkpoint.EventId :
                occurrence.SubjectIds.FirstOrDefault(x => x.StartsWith("event:", StringComparison.Ordinal))?.Substring(6);
            if (string.IsNullOrEmpty(eventId)) return null;
            var data = geo.EventSystem.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;
            var record = geo.EventSystem.GetEventRecord(eventId);
            if (data == null || record == null) return null;
            string siteRef = occurrence.SubjectIds.FirstOrDefault(x => x.StartsWith("S#", StringComparison.Ordinal));
            string vehicleRef = occurrence.SubjectIds.FirstOrDefault(x => x.StartsWith("V#", StringComparison.Ordinal));
            var site = IdentityResolver.Resolve(geo, siteRef, null) as GeoSite;
            var vehicle = IdentityResolver.Resolve(geo, vehicleRef, null) as GeoVehicle;
            var context = vehicle == null ? new GeoscapeEventContext(site, geo.ViewerFaction) :
                new GeoscapeEventContext(site, geo.ViewerFaction, vehicle);
            var shown = WithWireTexts(data, checkpoint.Title, checkpoint.Narrative);
            var state = new UIStateGeoscapeEvent(new GeoscapeEvent(shown, context) { Record = record });
            var request = new GeoscapeViewStateSwitchRequest(state, checkpoint.NativePriority) { PauseGame = true };
            WindowOrder.BindDurable(request, occurrence); return request;
        }

        /// <summary>Is the dialog showing the REAL event's choice buttons, or one of the module's own
        /// synthetic pages? A paging page (<c>SetPagingEncounter</c>:309-321) and the outcome/closing page
        /// (<c>SetClosingEncounter</c>:326-355) are each built inline over a throwaway
        /// <c>GeoscapeEvent</c> with <c>EventID == ""</c> whose text is already a frozen string and whose
        /// single OK button must stay live — re-running <c>SetChoices</c> with the REAL event there would
        /// replace that button with the picker, and CLOSING there would eat the player's result page. The
        /// module keeps no flag for which of the three it shows, so the question is asked of the BUTTONS:
        /// <c>SiteBaseChoiceButton.Choice</c> holds the very <c>GeoEventChoice</c> instance it was set from
        /// (SiteBaseChoiceButton.cs:43-47) and the def's list is the only source of those instances, so
        /// reference identity answers it.</summary>
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
        /// This is the ONE-FRAME belt in front of <see cref="RepaintDialog"/>'s close — a picker can be built
        /// over an already-resolved record (the answer landed while the raise was in flight), and the
        /// repaint that closes it is a frame away. Native widget API only:
        /// <c>PhoenixGeneralButton.SetInteractable(false)</c> clears <c>BaseButton.interactable</c> +
        /// <c>IsEnabled</c>, which <c>OnPointerClick</c>:327 requires, and <c>ResetButtonAnimations</c>:180-183
        /// paints the greyed state. The winner keeps <c>interactable</c> and gets <c>IsSelected</c>: with the
        /// widget's OWN default <c>IsNonInteractableWhenSelected</c> (true, PhoenixGeneralButton.cs:37) that is
        /// click-dead by the same :327 test while painting the selected look — "this is what was chosen".
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
                var paint = PaintChoice(frozen, winner != null && ReferenceEquals(btn.Choice, winner));
                btn.Button.IsSelected = paint.Selected;
                btn.Button.IsNonInteractableWhenSelected = paint.DeadWhenSelected;
                // DECIDED: we own interactability outright (winner live, losers dead). OPEN: the native
                // SetChoice we are a postfix on has just re-read affordability from the wallet
                // (SiteBaseChoicesController.cs:60 → choice.PassRequirements) and that answer must stand.
                if (frozen) btn.Button.SetInteractable(paint.Interactable);
                else btn.Button.ResetButtonAnimations();
            }
        }

        /// <summary>PURE (RailCheck L45): what ONE picker button must become, given whether the answer is
        /// decided and whether this button holds the winning choice. Three native widget writes:
        /// <c>IsSelected</c> (the "this is what was chosen" look), <c>SetInteractable</c>
        /// (PhoenixGeneralButton.cs:283 — clears <c>BaseButton.interactable</c> + <c>IsEnabled</c>, which
        /// <c>OnPointerClick</c>:327 requires) and <c>IsNonInteractableWhenSelected</c> (:37, default true).
        ///
        /// That last field is THE replay mechanism, and dropping it is what turned a cosmetic freeze into a
        /// hang: :327 early-returns on <c>(IsSelected &amp;&amp; IsNonInteractableWhenSelected)</c>, so a
        /// winner marked selected under the widget's own default is CLICK-DEAD and the window's only exit is
        /// Esc. Cleared, the highlighted winner stays clickable and the player's click walks the native path
        /// to the result page — <c>OnChoiceSelected</c> → <c>SelectChoice</c>:598 short-circuits on
        /// <c>IsCompleted</c> → <c>SetClosingEncounter(winner)</c> — which is what the other peers should see
        /// instead of the window vanishing.
        ///
        /// Every field is written on EVERY active button, never only the winner: the buttons are POOLED and
        /// reused by the next window (<c>AddChoicesButtons</c>:67-75 builds the list once), so a flag left
        /// behind deadens that slot in the next picker — including the OK button of a closing page.</summary>
        internal struct ChoicePaint
        {
            public bool Selected;            // PhoenixGeneralButton.IsSelected
            public bool Interactable;        // PhoenixGeneralButton.SetInteractable(bool)
            public bool DeadWhenSelected;    // PhoenixGeneralButton.IsNonInteractableWhenSelected
        }

        internal static ChoicePaint PaintChoice(bool decided, bool isWinner) => new ChoicePaint
        {
            Selected = decided && isWinner,
            Interactable = !decided || isWinner,
            DeadWhenSelected = !(decided && isWinner),
        };

        /// <summary>PURE (RailCheck L45): once a picker is decided, may the repaint CLOSE it? Only when it
        /// has no clickable way out. A decided picker WITH a winning choice that carries an Outcome is a
        /// REPLAY — winner highlighted and live, losers greyed, one click to the result page — and hard-
        /// dismissing that is the client-side face of the bug (the window vanishes ~0.2 s after opening with
        /// no user input, and the peers end up on different events). With no winner (native's -1 "no choice",
        /// UIModuleSiteEncounters.cs:562-566) or a winner with no <c>Outcome</c>, there is no page to show —
        /// <c>SetClosingEncounter</c>:333 dereferences <c>closingChoice.Outcome.OutcomeText</c> unguarded —
        /// and leaving the window up would strand the player behind dead buttons with Esc as the only
        /// exit.</summary>
        internal static bool DismissOnResolution(bool hasWinner, bool winnerHasOutcome) =>
            !hasWinner || !winnerHasOutcome;

        private static bool DurableChoiceLocked(GeoscapeEvent ev)
        {
            OccurrenceId occurrence; var store = DurableInboxSession.ActiveStore;
            return store != null && TryGetDurableOccurrence(ev, out occurrence) && store.Canonical.Decisions.Any(x =>
                x.Occurrence.Equals(occurrence) && x.Phase == SharedChoicePhase.ChoiceLocked);
        }

        /// <summary>Is this dialog's answer already decided, and by which choice? The ledger is the
        /// REPLICATED record, never the instance: <c>GeoscapeEvent.IsCompleted</c> is per-INSTANCE
        /// (GeoscapeEvent.cs:36) and a mirrored instance over a resolved record reports false. An empty
        /// <c>EventID</c> is one of the module's own synthetic pages — nothing to freeze. A resolved record
        /// with <c>SelectedChoice</c> outside the def's range (native's -1 "no choice",
        /// UIModuleSiteEncounters.cs:562-566) freezes every button and leaves no winner. The record must
        /// belong to the raise that opened THIS window (<see cref="IsResolvedForRaise"/>) — a re-triggered
        /// event's previous answer is still sitting in the record when the fresh window opens.</summary>
        internal static bool IsFrozen(GeoscapeEvent ev, out GeoEventChoice winner)
        {
            winner = null;
            if (string.IsNullOrEmpty(ev?.EventID)) return false;
            var rec = LiveRecord(ev.EventID, ev.Record);
            if (rec == null || !IsResolvedForRaise(rec.State, rec.TriggerCount, RaiseTriggerOf(ev),
                                                   PreAnsweredAtTrigger(ev.EventData))) return false;
            var choices = ev.EventData?.Choices;
            int i = rec.SelectedChoice;
            if (choices != null && i >= 0 && i < choices.Count) winner = choices[i];
            // THE PER-PEER MISSION-START FAMILY IS NEVER FROZEN (owner 2026-08-10, "без блокировки выбора").
            // This is the one gate that has to know: it feeds the freeze PAINT (FreezeChoiceButtons:1165 greys
            // every losing button), the replay (ResolutionIsNotOurs) and RepaintDialog's auto-close. Exempting
            // it here leaves every peer's copy fully clickable no matter who answered first.
            if (IsMissionStartConfirmationEvent(ev.EventData))
            {
                BriefProbe(ev.EventID, "freeze-skipped", "per-peer mission-start family — another peer's answer " +
                           "(choice " + i + ") locks NOTHING on this peer; every button stays live");
                winner = null;
                return false;
            }
            return true;
        }

        // ─── Which RAISE is the open window showing? (the stale-record race) ─

        /// <summary>The raise a mirrored window was opened by, keyed by the very <c>GeoscapeEvent</c> instance
        /// that went into the view state. Weak, so a closed window's entry dies with it — there is no close
        /// seam to clean up on, and the queue can hold several windows at once. Windows this peer opened
        /// NATIVELY (the host's own, and a window restored from a save) are simply not in here, and every
        /// query below then answers exactly as it did before the binding existed.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeoscapeEvent, RaiseBinding>
            Bound = new System.Runtime.CompilerServices.ConditionalWeakTable<GeoscapeEvent, RaiseBinding>();

        private sealed class RaiseBinding { public uint Seq; public int TriggerCount; public OccurrenceId Occurrence; }

        private static int RaiseTriggerOf(GeoscapeEvent ev) =>
            ev != null && Bound.TryGetValue(ev, out var b) ? b.TriggerCount : 0;

        /// <summary>PURE (RailCheck L39): which <c>TriggerCount</c> the window being raised NOW belongs to.
        /// The 0xB6 raise and the record's own 0xAC delta are separate surfaces that flush on their own
        /// cycles, so for a RE-triggered event the raise routinely arrives while <c>GetEventRecord</c> still
        /// returns the PREVIOUS, already-Completed record (measured 0.25-0.75 s). That record's answer is one
        /// trigger OLD — this window is the next one, so it is answered from <c>TriggerCount + 1</c>. A record
        /// that IS open (Triggered) is already this raise's, count as-is.</summary>
        internal static int RaiseTriggerCount(GeoscapeEventRecordState state, int triggerCount) =>
            state == GeoscapeEventRecordState.Triggered ? triggerCount : triggerCount + 1;

        /// <summary>PURE (RailCheck L39/L44): does this record resolution answer the raise the open window
        /// shows — and is it a resolution somebody MADE, rather than one the window was born with?
        ///
        /// <paramref name="raiseTriggerCount"/> is 0 for a window with no binding (native/host/restored), and
        /// every real count is ≥1, so those keep the stock "resolved means resolved" answer. A resolution
        /// OLDER than the raise must be ignored, not applied: acting on it greys a picker the player is
        /// legitimately looking at and, one ES delta later, CLOSES it
        /// (<see cref="RepaintDialog"/> → <c>FinishQueriedState</c>).
        ///
        /// <paramref name="preAnsweredAtTrigger"/> is the OTHER half, and it is what makes this a TRANSITION
        /// test rather than a state test. The game answers a single-choice event ITSELF, at trigger time,
        /// before the window is even queued (<c>GeoscapeEventSystem.OnEventTriggered</c>:651-655 —
        /// <c>if (@event.HasSingleChoice &amp;&amp; !IsEventTheMarketplace) CompleteEvent(Choices.FirstOrDefault())</c>,
        /// then <c>GeoscapeEventRaised?.Invoke</c>). Such a record is <c>Completed</c> on EVERY peer the
        /// instant the window opens, with no user input anywhere — so reading it as "somebody else won the
        /// race" freezes and then dismisses a window nobody answered. That was one root cause with two
        /// faces: the HOST's own native picker went click-dead (the winner's <c>IsSelected</c> +
        /// <c>PhoenixGeneralButton.IsNonInteractableWhenSelected</c>:37 kill <c>OnPointerClick</c>:327, and
        /// the host applies no deltas so nothing ever unsticks it), while every CLIENT had the same window
        /// auto-dismissed ~0.2 s after opening — leaving the peers on DIFFERENT events.</summary>
        internal static bool IsResolvedForRaise(GeoscapeEventRecordState state, int triggerCount,
                                                int raiseTriggerCount, bool preAnsweredAtTrigger) =>
            !preAnsweredAtTrigger && state != GeoscapeEventRecordState.Triggered && triggerCount >= raiseTriggerCount;

        /// <summary>PURE (RailCheck L44): the game's OWN trigger-time auto-answer condition, mirrored
        /// exactly — <c>GeoscapeEventSystem.cs:651</c> over <c>GeoscapeEventData.HasSingleChoice</c>
        /// (<c>Choices.Count &lt;= 1</c>, GeoscapeEventData.cs:65). Def-derived, therefore identical on
        /// every peer by law 10 (mod parity blocks the join otherwise) — which is why it is asked of the
        /// DEF and not stored on the raise binding: a window RESTORED from a save never passes a raise seam
        /// at all, and would come back frozen again.</summary>
        internal static bool PreAnsweredAtTrigger(int choiceCount, bool isMarketplace) =>
            choiceCount <= 1 && !isMarketplace;

        internal static bool PreAnsweredAtTrigger(GeoscapeEventData data)
        {
            if (data == null) return false;
            try
            {
                var es = GeoLevel()?.EventSystem;
                return PreAnsweredAtTrigger(data.Choices == null ? 0 : data.Choices.Count,
                                            es != null && es.IsEventTheMarketplace(data));
            }
            catch { return false; }
        }

        /// <summary>The live <c>GeoscapeEvent</c> this peer's own view holds for an id — on screen, popped
        /// and mid-switch, or still queued — else null. The HOST answer handler wants it because that
        /// instance carries the real <c>Context</c> (site + vehicle) the reward is applied against.</summary>
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

        /// <summary>Mark a mirrored instance resolved without resolving anything. <c>ShowEncounter</c> takes
        /// the single-choice branch for a narrative/quest event (UIModuleSiteEncounters.cs:239-241) →
        /// <c>SetSingleChoiceEncounter</c>:251 → <c>SelectChoice</c>:598 →
        /// <c>if (!ev.IsCompleted) ev.CompleteEvent(...)</c>, and <c>GeoscapeEvent.IsCompleted</c> is
        /// per-INSTANCE (GeoscapeEvent.cs:36) — a mirrored instance over an already-Completed record says
        /// "not completed" and would re-grant the ENTIRE reward client-side. Marking the instance
        /// completed skips that branch; <see cref="StubReward"/> keeps <c>SelectChoice</c>:604 and
        /// <c>SetClosingEncounter</c>:357 from NRE-ing on a null <c>ChoiceReward</c> and, when the host's
        /// 0xBD payload has arrived, gives the page the real list to draw. With no payload yet it is empty,
        /// <c>HasRewards()</c>==false (GeoFactionRewardApplyResult.cs:69) makes <c>ShowReward</c>:363
        /// return at once, and the native page renders outcome TEXT only. Consumer:
        /// <see cref="EventCompleteArbiter"/> refusing a resolution needs exactly that pair of writes.</summary>
        internal static void ClearResolvedInstance(GeoscapeEvent ev)
        {
            SetIsCompleted?.Invoke(ev, new object[] { false });
        }

        internal static void MarkResolvedInstance(GeoscapeEvent ev)
        {
            SetIsCompleted?.Invoke(ev, new object[] { true });
            // NEVER OVER A REAL ONE. The stub exists for the peer that did not mint the reward; the HOST that
            // lost the click race reaches this same replay (EventChoiceClientLock -> ReplayResolution) holding
            // the GeoFactionReward its own CompleteEvent:101 just minted, and overwriting it made
            // HasRewards()==false, ShowReward:363 return at once, and the host's outcome page list the point of
            // interest's gains as nothing at all. The stub is a FALLBACK, not a reset.
            if (ev?.ChoiceReward == null)
                SetChoiceReward?.Invoke(ev, new object[] { StubReward(ev?.EventID) });
        }

        /// <summary>The reward object the mirrored outcome page draws. No longer always EMPTY: when the
        /// host's 0xBD payload for this event has arrived it is BUILT from it — resources, items and every
        /// row kind the page renders (<c>MissionOutcomeMirror.Build</c> fills the ApplyResult and the
        /// companion lists <c>ShowReward</c> iterates instead). Nothing is granted here: every value already
        /// landed on this peer as ordinary replicated state, and this is the PRESENTATION of it, exactly like
        /// 0xBB does for a mission outcome. An empty stub stays the fallback, so a reward that has not
        /// arrived yet renders as no list rather than as a crash.</summary>
        private static GeoFactionReward StubReward(string eventId)
        {
            OccurrenceId occurrence;
            if (TryFindDurableOccurrence(eventId, out occurrence) &&
                _durableRewards.TryGetValue(occurrence, out var durableWire))
                return MissionOutcomeMirror.Build(GeoLevel(), durableWire, "Encounter");
            if (eventId != null && _rewards.TryGetValue(eventId, out var wire))
                return MissionOutcomeMirror.Build(GeoLevel(), wire, "Encounter");
            return new GeoFactionReward { ApplyResult = new GeoFactionRewardApplyResult() };
        }

        /// <summary>Close the declared race: 0xBD is broadcast when the HOST completes the event, while a
        /// mirroring peer may already be building its page off its own record delta, so a payload that lands
        /// a frame later used to be stashed for a page nobody would build again. If the dialog for this event
        /// is still live, its stub is REPLACED in place — the next thing to read <c>ChoiceReward</c>
        /// (<c>SetClosingEncounter</c>:357 → <c>ShowReward</c>) then sees the real reward.
        ///
        /// NOT a re-render, deliberately: the outcome page is a synthetic one-off built inline over a
        /// throwaway event with <c>EventID == ""</c> (UIModuleSiteEncounters.cs:326-355), which is exactly why
        /// <see cref="RepaintDialog"/> refuses to touch it — re-driving it needs the answering choice this
        /// peer no longer holds and re-posts the encounter sound. So the race narrows from "the whole dialog
        /// lifetime" to "after the page was already drawn", and that last sliver stays declared.</summary>
        private static void RestampLiveInstance(string eventId, MissionOutcomeMirror.RewardWire wire)
        {
            var ev = LiveInstance(GeoLevel()?.View, eventId);
            if (ev == null || !ev.IsCompleted || SetChoiceReward == null) return;
            SetChoiceReward.Invoke(ev, new object[] { MissionOutcomeMirror.Build(GeoLevel(), wire, "Encounter") });
            Debug.Log("[MP][events] reward for '" + eventId + "' arrived after this peer had already stubbed the " +
                      "live dialog — re-stamped in place, so the outcome page draws the host's list");
        }

        /// <summary>Is this dialog's outcome someone ELSE's to have decided — so that a click on it must
        /// charge nothing, resolve nothing and send no intent? Two ways in, one answer:
        ///   • the game answered it ITSELF at trigger (<see cref="PreAnsweredAtTrigger"/>) and this peer is
        ///     not the one that ran it. On the host the auto-complete really ran on THIS instance, so
        ///     <c>IsCompleted</c> is true and the click takes the fully native path (vanilla: the wallet
        ///     charge and any mission launch are the host's to make). On a client the mirrored
        ///     instance never ran it — relaying the click as a 0xB4 answer would be rejected by the host's
        ///     own validator ("already answered"), and letting it run natively would take the choice's cost
        ///     out of the CLIENT's wallet (UIModuleSiteEncounters.cs:571-573, two calls before any funnel a
        ///     guard could sit on), which is law 3 divergence;
        ///   • another peer answered THIS raise (<see cref="IsFrozen"/>) — the ordinary lost race, on either
        ///     peer.
        /// The winner is the record's <c>SelectedChoice</c>; for the pre-answered case whose record delta has
        /// not landed yet it is <c>Choices[0]</c>, which is what the game itself completed with
        /// (<c>CompleteEvent(Choices?.FirstOrDefault())</c>, GeoscapeEventSystem.cs:655).</summary>
        internal static bool ResolutionIsNotOurs(GeoscapeEvent ev, out GeoEventChoice winner)
        {
            winner = null;
            if (!InSession || string.IsNullOrEmpty(ev?.EventID)) return false;
            // The ONE case that stays fully native: the game answered THIS instance at trigger on a peer that
            // owns resolutions. IsCompleted alone is not that test — a host that LOST the race has its own
            // live dialog instance completed by the relayed answer (EventSync.HandleAnswer resolves through
            // EventPopup.LiveInstance), and letting that click run native would charge the losing host a
            // second time on top of the charge HandleAnswer already made.
            if (IntentRail.ShouldRunNative() && ev.IsCompleted && PreAnsweredAtTrigger(ev.EventData)) return false;
            if (IsFrozen(ev, out winner)) return true;                          // somebody else answered this raise
            if (!PreAnsweredAtTrigger(ev.EventData)) return false;              // still open: the click decides it
            var choices = ev.EventData?.Choices;
            winner = choices != null && choices.Count > 0 ? choices[0] : null;
            return true;
        }

        // ─── Which peer's click PRODUCED the resolution? (initiator vs observer) ─

        /// <summary>The one answer THIS peer has sent and not yet seen come back, as (event, raise, choice).
        /// A client's click is BLOCKED and relayed (<see cref="EventChoiceClientLock"/>), so by the time the
        /// answer exists as replicated state the gesture is long gone and nothing on the wire says whose it
        /// was — the record carries WHAT was chosen, never WHO chose it, and it must not: the ledger is
        /// replicated state, not a claim table (<see cref="EventSync"/>). So the answerer remembers its own
        /// click locally; every other peer has an empty memo and stays an observer.
        ///
        /// ONE slot, not a table: the geoscape shows one modal at a time and a peer can have at most one
        /// answer in flight. Overwritten by the next send, and consumed (or dropped) the moment the raise it
        /// belongs to is decided, so nothing survives to satisfy a later resolution by accident.
        ///
        /// The HOST needs no memo: its own click is never blocked — it runs the native funnel and reaches
        /// UIModuleSiteEncounters' result page inside the click itself, with no delta in between.</summary>
        /// <summary>Distinct from every index a peer can actually answer with — including native's -1
        /// "no choice" (UIModuleSiteEncounters.cs:562-566), which is a REAL answer whose author must still be
        /// recognised as the answerer. RailCheck L47 asserts exactly that, because reusing -1 as "nothing
        /// pending" would silently demote every no-choice answerer to an observer. CONST, and declared before
        /// the fields: <c>_pendingChoice</c>'s initializer needs it folded at compile time — as a
        /// <c>static readonly</c> declared after them it would still be 0 when they run.</summary>
        internal const int NothingPending = int.MinValue;

        private static string _pendingId;
        private static int _pendingTrigger;
        private static int _pendingChoice = NothingPending;

        private static string _replayedId;
        private static int _replayedTrigger;

        /// <summary>Remember that THIS PEER'S CLICK ON THIS RAISE WAS REPLAYED — i.e. the native
        /// <c>UIModuleSiteEncounters.SelectChoice</c> body never ran here, because
        /// <see cref="ResolutionIsNotOurs"/> turned the click into a result page instead. It is the ONE fact
        /// that separates a HOST that resolved the event itself (its <c>SelectChoice</c>:598-613 ran, tail and
        /// all) from a HOST that LOST the race (nothing of that method ran), and nothing else records it: the
        /// arriving record delta names the CHOICE and never the chooser, and by the time the peer clicks
        /// through the replayed page the record reads Completed on every peer alike. <see cref="MissionSync"/>
        /// reads it to decide whether this peer still owes itself the <c>LaunchMission</c> call :612 makes.
        /// Keyed on the RAISE as well as the id, for <see cref="AnswerIsOurs"/>'s third reason: a re-triggered
        /// event opens a fresh window, and a memo from the previous raise says nothing about this one.</summary>
        internal static void NoteReplayedClick(GeoscapeEvent ev)
        {
            _replayedId = ev?.EventID;
            _replayedTrigger = RaiseTriggerOf(ev);
        }

        /// <summary>Was THIS peer's click on this window replayed rather than executed? Not consumed: the
        /// replayed page's own OK button walks the same window through <c>SelectChoice</c> a second time
        /// (:1302's closing-page arm) and <c>FinishEncounter</c> — where the answer is needed — is reached
        /// from THERE, after the memo would already have been retired.</summary>
        internal static bool ClickWasReplayed(GeoscapeEvent ev)
            => !string.IsNullOrEmpty(ev?.EventID) &&
               string.Equals(_replayedId, ev.EventID, StringComparison.Ordinal) &&
               _replayedTrigger == RaiseTriggerOf(ev);

        /// <summary>Remember the answer this peer is about to relay. Called from the capture seam at the
        /// moment the click is converted into a 0xB4 intent — the last point at which "this peer chose it"
        /// is known anywhere.</summary>
        internal static void NoteOwnAnswer(GeoscapeEvent ev, int choiceIndex)
        {
            _pendingId = ev?.EventID;
            _pendingTrigger = RaiseTriggerOf(ev);
            _pendingChoice = choiceIndex;
        }

        /// <summary>PURE (RailCheck L47): is the resolution now sitting on the record the answer to the click
        /// THIS peer made on THIS window? All four axes are load-bearing:
        ///   • <paramref name="pendingChoice"/> == <see cref="NothingPending"/> — this peer never answered, so
        ///     it is an OBSERVER and must get the replay (winner highlighted + clickable) it can click through;
        ///   • the EVENT must match — a memo from another window says nothing about this one;
        ///   • the RAISE must match (<see cref="RaiseTriggerOf"/>) — a re-triggered event opens a FRESH window
        ///     while the previous raise's memo may still be unconsumed, and fast-forwarding the new picker on
        ///     the old click answers a question the player was never shown;
        ///   • the CHOICE must match — a peer that answered and LOST the race (its intent rejected, the other
        ///     peer's answer accepted) did not produce this resolution: it is an observer of someone else's
        ///     choice, and jumping it to a result page for a choice it did not pick is a lie.
        /// True ⇒ walk straight to the native result page: the player already clicked once and must not be
        /// asked to confirm its own answer.</summary>
        internal static bool AnswerIsOurs(string pendingId, int pendingTrigger, int pendingChoice,
                                         string eventId, int raiseTrigger, int selectedChoice) =>
            pendingChoice != NothingPending &&
            !string.IsNullOrEmpty(eventId) &&
            string.Equals(pendingId, eventId, StringComparison.Ordinal) &&
            pendingTrigger == raiseTrigger &&
            pendingChoice == selectedChoice;

        /// <summary>Ask <see cref="AnswerIsOurs"/> of the live memo and RETIRE it. The memo dies with the
        /// raise it names whether it matched or not — a lost race must leave nothing behind that a later
        /// resolution at the same index could accidentally satisfy.</summary>
        private static bool ConsumeOwnAnswer(GeoscapeEvent ev)
        {
            int trigger = RaiseTriggerOf(ev);
            var rec = LiveRecord(ev?.EventID, ev?.Record);
            bool ours = AnswerIsOurs(_pendingId, _pendingTrigger, _pendingChoice,
                                     ev?.EventID, trigger, rec == null ? -1 : rec.SelectedChoice);
            if (string.Equals(_pendingId, ev?.EventID, StringComparison.Ordinal) && _pendingTrigger == trigger)
            { _pendingId = null; _pendingChoice = NothingPending; }
            return ours;
        }

        /// <summary>Show the native OUTCOME page for a resolution THIS PEER MUST NOT RE-RUN — either one it
        /// did not make (an observer clicking through the replay, <see cref="ResolutionIsNotOurs"/>) or its
        /// OWN, coming back off the rail as a record delta (<see cref="ConsumeOwnAnswer"/>). Both need the
        /// same thing and neither may take the native click path, so they share this one: the answerer has
        /// already been charged on the host and the observer must not be charged at all.
        /// Nothing is charged (the native charge at
        /// UIModuleSiteEncounters.cs:571-573 is skipped entirely with the handler) and nothing is resolved —
        /// <see cref="MarkResolvedInstance"/> gives the instance the empty reward stub that
        /// <c>SetClosingEncounter</c>:357 and <c>SelectChoice</c>:604 dereference unguarded, and
        /// <c>HasRewards()</c>==false keeps <c>ShowReward</c>:363 from claiming rewards this peer never got.
        /// Returns false when there is no page to show, and says so — the caller closes the window instead of
        /// leaving a dead picker up.</summary>
        internal static bool ReplayResolution(UIModuleSiteEncounters module, GeoscapeEvent ev, GeoEventChoice winner)
        {
            if (module == null || ev == null || DismissOnResolution(winner != null, winner?.Outcome != null)) return false;
            if (SetClosingEncounter == null)
            {
                Debug.LogError("[MP][events] cannot replay '" + ev.EventID + "' — UIModuleSiteEncounters" +
                               ".SetClosingEncounter(GeoscapeEvent, GeoEventChoice, bool) did not resolve, so the " +
                               "winning choice has no result page and the window can only be closed");
                return false;
            }
            try
            {
                MarkResolvedInstance(ev);
                SetClosingEncounter.Invoke(module, new object[] { ev, winner, false });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][events] replay of '" + ev.EventID + "' threw while building the result page: " + ex);
                return false;
            }
        }

        /// <summary>An MP session is live, either side. The choice FREEZE, the close and the Esc guard are
        /// peer-symmetric multiplayer presentation rules — a host holding a dialog another peer already
        /// answered is the same case as a client — while outside a session neither may touch the native
        /// dialog at all.</summary>
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
            try { return GenericApplier.GeoLevel(); }
            catch { return null; }
        }

        /// <summary>The live (post-apply) record for an event id, falling back to the dialog's own ref.</summary>
        internal static GeoscapeEventRecord LiveRecord(string eventId, GeoscapeEventRecord fallback)
        {
            try { return GeoLevel()?.EventSystem?.GetEventRecord(eventId) ?? fallback; }
            catch { return fallback; }
        }

        /// <summary>Ship what the host's <c>CompleteEvent</c> actually granted, so every OTHER peer can render
        /// the green reward list without running the method that mints it. The WHOLE result since 2026-08-05,
        /// not just resources+items: the rows that hold live refs (diplomacy first — the one a player
        /// actually noticed missing, then new units, revealed sites, damaged aircraft and the rest) ride
        /// <c>MissionOutcomeMirror</c>'s generic row codec as stable ADDRESSES and are re-resolved on the
        /// receiving peer. Same codec as 0xBB, extended once rather than twice.</summary>
        internal static void HostBroadcastReward(GeoscapeEvent source, GeoFactionReward reward)
        {
            string eventId = source?.EventID;
            var result = reward?.ApplyResult;
            if (result == null || string.IsNullOrEmpty(eventId)) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                byte[] body;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    OccurrenceId occurrence;
                    bool durable = TryGetDurableOccurrence(source, out occurrence) ||
                                   TryFindDurableOccurrence(eventId, out occurrence);
                    if (durable) { w.Write(DurableRewardPayload); EventSync.WriteOccurrence(w, occurrence); }
                    else w.Write(eventId);
                    MissionOutcomeMirror.Encode(w, result);
                    body = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventReward, SyncKind.StateDelta, body)));
                // SYMMETRY: every peer that RECEIVES this records it (HandleInbound), the sender did not — so
                // StubReward on the host had nothing to build from and fell back to the empty object. Read back
                // from the bytes just written rather than converting a second way: the host then holds exactly
                // what the clients hold.
                using (var back = new MemoryStream(body))
                using (var r = new BinaryReader(back, Encoding.UTF8))
                {
                    OccurrenceId occurrence;
                    if (TryGetDurableOccurrence(source, out occurrence) || TryFindDurableOccurrence(eventId, out occurrence))
                    { r.ReadByte(); EventSync.ReadOccurrence(r); _durableRewards[occurrence] = MissionOutcomeMirror.DecodeRaw(r); }
                    else { WireString.ReadKey(r); _rewards[eventId] = MissionOutcomeMirror.DecodeRaw(r); }
                }
                Debug.Log("[MP][events] HOST reward for '" + eventId + "' broadcast");
            }
            catch (Exception ex)
            { Debug.LogError("[MP][events] reward broadcast for '" + eventId + "' failed: " + ex); }
        }

        internal static void HostBroadcastDurableReward(OccurrenceId occurrence, byte[] encodedReward)
        {
            if (encodedReward == null || encodedReward.Length == 0) return;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(DurableRewardPayload); EventSync.WriteOccurrence(w, occurrence); w.Write(encodedReward);
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventReward, SyncKind.StateDelta, ms.ToArray())));
            }
        }

        internal static void HostBroadcastDurableDecision(SharedChoiceDecision decision)
        {
            var engine = NetworkEngine.Instance;
            if (decision == null || engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            try
            {
                byte[] body;
                using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms, Encoding.UTF8))
                { w.Write(DurableDecisionPayload); EventSync.WriteDecision(w, decision); body = ms.ToArray(); }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventReward, SyncKind.StateDelta, body)));
            }
            catch (Exception ex) { Debug.LogError("[MP][events] durable decision broadcast failed: " + ex); }
        }

        internal static void HostRebroadcastLockedDecisions()
        {
            var engine = NetworkEngine.Instance; var store = DurableInboxSession.ActiveStore;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost || store == null ||
                Time.realtimeSinceStartup < _nextDurableDecisionReplay) return;
            _nextDurableDecisionReplay = Time.realtimeSinceStartup + 1f;
            foreach (var decision in store.Canonical.Decisions.Where(x => x.Phase == SharedChoicePhase.ChoiceLocked &&
                store.Ledger.AllEntries.Any(e => e.Occurrence.Equals(x.Occurrence) &&
                    e.Lifecycle != InboxLifecycle.Removed && e.Lifecycle != InboxLifecycle.Dismissed)).ToArray())
            {
                HostBroadcastDurableReward(decision.Occurrence, decision.RewardPayload);
                HostBroadcastDurableDecision(decision);
            }
        }
    }

    /// <summary>
    /// HOST capture seam (law 4a) for the event's REWARD. <c>GeoscapeEvent.CompleteEvent</c> is the ONLY
    /// writer of <c>ChoiceReward</c> (GeoscapeEvent.cs:101) and its very next line GRANTS it (:102), so a
    /// client must never reach it — which leaves every non-answering peer with a null reward and, until now,
    /// an empty green list on the outcome page (<see cref="EventPopup.MarkResolvedInstance"/> stubs it so the
    /// unguarded dereferences at <c>SetClosingEncounter</c>:357 and <c>SelectChoice</c>:604 cannot NRE).
    ///
    /// A POSTFIX, because the reward does not exist until the native body has run: it is a capture of a
    /// GRANTED amount, not a request for one, the same posture as
    /// <c>MissionOutcomeMirror</c>'s <c>OnMissionRewardApplied</c> seam. Host-only inside.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEvent), nameof(GeoscapeEvent.CompleteEvent))]
    internal static class EventRewardBroadcast
    {
        private static void Postfix(GeoscapeEvent __instance, GeoFactionReward __result) =>
            EventPopup.HostBroadcastReward(__instance, __result);
    }

    /// <summary>
    /// HOST capture seam (law 4a) for the window itself: <c>GeoscapeView.OnGeoscapeEventRaised</c>
    /// (GeoscapeView.cs:2034) is the ONE place the game turns a raised event into a queued dialog, and it is
    /// the last point at which the live <c>GeoscapeEventContext</c> — site, vehicle, and the def texts as
    /// this host resolved them — still exists. Everything downstream is a record, and a record cannot
    /// rebuild a context (see <see cref="EventPopup"/>).
    ///
    /// A POSTFIX, so the host's own window is queued exactly as it always was and the mirror is a pure
    /// addition. <c>SuppressEvents</c> is honoured explicitly because a postfix runs even when the native
    /// body took the early return at :2036 — mirroring a window the host itself refused to show would be a
    /// window only the client gets.
    ///
    /// The client arm is EMPTY on purpose: a client's own event raises are already blocked upstream at the
    /// model funnel (<c>ClientSimGate.GeoscapeEventRaiseGate</c> on
    /// <c>GeoscapeEventSystem.OnGeoscapeEvent</c>), and the mirrored raise pushes the view state through
    /// <c>GeoscapeViewSwitchQuery</c> DIRECTLY, so it never re-enters this method and cannot echo (law 8).
    /// The remaining caller — <c>GeoscapeView.ToMarketplace</c>:737 — is a legitimate local gesture on
    /// either peer, and <see cref="EventPopup.HostBroadcast"/> declines to mirror marketplace windows.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeView), "OnGeoscapeEventRaised", new[] { typeof(GeoscapeEvent) })]
    internal static class EventRaiseBroadcast
    {
        /// <summary>THE HOST'S OWN CURTAIN HOLD (2026-08-08). False = skip the native body this time; the
        /// event is parked and <c>EventPopup.DrainHostCurtained</c> re-invokes THIS method after the reveal,
        /// so the window is queued by the game's own code and the postfix below still mirrors it — one raise,
        /// one broadcast, just later. See <see cref="EventPopup.HostRaiseWaitsForReveal"/>.</summary>
        private static bool Prefix(GeoscapeView __instance, GeoscapeEvent geoEvent, out bool __state)
        {
            __state = __instance != null && !__instance.SuppressEvents &&
                      EventPopup.HoldHostRaiseBehindCurtain(geoEvent);
            return !__state;
        }

        /// <summary><paramref name="__state"/> = "the raise was PARKED". Harmony runs a postfix even when the
        /// prefix skipped the body, and mirroring a window this host has not queued yet would invert the very
        /// order the park exists to align — the clients would insert it while the host still had not. The
        /// replay re-enters this method, so the broadcast happens then, with the real queued priority.</summary>
        private static void Postfix(GeoscapeView __instance, GeoscapeEvent geoEvent, bool __state)
        {
            if (__state || __instance == null || __instance.SuppressEvents) return;
            EventPopup.HostBroadcast(__instance, geoEvent);
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
            if (!EventPopup.InSession) return true;             // solo: stock behaviour end to end

            // The OK/Continue button of a CLOSING page carries neither Outcome nor Requirments
            // (SetClosingEncounter:346-351 builds it with Text only), yet _geoEvent still points at the
            // REAL event there — SetEncounter never reassigns it (:265-303). FIRST, because a replayed
            // result page is reached through an already-resolved event and its own OK button must not be
            // read as another click on the decided picker. It resolves nothing: no Wallet.Take (:571-573
            // needs Requirments) and CompleteEvent is skipped on an instance already marked completed
            // (:562-567 and :580 → SelectChoice:600).
            if (ev.IsCompleted && choice != null && choice.Outcome == null && choice.Requirments == null) return true;

            // PER-PEER MISSION-START CONFIRMATION (owner 2026-08-10). Every peer answers its OWN copy: no
            // replay of anybody else's choice onto it, and a decline stays LOCAL — it resolves nothing shared,
            // so one player saying "not now" can never take the mission away from the rest (the 2026-08-08
            // report, in the seam the game really uses). The START side is unchanged and stays
            // host-authoritative: the host runs the native funnel, a client relays the 0xB4 answer, and the
            // launch behind it is single by MissionSync.Validate / MissionStartAlreadyCommitted (L405).
            // ponytail: a NON-mission choice in this family grants nothing to anyone, even if its def carries
            // a reward — upgrade to relaying reward-only declines if such an event ever turns up.
            bool perPeer = EventPopup.IsMissionStartConfirmationEvent(ev.EventData);

            // Decided elsewhere (the game at trigger, or the peer that won the race): show the RESULT PAGE
            // rather than eat the click. Peer-symmetric — a losing HOST must not pay for a choice it did not
            // get either, and that charge happens two calls before any funnel EventCompleteArbiter guards.
            if (!perPeer && EventPopup.ResolutionIsNotOurs(ev, out var winner))
            {
                // The native body below this line is what NEVER RUNS on this peer — including
                // SelectChoice:612's LaunchMission, the one call that carries a mission-start encounter to the
                // pre-mission squad screen. Record it here, at the only point where "my click was replayed"
                // is known, so MissionEncounterNav can re-issue it for a LOSING HOST exactly as it already
                // does for a client (MissionSync.cs).
                EventPopup.NoteReplayedClick(ev);
                var rec = EventPopup.LiveRecord(ev.EventID, ev.Record);
                Debug.Log("[MP][events] click on '" + ev.EventID + "' REPLAYED — the answer is not this peer's " +
                          "(record=" + (rec == null ? "none" : rec.State.ToString()) + ", choice " +
                          (rec == null ? -1 : rec.SelectedChoice) + "); showing the winning choice's result page");
                if (EventPopup.ReplayResolution(__instance, ev, winner)) return false;
                __instance.Context?.View?.FinishQueriedState();   // nothing to show — don't strand the player
                return false;
            }

            // A CANCEL on the per-peer family: local on EVERY peer, host included. Nothing is relayed and no
            // record is resolved, so the offer stays open for whoever still wants to take it. Closed with the
            // view's own FinishQueriedState — the same exit the replay arm above uses when it has no page to
            // show — deliberately NOT the module's FinishEncounter, whose postfix (MissionEncounterNav) would
            // read another peer's winning answer off the record and drag this one to the squad screen it just
            // declined. The squad screen still reaches every peer that DOES want it, by itself and with no
            // click, through MissionArrivalNav's state watch (no quorum, P13).
            if (perPeer && !EventPopup.StartsMission(choice))
            {
                EventPopup.BriefProbe(ev.EventID, "cancel-local", "per-peer mission-start family — this peer's " +
                    "decline closes ITS window only: no 0xB4 answer, no record resolution, the mission offer " +
                    "stays open for every other peer");
                __instance.Context?.View?.FinishQueriedState();
                // ...AND IT STAYS OPEN FOR THIS ONE TOO (2026-08-11). Closing the window is not retiring the
                // offer: the game's own re-offer switch is EnableGeoscapeEvent (CompleteEvent:103-106), and
                // without it neither menu row can ever come back (MissionReoffer's header has the measurement).
                MissionReoffer.AfterDecline(ev.EventID, ev.Context?.Site);
                return false;
            }

            if (IntentRail.ShouldRunNative())
            {
                if (perPeer)
                    EventPopup.BriefProbe(ev.EventID, "start-native", "per-peer mission-start family on the " +
                        "peer that owns resolutions — SelectChoice:598-613 runs natively, :612 opens this " +
                        "peer's squad screen and the launch is host-authoritative");
                if (SyncApplyScope.Active) return true;
                return !EventSync.TryHostLocalDurableAnswer(__instance, ev, choice);
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
            // Remember that THIS peer is the one answering, before the click is thrown away. The resolution
            // comes back as an ordinary record delta that names the CHOICE and never the chooser, so without
            // this the repaint cannot tell the answerer from an observer and paints the answerer the replay —
            // asking it to click its own answer a second time (EventPopup.ConsumeOwnAnswer).
            EventPopup.NoteOwnAnswer(ev, index);
            OccurrenceId occurrence;
            var store = DurableInboxSession.ActiveStore;
            if (!EventPopup.TryGetDurableOccurrence(ev, out occurrence) || store == null)
            { Debug.LogWarning("[MP][events] answer dropped — dialog has no exact durable occurrence"); return false; }
            string local = Multiplayer.Network.ClientIdentity.PlayerGuid.ToString("D");
            var entry = store.Ledger.AllEntries.FirstOrDefault(x => x.Occurrence.Equals(occurrence) &&
                string.Equals(x.Membership.PlayerGuid, local, StringComparison.OrdinalIgnoreCase));
            if (entry == null) { Debug.LogWarning("[MP][events] answer dropped — no active local entitlement"); return false; }
            IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpAnswer, "answer '" + id + "' choice=" + index,
                            w => EventSync.WriteDurableAnswer(w, occurrence, entry.Membership,
                                entry.LifecycleRevision, id, index));
            // The per-peer family gets no result page and no freeze, so nothing downstream would ever close
            // this peer's own copy — the answer is sent, the window is this peer's to dismiss. The squad
            // screen arrives on its own (MissionArrivalNav) the moment the host's ActiveMission lands here.
            if (perPeer)
            {
                EventPopup.BriefProbe(id, "start-relayed", "per-peer mission-start family on a client — 0xB4 " +
                    "answer sent (choice " + index + ") and this peer's own window closed; the launch is the " +
                    "host's to make, exactly once");
                __instance.Context?.View?.FinishQueriedState();
            }
            return false;
        }
    }

    /// <summary>
    /// The SAME intent-capture seam (law 4a) at the event class's SECOND completion funnel. The
    /// marketplace window is a different module with a different model funnel — its click
    /// (UIModuleTheMarketplace.OnChoiceSelected:210) calls
    /// <c>GeoscapeEvent.CompleteMarketplaceEvent</c> (GeoscapeEvent.cs:74), never
    /// <c>CompleteEvent</c> — so neither <see cref="EventChoiceClientLock"/> (wrong module) nor
    /// <see cref="EventCompleteArbiter"/> (wrong funnel) ever saw it. RailCheck L36 is what keeps the
    /// two funnels' coverage from drifting apart again.
    ///
    /// BLOCK-FIRST for the same reason the lock above is: <c>Wallet.Take</c>:215 charges the shared
    /// wallet two lines BEFORE the funnel (:219), so a model-seam refusal cannot save the resources.
    ///
    /// It really is reachable on a client, and with NO RECORD at all: MarketplaceAbility
    /// .ActivateInternal:43 → <c>GeoscapeView.ToMarketplace</c>:734-738 builds a fresh
    /// <c>GeoscapeEvent</c> (Record == null) and calls the view's <c>OnGeoscapeEventRaised</c>:2034
    /// DIRECTLY, bypassing <c>GeoscapeEventSystem</c> — so <c>GeoscapeEventRaiseGate</c> does not
    /// cover it, <see cref="EventPopup.HostBroadcast"/> deliberately never mirrors a marketplace window, and
    /// <c>GeoscapeView.SuppressEvents</c> is written by nothing but a console toggle
    /// (GeoscapeView.cs:2205). A client purchase would apply <c>ChoiceReward</c> locally and spend from
    /// the replicated wallet: PERMANENT divergence, because the diff is host-now vs host-before and never
    /// mentions a change only the client made.
    ///
    /// Refused, not relayed as a 0xB4 op: the offer LIST is not replicated (docs/rail-baseline.txt:14 —
    /// GeoMarketplace.MarketplaceOptions EXCLUDED, bridge-unresolved), so "buy offer N" would index into
    /// a list the host does not share. Blocking a divergence beats half an intent.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleTheMarketplace), "OnChoiceSelected", new[] { typeof(GeoEventChoice) })]
    internal static class MarketplaceChoiceClientLock
    {
        private static bool Prefix()
        {
            if (IntentRail.ShouldRunNative()) return true;
            Debug.Log("[MP][events] marketplace purchase BLOCKED on a client — the host owns the wallet and the " +
                      "reward (OnChoiceSelected:215 charges Wallet.Take before CompleteMarketplaceEvent:219); " +
                      "buying stays host-only until the offer list is replicated");
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
    /// <see cref="EventPopup.RepaintDialog"/> — so applying the freeze HERE covers a picker built over an
    /// already-answered record (the answer landed while the raise was in flight) and a picker resolving under
    /// the player mid-look, with one code path instead of one per entry point.
    /// </summary>
    [HarmonyPatch(typeof(SiteBaseChoicesController), "SetChoices")]
    internal static class EventChoiceFreeze
    {
        private static void Postfix(SiteBaseChoicesController __instance, GeoscapeEvent eventData) =>
            EventPopup.FreezeChoiceButtons(__instance, eventData);
    }

    /// <summary>
    /// DIAGNOSTIC ONLY. The token table dereferences the context UNGUARDED — every haven replacer is a bare
    /// <c>context.Site....</c> with no null check (GeoscapeEventContext.cs:20-40, <c>[HavenName]</c> =
    /// <c>context.Site.SiteName.Localize()</c>), and <c>[AircraftName]</c> does the same to
    /// <c>context.Vehicle</c> — and the throw lands inside <c>UIStateGeoscapeEvent.EnterState</c>, AFTER the
    /// raise has already been logged as a SUCCESS, leaving the window HALF-BUILT: the title is set
    /// (UIModuleSiteEncounters:217) but the description (:308) and <c>SetChoices</c> (:321) never run, so the
    /// body and EVERY choice button keep the placeholder the designers BAKED into the scene and the prefab
    /// ("Fasdasdsadasg…", "Really long choice description btw…"). A localized title over dev placeholder text
    /// and four dead buttons is what a player saw, and nothing in any log said it broke.
    ///
    /// The FIX for that is upstream, not here: a mirrored window is built from a REAL context and
    /// <see cref="EventPopup.ContextRefusal"/> raises NOTHING when the shipped site/vehicle does not resolve,
    /// so a context-LESS window no longer reaches the render at all. What remains reachable is the case the
    /// HOST hits too: a def holding a token its own context legitimately cannot fill. Swallowing to the
    /// ORIGINAL text is the game's OWN tolerance path for that (:229-234 substitutes only when a replacer
    /// produced a non-empty string, so an unresolvable token is designed to stay visible as its raw
    /// <c>[Token]</c>), and the real description and real choices render.
    ///
    /// It is NOT silent: every distinct text that trips it is logged as an ERROR once. If this ever fires on
    /// a MIRRORED window it means the refusal above let a bad context through, which is a bug in this file.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEventContext), "ReplaceEventTokens")]
    internal static class EventTokenDerefGuard
    {
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        private static Exception Finalizer(Exception __exception, string originalText, ref string __result)
        {
            if (__exception == null) return null;
            __result = originalText ?? "";
            if (_seen.Add(originalText ?? ""))
                Debug.LogError("[MP][events] token deref threw " + __exception.GetType().Name + " while resolving \"" +
                               (originalText ?? "") + "\" — this window's context has no Site/Vehicle for a token in " +
                               "its text. The raw [Token] stays visible and the window renders instead of aborting " +
                               "half-built; on a MIRRORED window this must never happen (EventPopup.ContextRefusal " +
                               "refuses to raise one), so seeing it there is a bug in the raise payload.");
            return null;
        }
    }

    /// <summary>
    /// THE EMPTY OK-MODAL, fixed at its source. The 2026-07-31 probe named the window on the 08-01 run:
    /// <c>event='PROG_AN2_MISS' useEventTexts=False outcomeLen=0 descrFallbackLen=325</c> — a page with a
    /// body of nothing and an OK button, exactly the shape the user reported.
    /// <c>UIModuleSiteEncounters.SetClosingEncounter</c> (:324-356) builds the body from
    /// <c>closingChoice.Outcome.OutcomeText</c> (:332) and refills it from the event description ONLY when
    /// <c>useEventTexts</c> is true (:333-336). A REPLAYED event — one whose answer belongs to another peer
    /// (<c>ReplayClosingPage</c> invokes it with <c>useEventTexts: false</c>) — therefore renders empty
    /// whenever that choice's outcome carries no text of its own, which is the ordinary case for a choice
    /// whose payload is mechanical rather than narrative.
    ///
    /// The fix runs the game's OWN :335 fallback for that case, and does it by substituting the parameter
    /// rather than by forcing <c>useEventTexts</c> true: that flag ALSO swaps the button caption from
    /// <c>OKTextKey</c> to <c>closingChoice.Text</c> (:345), which would relabel the button with the choice
    /// the player never picked. Past :332 the native body reads <c>closingChoice</c> only at :345, and only
    /// under the flag we are not setting — so a minimal clone is sufficient and touches nothing else. The
    /// clone is mandatory: <c>Outcome.OutcomeText</c> belongs to the DEF, and writing through it would
    /// corrupt that event for the rest of the campaign.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleSiteEncounters), "SetClosingEncounter")]
    internal static class ClosingEncounterEmptyBodyFix
    {
        private static void Prefix(GeoscapeEvent geoEvent, ref GeoEventChoice closingChoice, bool useEventTexts)
        {
            // useEventTexts==true already reaches the native fallback; nothing to repair.
            if (useEventTexts || closingChoice?.Outcome == null) return;
            try
            {
                var ctx = geoEvent?.Context;
                string body = null;
                try { body = closingChoice.Outcome.OutcomeText?.GetText(ctx); } catch { /* empty is the datum */ }
                if (!string.IsNullOrEmpty(body)) return;

                // The same string :335 would have used — the event description's last variation.
                var descr = geoEvent?.EventData?.Description;
                if (descr == null || descr.Count == 0) return;
                string fallback = null;
                try { fallback = descr[descr.Count - 1]?.GetText(ctx); } catch { }
                if (string.IsNullOrEmpty(fallback)) return;

                // Shaped exactly like the page the native body builds for itself at :338-343.
                closingChoice = new GeoEventChoice
                {
                    Text = closingChoice.Text,
                    Outcome = new GeoEventChoiceOutcome
                    {
                        OutcomeText = new EventTextVariation
                        {
                            General = new LocalizedTextBind(fallback, doNotLocalize: true)
                        }
                    }
                };
                Debug.Log("[MP][modals] empty outcome page refilled from the event description — event='" +
                          (geoEvent?.EventID ?? "<null>") + "' fallbackLen=" + fallback.Length +
                          " (native :335 only runs under useEventTexts, and a replayed page passes false).");
            }
            catch (Exception e) { Debug.LogWarning("[MP][modals] SetClosingEncounter refill failed: " + e.Message); }
        }
    }
}
