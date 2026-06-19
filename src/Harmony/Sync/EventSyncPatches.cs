using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Network.Sync;
using Multipleer.Network.Sync.Actions;
using UnityEngine;

namespace Multipleer.Harmony.Sync
{
    /// <summary>
    /// Host-side broadcast interceptor + FIRST-CLICK-WINS gate for geoscape event choices:
    /// <c>GeoscapeEvent.CompleteEvent(GeoEventChoice choice, GeoFaction faction)</c> (GeoscapeEvent.cs:86).
    /// The host's choice is authoritative; on a host pick this broadcasts the confirmed answer (Postfix) and
    /// marks the research channel dirty for an instant reveal.
    ///
    /// FIRST-CLICK-WINS: CompleteEvent is the SINGLE chokepoint both a host's own click and a client-relayed
    /// answer converge on (client: SyncEngine.OnActionRequest → EventReflection.TryHostNativeResolve drives the
    /// host's own native OnChoiceSelected → SelectChoice → CompleteEvent; or the fallback
    /// CompleteEventByOccurrence → CompleteEvent). The Prefix below claims the per-occurrence id on the shared
    /// <c>SyncEngine.Arbiter</c>: the FIRST claim proceeds (one RNG roll, one EventDismiss); a near-simultaneous
    /// LOSER skips native CompleteEvent entirely (no second roll/broadcast/throw). Because the winner's own native
    /// completion already advanced the host's open modal to the result page, a losing host click is dropped
    /// harmlessly.
    ///
    /// CLIENT relay is now DEAD/DORMANT: under the two-class dialog model
    /// (<see cref="EventDialogClientGuard"/>) the client never reaches <c>CompleteEvent</c> — its choice
    /// buttons are inert for CHOICE events and INFO dismiss is a pure local hide (no outcome), and
    /// <c>UIModuleSiteEncounters.SelectChoice</c> is short-circuited on the client. The non-host branch below
    /// therefore never executes in practice; it is kept ONLY as a fail-safe that BLOCKS a local client
    /// CompleteEvent (returns false) and does NOT relay an answer. The host path is untouched.
    /// </summary>
    [HarmonyPatch]
    public static class CompleteEventPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoscapeEvent");
            var choiceT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Events.GeoEventChoice");
            var facT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoFaction");
            if (t == null || choiceT == null || facT == null) return false;
            _target = AccessTools.Method(t, "CompleteEvent", new[] { choiceT, facT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = GeoscapeEvent; choice = the chosen GeoEventChoice (may be null = decline).
        // __state carries the host action to broadcast AFTER the original completes successfully (Postfix).
        public static bool Prefix(object __instance, object choice, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;

            // PERMISSION (user directive): event choices are NOT permission-gated for now — everyone may click,
            // last-write-wins (permission system deferred; PermissionGate code kept for re-enable). The prior
            // PermissionGate.Check(Dialogs) gate here could block + return false, freezing a non-permitted player's
            // click. Removed; Validate + native IsCompleted self-guard still protect correctness.

            try
            {
                string eventId = EventReflection.GetEventId(__instance);
                int choiceIndex = EventReflection.GetChoiceIndex(__instance, choice);
                if (string.IsNullOrEmpty(eventId)) return true;
                // Could not resolve a real (non-null) choice → do NOT broadcast a bogus decline (-1).
                // Fail OPEN to local vanilla handling instead of replicating the wrong outcome.
                if (choiceIndex == EventReflection.ChoiceLookupFailed) return true;
                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                // occId rides the action wire now (AnswerEventAction(occId, eventId, choiceIndex)); read it off
                // THIS live instance (order-independent GetOrAssign). The client suppresses this echo
                // (IHostOnlyApply) — its real result arrives via the EventDismiss broadcast — so the occId here
                // only needs to keep the action well-formed.
                if (engine.IsHost)
                {
                    ushort occId = EventOccurrenceIds.GetOrAssign(__instance);
                    // FIRST-CLICK-WINS gate. CompleteEvent is the universal host chokepoint both a host's own
                    // click AND a client-relayed answer (TryHostNativeResolve drives the same native
                    // OnChoiceSelected → CompleteEvent) converge on. Claim the occurrence: the FIRST claim wins
                    // (proceeds through native CompleteEvent → one RNG roll → Postfix broadcasts exactly ONE
                    // EventDismiss). A LOST claim is a near-simultaneous double — SKIP native CompleteEvent
                    // (no second roll, no second dismiss broadcast, and no native IsCompleted-throw); the winner's
                    // own native completion already advanced the host modal to the result page (TryHostNativeResolve
                    // drives the host's live module either way), so the loser is harmlessly dropped. Silent (no
                    // error dialog). occId 0 (unresolvable) is never claimed → fail OPEN to native (single-resolve).
                    var arbiter = engine.Sync?.Arbiter;
                    if (occId != 0 && arbiter != null && !arbiter.Claim(occId))
                    {
                        Debug.Log("[Multipleer] CompleteEventPatch first-wins LOST occId=" + occId + " eventId=" + eventId +
                                  " → skip native CompleteEvent (winner already resolved this occurrence)");
                        __state = null;
                        return false;
                    }
                    __state = new AnswerEventAction(occId, eventId, choiceIndex);
                    return true;
                }
                // Client: DEAD path under the two-class model — never reached (client choice buttons are inert
                // / INFO is a pure local hide / SelectChoice is short-circuited). Kept only as a fail-safe:
                // block any stray local CompleteEvent and do NOT relay an answer (host owns every outcome).
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer] CompleteEventPatch failed: " + ex.Message);
                return true;
            }
        }

        // Runs only if the original CompleteEvent returned normally (Harmony skips Postfix on a thrown
        // original) → broadcast the host's confirmed answer. Host-only via __state being set in Prefix.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try
            {
                NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state);
                // TASK1 — instant event-driven research reveal (host-LOCAL answer path): the host's event
                // choice can REVEAL research (FIX#2 ch2 carries Research.Visible), but an event answer fires
                // no research event to self-mark ch2, so the reveal otherwise waited for the next in-game
                // HourTicked (frozen while the event UI pauses the clock). Mark ch2 dirty so the reveal ships
                // on the next real-time Tick flush. Host-only (__state is set in Prefix only when IsHost);
                // idempotent reconcile. The client-relayed answer path is covered in SyncEngine.OnActionRequest.
                NetworkEngine.Instance?.Sync?.MarkChannelDirty(2);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] CompleteEventPatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
