using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Common.Core;

namespace RailCheck
{
    /// <summary>
    /// L103 — THE NATIVE TACTICAL ENTRY IS AN EXPERIMENT, AND AN EXPERIMENT THAT CANNOT BE TURNED OFF IS
    /// NOT AN EXPERIMENT.
    ///
    /// The shipping entry is the mid-tactical SAVE TRANSFER: the host writes a save at deploy-ready and every
    /// client builds its battle from those exact bytes (<c>SaveTransferCoordinator
    /// .HostBeginTacticalEntryTransfer</c> → <c>SendBlob</c> → <c>OnSaveChunk</c>/<c>OnSaveDone</c> →
    /// <c>PrepareEntryFromBlobCrt</c> → <c>EnterLevel</c>). The native path
    /// (<see cref="NativeTacticalEntry"/>) instead ships the GENERATION RESULT — one
    /// <c>TacticalGameParams</c>, kilobytes — and lets every peer re-enter the game's own
    /// <c>GeoLevelController.LaunchTacticalGame</c>. It is unproven, and the same save apparatus also serves
    /// the mid-session join and the F2 reload, so this law's job is to keep the retreat open and the seam
    /// honest rather than to bless the experiment.
    ///
    /// ARM (a) THE BRACKET MUST GIVE THE STREAM BACK. Seeding <c>UnityEngine.Random</c> and swapping
    /// <c>SharedData.Random</c> for a generation window is only safe because the window is closed again:
    /// <c>Random.state</c> is process-global and <c>SharedData.Random</c> is a public field every later
    /// scatter roll, damage roll and fumble draws from (<c>SharedData.cs:70</c>). A bracket that enters and
    /// never exits does not desync the battle — it makes every roll for the rest of the session a function of
    /// a map seed, silently. So the law reads the IL: <c>EnterSeed</c> must READ <c>Random.state</c> and
    /// <c>ExitSeed</c> must WRITE it back, and both must touch <c>SharedData.Random</c>.
    ///
    /// ARM (b) IT MUST BE A FINALIZER, NOT A POSTFIX. A Harmony postfix does not run when the original
    /// throws, and both bracketed methods can throw — <c>GeoMission.GetPlotForMission</c>:577 throws
    /// outright when a mission has no eligible plot, and deployment reports problems from inside
    /// <c>DeployForTurn</c>. A leaked bracket is arm (a)'s failure by another route, so the shape is pinned.
    ///
    /// ARM (c) NO SAVE-TRANSFER MEMBER MAY BE DELETED. The named members are the whole retreat, and three of
    /// them (<c>PrepareEntryFromBlobCrt</c>, <c>EnterLevel</c>, <c>OnSaveDone</c>) additionally carry the
    /// mid-session on-demand join and the F2 reload — deleting one to "clean up after the experiment" breaks
    /// two features that have nothing to do with tactical entry.
    ///
    /// ARM (d) THE FALLBACK MUST STAY REACHABLE WHILE THE EXPERIMENT IS ON. The single point where the host
    /// decides to ship a save is <c>TacDeployReadyCapture</c>'s coroutine; if native mode short-circuits it
    /// on <c>Enabled</c> ALONE then a client that could not build its battle locally has asked for a save
    /// that will never come, and sits behind the curtain until the 180 s reveal deadline. The coroutine must
    /// therefore consult <c>FallbackArmed</c> as well as <c>Enabled</c>, and must still reach
    /// <c>HostBeginTacticalEntryTransfer</c>.
    ///
    /// ARM (e) EVERY NATIVE-MODE PATCH MUST BE GATED ON THE SWITCH. Harmony's <c>Prepare()</c> is what keeps
    /// the three patches from binding at all when the experiment is off — the difference between "a flag we
    /// check" and "a flag that means the code is not there". The law does not assert the switch's VALUE
    /// (turning the experiment on is a decision, not a violation); it asserts the gate exists on each patch.
    ///
    /// ARM (f) THE SEED MUST COME FROM REPLICATED STATE. <c>GeoSite.RandomSeed</c> already rides the rail
    /// (docs/rail-baseline.txt:706), which is the whole reason it can be the spawn seed. A seed pulled from
    /// <c>Environment.TickCount</c>, <c>DateTime.Now</c>, <c>Guid</c> or <c>Stopwatch</c> — the exact shape
    /// TFTV uses ~15 times over — reproduces nothing on a second machine, so those are named and banned in
    /// the capture path.
    ///
    /// ARM (g) THE SURFACE MUST BE CLAIMED EVEN WHEN THE EXPERIMENT IS OFF. <c>SurfaceRouter</c> consults the
    /// tactical hook before the geoscape one precisely so a tactical id cannot be eaten by a geoscape
    /// handler (law L62). A handler that returns false while disabled hands 0x89 to the geoscape chain.
    /// </summary>
    internal static class L103_NativeTacticalEntry
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var native = typeof(NativeTacticalEntry);

            // ── (a) the bracket gives the stream back ──────────────────────
            var enter = native.GetMethod("EnterSeed", All);
            var exit = native.GetMethod("ExitSeed", All);
            if (enter == null || exit == null)
            {
                yield return "L103 bracket-gone: NativeTacticalEntry.EnterSeed/ExitSeed no longer exist — the " +
                             "seed bracket is what makes a locally-built battle reproducible at all, and " +
                             "without the pair there is nothing to restore UnityEngine.Random with.";
            }
            else
            {
                var unityRandom = typeof(UnityEngine.Random);
                var getState = unityRandom.GetProperty("state", All)?.GetGetMethod();
                var setState = unityRandom.GetProperty("state", All)?.GetSetMethod();
                var sharedRandom = typeof(SharedData).GetField("Random", All);

                if (getState == null || setState == null)
                    yield return "L103 bracket-premise: UnityEngine.Random.state has no readable+writable " +
                                 "property pair any more. InitState/value/Range are InternalCall and cannot be " +
                                 "patched, so state get/set was the ONLY handle on the stream — the bracket's " +
                                 "whole premise is gone and the experiment must be re-grounded, not adjusted.";
                else
                {
                    if (!Calls(enter, getState))
                        yield return "L103 bracket-no-save: EnterSeed does not READ UnityEngine.Random.state, so " +
                                     "it has nothing to put back — every roll after the generation window is a " +
                                     "function of the map seed, with no log line to say so.";
                    if (!Calls(exit, setState))
                        yield return "L103 bracket-no-restore: ExitSeed does not WRITE UnityEngine.Random.state. " +
                                     "Random.state is process-global: the seeded stream then owns scatter " +
                                     "(Weapon.cs:481/487/490), damage rolls and fumbles for the rest of the session.";
                }

                if (sharedRandom == null)
                    yield return "L103 bracket-premise: SharedData.Random is no longer a field — the swap the " +
                                 "bracket performs (SharedData.cs:70, the generator every deploy shuffle and " +
                                 "placement draw uses) cannot be done by assignment any more.";
                else
                {
                    if (!Touches(enter, sharedRandom) || !Touches(exit, sharedRandom))
                        yield return "L103 bracket-shared-half: EnterSeed/ExitSeed must BOTH touch " +
                                     "SharedData.Random — it is the generator TacticalDeployZone" +
                                     ".GetOrderedSpawnPositions:277 and TacParticipantSpawn's placement draws " +
                                     "use, so seeding only UnityEngine.Random reproduces the map and not the board.";
                }
            }

            // ── (b) a finalizer, never a postfix ──────────────────────────
            foreach (var patch in new[] { typeof(DeploymentSetSeedBracket), typeof(InitialDeploySeedBracket) })
            {
                if (patch.GetMethod("Finalizer", All) == null)
                    yield return "L103 bracket-leaks-on-throw: " + patch.Name + " has no Finalizer. A Harmony " +
                                 "POSTFIX is skipped when the original throws, and the bracketed windows do " +
                                 "throw (GeoMission.GetPlotForMission:577 throws on a mission with no eligible " +
                                 "plot); the seeded stream would then never be given back.";
                if (patch.GetMethod("Postfix", All) != null)
                    yield return "L103 bracket-wrong-exit: " + patch.Name + " exits through a Postfix. Use the " +
                                 "Finalizer only — two exits means the restore can run twice, and the postfix " +
                                 "half still does not run on the throw path it was added to cover.";
            }

            // ── (c) the retreat is intact ─────────────────────────────────
            var coord = typeof(SaveTransferCoordinator);
            foreach (var name in new[]
                     {
                         "HostBeginTacticalEntryTransfer", "HostTacticalEntryTransferCrt",
                         "AbortTacticalEntryTransfer", "OnEntryTransferAbort", "OpenTacticalEntryBarrier",
                         "HostWriteTacticalSaveCrt", "PrepareEntryFromBlobCrt", "EnterLevel",
                         "OnSaveChunk", "OnSaveDone", "SendBlob"
                     })
                if (coord.GetMethod(name, All) == null)
                    yield return "L103 fallback-deleted: SaveTransferCoordinator." + name + " is gone. The save " +
                                 "transfer is the entry that WORKS and the experiment's only retreat; three of " +
                                 "these members also carry the mid-session on-demand join and the F2 reload, so " +
                                 "removing one for the experiment's sake breaks features it never touched.";

            // ── (d) + (e) the switch, and the fallback under it ───────────
            var enabled = native.GetField("Enabled", All);
            if (enabled == null || enabled.FieldType != typeof(bool) || !enabled.IsStatic || !enabled.IsInitOnly)
                yield return "L103 switch-shape: NativeTacticalEntry.Enabled must be a `static readonly bool` — " +
                             "readonly so no runtime path can flip the experiment on behind the player, and NOT " +
                             "`const`, because a const folds every `if (Enabled && …)` out at compile time and " +
                             "takes the fallback wiring arm (d) reads with it. A retreat that is not in the " +
                             "assembly is not a retreat.";

            foreach (var patch in new[]
                     { typeof(NativeEntryCapture), typeof(DeploymentSetSeedBracket), typeof(InitialDeploySeedBracket) })
                if (patch.GetMethod("Prepare", All) == null)
                    yield return "L103 patch-ungated: " + patch.Name + " has no Prepare(). With the experiment " +
                                 "off its patch would still BIND — the difference between a flag that is checked " +
                                 "and a flag that means the code is not in the game at all.";

            var capture = typeof(TacDeployReadyCapture);
            var crt = capture.GetMethods(All).FirstOrDefault(m => m.Name.IndexOf("CaptureWhenPlayable", StringComparison.Ordinal) >= 0);
            var crtBody = crt == null ? null : MoveNextOf(crt);
            if (crtBody == null)
                yield return "L103 capture-gone: TacDeployReadyCapture's deploy-ready coroutine is unreachable — " +
                             "it is the ONE place the host decides to ship the entry save, so neither the " +
                             "shipping path nor the experiment's fallback has a trigger any more.";
            else
            {
                var fallbackArmed = native.GetProperty("FallbackArmed", All)?.GetGetMethod(nonPublic: true)
                                    ?? native.GetProperty("FallbackArmed", All)?.GetGetMethod();
                if (fallbackArmed == null || !Calls(crtBody, fallbackArmed))
                    yield return "L103 fallback-unreachable: the deploy-ready coroutine does not consult " +
                                 "NativeTacticalEntry.FallbackArmed. Skipping the transfer on Enabled ALONE " +
                                 "means a peer that answered 'I cannot build this locally' waits for a save " +
                                 "that is never written, and only the 180 s reveal deadline ends it.";
                var begin = coord.GetMethod("HostBeginTacticalEntryTransfer", All);
                if (begin != null && !Calls(crtBody, begin))
                    yield return "L103 fallback-unwired: the deploy-ready coroutine no longer calls " +
                                 "HostBeginTacticalEntryTransfer — the retreat exists but nothing walks it.";
            }

            // ── (f) the seed comes from replicated state ──────────────────
            var hostCapture = native.GetMethod("HostCapture", All);
            var randomSeed = typeof(GeoSite).GetProperty("RandomSeed", All)?.GetGetMethod();
            if (hostCapture == null)
                yield return "L103 capture-half-gone: NativeTacticalEntry.HostCapture is gone — nothing turns " +
                             "the host's generation result into the message the clients build from.";
            else
            {
                if (randomSeed != null && !Calls(hostCapture, randomSeed))
                    yield return "L103 seed-not-replicated: HostCapture no longer reads GeoSite.RandomSeed. That " +
                                 "field is already mirrored (docs/rail-baseline.txt:706), which is the only " +
                                 "reason it can seed a window both peers execute; anything else is a number one " +
                                 "machine invented.";
                foreach (var banned in WallClockSeeds())
                    if (Calls(hostCapture, banned))
                        yield return "L103 seed-wall-clock: HostCapture reaches " + banned.DeclaringType?.Name +
                                     "." + banned.Name + ". A wall-clock seed reproduces nothing on a second " +
                                     "machine — it is exactly the shape that already makes this bracket useless " +
                                     "under TFTV (UnityEngine.Random.InitState((int)Stopwatch.GetTimestamp()), " +
                                     "TFTVBaseDefenseTactical.cs:1511/1852/2304/2364/2570/2665).";
            }

            // ── (g) the surface is claimed, and is nobody else's ──────────
            if (SurfaceIds.TacEntryParams < 0x80 || SurfaceIds.TacEntryParams > 0x9F)
                yield return "L103 surface-out-of-band: TacEntryParams=0x" +
                             SurfaceIds.TacEntryParams.ToString("X2") + " is outside the tactical band " +
                             "0x80-0x9F (law L62) — a tactical id minted in the geoscape range is silently " +
                             "eaten by the geoscape handler chain.";
            foreach (var f in typeof(SurfaceIds).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.FieldType == typeof(byte) && f.Name != nameof(SurfaceIds.TacEntryParams) &&
                    (byte)f.GetValue(null) == SurfaceIds.TacEntryParams)
                    yield return "L103 surface-collision: TacEntryParams shares its id with SurfaceIds." + f.Name +
                                 " — two senders on one byte mis-route on every peer.";

            var handle = native.GetMethod("HandleInbound", All);
            if (handle == null)
                yield return "L103 surface-unclaimed: NativeTacticalEntry.HandleInbound is gone, so 0x" +
                             SurfaceIds.TacEntryParams.ToString("X2") + " falls through the tactical hook to " +
                             "the geoscape chain — the exact cross-band mis-route law L62 exists to stop.";
        }

        // ─── IL helpers (same technique as L97: real operand-size table, never a byte scan) ───

        private static bool Calls(MethodBase m, MethodBase callee)
        {
            if (m == null || callee == null) return false;
            foreach (var c in Callees(m))
                if (c.MetadataToken == callee.MetadataToken && c.Module == callee.Module) return true;
            return false;
        }

        private static bool Touches(MethodBase m, FieldInfo field)
        {
            if (m == null || field == null) return false;
            foreach (var step in Walk(m))
            {
                if (step.Value.Op.OperandType != OperandType.InlineField) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(step.Key, step.Value.Pos)); } catch { }
                if (f != null && f.MetadataToken == field.MetadataToken && f.Module == field.Module) return true;
            }
            return false;
        }

        /// <summary>Compiler-generated coroutine bodies live on the state machine's MoveNext, not the method
        /// that returns the enumerator — an IL scan of the latter finds one <c>newobj</c> and nothing else.</summary>
        private static MethodBase MoveNextOf(MethodInfo m)
        {
            foreach (var t in m.DeclaringType.GetNestedTypes(All))
                if (t.Name.IndexOf(m.Name, StringComparison.Ordinal) >= 0)
                {
                    var mn = t.GetMethod("MoveNext", All);
                    if (mn != null) return mn;
                }
            return m;
        }

        private static IEnumerable<MethodBase> WallClockSeeds()
        {
            var probes = new[]
            {
                typeof(Environment).GetProperty("TickCount", All)?.GetGetMethod(),
                typeof(DateTime).GetProperty("Now", All)?.GetGetMethod(),
                typeof(DateTime).GetProperty("UtcNow", All)?.GetGetMethod(),
                typeof(System.Diagnostics.Stopwatch).GetMethod("GetTimestamp", All),
                typeof(Guid).GetMethod("NewGuid", All)
            };
            foreach (var p in probes) if (p != null) yield return p;
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
