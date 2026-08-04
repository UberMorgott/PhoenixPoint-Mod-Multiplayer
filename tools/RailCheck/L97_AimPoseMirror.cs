using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace RailCheck
{
    /// <summary>
    /// L97 — THE AIM POSE CROSSES, AND NOTHING ELSE ABOUT AIMING DOES.
    ///
    /// A8b restores exactly half of the removed A8: a soldier committed to manual aim holds the same
    /// ANIMATOR STANCE on every peer, so a shot starts from the same state everywhere.
    /// <c>TacticalLevelController.FireWeaponAtTargetCrt</c>:1645 skips the entire aim-start block
    /// (:1647-1678, an <c>AnimatorStateCheckpoint</c> wait with a 5 s ceiling) when
    /// <c>TacticalActor.CurrentlyAiming</c> is true — so without the mirror the acting peer fires instantly
    /// while every other window first plays the entry-into-aim, a built-in desync on EVERY shot.
    ///
    /// THE OTHER HALF WAS REMOVED BY DEVELOPER DECISION (2026-08-04) AND MUST STAY REMOVED: the
    /// third-person aim camera, the aim UI, the auto-enter/auto-leave of a watcher's aim view state and the
    /// TAB-target mirror. A watcher keeps its own free camera and its own screen. Arm (d) below is the one
    /// that stays red if any of it returns — and it scans STRING LITERALS as well as call edges, because
    /// the deleted code reached the view stack through <c>AccessTools.Method(…, "ActivateAttackAbilityState")</c>
    /// and a callee-only walk would see nothing but <c>AccessTools.Method</c>.
    ///
    /// Falsify: delete the <c>SetAimParams</c> call → <c>pose-inert</c>; swap it for
    /// <c>IdleAbility.ForceRefresh</c> → <c>pose-navigates</c> + <c>pose-inert</c> (3071859's defect: that
    /// path's consumption is <c>TacticalNav.ExecutePoints</c>, a real journey on a mirror); write
    /// <c>SetForward</c> from <c>ApplyStance</c> instead of arming the lerp → <c>facing-snaps</c>; re-add the
    /// auto-enter → <c>camera-ui-half-returned</c>; stop clearing the table per battle →
    /// <c>pose-leaks-battle</c>; break any <c>Decide</c> case → <c>arbiter-*</c>.
    /// </summary>
    internal static class L97AimPoseMirror
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        public static IEnumerable<string> Check(Assembly game)
        {
            var mirror = typeof(Multiplayer.Tactical.TacticalAimPoseSync);
            var apply = Method(mirror, "ApplyStance");
            var seam = NestedMethod(mirror, "AimStatePatch", "Postfix");

            // ─── (a) THE MIRROR WRITES THE ANIMATOR INTEGERS CurrentlyAiming ACTUALLY READS ───
            if (apply == null || seam == null)
            {
                yield return "L97 pose-unwired: TacticalAimPoseSync.ApplyStance / AimStatePatch.Postfix no " +
                             "longer resolve. Nothing then holds a mirrored soldier in the aim stance, so " +
                             "FireWeaponAtTargetCrt:1645 takes its 5 s aim-start wait on every peer but the " +
                             "one that aimed — the shot desync this arc exists to remove";
                yield break;
            }
            if (!Reaches(apply, "PathProcessorUtils", "SetAimParams"))
                yield return "L97 pose-inert: ApplyStance no longer calls PathProcessorUtils.SetAimParams. " +
                             "TacticalActor.CurrentlyAiming:228-238 reads ONLY Animator.GetInteger" +
                             "(\"TravelType\")==7 / (\"ShootSegmentType\")==5, so anything else poses nothing " +
                             "the fire coroutine can see — the seam would run and change nothing, this repo's " +
                             "dominant bug shape";
            if (!Reaches(apply, "PathProcessorUtils", "SetNullNavParams"))
                yield return "L97 pose-inert: ApplyStance no longer clears the stance with " +
                             "PathProcessorUtils.SetNullNavParams (the pair FireWeaponAtTargetCrt:1712 uses on " +
                             "itself), so a soldier who STOPPED aiming stays welded into the aim loop on every " +
                             "mirror until the battle ends";
            if (!Reaches(apply, "TacActorAnimActions", "ActivateShootingClips"))
                yield return "L97 pose-inert: ApplyStance no longer binds the shooting clips. " +
                             "FireWeaponAtTargetCrt:1566 does exactly that one line before setting these same " +
                             "params; without the override the \"Aim Loop\" animator state has no clip and the " +
                             "integers move a soldier into a pose with no animation";
            if (!Reaches(seam, "TacticalAimPoseSync", "Sample") || !Reaches(seam, "TacticalAimPoseSync", "Emit"))
                yield return "L97 pose-unwired: the TacticalViewState.Update postfix no longer samples and " +
                             "emits. Capture by SAMPLING is what makes this surface correct: UIStateShoot " +
                             "genuinely alternates target→null→target across ExitState:1261 and " +
                             "SetShootTarget:277, so an edge capture over-emits AND swallows at once (3071859)";

            // ─── (b) THE NAV BAN, MECHANICAL — the defect that got 3071859 reverted by 0252247 ───
            // Roots = every GAME method the mirror half calls to produce the pose. A closure over fewer than
            // all of them is a proof about the wrong set (0a66988).
            var roots = new[]
            {
                GameMethod(game, "PhoenixPoint.Tactical.Levels.PathProcessors.PathProcessorUtils", "SetAimParams"),
                GameMethod(game, "PhoenixPoint.Tactical.Levels.PathProcessors.PathProcessorUtils", "SetNullNavParams"),
                GameMethod(game, "PhoenixPoint.Tactical.Entities.Animations.TacActorAnimActions", "ActivateShootingClips"),
            };
            if (roots.Any(r => r == null))
                yield return "L97 premise-changed: one of PathProcessorUtils.SetAimParams / SetNullNavParams / " +
                             "TacActorAnimActions.ActivateShootingClips no longer resolves in Assembly-CSharp. " +
                             "The mirror is written against them verbatim and the nav ban is proved over them";
            else
            {
                // The two pose writers are walked DEEP (their tail is provably shallow: SetAimParams →
                // SetParams → Animator.SetInteger and nothing else). The clip bind is walked SHALLOW on
                // purpose — its tail runs through the anim-action def lookup, where a deep bounded walk would
                // start inventing edges, and it is not a pose write at all: the game itself calls it from
                // FireWeaponAtTargetCrt:1566. Two roots at two depths beats one number that is wrong for both.
                string hit = ClosureHit(new[] { roots[0], roots[1] }, 6) ?? ClosureHit(new[] { roots[2] }, 2);
                if (hit != null)
                    yield return "L97 pose-navigates: the pose writers now reach " + hit + ". A mirror must be " +
                                 "POSED, never MOVED: 3071859 fed a CoverPose to IdleAbility.ForceRefresh, whose " +
                                 "consumption IdleAbility.IdleAction:275-290 → RefreshIdle:324 → DoAimOrPeek:166 → " +
                                 "TacticalNav.ExecutePoints is a NAV TRAVERSAL — a jetpacked soldier re-flew to " +
                                 "his old tile and back, and reported HasExecutingAbility() the whole way";
            }
            foreach (var banned in new[] { "ForceRefresh", "RefreshIdle", "DoAimOrPeek", "ExecutePoints",
                                           "GetAimOrPeekPathPoints", "NavigateAndWaitUntilFinished" })
                if (Reaches(apply, null, banned))
                {
                    yield return "L97 pose-navigates: ApplyStance calls " + banned + " directly. Same defect, " +
                                 "one level up";
                    break;
                }

            // ─── (c) FACING LERPS, IT DOES NOT SNAP ───
            // SetForward is the ENDPOINT of the game's own facing lerp (TacticalNavigationComponent
            // .NoAnimsFace:1035-1053, Vector3.Slerp stepped by Timing.Delta * 6f). Writing it from the applier
            // reached the same heading in one frame — the target-switch teleport measured on every non-acting
            // instance while the acting one swung.
            if (Reaches(apply, "ActorComponent", "SetForward"))
                yield return "L97 facing-snaps: ApplyStance writes SetForward itself instead of arming the " +
                             "lerp. That is the ENDPOINT of the native facing swing, reached in one frame — " +
                             "mirrored soldiers teleport their heading on every TAB retarget";
            var advance = Method(mirror, "AdvanceTurns");
            if (advance == null || !Reaches(advance, "ActorComponent", "SetForward") ||
                !Reaches(advance, "Vector3", "Slerp"))
                yield return "L97 facing-frozen: AdvanceTurns no longer steps the mirrored turn with " +
                             "Vector3.Slerp → SetForward, so an armed turn never completes and a mirrored " +
                             "soldier holds the aim pose facing wherever he last stood";
            if (advance != null && Reaches(advance, "Time", "get_deltaTime"))
                yield return "L97 facing-wallclock: AdvanceTurns steps on Time.deltaTime. The game's own " +
                             "NoAnimsFace reads base.Timing.Delta, which carries that actor's TimingScale (an " +
                             "unrevealed actor runs at 4x) — the wall clock makes mirrored turns disagree with " +
                             "native ones exactly where the game speeds them up";

            // ─── (d) THE CAMERA / UI HALF MAY NOT COME BACK ───
            // Call edges AND string literals: the deleted code reached the view stack by reflection
            // (AccessTools.Method(type, "ActivateAttackAbilityState")), which no callee walk can see.
            var viewStackNames = new[] { "ActivateAttackAbilityState", "SwitchToPreviousState", "SwitchToState",
                                         "EnterFpsCamera", "EnterState", "ExitState" };
            foreach (var m in AllMethodsOf(mirror))
            {
                string found = viewStackNames.FirstOrDefault(n => Reaches(m, null, n))
                               ?? Literals(m).FirstOrDefault(viewStackNames.Contains);
                if (found == null) continue;
                yield return "L97 camera-ui-half-returned: " + Describe(m) + " reaches \"" + found + "\". A8b " +
                             "restores the POSE only — the third-person aim camera, the aim UI and the " +
                             "auto-enter/auto-leave of a watcher's aim view state were removed by developer " +
                             "decision (2026-08-04): a watcher must NOT get the aiming player's camera or aim " +
                             "interface, and an arriving message must never move anyone's state stack (an " +
                             "Enter of an already-popped state resurrects a zombie that re-fires its cached " +
                             "ability, 7933bbe / law L63)";
                break;
            }
            if (SurfaceId("TacAimPose") == 0x85 || SurfaceId("TacAimPoseIntent") == 0x86)
                yield return "L97 tombstone-reused: A8b is riding the RETIRED 0x85/0x86 ids. A new sender on a " +
                             "retired id silently mis-routes on a peer still carrying the old meaning — the " +
                             "whole reason SurfaceIds keeps tombstones";

            // ─── (e) THE TABLE IS PER BATTLE ───
            var reset = Method(mirror, "ResetForBattle");
            if (reset == null || !Reaches(seam, "TacticalAimPoseSync", "ResetForBattle") ||
                !WritesClear(reset, mirror, "_aim"))
                yield return "L97 pose-leaks-battle: the shared table is no longer cleared when the battle " +
                             "changes. Actor keys are RE-DERIVED per battle (negative ordinals over battle-start " +
                             "position), so a carried-over table poses the next battle's actors off a dead one's " +
                             "values — and a soldier welded into the aim loop by a stale entry never leaves it";

            // ─── (f) THE ARBITER, EXECUTED ───
            foreach (var complaint in Arbiter()) yield return complaint;

            // ─── (g) PREMISES — say so rather than let them rot ───
            var actor = game.GetType("PhoenixPoint.Tactical.Entities.TacticalActor");
            var aiming = actor?.GetProperty("CurrentlyAiming", AllMembers)?.GetGetMethod(true);
            if (aiming == null || !Reaches(aiming, "Animator", "GetInteger"))
                yield return "L97 premise-changed: TacticalActor.CurrentlyAiming no longer reads " +
                             "Animator.GetInteger (a game patch backing it with a model field). The integers " +
                             "this mirror writes would then mean NOTHING and every mirrored shot would " +
                             "silently replay the aim-in animation again — the exact desync, with a green law";
            var setParams = GameMethod(game, "PhoenixPoint.Tactical.Levels.PathProcessors.PathProcessorUtils",
                                       "SetParams");
            if (setParams == null || !Reaches(setParams, "Animator", "SetInteger"))
                yield return "L97 premise-changed: PathProcessorUtils.SetParams no longer bottoms out in " +
                             "Animator.SetInteger. Its emptiness IS the nav-freedom proof — it cannot allocate " +
                             "a PathPointInfo, start a coroutine, or reach TacticalNav at all";
            // FireWeaponAtTargetCrt is an iterator: its own body only news up the state machine, so the test
            // has to be against the compiler-generated MoveNext or it is red against correct code.
            var fire = CoroutineBody(game.GetType("PhoenixPoint.Tactical.Levels.TacticalLevelController"),
                                     "FireWeaponAtTargetCrt");
            if (fire != null && !ClosureHitsName(fire, "get_CurrentlyAiming", 3))
                yield return "L97 premise-changed: FireWeaponAtTargetCrt no longer tests CurrentlyAiming, so " +
                             "holding the stance no longer skips the aim-start wait and this whole surface " +
                             "buys nothing. Re-derive what the shot now branches on before keeping it";
        }

        // ─── the pure arbiter, run on the six cases the shared-battle model actually produces ───

        private static IEnumerable<string> Arbiter()
        {
            // (liveActor, liveTarget, reportedActor, reportedTarget, sharedForReported, sharedForLive)
            foreach (var c in new[]
            {
                Case("fresh-aim",        1, 9, 0, 0, 0, 0, false, true),
                Case("steady-state",     1, 9, 1, 9, 9, 9, false, false),
                // A new target on the SAME soldier needs no separate clear: the assert overwrites the entry.
                Case("retarget",         1, 8, 1, 9, 9, 9, false, true),
                Case("self-heal",        1, 9, 1, 9, 0, 0, false, true),
                Case("leaving-clears",   0, 0, 1, 9, 9, 0, true,  false),
                Case("no-overwrite",     0, 0, 1, 9, 7, 0, false, false),
                Case("no-contest",       1, 9, 1, 9, 7, 7, false, false),
            })
                if (c != null) yield return c;
        }

        private static string Case(string name, int la, int lt, int ra, int rt, int sr, int sl,
                                   bool wantClear, bool wantAssert)
        {
            bool clear, assert;
            Multiplayer.Tactical.TacticalAimPoseSync.Decide(la, lt, ra, rt, sr, sl, out clear, out assert);
            if (clear == wantClear && assert == wantAssert) return null;
            return "L97 arbiter-" + name + ": Decide(live " + la + "→" + lt + ", reported " + ra + "→" + rt +
                   ", shared[reported]=" + sr + ", shared[live]=" + sl + ") answered clear=" + clear +
                   " assert=" + assert + ", expected clear=" + wantClear + " assert=" + wantAssert +
                   ". Last-writer-wins over one soldier two peers may both aim is decided HERE and nowhere " +
                   "else; a wrong answer is either a stance nobody holds pinned onto a mirror, or an " +
                   "unbounded re-assert ping-pong between two peers aiming him at two different enemies";
        }

        // ─── helpers (self-contained: Program.cs is not partial, so its own copies are out of reach) ───

        private static MethodBase Method(Type owner, string name) => owner?.GetMethod(name, AllMembers);

        private static MethodBase NestedMethod(Type owner, string nested, string name) =>
            Method(owner?.GetNestedType(nested, AllMembers), name);

        private static MethodBase GameMethod(Assembly game, string typeName, string name) =>
            Method(game.GetType(typeName), name);

        /// <summary>The <c>MoveNext</c> of an iterator's generated state machine — where a coroutine's real IL
        /// lives. Walking the declared method instead would see only the constructor.</summary>
        private static MethodBase CoroutineBody(Type owner, string name)
        {
            var sm = owner?.GetNestedTypes(AllMembers)
                          .FirstOrDefault(t => t.Name.Contains("<" + name + ">"));
            return Method(sm, "MoveNext");
        }

        private static byte SurfaceId(string name)
        {
            var f = typeof(Multiplayer.Network.Sync.SurfaceIds).GetField(name, AllMembers);
            return f == null ? (byte)0 : (byte)f.GetRawConstantValue();
        }

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
        {
            foreach (var m in t.GetMethods(AllMembers)) yield return m;
            foreach (var n in t.GetNestedTypes(AllMembers))
                foreach (var m in n.GetMethods(AllMembers)) yield return m;
        }

        private static string Describe(MethodBase m) =>
            (m.DeclaringType == null ? "" : m.DeclaringType.Name + ".") + m.Name;

        private static bool Reaches(MethodBase m, string ownerName, string calleeName) =>
            m != null && Callees(m).Any(c => c.Name == calleeName &&
                                             (ownerName == null || (c.DeclaringType != null &&
                                                                    c.DeclaringType.Name == ownerName)));

        /// <summary>True when <paramref name="m"/> calls <c>Clear</c> on the named field — enough to prove the
        /// per-battle wipe really empties the table rather than merely being called.</summary>
        private static bool WritesClear(MethodBase m, Type owner, string field)
        {
            var f = owner.GetField(field, AllMembers);
            return f != null && Reaches(m, null, "Clear");
        }

        /// <summary>Every string this method pushes with <c>ldstr</c>. The removed camera/UI half reached the
        /// view stack through <c>AccessTools.Method(type, "…")</c>, so the NAME is the only edge there is.
        /// </summary>
        private static IEnumerable<string> Literals(MethodBase m)
        {
            foreach (var step in Walk(m))
            {
                if (step.Value.Op != OpCodes.Ldstr) continue;
                string s = null;
                try { s = m.Module.ResolveString(BitConverter.ToInt32(step.Key, step.Value.Pos)); } catch { }
                if (s != null) yield return s;
            }
        }

        /// <summary>Transitive callee walk over the GAME assembly from several roots, bounded by depth.
        /// Returns the first banned edge as "Owner.Method", or null. Deliberately does NOT root at
        /// <c>ActorComponent.SetForward</c>: that is the game's OWN per-frame facing primitive (NoAnimsFace
        /// calls it every frame of a native swing) and its transitive tail runs through the level controller's
        /// ActorMoved event, where a depth-bounded walk would invent edges — a law that cries wolf is a law
        /// that gets ignored. Arm (c) covers the facing half by shape instead.</summary>
        private static string ClosureHit(MethodBase[] roots, int depth)
        {
            var bannedOwners = new[] { "IdleAbility", "TacticalNavigationComponent" };
            var bannedNames = new[] { "ExecutePoints", "GetAimOrPeekPathPoints", "NavigateAndWaitUntilFinished" };
            var asm = roots[0].DeclaringType.Assembly;
            var seen = new HashSet<MethodBase>();
            var frontier = roots.ToList();
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                    foreach (var c in Callees(m))
                    {
                        var owner = c.DeclaringType;
                        if (owner != null && bannedOwners.Contains(owner.Name)) return Describe(c);
                        if (bannedNames.Contains(c.Name)) return Describe(c);
                        if (owner != null && owner.Assembly == asm && seen.Add(c)) next.Add(c);
                    }
                frontier = next;
            }
            return null;
        }

        private static bool ClosureHitsName(MethodBase root, string name, int depth)
        {
            var seen = new HashSet<MethodBase>();
            var frontier = new List<MethodBase> { root };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                    foreach (var c in Callees(m))
                    {
                        if (c.Name == name) return true;
                        if (c.DeclaringType != null && c.DeclaringType.Assembly == root.DeclaringType.Assembly &&
                            seen.Add(c)) next.Add(c);
                    }
                frontier = next;
            }
            return false;
        }

        private static List<MethodBase> Callees(MethodBase m)
        {
            var seq = new List<MethodBase>();
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineMethod ||
                    (step.Value.Op != OpCodes.Call && step.Value.Op != OpCodes.Callvirt)) continue;
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(step.Key, step.Value.Pos),
                                                      typeArgs, methodArgs); } catch { }
                if (callee != null) seq.Add(callee);
            }
            return seq;
        }

        private struct Step { public OpCode Op; public int Pos; }

        /// <summary>IL walked with the real operand-size table — a naive byte scan would match operand bytes
        /// and invent edges. Anything unparseable ABANDONS the method rather than guessing.</summary>
        private static IEnumerable<KeyValuePair<byte[], Step>> Walk(MethodBase m)
        {
            byte[] il = null;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                OpCode op;
                if (!OpCodeByValue.TryGetValue(code, out op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                yield return new KeyValuePair<byte[], Step>(il, new Step { Op = op, Pos = i });
                i += size;
            }
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodes();

        private static Dictionary<short, OpCode> BuildOpCodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); map[op.Value] = op; }
            return map;
        }

        private static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return pos + 4 > il.Length ? -1 : 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return -1;
            }
        }
    }
}
