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
    /// re-emit of <see cref="RecordScope"/> + the reject nudge, after which that peer's
    /// open picker is closed by the repaint (<see cref="EventPopup.RepaintDialog"/>) — the record delta IS
    /// the dismiss signal, so this family ships no dismiss message.
    ///
    /// Why the record and not the instance: <c>GeoscapeEvent.IsCompleted</c> is PER-INSTANCE
    /// (GeoscapeEvent.cs:36) and this handler may have to SYNTHESISE an instance (the host's own view
    /// need not hold a dialog for the id a client answered). A fresh instance over an already-resolved
    /// record reports "not completed", sails past CompleteEvent's own guard (:88) and re-grants the
    /// entire reward. The record check is the only real guard, so it runs BEFORE the instance exists.
    /// </summary>
    public static class EventSync
    {
        internal const byte OpAnswer = 1;       // [eventId:string][choiceIndex:i32]
        internal const byte OpSetVariable = 2;  // [name:string][value:i32]

        /// <summary>The forced re-emit scope for a rejected answer — the "ES" ROOT, not a per-record path.
        /// <c>EncounterRecords</c> classifies <see cref="FieldClass.EntityList"/> (docs/rail-baseline.txt:470),
        /// so the whole ledger rides as ONE canonical blob at its OWNER's path (DiffEngine.AddEntityListEntry)
        /// and no per-record path exists to narrow to; the record's subtree IS the root. This used to read
        /// <c>"ES.EncounterRecords#" + eventId</c> — the <c>&lt;path&gt;.&lt;Field&gt;#&lt;key&gt;</c> form the
        /// walk emits ONLY for EntityCollection fields (DiffEngine.cs:919) — so every reject re-emitted
        /// NOTHING and the losing peer's picker stayed open forever, with no log line. RailCheck L57 pins both
        /// halves against the live classifier; DiffEngine now logs any forced scope that matches zero paths.</summary>
        /// (static readonly, not const: RailCheck L57 compares it against the root key it re-derives, and a
        /// const would constant-fold that comparison away at compile time — a law that cannot fail.)
        internal static readonly string RecordScope = "ES";

        internal static void RegisterIntents()
        {
            var ops = new Dictionary<byte, IntentRail.OpHandler>
            {
                [OpAnswer] = HandleAnswer,
                [OpSetVariable] = HandleSetVariable,
            };
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

        /// <summary>Why a relayed variable write changes nothing, or null when it must be applied. PURE so
        /// the harness can execute both corners (RailCheck L333), and NAMED so the no-op cannot go back to
        /// being a bare <c>return</c>: an intent that reaches the host and leaves no line behind is
        /// indistinguishable from one that was lost in transit, and that ambiguity cost a whole cross-peer
        /// log sweep on 2026-08-08 chasing six "vanished" intents that had in fact all applied.
        ///
        /// <paramref name="current"/> is read with <c>int.MinValue</c> as the absent-key sentinel
        /// (GeoscapeEventSystem.GetVariable:270-277), so a first write of 0 to a variable nobody has set is
        /// a real change and lands — comparing against 0 would silently drop every "clear this flag" on a
        /// fresh campaign. The reason is never blank: a swallow with a return value is still a swallow.</summary>
        internal static string NoOpReason(int current, int value)
        {
            return current == value ? "the host already has " + value + " — no state to change" : null;
        }

        // ─── HOST: apply through the SAME native funnel (dedup/decode/reject = IntentRail) ─────

        private static void HandleAnswer(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string eventId = r.ReadString();
            int index = r.ReadInt32();

            var geo = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>();
            var es = geo?.EventSystem;
            if (es == null)
            { IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "no geoscape for answer to '" + eventId + "'"); return; }

            var rec = es.GetEventRecord(eventId);                                   // GeoscapeEventSystem.cs:313
            var data = es.GetEventByID(eventId, canFail: true)?.GeoscapeEventData;  // :280
            if (rec == null || data == null)
            {
                IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "no " + (rec == null ? "record" : "def") +
                                  " for event '" + eventId + "' on the host", RecordScope);
                return;
            }
            string why = Validate(rec.State, index, data.Choices == null ? 0 : data.Choices.Count);
            // NOTIFY: the popup this peer answered is already gone from its screen, and the only reason its
            // answer can die is that ANOTHER peer answered first (record state). Vanilla has no control to
            // grey here — single-player cannot lose a race for an event — so without a word the player just
            // watched a choice evaporate.
            if (why != null)
            { IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "event '" + eventId + "': " + why,
                                true /* notify */, RecordScope); return; }

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

            // Native tail (:604-613), BOTH HALVES. WHICH peer plays the mission is tactical scope (law 5);
            // dropping the mission silently is not an option — that is the swallow class.
            //
            // :611 BEFORE :612, and the order is the whole point (RailCheck L144). LaunchMission only QUEUES
            // the squad screen, and that queue is dead while this peer's OWN encounter window is the current
            // queried state switch — so a tail that copied only :612 left the losing host staring at a stale
            // picker with a deployment request behind it that nothing would ever serve. Native never separates
            // the two: UIModuleSiteEncounters.SelectChoice:611-612 is FinishEncounter(); LaunchMission(…).
            var mission = reward?.ApplyResult?.StartMission;
            if (mission == null) return;
            if (geo.View == null)
                Debug.LogWarning("[MP][events] '" + eventId + "' generated a mission but there is no GeoscapeView to launch it");
            else
            {
                MissionEncounterNav.FinishOwnEncounterWindow(geo.View, eventId, true);   // :611
                geo.View.LaunchMission(mission, ev.Context.Vehicle);                     // :612
            }
        }

        /// <summary>HOST: replay the client's variable write through the game's OWN setter. NEVER the
        /// backing dictionary — <c>SetVariable</c>:261 raises <c>VariableSet</c>, which is the notification
        /// anything listening (the game's own trigger evaluation, a mod's UI) is subscribed to, and the
        /// mirrored apply on the other side writes the dict RAW precisely because it must not re-raise it.
        /// The result travels back to every peer as ordinary <c>CustomVariables</c> deltas — this surface
        /// ships no host→all message and needs none.
        ///
        /// NO VALIDATOR, deliberately, and the same posture as the aim-pose intent (0x88): a variable write
        /// spends nothing, orders nothing and names no entity, so there is no host-side fact that could
        /// refuse it and arrival order IS the arbitration (law 7). The equality early-out is not a guard, it
        /// is noise control: a no-op write still logs and still re-raises.</summary>
        private static void HandleSetVariable(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            string name = r.ReadString();
            int value = r.ReadInt32();

            var es = GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>()?.EventSystem;
            if (es == null)
            { IntentRail.Reject(SurfaceIds.GeoEventIntent, senderPeerId, "no geoscape for variable '" + name + "'"); return; }
            string noop = NoOpReason(es.GetVariable(name, int.MinValue), value);
            if (noop != null)
            {
                Debug.Log("[MP][events] HOST variable '" + name + "'=" + value + " NOT applied — " + noop +
                          " nonce=" + nonce + " peer=" + senderPeerId);
                return;
            }

            es.SetVariable(name, value);   // GeoscapeEventSystem.cs:251 — the game's own setter
            Debug.Log("[MP][events] HOST variable '" + name + "'=" + value + " nonce=" + nonce + " peer=" + senderPeerId);
        }

        // ─── LOCAL PREFERENCE: an event variable that is a VIEW SETTING, not campaign state ─────

        /// <summary>Event variables that name what THIS player is looking at rather than what the campaign
        /// is. They are excluded from the rail on EVERY peer — host, client and apply alike — because the
        /// exclusion only works if the key never enters <c>_customVariables</c> ANYWHERE: the rail carries
        /// the whole dictionary as <c>CustomVariables</c> (docs/rail-baseline.txt:473) and the apply writes
        /// it RAW, so a key present on the host is a delta no per-peer filter downstream could undo.
        ///
        /// <c>TFTV_HelmetsOff</c> is TFTV's helmet toggle (ShowWithoutHelmet.cs:33 <c>HelmetsOff</c>,
        /// persisted :116, read :98). It is CAMPAIGN-GLOBAL by TFTV's own design — there is no per-character
        /// helmet state to sync instead — so replicating it meant one peer's click restyled every soldier on
        /// every screen, including the soldier another peer had open. <c>UiEventMap</c>:242-250 already
        /// declared that which helmet a player looks at is that player's own view; this seam is what makes
        /// the declaration true. The mixed appearance that remains is TFTV forcing helmets ON for
        /// mutoid/augmented-head characters (:221-236, :296-310), not a sync artefact.
        ///
        /// AND IT KILLS THE FLIP-FLOP, which was not a second bug: <see cref="EventVariableCapture"/>'s
        /// equality early-out reads the LIVE dictionary, which a blocked write never updates, so TFTV
        /// re-read the stale value (:88-105), re-applied it (:138) and its own postfix (:264) wrote again —
        /// forever. The shadow below is the value TFTV now reads back, so the second write is equal and
        /// stops.</summary>
        internal static readonly HashSet<string> LocalPreferenceVariables =
            new HashSet<string>(StringComparer.Ordinal) { "TFTV_HelmetsOff" };

        /// <summary>Per-process, deliberately NOT saved: a view setting that survived into a save file would
        /// be shipped with it on the next transfer, which is the thing this seam exists to prevent.</summary>
        private static readonly Dictionary<string, int> _localPreferences = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>TRUE = handled locally, the caller must ship NOTHING and run NO native write.</summary>
        internal static bool TrySetLocalPreference(string variable, int value)
        {
            if (variable == null || !LocalPreferenceVariables.Contains(variable)) return false;
            int had;
            bool changed = !_localPreferences.TryGetValue(variable, out had) || had != value;
            _localPreferences[variable] = value;
            if (changed)
                Debug.Log("[MP][events] local preference '" + variable + "'=" + value +
                          " kept off the rail — this is a per-player view setting, not campaign state");
            return true;
        }

        /// <summary>TRUE = answer with <paramref name="value"/> instead of the replicated dictionary. A
        /// listed name this process has never written falls through to native, which returns the caller's
        /// own default — the same answer an untouched campaign would give.</summary>
        internal static bool TryGetLocalPreference(string variable, out int value)
        {
            value = 0;
            return variable != null && LocalPreferenceVariables.Contains(variable) &&
                   _localPreferences.TryGetValue(variable, out value);
        }
    }

    /// <summary>
    /// THE CLIENT HALF OF THE EVENT-VARIABLE GAP, and a gap CLASS rather than one feature.
    /// <c>GeoscapeEventSystem._customVariables</c> is rail-covered campaign state
    /// (docs/rail-baseline.txt:473, <c>EntityList CustomVariables</c>) and therefore HOST-AUTHORITATIVE —
    /// but until now nothing in the mod ever sent a <c>SetVariable</c> anywhere. A client writing one wrote
    /// it into its own dictionary, shipped nothing, and had it silently reverted by the next mirrored diff:
    /// the apply writes the dict RAW, so the revert produced no exception and no log line. That is the
    /// silent-swallow class this repo exists to kill, and it is not specific to any one variable — every
    /// mod that stores a campaign flag here was broken in the same way and is fixed by this one seam.
    ///
    /// BLOCK-FIRST, like every other geoscape family and for the same reason (law 3 + RailCheck L19): this
    /// is a MODEL method, so a postfix would have let the client's write land first and shipped a RESULT —
    /// which is precisely the state the next mirrored diff silently reverts. <see cref="IntentRail
    /// .ShouldRunNative"/> carries the three arms verbatim: solo runs native (the write IS the truth), an
    /// APPLY runs native (that is the mirror landing — and it must never bounce back at the host, once per
    /// batch, forever), the HOST runs native (its own write IS the shared state, and the generic diff
    /// already carries it to everyone).
    ///
    /// LOCAL FEEDBACK IS NOT LOST BY BLOCKING, because a caller that stores a preference here keeps its own
    /// in-memory copy and repaints off that; what the block removes is only the divergent CAMPAIGN value.
    /// The authoritative one lands a round trip later as an ordinary <c>CustomVariables</c> delta and
    /// repaints again through the same universal seam.
    ///
    /// THE EQUALITY CHECK IS NOT A GUARD, it is noise control, and it reads the LIVE dictionary rather than
    /// a remembered value (<see cref="int.MinValue"/> = the absent-key sentinel of the game's own
    /// <c>GetVariable(string,int)</c>:270). An unchanged write is the COMMON case, not a rare one — a
    /// screen that re-applies its stored value on every repaint writes it back unchanged — and shipping
    /// those turns one gesture into a stream of identical intents.
    ///
    /// NO VALIDATOR AND NO REJECT PATH is the deliberate posture, so nothing needs reconverging when the
    /// host declines: it never declines. See <see cref="EventSync"/>'s handler for why.
    ///
    /// GENERALITY VERIFIED, not assumed: the game's own callers of this setter are
    /// <c>GeoFactionReward.Apply</c>:199, <c>EncounterVarResearchReward</c>:22/:26 and
    /// <c>GeoMarketplace</c>:163/:173. None of them can reach this on a client — event completion is
    /// refused by <see cref="EventCompleteArbiter"/>, the marketplace one by
    /// <see cref="MarketplaceCompleteArbiter"/>, and research rewards hang off the hourly sim, which
    /// <c>ClientSimGate</c> does not run on a client at all. So what remains on a client is a live gesture,
    /// which is exactly what this ships — and blocking those three costs nothing even if one ever did
    /// reach here, since all three are host-authoritative writes a client has no business making.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEventSystem), nameof(GeoscapeEventSystem.SetVariable),
                  new[] { typeof(string), typeof(int) })]
    internal static class EventVariableCapture
    {
        private static bool Prefix(GeoscapeEventSystem __instance, string variable, int value)
        {
            // BEFORE the peer-role decision, deliberately: a view setting is not campaign state on ANY peer,
            // and letting the host (or a solo run that later hosts) write it into _customVariables is exactly
            // what puts it on the wire. See EventSync.LocalPreferenceVariables.
            if (EventSync.TrySetLocalPreference(variable, value)) return false;
            if (IntentRail.ShouldRunNative()) return true;
            if (__instance.GetVariable(variable, int.MinValue) != value)
                IntentRail.Send(SurfaceIds.GeoEventIntent, EventSync.OpSetVariable,
                    "variable '" + variable + "'=" + value,
                    w => { w.Write(variable); w.Write(value); });
            return false;
        }
    }

    /// <summary>
    /// THE READ SIDE of the local-preference seam (<see cref="EventSync.LocalPreferenceVariables"/>): a
    /// listed name answers this process's OWN value, never the replicated dictionary.
    ///
    /// The 2-ARG overload is the only one patched, and it is the whole funnel:
    /// <c>GeoscapeEventSystem.GetVariable(string)</c>:265-268 is <c>return GetVariable(variable, 0);</c>,
    /// so every read in the game and in every mod lands here. Patching the 1-arg one as well would be the
    /// AccessTools exact-parameter-match trap wearing a second hat — two patches, one of them redundant,
    /// and the redundant one is the one that goes stale.
    ///
    /// Falling THROUGH for a listed name this process never wrote is deliberate: native returns the
    /// caller's default (:270-277), which is what an untouched campaign answers, so a fresh session shows
    /// the game's default rather than another peer's preference.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEventSystem), nameof(GeoscapeEventSystem.GetVariable),
                  new[] { typeof(string), typeof(int) })]
    internal static class EventVariableLocalRead
    {
        private static bool Prefix(string variable, ref int __result)
        {
            int local;
            if (!EventSync.TryGetLocalPreference(variable, out local)) return true;
            __result = local;
            return false;
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
        /// <summary>The arm EVERY funnel in the set shares: a CLIENT never resolves an event locally, not
        /// even an OPEN one (law 3). Single-sourced because the set is PLURAL — <c>GeoscapeEvent</c> has
        /// two completion funnels (see <see cref="MarketplaceCompleteArbiter"/>) — so a funnel added to
        /// the class inherits the decision instead of a hand-written copy of it. <paramref name="reach"/>
        /// is the funnel's own answer to "then what still reaches it here?", because that differs per
        /// funnel and a refusal line must say something true. null = let native run.</summary>
        internal static string ClientRefusal(string reach)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;      // solo: nobody else can claim it
            if (engine.IsHost || SyncApplyScope.Active) return null;         // an apply may legitimately reach it
            return "a client never resolves an event locally — " + reach;
        }

        private static bool Prefix(GeoscapeEvent __instance, ref GeoFactionReward __result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;      // solo: nobody else can claim it
            if (string.IsNullOrEmpty(__instance?.EventID)) return true;      // synthetic closing page (SetClosingEncounter:326-331)
            var rec = EventPopup.LiveRecord(__instance.EventID, __instance.Record);

            // The client arm's reach: its own clicks are already blocked one layer up
            // (EventChoiceClientLock), so what still gets here on a client is a dialog TEARDOWN with no
            // user in it: UIStateGeoscapeEvent.ExitState:61-65 completes a still-Triggered event with
            // Choices.Last() and throws the reward away, reached by the universal repaint's fallback
            // Exit+Enter (OpenUiRepaint.cs:189-206). B3 makes that structurally unreachable with a
            // UiNativeRepaint entry for the screen; until then this is the only thing between a repaint
            // and a client-side grant of a choice nobody picked.
            string why = ClientRefusal("this funnel is only reachable from a dialog teardown")
                ?? (rec == null || rec.State == GeoscapeEventRecordState.Triggered
                    ? null
                    : "the record is " + rec.State + " (choice " + rec.SelectedChoice + ") — the first answer is frozen");
            if (why == null) return true;

            EventPopup.MarkResolvedInstance(__instance);
            __result = __instance.ChoiceReward;
            Debug.Log("[MP][events] CompleteEvent for '" + __instance.EventID + "' skipped — " + why);
            return false;
        }
    }

    /// <summary>
    /// The same backstop at the class's OTHER funnel: <c>GeoscapeEvent.CompleteMarketplaceEvent</c>
    /// (GeoscapeEvent.cs:74). Same law, same shared decision (<see cref="EventCompleteArbiter.ClientRefusal"/>),
    /// separate patch class only because Harmony cannot inject <c>__result</c> into a prefix on a VOID
    /// method — <c>CompleteEvent</c> returns the reward its callers dereference, this one returns nothing.
    ///
    /// The record-freeze arm deliberately does NOT apply here: this funnel never writes the record
    /// (compare <c>CompleteEvent</c>:97/:114 — there is no <c>Record.SelectChoice</c>/<c>Complete</c> in
    /// :74-84), so the record is not its ledger and a freeze check would refuse legitimate HOST purchases
    /// — the marketplace is an N-purchase shop whose offers are consumed from
    /// <c>GeoMarketplace.MarketplaceChoices</c>, and path (a) below has no record at all while a
    /// system-raised one stays <c>Triggered</c> across every purchase.
    ///
    /// The client's own click is blocked a layer up (<see cref="MarketplaceChoiceClientLock"/>, which has
    /// to be block-first because <c>Wallet.Take</c> runs before this funnel). This is the backstop for the
    /// paths that layer cannot see, and it writes NOTHING on refusal: <c>ChoiceReward</c> stays null,
    /// which is exactly the state a marketplace instance is in before its first purchase, and
    /// <c>IsCompleted</c> must stay false or the window's own priority logic
    /// (GeoscapeView.cs:2049) reads a purchase as a closed event.
    /// </summary>
    [HarmonyPatch(typeof(GeoscapeEvent), nameof(GeoscapeEvent.CompleteMarketplaceEvent))]
    internal static class MarketplaceCompleteArbiter
    {
        private static bool Prefix(GeoscapeEvent __instance)
        {
            string why = EventCompleteArbiter.ClientRefusal(
                "the marketplace window is a purely LOCAL gesture (MarketplaceAbility.ActivateInternal:43 → " +
                "GeoscapeView.ToMarketplace:734), so nothing on the rail legitimately reaches it here");
            if (why == null) return true;
            Debug.Log("[MP][events] CompleteMarketplaceEvent for '" + __instance?.EventID + "' skipped — " + why);
            return false;
        }
    }
}
