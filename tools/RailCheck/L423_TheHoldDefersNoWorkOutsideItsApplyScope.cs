using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L423 — A HOLD MAY NOT DEFER WORK OUT OF THE SCOPE THAT WAS SUPPRESSING IT.
    ///
    /// THE FAILURE (8fb08bc). <c>ReturnCountdown</c>'s five-second strip is documented as a peer-LOCAL clock
    /// that starts on THIS peer's own click, and its prefix on <c>TacticalView.GoToGeoscape</c> returns false
    /// to swallow the call while it counts. It was swallowing the calls the MOD ITSELF makes through that
    /// funnel. <c>TacticalTurnSync.ApplyLeave</c> — a peer being carried out of the battle by the host — runs
    /// under <c>SyncApplyScope</c>, and that scope is the ONLY thing suppressing the leave capture's
    /// re-emission. Held, the native exit did not happen at the apply; it happened five seconds later inside
    /// <c>Tick</c>, OUTSIDE the scope. The capture then fired unsuppressed and the carried peer sent a leave
    /// ask straight back to the host — a direct echo of the host's own instruction, and with a chain of peers
    /// a T+5 / T+10 / T+15 cascade.
    ///
    /// (The same hold on <c>HandleLeaveBattle</c> had the other half of it: the host logged "ACCEPTED —
    /// running the host's own GoToGeoscape" over a call that ran nothing, and any throw from the real body
    /// surfaced 5 s later on a stack where <c>IntentRail.HandleInbound</c>'s catch — the asking peer's only
    /// reject — is long gone. That one is covered by the <c>ModDriving</c> arm below.)
    ///
    /// THE GENERAL RULE THIS IS AN INSTANCE OF. A suppression scope suppresses what happens INSIDE it. Any
    /// mechanism that defers work out of a scope silently un-suppresses that work, and the deferral is
    /// invisible at the call site — <c>ApplyLeave</c>'s own code is correct, complete, and inside its scope;
    /// the echo it produced came from a prefix in another file deciding to run the exit later. So the check
    /// belongs on the DEFERRER: the hold must know that a scoped apply is not a human's click.
    ///
    /// THE ARMS:
    ///   (a) <c>hold-defers-a-scoped-apply</c> — <c>ReturnHoldPatch.Prefix</c> must reach
    ///       <c>SyncApplyScope.Active</c>. This is the reported defect verbatim.
    ///   (b) <c>hold-defers-a-mod-driven-exit</c> — and it must consult <c>ReturnCountdown.ModDriving</c>, the
    ///       marker on the mod's other two invocations of this funnel (the strip's own release at zero, and
    ///       the host executing an accepted peer ask). Either escape missing puts a mod-driven exit five
    ///       seconds into the future.
    ///   (c) <c>the-hold-stopped-holding</c> — the prefix still RETURNS BOOL. All of the above is about a
    ///       swallow; a prefix that cannot swallow has nothing to escape from, and arms (a)/(b) would then be
    ///       asserting two reads that mean nothing. This is also what keeps the law honest if the strip is
    ///       ever reimplemented as a postfix.
    ///
    /// GUARD: <c>premise-changed</c> when the patch class, its prefix, <c>SyncApplyScope.Active</c> or
    /// <c>ModDriving</c> stop resolving — the arms are reachability claims about exactly those members.
    ///
    /// Falsify: drop <c>SyncApplyScope.Active</c> from the prefix's early-out → (a); drop <c>ModDriving</c>
    /// → (b).
    /// </summary>
    internal static class L423_TheHoldDefersNoWorkOutsideItsApplyScope
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var hold = typeof(ReturnCountdown).GetNestedType("ReturnHoldPatch", All);
            var prefix = hold?.GetMethod("Prefix", All);
            var active = typeof(SyncApplyScope).GetProperty("Active", All)?.GetGetMethod(true);
            var modDriving = typeof(ReturnCountdown).GetField("ModDriving", All);

            if (prefix == null || active == null || modDriving == null)
            {
                yield return "L423 premise-changed: ReturnCountdown.ReturnHoldPatch.Prefix, " +
                             "SyncApplyScope.Active or ReturnCountdown.ModDriving no longer resolves. The arms " +
                             "below are reachability claims about exactly those members, so all of them would " +
                             "pass while the strip is free to swallow the mod's own exits again — which turned a " +
                             "host-driven leave into the carried peer asking the host to leave, five seconds " +
                             "later and outside the scope that was suppressing it.";
                yield break;
            }

            // (c) first — the other two arms are about escaping a swallow that has to exist.
            if (prefix.ReturnType != typeof(bool))
            {
                yield return "L423 the-hold-stopped-holding: ReturnHoldPatch.Prefix returns " +
                             prefix.ReturnType.Name + ", so it cannot swallow the native return at all and the " +
                             "five-second strip is decorative. Arms (a) and (b) are about escapes from a swallow; " +
                             "with no swallow they assert two reads that decide nothing.";
                yield break;
            }

            if (!Reaches(prefix, active))
                yield return "L423 hold-defers-a-scoped-apply: ReturnHoldPatch.Prefix does not consult " +
                             "SyncApplyScope.Active, so it swallows the exit TacticalTurnSync.ApplyLeave makes " +
                             "for a peer the host is carrying out of the battle. The native exit then happens " +
                             "five seconds later, inside Tick — OUTSIDE the apply scope, which is the only thing " +
                             "suppressing TacLeaveBattleCapture — and that peer echoes the host's own leave " +
                             "instruction straight back at it. Down a chain of peers this is a T+5/T+10/T+15 " +
                             "cascade. A peer that never clicked has nothing to count down.";

            if (!Program.ReadsField(prefix, modDriving))
                yield return "L423 hold-defers-a-mod-driven-exit: ReturnHoldPatch.Prefix does not consult " +
                             "ReturnCountdown.ModDriving, the marker TacticalTurnSync.InvokeNativeLeave sets " +
                             "across every GoToGeoscape the MOD drives — the strip's own release at zero, and " +
                             "the host executing an accepted peer ask. Held, the host logs 'ACCEPTED — running " +
                             "the host's own GoToGeoscape' over a call that ran nothing, and a throw from the " +
                             "real body surfaces 5 s later inside Tick, on a stack where IntentRail." +
                             "HandleInbound's catch — the asking peer's only reject — is long gone. The asking " +
                             "peer has already spent its own five seconds.";
        }

        private static bool Reaches(MethodBase from, MethodBase target) =>
            Program.CalleeSequence(from)
                   .Any(c => c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module);
    }
}
