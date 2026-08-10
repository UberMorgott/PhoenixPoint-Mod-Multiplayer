using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L408 — A PREPARE THAT DID NOT FINISH MUST LEAVE THIS PEER'S OWN CAMPAIGN IDENTITY BEHIND.
    ///
    /// <c>PrepareEntryFromBlobCrt</c> (SaveTransferCoordinator:1832) builds the incoming save's scene binding
    /// on the LIVE <c>PhoenixSaveManager</c>, and step 1b writes the REMOTE save's identity onto it before
    /// the load is known to succeed: <c>LatestLoad</c> (whose native setter also assigns <c>_currentGameId</c>
    /// and <c>IsIronmanMode</c>, PhoenixSaveManager.cs:70-78), <c>_currentGameId</c>, <c>_currentDifficulty</c>,
    /// <c>_enabledDlc</c> — plus <c>_currentGeoscapeSection.ContentObjects</c> in step 1c. Steps 1c-3 all read
    /// the transferred blob and can throw (a truncated/foreign blob, the level-params read,
    /// <c>CreateSceneBinding</c>), and a failed prepare is NOT fatal to the peer: the caller aborts the
    /// curtain and the player KEEPS PLAYING their own campaign (ClientLoadCrt:1821).
    ///
    /// So the failure is silent and it is a data-loss failure, not a display one: nothing on screen changes,
    /// and the peer's very next save is written under another campaign's GameId/ironman flag/DLC set. The
    /// only place that can be fixed is here — the write must carry its own undo, and the coroutine must run
    /// it on every path that does not reach <c>_pendingResult</c>.
    ///
    /// THE ARMS:
    ///   (a) <c>write-has-no-undo</c> — <c>ApplyPrepareLoadGameState</c> must RETURN the restore. A void
    ///       write cannot be taken back by any caller, however careful.
    ///   (b) <c>prepare-never-restores</c> — the coroutine's <c>MoveNext</c> must actually invoke that
    ///       restore delegate. A returned-and-dropped undo is the same bug with extra code.
    ///   (c) <c>prepare-has-no-finally</c> — and it must invoke it from a FINALLY, or a throw in the
    ///       level-params read (the measured failure mode) walks straight past the restore.
    ///
    /// GUARD: <c>premise-changed</c> if the coroutine type, its <c>MoveNext</c>, the write method, or the
    /// call from the coroutine to the write no longer resolves — a law that cannot see the write must not
    /// report the seam as safe.
    ///
    /// Falsify: make <c>ApplyPrepareLoadGameState</c> void again → (a); drop the <c>undoIdentity?.Invoke()</c>
    /// → (b); move that invoke out of the <c>finally</c> into a plain post-step statement → (c).
    /// </summary>
    internal static class L408_AFailedPrepareLeavesTheLocalCampaignsIdentity
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var write = coord.GetMethod("ApplyPrepareLoadGameState", All);
            // The compiler-generated iterator for the coroutine — the real body lives in its MoveNext.
            var machine = coord.GetNestedTypes(All)
                .FirstOrDefault(t => t.Name.IndexOf("PrepareEntryFromBlobCrt", StringComparison.Ordinal) >= 0);
            var moveNext = machine?.GetMethod("MoveNext", All);

            if (write == null || moveNext == null ||
                !Program.CalleeSequence(moveNext).Any(c => c.Name == "ApplyPrepareLoadGameState"))
            {
                yield return "L408 premise-changed: SaveTransferCoordinator.{ApplyPrepareLoadGameState, " +
                             "PrepareEntryFromBlobCrt} no longer resolve as a coroutine that writes the live " +
                             "SaveManager's identity from the transferred metadata. The speculative-write seam " +
                             "has moved; re-read it before assuming a failed prepare still leaves this peer " +
                             "playing its OWN campaign.";
                yield break;
            }

            // (a) the write must hand back its undo
            if (write.ReturnType != typeof(Action))
                yield return "L408 write-has-no-undo: ApplyPrepareLoadGameState returns " + write.ReturnType.Name +
                             " — it writes LatestLoad/_currentGameId/_currentDifficulty/_enabledDlc onto the LIVE " +
                             "SaveManager before the load that justifies them is known to succeed, and hands the " +
                             "caller nothing to take them back with. A prepare that then throws leaves this peer " +
                             "pointing at the REMOTE save's campaign id, ironman flag and DLC set while it keeps " +
                             "playing its own game, and its next save is written under that wrong identity.";

            // (b) …and the coroutine must run it. An iterator's `finally` body is LIFTED out of MoveNext into
            // its own `<>m__FinallyN` on the state machine (verified: MoveNext calls <>m__Finally1/2/3 and
            // nothing else), so both the invoke test and the "from a finally" test read that whole family.
            var finallyBodies = machine.GetMethods(All)
                .Where(mi => mi.Name.IndexOf("m__Finally", StringComparison.Ordinal) >= 0).ToList();
            Func<MethodBase, bool> callsUndo = mb =>
                Program.CalleeSequence(mb).Any(c => c.Name == "Invoke" && c.DeclaringType == typeof(Action));
            bool undoFromFinally = finallyBodies.Any(f => callsUndo(f)) || InvokedFromFinally(moveNext);
            bool invokesUndo = undoFromFinally || callsUndo(moveNext);
            if (!invokesUndo)
                yield return "L408 prepare-never-restores: PrepareEntryFromBlobCrt never invokes the restore " +
                             "delegate. The identity write at step 1b is unconditional and the metadata/level-params/" +
                             "CreateSceneBinding steps after it can all fail, so on every failure path the live " +
                             "SaveManager keeps the transferred save's identity.";

            // (c) …from a finally, or a throw skips it
            if (invokesUndo && !undoFromFinally)
                yield return "L408 prepare-has-no-finally: the restore is invoked, but not from any finally region " +
                             "of PrepareEntryFromBlobCrt (" + finallyBodies.Count + " lifted finally bod(y/ies) " +
                             "seen). The measured failure is a THROW out of the blob reads, which walks past every " +
                             "ordinary statement — a restore that only runs on the tidy path restores nothing when " +
                             "it matters.";
        }

        /// <summary>True if some <c>Action.Invoke</c> call site sits inside a finally region of the method.
        /// IL-offset containment, so it survives any re-ordering the compiler does to the state machine.</summary>
        private static bool InvokedFromFinally(MethodBase m)
        {
            var body = m.GetMethodBody();
            if (body == null) return false;
            var regions = body.ExceptionHandlingClauses
                .Where(c => c.Flags == ExceptionHandlingClauseOptions.Finally)
                .Select(c => new { Start = c.HandlerOffset, End = c.HandlerOffset + c.HandlerLength })
                .ToList();
            if (regions.Count == 0) return false;
            foreach (var site in Program.CallSites(m))
                if (site.Value != null && site.Value.Name == "Invoke" && site.Value.DeclaringType == typeof(Action) &&
                    regions.Any(r => site.Key >= r.Start && site.Key < r.End))
                    return true;
            return false;
        }
    }
}
