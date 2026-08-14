using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L481 — A DROPPED REFERENCE ASKS FOR ITSELF BACK ON THE VALUE BATCH THAT DROPPED IT.
    ///
    /// THE FAILURE (live 2026-08-13, host + client logs). A client equipping soldiers from
    /// <c>UIStateEditSoldier</c> sent eleven loadout intents; the host applied every one of them
    /// (<c>[MP][equip] HOST intent APPLIED char=U#4 nonce=320/321</c>, <c>char=U#5 nonce=322-326</c>,
    /// no rejects) and the deltas went out. Yet the host's CRC backstop later named
    /// <c>root 'U#4' DIVERGED ... at quiescent seq 1008</c> (22:15:00.799) and
    /// <c>root 'U#5' DIVERGED ... seq 1009</c> (22:15:02.852), 21 s and 7 s after the edits, and the client
    /// named the field: <c>U#4._equipmentItems</c> / <c>U#5._equipmentItems</c>. Only the emergency
    /// full-subtree re-emit repaired it.
    ///
    /// THE MECHANISM. <c>GenericApplier</c>'s EntityList arm removes an element whose referent is not on
    /// this peer yet and records the REFERRER through <c>NoteRefDrop</c> — "the list applies SHORT and the
    /// host, whose own list did not change, never re-ships it". That record was consumed by exactly ONE
    /// caller: the structural-create backfill. A drop with no create behind it — every equipment edit —
    /// was therefore never redeemed at all, and the mirror stood knowingly stale until the CRC backstop
    /// happened to sample that root minutes later. <c>_refDropPaths</c> is a HashSet that is otherwise
    /// only cleared on session reset, so the loss was silent as well as unbounded in time.
    ///
    /// THE RULE. A value batch that recorded a ref drop asks the host for the REFERRER's subtree before it
    /// returns — the same scope rule the create backfill uses (never the missing referent: it has no entry
    /// of its own to restate) and the same per-root-throttled <c>RequestResync</c>. One exception, and it
    /// is not an optimization: while a FULL resend is already pending, that answer restates every root, and
    /// join hydration — where drops arrive in bulk — is exactly when one is in flight.
    ///
    /// NOT A QUORUM: this is one peer asking the host for bytes it already owns. Nothing waits on a human,
    /// nobody's progress is gated, and a peer that never asks simply keeps the stale value it already had.
    ///
    /// THE ARMS:
    ///   (0) <c>premise-changed</c> — the four seams this law reads still exist by name.
    ///   (a) <c>gate-*</c> — the shipped decision <see cref="GenericApplier.ShouldRedeemRefDrops"/> driven
    ///       case by case: drops + no pending full ⇒ ask; no drops ⇒ silent; drops + pending full ⇒ silent.
    ///   (b) <c>unwired</c> — IL: <c>ApplyDelta</c> (the value-batch path) must reach <c>RedeemRefDrops</c>.
    ///       Arm (a) drives a pure function and stays green while nothing calls it.
    ///   (c) <c>no-request</c> — IL: <c>RedeemRefDrops</c> must reach <c>RequestResync</c>. A redeemer that
    ///       clears the record without asking is worse than the bug: it erases the evidence too.
    ///   (d) <c>ungated</c> — IL: <c>RedeemRefDrops</c> must reach <c>ShouldRedeemRefDrops</c>, or (a)
    ///       tests a function the shipped path no longer consults.
    ///
    /// Falsify: return false from ShouldRedeemRefDrops → (a); drop the RedeemRefDrops call out of
    /// ApplyDelta → (b); clear _refDropPaths without RequestResync → (c); inline the gate → (d).
    /// </summary>
    internal static class L481_ADroppedReferenceAsksForItselfBack
    {
        internal static IEnumerable<string> Check()
        {
            // (a) The shipped gate, case by case. -1f is the "no full resend pending" sentinel.
            if (!GenericApplier.ShouldRedeemRefDrops(1, -1f))
                yield return "L481 gate-silent: a value batch that recorded a dropped reference, with no full " +
                             "resend pending, refused to ask for the referrer back. That is the 2026-08-13 " +
                             "U#4/U#5 '_equipmentItems' divergence exactly — a mirror that KNOWS it applied a " +
                             "short list and says nothing, until the CRC backstop notices 21 s later.";

            if (GenericApplier.ShouldRedeemRefDrops(0, -1f))
                yield return "L481 gate-chatty: a batch that dropped NOTHING still asked the host to re-emit. " +
                             "Every clean value batch would then cost a scoped resend round-trip.";

            if (GenericApplier.ShouldRedeemRefDrops(3, 12.5f))
                yield return "L481 gate-storms-the-join: drops were redeemed one-by-one while a FULL resend was " +
                             "already pending. Join hydration drops references in bulk (the referent's create " +
                             "has not landed yet), so this is a scoped request per referrer root on top of the " +
                             "full answer that was about to restate all of them anyway.";

            // (b)-(d) IL: the wiring the pure arm cannot see.
            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var applier = typeof(GenericApplier);
            var applyDelta = applier.GetMethod("ApplyDelta", Any);
            var redeem = applier.GetMethod("RedeemRefDrops", Any);
            var request = applier.GetMethod("RequestResync", Any);
            var gate = applier.GetMethod("ShouldRedeemRefDrops", Any);

            if (applyDelta == null || redeem == null || request == null || gate == null)
                yield return "L481 premise-changed: GenericApplier no longer exposes ApplyDelta/RedeemRefDrops/" +
                             "RequestResync/ShouldRedeemRefDrops by those names, so this law can assert nothing " +
                             "about the redemption path. Re-point it at the renamed seam.";
            else
            {
                if (!CallsMethod(applyDelta, redeem))
                    yield return "L481 unwired: the value-batch apply (ApplyDelta) does not reach RedeemRefDrops. " +
                                 "Recorded drops fall back to being consumed only by the structural-create " +
                                 "backfill — which is the bug: an equipment edit produces no create.";

                if (!CallsMethod(redeem, request))
                    yield return "L481 no-request: RedeemRefDrops does not reach RequestResync. Clearing the " +
                                 "record without asking for the bytes back leaves the same stale mirror AND " +
                                 "destroys the only trace that a reference was ever dropped.";

                if (!CallsMethod(redeem, gate))
                    yield return "L481 ungated: RedeemRefDrops does not consult ShouldRedeemRefDrops, so the " +
                                 "pending-full-resend exception this law executes is not the one that ships.";
            }
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            if (caller == null || target == null) return false;
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
