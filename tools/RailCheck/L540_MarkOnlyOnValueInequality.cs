using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L540 — A MARK IS RAISED BY A VALUE THAT DIFFERS, NEVER BY "A WRITE HAPPENED".
    ///
    /// THE REPORTED SYMPTOM. An unrelated peer's manufacturing tick repainted the soldier-edit screen,
    /// which routes to a DESTRUCTIVE native refresh (UIStateEditSoldier.DisplaySoldier →
    /// UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true) → CommonCharacterUtils
    /// .ResetCharacterAnimation = Animator.Play(0,-1,0f)), resetting the soldier model and its animation.
    /// A large share of those repaints were owed to nothing: the batch rewrote leaves with the values they
    /// already held. Change detection must fire on VALUE INEQUALITY (Bevy set_if_neq, Unity DOTS chunk
    /// version numbers, §2.5).
    ///
    /// COMPARED BY VALUE OR BYTES, NEVER BY REFERENCE — the game mutates state in place, so reference
    /// memoization is useless (reselect FAQ, §2.5). That is arm (d), and it is the arm a naive
    /// implementation fails.
    ///
    /// SCOPE OF THE GATE, stated so a reader does not over-read this law. Only FieldClass.Leaf is
    /// snapshotable at the call site (GenericApplier.ApplyEntry): a leaf apply REPLACES the entity's
    /// reference, while every container class is mutated THROUGH the same reference, so a before/after
    /// comparison there would report "unchanged" for a real change. Containers keep marking
    /// unconditionally — the safe direction, and the one REACTIVITY demands.
    ///
    /// ARMS, all EXECUTED against the real GenericApplier.LeafChanged with no game:
    ///   (a) equal-value-marks — two equal boxed values do NOT change.
    ///   (b) different-value-silent — two different boxed values DO change (without this the law passes
    ///       against a predicate that always says "unchanged", which would freeze every screen).
    ///   (c) different-bytes-silent — the blob path, the changed direction.
    ///   (d) reference-equality-used — two DISTINCT byte arrays with identical contents compare as
    ///       UNCHANGED. A reference compare would call them different and mark on every batch.
    ///   (e) null-is-not-a-change — null vs null is unchanged; null vs a value is changed. An unreadable
    ///       field must degrade to "changed", never to "unchanged".
    ///
    /// ROLES SEPARATED (§C.3): LeafChanged is a pure function of two values with no peer, no session and
    /// no role — there is nothing role-dependent for one role to hide from the other.
    ///
    /// Falsify (compile-valid src mutations, each named): `LeafChanged => true;` → (a), (c); `=> false;`
    /// → (b); replace the byte loop with `ReferenceEquals(a, b)` → (d); return false for null/null
    /// asymmetrically → (e).
    /// </summary>
    internal static class L540_MarkOnlyOnValueInequality
    {
        internal static IEnumerable<string> Check()
        {
            var applier = typeof(GenericApplier);
            var changed = applier.GetMethod("LeafChanged", BindingFlags.Static | BindingFlags.NonPublic |
                                                           BindingFlags.Public);
            if (changed == null)
            {
                yield return "L540 premise-changed: GenericApplier.LeafChanged did not resolve, so this " +
                             "law cannot execute the decision it exists to constrain. Re-point it before " +
                             "believing the verdict.";
                yield break;
            }

            if (GenericApplier.LeafChanged(42, 42))
                yield return "L540 equal-value-marks: two equal values reported a change. A batch that " +
                             "rewrites a leaf with the value it already holds must raise NO mark — " +
                             "marking on write is the direct cause of an unrelated peer's manufacturing " +
                             "tick resetting the local soldier model and animation.";

            if (!GenericApplier.LeafChanged(42, 43))
                yield return "L540 different-value-silent: two DIFFERENT values reported no change. " +
                             "Without this direction the law is satisfied by a predicate that always " +
                             "answers 'unchanged', which freezes every screen — and a stale screen is a " +
                             "defect in this repo, not a cosmetic issue.";

            if (GenericApplier.LeafChanged(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }))
                yield return "L540 reference-equality-used: two DISTINCT byte arrays with identical " +
                             "contents reported a change. The game mutates state in place, so identity " +
                             "memoization is useless here — the comparison must be by VALUE or by hash, " +
                             "never by reference.";

            if (!GenericApplier.LeafChanged(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }))
                yield return "L540 different-bytes-silent: two different blobs reported no change.";
            if (!GenericApplier.LeafChanged(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }))
                yield return "L540 different-bytes-silent: blobs of different length reported no change.";

            if (GenericApplier.LeafChanged(null, null))
                yield return "L540 null-is-not-a-change: null vs null reported a change, so every " +
                             "unreadable field would mark on every batch and the scoping would buy " +
                             "nothing.";
            if (!GenericApplier.LeafChanged(null, 7))
                yield return "L540 positive-control: null vs a value reported NO change. An unreadable " +
                             "before-value must degrade to 'changed' — REACTIVITY is a hard mandate, so " +
                             "an unreadable model may cost a repaint and may never cost a stale screen.";
        }
    }
}
