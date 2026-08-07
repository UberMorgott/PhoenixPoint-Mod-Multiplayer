using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L196 — THE REVEAL'S INPUT UNLOCK LANDS BEFORE THIS PEER RELEASES ANY QUEUED WINDOW.
    ///
    /// THE PREDICTION THIS LAW CASHES. L175's own text warned about "holding a cutscene behind a player with
    /// nothing able to release it". On 2026-08-08 the mirror image happened: the cutscene was NOT held, it
    /// was entered — and its <c>EnterState</c>'s <c>SetInputState("Cutscene")</c> was stored and DISCARDED,
    /// because a stale loading override was still held at that instant
    /// (<c>InputController.SetInputSets</c>:520-527 applies a new set only while no override is held).
    /// Escape did nothing; the peer watched the whole video with three modals stacked behind it.
    ///
    /// WHY THIS IS AN ORDERING LAW AND NOT A SECOND COPY OF L142. L142 asserts the DECISION
    /// (<c>RevealInputLock.ShouldClear</c>) and its wiring — that <c>Update</c> reaches <c>Converge</c> at
    /// all. Both were GREEN through this bug, and correctly so: the invariant WAS converged, just not before
    /// the frame in which a queued state entered. The unlock is only worth anything if it lands FIRST, so
    /// what this law asserts is the ONE ordering this codebase actually controls — inside our own frame,
    /// input is unlatched before any window we hold is released:
    ///
    ///     NetworkEngine.Update: Transport → Session → SaveTransfer.Update → Sync.Tick
    ///                                                   └ RepairRevealInputLock  └ EventPopup.DrainHeldRaises
    ///
    /// Move the converge below anything in <c>SaveTransferCoordinator.Update</c>, or move the sync tick above
    /// the coordinator, and a peer can release a mirrored window into a frame whose input is still latched —
    /// which is a window the player can see and cannot dismiss.
    ///
    /// ARM (c) KEEPS IT UNCONDITIONAL. A converge behind a role check, a barrier flag or a level lookup is
    /// the previous repair's exact failure (L142 arms (e)/(f)) arriving as a SCHEDULING bug instead of a
    /// decision bug, and no execution of ShouldClear can see it.
    ///
    /// Falsify (each verified RED, then restored): move <c>RepairRevealInputLock()</c> below
    /// <c>FlushLoadComplete()</c> in <c>SaveTransferCoordinator.Update</c> → <c>unlock-is-not-first</c>;
    /// swap <c>SaveTransfer?.Update()</c> and <c>Sync?.Tick()</c> in <c>NetworkEngine.Update</c> →
    /// <c>queue-runs-before-the-unlock</c>; gate the repair on <c>IsHost</c> → <c>unlock-is-role-gated</c>;
    /// unwire <c>DrainHeldRaises</c> from the tick → <c>ordering-guards-nothing</c>.
    /// </summary>
    internal static class L196_TheUnlockLandsBeforeTheQueueDoes
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var mod = coord.Assembly;

            var coordUpdate = coord.GetMethod("Update", All, null, Type.EmptyTypes, null);
            var repair = coord.GetMethod("RepairRevealInputLock", All);
            var engineUpdate = typeof(NetworkEngine).GetMethod("Update", All, null, Type.EmptyTypes, null);
            var syncTick = typeof(SyncEngine).GetMethod("Tick", All, null, Type.EmptyTypes, null);
            var drain = typeof(EventPopup).GetMethod("DrainHeldRaises", All);

            if (coordUpdate == null || repair == null || engineUpdate == null || syncTick == null || drain == null)
            {
                yield return "L196 premise-changed: SaveTransferCoordinator.{Update,RepairRevealInputLock}, " +
                             "NetworkEngine.Update, SyncEngine.Tick or EventPopup.DrainHeldRaises no longer " +
                             "resolves. The frame order this law is about has moved, and an ordering assertion " +
                             "over a shape that no longer exists passes while saying nothing.";
                yield break;
            }

            // ── (a) THE UNLOCK IS THE FIRST THING THE COORDINATOR DOES ───────────────
            var coordOrder = Program.CalleeSequence(coordUpdate);
            int repairAt = coordOrder.FindIndex(m => m.MetadataToken == repair.MetadataToken &&
                                                     m.Module == repair.Module);
            if (repairAt < 0)
                yield return "L196 unlock-is-not-first: SaveTransferCoordinator.Update does not reach " +
                             "RepairRevealInputLock at all. The unlock is an EDGE the co-op reveal machinery " +
                             "deliberately destroys and re-issues; a peer that loses it keeps a live world with " +
                             "no camera, no hotkeys and no cutscene skip, silently and forever.";
            else if (repairAt != 0)
                yield return "L196 unlock-is-not-first: RepairRevealInputLock is callee #" + repairAt +
                             " of SaveTransferCoordinator.Update, behind " + coordOrder[0].Name + ". Everything " +
                             "ahead of it can advance this peer past the reveal within the same frame, and a " +
                             "state entered in that frame has its SetInputState stored and discarded — the " +
                             "2026-08-08 unskippable intro, exactly.";

            // ── (b) …AND THE COORDINATOR RUNS BEFORE THE QUEUE THIS PEER DRAINS ──────
            var engineOrder = Program.CalleeSequence(engineUpdate);
            int coordAt = engineOrder.FindIndex(m => m.MetadataToken == coordUpdate.MetadataToken &&
                                                     m.Module == coordUpdate.Module);
            int tickAt = engineOrder.FindIndex(m => m.MetadataToken == syncTick.MetadataToken &&
                                                    m.Module == syncTick.Module);
            if (coordAt < 0 || tickAt < 0)
                yield return "L196 queue-runs-before-the-unlock: NetworkEngine.Update no longer calls both " +
                             "SaveTransferCoordinator.Update and SyncEngine.Tick, so the ordering this law " +
                             "asserts is not decidable at all and the unlock has no guaranteed position in the " +
                             "frame that releases windows.";
            else if (coordAt > tickAt)
                yield return "L196 queue-runs-before-the-unlock: NetworkEngine.Update ticks the SYNC engine " +
                             "(callee #" + tickAt + ") before the save-transfer coordinator (#" + coordAt + "). " +
                             "The sync tick is what drains held raises onto this peer's queue, so a window can " +
                             "now be released into a frame whose input override has not been cleared yet — " +
                             "visible, modal, and undismissable.";

            // ── (c) THE UNLOCK IS UNCONDITIONAL: no role, no peer, no level ──────────
            var peerish = new[] { "SessionManager", "PingTable", "PeerListEntry", "LobbyController",
                                  "RosterProgressTracker" };
            foreach (var c in Program.Callees(repair, mod))
            {
                if (c.Name == "get_IsHost")
                    yield return "L196 unlock-is-role-gated: RepairRevealInputLock reads IsHost. The override " +
                                 "lives on the game-scoped InputController and EVERY peer sets _revealed in " +
                                 "PerformDeferredLift — a role branch here re-opens exactly the hole L142's " +
                                 "arms (e)/(f) closed, one layer up.";
                if (c.DeclaringType != null && peerish.Contains(c.DeclaringType.Name))
                    yield return "L196 unlock-is-role-gated: RepairRevealInputLock calls " +
                                 c.DeclaringType.Name + "." + c.Name + ". Converging this peer's own input " +
                                 "state must never become a question about another peer — that is P13's wait-on-" +
                                 "a-person arriving through the input system.";
            }

            // ── (d) NON-VACUITY: the thing being ordered against really is the release ─
            if (!Program.Callees(syncTick, mod).Any(m => m.MetadataToken == drain.MetadataToken &&
                                                         m.Module == drain.Module))
                yield return "L196 ordering-guards-nothing: SyncEngine.Tick no longer reaches " +
                             "EventPopup.DrainHeldRaises, so arm (b) orders the unlock against a tick that " +
                             "releases no windows and would stay green through the whole defect.";
        }
    }
}
