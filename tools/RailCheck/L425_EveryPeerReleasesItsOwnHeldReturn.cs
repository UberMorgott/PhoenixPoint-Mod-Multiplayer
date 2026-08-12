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
    /// AND THE SAME BUG CAME BACK FROM THE OTHER SIDE (adversarial review, 2026-08-12). The host armed at
    /// T0 off its own click; the client clicked Continue at T0+3 and armed its OWN clock; the host hit zero
    /// at T0+5 and broadcast the CLEAR, which zeroed the client's own hold — the click that hold had already
    /// swallowed was gone and its return never ran. So the countdown state carries an OWNER
    /// (<c>ReturnCountdown._mine</c>): a hold that swallowed THIS peer's click is ended by this peer alone,
    /// and every remote clear path reads that bit before touching it. Reaching zero broadcasts nothing at
    /// all — each peer's own clock expires on its own, and a CLEAR now means only "the arm I broadcast is
    /// cancelled".
    ///
    /// SO THESE WRITES ARE LOAD-BEARING AND NONE IS OBSERVABLE FROM ANY PURE FUNCTION:
    ///   (a) the hold ARMS the peer whose click it just ate — otherwise the swallow is invisible to the
    ///       capture that reads <see cref="ReturnCountdown.Holding"/> to decide whether the leave happened;
    ///   (b) an arm that arrives WITHOUT a view resolves the live one instead of keeping null, or the host
    ///       cancels the very countdown a client is waiting on;
    ///   (c) the client's CLEAR applier BRANCHES on the ownership bit — reading it into a log line is not
    ///       enough — so a hold this peer armed for its own swallowed click survives anything the host says
    ///       about its own strip;
    ///   (d) the release path broadcasts NOTHING — no CLEAR, no cancel. A peer reaching zero, or losing its
    ///       level, is not an event about anybody else's hold;
    ///   (e) the prefix claims ownership TWICE — once on the client's own arm, and once again on the
    ///       re-click it swallows. A peer that clicks Continue while it is only MIRRORING the host's arm
    ///       has its click eaten by the <c>_zeroAt &gt; 0</c> swallow, and without that second write the
    ///       hold stays un-owned and the next CLEAR discards a click that is already gone;
    ///   (f) <c>HostCancel</c> branches on the same bit, or a client's veto zeroes the hold that ate the
    ///       HOST's own Continue.
    /// All are IL facts about a few small methods, which is why this law reads IL rather than executing a
    /// predicate: there is no pure decider here to run.
    ///
    /// NOT A QUORUM (P13, and this law does not create one): every peer releases its own hold on its own
    /// clock in <c>Tick</c>; nobody waits for another human to press anything.
    ///
    /// Falsify: delete the client-branch arm in the prefix → hold-swallows-without-arming; put
    /// <c>if (viewIfHost != null) _view = viewIfHost;</c> back → asked-arm-holds-no-view; delete the
    /// <c>_mine</c> check from <c>HandleCountdown</c>'s clear branch → remote-clear-ignores-ownership; put
    /// the zero-CLEAR broadcast (or the <c>HostCancel</c> on a lost view) back into <c>Tick</c> →
    /// release-cancels-another-peers-hold; put <c>if (_zeroAt &gt; 0f) return false;</c> back without the
    /// <c>_mine = true</c> → swallowed-click-unowned; drop <c>HostCancel</c>'s <c>if (_mine)</c> →
    /// host-cancel-ignores-ownership.
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
            var mine = countdown.GetField("_mine", All);
            var handle = countdown.GetMethod("HandleCountdown", All);
            var tick = countdown.GetMethod("Tick", All);
            var hostCancel = countdown.GetMethod("HostCancel", All);
            var broadcast = typeof(Multiplayer.Network.NetworkEngine).GetMethod("BroadcastToAll", All);
            var modDriving = countdown.GetField("ModDriving", All);
            if (zeroAt == null || view == null || hostArm == null || display == null || prefix == null ||
                tlc == null || mine == null || handle == null || tick == null || hostCancel == null ||
                broadcast == null || modDriving == null)
            {
                // GUARD. Every arm below is a statement about these six members; without them the law is
                // asking nothing and must say so rather than pass.
                yield return "L425 premise-changed: ReturnCountdown's hold no longer has the shape this law " +
                             "describes (missing " +
                             (zeroAt == null ? "_zeroAt" : view == null ? "_view" :
                              hostArm == null ? "HostArm" : display == null ? "DisplaySecondsLeft" :
                              prefix == null ? "ReturnHoldPatch.Prefix" : tlc == null ? "TacticalDamageSync.Tlc" :
                              mine == null ? "_mine" : handle == null ? "HandleCountdown" :
                              tick == null ? "Tick" : hostCancel == null ? "HostCancel" :
                              broadcast == null ? "NetworkEngine.BroadcastToAll" : "ModDriving") +
                             ") — the two writes that keep a client's own Continue from being swallowed " +
                             "forever cannot be checked, so re-derive them before trusting this seam";
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

            // ── arm (c): a remote CLEAR asks who owns the hold before dropping it — and ACTS on the answer.
            // A read alone is not enough: logging _mine and clearing anyway would have passed.
            if (!BranchesOnField(handle, mine))
                yield return "L425 remote-clear-ignores-ownership: ReturnCountdown.HandleCountdown drops the hold " +
                             "on a host CLEAR without reading _mine. A peer that clicked Continue has ALREADY had " +
                             "that click swallowed, so the host zeroing its hold discards the click and nothing " +
                             "ever runs that peer's return — it sits on the battle summary while the host is back " +
                             "on the geoscape (adversarial review 2026-08-12).";

            // ── arm (d): the release path tells nobody. A CLEAR at zero is indistinguishable from a cancel.
            if (Calls(tick, hostCancel) || Calls(tick, broadcast))
                yield return "L425 release-cancels-another-peers-hold: ReturnCountdown.Tick broadcasts (HostCancel " +
                             "or BroadcastToAll) when it reaches zero or loses its view. That message lands on " +
                             "peers holding their own swallowed clicks and stops countdowns nobody asked it to " +
                             "stop; combined with a host that has already left the battle it arms and cancels " +
                             "within one round trip and the client can never leave.";

            // ── arm (e): the swallow OWNS what it eats. Two ownership writes in the prefix: the client's
            // own arm, and the re-click the `_zeroAt > 0` branch swallows while this peer is only mirroring.
            if (WriteCount(prefix, mine) < 2)
                yield return "L425 swallowed-click-unowned: ReturnCountdown.ReturnHoldPatch.Prefix claims " +
                             "ownership fewer than twice. The `_zeroAt > 0` swallow eats a Continue click " +
                             "without setting _mine, so a peer that clicks while MIRRORING the host's arm owns " +
                             "nothing: the next CLEAR (HandleCountdown, HostCancel) drops a hold whose click is " +
                             "already gone and nothing runs that peer's return until it presses Continue again " +
                             "(adversarial review 2026-08-13).";

            // ── arm (f): the host's own swallowed click is not a client's to veto.
            if (!BranchesOnField(hostCancel, mine))
                yield return "L425 host-cancel-ignores-ownership: ReturnCountdown.HostCancel no longer branches " +
                             "on _mine, so a client's 0x4C zeroes the hold that ate the HOST's own Continue — " +
                             "the same discarded-click defect as the client side, in the other direction.";

            // ── POSITIVE CONTROL: the write detector can say NO. DisplaySecondsLeft only READS _zeroAt, so a
            // detector that answered yes here would make arm (a) green on any body at all.
            if (WritesField(display, zeroAt))
                yield return "L425 control-failed: the IL write detector claims DisplaySecondsLeft writes " +
                             "_zeroAt, which only reads it — arm (a) cannot distinguish an arming prefix from " +
                             "any other body and proves nothing.";
            // The read detector must be able to say YES, or arm (c) is green on any body at all; the call
            // detector's positive control is arm (b), which requires a call it must find.
            if (!ReadsField(display, zeroAt))
                yield return "L425 control-failed: the IL read detector cannot see DisplaySecondsLeft reading " +
                             "_zeroAt, which is its first statement — the ownership bit could then go unread " +
                             "everywhere and nothing here would notice.";
            // The branch detector must be able to say YES on a bool it does NOT own: the prefix's first
            // statement is `if (ModDriving || ...)`. Without this, arms (c) and (f) are green on any body.
            if (!BranchesOnField(prefix, modDriving))
                yield return "L425 control-failed: the IL branch detector cannot see ReturnHoldPatch.Prefix " +
                             "branching on ModDriving, which is its first statement — arms (c) and (f) would " +
                             "pass on a HandleCountdown and a HostCancel that never ask who owns the hold.";
        }

        private static bool WritesField(MethodBase m, FieldInfo field) => Touches(m, field, 0x80); // stsfld

        private static bool ReadsField(MethodBase m, FieldInfo field) => Touches(m, field, 0x7E);  // ldsfld

        /// <summary>How many times the body ASSIGNS the field (stsfld), not merely whether it does.</summary>
        private static int WriteCount(MethodBase m, FieldInfo field) => Count(m, field, 0x80);

        /// <summary>Does the body BRANCH on the field — <c>ldsfld</c> followed by brfalse/brtrue (short or
        /// long)? That is what `if (_mine) return;` compiles to, and what a read that only feeds a log line
        /// does not. A DEBUG build spills the loaded bool through a local first (<c>stloc</c>/<c>ldloc</c>),
        /// so that one pair is stepped over — and nothing else is, or "branches on" would mean "mentions".</summary>
        private static bool BranchesOnField(MethodBase m, FieldInfo field)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (var i = 0; i + 5 < il.Length; i++)
            {
                if (il[i] != 0x7E) continue;                    // ldsfld
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f != field) continue;
                var j = i + 5;
                if (j < il.Length && il[j] >= 0x0A && il[j] <= 0x0D) j += 1;        // stloc.0-3
                else if (j + 1 < il.Length && il[j] == 0x13) j += 2;                // stloc.s
                if (j < il.Length && il[j] >= 0x06 && il[j] <= 0x09) j += 1;        // ldloc.0-3
                else if (j + 1 < il.Length && il[j] == 0x11) j += 2;                // ldloc.s
                if (j >= il.Length) continue;
                var next = il[j];                               // brfalse/brtrue, short and long forms
                if (next == 0x2C || next == 0x2D || next == 0x39 || next == 0x3A) return true;
            }
            return false;
        }

        private static int Count(MethodBase m, FieldInfo field, byte opcode)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return 0;
            var n = 0;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != opcode) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f == field) n++;
            }
            return n;
        }

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
