using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L425 — A PEER THAT SWALLOWS ITS OWN CONTINUE MUST ARM ITS OWN CLOCK, AND AN ASKED-FOR ARM MUST HOLD
    /// A RUNNABLE VIEW.
    ///
    /// THE MEASURED FAILURE (live co-op, 2026-08-12 22:11:58). A CLIENT clicked Continue on the battle
    /// summary. <c>ReturnCountdown.ReturnHoldPatch.Prefix</c> returned false — swallowing that peer's own
    /// <c>GoToGeoscape</c> — but wrote nothing to <c>_zeroAt</c>, so <c>Holding</c> was false and
    /// <c>TacLeaveBattleCapture</c> latched <c>TacticalTurnSync.LeftBattle</c> over a return that never ran.
    /// The host accepted the ask, ran its OWN GoToGeoscape ("ACCEPTED — running the host's own
    /// GoToGeoscape"), and its <c>OpLeave</c> then found <c>LeftBattle</c> already true on the asking peer:
    /// <c>ApplyLeave</c> no-opped, and NOTHING anywhere ran that client's return. It sat on the summary
    /// screen for the rest of the session while the host waited on a reveal barrier it could never open.
    /// The same click also armed the host's strip through <c>NetworkEngine</c>:721, which passes NO view —
    /// <c>_view</c> stayed null, <c>Tick</c> found "nothing left to run", dropped the strip and broadcast the
    /// CLEAR that stopped the asking client's countdown too. Ten Continue presses, ten identical pairs.
    ///
    /// AND A CANCEL PUTS EVERY PEER BACK WHERE IT WAS (live 2026-08-13). The ownership bit this law used to
    /// require (<c>ReturnCountdown._mine</c>, arms (c)/(e)/(f), deleted with those arms) made a hold that had
    /// swallowed a peer's own Continue refuse every remote CLEAR. That was needed only while a CLEAR could
    /// go out for something other than a human's CANCEL — the release broadcasting at zero, the give-up
    /// broadcasting, a view-less host arming and cancelling within one round trip. Arms (b) and (d) below
    /// forbid all three, so a CLEAR on the wire is now a human's CANCEL and nothing else. What the bit
    /// bought instead was an ORPHAN hold: two peers both clicking Continue at the end of a mission is
    /// ordinary, the second click is swallowed by the strip already running, and after somebody pressed
    /// CANCEL that swallowed click went on ticking where no peer could see it — then expired, sent its
    /// leave, and pulled every peer to the geoscape with no countdown on screen at all.
    ///
    /// SO THESE FACTS ARE LOAD-BEARING AND NONE IS OBSERVABLE FROM ANY PURE FUNCTION:
    ///   (a) the hold ARMS the peer whose click it just ate — otherwise the swallow is invisible to the
    ///       capture that reads <see cref="ReturnCountdown.Holding"/> to decide whether the leave happened;
    ///   (b) an arm that arrives WITHOUT a view resolves the live one instead of keeping null, or the host
    ///       cancels the very countdown a client is waiting on;
    ///   (c) the release path broadcasts NOTHING — no CLEAR, no cancel. A peer reaching zero, or losing its
    ///       level, is not an event about anybody else's hold, and a broadcast there would be
    ///       indistinguishable from the cancel that (c2) now makes unconditional;
    ///   (c2) BOTH clear paths end the hold UNCONDITIONALLY — <c>HandleCountdown</c>'s zero branch and
    ///       <c>HostCancel</c> each reach <c>ClearLocal</c>, the host re-broadcasts the clear, and no
    ///       ownership field exists to branch on. One countdown, one clear, and the next press by any peer
    ///       arms a fresh five seconds.
    /// All are IL facts about a few small methods, which is why this law reads IL rather than executing a
    /// predicate: there is no pure decider here to run.
    ///
    /// NOT A QUORUM (P13, and this law does not create one): every peer releases its own hold on its own
    /// clock in <c>Tick</c>; nobody waits for another human to press anything, and a cancel blocks nobody —
    /// every summary screen stays up with a live Continue button.
    ///
    /// Falsify: delete the client-branch arm in the prefix → hold-swallows-without-arming; put
    /// <c>if (viewIfHost != null) _view = viewIfHost;</c> back → asked-arm-holds-no-view; put the zero-CLEAR
    /// broadcast (or the <c>HostCancel</c> on a lost view) back into <c>Tick</c> →
    /// release-cancels-another-peers-hold; re-introduce a <c>_mine</c>-style bit, or return early from
    /// either clear path instead of clearing → cancel-is-not-authoritative.
    /// </summary>
    internal static class L425_EveryPeerReleasesItsOwnHeldReturn
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var countdown = typeof(ReturnCountdown);
            var zeroAt = countdown.GetField("_zeroAt", All);
            var view = countdown.GetField("_view", All);
            var hostArm = countdown.GetMethod("HostArm", All);
            var display = countdown.GetMethod("DisplaySecondsLeft", All);
            var hold = countdown.GetNestedType("ReturnHoldPatch", All);
            var prefix = hold == null ? null : hold.GetMethod("Prefix", All);
            var tlc = typeof(TacticalDamageSync).GetMethod("Tlc", All);
            var handle = countdown.GetMethod("HandleCountdown", All);
            var tick = countdown.GetMethod("Tick", All);
            var hostCancel = countdown.GetMethod("HostCancel", All);
            var clearLocal = countdown.GetMethod("ClearLocal", All);
            var broadcast = typeof(Multiplayer.Network.NetworkEngine).GetMethod("BroadcastToAll", All);
            if (zeroAt == null || view == null || hostArm == null || display == null || prefix == null ||
                tlc == null || handle == null || tick == null || hostCancel == null || clearLocal == null ||
                broadcast == null)
            {
                // GUARD. Every arm below is a statement about these members; without them the law is
                // asking nothing and must say so rather than pass.
                yield return "L425 premise-changed: ReturnCountdown's hold no longer has the shape this law " +
                             "describes (missing " +
                             (zeroAt == null ? "_zeroAt" : view == null ? "_view" :
                              hostArm == null ? "HostArm" : display == null ? "DisplaySecondsLeft" :
                              prefix == null ? "ReturnHoldPatch.Prefix" : tlc == null ? "TacticalDamageSync.Tlc" :
                              handle == null ? "HandleCountdown" : tick == null ? "Tick" :
                              hostCancel == null ? "HostCancel" : clearLocal == null ? "ClearLocal" :
                              "NetworkEngine.BroadcastToAll") +
                             ") — neither the arm that keeps a client's own Continue from being swallowed " +
                             "forever nor the cancel that takes the countdown back on every peer can be " +
                             "checked, so re-derive them before trusting this seam";
                yield break;
            }

            // ── arm (a): the swallow arms the peer it swallowed.
            if (!WritesField(prefix, zeroAt))
                yield return "L425 hold-swallows-without-arming: ReturnCountdown.ReturnHoldPatch.Prefix returns " +
                             "false without ever writing _zeroAt, so on a CLIENT the click is eaten while " +
                             "Holding stays false. TacLeaveBattleCapture then announces a leave that did not " +
                             "happen and latches TacticalTurnSync.LeftBattle, after which the host's own OpLeave " +
                             "is a no-op on that peer and NOTHING runs its return — it sits on the battle " +
                             "summary for the rest of the session (live 2026-08-12).";

            // ── arm (b): an arm asked for by a client still holds something to run.
            if (!Calls(hostArm, tlc))
                yield return "L425 asked-arm-holds-no-view: ReturnCountdown.HostArm no longer resolves the live " +
                             "TacticalView when it is handed none. A client's ask arrives through " +
                             "NetworkEngine:721 with a null view, and a null _view makes Tick drop the strip and " +
                             "broadcast the CLEAR that stops the asking peer's own countdown — the exact pair of " +
                             "log lines the 2026-08-12 session repeated ten times.";

            // ── arm (c): the release path tells nobody. A CLEAR at zero is indistinguishable from a cancel.
            if (Calls(tick, hostCancel) || Calls(tick, broadcast))
                yield return "L425 release-cancels-another-peers-hold: ReturnCountdown.Tick broadcasts (HostCancel " +
                             "or BroadcastToAll) when it reaches zero or loses its view. That message lands on " +
                             "peers holding their own swallowed clicks and stops countdowns nobody asked it to " +
                             "stop; combined with a host that has already left the battle it arms and cancels " +
                             "within one round trip and the client can never leave.";

            // ── arm (c2): a CANCEL is authoritative. Both clear paths reach ClearLocal, the host says so to
            // every peer, and no ownership latch exists for either of them to except itself on.
            foreach (var ownership in countdown.GetFields(All))
            {
                if (ownership.FieldType != typeof(bool) || ownership.Name == "ModDriving") continue;
                yield return "L425 cancel-is-not-authoritative: ReturnCountdown carries a second bool (" +
                             ownership.Name + ") beside ModDriving — the ownership latch this seam had to lose. " +
                             "A hold that excepts itself from the CLEAR keeps ticking after a peer pressed " +
                             "CANCEL, where nobody can see it, and takes every peer to the geoscape when it " +
                             "expires with no countdown on any screen (live 2026-08-13).";
            }
            if (!Calls(handle, clearLocal))
                yield return "L425 cancel-is-not-authoritative: ReturnCountdown.HandleCountdown never calls " +
                             "ClearLocal, so a peer can survive the host's CLEAR with a hold still armed. " +
                             "A cancel must restore the exact pre-arm state on EVERY peer.";
            if (!Calls(hostCancel, clearLocal) || !Calls(hostCancel, broadcast))
                yield return "L425 cancel-is-not-authoritative: ReturnCountdown.HostCancel no longer both clears " +
                             "the host's own hold and re-broadcasts the CLEAR, so one peer's CANCEL leaves the " +
                             "countdown running somewhere — and the next Continue press finds a strip nobody can " +
                             "see instead of arming a fresh five seconds.";

            // ── POSITIVE CONTROL: the write detector can say NO. DisplaySecondsLeft only READS _zeroAt, so a
            // detector that answered yes here would make arm (a) green on any body at all.
            if (WritesField(display, zeroAt))
                yield return "L425 control-failed: the IL write detector claims DisplaySecondsLeft writes " +
                             "_zeroAt, which only reads it — arm (a) cannot distinguish an arming prefix from " +
                             "any other body and proves nothing.";
            // The call detector must be able to say NO as well as yes (arm (b) needs a call it must find):
            // DisplaySecondsLeft calls neither, so a detector answering yes here would make (c) — which is a
            // MUST-NOT-call arm — green on a Tick that broadcasts on every release.
            if (Calls(display, broadcast) || Calls(display, clearLocal))
                yield return "L425 control-failed: the IL call detector claims DisplaySecondsLeft calls " +
                             "BroadcastToAll or ClearLocal, which it does not — arm (c) cannot tell a silent " +
                             "release from one that cancels every other peer's hold.";
        }

        private static bool WritesField(MethodBase m, FieldInfo field) => Touches(m, field, 0x80); // stsfld

        private static bool Touches(MethodBase m, FieldInfo field, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (Array.IndexOf(opcodes, il[i]) < 0) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f == field) return true;
            }
            return false;
        }

        private static bool Calls(MethodBase m, MethodBase callee)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null || callee == null) return false;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                MethodBase target = null;
                try { target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (target == callee) return true;
            }
            return false;
        }
    }
}
