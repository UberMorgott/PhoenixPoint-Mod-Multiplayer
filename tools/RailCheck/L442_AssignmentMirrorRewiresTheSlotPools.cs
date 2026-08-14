using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L442 — THE ASSIGNMENT MIRROR IS NOT DONE WHEN THE TABLE IS RIGHT.
    ///
    /// This is the law for the step that is invisible when it is missing. TFTV's economy output does not read
    /// <c>PersonnelData._assignments</c>: it reads <c>FacilitySlotPools.UsedSlots</c>, and <c>UsedSlots</c> is
    /// only ever DERIVED from the assignment table, in one place — <c>ResyncWorkSlots</c> (Data.cs:895-906 →
    /// <c>Workers.SetUsedSlots</c>:331). A client apply that writes the dictionary and stops leaves every peer
    /// showing the same board and running a different economy: identical rows, divergent research and
    /// production, no exception, no divergence report, nothing in any log. Every other law in this suite would
    /// stay green through it, which is exactly why this one exists.
    ///
    /// ARMS
    ///   (a) <c>root-never-crosses</c> — <c>AssignState</c> must CLASSIFY, with every member a
    ///       <c>LeafDict</c>. Without <c>[SerializeType(SerializeAll)]</c>
    ///       <c>RailMeta.HasPersistentMembers</c> is false, <c>RailType.Source</c> is "none", the walk emits
    ///       ZERO fields (DiffEngine.cs:1113-1117) and the host's table never leaves the host — the bug
    ///       <c>DeployPrepState</c> actually shipped with on 2026-08-13, green under four other arms. And a
    ///       member that classified as anything but <c>LeafDict</c> would ship as ONE whole-field value, so
    ///       moving one worker would resend the whole pool.
    ///   (b) <c>codec-cannot-carry-a-member</c> — every dictionary's VALUE type must resolve to a real
    ///       <c>LeafKind</c>, executed through <c>RailMeta.LeafKindOf</c>. That is the round-trip question in
    ///       the only form a headless harness can ask it: a member the codec has no leaf kind for is silently
    ///       excluded, not loudly refused.
    ///   (c) <c>slot-pools-not-rewired</c> — <c>AssignSync.ClientApply</c> must reach <c>ResyncWorkSlots</c>.
    ///       It is reached BY NAME (TFTV is never referenced by assembly), so the assertion is over the
    ///       method's own string constants — which is precisely what deleting the call removes.
    ///   (d) <c>apply-is-load-shaped</c> — and it must NOT reach <c>ClearAssignments</c> or
    ///       <c>RestoreAssignments</c>. The first is TFTV's own wipe; the second is not a restore at all — it
    ///       runs <c>EnforceLivingCapacity</c> and <c>TryAutoAssignUnassignedPersonnel</c> and would move
    ///       people on the client, locally, against a table the host owns.
    ///   (e) <c>boundary-leaves-state-behind</c> — <c>Clear()</c>, EXECUTED, must empty EVERY dictionary. A
    ///       member added without a line in <c>Clear</c> survives the reload boundary, is captured by the
    ///       post-reload baseline walk and is therefore never emitted again (IdentityResolver.cs:205-206).
    ///   (f) <c>prune-forgets-a-member</c> — <c>PruneSessions</c>, EXECUTED, must drop a dead session key
    ///       from every SESSION dictionary and touch none of the others. A forgotten dictionary keeps a ghost
    ///       row alive on every client for the rest of the campaign.
    ///   (h) <c>partial-apply-closes-the-gate</c> — <c>AssignAuthority.CommitApplyFingerprint</c>, EXECUTED.
    ///       The open gate IS the retry: a skipped id waits on a <c>GeoCharacter</c> delta that does not
    ///       touch <c>AssignState</c>, so a fingerprint committed over skips means the row — or a whole
    ///       training session — is gone for the rest of the campaign, under a log line promising a retry.
    ///   (i) <c>empty-mirror-wipes-the-table</c> — <c>AssignAuthority.MirrorIsUsable</c>, EXECUTED. An empty
    ///       root is indistinguishable from "the delta has not arrived" until a non-empty one has been seen
    ///       once, and applying it deletes the client's table and then recomputes its economy from nothing.
    ///   (g) <c>mirror-never-applies</c> — the client's change gate, EXECUTED: two different tables must
    ///       fingerprint differently and equal tables must agree. A constant fingerprint means the apply runs
    ///       once and never again, which looks exactly like a working mirror until the first reassignment.
    ///
    /// Falsify: delete the <c>ResyncWorkSlots</c> call from the client apply → (c); drop the
    /// <c>[SerializeType]</c> attribute → (a); add a dictionary and forget it in <c>Clear</c> → (e) or in
    /// <c>PruneSessions</c> → (f); make <c>Fingerprint</c> return a constant → (g); commit the fingerprint
    /// before the apply runs → (h); drop the empty-mirror guard → (i).
    /// </summary>
    internal static class L442_AssignmentMirrorRewiresTheSlotPools
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var apply = typeof(AssignSync).GetMethod("ClientApply", All);
            var clear = typeof(AssignState).GetMethod("Clear", All);
            var prune = typeof(AssignState).GetMethod("PruneSessions", All);
            var fingerprint = typeof(AssignState).GetMethod("Fingerprint", All);
            var dicts = typeof(AssignState).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType.IsGenericType &&
                            f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                            f.FieldType.GetGenericArguments()[0] == typeof(int))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

            if (apply == null || clear == null || prune == null || fingerprint == null || dicts.Length == 0)
            {
                yield return "L442 premise-changed: AssignSync.ClientApply / AssignState.Clear / " +
                             "PruneSessions / Fingerprint, or the int-keyed dictionary members themselves, no " +
                             "longer resolve. The mirror is those dictionaries plus the one recompute that " +
                             "turns them into an economy — re-point this law at whatever carries them now; do " +
                             "not delete it, because the failure it names is silent on both peers";
                yield break;
            }

            // ═══ (a) the root classifies, and every member ships per SUB-KEY ═══
            var rt = RailType.Get(typeof(AssignState));
            if (rt == null || rt.Fields.Count == 0)
                yield return "L442 root-never-crosses: AssignState classifies with " +
                             (rt == null ? "no RailType at all" : "ZERO rail fields (Source=" + rt.Source + ")") +
                             ". Registering \"M#assign\" is only half the mod-root contract — without " +
                             "[SerializeType(SerializeMembersByDefault = SerializeAll)] the walk emits nothing " +
                             "for it (DiffEngine.cs:1113-1117), the host's assignment table never leaves the " +
                             "host, and every client runs its own economy off an empty mirror.";
            else
            {
                if (rt.Fields.Count != dicts.Length)
                    yield return "L442 root-never-crosses: AssignState declares " + dicts.Length +
                                 " int-keyed dictionary member(s) but classifies " + rt.Fields.Count +
                                 " rail field(s) — a member the classifier dropped is one the wire never " +
                                 "carries, and nothing else reports it.";
                foreach (var f in rt.Fields.Where(f => f.Class != FieldClass.LeafDict))
                    yield return "L442 root-never-crosses: AssignState." + f.Name + " classifies as " +
                                 f.Class + (f.Class == FieldClass.Excluded ? " (" + f.Exclude + ")" : "") +
                                 ", not LeafDict. A LeafDict ships PER-KEY deltas; anything else ships the " +
                                 "WHOLE member as one value, so moving a single worker resends the entire " +
                                 "personnel pool to every peer.";

                // ═══ (b) and the codec really has a leaf kind for each value type ═══
                foreach (var f in rt.Fields.Where(f => f.Class == FieldClass.LeafDict))
                {
                    LeafKind kind;
                    if (!RailMeta.LeafKindOf(f.DictValType, out kind) || kind == LeafKind.Null)
                        yield return "L442 codec-cannot-carry-a-member: AssignState." + f.Name + " holds " +
                                     (f.DictValType == null ? "<null>" : f.DictValType.Name) + " values, for " +
                                     "which the leaf codec has no kind. The member would be dropped at encode " +
                                     "time rather than refused, which is this repo's dominant bug class.";
                }
            }

            // ═══ (c)+(d) the apply's two obligations ═══
            // (c) reads the apply's OWN IL — the ResyncWorkSlots call has to be in this method, where
            // deleting it is what the mutation test deletes. (d) reads the apply AND everything it calls in
            // this assembly: a forbidden TFTV wipe smuggled into RebuildSessions or RestoreIntDict is the
            // same defect one frame further down the stack.
            var literals = new HashSet<string>(Program.StringRefs(apply).Where(s => s != null), StringComparer.Ordinal);
            var reachable = new HashSet<string>(literals, StringComparer.Ordinal);
            foreach (var callee in Program.Callees(apply, typeof(AssignSync).Assembly))
                foreach (var s in Program.StringRefs(callee))
                    if (s != null) reachable.Add(s);
            if (!literals.Contains("ResyncWorkSlots"))
                yield return "L442 slot-pools-not-rewired: AssignSync.ClientApply no longer names " +
                             "ResyncWorkSlots. The mirrored assignment table would then be byte-correct while " +
                             "FacilitySlotPools.UsedSlots — the ONLY input the research and production output " +
                             "is actually computed from (Data.cs:895-906 → Workers.SetUsedSlots:331) — keeps " +
                             "whatever the client last derived locally. Both peers see the same board and run " +
                             "different economies, with nothing in any log.";
            foreach (var forbidden in new[] { "ClearAssignments", "RestoreAssignments" })
                if (reachable.Contains(forbidden))
                    yield return "L442 apply-is-load-shaped: AssignSync.ClientApply reaches TFTV's " + forbidden +
                                 ". " + (forbidden == "ClearAssignments"
                                     ? "That wipes the whole table, dropping the Character references the open " +
                                       "ASSIGNMENTS panel is holding — the clear-then-refill shape this design " +
                                       "exists to avoid."
                                     : "That is not a restore: it runs EnforceLivingCapacity and " +
                                       "TryAutoAssignUnassignedPersonnel, so the CLIENT would evict and " +
                                       "re-assign people locally against a table the host owns.");

            // ═══ (h) a partial apply may NOT close the gate ═══
            // The client's only retry mechanism is the gate staying open: a skipped id is one whose
            // GeoCharacter delta has not landed yet, and that arrival does NOT change AssignState, so a
            // fingerprint committed over skips means the retry never happens. The row — or, worse, the whole
            // training session — is then gone for the rest of the campaign, with a log line that says it
            // will be retried.
            if (!AssignAuthority.CommitApplyFingerprint(0, 0))
                yield return "L442 POSITIVE CONTROL: a COMPLETE apply refuses to commit its fingerprint, so " +
                             "the client rebuilds TFTV's statics by reflection and repaints the open screen " +
                             "on every single tick.";
            if (AssignAuthority.CommitApplyFingerprint(1, 0) || AssignAuthority.CommitApplyFingerprint(0, 1) ||
                AssignAuthority.CommitApplyFingerprint(2, 3))
                yield return "L442 partial-apply-closes-the-gate: a PARTIAL apply commits its fingerprint. " +
                             "The skipped ids are waiting on a GeoCharacter delta that does not touch " +
                             "AssignState, so nothing ever reopens the gate: those personnel rows and " +
                             "training sessions are lost for the rest of the campaign.";
            if (!Program.Callees(apply, typeof(AssignSync).Assembly).Any(m => m.Name == "CommitApplyFingerprint"))
                yield return "L442 partial-apply-closes-the-gate: AssignSync.ClientApply does not ask " +
                             "AssignAuthority.CommitApplyFingerprint, so the arm above asserts a decision " +
                             "production never consults.";

            // ═══ (i) an empty mirror is not truth until it has been full once ═══
            if (AssignAuthority.MirrorIsUsable(false, 0, 4))
                yield return "L442 empty-mirror-wipes-the-table: an EMPTY \"M#assign\" root is applied over a " +
                             "client that still holds live personnel rows. Before the first delta arrives — a " +
                             "joiner, or a host that has not projected yet — that root reads zero by default, " +
                             "so this deletes the whole assignment table and then recomputes the research and " +
                             "production economy from nothing.";
            if (!AssignAuthority.MirrorIsUsable(false, 3, 4) || !AssignAuthority.MirrorIsUsable(false, 0, 0) ||
                !AssignAuthority.MirrorIsUsable(true, 0, 4))
                yield return "L442 POSITIVE CONTROL: a non-empty mirror, an empty-over-empty apply, or a host " +
                             "that legitimately emptied its table after a real one had already arrived is " +
                             "refused. That is a client which can never converge again — the opposite of what " +
                             "the guard is for.";
            if (!Program.Callees(apply, typeof(AssignSync).Assembly).Any(m => m.Name == "MirrorIsUsable"))
                yield return "L442 empty-mirror-wipes-the-table: AssignSync.ClientApply does not ask " +
                             "AssignAuthority.MirrorIsUsable, so nothing stops the pre-first-delta wipe.";

            // ═══ (e) the reload-boundary reset covers every member ═══
            var state = new AssignState();
            Fill(dicts, state, 7);
            Fill(dicts, state, 9);
            clear.Invoke(state, null);
            foreach (var f in dicts)
                if (Count(f, state) != 0)
                    yield return "L442 boundary-leaves-state-behind: AssignState.Clear() left " + f.Name +
                                 " non-empty. A mod root that is not empty at a reload boundary is captured by " +
                                 "the post-reload BASELINE walk and therefore never emitted again — the client " +
                                 "keeps the pre-reload value until somebody forces a full resend.";

            // ═══ (f) and the session prune covers every session member ═══
            Fill(dicts, state, 7);
            Fill(dicts, state, 9);
            prune.Invoke(state, new object[] { new HashSet<int> { 7 } });
            foreach (var f in dicts)
            {
                bool isSession = f.Name.StartsWith("Session", StringComparison.Ordinal);
                int count = Count(f, state);
                if (isSession && count != 1)
                    yield return "L442 prune-forgets-a-member: AssignState.PruneSessions left " + f.Name +
                                 " with " + count + " entr(y/ies) instead of 1 — a session the host ended is " +
                                 "still half-present in the mirror, so every client keeps painting a ghost " +
                                 "training row for the rest of the campaign.";
                if (!isSession && count != 2)
                    yield return "L442 prune-forgets-a-member: AssignState.PruneSessions touched " + f.Name +
                                 ", which is NOT a session member. The assignment table and the two stat " +
                                 "dictionaries have their own key sets and their own prune; pruning them here " +
                                 "deletes live rows.";
            }

            // ═══ (g) the client's change gate is a real function of the content ═══
            var a = new AssignState();
            var b = new AssignState();
            if ((int)fingerprint.Invoke(a, null) != (int)fingerprint.Invoke(b, null))
                yield return "L442 mirror-never-applies: two IDENTICAL AssignStates fingerprint differently, so " +
                             "the client re-applies the whole mirror every tick — a per-frame reflection " +
                             "rebuild of TFTV's statics and a per-frame repaint of the open screen.";
            b.AssignRole[7] = AssignSync.RoleResearch;
            if ((int)fingerprint.Invoke(a, null) == (int)fingerprint.Invoke(b, null))
                yield return "L442 mirror-never-applies: an empty table and a table with one assigned worker " +
                             "fingerprint the SAME, so the client's change gate never opens. The mirror would " +
                             "apply once and then never again — which looks like a working surface until the " +
                             "first reassignment and diverges from there on.";
        }

        private static void Fill(FieldInfo[] dicts, AssignState state, int key)
        {
            foreach (var f in dicts)
            {
                var d = (IDictionary)f.GetValue(state);
                var valueType = f.FieldType.GetGenericArguments()[1];
                d[key] = valueType == typeof(string) ? "" : Activator.CreateInstance(valueType);
            }
        }

        private static int Count(FieldInfo f, AssignState state) => ((IDictionary)f.GetValue(state)).Count;
    }
}
