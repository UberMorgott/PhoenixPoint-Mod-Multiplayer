using System;
using System.Collections.Generic;
using System.Globalization;

namespace Multiplayer.Network.Parity
{
    /// <summary>
    /// Pure half of the host-settings auto-apply (the game-facing reflection apply lives in
    /// src/Network/ParityConfigSync). Decides which manifest values are generically appliable and
    /// parses the deterministic string form (ParityManifestCollector.SerializeValue: invariant
    /// culture, floats "R") back into a typed value. Complex/array config values are NOT appliable —
    /// they keep diffing, so the roster badge + ready lock persist and the tooltip names the key.
    /// </summary>
    public static class ParityAutoApply
    {
        /// <summary>Generically appliable config-field types: primitives, enums, string, decimal.</summary>
        public static bool IsScalar(Type t)
            => t != null && (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal));

        /// <summary>Parse a manifest value string back to <paramref name="t"/>; false = not appliable.</summary>
        public static bool TryParseValue(Type t, string s, out object value)
        {
            value = null;
            if (!IsScalar(t) || s == null) return false;
            try
            {
                if (t == typeof(string)) { value = s; return true; }
                if (t.IsEnum) { value = Enum.Parse(t, s); return true; }
                value = Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Split sorted "key=value" manifest entries into a key→value map (the ONE splitter,
        /// shared with ParityComparer's diff formatting).</summary>
        public static Dictionary<string, string> ToMap(List<string> entries)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e == null) continue;
                    var i = e.IndexOf('=');
                    if (i < 0) d[e] = "";
                    else d[e.Substring(0, i)] = e.Substring(i + 1);
                }
            return d;
        }
    }
}
