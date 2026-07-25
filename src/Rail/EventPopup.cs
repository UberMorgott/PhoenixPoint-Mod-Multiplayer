using System;
using System.Collections.Generic;
using HarmonyLib;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Law 11 presentation for the GeoscapeEventSystem kind ("ES"): geoscape EVENT POPUPS on the client,
    /// driven purely by mirrored STATE — no event-raise messages, no occurrence ids (the old repo's
    /// dedup/FIFO machinery). The rail ships <c>_records</c> (EncounterRecords twin); this class diffs the
    /// live record states against a latch after each apply batch:
    ///   • → Triggered (new record or TriggerCount bump): raise the popup the way the game itself does —
    ///     <c>GeoscapeView.OnGeoscapeEventRaised</c> (GeoscapeView.cs:2034) builds a
    ///     <c>UIStateGeoscapeEvent</c> and enqueues it via <c>_viewSwichQuery.QueryStateSwitch</c> (:2062);
    ///     we replicate that push with <c>PauseGame=false</c> (pause is host-authoritative and arrives via
    ///     the TimeAnchor — forcing a local pause fought it in the old repo). The switch query is a
    ///     priority queue, so multiple events FIFO natively. Idempotent redelivery = no state transition =
    ///     no duplicate popup.
    ///   • Triggered → SelectedChoice/Completed/Reset (host resolved it): if the OPEN state is this
    ///     event's dialog, refresh its stale <c>Record</c> ref (blob apply rebuilt the record instance)
    ///     and close via <c>GeoscapeView.FinishQueriedState</c> (:2164 → FinishCurrentStateSwitch pops the
    ///     state) — the ExitState guard (UIStateGeoscapeEvent.cs:61-65 completes a still-Triggered event
    ///     locally) then sees the resolved state and stays silent.
    /// Raise gate: ≥2 choices only. Single-choice events auto-complete host-side at trigger
    /// (GeoscapeEventSystem.OnEventTriggered:651) so their record arrives already resolved — no pending
    /// decision to mirror. Marketplace events open a whole trade screen (UIStateMarketplaceGeoscapeEvent)
    /// — skipped for now, logged once.
    /// Client choice resolution is FORBIDDEN (host-authoritative): the lock patches below swallow choice
    /// clicks and Esc while the record is unresolved. Relaying the client's pick host-ward through
    /// IntentRail is the follow-up, not this batch.
    /// </summary>
    internal static class EventPopup
    {
        private struct Seen { public GeoscapeEventRecordState State; public int Count; }

        private static readonly Dictionary<string, Seen> _latch = new Dictionary<string, Seen>(StringComparer.Ordinal);
        private static bool _seeded;
        private static readonly HashSet<string> _loggedSkips = new HashSet<string>(StringComparer.Ordinal);

        private static readonly System.Reflection.FieldInfo RecordsField =
            AccessTools.Field(typeof(GeoscapeEventSystem), "_records");                 // GeoscapeEventSystem.cs:92
        private static readonly System.Reflection.FieldInfo SwitchQueryField =
            AccessTools.Field(typeof(GeoscapeView), "_viewSwichQuery");                  // GeoscapeView.cs:138 (game typo)

        /// <summary>Reload/session boundary: forget the latch; the first Sync after it re-seeds SILENTLY
        /// (the transferred save carries the whole campaign's records — not a popup storm).</summary>
        public static void Reset()
        {
            _latch.Clear();
            _seeded = false;
        }

        /// <summary>Client-only by construction (called from UiEventMap.Fire, which only runs in the
        /// GenericApplier apply path). Diffs live records vs the latch, raises/dismisses, re-latches.</summary>
        public static void Sync(GeoscapeEventSystem es, GeoLevelController geo)
        {
            if (!(RecordsField?.GetValue(es) is Dictionary<string, GeoscapeEventRecord> records)) return;
            if (!_seeded)
            {
                foreach (var kv in records) _latch[kv.Key] = new Seen { State = kv.Value.State, Count = kv.Value.TriggerCount };
                _seeded = true;
                return;
            }
            foreach (var kv in records)
            {
                var rec = kv.Value;
                bool had = _latch.TryGetValue(kv.Key, out var prev);
                if (rec.State == GeoscapeEventRecordState.Triggered &&
                    (!had || prev.State != GeoscapeEventRecordState.Triggered || rec.TriggerCount > prev.Count))
                    Raise(es, geo, kv.Key, rec);
                else if (had && prev.State == GeoscapeEventRecordState.Triggered &&
                         rec.State != GeoscapeEventRecordState.Triggered)
                    Dismiss(geo, kv.Key, rec);
                _latch[kv.Key] = new Seen { State = rec.State, Count = rec.TriggerCount };
            }
        }

        private static void Raise(GeoscapeEventSystem es, GeoLevelController geo, string eventId, GeoscapeEventRecord rec)
        {
            try
            {
                var data = es.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;
                if (data == null)
                { if (_loggedSkips.Add(eventId)) Debug.Log("[Multiplayer][rail] EventPopup: no def for '" + eventId + "' — not mirrored"); return; }
                if (es.IsEventTheMarketplace(data))
                { if (_loggedSkips.Add(eventId)) Debug.Log("[Multiplayer][rail] EventPopup: marketplace event '" + eventId + "' — popup mirror not wired yet"); return; }
                if (data.Choices == null || data.Choices.Count < 2)
                { if (_loggedSkips.Add(eventId)) Debug.Log("[Multiplayer][rail] EventPopup: '" + eventId + "' has <2 choices (host auto-resolves) — no pending decision to mirror"); return; }
                var view = geo.View;
                if (view == null || !(SwitchQueryField?.GetValue(view) is GeoscapeViewSwitchQuery q)) return;
                // Same synthetic-context shape the game uses for its own re-entry (GeoscapeView.
                // ToMarketplace:735-738); the site may legitimately be null for site-less events.
                var geoEvent = new GeoscapeEvent(data, new GeoscapeEventContext(es.FindEventLocation(eventId), geo.ViewerFaction))
                { Record = rec };
                q.QueryStateSwitch(new GeoscapeViewStateSwitchRequest(new UIStateGeoscapeEvent(geoEvent))
                { PauseGame = false }); // pause mirrors from the host via the TimeAnchor
                Debug.Log("[Multiplayer][rail] EventPopup: raised '" + eventId + "' (triggerCount=" + rec.TriggerCount + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] EventPopup raise '" + eventId + "' failed: " + ex.Message); }
        }

        private static void Dismiss(GeoLevelController geo, string eventId, GeoscapeEventRecord liveRec)
        {
            try
            {
                if (!(geo.View?.CurrentViewState is UIStateGeoscapeEvent st) || st.Event?.EventID != eventId) return;
                st.Event.Record = liveRec; // the dialog holds the pre-apply record instance — refresh so the ExitState guard reads the RESOLVED state
                geo.View.FinishQueriedState();
                Debug.Log("[Multiplayer][rail] EventPopup: dismissed '" + eventId + "' (host resolved → " + liveRec.State + ")");
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][rail] EventPopup dismiss '" + eventId + "' failed: " + ex.Message); }
        }

        internal static bool IsClient
        {
            get
            {
                var e = NetworkEngine.Instance;
                return e != null && e.IsActiveSession && !e.IsHost;
            }
        }

        /// <summary>The live (post-apply) record for an event id, falling back to the dialog's own ref.</summary>
        internal static GeoscapeEventRecord LiveRecord(string eventId, GeoscapeEventRecord fallback)
        {
            try
            {
                var geo = Base.Core.GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
                return geo?.EventSystem?.GetEventRecord(eventId) ?? fallback;
            }
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

        private static bool Prefix(UIModuleSiteEncounters __instance)
        {
            if (!EventPopup.IsClient) return true;
            if (PagingField != null && (bool)(PagingField.GetValue(__instance) ?? false)) return true;
            var ev = GeoEventField?.GetValue(__instance) as GeoscapeEvent;
            if (string.IsNullOrEmpty(ev?.EventID)) return true; // not a mirrored host event
            Debug.Log("[Multiplayer][rail] EventPopup: choice click swallowed on client for '" + ev.EventID + "' — the host decides");
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
