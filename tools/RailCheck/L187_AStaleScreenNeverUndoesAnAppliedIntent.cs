using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L187 — A STALE SCREEN NEVER UNDOES AN APPLIED INTENT, AND A PEER WHOSE GESTURE IS UNDONE IS TOLD.
    ///
    /// THE DEFECT (2026-08-07 co-op session, HOST log). Seven consecutive client equip intents were applied
    /// and then silently reverted on the host — `nonce=43…49`, all `char=U#5`, inside eleven seconds:
    ///   23:12:44.774 [MP][equip] HOST intent APPLIED char=U#5 nonce=43 peer=1 took=1 returned=0
    ///   23:12:44.918 [MP][equip] HOST previous apply was REVERTED for char=U#5 — the live loadout is back
    ///                to its PRE-intent content … (a stale view→model flush, open-screen UpdateState)
    /// The CLIENT's half of that same window (nonces 43-52) contains no revert, reject or correction line of
    /// any kind: it believed all ten gestures had landed. Two separate holes, and the law closes both.
    ///
    /// (1) THE ROOT. <c>SetItemsApplyGate</c> blocks a view→model flush only INSIDE an apply
    /// (<c>SyncApplyScope</c>). A host standing in its own equip screen while a remote intent lands is in no
    /// scope at all — <c>IntentRail.ShouldRunNative</c> returns true for a host — so
    /// <c>UIStateEditSoldier.UpdateState</c>'s next flush wrote its still-PRE-intent widget lists straight
    /// back over the applied loadout, and the DiffEngine then correctly emitted nothing (it can only ship
    /// what the model holds). That is the ordinary two-players-in-a-loadout case, not an edge, and the
    /// existing guard only DETECTED it — one intent late, because it is asked when the NEXT intent arrives.
    /// <see cref="EquipSync.JudgeHostFlush"/> asks the same unambiguous question one flush EARLIER, over the
    /// four byte bodies and nothing else, so this arm executes the real production decision.
    ///
    /// (2) THE SILENCE. The revert was an ERROR on the host's console and nothing anywhere else. A revert is
    /// a refusal that arrives late, so it now takes the refusal path — <c>CheckLastApplyStuck</c> reaches
    /// <see cref="EquipSync"/>'s <c>Reject</c>, which re-emits the character + storage subtrees and nudges
    /// the ONE peer whose loadout moved back under it. That needs the peer's identity to be recorded beside
    /// the applied bytes, which is what <c>RecordApplied</c> is for.
    ///
    /// Falsify: block an idle re-flush → <c>L187 host-screen-frozen</c>; block a real host edit →
    /// <c>L187 host-edit-fought</c>; stop recognising the wholesale write-back →
    /// <c>L187 stale-revert-unrecognised</c>; fire when the model has already moved on →
    /// <c>L187 verdict-ignores-the-model</c>; fire for a no-op intent → <c>L187 no-op-intent-armed</c>;
    /// unwire the guard from the seam → <c>L187 guard-is-decorative</c>; drop the verdict →
    /// <c>L187 seam-decides-alone</c>; drop the repaint → <c>L187 blocked-screen-never-repaints</c>;
    /// drop the notify → <c>L187 revert-is-silent</c>; stop recording who asked →
    /// <c>L187 revert-has-no-addressee</c>.
    /// </summary>
    internal static class L187_AStaleScreenNeverUndoesAnAppliedIntent
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var eq = typeof(EquipSync);
            var judge = eq.GetMethod("JudgeHostFlush", All, null,
                            new[] { typeof(byte[]), typeof(byte[]), typeof(byte[]), typeof(byte[]) }, null);
            var block = eq.GetMethod("BlockStaleHostRevert", All);
            var reseed = eq.GetMethod("ReseedLocalScreenAfterRemoteMutation", All);
            var stuck = eq.GetMethod("CheckLastApplyStuck", All);
            var reject = eq.GetMethod("RejectAndNotify", All, null,
                             new[] { typeof(ulong), typeof(int), typeof(string) }, null);
            var record = eq.GetMethod("RecordApplied", All);
            var handle = eq.GetMethod("HandleIntent", All);
            var capture = eq.GetNestedType("SetItemsCapturePatch", All);
            var prefix = capture?.GetMethod("Prefix", All);

            if (judge == null || block == null || reseed == null || stuck == null || reject == null ||
                record == null || handle == null || prefix == null)
            {
                yield return "L187 premise-changed: EquipSync.JudgeHostFlush / BlockStaleHostRevert / " +
                             "ReseedLocalScreenAfterRemoteMutation / CheckLastApplyStuck / RejectAndNotify / " +
                             "RecordApplied / HandleIntent or SetItemsCapturePatch.Prefix no " +
                             "longer resolves. GeoCharacter.SetItems is the ONE model funnel every loadout " +
                             "write bottoms out in; a version of this seam that cannot tell a stale screen " +
                             "from a gesture reverts applied intents in silence, which is how seven of " +
                             "thirty-one client gestures died on 2026-08-07.";
                yield break;
            }

            // ── (a) THE OUTCOME, over real encoded loadout bodies ──
            // A = the loadout before the remote intent, B = the loadout the host committed for it,
            // C = some third loadout a host-local edit would produce.
            byte[] a = Body("rifle"), b = Body("rifle", "medkit"), c = Body("rifle", "grenade");

            if (EquipSync.JudgeHostFlush(a, b, b, a) != EquipSync.FlushVerdict.StaleRevert)
                yield return "L187 stale-revert-unrecognised: a flush that writes back EXACTLY the loadout " +
                             "as it stood before the last remote apply, while the host still holds that " +
                             "apply, is no longer recognised as the stale screen it is. That is the " +
                             "2026-08-07 signature verbatim — seven applies undone in eleven seconds, each " +
                             "within 150-1800 ms, and the rail emitted nothing because there was nothing " +
                             "left in the model to emit.";
            if (EquipSync.JudgeHostFlush(a, b, b, b) != EquipSync.FlushVerdict.Native)
                yield return "L187 host-screen-frozen: an idle re-flush of what the host ALREADY holds is " +
                             "being treated as suspicious. The equip screen re-flushes on every UI event — " +
                             "six or seven frames in a row after one drag — so a guard that eats those " +
                             "stops the host committing its own gestures at all.";
            if (EquipSync.JudgeHostFlush(a, b, b, c) != EquipSync.FlushVerdict.Retire)
                yield return "L187 host-edit-fought: a genuine host-local edit no longer RETIRES the memo. " +
                             "Without that the guard is a lock rather than a guard: a host who later drags " +
                             "the loadout back to its old layout by hand would be refused forever, because " +
                             "his last step lands on exactly the pre-intent content.";

            // ── (b) POSITIVE CONTROL: the three shapes that must decide NOTHING ──
            if (EquipSync.JudgeHostFlush(a, b, a, a) != EquipSync.FlushVerdict.Native)
                yield return "L187 verdict-ignores-the-model: the host no longer holds what it committed " +
                             "(canon != after) and the guard armed anyway. Something else already moved the " +
                             "loadout, so the memo describes a state that is gone and blocking on it would " +
                             "refuse a legitimate write on evidence that has expired.";
            if (EquipSync.JudgeHostFlush(a, a, a, a) != EquipSync.FlushVerdict.Native)
                yield return "L187 no-op-intent-armed: an intent that changed nothing (before == after) arms " +
                             "the guard. There is no apply to protect, so every subsequent flush of that " +
                             "same content would be blocked as a revert of nothing.";
            if (EquipSync.JudgeHostFlush(null, b, b, a) != EquipSync.FlushVerdict.Native)
                yield return "L187 verdict-guesses-on-nothing: with no recorded pre-intent body the verdict " +
                             "must be Native. A guard that decides on a half-populated memo blocks writes " +
                             "on a comparison it never made.";

            // ── (c) the seam: the decision is CONSULTED, and the block repaints ──
            var prefixCalls = Program.Callees(prefix, eq.Assembly).ToList();
            if (!prefixCalls.Any(m => m.MetadataToken == block.MetadataToken))
                yield return "L187 guard-is-decorative: SetItemsCapturePatch.Prefix — the ONE capture seam on " +
                             "GeoCharacter.SetItems — no longer consults the stale-flush guard, so arm (a) is " +
                             "proved about a decision the live write path does not make.";
            var blockCalls = Program.Callees(block, eq.Assembly).ToList();
            if (!blockCalls.Any(m => m.MetadataToken == judge.MetadataToken))
                yield return "L187 seam-decides-alone: BlockStaleHostRevert no longer routes through " +
                             "JudgeHostFlush, so the seam and the falsified decision have drifted apart and " +
                             "arm (a) proves nothing about what runs.";
            if (!blockCalls.Any(m => m.MetadataToken == reseed.MetadataToken))
                yield return "L187 blocked-screen-never-repaints: the block drops the write and marks nothing " +
                             "dirty. Blocking is only half — the gate stops the screen writing back, it does " +
                             "not make the screen SHOW the new loadout, and a host left looking at the " +
                             "pre-intent widgets is the REACTIVITY mandate broken on the peer doing the " +
                             "blocking.";

            // ── (d) the peer whose gesture was undone is TOLD ──
            if (!Program.Callees(stuck, eq.Assembly).Any(m => m.MetadataToken == reject.MetadataToken))
                yield return "L187 revert-is-silent: CheckLastApplyStuck detects that an apply was undone and " +
                             "no longer reaches RejectAndNotify. That is the 2026-08-07 asymmetry — the host " +
                             "logs seven ERRORs to its own console while the client's log carries no revert, " +
                             "reject or correction line at all and its player keeps looking at a loadout the " +
                             "host does not have.";
            if (!Program.Callees(handle, eq.Assembly).Any(m => m.MetadataToken == record.MetadataToken))
                yield return "L187 revert-has-no-addressee: an applied intent no longer records WHO asked for " +
                             "it beside what it committed, so a later revert has nobody to answer. Arm (d) " +
                             "then reaches a Reject that can never name a peer.";
        }

        /// <summary>One encoded loadout body through the production codec — the same bytes the live guard
        /// compares, so the cube above is not a statement about hand-written arrays.</summary>
        private static byte[] Body(params string[] equipmentDefGuids)
        {
            var lists = new List<EquipSync.SlotRef>[3];
            lists[1] = equipmentDefGuids
                .Select(g => new EquipSync.SlotRef { Guid = g, Count = 1, Charges = 0 }).ToList();
            return EquipSync.EncodeBody(false, lists);
        }
    }
}
