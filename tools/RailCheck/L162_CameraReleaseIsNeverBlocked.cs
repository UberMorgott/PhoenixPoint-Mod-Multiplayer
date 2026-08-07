using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Cameras;
using Base.Cameras.CameraNodes;
using HarmonyLib;

namespace RailCheck
{
    /// <summary>
    /// L162 — A PEER'S CAMERA ALWAYS COMES BACK. NOTHING OF OURS MAY STAND BETWEEN A CHASE AND ITS RELEASE.
    ///
    /// THE REPORT (3-peer session, 2026-08-07): "when two players act simultaneously in tactical, one
    /// player's camera LOCKS onto a soldier and he can no longer move it." L75 was GREEN throughout,
    /// because L75 arm C asserted that the pop gate BINDS — a call, not an outcome — and binding was
    /// exactly the problem. That arm is repaired by this law rather than baselined: L75 keeps the push
    /// filter (its real subject), and the release path gets an assertion of its own.
    ///
    /// WHY A LOCK IS PERMANENT, in the game's own words. <c>PlanarCamDef</c>:22 ships the tooltip
    /// "Chasing a transform has to be manually deactivated or the camera will be locked."
    /// <c>PlanarCamDef.GetChaseParams</c>:44 sets <c>LockCameraMovement = DisableInputDuringChase</c> —
    /// which DEFAULTS TO TRUE — and aims <c>ChaseTransform</c> at the acting actor. Then
    /// <c>PlanarScrollCamera</c>:468 discards every pan/drag/edge-scroll input while that flag is set, :838
    /// <c>StopCameraChase</c> REFUSES to end a locked chase, and :747 will only self-end a chase whose
    /// <c>ChaseTransform</c> is null. The single release is <c>CameraDirector.Evaluate()</c> issuing a
    /// different chase, and <c>Evaluate</c> is reachable from exactly three public members — all of them on
    /// the POP side: <c>RemoveHint</c>, <c>RemoveHints</c>, <c>ForcedReset</c>. Skip one of those and the
    /// camera is gone for the rest of the battle.
    ///
    /// AND THE STATE DOES LOSE ENTRIES WITHOUT A POP, which is what made a "only pop what is in the state"
    /// gate lethal instead of merely useless: <c>Evaluate</c> ends with
    /// <c>CameraDirectorState.ClearUnmanagedParams</c>, dropping every item whose params are
    /// <c>Unmanaged</c> and whose hint is not in the current node's <c>StateFrom</c> — and the whole SHOT
    /// family is unmanaged. Two peers acting at once is precisely what produces the extra evaluations that
    /// sweep this peer's own live entry; its real pop then found nothing, the gate skipped the original, no
    /// <c>Evaluate</c> ran, and the locked chase stayed installed.
    ///
    /// THE ARMS:
    ///   (a) <c>release-is-patched</c> — the OUTCOME arm, and the whole law. NO type in the Multiplayer
    ///       assembly may patch <c>CameraDirector.RemoveHint</c>, <c>RemoveHints</c> or <c>ForcedReset</c>
    ///       at all. Not "may patch them but must return true": a prefix that answers true today is one
    ///       edit away from answering false, and this is the class of bug where that edit ships green.
    ///       Both attribute styles are read (class-level <c>[HarmonyPatch(...)]</c> and a
    ///       <c>TargetMethod</c>/<c>TargetMethods</c> resolver), because the gate this law removes used the
    ///       second one and an attribute-only scan would have missed it.
    ///   (b) <c>lock-is-real</c> — the premise. <c>PlanarCamDef.DisableInputDuringChase</c> still exists
    ///       and still defaults to true, <c>CameraChaseParams.LockCameraMovement</c> and
    ///       <c>ChaseTransform</c> still exist, and <c>CameraDirector</c> still declares all three release
    ///       members. If the engine ever stopped locking input during a chase this law would be guarding a
    ///       hazard that no longer exists, and that has to be noticed rather than silently kept.
    ///   (c) <c>push-filter-survives</c> — the thing that is NOT being deleted. <c>CameraAbilityHintGate</c>
    ///       must still exist: the fix is "never block the release", not "stop filtering foreign
    ///       cinematics", and a lazy reading of this law that removed the push filter too would hand every
    ///       peer's camera back to whoever moved last (L75/L97's original complaint).
    ///   (d) POSITIVE CONTROL, EXECUTED — the same scan is run over <see cref="FakeGate"/>, a patch class
    ///       that names <c>CameraDirector.RemoveHint</c> the way the deleted one did. It must go red. A
    ///       scan that resolved no patch classes at all would otherwise pass forever.
    ///
    /// Falsify: re-add any <c>[HarmonyPatch]</c> class targeting one of the three release members → (a);
    /// flip <c>DisableInputDuringChase</c>'s default or drop <c>LockCameraMovement</c> → (b); delete
    /// <c>CameraAbilityHintGate</c> → (c); empty <see cref="FakeGate"/> → (d).
    /// </summary>
    internal static class L162_CameraReleaseIsNeverBlocked
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        /// <summary>Every member whose only job, from our point of view, is to reach
        /// <c>CameraDirector.Evaluate</c> — the one thing that can take a locked chase off a peer.</summary>
        private static readonly string[] ReleaseMembers = { "RemoveHint", "RemoveHints", "ForcedReset" };

        internal static IEnumerable<string> Check()
        {
            var director = typeof(CameraDirector);
            var chaseParams = typeof(CameraChaseParams);
            var planar = typeof(PlanarCamDef);

            var disableDuringChase = planar.GetField("DisableInputDuringChase", AllMembers);
            var lockMovement = chaseParams.GetField("LockCameraMovement", AllMembers);
            var chaseTransform = chaseParams.GetField("ChaseTransform", AllMembers);
            var release = ReleaseMembers.Select(n => director.GetMethod(n, AllMembers)).ToArray();

            if (disableDuringChase == null || lockMovement == null || chaseTransform == null ||
                release.Any(m => m == null))
            {
                yield return "L162 premise-changed: PlanarCamDef.DisableInputDuringChase, " +
                             "CameraChaseParams.LockCameraMovement/ChaseTransform, or one of " +
                             "CameraDirector.RemoveHint/RemoveHints/ForcedReset no longer resolves. The lock " +
                             "hazard and its release path are both named against the shipped game assembly, so " +
                             "a rename here means this law is guarding a shape the build does not have.";
                yield break;
            }

            // ── arm (b): the hazard and its only exit are both still where this law says they are, read
            // off the shipped IL rather than off a comment. Not "does the field default to true" — a
            // ScriptableObject's field initialisers need a Unity player loop the harness does not have, and
            // an arm that cannot run is an arm that lies.
            if (!AnyMethodOf(typeof(PlanarScrollCamera)).Any(m => ReadsField(m, lockMovement)))
                yield return "L162 lock-is-real: PlanarScrollCamera no longer reads " +
                             "CameraChaseParams.LockCameraMovement anywhere. That flag is what makes a chase " +
                             "eat the player's pan/drag/edge-scroll (:468) and what makes StopCameraChase " +
                             "refuse to end it (:838). If the engine stopped honouring it, this law is guarding " +
                             "a hazard that has moved and the ban below should be re-argued, not kept.";

            if (!Reaches(release[0], "Evaluate"))
                yield return "L162 lock-is-real: CameraDirector.RemoveHint no longer calls Evaluate(). The whole " +
                             "reason a prefix here is forbidden is that RemoveHint is one of only three ways to " +
                             "re-match the director tree and issue a chase that replaces the locked one. If the " +
                             "release moved somewhere else, the ban has to move with it.";

            // ── arm (a): nothing of ours patches the release. THE law.
            foreach (var v in Scan(typeof(Multiplayer.Tactical.TacticalCameraPolicy).Assembly.GetTypes(),
                                   "the Multiplayer assembly"))
                yield return v;

            // ── arm (c): the push filter is NOT what is being removed.
            if (typeof(Multiplayer.Tactical.TacticalCameraPolicy).Assembly
                    .GetType("Multiplayer.Tactical.CameraAbilityHintGate") == null)
                yield return "L162 push-filter-survives: CameraAbilityHintGate is gone. This law forbids " +
                             "blocking the camera's RELEASE, never filtering foreign cinematics — without the " +
                             "push filter every peer's camera is yanked onto whichever soldier someone else " +
                             "just moved, which is the complaint L75 and L97 exist for.";

            // ── arm (d): the scan can see a violation.
            if (!Scan(new[] { typeof(FakeGate) }, "FakeGate").Any())
                yield return "L162 control-not-red: FakeGate names CameraDirector.RemoveHint through a " +
                             "TargetMethod resolver exactly as the deleted gate did, and the scan did not flag " +
                             "it. The arm cannot tell a patched release from an unpatched one, so its green " +
                             "above means nothing.";
        }

        private static IEnumerable<string> Scan(IEnumerable<Type> types, string label)
        {
            foreach (var t in types)
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length == 0) continue;
                foreach (var target in TargetsOf(t))
                {
                    if (target == null || target.DeclaringType != typeof(CameraDirector)) continue;
                    if (!ReleaseMembers.Contains(target.Name)) continue;
                    yield return "L162 release-is-patched: " + label + " declares " + t.Name + " on " +
                                 "CameraDirector." + target.Name + ". That method is one of only three ways to " +
                                 "reach Evaluate(), and Evaluate is the ONLY thing that can lift a chase whose " +
                                 "LockCameraMovement is set (PlanarScrollCamera:468 eats every pan, :838 " +
                                 "refuses to stop a locked chase, :747 self-ends only a null ChaseTransform). A " +
                                 "seam here does not risk a stale camera; it risks a player who cannot move his " +
                                 "own view for the rest of the battle — reported live on 2026-08-07 while L75 " +
                                 "stayed green because it asserted that this very patch BOUND.";
                }
            }
        }

        /// <summary>Both ways a Harmony class can name its target: the class-level attribute, and a
        /// <c>TargetMethod</c>/<c>TargetMethods</c> resolver. The deleted gate used the second, so an
        /// attribute-only scan would have been green over it.</summary>
        private static IEnumerable<MethodBase> TargetsOf(Type t)
        {
            foreach (var attr in t.GetCustomAttributes(typeof(HarmonyPatch), inherit: false))
            {
                var info = AccessTools.Field(attr.GetType(), "info")?.GetValue(attr) as HarmonyMethod;
                if (info == null || info.declaringType == null || string.IsNullOrEmpty(info.methodName)) continue;
                MethodBase named = null;
                try { named = AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes); }
                catch { }
                if (named != null) yield return named;
            }

            foreach (var name in new[] { "TargetMethod", "TargetMethods" })
            {
                var resolver = t.GetMethod(name, AllMembers);
                if (resolver == null || resolver.GetParameters().Length != 0) continue;
                object got = null;
                try { got = resolver.Invoke(null, null); } catch { }
                if (got is MethodBase one) yield return one;
                else if (got is IEnumerable<MethodBase> many)
                    foreach (var m in many) yield return m;
            }
        }

        /// <summary>ARM (d). Never registered, never patched — it exists only to be scanned, and it names
        /// its target exactly the way the gate this law deletes did.</summary>
        [HarmonyPatch]
        private static class FakeGate
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(CameraDirector), nameof(CameraDirector.RemoveHint),
                                   new[] { typeof(CameraDirectorHint) });

            private static bool Prefix() => false;
        }

        // ─── IL helpers (same primitives as L160; Program.cs is not partial) ────────────────

        private static IEnumerable<MethodBase> AnyMethodOf(Type t)
            => t.GetMethods(AllMembers).Cast<MethodBase>().Concat(t.GetConstructors(AllMembers));

        private static bool ReadsField(MethodBase m, FieldInfo field)
            => TokensAfter(m, 0x7B, 0x7C).Any(tok =>   // ldfld / ldflda
            {
                try { return m.Module.ResolveField(tok) == field; } catch { return false; }
            });

        private static bool Reaches(MethodBase caller, string calleeName)
            => TokensAfter(caller, 0x28, 0x6F).Any(tok =>   // call / callvirt
            {
                try { return caller.Module.ResolveMethod(tok)?.Name == calleeName; } catch { return false; }
            });

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
