using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;

namespace RailCheck
{
    /// <summary>
    /// L137 — "THIS SOLDIER'S TURN IS OVER" IS THE HOST'S ANSWER, ON EVERY PEER.
    ///
    /// THE HOLE, and it is the same FIELD CLASS L131 closed for statuses, one rung worse. Position, ap, wp, hp,
    /// dead, every body part and (since L131) the status set all ride the rail. The ABILITY-TRAIT set rode
    /// NOTHING — <c>grep AbilityTraits|HasEndedTurn|terminal</c> over <c>src/</c> was zero hits — and it is not
    /// a cosmetic field: <c>TacticalActor</c>:198 defines <c>HasEndedTurn =&gt; HasAbilityTrait("terminal")</c>,
    /// so whether a soldier may still be commanded at all is a STRING IN A LIST, written only as a side effect
    /// of replaying the ability that ended his turn (<c>TacticalAbility.ApplyAbilityTraits</c>:915-919 →
    /// <c>TacticalActor.SetAbilityTraits</c>:1317, a replace-set). Every way the replay can fail to happen
    /// therefore left a permanent, one-sided answer:
    ///  • the host REFUSED the order — the acting peer had already ended that soldier's turn on its own screen
    ///    and no host message could hand him back (the 2026-08-06 overwatch desync, L132);
    ///  • the mirror was dropped or queued behind a stuck ability, so the trait was never applied here;
    ///  • a forced settle TORE the mirrored ability mid-coroutine, so its <c>ApplyAbilityTraits</c> never ran.
    /// The class is wider than overwatch: every <c>EndsTurn</c> ability (<c>EndTurnAbility</c>,
    /// <c>EnterVehicleAbility</c>, <c>RamAbility</c>), <c>IsOnStandBy</c>, the turn-reset exemption
    /// <c>DoNotResetThisTurn</c> (<c>TacticalActor</c>:1241-1248) and every modded trait share it.
    ///
    /// THE FIX IS THE EXISTING SEAM, NOT A NEW ONE: the trait list rides the 0x82 settle beside the status set
    /// and is applied through the game's OWN replace-set. No surface id, no ability knowledge, and the
    /// turn-edge sweep already re-asserts a settle for every keyed live actor, so the set converges routinely
    /// rather than only when someone notices.
    ///
    /// WHAT THIS LAW ASSERTS IS THE OUTCOME. Arm (a) executes the SHIPPED control flow — the settle applies the
    /// host's list only when <see cref="TacticalCommandSync.TraitsDiffer"/> says the two disagree — and then
    /// asserts what the peer is LEFT HOLDING: the host's set, and therefore the host's answer to
    /// <c>HasEndedTurn</c>. A gate that wrongly reports "same" is not a missed log line, it is the divergence
    /// surviving the correction that existed to remove it. Arm (b) round-trips the codec, because it rides
    /// INSIDE the settle after the status set — a one-sided change misaligns every field behind it for every
    /// actor in the message, not one trait. Arm (c) is structural: a perfect verdict on a set that never
    /// crosses the wire converges nothing, and a set applied by a reflection poke instead of
    /// <c>SetAbilityTraits</c> would skip the game's own writer.
    ///
    /// Falsify: make <c>TraitsDiffer</c> return false for lists of equal length →
    /// <c>L137 divergence-survives-the-settle</c> and <c>L137 turn-stays-one-sided</c>; make it return true
    /// always → <c>L137 agreeing-sets-churn</c>; drop the <c>WriteTraits</c> call from <c>HostSettle</c> →
    /// <c>L137 settle-carries-no-traits</c>; drop <c>ApplyTraits</c> from <c>ApplySettle</c> →
    /// <c>L137 settle-applies-no-traits</c>; drop the <c>MarkDirty</c> from <c>ApplyTraits</c> →
    /// <c>L137 handed-back-soldier-stays-greyed-out</c>.
    /// </summary>
    internal static class L137_AbilityTraitSetIsRailed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The one the game reads for <c>HasEndedTurn</c> (<c>TacticalActor</c>:198), compared with
        /// <c>OrdinalIgnoreCase</c> by <c>HasAbilityTrait</c>:1330.</summary>
        private const string Terminal = "terminal";

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var differ = cmd.GetMethod("TraitsDiffer", All);
            var write = cmd.GetMethod("WriteTraits", All);
            var read = cmd.GetMethod("ReadTraits", All);
            var apply = cmd.GetMethod("ApplyTraits", All);
            var settle = cmd.GetMethod("ApplySettle", All);
            if (differ == null || write == null || read == null || apply == null || settle == null)
            {
                yield return "L137 premise-changed: TacticalCommandSync.{TraitsDiffer,WriteTraits,ReadTraits," +
                             "ApplyTraits,ApplySettle} no longer resolves. The ability-trait set is the carrier " +
                             "of HasEndedTurn and it has no other one — re-read this law before assuming a " +
                             "soldier's turn still means the same thing on two peers.";
                yield break;
            }

            // ── (a) THE OUTCOME: what is this peer left holding? ─────────────
            foreach (var v in Converges("this peer ended his turn and the host did NOT (the refused overwatch)",
                                        new[] { Terminal }, new[] { "start" })) yield return v;
            foreach (var v in Converges("the host ended his turn and this peer did not (a dropped mirror)",
                                        new[] { "start" }, new[] { Terminal })) yield return v;
            foreach (var v in Converges("a torn coroutine left this peer half way",
                                        new[] { "start" }, new[] { "start", Terminal })) yield return v;
            foreach (var v in Converges("this peer holds a trait the host does not",
                                        new[] { "start", "DoNotResetThisTurn" }, new[] { "start" })) yield return v;
            foreach (var v in Converges("identical", new[] { "start" }, new[] { "start" })) yield return v;
            foreach (var v in Converges("the host cleared everything", new[] { Terminal }, new string[0])) yield return v;
            foreach (var v in Converges("this peer is empty", new string[0], new[] { Terminal })) yield return v;
            // Same set, different ORDER is still the same answer to every question the game asks of it
            // (HasAbilityTrait/HasAbilityTraits are membership tests) — but the shipped compare is ordered, so
            // this pair legitimately re-writes the list. It must still CONVERGE, which is all the law claims.
            foreach (var v in Converges("re-ordered", new[] { Terminal, "start" }, new[] { "start", Terminal }))
                yield return v;
            // The comparer is the game's own: a trait that differs only in case is the SAME trait to
            // HasAbilityTrait, and treating it as a difference re-writes and re-logs on every turn edge.
            {
                var churn = TacticalCommandSync.TraitsDiffer(new List<string> { "Terminal" },
                                                             new List<string> { "terminal" });
                if (churn)
                    yield return "L137 agreeing-sets-churn: two peers holding the SAME trait in different case " +
                                 "are treated as disagreeing. HasAbilityTrait:1330 compares OrdinalIgnoreCase, so " +
                                 "this is a rewrite and a warning line for every keyed live actor on every " +
                                 "turn-edge sweep, reporting a divergence that does not exist.";
            }
            if (TacticalCommandSync.TraitsDiffer(new List<string> { "start" }, new List<string> { "start" }))
                yield return "L137 agreeing-sets-churn: two peers that already agree are still reconciled. The " +
                             "turn-edge sweep settles every keyed live actor, so this is ~140 needless rewrites " +
                             "and ~140 warning lines per faction turn, each one claiming a desync.";

            // ── (b) the codec: what the host wrote is what the peer reads ────
            {
                var sent = new List<string> { "start", Terminal, "DoNotResetThisTurn" };
                List<string> got = null;
                string threw = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) TacticalCommandSync.WriteTraits(w, sent);
                        ms.Position = 0;
                        using (var r = new BinaryReader(ms, Encoding.UTF8, true)) got = TacticalCommandSync.ReadTraits(r);
                    }
                }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null)
                    yield return "L137 codec-throws: writing and reading a trait set threw (" + threw + "). It is " +
                                 "the LAST field of the 0x82 settle, so a throw here does not lose one trait — it " +
                                 "aborts the settle that carries the position, the AP and the status set with it.";
                else if (got == null || !got.SequenceEqual(sent, StringComparer.Ordinal))
                    yield return "L137 codec-round-trip-lost: a trait set written by the host reads back as [" +
                                 string.Join(", ", (got ?? new List<string>()).ToArray()) + "] instead of [" +
                                 string.Join(", ", sent.ToArray()) + "]. The two sides of this codec have " +
                                 "diverged, which misaligns the settle stream itself.";
                // An empty set is the ordinary case for an actor that has not acted, and it must consume its
                // bytes exactly like a full one — the reader that stops early takes the rest of the message
                // with it.
                List<string> empty = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                        { TacticalCommandSync.WriteTraits(w, new List<string>()); w.Write(0x5EA1); }
                        ms.Position = 0;
                        using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                        {
                            empty = TacticalCommandSync.ReadTraits(r);
                            if (r.ReadInt32() != 0x5EA1)
                                empty = null;   // the reader ate bytes that were not its own
                        }
                    }
                }
                catch { empty = null; }
                if (empty == null || empty.Count != 0)
                    yield return "L137 empty-set-misreads-the-stream: an EMPTY trait set does not round-trip to an " +
                                 "empty list that leaves the stream exactly where it started. That is the common " +
                                 "case — every actor who has not acted — so this desynchronises the settle " +
                                 "reader for every message rather than for an unusual one.";
            }

            // ── (c) the set must CROSS, must LAND, and must land NATIVELY ────
            var asm = cmd.Assembly;
            foreach (var v in MustReachInType(cmd, "WriteTraits", cmd, asm,
                     "L137 settle-carries-no-traits: HostSettle no longer writes the trait set. The settle is the " +
                     "ONLY carrier — it closes every rider action and the turn-edge sweep re-issues one for every " +
                     "keyed live actor — so without it HasEndedTurn goes back to being whatever each peer " +
                     "happened to replay, and a soldier the host refused stays dead to input forever.")) yield return v;
            if (!Program.Callees(settle, asm).Any(c => c.MetadataToken == apply.MetadataToken))
                yield return "L137 settle-applies-no-traits: ApplySettle reads the host's trait set and does " +
                             "nothing with it. That call is also what gives a TORN mirrored ability a defined " +
                             "turn state — the cancel above it kills the coroutine before its own " +
                             "ApplyAbilityTraits runs, so this is the only thing that decides whether the soldier " +
                             "it left behind has ended his turn.";
            var game = typeof(TacticalActor).Assembly;
            if (!Program.Callees(apply, game).Any(c => c.Name == "SetAbilityTraits" &&
                                                       c.DeclaringType == typeof(TacticalActor)))
                yield return "L137 traits-not-applied-natively: ApplyTraits no longer goes through " +
                             "TacticalActor.SetAbilityTraits:1317. That method is the game's own replace-set " +
                             "(Clear + AddRange) and writing the readonly AbilityTraits list any other way is the " +
                             "reflection-poke shape this repo has already paid for twice.";
            if (!Program.Callees(apply, asm).Any(c => c.Name == "MarkDirty" &&
                                                      c.DeclaringType == typeof(TacticalUiRepaint)))
                yield return "L137 handed-back-soldier-stays-greyed-out: ApplyTraits changes HasEndedTurn and does " +
                             "not mark the tactical UI dirty. TacticalUiRepaint's own second dirty source is the " +
                             "gap between the painted AP/WP and the live ones — it cannot see a trait — and " +
                             "nothing native repaints an ability bar for a model change this peer did not click " +
                             "(UIModuleAbilities.SetAbilities runs on EnterState only). The soldier the host just " +
                             "handed back stays unusable on the screen that is watching him, which is postulate 1.";
        }

        /// <summary>The outcome arm: run the SHIPPED control flow (ApplyTraits replaces only when TraitsDiffer
        /// says so) and assert what this peer is left holding — the host's set, and the host's HasEndedTurn.</summary>
        private static IEnumerable<string> Converges(string what, string[] local, string[] host)
        {
            var after = TacticalCommandSync.TraitsDiffer(local, host) ? host.ToList() : local.ToList();

            var got = after.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            var want = host.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (!got.SequenceEqual(want, StringComparer.OrdinalIgnoreCase))
                yield return "L137 divergence-survives-the-settle (" + what + "): after the host's settle this " +
                             "peer holds [" + string.Join(", ", got.ToArray()) + "] where the host holds [" +
                             string.Join(", ", want.ToArray()) + "]. The correction ran and the two peers are " +
                             "still playing different battles.";

            bool hostEnded = host.Contains(Terminal, StringComparer.OrdinalIgnoreCase);
            bool peerEnded = after.Contains(Terminal, StringComparer.OrdinalIgnoreCase);
            if (hostEnded != peerEnded)
                yield return "L137 turn-stays-one-sided (" + what + "): after the settle the host says this " +
                             "soldier's turn is " + (hostEnded ? "OVER" : "NOT over") + " and this peer says it " +
                             "is " + (peerEnded ? "OVER" : "NOT over") + ". HasEndedTurn is TacticalActor:198 " +
                             "HasAbilityTrait(\"terminal\") and it gates whether that soldier can be commanded " +
                             "at all — this is the live desync verbatim: overwatch ends the turn on the acting " +
                             "client while every other peer may still walk him.";
        }

        /// <summary>HostSettle hands its body to <c>Send</c> as a lambda, so the call lives in a
        /// compiler-generated closure method rather than in the named one (the same reason L131 arm (c) is
        /// written this way). The honest question is "does this rail type write the trait set anywhere", and
        /// deleting the call still answers no.</summary>
        private static IEnumerable<string> MustReachInType(Type owner, string callee, Type calleeOwner,
                                                           Assembly asm, string violation)
        {
            foreach (var t in new[] { owner }.Concat(owner.GetNestedTypes(All)))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(All)))
                    if (Program.Callees(m, asm).Any(c => c.Name == callee && c.DeclaringType == calleeOwner))
                        yield break;
            yield return violation;
        }
    }
}
