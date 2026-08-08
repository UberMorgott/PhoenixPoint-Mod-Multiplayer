using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L231 — A REPLICATED ACTION RUNS ITS WHOLE ANIMATION BEFORE ITS OUTCOME LANDS, AND NO PEER IS LEFT
    /// AIMING WITH NO WAY OUT.
    ///
    /// THE DEFECT (owner, 2026-08-08, three live instances): "my soldier had finished shooting and the enemy
    /// died about two seconds later, and then that peer got stuck in aiming mode and no button did anything."
    /// L230 had just made every peer start the animation from the host's record, so the START times finally
    /// agreed — and the END times did not, because the host's own playback stalled while everybody else's ran.
    ///
    /// MEASURED, NOT ARGUED. Three logs of the same shot, host time in brackets:
    ///   · acting peer: mirror arrives +0.5 ms, animation runs, <c>FireWeaponAtTargetCrt() finished</c> at
    ///     309.068, damage lands at 310.419 and the target dies at 310.553 — 1.5 s after the shooter had
    ///     visibly stopped shooting.
    ///   · host: <c>wait while [AIM_START_ANIMATION_IS_ACTIVE]</c> at 536.391, "HOST aim-pose 3 -&gt; &lt;none&gt;"
    ///     at 536.408 — SEVENTEEN MILLISECONDS later — and then
    ///     <c>Actor Soldier_5 has timed out waiting for aim animation in the fire coroutine. Current
    ///     animation is HL_IdleAlert_AR</c> at 540.043. Three and a half seconds of nothing, on the one peer
    ///     that decides when damage is dealt.
    ///   · the third peer, which never touched that soldier: activation to damage 0.679 s, exactly the host's
    ///     own 0.650 s the time the stall did not happen. Nothing was dropped and no message was late.
    ///
    /// THE CAUSE, and it is a collision between two of our own surfaces rather than a bug in either. The
    /// acting peer's UI leaves <c>UIStateShoot</c> the instant its mirrored order arrives
    /// (<c>TacticalCommandSync.ReleaseLocalUiHolding</c>), so <c>TacticalAimPoseSync.Emit</c> correctly
    /// announces a stance that peer no longer holds. That clear reaches the host one frame INTO the shot,
    /// where <c>ApplyStance</c> called <c>PathProcessorUtils.SetNullNavParams</c> on the very animator
    /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1646-1665 had just written with
    /// <c>SetAimParams(AimStart)</c> and was spinning on. The "AimStart" checkpoint never cleared, so the
    /// coroutine sat out its whole five-second (actor-timed) timeout before firing.
    ///
    /// THE OUTCOME THIS LAW ASSERTS is therefore that an actor the ENGINE is driving keeps its animator: not
    /// that a guard was written, not that a message was sent. A law asserting "the stance is relayed" was
    /// green throughout — the stance was always relayed, on time, to the right peers, and it is what broke
    /// the shot.
    ///
    /// THE ARMS:
    ///   (a) <c>stance-stomps-a-driven-actor</c> — THE RULE, executed to exhaustion over all four inputs.
    ///       <see cref="TacticalAimPoseSync.StanceMustWait"/> must WITHHOLD while the engine is executing an
    ///       ability on that actor and while it is navigating, and must LET THROUGH when it is doing neither
    ///       (an actor that never receives a stance replays the aim-in animation before every shot, which is
    ///       the desync this whole surface exists to remove).
    ///   (b) <c>stance-write-not-gated</c> — <c>ApplyStance</c> must reach that rule AND must reach the
    ///       engine's own <c>HasExecutingAbility</c>. Asking a stale local flag instead of the engine looks
    ///       identical from the outside and restores the stall; and a rule nothing calls is arm (a) grading
    ///       its own homework.
    ///   (c) <c>withheld-stance-dropped</c> — a withheld write must be OWED, not lost: <c>ApplyStance</c>
    ///       must reach <c>_pending.Add</c>, <c>RetryPending</c> must reach <c>ApplyStance</c>, and the
    ///       per-frame postfix must reach <c>RetryPending</c>. Without the retry the guard trades a stalled
    ///       shot for a permanent stance divergence — the soldier that keeps replaying aim-in, forever.
    ///   (d) <c>aim-state-has-no-exit</c> — since L230 the click's native activation is SUPPRESSED, state
    ///       switch included, so <c>ReleaseLocalUiHolding</c> is the ONLY thing that ever leaves
    ///       <c>UIStateShoot</c> on the peer that clicked. Both <c>TacticalCommandSync.ApplyActivate</c> (its
    ///       refusal branches included) and <c>TacticalCommandSync.TickEchoWaits</c> must reach it. A path
    ///       that ends the echo wait without releasing the screen is the reported lock-up verbatim: the
    ///       order is gone, the wait is cleared, no button works and nothing else is coming.
    ///   (e) POSITIVE CONTROL, EXECUTED — <see cref="FakeStance"/> ignores execution, writes the animator
    ///       without asking the rule or the engine, drops what it withholds, and ends a wait without
    ///       releasing the screen. All four arms must go red on it, or their green above is a scan that
    ///       resolved nothing.
    ///
    /// NOT A QUORUM. Nothing here waits on another PEER: arm (a) waits on the local ENGINE finishing an
    /// animation it is already playing, which ends by itself with no human in the loop, and arm (c) is what
    /// guarantees it ends at all. An AFK peer cannot hold a stance for one frame.
    ///
    /// Falsify: make <c>StanceMustWait</c> ignore execution -&gt; (a); call <c>SetNullNavParams</c> without
    /// consulting it -&gt; (b); drop the <c>_pending.Add</c> or unhook <c>RetryPending</c> -&gt; (c); remove
    /// the release from <c>TickEchoWaits</c> or from <c>ApplyActivate</c>'s refusal branch -&gt; (d).
    /// </summary>
    internal static class L231_TheOutcomeLandsAtTheEndOfTheLocalAnimation
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var pose = typeof(TacticalAimPoseSync);
            var apply = pose.GetMethod("ApplyStance", AllMembers);
            var retry = pose.GetMethod("RetryPending", AllMembers);
            var postfix = pose.Assembly.GetType("Multiplayer.Tactical.TacticalAimPoseSync+AimStatePatch");
            var frame = postfix == null ? null : postfix.GetMethod("Postfix", AllMembers);
            var sync = typeof(TacticalCommandSync);
            var applyActivate = sync.GetMethod("ApplyActivate", AllMembers);
            var tick = sync.GetMethod("TickEchoWaits", AllMembers);

            if (apply == null || retry == null || frame == null || applyActivate == null || tick == null)
            {
                yield return "L231 premise-changed: one of TacticalAimPoseSync.{ApplyStance, RetryPending, " +
                             "AimStatePatch.Postfix} or TacticalCommandSync.{ApplyActivate, TickEchoWaits} no " +
                             "longer resolves. The seams this law is written over have moved, so every arm " +
                             "below would be asserting about a shape the build no longer has.";
                yield break;
            }

            foreach (var v in ScanRule(TacticalAimPoseSync.StanceMustWait, "StanceMustWait")) yield return v;
            foreach (var v in ScanGate(apply, "ApplyStance")) yield return v;
            foreach (var v in ScanOwed(apply, retry, frame, "ApplyStance")) yield return v;
            foreach (var v in ScanExit(applyActivate, tick, "TacticalCommandSync")) yield return v;

            // ── arm (e): every arm must be able to SEE its own violation.
            var fake = typeof(FakeStance);
            var control = ScanRule(FakeStance.Rule, "FakeStance.Rule")
                .Concat(ScanGate(fake.GetMethod("Apply", AllMembers), "FakeStance.Apply"))
                .Concat(ScanOwed(fake.GetMethod("Apply", AllMembers), fake.GetMethod("Retry", AllMembers),
                                 fake.GetMethod("Frame", AllMembers), "FakeStance.Apply"))
                .Concat(ScanExit(fake.GetMethod("Refuse", AllMembers), fake.GetMethod("Expire", AllMembers),
                                 "FakeStance"))
                .ToList();
            foreach (var want in new[] { "stance-stomps-a-driven-actor", "stance-write-not-gated",
                                         "withheld-stance-dropped", "aim-state-has-no-exit" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L231 control-not-red: FakeStance commits " + want + " and the scan did not " +
                                 "flag it. That arm cannot tell the fixed shape from the broken one, so its " +
                                 "green above means nothing.";
        }

        /// <summary>Arm (a) — the rule, run to exhaustion.</summary>
        private static IEnumerable<string> ScanRule(Func<bool, bool, bool> rule, string label)
        {
            if (!rule(false, true))
                yield return "L231 stance-stomps-a-driven-actor: " + label + "(notNavigating, executing) lets " +
                             "the stance mirror write the animator of an actor the ENGINE is driving. " +
                             "TacticalLevelController.FireWeaponAtTargetCrt:1646-1665 writes the same " +
                             "parameters and then spins on the 'AimStart' checkpoint for up to five of that " +
                             "actor's seconds; a clear landing inside that window (measured 17 ms after the " +
                             "wait began) stalled the HOST's shot for 3.65 s while every other peer played it " +
                             "immediately, so the target died a second and a half after the shooter had " +
                             "visibly finished.";
            if (!rule(true, false))
                yield return "L231 stance-stomps-a-driven-actor: " + label + "(navigating, idle) lets the " +
                             "stance be written mid-walk. The facing lerp fights the navigation the engine is " +
                             "running, which is what the pending/retry path was built for in the first place.";
            if (rule(false, false))
                yield return "L231 stance-stomps-a-driven-actor: " + label + "(idle, not navigating) WITHHOLDS " +
                             "the write. Nothing would ever reach the animator, so every peer replays the " +
                             "aim-in animation before every shot — the exact desync this surface exists to " +
                             "remove, traded for the one it was meant to fix.";
        }

        /// <summary>Arm (b) — the write really asks, and asks the ENGINE.</summary>
        private static IEnumerable<string> ScanGate(MethodBase apply, string label)
        {
            if (!Reaches(apply, null, "StanceMustWait"))
                yield return "L231 stance-write-not-gated: " + label + " must decide through StanceMustWait. " +
                             "A rule nothing calls is arm (a) grading its own homework: the predicate stays " +
                             "green while the animator is stomped exactly as before.";
            if (!Reaches(apply, null, "HasExecutingAbility"))
                yield return "L231 stance-write-not-gated: " + label + " must ask the engine's own " +
                             "HasExecutingAbility (TacticalActorBase:695-704), which ignores IdleAbility by " +
                             "the engine's rule — idling IS the state a stance is written on, and only a real " +
                             "ability owns the animator. A local flag of our own answers stale by exactly the " +
                             "one frame that matters.";
        }

        /// <summary>Arm (c) — a withheld write is owed, not lost.</summary>
        private static IEnumerable<string> ScanOwed(MethodBase apply, MethodBase retry, MethodBase frame,
                                                    string label)
        {
            if (!PendsAnActorKey(apply))
                yield return "L231 withheld-stance-dropped: " + label + " must PEND what it withholds. " +
                             "Dropping it trades a stalled shot for a permanent divergence — that soldier " +
                             "keeps the wrong stance until something else happens to change it.";
            if (!Reaches(retry, null, "ApplyStance"))
                yield return "L231 withheld-stance-dropped: RetryPending must re-run ApplyStance, or the " +
                             "pending set is a list of writes nobody ever performs.";
            if (!Reaches(frame, null, "RetryPending"))
                yield return "L231 withheld-stance-dropped: the per-frame postfix must pump RetryPending. " +
                             "A retry loop nothing calls is the same silent swallow with more code.";
        }

        /// <summary>Arm (d) — an aim state always has an exit.</summary>
        private static IEnumerable<string> ScanExit(MethodBase applyActivate, MethodBase tick, string label)
        {
            if (!Reaches(applyActivate, null, "ReleaseLocalUiHolding"))
                yield return "L231 aim-state-has-no-exit: " + label + ".ApplyActivate must release this peer's " +
                             "UI, on its refusal branches too. Since L230 the click's native activation is " +
                             "suppressed WITH its state switch, so this is the only thing that ever leaves " +
                             "UIStateShoot on the peer that clicked: a mirror that arrives and cannot be " +
                             "played leaves that player aiming at a soldier nothing will ever start.";
            if (!Reaches(tick, null, "ReleaseLocalUiHolding"))
                yield return "L231 aim-state-has-no-exit: " + label + ".TickEchoWaits must release this peer's " +
                             "UI when the wait gives up. Clearing the wait alone is half a recovery — the " +
                             "soldier is commandable again on paper while the player is still standing in a " +
                             "targeting state where no button does anything, for the rest of the battle.";
        }

        /// <summary>ARM (e). Never instantiated, never registered — it exists only to be walked and called.
        /// One violation per arm: a rule blind to execution, a write that asks nothing, a withhold that drops
        /// what it withholds, and two ends-of-order that never give the screen back.</summary>
        private static class FakeStance
        {
            internal static bool Rule(bool navigating, bool executing) => navigating;   // (a): blind to the shot

            internal static void Apply(int key)
            {
                SetNullNavParams(key);                       // (b)+(c): no rule, no engine, nothing pended
            }

            internal static void Retry() { }                 // (c): never re-applies

            internal static void Frame() { }                 // (c): never pumps

            internal static void Refuse()
            {
                Debug.LogError("cannot play that here");     // (d): names it, never releases
            }

            internal static void Expire()
            {
                Debug.LogError("echo lost");                 // (d): same
            }

            private static void SetNullNavParams(int key) { }
        }

        // ─── IL helpers (same primitives as L230; Program.cs is not partial) ─────────────────────

        /// <summary>An <c>Add</c> onto a one-argument generic collection OF ACTOR KEYS — which is what tells
        /// <c>_pending</c> (<c>HashSet&lt;int&gt;</c>) apart from the <c>_loggedFailures</c>
        /// (<c>HashSet&lt;string&gt;</c>) sitting three lines above it in the same method. Matching on the
        /// NAME alone would have kept arm (c) green with the whole pending path deleted.</summary>
        private static bool PendsAnActorKey(MethodBase caller)
            => CalleesOf(caller).Any(c => c.Name == "Add" && c.DeclaringType != null &&
                                          c.DeclaringType.IsGenericType &&
                                          c.DeclaringType.GetGenericArguments().Length == 1 &&
                                          c.DeclaringType.GetGenericArguments()[0] == typeof(int));

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
