using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
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
        public static void Reset() => Seq.Reset();

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
            /// looking at DIFFERENT events. Ties need nothing extra: equal priorities append in insert order
            /// on both sides, and the client's inserts are the host's own order (the raise <c>seq</c> is
            /// strictly increasing and <see cref="SurfaceSeq"/> drops anything out of order).</summary>
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
                    EventId = r.ReadString(),
                    SiteRef = r.ReadString(),
                    VehicleRef = r.ReadString(),
                    Title = r.ReadString(),
                    Narrative = r.ReadString(),
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
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, Encode(seq, p));
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][events] HOST raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority +
                          " site=" + (p.SiteRef == "" ? "none" : p.SiteRef) + " vehicle=" +
                          (p.VehicleRef == "" ? "none" : p.VehicleRef) + " titleLen=" + p.Title.Length +
                          " narrLen=" + p.Narrative.Length);
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
        private static bool StartsMission(GeoEventChoice choice) => choice?.Outcome?.StartMission?.MissionTypeDef != null;

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

        /// <summary>Rebuild the host's window here: resolve the shipped refs, build the REAL context, apply
        /// the wire texts to a PRIVATE copy of the event data, and push the NATIVE view state through the
        /// game's own switch query — the same call the host's <c>OnGeoscapeEventRaised</c> makes.
        /// Returns false when nothing was raised (and always says why).</summary>
        private static bool RaiseMirrored(Raise p, uint seq)
        {
            var geo = GeoLevel();
            var view = geo?.View;
            if (view == null || !(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q))
            {
                // Not "later": this peer has no geoscape to put a window in (tactical mission, mid-load), and
                // there is no history to replay it from. Dropped, loudly — a v1 client behaved the same way.
                Debug.LogWarning("[MP][events] raise of '" + p.EventId + "' DROPPED — this peer has no live " +
                                 "GeoscapeView to show it in, and windows are not replayed after the fact");
                return false;
            }

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
            Bound.Add(geoEvent, new RaiseBinding { Seq = seq, TriggerCount = raiseTrigger });

            // The host's OWN priority, so this peer's queue orders the window exactly as the host's did.
            q.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent), p.Priority)
            { PauseGame = false }); // pause mirrors from the host via the TimeAnchor
            Debug.Log("[MP][events] raised '" + p.EventId + "' seq=" + seq + " priority=" + p.Priority + " site=" +
                      (site == null ? "none" : site.SiteId.ToString()) +
                      " vehicle=" + (vehicle == null ? "none" : vehicle.VehicleID.ToString()) +
                      " record=" + rec.State + "#" + rec.TriggerCount +
                      (es.GetEventRecord(p.EventId) == null ? " (placeholder)" : "") +
                      " answeredFrom=#" + raiseTrigger);
            return true;
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
                if (DismissOnResolution(winner != null, winner?.Outcome != null))
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

        private sealed class RaiseBinding { public uint Seq; public int TriggerCount; }

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
        /// completed skips that branch; the empty reward stub keeps <c>SelectChoice</c>:604 and
        /// <c>SetClosingEncounter</c>:357 from NRE-ing on a null <c>ChoiceReward</c>, while
        /// <c>HasRewards()</c>==false (GeoFactionRewardApplyResult.cs:69) makes <c>ShowReward</c>:363
        /// return at once, so the native page renders outcome TEXT only. Consumer:
        /// <see cref="EventCompleteArbiter"/> refusing a resolution needs exactly that pair of writes.</summary>
        internal static void MarkResolvedInstance(GeoscapeEvent ev)
        {
            SetIsCompleted?.Invoke(ev, new object[] { true });
            SetChoiceReward?.Invoke(ev, new object[] { new GeoFactionReward { ApplyResult = new GeoFactionRewardApplyResult() } });
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
        private static void Postfix(GeoscapeView __instance, GeoscapeEvent geoEvent)
        {
            if (__instance == null || __instance.SuppressEvents) return;
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

            // Decided elsewhere (the game at trigger, or the peer that won the race): show the RESULT PAGE
            // rather than eat the click. Peer-symmetric — a losing HOST must not pay for a choice it did not
            // get either, and that charge happens two calls before any funnel EventCompleteArbiter guards.
            if (EventPopup.ResolutionIsNotOurs(ev, out var winner))
            {
                var rec = EventPopup.LiveRecord(ev.EventID, ev.Record);
                Debug.Log("[MP][events] click on '" + ev.EventID + "' REPLAYED — the answer is not this peer's " +
                          "(record=" + (rec == null ? "none" : rec.State.ToString()) + ", choice " +
                          (rec == null ? -1 : rec.SelectedChoice) + "); showing the winning choice's result page");
                if (EventPopup.ReplayResolution(__instance, ev, winner)) return false;
                __instance.Context?.View?.FinishQueriedState();   // nothing to show — don't strand the player
                return false;
            }

            if (IntentRail.ShouldRunNative()) return true;   // HOST / solo / inside an apply: the click decides it

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
            IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpAnswer, "answer '" + id + "' choice=" + index,
                            w => { w.Write(id); w.Write(index); });
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
}
