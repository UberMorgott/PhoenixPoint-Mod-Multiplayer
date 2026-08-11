using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network;

namespace RailCheck
{
    /// <summary>
    /// L415 — A TRANSFER THAT DIES ASYNCHRONOUSLY GIVES THE TERMINAL FLAGS BACK.
    ///
    /// THE FAILURE (52c2262). Both transfer launch sites clear the three terminal flags
    /// (<c>_begun</c>, <c>_loadCompleteSent</c>, <c>_revealAllSent</c>) before calling
    /// <c>LaunchTransfer</c>, and used to restore them only when that call returned false SYNCHRONOUSLY.
    /// But <c>LaunchTransfer</c> returns true THE MOMENT THE COROUTINE STARTS — its whole tail is
    /// <c>timing.Start(HostSerializeAndSendCrt(...)); return true;</c> — and
    /// <c>HostSerializeAndSendCrt</c> can still fail one frame later on an empty blob. That arm restored
    /// nothing.
    ///
    /// WHAT THAT COSTS. A mid-session host is then left with <c>SessionStarted == false</c> while standing
    /// in a LIVE co-op level. <c>HostLoadGuard</c> and <c>ShouldInterceptInSessionHostLoad</c> both AND on
    /// it, so the next F2/CONTINUE is either refused outright — or, worse, runs as a VANILLA SOLO load
    /// while the clients keep playing — and <c>ArmSelfLoadBarrier</c> never re-arms. Nothing on screen
    /// says any of this; the host just finds the game will not reload.
    ///
    /// THE RULE. <c>ClearTerminalFlags</c> snapshots and clears, and RETURNS the undo (the same
    /// undo-delegate shape L408 polices on the SaveManager identity). Both launch sites park that undo in
    /// <c>_undoTransferFlags</c>, so EVERY failure arm — synchronous or asynchronous — can take the clear
    /// back; the field is nulled once the blob is in hand and the transfer is really under way.
    ///
    /// THE ARMS:
    ///   (a) <c>async-arm-never-restores</c> — <c>HostSerializeAndSendCrt</c>'s compiler-generated
    ///       <c>MoveNext</c> must READ <c>_undoTransferFlags</c> and INVOKE it. The empty-blob abort is the
    ///       one failure the launch sites cannot reach, so if this arm is silent the bug is back whole.
    ///   (b) <c>launch-site-drops-the-undo</c> — <c>HostStartSessionInGame</c> and
    ///       <c>NewCampaignAutosaveAndTransferCrt</c> must each call <c>ClearTerminalFlags</c> AND store its
    ///       result into <c>_undoTransferFlags</c>. A launch that clears the flags with the undo dropped on
    ///       the floor leaves the async arm holding null, which invokes nothing and reports nothing.
    ///
    /// LIMIT, STATED PLAINLY: arm (a) reads "some <c>Action.Invoke</c> in this MoveNext" alongside the
    /// field read, in L408's exact shape. It does not prove the invoke consumes THAT field's value; today
    /// the coroutine holds exactly one <c>Action.Invoke</c> and one undo, and the day a second delegate
    /// lands here this arm should grow an offset-proximity test rather than be trusted as-is.
    ///
    /// GUARD: <c>premise-changed</c> if the coroutine type, its <c>MoveNext</c>, the field,
    /// <c>ClearTerminalFlags</c>, its <c>Action</c> return, or either launch site stops resolving — a law
    /// that cannot see the undo must not report the seam as safe.
    ///
    /// Falsify: delete the <c>_undoTransferFlags?.Invoke()</c> from the empty-blob arm → (a); make a launch
    /// site call <c>ClearTerminalFlags()</c> without storing the result → (b).
    /// </summary>
    internal static class L415_ATransferThatFailsAsyncGivesTheTerminalFlagsBack
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var coord = typeof(SaveTransferCoordinator);
            var undo = coord.GetField("_undoTransferFlags", All);
            var clear = coord.GetMethod("ClearTerminalFlags", All);
            var sendMoveNext = CoroutineMoveNext(coord, "HostSerializeAndSendCrt");
            var inGame = coord.GetMethod("HostStartSessionInGame", All);
            var newCampaign = CoroutineMoveNext(coord, "NewCampaignAutosaveAndTransferCrt");

            if (undo == null || clear == null || clear.ReturnType != typeof(Action) ||
                sendMoveNext == null || inGame == null || newCampaign == null)
            {
                yield return "L415 premise-changed: SaveTransferCoordinator.{_undoTransferFlags, " +
                             "ClearTerminalFlags -> Action, HostSerializeAndSendCrt, HostStartSessionInGame, " +
                             "NewCampaignAutosaveAndTransferCrt} no longer resolve as a launch that parks an " +
                             "undo and a coroutine that can take it back. The terminal-flag seam has moved; " +
                             "re-read it before assuming an empty-blob abort still leaves the host able to " +
                             "reload its own live session.";
                yield break;
            }

            // ── arm (a): the async failure arm — the only one the launch sites cannot reach.
            bool readsUndo = Program.FieldRefs(sendMoveNext).Any(f => Same(f, undo));
            bool invokes = Program.CalleeSequence(sendMoveNext)
                .Any(c => c != null && c.Name == "Invoke" && c.DeclaringType == typeof(Action));
            if (!readsUndo || !invokes)
                yield return "L415 async-arm-never-restores: HostSerializeAndSendCrt reads _undoTransferFlags=" +
                             readsUndo + " invokes-an-Action=" + invokes + ". LaunchTransfer already returned " +
                             "true when this coroutine started, so the launch sites' synchronous restore is long " +
                             "gone and the empty-blob abort is the ONLY place the terminal flags can be given " +
                             "back. Without it a mid-session host sits at SessionStarted==false inside a live " +
                             "co-op level: HostLoadGuard and ShouldInterceptInSessionHostLoad both AND on that " +
                             "flag, so the next F2/CONTINUE is refused — or runs as a VANILLA SOLO load while " +
                             "the clients keep playing — and ArmSelfLoadBarrier never re-arms.";

            // ── arm (b): both launches must actually park the undo where that arm can find it.
            foreach (var site in new[]
                     {
                         new KeyValuePair<string, MethodBase>("HostStartSessionInGame", inGame),
                         new KeyValuePair<string, MethodBase>("NewCampaignAutosaveAndTransferCrt", newCampaign),
                     })
            {
                bool calls = Program.CalleeSequence(site.Value)
                    .Any(c => c != null && c.MetadataToken == clear.MetadataToken && c.Module == clear.Module);
                bool parks = Program.FieldRefs(site.Value, OpCodes.Stfld).Any(f => Same(f, undo));
                if (calls && parks) continue;
                yield return "L415 launch-site-drops-the-undo: " + site.Key + " calls ClearTerminalFlags=" +
                             calls + " stores _undoTransferFlags=" + parks + ". A launch that clears the three " +
                             "terminal flags without parking the undo leaves every later failure arm holding " +
                             "null: the invoke runs, restores nothing, and says nothing. Clearing the flags and " +
                             "keeping the undo are one act — the flags are cleared for a transfer that has not " +
                             "yet proved it can produce bytes.";
            }
        }

        /// <summary>The compiler-generated iterator for a coroutine — the real body lives in its MoveNext,
        /// the same lookup L408 does for PrepareEntryFromBlobCrt.</summary>
        private static MethodBase CoroutineMoveNext(Type owner, string name)
            => owner.GetNestedTypes(All)
                .FirstOrDefault(t => t.Name.IndexOf(name, StringComparison.Ordinal) >= 0)
                ?.GetMethod("MoveNext", All);

        private static bool Same(FieldInfo a, FieldInfo b)
            => a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
