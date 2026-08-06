using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Utils;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L135 — ONE PRODUCER PER MIRRORED WINDOW: A PEER THAT DID NOT AUTHOR THE SAVE RESTORES NONE OF ITS
    /// MIRRORED WINDOWS.
    ///
    /// THE REPORT (2026-08-06). A new co-op campaign starts. The three TFTV intro popups
    /// (<c>IntroBetterGeo_0/1/2</c>) and the campaign cutscene appear on the HOST, are answered there — and
    /// then appear AGAIN on every CLIENT. Duplicated, once per client, every time.
    ///
    /// TWO PRODUCERS FOR ONE WINDOW, and the second was the SAVEGAME. Measured write-order: the host raised
    /// the three events at 20:36:39.885-.887 (<c>EventPopup</c>:1258 <c>EventRaiseBroadcast</c> → :196
    /// <c>HostBroadcast</c>, surface 0xB6) and the new-campaign autosave ran 14 ms later at 20:36:39.899
    /// (<c>SaveTransferCoordinator</c>:1137 → :1194 <c>AutosaveGame</c>), while the host did not answer them
    /// until 20:36:53. The window QUEUE is part of the save (<c>GeoscapeViewSwitchQuery.GetRestorableData</c>
    /// :25 ← <c>GeoLevelController.RecordInstanceData</c>:415), so the transferred blob carried three queued
    /// <c>UIStateGeoscapeEvent</c>s, and <c>UIStateGeoscapeEvent.RestoreContext.RegenerateState</c> rebuilds
    /// them unconditionally. The SAME client also held the same three as 0xB6 raises and replayed them at
    /// reveal (20:36:57.309-.444) through <c>EventPopup</c>:511 <c>RaiseMirrored</c>, which never asked
    /// whether the queue already held a window for that <c>EventId</c>. Six teardowns against three raises
    /// per client; <c>multiplayer.log</c>:751 is the proof line — a <c>UIStateGeoscapeEvent</c> for
    /// <c>IntroBetterGeo_0</c> torn down 146 ms BEFORE the first mod raise at :795, and on a client that
    /// funnel is reachable only from <c>UIStateGeoscapeEvent.ExitState</c> (<c>EventSync</c>:191).
    ///
    /// WHY THREE EXISTING LAWS STAYED GREEN THROUGH IT, which is the reason this one exists at all:
    ///   L49 asks "no <c>ModalType</c> a native raiser opens may also be Mirrored" — <c>ModalType</c>/0xB7
    ///       scoped, and it derives producers from the GAME's IL raisers; the second producer here is the
    ///       save-restore path and the window is a <c>UIStateGeoscapeEvent</c> (0xB6).
    ///   L117 prunes restored entries by their <c>GeoMission</c> subject (<c>WindowQueueSync</c>:509
    ///       <c>SubjectMission</c>), so an event-carrying entry is never even examined.
    ///   L93 arms F/H (<c>L93_WindowOrderAndHistory</c>:222, :274) assert the carry EXISTS. Neither ever
    ///       asserts a COUNT, and the bug was entirely a count.
    ///
    /// THE OUTCOME THIS LAW ASSERTS, and the honest limit on it. The outcome wanted is "at most one live
    /// window per event per peer". A static harness cannot observe live windows, so it is asserted in its
    /// strongest feasible equivalent: on a peer that did not author the save, the RESTORE contributes ZERO
    /// entries for every window kind declared <c>WindowSync.Mirrored</c> — leaving the mirror surface as the
    /// single producer, hence exactly one window. Arms (A)/(B) EXECUTE the real shipped classifier
    /// (<c>RestoreDropsResolvedSubjects.KindIsMirrored</c>) over the real coverage table and over the real
    /// restorable context types in the GAME assembly; only (C)/(D) are structural.
    ///
    /// ARM (C) IS THE DEFERRAL HALF and it is load-bearing, not decoration. A peer that entered a mission
    /// with genuinely unseen windows must still get them back, and after this drop the ONLY thing that
    /// returns them is <c>EventPopup._unanswered</c> → <c>RequeueUnanswered</c>:460 → <c>_held</c> →
    /// <c>DrainHeldRaises</c>:432, driven by <c>ReplenishSync.CarryUnreadWindowsPatch</c>. That patch and
    /// this drop must read the SAME producer signal, or one peer silently loses windows the other half
    /// assumed it would re-carry — so the law asserts both gates ask <c>NetworkEngine.IsActiveSession</c> and
    /// <c>IsHost</c>, and that the re-carry still calls <c>RequeueUnanswered</c>.
    ///
    /// Falsify: make the classifier return true for a LocalOnly/Gap kind → <c>drop-reaches-a-kind-with-no-
    /// producer</c>; make it return false for a Mirrored kind (or key the modal family on the view-state
    /// type instead of the <c>ModalType</c> axis) → <c>mirrored-kind-survives-the-restore</c>; let a game
    /// restorable context stop being nested in its view state → <c>restore-key-not-total</c>; drop the
    /// producer gate from the prefix, or change either gate's signal → <c>producer-signal-not-shared</c>;
    /// remove the <c>RequeueUnanswered</c> call → <c>deferral-not-carried</c>; key the drop on
    /// <c>GeoscapeEventRecordState</c> → <c>drop-keyed-on-record-state</c> (these are single-choice events,
    /// already <c>Completed</c> at trigger, <c>GeoscapeEventSystem</c>:651-655 — the trap the 0xB6 design
    /// already documents).
    /// </summary>
    internal static class L135_OneProducerPerMirroredWindow
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var filter = typeof(RestoreDropsResolvedSubjects);
            var prefix = filter.GetMethod("Prefix", All);
            var producer = filter.GetMethod("RestoringAnotherPeersBlob", All);
            var verdict = filter.GetMethod("KindIsMirrored", All, null,
                                           new[] { typeof(Type), typeof(ModalType) }, null);

            var carry = typeof(ReplenishSync).GetNestedType("CarryUnreadWindowsPatch", All)
                                             ?.GetMethod("Postfix", All);
            var requeue = typeof(EventPopup).GetMethod("RequeueUnanswered", All);
            var isSession = typeof(NetworkEngine).GetProperty("IsActiveSession", All)?.GetGetMethod(true);
            var isHost = typeof(NetworkEngine).GetProperty("IsHost", All)?.GetGetMethod(true);
            var recordState = AccessToolsSafeType("PhoenixPoint.Geoscape.Levels.GeoscapeEventRecordState");

            if (prefix == null || producer == null || verdict == null || carry == null || requeue == null ||
                isSession == null || isHost == null)
            {
                yield return "L135 premise-changed: RestoreDropsResolvedSubjects.{Prefix," +
                             "RestoringAnotherPeersBlob,KindIsMirrored(Type,ModalType)}, or " +
                             "ReplenishSync.CarryUnreadWindowsPatch.Postfix / EventPopup.RequeueUnanswered, " +
                             "or NetworkEngine.{IsActiveSession,IsHost}, no longer resolves. The producer test " +
                             "and its deferral counterpart have moved and this law is asserting something " +
                             "about a shape they no longer have — re-read both before trusting the drop.";
                yield break;
            }

            // ── (A) EXECUTED: the verdict is TOTAL over the coverage table and reaches every Mirrored kind ──
            // Mirrored ⇒ dropped on a non-producer peer (the restore contributes nothing, so the mirror
            // surface stays the single producer). LocalOnly / Gap ⇒ kept, because they have NO producer and
            // dropping them would be a loss rather than a de-duplication.
            foreach (var kv in GeoWindowCoverage.Declared)
            {
                if (kv.Key == typeof(UIStateGeoModal)) continue;   // its verdict lives on the ModalType axis
                bool dropped = RestoreDropsResolvedSubjects.KindIsMirrored(kv.Key, default(ModalType));
                if (kv.Value.Sync == WindowSync.Mirrored && !dropped)
                    yield return "L135 mirrored-kind-survives-the-restore: '" + kv.Key.Name + "' is declared " +
                                 "Mirrored — the host's raise surface is its ONE producer — yet the restore " +
                                 "filter keeps it on a peer that did not author the save. That peer then holds " +
                                 "the raised copy AND the restored copy: two windows for one raise, which is " +
                                 "the campaign-intro duplicate verbatim.";
                if (kv.Value.Sync != WindowSync.Mirrored && dropped)
                    yield return "L135 drop-reaches-a-kind-with-no-producer: '" + kv.Key.Name + "' is declared " +
                                 kv.Value.Sync + ", i.e. NOTHING re-delivers it over the wire, and the restore " +
                                 "filter drops it anyway. De-duplication needs a duplicate; this is just a lost " +
                                 "window, and it would be lost silently on every load.";
            }
            foreach (ModalType modal in Enum.GetValues(typeof(ModalType)))
            {
                var rule = GeoWindowCoverage.RuleForModal(modal);
                if (rule == null) continue;                        // L49 owns undeclared modals
                bool dropped = RestoreDropsResolvedSubjects.KindIsMirrored(typeof(UIStateGeoModal), modal);
                if ((rule.Sync == WindowSync.Mirrored) == dropped) continue;
                yield return (rule.Sync == WindowSync.Mirrored
                                 ? "L135 mirrored-kind-survives-the-restore: modal '"
                                 : "L135 drop-reaches-a-kind-with-no-producer: modal '") + modal +
                             "' is declared " + rule.Sync + " but the restore filter " +
                             (dropped ? "drops" : "keeps") + " it. The modal family is 43 windows wearing one " +
                             "UIStateGeoModal, so a filter that reads only the VIEW-STATE type gets all 43 " +
                             "wrong together — the ModalType axis is the only place their verdicts exist.";
            }

            // ── (B) EXECUTED: the key is TOTAL over the game's real restorable set ──
            // Every IGeoscapeRestorableViewStateContext the game ships is a private nested class of the view
            // state it rebuilds, which is what lets DeclaringType key straight into the coverage table with
            // no per-context entry. If the game ever ships a standalone context, that key returns null and
            // the entry is kept — a silent duplicate, exactly the bug.
            var contexts = game.GetTypes()
                               .Where(t => typeof(IGeoscapeRestorableViewStateContext).IsAssignableFrom(t) &&
                                           !t.IsInterface && !t.IsAbstract)
                               .ToList();
            if (contexts.Count == 0)
                yield return "L135 restore-key-not-total: no IGeoscapeRestorableViewStateContext implementor " +
                             "resolves in the game assembly at all, so this law proved nothing about the set " +
                             "it is supposed to cover.";
            foreach (var ctx in contexts)
            {
                var stateType = ctx.DeclaringType;
                if (stateType != null && GeoWindowCoverage.RuleFor(stateType) != null) continue;
                yield return "L135 restore-key-not-total: the restorable context '" + ctx.FullName + "' does " +
                             "not key into GeoWindowCoverage — its declaring type is '" +
                             (stateType == null ? "<none, it is not nested in a view state>" : stateType.Name) +
                             "'. The restore filter reads the window kind off DeclaringType, so this entry is " +
                             "restored UNJUDGED on every peer: if its window is mirrored, every non-authoring " +
                             "peer gets it twice and nothing says so.";
            }

            // ── (C) STRUCTURAL: the drop and the deferral re-carry ask the SAME question ──
            var prefixCallees = Program.Callees(prefix, filter.Assembly).ToList();
            if (!prefixCallees.Any(c => c.MetadataToken == producer.MetadataToken && c.Module == producer.Module))
                yield return "L135 producer-signal-not-shared: the restore prefix never calls " +
                             "RestoringAnotherPeersBlob, so whatever it is dropping, it is not dropping it " +
                             "because this peer is not the producer. Dropping a mirrored window on the peer " +
                             "that RAISED it deletes the host's own queue.";
            foreach (var gate in new[] { producer, carry })
            {
                var callees = Program.Callees(gate, gate.DeclaringType.Assembly).ToList();
                if (callees.Any(c => c.MetadataToken == isSession.MetadataToken && c.Module == isSession.Module) &&
                    callees.Any(c => c.MetadataToken == isHost.MetadataToken && c.Module == isHost.Module))
                    continue;
                yield return "L135 producer-signal-not-shared: '" + gate.DeclaringType.Name + "." + gate.Name +
                             "' no longer reads BOTH NetworkEngine.IsActiveSession and IsHost. The drop " +
                             "(WindowQueueSync) and the re-carry (ReplenishSync.CarryUnreadWindowsPatch) are " +
                             "two halves of one predicate: the peer whose restored mirrored windows are " +
                             "dropped is exactly the peer whose own unanswered raises are re-held. Let the two " +
                             "disagree and a peer loses the windows it deferred into a battle, silently.";
            }
            if (!Program.Callees(carry, carry.DeclaringType.Assembly)
                        .Any(c => c.MetadataToken == requeue.MetadataToken && c.Module == requeue.Module))
                yield return "L135 deferral-not-carried: CarryUnreadWindowsPatch no longer calls " +
                             "EventPopup.RequeueUnanswered. After the mirrored-restore drop that call is the " +
                             "ONLY path by which a peer's genuinely-unseen windows come back from a mission — " +
                             "the save cannot do it, because on a client the save is the host's.";

            // ── (D) STRUCTURAL: not keyed on the record state, and not on any single popup ──
            if (recordState != null &&
                prefixCallees.Concat(Program.Callees(verdict, filter.Assembly))
                             .Any(c => c.DeclaringType == recordState ||
                                       (c is MethodInfo mi && mi.ReturnType == recordState)))
                yield return "L135 drop-keyed-on-record-state: the restore filter reads " +
                             "GeoscapeEventRecordState. A single-choice geoscape event is already Completed " +
                             "the moment it triggers (GeoscapeEventSystem:651-655), so that flag cannot tell a " +
                             "duplicate from an unread window — it reads 'answered' for both. The producer, " +
                             "not the record, is the question.";
        }

        private static Type AccessToolsSafeType(string name)
        {
            try { return HarmonyLib.AccessTools.TypeByName(name); }
            catch { return null; }
        }
    }
}
