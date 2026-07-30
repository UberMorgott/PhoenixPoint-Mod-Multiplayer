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

        /// <summary>FULL session teardown only (SyncEngine.DetachAllChannels) — the raise seq is a host
        /// monotonic stream and a client last-writer guard, so it must NOT be touched at a mid-session
        /// reload boundary (rca-3 contract: a host counter that restarts mid-session makes every following
        /// raise look stale to a client that kept its own high-water mark, and the windows vanish silently).</summary>
        public static void Reset() => Seq.Reset();

        // ─── THE PAYLOAD (host→all, surface 0xB6) ──────────────────────────

        /// <summary>One raise, as the wire carries it. Refs are rail ROOT REFS (law 2 hybrid addressing —
        /// "S#&lt;siteId&gt;" / "V#&lt;id&gt;@&lt;ownerFactionGuid&gt;"), "" when the host's own context had
        /// none. Texts are what the HOST resolved; the client prefers its OWN def whenever that def resolves
        /// to anything (see <see cref="StampWireTexts"/>), so these matter only for a def whose text exists
        /// solely as a host-side RUNTIME mutation (TFTV VoidOmen_{0..19}: empty loc keys +
        /// LocalizedTextBind(text, doNotLocalize:true) written at roll time, TFTVODIandVoidOmenRoll.cs:638-639).</summary>
        internal struct Raise
        {
            public string EventId;
            public string SiteRef;
            public string VehicleRef;
            public string Title;
            public string Narrative;
        }

        /// <summary>[seq:u32][eventId][siteRef][vehicleRef][title][narrative]. Pure both ways so RailCheck
        /// L39 can round-trip it headless — a wire that drops a field silently is a window rendered against
        /// the wrong context, which is the exact failure this family replaced.</summary>
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
                };
            }
        }

        // ─── HOST: capture at the native raise seam ────────────────────────

        /// <summary>Host-side broadcast of ONE window, called from the postfix on the native seam. Reads the
        /// live event's OWN context — the only place site + vehicle exist — and the two strings the native
        /// header/body render, then ships them. Never throws into game code: a raise this fails on is a
        /// window the client does not get, and it says so.</summary>
        internal static void HostBroadcast(GeoscapeEvent ev)
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
                };
                uint seq = Seq.Next(SurfaceIds.GeoEventRaise);
                var env = SyncProtocol.EncodeEnvelope(SurfaceIds.GeoEventRaise, SyncKind.StateDelta, Encode(seq, p));
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope, env));
                Debug.Log("[MP][events] HOST raised '" + p.EventId + "' seq=" + seq + " site=" +
                          (p.SiteRef == "" ? "none" : p.SiteRef) + " vehicle=" +
                          (p.VehicleRef == "" ? "none" : p.VehicleRef) + " titleLen=" + p.Title.Length +
                          " narrLen=" + p.Narrative.Length);
            }
            catch (Exception ex)
            {
                Debug.LogError("[MP][events] HOST raise broadcast FAILED for '" + ev.EventID + "' — no peer will see " +
                               "this window: " + ex);
            }
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
                if (RaiseMirrored(p)) Seq.Mark(SurfaceIds.GeoEventRaise, seq);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] EventPopup inbound failed: " + ex); }
            return true;
        }

        /// <summary>Rebuild the host's window here: resolve the shipped refs, build the REAL context, stamp
        /// the wire texts only where this peer's own def has nothing, and push the NATIVE view state through
        /// the game's own switch query — the same call the host's <c>OnGeoscapeEventRaised</c> makes.
        /// Returns false when nothing was raised (and always says why).</summary>
        private static bool RaiseMirrored(Raise p)
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

            StampWireTexts(data, p.Title, p.Narrative);
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
            var geoEvent = new GeoscapeEvent(data, context) { Record = rec };

            q.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent))
            { PauseGame = false }); // pause mirrors from the host via the TimeAnchor
            Debug.Log("[MP][events] raised '" + p.EventId + "' site=" + (site == null ? "none" : site.SiteId.ToString()) +
                      " vehicle=" + (vehicle == null ? "none" : vehicle.VehicleID.ToString()) +
                      " record=" + rec.State + (es.GetEventRecord(p.EventId) == null ? " (placeholder)" : ""));
            return true;
        }

        /// <summary>Stamp the host-resolved text onto this peer's def as a LITERAL bind — but ONLY where the
        /// peer's own def resolves to nothing. Mod parity (law 10) makes the defs identical, so the local
        /// resolution is normally the right one AND it is in the PLAYER'S language; overwriting it with the
        /// host's would ship one peer the other's locale and permanently mutate a shared def. The case that
        /// is left is the one the wire exists for: a def whose text only ever existed as a host-side RUNTIME
        /// mutation (TFTV VoidOmen writes LocalizedTextBind(text, doNotLocalize:true) at roll time), whose
        /// keys are empty here and would render a BLANK window.</summary>
        private static void StampWireTexts(GeoscapeEventData data, string title, string narrative)
        {
            try
            {
                if (!string.IsNullOrEmpty(title) && string.IsNullOrEmpty(data.Title?.Localize()))
                    data.Title = new LocalizedTextBind(title, doNotLocalize: true);
                var desc = data.Description;
                if (string.IsNullOrEmpty(narrative) || desc == null || desc.Count == 0) return;
                // The LAST variation is the entry the host resolved; earlier pages of a multi-page def stay
                // local so paging still renders this peer's own text.
                var last = desc[desc.Count - 1];
                if (last != null && string.IsNullOrEmpty(last.General?.Localize()))
                    last.General = new LocalizedTextBind(narrative, doNotLocalize: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MP][events] wire-text stamp failed (" + ex.Message + ") — the window renders " +
                                 "this peer's own def text");
            }
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
        ///     only exit is Esc. Guarded by <see cref="ShowingRealChoices"/> so it never closes an OUTCOME page:
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
            if (InSession && IsFrozen(ev, out _))
            {
                Debug.Log("[MP][events] closing the open picker for '" + ev.EventID + "' — the record is resolved, " +
                          "so this peer lost the race and every button on it is already dead");
                view.FinishQueriedState();
                return true;
            }
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
                bool isWinner = frozen && winner != null && ReferenceEquals(btn.Choice, winner);
                btn.Button.IsSelected = isWinner;
                if (frozen && !isWinner) btn.Button.SetInteractable(false);
                else btn.Button.ResetButtonAnimations();
            }
        }

        /// <summary>Is this dialog's answer already decided, and by which choice? The ledger is the
        /// REPLICATED record, never the instance: <c>GeoscapeEvent.IsCompleted</c> is per-INSTANCE
        /// (GeoscapeEvent.cs:36) and a mirrored instance over a resolved record reports false. An empty
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
            if (__instance != null && __instance.SuppressEvents) return;
            EventPopup.HostBroadcast(geoEvent);
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
