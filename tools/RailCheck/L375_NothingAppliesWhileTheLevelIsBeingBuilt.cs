using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Modding;

namespace RailCheck
{
    /// <summary>L375 — batches wait for this level's OnGeoscapeStart latch, not merely its object.</summary>
    internal static class L375_NothingAppliesWhileTheLevelIsBeingBuilt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private static class FakeSeam
        {
            internal static object ObjectExists(object current) => current;
            internal static object ThisInstanceStarted(object current, object started) =>
                current != null && GenericApplier.SameLevelInstance(current, started) ? current : null;
        }

        internal static IEnumerable<string> Check()
        {
            var started = typeof(GenericApplier).GetMethod("StartedGeoLevel", All);
            var mark = typeof(GenericApplier).GetMethod("MarkGeoscapeStarted", All);
            var delta = typeof(GenericApplier).GetMethod("ApplyDelta", All);
            var structural = typeof(GenericApplier).GetMethod("ApplyStructural", All);
            var closer = typeof(GenericApplier).GetMethod("ClientMissedBatchTick", All);
            var latch = typeof(GenericApplier).GetField("_startedLevel", All);
            var same = typeof(GenericApplier).GetMethod("SameLevelInstance", All);
            var patch = typeof(GenericApplier).Assembly.GetType("Multiplayer.Network.Sync.GeoscapeStartedPatch");
            var postfix = patch?.GetMethod("Postfix", All);
            if (started == null || mark == null || delta == null || structural == null || closer == null ||
                latch == null || same == null || postfix == null)
            {
                yield return "L375 premise-changed: the StartedGeoLevel latch family, both batch entry points, " +
                             "ClientMissedBatchTick or GeoscapeStartedPatch.Postfix no longer resolves. The " +
                             "mid-LevelCrt hole is unchecked, not satisfied.";
                yield break;
            }

            // Executed pre-start outcome in the headless harness: no current level must never be reported ready.
            object before = new object(); string threw = null;
            try { before = started.Invoke(null, null); }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name; }
            if (threw != null || before != null)
                yield return "L375 no-level-reported-started: StartedGeoLevel " +
                             (threw == null ? "answered a level" : "threw " + threw) +
                             " before a geoscape exists; batches can then enter a graph that is still absent.";

            // The answer must compare the current instance with the latched instance. A boolean latch would
            // incorrectly bless the next geoscape after teardown.
            var fields = Program.FieldRefs(started).ToList();
            if (!fields.Contains(latch) || !Program.CalleeSequence(started).Any(m => m == same))
                yield return "L375 latch-is-not-this-level: StartedGeoLevel does not read _startedLevel and " +
                             "compare it by reference with the current GeoLevelController. A stale boolean " +
                             "from the previous campaign would allow the next level while it is still building.";

            foreach (var entry in new[] { delta, structural, closer })
                if (!Program.CalleeSequence(entry).Any(m => m == started))
                    yield return "L375 gate-disagrees: " + entry.Name + " no longer consults StartedGeoLevel. " +
                                 "ApplyDelta, ApplyStructural and the resend closer must use the same predicate " +
                                 "or the recovery packet lands back inside the hole it answers.";

            if (!Program.CalleeSequence(postfix).Any(m => m == mark) ||
                !Program.FieldRefs(mark, System.Reflection.Emit.OpCodes.Stsfld).Contains(latch))
                yield return "L375 start-callback-does-not-latch: the OnGeoscapeStart postfix no longer reaches " +
                             "MarkGeoscapeStarted or that method no longer writes _startedLevel.";

            var harmony = postfix.GetCustomAttributes(typeof(HarmonyPostfix), false).Any();
            var container = patch.GetCustomAttributes(typeof(HarmonyPatch), false)
                                 .Cast<HarmonyPatch>().Any(a => a.info.methodName == "OnGeoscapeStart");
            if (!harmony || !container)
                yield return "L375 readiness-seam-moved: GeoscapeStartedPatch is not a postfix on " +
                             "ModManager.OnGeoscapeStart. Prefix would latch before other mods finish their " +
                             "start work; another callback is only a guess at LevelCrt completion.";

            // Premise: the native coroutine must still contain OnGeoscapeStart and only then enter its
            // GameOverCheck loop. Iterator bodies live in the generated MoveNext method.
            var controller = typeof(GeoLevelController);
            var levelCrt = AccessTools.Method(controller, "LevelCrt");
            var bodies = new List<MethodBase>();
            if (levelCrt != null) bodies.Add(levelCrt);
            bodies.AddRange(controller.GetNestedTypes(All).Where(t => t.Name.IndexOf("LevelCrt", StringComparison.Ordinal) >= 0)
                                      .Select(t => (MethodBase)t.GetMethod("MoveNext", All)).Where(m => m != null));
            var sequence = bodies.SelectMany(Program.CalleeSequence).ToList();
            int onStart = sequence.FindIndex(m => m.DeclaringType == typeof(ModManager) && m.Name == "OnGeoscapeStart");
            int gameOver = onStart < 0 ? -1 :
                sequence.FindIndex(onStart + 1, m => m.Name.IndexOf("GameOverCheck", StringComparison.Ordinal) >= 0);
            if (onStart < 0 || gameOver < 0)
                yield return "L375 native-premise-changed: GeoLevelController.LevelCrt no longer calls " +
                             "ModManager.OnGeoscapeStart immediately before entering its GameOverCheck phase. " +
                             "The callback may no longer mean that faction Research/ManufactureQueue graphs " +
                             "are completely built; re-ground the readiness seam before accepting batches.";

            // POSITIVE CONTROL over the precise discriminator: mere object existence must fail while an
            // instance-aware latch must contain ReferenceEquals.
            var weak = typeof(FakeSeam).GetMethod("ObjectExists", All);
            var strong = typeof(FakeSeam).GetMethod("ThisInstanceStarted", All);
            var a = new object();
            if (FakeSeam.ThisInstanceStarted(a, a) != a || FakeSeam.ThisInstanceStarted(a, new object()) != null ||
                Program.CalleeSequence(weak).Any(m => m == same) ||
                !Program.CalleeSequence(strong).Any(m => m == same))
                yield return "L375 positive-control-broken: the predicate probe cannot distinguish 'the level " +
                             "object exists' from 'this exact level was latched started', so it would stay green " +
                             "over the original 423-drop regression.";
        }

    }
}
