using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// SHARED reflection boundary for <c>PhoenixPoint.Tactical.Levels.FactionObjectives.FactionObjective</c>
    /// value-stamping — the ONE place that binds the <c>State</c> private setter + the
    /// <c>FactionObjectiveState</c> enum (used by BOTH the TS4 mission-end repaint and the live 0x99
    /// objective mirror) plus the subclass PROGRESS int fields. Resolve-by-member + skip-on-null throughout
    /// (TFTV-tolerant): an unknown subclass simply resolves its OWN members or degrades to a no-op.
    ///
    /// PROGRESS CONTRACT: a subclass's progress (TurnsRemaining, _picked, CurrentIntegrity, …) lives in the
    /// instance int fields DECLARED BELOW <c>FactionObjective</c> (auto-property backing fields included).
    /// Both sides run the same game assembly, so "all subclass-declared int fields, base-most type first,
    /// then by field name (ordinal)" is a deterministic shared order — the host reads that vector, the client
    /// value-stamps it back. Config ints (e.g. <c>SurviveTurns</c>) ride along carrying identical values
    /// (harmless by construction). Guarded by the wire CLASS-NAME check before any write.
    /// </summary>
    internal static class FactionObjectiveReflect
    {
        public const string FactionObjectiveTypeName =
            "PhoenixPoint.Tactical.Levels.FactionObjectives.FactionObjective";

        private static MethodInfo _stateSetter;    // FactionObjective.State (private set)
        private static Type _stateEnumType;        // FactionObjectiveState
        private static readonly Dictionary<Type, FieldInfo[]> _progressFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>Value-stamp <c>State</c> via the private setter (never invokes completion logic).
        /// Resolves against the objective's OWN type (TFTV subclass tolerant); no-op if unresolvable.</summary>
        public static void SetState(object objective, byte state)
        {
            if (objective == null) return;
            if (_stateSetter == null || _stateSetter.DeclaringType == null ||
                !_stateSetter.DeclaringType.IsInstanceOfType(objective))
                _stateSetter = AccessTools.PropertySetter(objective.GetType(), "State");
            if (_stateEnumType == null || !_stateEnumType.IsEnum)
                _stateEnumType = AccessTools.Property(objective.GetType(), "State")?.PropertyType;
            if (_stateSetter == null || _stateEnumType == null) return;
            object enumVal = Enum.ToObject(_stateEnumType, (int)state);
            _stateSetter.Invoke(objective, new[] { enumVal });
        }

        /// <summary>Read <c>State</c> as a byte (FactionObjectiveState underlying int). 0 (InProgress) on any miss.</summary>
        public static byte ReadState(object objective)
        {
            try
            {
                object st = AccessTools.Property(objective.GetType(), "State")?.GetValue(objective, null);
                return st != null ? (byte)Convert.ToInt32(st) : (byte)0;
            }
            catch { return 0; }
        }

        /// <summary>Concrete class name — the wire sanity discriminator (short name; unique across the
        /// vanilla + TFTV objective sets).</summary>
        public static string ClassNameOf(object objective) => objective?.GetType().Name ?? "";

        /// <summary>The <c>Description</c> LocalizedTextBind's <c>LocalizationKey</c> ("" when none) — the
        /// stable shared add-resolution discriminator.</summary>
        public static string DescKeyOf(object objective)
        {
            try
            {
                object bind = AccessTools.Field(objective.GetType(), "Description")?.GetValue(objective);
                if (bind == null) return "";
                return AccessTools.Field(bind.GetType(), "LocalizationKey")?.GetValue(bind) as string ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Read the deterministic subclass progress int vector (see class summary).</summary>
        public static int[] ReadProgress(object objective)
        {
            var fields = ProgressFields(objective?.GetType());
            if (fields == null || fields.Length == 0) return new int[0];
            var values = new int[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                try { values[i] = (int)fields[i].GetValue(objective); } catch { values[i] = 0; }
            }
            return values;
        }

        /// <summary>Value-stamp the progress vector back (up to the shorter of field set / value set —
        /// tolerant of a wire/class drift the CLASS-NAME check let through).</summary>
        public static void WriteProgress(object objective, int[] values)
        {
            if (values == null || values.Length == 0) return;
            var fields = ProgressFields(objective?.GetType());
            if (fields == null) return;
            int n = Math.Min(fields.Length, values.Length);
            for (int i = 0; i < n; i++)
            {
                try { fields[i].SetValue(objective, values[i]); } catch { }
            }
        }

        /// <summary>Instance int fields declared BELOW FactionObjective (auto-prop backing fields included),
        /// base-most declaring type first, then by field name ordinal — a deterministic shared order. Cached
        /// per concrete type. Capped at <see cref="TacticalObjectiveCodec.MaxProgress"/>.</summary>
        private static FieldInfo[] ProgressFields(Type type)
        {
            if (type == null) return null;
            if (_progressFields.TryGetValue(type, out var cached)) return cached;

            var chain = new List<Type>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == FactionObjectiveTypeName || t == typeof(object)) break;
                chain.Add(t);
            }
            chain.Reverse();   // base-most subclass first

            var fields = new List<FieldInfo>();
            foreach (var t in chain)
            {
                var declared = new List<FieldInfo>();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(int)) declared.Add(f);
                declared.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                fields.AddRange(declared);
            }
            if (fields.Count > TacticalObjectiveCodec.MaxProgress)
                fields.RemoveRange(TacticalObjectiveCodec.MaxProgress, fields.Count - TacticalObjectiveCodec.MaxProgress);

            var arr = fields.ToArray();
            _progressFields[type] = arr;
            return arr;
        }
    }
}
