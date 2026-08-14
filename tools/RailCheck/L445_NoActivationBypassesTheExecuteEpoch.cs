using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>
    /// L445 — NO ACTIVATION REACHES THE ENGINE EXCEPT THROUGH THE SHARED EXECUTE EPOCH.
    ///
    /// THE DEFECT, sixth regression of the same sentence (owner, 2026-08-14): "one player is still raising the
    /// weapon while on another instance the enemy is already dead". The shot was NOT the thing that was wrong.
    /// Shots were minted with <c>executeAt = PingTable.NowMs() + HostExecuteLeadMs()</c>, shipped with an
    /// epoch/id/executeAt header and released only by <c>TickScheduledActivations</c> once
    /// <c>ExecuteEpochReached</c> said so — on every peer including the host. MOVES were not. df426c9 split the
    /// retired op 1 into <c>OpActivate</c> (scheduled) and <c>OpMoveActivate</c> = op 8 (NOT scheduled), and op
    /// 8 was applied ON RECEIPT — the one <c>ApplyActivate</c> call outside the pump. So a move started at the
    /// click on the acting peer and half an RTT later on every other, and that skew then LEAKED INTO THE SHOT:
    /// a shot released at the shared epoch enters the engine's per-actor channel
    /// (<c>ShootAbility.Activate</c>:173, <c>EnqueueAction(soloAfterCurrent:true)</c>), so on a peer whose move
    /// was still running the shot queued BEHIND it. The animator normalization
    /// (<c>ShouldNormalizeRelayedShoot</c>) fixes the baseline, never the start.
    ///
    /// WHY THIS LAW EXISTS AND THE EXISTING ONES DID NOT CATCH IT. L230 asserts the timing architecture, but
    /// structurally and for ONE op — <c>HandleActivate</c> reaches <c>QueueActivation</c>, the pump reaches
    /// <c>ApplyActivate</c> — all of which stayed true while op 8 quietly ran beside it. L104's header CLAIMS
    /// the action anchor and only checks camera-tail suppression, aim anchor and animator normalization. Both
    /// were green through the whole defect. The property neither of them states is a NEGATIVE one, and it is
    /// the only one that closes the hole: nothing may start an activation except the pump.
    ///
    /// THE ARMS:
    ///   (a) <c>activation-bypasses-the-pump</c> — over the WHOLE assembly, every method (lambdas and
    ///       compiler-generated closures included) whose IL calls <c>ApplyActivate</c> must be
    ///       <c>TickScheduledActivations</c>. Exactly one caller, named. A second caller IS the defect,
    ///       whatever op it decodes.
    ///   (b) <c>activation-outside-the-mirror</c> — inside <c>TacticalCommandSync</c> and its closures, the
    ///       only method that may call <c>TacticalAbility.Activate</c> at all is <c>ApplyActivate</c>. This is
    ///       the shape the deleted <c>HandleActivate</c> early-return had (<c>ability.Activate(target)</c> for
    ///       an <c>IMoveAbility</c>): it never touched <c>ApplyActivate</c>, so arm (a) alone would not see it.
    ///   (c) <c>inbound-skips-the-queue</c> — <c>ApplyInbound</c>'s decode/commit family must still reach
    ///       <c>QueueActivation</c>. Arms (a) and (b) are satisfiable by deleting the commit path entirely;
    ///       this one keeps the record actually landing on the schedule.
    ///   (d) <c>retired-move-op-reused</c> — no wire-op constant on <c>TacticalCommandSync</c> may hold the
    ///       value 8. Op 8 shipped a move with no executeAt; reusing the number means a peer on the old build
    ///       has its move record decoded as something else instead of being refused out loud, which is a
    ///       SILENT desync — the one failure mode retiring a number exists to prevent (op 1's precedent).
    ///   (e) POSITIVE CONTROL, EXECUTED — <see cref="FakeBypass"/> commits all four violations and is scanned
    ///       by the same four functions. Each must go red on it, or the green above is a walk that resolved
    ///       nothing.
    ///
    /// NOT A QUORUM. Scheduling against the host's clock waits on a NUMBER, not on a player: the pump releases
    /// on time whether every other peer is AFK, loading or gone, and a peer with no usable clock sample
    /// degrades to a bounded local lead rather than waiting for anyone.
    ///
    /// Falsify: restore the op-8 direct-apply branch in <c>ApplyInbound</c> -> (a); restore the
    /// <c>IMoveAbility</c> early-return in <c>HandleActivate</c> -> (b); delete the queue call from the
    /// decoder -> (c); reintroduce <c>OpMoveActivate = 8</c> -> (d).
    /// </summary>
    internal static class L445_NoActivationBypassesTheExecuteEpoch
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var apply = sync.GetMethod("ApplyActivate", All);
            var pump = sync.GetMethod("TickScheduledActivations", All);
            var queue = sync.GetMethod("QueueActivation", All);
            var inbound = sync.GetMethod("ApplyInbound", All);
            // Resolved through the HIERARCHY on purpose: the call the compiler emits for
            // ability.Activate(target) binds to Ability.Activate, the base declaration — asserting on
            // TacticalAbility's own member would have made this arm scan for a call site that never exists.
            var activate = typeof(TacticalAbility).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                              BindingFlags.Instance)
                                                  .FirstOrDefault(m => m.Name == "Activate");

            if (apply == null || pump == null || queue == null || inbound == null || activate == null)
            {
                yield return "L445 premise-changed: one of TacticalCommandSync.{ApplyActivate, " +
                             "TickScheduledActivations, QueueActivation, ApplyInbound} or " +
                             "the Ability.Activate the mirror calls no longer resolves. Every arm below is written over " +
                             "those exact seams, so it would be asserting about a shape the build no longer " +
                             "has — re-audit the activation path before re-pointing this law.";
                yield break;
            }

            foreach (var v in ScanSoleCaller(AssemblyMethods(sync.Assembly), apply, "TickScheduledActivations",
                                             "TacticalCommandSync.ApplyActivate"))
                yield return v;
            foreach (var v in ScanNativeStart(FamilyOf(sync), "ApplyActivate", "TacticalCommandSync"))
                yield return v;
            foreach (var v in ScanCommitReachesQueue(FamilyOf(sync), "ApplyInbound", "QueueActivation"))
                yield return v;
            foreach (var v in ScanRetiredOp(sync, "TacticalCommandSync")) yield return v;

            // ── arm (e): every arm must be able to SEE its own violation ──────────────────────────────
            var fake = typeof(FakeBypass);
            var fakeApply = fake.GetMethod("Apply", All);
            var control = ScanSoleCaller(FamilyOf(fake), fakeApply, "Pump", "FakeBypass.Apply")
                .Concat(ScanNativeStart(FamilyOf(fake), "Apply", "FakeBypass"))
                .Concat(ScanCommitReachesQueue(FamilyOf(fake), "Inbound", "QueueActivation"))
                .Concat(ScanRetiredOp(fake, "FakeBypass"))
                .ToList();
            foreach (var want in new[] { "activation-bypasses-the-pump", "activation-outside-the-mirror",
                                         "inbound-skips-the-queue", "retired-move-op-reused" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L445 control-not-red: FakeBypass commits " + want + " and the scan did not " +
                                 "flag it. That arm cannot tell the scheduled shape from the on-receipt one, " +
                                 "so its green above proves nothing — which is exactly how op 8 lived beside " +
                                 "two green laws for six regressions.";
        }

        /// <summary>Arm (a) — exactly one caller, and it is the pump.</summary>
        private static IEnumerable<string> ScanSoleCaller(IEnumerable<MethodBase> family, MethodBase target,
                                                          string allowedCaller, string label)
        {
            if (target == null) yield break;
            var callers = family.Where(m => Calls(m, target)).Select(Owner).Distinct().ToList();
            if (callers.Count == 0)
                yield return "L445 activation-bypasses-the-pump: nothing in the assembly calls " + label +
                             " at all. A mirror that is never applied is not a fixed timing model, it is a " +
                             "battle where no peer ever plays anybody else's order.";
            foreach (var c in callers.Where(c => !c.Contains(allowedCaller)))
                yield return "L445 activation-bypasses-the-pump: " + c + " calls " + label + " outside " +
                             allowedCaller + ". That is op 8 verbatim — an activation applied when the packet " +
                             "ARRIVES instead of when the shared epoch is reached, so it starts at the click " +
                             "on one peer and half an RTT later on every other. The skew does not stay in that " +
                             "action either: the next shot is released on time and then queues behind the " +
                             "still-running one in the engine's per-actor channel (ShootAbility.Activate:173).";
        }

        /// <summary>Arm (b) — the engine is entered from the mirror and nowhere else.</summary>
        private static IEnumerable<string> ScanNativeStart(IEnumerable<MethodBase> family, string allowedCaller,
                                                           string label)
        {
            foreach (var c in family.Where(StartsAnAbility)
                                    .Select(Owner).Distinct().Where(c => !c.Contains(allowedCaller)))
                yield return "L445 activation-outside-the-mirror: " + label + "." + c + " calls " +
                             "TacticalAbility.Activate directly. Only " + allowedCaller + " may hand an actor " +
                             "to the engine, because it is the only entry the pump gates on the shared " +
                             "executeAt. A direct call here never touches ApplyActivate, so the sole-caller " +
                             "arm cannot see it: this is the exact IMoveAbility early-return that made moves " +
                             "start on each peer's own clock.";
        }

        /// <summary>Arm (c) — the decoded record still lands on the schedule.</summary>
        private static IEnumerable<string> ScanCommitReachesQueue(IEnumerable<MethodBase> family,
                                                                  string decoder, string queueName)
        {
            var decode = family.Where(m => Owner(m).Contains(decoder)).ToList();
            if (decode.Count == 0 || !decode.Any(m => Reaches(m, null, queueName)))
                yield return "L445 inbound-skips-the-queue: " + decoder + " and its commit closures no longer " +
                             "reach " + queueName + ". Deleting the commit path satisfies every 'nothing " +
                             "applies early' arm perfectly and mirrors nothing at all — the schedule has to be " +
                             "where the record actually goes.";
        }

        /// <summary>Arm (d) — op 8 stays dead.</summary>
        private static IEnumerable<string> ScanRetiredOp(Type owner, string label)
        {
            foreach (var f in owner.GetFields(All).Where(f => f.IsLiteral && f.FieldType == typeof(byte) &&
                                                              f.Name.StartsWith("Op", StringComparison.Ordinal)))
            {
                object raw = null;
                try { raw = f.GetRawConstantValue(); } catch { }
                if (raw is byte && (byte)raw == 8)
                    yield return "L445 retired-move-op-reused: " + label + "." + f.Name + " is wire op 8, which " +
                                 "is RETIRED. Op 8 carried a move with no actionId and no executeAt; a peer " +
                                 "still running that build must hit ApplyInbound's unknown-op refusal — loud " +
                                 "and terminal — rather than have its bytes decoded as a different record. " +
                                 "Reusing a retired number turns a version mismatch into a silent desync, the " +
                                 "same reason op 1 is still dead.";
            }
        }

        /// <summary>ARM (e). Never registered, never called at runtime — it exists only to be walked. One
        /// violation per arm: a second caller of its own apply, a direct Activate outside it, a decoder that
        /// never queues, and the retired op number back on a constant.</summary>
        private static class FakeBypass
        {
            internal const byte OpMoveActivate = 8;                 // (d): the dead number, reused

            internal static void Apply(TacticalAbility ability) { }

            internal static void Pump(TacticalAbility ability) { Apply(ability); }

            internal static void Inbound(TacticalAbility ability) { Apply(ability); }   // (a) + (c)

            internal static void HandleMove(TacticalAbility ability) { ability.Activate(null); }  // (b)
        }

        /// <summary>A method and the closures the compiler lifted out of it are ONE author. Reporting
        /// <c>&lt;ApplyInbound&gt;b__12_1</c> as an unrelated caller would let the real seam hide behind a
        /// lambda, which is precisely where op 8's apply lived.</summary>
        private static string Owner(MethodBase m)
        {
            var t = m.DeclaringType;
            string name = m.Name;
            int open = name.IndexOf('<'), close = name.IndexOf('>');
            if (open == 0 && close > 1) return name.Substring(1, close - 1);
            while (t != null && t.IsNested && t.Name.StartsWith("<", StringComparison.Ordinal)) t = t.DeclaringType;
            return name;
        }

        private static IEnumerable<MethodBase> FamilyOf(Type t)
        {
            foreach (var m in t.GetMethods(All)) yield return m;
            foreach (var n in t.GetNestedTypes(All))
                foreach (var m in n.GetMethods(All)) yield return m;
        }

        private static IEnumerable<MethodBase> AssemblyMethods(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                MethodBase[] members;
                try { members = t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).ToArray(); }
                catch { continue; }
                foreach (var m in members) yield return m;
            }
        }

        /// <summary>Does this method hand an actor to the engine? Named by the ABILITY HIERARCHY, not by one
        /// class: <c>ability.Activate(target)</c> emits a call to <c>Ability.Activate</c> even where the
        /// static type is <c>TacticalAbility</c>, and a subclass override would emit a third name again.</summary>
        private static bool StartsAnAbility(MethodBase caller)
            => CalleesOf(caller).Any(c => c.Name == "Activate" && c.DeclaringType != null &&
                                          (c.DeclaringType.Name == "Ability" ||
                                           c.DeclaringType.Name == "TacticalAbility"));

        private static bool Calls(MethodBase caller, MethodBase target)
            => CalleesOf(caller).Any(c => c.MetadataToken == target.MetadataToken && c.Module == target.Module);

        private static bool Reaches(MethodBase caller, string declaringType, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName &&
                                          (declaringType == null || (c.DeclaringType != null &&
                                                                     c.DeclaringType.Name == declaringType)));

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F, 0x73))
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m == null ? null : m.GetMethodBody() == null ? null : m.GetMethodBody().GetILAsByteArray(); }
            catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
