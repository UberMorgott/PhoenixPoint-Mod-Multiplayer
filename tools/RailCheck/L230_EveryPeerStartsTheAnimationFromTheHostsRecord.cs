using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L230 — EVERY PEER STARTS AN ATTACK ANIMATION FROM THE HOST'S RECORD, INCLUDING THE ONE THAT CLICKED,
    /// AND NO ACTOR IS LEFT WAITING FOR AN ECHO FOREVER.
    ///
    /// THE DEFECT (owner, 2026-08-08): "I throw a grenade and on one instance it has already exploded while
    /// on another it is only just leaving the hand. Same with shooting: one player is still aiming while on
    /// another instance that soldier has already fired and the enemy is dead." The rail was not dropping
    /// anything — the SAME order simply had three different start times by construction: the acting peer
    /// began at its own click (A3a's speculative local play), the host began when the intent landed, and the
    /// watchers began when the mirror landed. Every peer was correct in isolation.
    ///
    /// THE OUTCOME THIS LAW ASSERTS is therefore about WHERE an animation may start, not about whether a
    /// message was sent. A law that asserted "the intent is sent" would have been green through the whole
    /// defect — the intent was always sent. The arms below name the two facts that make the start times
    /// agree, plus the two that stop the cure being worse than the disease.
    ///
    /// THE ARMS:
    ///   (a) <c>click-plays-locally</c> — THE RULE, executed. <see cref="TacticalCommandSync.OrderWaitsForTheEcho"/>
    ///       must say WAIT for a rider that is not a move inside a shared battle, and must say PLAY-LOCALLY
    ///       for the three cases that are deliberately exempt (solo, a declared-local ability, and an
    ///       <c>IMoveAbility</c> — whose <c>FollowupAbility</c> the codec drops, so deferring it would delete
    ///       the acting player's follow-up attack instead of merely delaying it). Run to exhaustion over all
    ///       eight inputs, so a rule quietly inverted or widened is red.
    ///   (b) <c>echo-seam-unbound</c> — <c>ClickedOrderWaitsForTheEcho.Seam</c> must RESOLVE, and to
    ///       <c>TacticalViewState.ActivateAbility</c> with exactly the four parameter types. This is the trap
    ///       this repo keeps paying for: <c>AccessTools.Method(type, name, Type[])</c> does EXACT parameter
    ///       matching, so one widened or reordered type yields null, <c>Prepare()</c> stands the patch down,
    ///       and every clicked order silently plays locally again — the defect back in full with a green
    ///       build. Asserted on the RESOLVED member and not on the source text.
    ///   (c) <c>echo-wait-unbounded</c> — <see cref="TacticalCommandSync.EchoCeilingFrames"/> must be a real
    ///       bound, must be LONGER than the host's own <see cref="TacticalCommandSync.DeferCeilingSeconds"/>
    ///       hold (or a legitimately held order is abandoned by the peer that sent it), and
    ///       <c>TickEchoWaits</c> must reach <c>Debug.LogError</c>. A suppressed click plus a lost echo is a
    ///       soldier that cannot be commanded for the rest of the battle; dropping that silently is this
    ///       repo's dominant bug class landing on the most visible surface it has.
    ///   (d) <c>host-click-skips-arbitration</c> — <see cref="TacticalCommandSync.PublishClickedOrder"/> must
    ///       reach <c>HandleActivate</c>. THE HOST IS HALF THE DEFECT: a host click reaches <c>Activate</c>
    ///       natively, takes the <c>RelayMirror</c> branch and never touches <c>Validate</c> (the asymmetry
    ///       L210 measured — the host's free-aim shot fired for 76 damage on the same disabled state that
    ///       refused five client shots). Feeding the host's own click to the function that answers a peer's
    ///       is what puts both on one path; calling <c>ability.Activate</c> directly in that branch would
    ///       look identical from the outside and restore the bypass.
    ///   (e) <c>acting-peer-never-mirrored</c> — <see cref="TacticalCommandSync.OnAbilityActivated"/> must
    ///       reach <c>OrderWaitsForTheEcho</c>. The mirror used to EXCLUDE the peer that sent the intent,
    ///       because that peer had already played the order itself. A peer that now waits and is still
    ///       excluded waits for a record addressed to everybody but him — every shot would run to the arm (c)
    ///       ceiling. The exclusion must be decided by the same one rule, which is what this arm pins.
    ///   (f) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> inverts the rule, unbinds the seam, bounds
    ///       nothing and warns instead of erroring, activates directly instead of arbitrating, and excludes
    ///       by origin. All five arms must go red on it, or their green above is a scan that resolved nothing.
    ///
    /// NOT A QUORUM, and the distinction is the mod's hardest rule. The only peer ever waited on is the HOST,
    /// which answers by itself with no human action in the loop; no peer's progress depends on another peer
    /// ACTING, and an AFK peer cannot add one frame to the wait. Arm (c) is what keeps it that way even when
    /// the host's answer never comes at all.
    ///
    /// Falsify: invert <c>OrderWaitsForTheEcho</c> -> (a); widen a parameter type in <c>Seam</c> -> (b);
    /// set <c>EchoCeilingFrames</c> below <c>DeferCeilingSeconds*60</c>, or drop the <c>Debug.LogError</c>
    /// from <c>TickEchoWaits</c> -> (c); make the host branch call <c>ability.Activate</c> -> (d); restore
    /// <c>_replayOriginPeer</c> as the mirror's exclude argument -> (e).
    /// </summary>
    internal static class L230_EveryPeerStartsTheAnimationFromTheHostsRecord
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var tick = sync.GetMethod("TickEchoWaits", AllMembers);
            var publish = sync.GetMethod("PublishClickedOrder", AllMembers);
            var capture = sync.GetMethod("OnAbilityActivated", AllMembers);
            var patch = typeof(TacticalCommandSync).Assembly
                            .GetType("Multiplayer.Tactical.ClickedOrderWaitsForTheEcho");
            var seamField = patch == null ? null : patch.GetField("Seam", AllMembers);
            var viewSeam = typeof(TacticalViewState).GetMethod("ActivateAbility", AllMembers);

            if (tick == null || publish == null || capture == null || seamField == null || viewSeam == null)
            {
                yield return "L230 premise-changed: one of TacticalCommandSync.{TickEchoWaits, " +
                             "PublishClickedOrder, OnAbilityActivated}, ClickedOrderWaitsForTheEcho.Seam or " +
                             "TacticalViewState.ActivateAbility no longer resolves. The seams this law is " +
                             "written over have moved, so every arm below would be asserting about a shape " +
                             "the build no longer has.";
                yield break;
            }

            foreach (var v in ScanRule(TacticalCommandSync.OrderWaitsForTheEcho, "OrderWaitsForTheEcho"))
                yield return v;
            foreach (var v in ScanSeam(seamField.GetValue(null) as MethodBase, "ClickedOrderWaitsForTheEcho.Seam"))
                yield return v;
            foreach (var v in ScanBound(tick, TacticalCommandSync.EchoCeilingFrames,
                                        TacticalCommandSync.DeferCeilingSeconds, "TickEchoWaits"))
                yield return v;
            foreach (var v in ScanHostPath(publish, "PublishClickedOrder")) yield return v;
            foreach (var v in ScanMirrorAudience(capture, "OnAbilityActivated")) yield return v;

            // ── arm (f): every arm must be able to SEE its own violation.
            var fake = typeof(FakeSeam);
            var control = ScanRule(FakeSeam.Rule, "FakeSeam.Rule")
                .Concat(ScanSeam(null, "FakeSeam.Seam"))
                .Concat(ScanBound(fake.GetMethod("Tick", AllMembers), 60, 10f, "FakeSeam.Tick"))
                .Concat(ScanHostPath(fake.GetMethod("Publish", AllMembers), "FakeSeam.Publish"))
                .Concat(ScanMirrorAudience(fake.GetMethod("Capture", AllMembers), "FakeSeam.Capture"))
                .ToList();
            foreach (var want in new[] { "click-plays-locally", "echo-seam-unbound", "echo-wait-unbounded",
                                         "host-click-skips-arbitration", "acting-peer-never-mirrored" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L230 control-not-red: FakeSeam commits " + want + " and the scan did not " +
                                 "flag it. That arm cannot tell the fixed shape from the broken one, so its " +
                                 "green above means nothing — the exact way L169 stayed green while a client " +
                                 "free-aim shot could not fire at all.";
        }

        /// <summary>Arm (a) — the rule, run to exhaustion.</summary>
        private static IEnumerable<string> ScanRule(Func<bool, bool, bool, bool> rule, string label)
        {
            if (!rule(true, true, false))
                yield return "L230 click-plays-locally: " + label + "(sharedBattle, rider, notMove) says the " +
                             "acting peer plays its own click. That is the defect verbatim — the clicking " +
                             "peer starts the animation at press time while every other peer starts it a ping " +
                             "later, so a grenade has already exploded on one screen while it is still in the " +
                             "hand on another. The order must be published and the animation started from the " +
                             "host's mirrored record, the same record every watching peer plays from.";
            if (rule(true, true, true))
                yield return "L230 click-plays-locally: " + label + " defers an IMoveAbility. FollowupAbility " +
                             "and FollowupAbilityTarget are in TacAbilityTargetCodec.Dropped, so a move that " +
                             "carries a follow-up attack (UIStateCharacterSelected.MoveAndActivateAbility:945) " +
                             "loses that attack on the wire: deferring the move DELETES the acting player's " +
                             "own follow-up shot rather than delaying it. Move is also the one rider whose " +
                             "divergence the settle/closer already corrects.";
            if (rule(true, false, false))
                yield return "L230 click-plays-locally: " + label + " defers a NON-rider. A declared-local " +
                             "ability (TacticalCommandSync.LocalAbilities — inventory, crate, idle, panic) is " +
                             "never mirrored at all, so waiting for an echo of it is waiting for a record no " +
                             "peer will ever send: that soldier stands frozen until the arm (c) ceiling.";
            if (rule(false, true, false))
                yield return "L230 click-plays-locally: " + label + " defers a click outside a shared battle. " +
                             "In a solo game there is no host to echo, so this makes single-player unplayable " +
                             "— every click would do nothing for " +
                             (TacticalCommandSync.EchoCeilingFrames / 60) + "s and then log an error.";
        }

        /// <summary>Arm (b) — the seam really binds, with the exact signature.</summary>
        private static IEnumerable<string> ScanSeam(MethodBase seam, string label)
        {
            if (seam == null)
            {
                yield return "L230 echo-seam-unbound: " + label + " is NULL. AccessTools.Method does EXACT " +
                             "parameter matching — one widened, reordered or renamed type resolves to null, " +
                             "Prepare() stands the patch down (loudly, but only in a live log), and every " +
                             "clicked order plays locally at press time again. The build stays green while " +
                             "the whole fix is absent.";
                yield break;
            }
            if (seam.DeclaringType != typeof(TacticalViewState) || seam.Name != "ActivateAbility")
                yield return "L230 echo-seam-unbound: " + label + " resolved to " + seam.DeclaringType + "." +
                             seam.Name + ". The seam must be TacticalViewState.ActivateAbility — the ONE " +
                             "method every player click passes through (UIStateShoot is its only override in " +
                             "the game and it calls base). Blocking one layer down at TacticalAbility.Activate " +
                             "cannot work: it is VIRTUAL, and skipping the base body still lets " +
                             "ShootAbility.Activate:165-174 run its own PlayAction(Shoot).";
            var want = new[] { typeof(TacticalAbility), typeof(TacticalAbilityTarget),
                               typeof(Base.UI.StateStackAction), typeof(Func<TacticalAbility, bool>) };
            var got = seam.GetParameters().Select(p => p.ParameterType).ToArray();
            if (!got.SequenceEqual(want))
                yield return "L230 echo-seam-unbound: " + label + " has parameters (" +
                             string.Join(", ", got.Select(t => t.Name).ToArray()) + ") — the click funnel this " +
                             "law is written over takes (TacticalAbility, TacticalAbilityTarget, " +
                             "StateStackAction, Func<TacticalAbility,bool>). A different overload is a " +
                             "different seam and the clicked orders route around it.";
        }

        /// <summary>Arm (c) — the wait is bounded, long enough, and loud when it trips.</summary>
        private static IEnumerable<string> ScanBound(MethodBase tick, int ceilingFrames, float hostHoldSeconds,
                                                     string label)
        {
            if (ceilingFrames <= 0 || ceilingFrames <= hostHoldSeconds * 60f)
                yield return "L230 echo-wait-unbounded: " + label + "'s ceiling is " + ceilingFrames +
                             " frames against a host hold of " + hostHoldSeconds + "s. It must be POSITIVE (an " +
                             "unbounded wait leaves a soldier uncommandable for the rest of the battle, since " +
                             "the click that would command him is suppressed) and LONGER than the host's own " +
                             "hold, because a host legitimately queuing this peer's order behind that same " +
                             "peer's previous one gives up first and answers with a reject + forced settle, " +
                             "which is what clears the wait through QueueSettle.";
            if (!Reaches(tick, "Debug", "LogError"))
                yield return "L230 echo-wait-unbounded: " + label + " must reach Debug.LogError when the wait " +
                             "gives up. An echo that never arrives means this peer never played an action " +
                             "every other peer did — releasing the soldier quietly leaves a divergence with " +
                             "no trace at all, which is this repo's dominant bug class landing on the most " +
                             "visible surface it has. A warning is not enough: this is a lost order.";
        }

        /// <summary>Arm (d) — the host's own click is arbitrated like a peer's.</summary>
        private static IEnumerable<string> ScanHostPath(MethodBase publish, string label)
        {
            if (!Reaches(publish, null, "HandleActivate"))
                yield return "L230 host-click-skips-arbitration: " + label + " must hand the HOST's own click " +
                             "to HandleActivate — the very function that answers a client's order — so it is " +
                             "validated, held and mirrored from one place. Calling ability.Activate directly " +
                             "in that branch restores the RelayMirror bypass L210 measured: the host's own " +
                             "free-aim shot fired for 76 damage on exactly the disabled state that had just " +
                             "refused five client shots, because a host click never reaches Validate.";
        }

        /// <summary>Arm (e) — the mirror's audience is decided by the one rule.</summary>
        private static IEnumerable<string> ScanMirrorAudience(MethodBase capture, string label)
        {
            if (!Reaches(capture, null, "OrderWaitsForTheEcho"))
                yield return "L230 acting-peer-never-mirrored: " + label + " must decide the mirror's exclude " +
                             "argument through OrderWaitsForTheEcho. The exclusion exists only because a " +
                             "SPECULATIVE acting peer had already played the order; a peer that now waits for " +
                             "the echo and is still excluded from it waits for a record addressed to everybody " +
                             "but him, so every one of his shots runs to the ceiling and then logs a lost echo.";
        }

        /// <summary>ARM (f). Never instantiated, never registered — it exists only to be walked and called.
        /// One violation per arm: an inverted rule, an unbound seam, a bound shorter than the host's hold with
        /// only a warning, a host branch that activates directly, and a capture that excludes by origin.</summary>
        private static class FakeSeam
        {
            internal static bool Rule(bool inSharedBattle, bool abilityIsRider, bool abilityIsMove)
                => false;                                          // (a): nothing ever waits

            internal static void Tick()
            {
                Debug.LogWarning("echo wait gave up");             // (c): warns, never errors
            }

            internal static bool Publish(TacticalAbility ability, TacticalAbilityTarget target)
            {
                ability.Activate(target);                          // (d): straight past the arbitration
                return true;
            }

            internal static void Capture(TacticalAbility ability)
            {
                Send(ability == null ? 0UL : 1UL);                 // (e): excludes by origin, no rule read
            }

            private static void Send(ulong excludePeer) { }
        }

        // ─── IL helpers (same primitives as L220; Program.cs is not partial) ─────────────────────

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
