using System;
using System.Collections.Generic;
using System.IO;
using Base.Core;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Events;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Geoscape EVENT-WINDOW answer family (surface 0xB4, law 1): the client's choice click is BLOCKED
    /// at its presentation seam (<see cref="EventChoiceClientLock"/>, block-first per
    /// <see cref="IntentRail.ShouldRunNative"/>) and relayed here as
    /// <c>answer(eventId, choiceIndex)</c>; the HOST runs the same native funnel the local click would
    /// have — <c>GeoscapeEvent.CompleteEvent</c> (GeoscapeEvent.cs:86) — and the outcome reaches every
    /// peer as ordinary 0xAC deltas: the record's own leaves (<c>_state</c>/<c>_selectedChoice</c>/
    /// <c>_completedAt</c>, docs/rail-baseline.txt:240-247) plus whatever roots the reward touched
    /// (wallet, sites, diplomacy, soldiers). Intent-only surface — no host→all event message exists.
    ///
    /// FIRST-CHOICE-WINS, and the ledger is REPLICATED STATE, not a claim table: an answer is accepted
    /// only while <c>GeoscapeEventRecord.State == Triggered</c> (GeoscapeEventRecord.cs:34, flipped to
    /// SelectedChoice/Completed inside CompleteEvent at :97/:114). v1 kept an in-memory arbiter keyed by
    /// occurrence id, which every reload invalidated; a record survives save/load and the save transfer,
    /// so there is nothing to reset. Two peers answering within one RTT is the NORMAL case, not an
    /// error: the second is REJECTED (never thrown) and reconverged by IntentRail.Reject's scoped
    /// re-emit of <c>ES.EncounterRecords#&lt;eventId&gt;</c> + the reject nudge, after which that peer's
    /// open picker flips to outcome mode (EventPopup.FlipResolvedOpenWindow).
    ///
    /// Why the record and not the instance: <c>GeoscapeEvent.IsCompleted</c> is PER-INSTANCE
    /// (GeoscapeEvent.cs:36) and this handler may have to SYNTHESISE an instance (the host's own view
    /// need not hold a dialog for the id a client answered). A fresh instance over an already-resolved
    /// record reports "not completed", sails past CompleteEvent's own guard (:88) and re-grants the
    /// entire reward. The record check is the only real guard, so it runs BEFORE the instance exists.
    /// </summary>
    public static class EventSync
    {
        internal const byte OpAnswer = 1;  // [eventId:string][choiceIndex:i32]

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler> { [OpAnswer] = HandleAnswer };
            IntentRail.Register(SurfaceIds.GeoEventIntent, "event", ops);
        }

        /// <summary>The whole acceptance decision, as a PURE function of the replicated ledger and the
        /// host's own def: null = accept, otherwise the human reason a peer's answer was refused. Pure so
        /// the race it arbitrates is testable headless — RailCheck L27 calls it directly (an in-game-only
        /// arbiter is how v1's double-grants stayed invisible for a month). A reason is never blank: a
        /// silently eaten click is the bug class this family exists to kill.</summary>
        internal static string Validate(GeoscapeEventRecordState state, int choiceIndex, int choiceCount)
        {
            if (state != GeoscapeEventRecordState.Triggered)
                return "already answered (record=" + state + ") — the first choice is frozen for everyone";
            if (choiceIndex < -1 || choiceIndex >= choiceCount)
                return "choice index " + choiceIndex + " outside [-1," + choiceCount + ") — stale mirror or def mismatch";
            return null;
        }

        // ─── HOST: apply through the SAME native funnel (dedup/decode/reject = IntentRail) ─────

        private static void HandleAnswer(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string eventId = r.ReadString();
            int index = r.ReadInt32();
            string scope = "ES.EncounterRecords#" + eventId; // DiffEngine.cs:811 keyed-element path form

            var geo = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
            var es = geo?.EventSystem;
            if (es == null)
            { IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "no geoscape for answer to '" + eventId + "'"); return; }

            var rec = es.GetEventRecord(eventId);                                   // GeoscapeEventSystem.cs:313
            var data = es.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;  // :280
            if (rec == null || data == null)
            {
                IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "no " + (rec == null ? "record" : "def") +
                                  " for event '" + eventId + "' on the host", scope);
                return;
            }
            string why = Validate(rec.State, index, data.Choices == null ? 0 : data.Choices.Count);
            if (why != null)
            { IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "event '" + eventId + "': " + why, scope); return; }

            // Prefer the host's OWN live instance: it carries the real Context (site + vehicle) the reward
            // and any mission launch are applied against. Otherwise synthesise the same shape the game
            // uses for its own re-entry (GeoscapeView.ToMarketplace:735-738) — the site is legitimately
            // null for site-less events.
            var ev = EventPopup.LiveInstance(geo.View, eventId)
                     ?? new GeoscapeEvent(data, new GeoscapeEventContext(es.FindEventLocation(eventId), geo.ViewerFaction)) { Record = rec };
            var choice = index < 0 ? null : data.Choices[index];
            var faction = geo.ViewerFaction;

            // The UI layer's own charge (UIModuleSiteEncounters.cs:571-573) — the client must never pay
            // locally (law 3), so the winner's cost comes out of the host's wallet and rides back as a delta.
            if (choice != null && choice.Requirments != null)
                faction.Wallet.Take(choice.Requirments.Resources, OperationReason.Gift);

            // A choice with no Outcome resolves the event with NO choice: that is what native does
            // (:562-566 CompleteEvent(null)) and it is mandatory, because CompleteEvent dereferences
            // choice.Outcome unguarded (GeoscapeEvent.cs:101). Native's one degenerate sub-case — cost but
            // no outcome — takes the payment and returns WITHOUT completing (:571-582); we complete
            // anyway, because a record left Triggered is a window no peer can ever close.
            var reward = ev.CompleteEvent(choice != null && choice.Outcome == null ? null : choice, faction);
            Debug.Log("[MP][events] HOST answered '" + eventId + "' choice=" + index + " → record=" + rec.State +
                      " selected=" + rec.SelectedChoice + " nonce=" + nonce + " peer=" + senderPeerId);

            // Native tail (:604-613). WHICH peer plays the mission is tactical scope (law 5); dropping the
            // mission silently is not an option — that is the swallow class.
            var mission = reward?.ApplyResult?.StartMission;
            if (mission == null) return;
            if (geo.View == null)
                Debug.LogWarning("[MP][events] '" + eventId + "' generated a mission but there is no GeoscapeView to launch it");
            else
                geo.View.LaunchMission(mission, ev.Context.Vehicle);
        }
    }

    /// <summary>
    /// THE first-choice-wins backstop, at the model funnel every resolution passes through:
    /// <c>GeoscapeEvent.CompleteEvent</c> (GeoscapeEvent.cs:86). A host-local click
    /// (OnChoiceSelected → SelectChoice:602), a relayed answer (<see cref="EventSync"/>) and a dialog
    /// TEARDOWN (<c>UIStateGeoscapeEvent.ExitState</c>:61-65 completes a still-open event with
    /// <c>Choices.Last()</c>) all land here — the last of those is reached by Esc and by the universal
    /// repaint's fallback Exit+Enter, i.e. by no gesture at all.
    ///
    /// Refuse whenever the replicated record says the decision is no longer open — and, on a client,
    /// refuse unconditionally (law 3). The instance's own
    /// <c>IsCompleted</c> guard (:88) cannot do this job: it is PER-INSTANCE (:36), so a freshly built
    /// instance over a resolved record re-runs <c>GenerateFactionReward</c> + <c>ChoiceReward.Apply</c>
    /// and grants everything a second time — wallet, sites, diplomacy, and created soldiers
    /// (GeoEventChoiceOutcome:296/305). Refusal is not a bare skip: <c>__result</c> and the instance's
    /// <c>ChoiceReward</c> get the empty stub, because <c>SelectChoice</c>:604 and
    /// <c>SetClosingEncounter</c>:357 dereference it unguarded, and <c>HasRewards()</c>==false on the stub
    /// (GeoFactionRewardApplyResult.cs:69) makes the native page render outcome TEXT only.
    /// Solo is untouched, and so is the host's normal path: the auto-complete at trigger runs while the
    /// record is Triggered (GeoscapeEventSystem.cs:648-655).
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEvent), nameof(GeoscapeEvent.CompleteEvent))]
    internal static class EventCompleteArbiter
    {
        private static bool Prefix(GeoscapeEvent __instance, ref GeoFactionReward __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;      // solo: nobody else can claim it
            if (string.IsNullOrEmpty(__instance?.EventID)) return true;      // synthetic closing page (SetClosingEncounter:326-331)
            var rec = EventPopup.LiveRecord(__instance.EventID, __instance.Record);

            // A CLIENT never resolves an event locally, not even an OPEN one (law 3). Its own clicks are
            // already blocked one layer up (EventChoiceClientLock), so what still reaches this funnel on a
            // client is a dialog TEARDOWN with no user in it: UIStateGeoscapeEvent.ExitState:61-65
            // completes a still-Triggered event with Choices.Last() and throws the reward away, and it is
            // reached by the universal repaint's fallback Exit+Enter (OpenUiRepaint.cs:189-206). B3 makes
            // that structurally unreachable with a UiNativeRepaint entry for the screen; until then this
            // is the only thing between a repaint and a client-side grant of a choice nobody picked.
            string why = !engine.IsHost && !SyncApplyScope.Active
                ? "a client never resolves an event locally — this funnel is only reachable from a dialog teardown"
                : rec == null || rec.State == GeoscapeEventRecordState.Triggered
                    ? null
                    : "the record is " + rec.State + " (choice " + rec.SelectedChoice + ") — the first answer is frozen";
            if (why == null) return true;

            EventPopup.MarkResolvedInstance(__instance);
            __result = __instance.ChoiceReward;
            Debug.Log("[MP][events] CompleteEvent for '" + __instance.EventID + "' skipped — " + why);
            return false;
        }
    }
}
