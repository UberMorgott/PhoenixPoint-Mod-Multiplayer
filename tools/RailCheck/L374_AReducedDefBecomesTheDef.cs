using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Core;
using Base.Defs;
using PhoenixPoint.Geoscape.Entities.Research;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L374 — a reduced DTO def id is converted back to the rich live def before the write.</summary>
    internal static class L374_AReducedDefBecomesTheDef
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private static class FakeSeam
        {
            internal static void RawWriter(object entity, object value, Action<object, object> set) => set(entity, value);
            internal static object Converted(Type type, object value) => RailMeta.LiveFromReduced(type, value);
        }

        internal static IEnumerable<string> Check()
        {
            var convert = typeof(RailMeta).GetMethod("LiveFromReduced", All);
            var apply = typeof(GenericApplier).GetMethod("ApplyEntry", All);
            var setValue = typeof(RailField).GetMethod("SetValue", All);
            if (convert == null || apply == null || setValue == null)
            {
                yield return "L374 premise-changed: RailMeta.LiveFromReduced, GenericApplier.ApplyEntry or " +
                             "RailField.SetValue no longer resolves. The inverse of the DTO reduction and its " +
                             "write-site are therefore unchecked, not satisfied.";
                yield break;
            }

            // Executed negative and pass-through outcomes: an unresolved reduction is never the raw string,
            // and a value already rich enough is byte-for-byte the same reference.
            var rich = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ResearchDef));
            object pass = null, miss = new object(); string threw = null;
            try
            {
                pass = convert.Invoke(null, new[] { typeof(ResearchDef), rich });
                miss = convert.Invoke(null, new object[] { typeof(ResearchDef), "L374-def-that-does-not-exist" });
            }
            catch (Exception ex) { threw = (ex.InnerException ?? ex).GetType().Name; }
            if (threw != null)
                yield return "L374 reduced-coercion-threw: LiveFromReduced threw " + threw +
                             " for an unknown ResearchDef.Id; an unknown id must keep the live value, never " +
                             "throw out of the per-field apply.";
            else
            {
                if (!ReferenceEquals(pass, rich))
                    yield return "L374 rich-def-did-not-pass-through: a value already of ResearchDef was " +
                                 "replaced. The ordinary live-table path already carries the rich def and must " +
                                 "not enter reduced-id lookup.";
                if (miss != null)
                    yield return "L374 unknown-id-became-a-value: an unknown ResearchDef.Id answered " +
                                 miss.GetType().Name + " instead of null; the caller must keep its current live " +
                                 "value, never write the raw string.";
            }

            // When RailCheck is hosted with a game component graph, execute the original live outcome too:
            // the string stored by GeoHavenInstanceData must resolve to the very same repository object.
            // The normal console host deliberately has no DefRepository, so this arm is conditional rather
            // than pretending an uninitialised ResearchDef is a real graph.
            DefRepository repo = null;
            try { repo = GameUtl.GameComponent<DefRepository>(); } catch { }
            if (repo != null)
            {
                var idField = typeof(ResearchDef).GetField("Id", All);
                var sample = repo.GetAllDefs(typeof(ResearchDef), inherited: true)
                                 .OfType<ResearchDef>()
                                 .FirstOrDefault(d => idField?.GetValue(d) is string id && id.Length != 0);
                if (sample == null || idField == null)
                    yield return "L374 live-def-premise-changed: a live DefRepository exists but exposes no " +
                                 "ResearchDef with a readable non-empty Id, so the reported haven reduction " +
                                 "cannot be exercised against the real graph.";
                else
                {
                    object got = null; string liveThrew = null;
                    try { got = convert.Invoke(null, new[] { typeof(ResearchDef), idField.GetValue(sample) }); }
                    catch (Exception ex) { liveThrew = (ex.InnerException ?? ex).GetType().Name; }
                    if (liveThrew != null || !ReferenceEquals(got, sample))
                        yield return "L374 reduced-id-did-not-become-def: ResearchDef.Id from the live repository " +
                                     (liveThrew == null ? "did not answer THAT def instance" : "threw " + liveThrew) +
                                     ". The DTO-twin write would still receive a string instead of ResearchDef.";
                }
            }

            // The real lookup must remain generic over BaseDef and must ask the repository for the graph.
            var defLookup = typeof(RailMeta).GetMethod("DefByReducedId", All);
            var lookupCalls = defLookup == null ? new List<MethodBase>() : Program.CalleeSequence(defLookup);
            if (defLookup == null || !lookupCalls.Any(m => m.DeclaringType == typeof(DefRepository)))
                yield return "L374 def-graph-unreached: DefByReducedId no longer reaches DefRepository. A " +
                             "GeoHavenInstanceData string names ResearchDef.Id, not a constructible value; " +
                             "without the real def graph it can never become THAT def instance.";

            // Outcome at the write site, not merely a utility that happens to work in isolation.
            var calls = Program.CalleeSequence(apply);
            int conversionAt = calls.FindIndex(m => m == convert);
            int writeAt = calls.FindIndex(m => m == setValue);
            if (conversionAt < 0 || writeAt < 0 || conversionAt > writeAt)
                yield return "L374 raw-reduced-value-reaches-write: GenericApplier.ApplyEntry does not reach " +
                             "LiveFromReduced before RailField.SetValue. A correct converter sitting unused " +
                             "does not stop System.String from being written into ResearchDef.";

            // POSITIVE CONTROL: the same discriminator must reject the historical raw writer and accept a
            // seam which actually invokes the converter.
            var raw = typeof(FakeSeam).GetMethod("RawWriter", All);
            var converted = typeof(FakeSeam).GetMethod("Converted", All);
            if (Program.CalleeSequence(raw).Any(m => m == convert) ||
                !Program.CalleeSequence(converted).Any(m => m == convert))
                yield return "L374 positive-control-broken: the call probe cannot distinguish a raw write " +
                             "from a path that invokes LiveFromReduced, so the write-half arm is vacuous.";
        }
    }
}
