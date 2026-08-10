using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Events;

namespace Multiplayer.Network.Sync
{
    internal static class EventRewardTransaction
    {
        [ThreadStatic] private static Capture _active;
        private sealed class Capture { internal OccurrenceId Occurrence; internal DurableInboxStore Store; }
        private sealed class Scope : IDisposable { public void Dispose() { _active = null; } }

        internal static IDisposable Begin(OccurrenceId occurrence, DurableInboxStore store)
        {
            if (_active != null) throw new InvalidOperationException("nested durable event reward capture");
            _active = new Capture { Occurrence = occurrence, Store = store };
            return new Scope();
        }

        internal static IReadOnlyList<CanonicalRewardItemId> Canonicalize(OccurrenceId occurrence,
            GeoFactionReward reward)
        {
            if (reward == null) throw new InvalidOperationException("native event generated no reward");
            var facts = new List<string>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Append("reward", reward, facts, seen, 0);
            string subject = occurrence.SubjectIds.First();
            return facts.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => new CanonicalRewardItemId(occurrence, subject, x)).ToArray();
        }

        internal static void CaptureGenerated(GeoFactionReward reward)
        {
            var active = _active; if (active == null) return;
            var facts = Canonicalize(active.Occurrence, reward);
            byte[] payload = null;
            if (reward?.ApplyResult != null)
                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms, Encoding.UTF8))
                { MissionOutcomeMirror.Encode(writer, reward.ApplyResult); payload = ms.ToArray(); }
            if (!active.Store.ReplacePendingDecisionRewards(active.Occurrence, facts, payload))
                throw new InvalidOperationException("generated reward could not replace pending canonical facts");
        }

        private static void Append(string path, object value, List<string> facts, HashSet<object> seen, int depth)
        {
            if (depth > 8) throw new InvalidOperationException("reward graph exceeds canonical depth at " + path);
            if (value == null) { facts.Add(path + "=null"); return; }
            var type = value.GetType();
            if (value is string || type.IsPrimitive || type.IsEnum || value is decimal)
            { facts.Add(path + "=" + Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is BaseDef def) { facts.Add(path + "=D#" + def.Guid); return; }
            string stable = MissionOutcomeMirror.Ref(value);
            if (!string.IsNullOrEmpty(stable)) { facts.Add(path + "=" + stable); return; }
            if (!type.IsValueType && !seen.Add(value))
            { facts.Add(path + "=@shared"); return; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                Append(path + ".Key", type.GetProperty("Key").GetValue(value, null), facts, seen, depth + 1);
                Append(path + ".Value", type.GetProperty("Value").GetValue(value, null), facts, seen, depth + 1);
                return;
            }
            if (value is IEnumerable sequence)
            {
                var elements = new List<string>(); int index = 0;
                foreach (var element in sequence)
                {
                    var nested = new List<string>(); Append("v", element, nested, seen, depth + 1);
                    elements.Add(string.Join(";", nested)); if (++index > 4096) throw new InvalidOperationException("reward collection too large");
                }
                elements.Sort(StringComparer.Ordinal);
                for (int i = 0; i < elements.Count; i++) facts.Add(path + "[" + i + "]=" + elements[i]);
                return;
            }
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => !typeof(Delegate).IsAssignableFrom(x.FieldType) &&
                    (x.IsPublic || HasSerializeMember(x)))
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 &&
                    !typeof(Delegate).IsAssignableFrom(x.PropertyType) &&
                    (HasSerializeMember(x) || x.PropertyType.IsPrimitive || x.PropertyType.IsEnum || x.PropertyType == typeof(string)))
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            if (fields.Length == 0 && properties.Length == 0)
                throw new InvalidOperationException("unsupported unaddressable reward member " + type.FullName + " at " + path);
            foreach (var field in fields) Append(path + "." + field.Name, field.GetValue(value), facts, seen, depth + 1);
            foreach (var property in properties) Append(path + "." + property.Name, property.GetValue(value, null), facts, seen, depth + 1);
        }

        private static bool HasSerializeMember(MemberInfo member) => member.GetCustomAttributes(false)
            .Any(x => string.Equals(x.GetType().Name, "SerializeMemberAttribute", StringComparison.Ordinal));

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }

    [HarmonyPatch(typeof(GeoEventChoiceOutcome), nameof(GeoEventChoiceOutcome.GenerateFactionReward))]
    internal static class DurableGeneratedRewardCapturePatch
    {
        private static void Postfix(GeoFactionReward __result) => EventRewardTransaction.CaptureGenerated(__result);
    }

    [HarmonyPatch(typeof(GeoFactionReward), nameof(GeoFactionReward.Apply))]
    internal static class DurableAppliedRewardCapturePatch
    {
        private static void Postfix(GeoFactionReward __instance) =>
            EventRewardTransaction.CaptureGenerated(__instance);
    }
}
