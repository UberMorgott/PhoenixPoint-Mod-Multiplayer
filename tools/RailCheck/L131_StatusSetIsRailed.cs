using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L131 — EVERY PEER ENDS WITH THE SAME STATUS SET, AND THEREFORE THE SAME PASSENGER ROSTER.
    ///
    /// THE HOLE. hp, ap, wp, dead and every body part's hp/armor rode the actor rail; the STATUS SET rode
    /// nothing. Statuses existed on each peer purely as a side effect of replaying the ability that applied
    /// them, so a dropped order, an order queued behind a stuck mirror, one the host refused, or a coroutine
    /// torn by a forced settle left a status the host had and a peer did not — and nothing anywhere
    /// reconciled it, in a battle or in the resnapshot that exists precisely to reconcile things.
    /// <c>VehicleComponent.Passengers</c> is the loudest instance because it is written ONLY from
    /// <c>MountedStatus.OnApply/OnUnapply</c>:41/:95 — who is inside an Armadillo was, literally, a replay
    /// artefact — but the hole is the FIELD CLASS, not the vehicle: stances, buffs, panic, the vehicle's own
    /// empty/occupied status and every modded status shared it.
    ///
    /// WHAT THIS LAW ASSERTS IS THE OUTCOME, not that a call happens. Arm (a) EXECUTES the reconcile plan and
    /// then applies it to the local multiset, asserting the result IS the host's multiset — the plan is what
    /// makes the two peers equal, so a plan that is merely called is worth nothing. Arm (b) round-trips the
    /// codec, because a one-sided change to it desynchronises the whole 0x82/0x84 stream rather than one
    /// status. Arm (c) is the only structural one: a perfect plan on a set that never crosses the wire
    /// converges nothing, so both host state ops must WRITE it and both applies must RECONCILE it.
    ///
    /// Falsify: drop the `unapply.Add(k)` line from <c>TacticalStatusSet.Plan</c> →
    /// <c>L131 plan-leaves-a-status-behind</c>; drop the <c>TacticalStatusSet.Write</c> call from
    /// <c>HostSettle</c> → <c>L131 settle-carries-no-statuses</c>; drop the <c>Reconcile</c> call from
    /// <c>ApplySettle</c> → <c>L131 settle-applies-no-statuses</c>.
    /// </summary>
    internal static class L131_StatusSetIsRailed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // Keys as the rail mints them: "<actorKeyTheStatusNames>@<statusDefName>".
        private const string Mounted = "-7@MountedStatus_StatusDef";
        private const string MountedElsewhere = "-9@MountedStatus_StatusDef";
        private const string Panic = "0@Panic_StatusDef";
        private const string Buff = "0@Rage_StatusDef";

        internal static IEnumerable<string> Check()
        {
            var set = typeof(TacticalStatusSet);
            var plan = set.GetMethod("Plan", All);
            var write = set.GetMethod("Write", All);
            var read = set.GetMethod("Read", All);
            var collect = set.GetMethod("Collect", All);
            var reconcile = set.GetMethod("Reconcile", All);
            if (plan == null || write == null || read == null || collect == null || reconcile == null)
            {
                yield return "L131 premise-changed: TacticalStatusSet.{Plan,Write,Read,Collect,Reconcile} no " +
                             "longer resolves. The status set is the one field class of an actor that has no " +
                             "other carrier — re-read this law before assuming a passenger roster still " +
                             "converges.";
                yield break;
            }

            // ── (a) THE OUTCOME: after the plan, the sets are EQUAL ──────────
            foreach (var v in Converges("identical", new[] { Panic, Mounted }, new[] { Panic, Mounted })) yield return v;
            foreach (var v in Converges("host has a status this peer lost",
                                        new[] { Panic }, new[] { Panic, Mounted })) yield return v;
            foreach (var v in Converges("this peer has a status the host does not",
                                        new[] { Panic, Mounted }, new[] { Panic })) yield return v;
            foreach (var v in Converges("mounted in the WRONG vehicle",
                                        new[] { MountedElsewhere }, new[] { Mounted })) yield return v;
            foreach (var v in Converges("stacks", new[] { Buff }, new[] { Buff, Buff })) yield return v;
            foreach (var v in Converges("this peer is empty", new string[0], new[] { Panic, Mounted })) yield return v;
            foreach (var v in Converges("the host is empty", new[] { Panic, Mounted }, new string[0])) yield return v;

            // The plan must also be MINIMAL: a plan that removes and re-applies what already agrees replays
            // every status's OnApply on every settle — which for MountedStatus means leaving and re-entering
            // the vehicle, with its animation, on every turn edge.
            {
                var apply = new List<string>();
                var unapply = new List<string>();
                TacticalStatusSet.Plan(new List<string> { Panic, Mounted }, new List<string> { Mounted, Panic },
                                       apply, unapply);
                if (apply.Count != 0 || unapply.Count != 0)
                    yield return "L131 plan-churns-agreeing-statuses: two peers that already hold the same set " +
                                 "are still given " + apply.Count + " apply(s) and " + unapply.Count + " removal(s). " +
                                 "Every settle and every turn-edge sweep would then re-run each status's own " +
                                 "OnApply — for MountedStatus that is the passenger leaving and re-boarding the " +
                                 "vehicle, animation and all, several times a turn.";
            }

            // ── (b) the codec: what the host wrote is what the peer reads ────
            {
                var sent = new List<string> { Mounted, Panic, Buff };
                List<string> got = null;
                string threw = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) TacticalStatusSet.Write(w, sent);
                        ms.Position = 0;
                        using (var r = new BinaryReader(ms, Encoding.UTF8, true)) got = TacticalStatusSet.Read(r);
                    }
                }
                catch (Exception ex) { threw = ex.Message; }
                if (threw != null)
                    yield return "L131 codec-throws: writing and reading a status set threw (" + threw + "). It " +
                                 "rides inside the 0x82 settle and the 0x84 resnapshot, so a throw here is not " +
                                 "one lost status — it is the rest of that message, for every actor behind it.";
                else if (got == null || !got.SequenceEqual(sent, StringComparer.Ordinal))
                    yield return "L131 codec-round-trip-lost: a status set written by the host reads back as [" +
                                 string.Join(", ", (got ?? new List<string>()).ToArray()) + "] instead of [" +
                                 string.Join(", ", sent.ToArray()) + "]. The two sides of this codec have " +
                                 "diverged, which misaligns every field after it in the same message.";
            }

            // ── (c) the set must actually CROSS and actually LAND ────────────
            var cmd = typeof(TacticalCommandSync);
            var dmg = typeof(Multiplayer.Tactical.TacticalDamageSync);
            var asm = set.Assembly;
            foreach (var v in MustReachInType(cmd, "Write", asm,
                     "L131 settle-carries-no-statuses: HostSettle no longer writes the status set. The settle is " +
                     "the ONLY routine carrier — it closes every rider action and the turn-edge sweep re-issues " +
                     "one for every keyed live actor — so without it the set converges only when a peer happens " +
                     "to notice a hole in the 0x84 stream, which a lost Enter order never causes.")) yield return v;
            foreach (var v in MustReachInType(dmg, "Write", asm,
                     "L131 resnap-carries-no-statuses: HandleResnapRequest no longer writes the status set. The " +
                     "resnapshot is the recovery path for exactly the divergences nothing else heals; leaving the " +
                     "statuses out of it is what made a desynchronised passenger roster permanent.")) yield return v;
            foreach (var v in MustReach(cmd.GetMethod("ApplySettle", All), "Reconcile", asm,
                     "L131 settle-applies-no-statuses: ApplySettle no longer reconciles the status set. That call " +
                     "is also what gives a TORN mirrored ability a defined terminal state — the cancel above it " +
                     "kills the coroutine wherever it stands, and this is what makes the leftovers the host's " +
                     "state rather than half of an entry into a vehicle.")) yield return v;
            foreach (var v in MustReach(dmg.GetMethod("ApplyResnap", All), "Reconcile", asm,
                     "L131 resnap-applies-no-statuses: ApplyResnap reads the host's status set and does nothing " +
                     "with it. The recovery path then repairs every field of an actor except the one it was " +
                     "extended to repair.")) yield return v;
        }

        /// <summary>The outcome arm: run the plan, apply it, and assert the peer's set IS the host's.</summary>
        private static IEnumerable<string> Converges(string what, string[] local, string[] host)
        {
            var apply = new List<string>();
            var unapply = new List<string>();
            TacticalStatusSet.Plan(local.ToList(), host.ToList(), apply, unapply);

            var after = local.ToList();
            foreach (var k in unapply) after.Remove(k);          // one instance per planned removal
            after.AddRange(apply);
            after.Sort(StringComparer.Ordinal);
            var want = host.ToList();
            want.Sort(StringComparer.Ordinal);

            if (!after.SequenceEqual(want, StringComparer.Ordinal))
                yield return "L131 plan-leaves-a-status-behind (" + what + "): after the reconcile plan this " +
                             "peer holds [" + string.Join(", ", after.ToArray()) + "] where the host holds [" +
                             string.Join(", ", want.ToArray()) + "]. The two peers are then playing different " +
                             "battles for the rest of the mission, and if the difference is a MountedStatus they " +
                             "disagree about who is inside a vehicle — which decides who dies with it.";
        }

        /// <summary>Both host writers hand their body to <c>Send</c> as a lambda, so the call lives in a
        /// compiler-generated closure method rather than in the named one — the question this arm can honestly
        /// ask is "does this rail type write the status set anywhere", and deleting the call still answers no.</summary>
        private static IEnumerable<string> MustReachInType(Type owner, string callee, Assembly asm, string violation)
        {
            foreach (var t in new[] { owner }.Concat(owner.GetNestedTypes(All)))
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                   .Concat(t.GetConstructors(All)))
                    if (Program.Callees(m, asm).Any(c => c.Name == callee &&
                                                         c.DeclaringType == typeof(TacticalStatusSet)))
                        yield break;
            yield return violation;
        }

        private static IEnumerable<string> MustReach(MethodBase caller, string callee, Assembly asm, string violation)
        {
            if (caller == null)
            {
                yield return violation + " [the method itself is gone, so this arm checked nothing]";
                yield break;
            }
            if (!Program.Callees(caller, asm).Any(c => c.Name == callee &&
                                                       c.DeclaringType == typeof(TacticalStatusSet)))
                yield return violation;
        }
    }
}
