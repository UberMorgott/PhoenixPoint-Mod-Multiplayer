using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L430 — A CLICK THIS MOD EATS ALWAYS LEAVES THE PLAYER A WAY OUT OF THE WINDOW.
    ///
    /// THE MEASURED FAILURE (live 3-instance co-op, 2026-08-13 21:27). The post-mission consequence event
    /// <c>PROG_NJ0_WIN</c> was raised on the HOST in the same second its geoscape finished loading
    /// ("window-queue restore: 0 entries" 21:27:27.500, "HOST raised 'PROG_NJ0_WIN' seq=7" 21:27:27.749), so
    /// nothing ever minted a durable occurrence for it. Both clients closed their own copies through the
    /// ordinary replay page (21:27:33.488 / 21:27:35.940). The host then clicked FOURTEEN times — 21:27:37.192
    /// through 21:28:10.032 — and every single click produced
    /// <c>[MP][events] host-local answer blocked — no exact durable occurrence</c> and nothing else:
    /// <c>EventSync.TryHostLocalDurableAnswer</c> returned true, suppressing the game's own
    /// <c>UIModuleSiteEncounters.OnChoiceSelected</c> body, while resolving nothing. The window could not be
    /// closed by any input and the player killed all three processes.
    ///
    /// WHY A LAW AND NOT JUST A FIX: this is the THIRD appearance of the same shape (see
    /// <c>DurableInboxSession.OpenSessionStore</c>'s header — "a mission-start event window that swallows
    /// eleven consecutive clicks", 2026-08-10). Every time, a precondition of the durable path failed and the
    /// seam reported "handled". A swallowed click is UNRECOVERABLE by construction: no later delta, timeout or
    /// peer action can un-eat it, so "handled" must mean the window has an exit — the answer resolved and its
    /// result page drawn (<see cref="EventSync.HostLocalAnswer.Resolved"/>), or the window closed outright
    /// (<see cref="EventSync.HostLocalAnswer.Refused"/>). Anything else hands the click back to the game.
    ///
    /// NOT A QUORUM (P13): every arm is about ONE peer closing ITS OWN copy. No peer waits on another peer's
    /// click, and the falling-through host runs the vanilla native path that a solo host would run.
    ///
    /// Falsify: make <c>BlocksNativeClick</c> return true unconditionally (the 2026-08-13 behaviour) →
    /// no-exit-swallow + not-durable-must-not-block; make <c>ClosesWindow</c> return false for
    /// <c>Refused</c> → no-exit-swallow; stop calling either from <c>TryHostLocalDurableAnswer</c> →
    /// deciders-are-decoration.
    /// </summary>
    internal static class L430_ASwallowedClickAlwaysHasAnExit
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(EventSync);
            var outcomes = sync.GetNestedType("HostLocalAnswer", All);
            var blocks = sync.GetMethod("BlocksNativeClick", All);
            var closes = sync.GetMethod("ClosesWindow", All);
            var seam = sync.GetMethod("TryHostLocalDurableAnswer", All);
            if (outcomes == null || !outcomes.IsEnum || blocks == null || closes == null || seam == null)
            {
                // GUARD. Every arm is a statement about these four members; without them this law asks
                // nothing at all and must say so rather than pass.
                yield return "L430 premise-changed: EventSync no longer carries the host-local answer shape this " +
                             "law describes (missing " +
                             (outcomes == null || !outcomes.IsEnum ? "the HostLocalAnswer enum" :
                              blocks == null ? "BlocksNativeClick" :
                              closes == null ? "ClosesWindow" : "TryHostLocalDurableAnswer") +
                             ") — whether a host click that resolves nothing can still be swallowed is " +
                             "unchecked, so re-derive it before trusting this seam";
                yield break;
            }

            // ── arm (a): EVERY blocking outcome has an exit. Executed, not read: these are pure deciders.
            foreach (EventSync.HostLocalAnswer outcome in Enum.GetValues(typeof(EventSync.HostLocalAnswer)))
            {
                if (!EventSync.BlocksNativeClick(outcome)) continue;
                if (outcome == EventSync.HostLocalAnswer.Resolved || EventSync.ClosesWindow(outcome)) continue;
                yield return "L430 no-exit-swallow: EventSync.BlocksNativeClick(" + outcome + ") suppresses the " +
                             "game's own OnChoiceSelected body, yet that outcome neither resolved the answer nor " +
                             "closes the window — so the click is eaten and NOTHING can ever re-offer it. That is " +
                             "the un-closable post-mission window of 2026-08-13 (14 dead clicks, all three " +
                             "instances force-killed) and the 11 dead clicks of 2026-08-10.";
            }

            // ── arm (b): the outcome the live bug produced, named. A failed precondition of the DURABLE path
            // is not a reason to take the click away from the GAME — the host owns resolutions and the native
            // body is exactly what a solo host would run.
            if (EventSync.BlocksNativeClick(EventSync.HostLocalAnswer.NotDurable))
                yield return "L430 not-durable-must-not-block: a click with no durable occurrence (no ledger " +
                             "entry, no local entitlement, or a store that did not exist when the raise landed) " +
                             "blocks the native handler again. The host's own window then has no working button " +
                             "at all — measured 21:27:37→21:28:10 on 2026-08-13.";

            // ── arm (c): the deciders are the seam's ACTUAL control flow, not commentary beside it.
            if (!Calls(seam, blocks) || !Calls(seam, closes))
                yield return "L430 deciders-are-decoration: EventSync.TryHostLocalDurableAnswer no longer routes " +
                             "through " + (Calls(seam, blocks) ? "ClosesWindow" : "BlocksNativeClick") + ", so the " +
                             "pure rule above governs nothing and the seam is free to swallow a click again.";

            // ── POSITIVE CONTROL: the call detector can say NO. BlocksNativeClick calls neither of the pair,
            // so a detector answering yes here would make arm (c) green over any body whatsoever.
            if (Calls(blocks, closes) || Calls(blocks, seam))
                yield return "L430 control-failed: the IL call detector claims BlocksNativeClick calls " +
                             "ClosesWindow or TryHostLocalDurableAnswer, which it does not — arm (c) cannot tell " +
                             "a seam that uses these deciders from one that ignores them.";
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
