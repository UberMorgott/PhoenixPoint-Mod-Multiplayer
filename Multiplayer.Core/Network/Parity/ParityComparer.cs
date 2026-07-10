using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Network.Parity
{
    /// <summary>
    /// FIX-4 — pure host-authoritative parity comparison. <see cref="Compare"/> returns the list of
    /// human-readable diffs between the host manifest (reference) and a client manifest; an empty list
    /// means full parity. SOFT GATE: a mismatched client still JOINS the lobby, but gets a roster
    /// warning badge and cannot READY (<see cref="ReadyAllowed"/>, enforced host-side too) until the
    /// diffs clear. Diff rules:
    ///   • DLC: only "missing on client" diffs — the host save uses it and the client lacks it, so the
    ///     transferred save fails to deserialize. Extra DLC on the client is harmless (it just owns more).
    ///   • Mods: missing / extra / version-differs all diff — a def-patching mod divergence desyncs.
    ///   • Settings: for a mod present on BOTH sides, any per-key value difference diffs (mods read
    ///     config at load time, so different settings = different behaviour/defs). Host settings are
    ///     AUTO-APPLIED on the client at join (ParityConfigSync); only unappliable keys keep diffing.
    /// </summary>
    public static class ParityComparer
    {
        public static List<string> Compare(ParityManifest host, ParityManifest client)
        {
            var diffs = new List<string>();
            if (host == null || client == null)
            {
                diffs.Add("Parity manifest missing (the other player did not send one — incompatible Multiplayer mod version?).");
                return diffs;
            }

            // ── DLC: block only when the host has a DLC the client does not. ──
            var clientDlc = new HashSet<string>(client.Dlc ?? new List<string>(), StringComparer.Ordinal);
            foreach (var d in host.Dlc ?? new List<string>())
                if (!clientDlc.Contains(d))
                    diffs.Add($"DLC missing on client: {d}");

            // ── Mods: missing / extra / version mismatch all block. ──
            var hostMods = ToModMap(host.Mods);
            var clientMods = ToModMap(client.Mods);
            foreach (var kv in hostMods)
            {
                if (!clientMods.TryGetValue(kv.Key, out var cv))
                    diffs.Add($"Mod missing on client: {kv.Key} v{kv.Value}");
                else if (!string.Equals(kv.Value, cv, StringComparison.Ordinal))
                    diffs.Add($"Mod version differs: {kv.Key} host v{kv.Value} != client v{cv}");
            }
            foreach (var kv in clientMods)
                if (!hostMods.ContainsKey(kv.Key))
                    diffs.Add($"Extra mod on client: {kv.Key} v{kv.Value}");

            // ── Settings: only for mods present on BOTH sides (mod-presence handled above). ──
            var clientSettings = (client.Settings ?? new List<ModSettings>())
                .GroupBy(s => s.ModId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var hs in host.Settings ?? new List<ModSettings>())
            {
                if (!clientSettings.TryGetValue(hs.ModId, out var cs)) continue;
                if (hs.Hash == cs.Hash) continue; // fast equal
                foreach (var line in DiffEntries(hs.ModId, hs.Entries, cs.Entries))
                    diffs.Add(line);
            }

            return diffs;
        }

        /// <summary>Join the diffs into a single message-box-ready block ("" when there are none).</summary>
        public static string Format(List<string> diffs)
            => diffs == null || diffs.Count == 0 ? "" : string.Join("\n", diffs);

        /// <summary>
        /// Parity soft-gate READY decision (shared by the host's authoritative gate and the client's
        /// button lock): a peer may ready up only when its stored diff text is empty.
        /// </summary>
        public static bool ReadyAllowed(string parityDiffs) => string.IsNullOrEmpty(parityDiffs);

        private static Dictionary<string, string> ToModMap(List<ModRef> mods)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mods != null)
                foreach (var m in mods)
                    if (m != null && !string.IsNullOrEmpty(m.Id))
                        d[m.Id] = m.Version ?? "";
            return d;
        }

        private static IEnumerable<string> DiffEntries(string modId, List<string> hostEntries, List<string> clientEntries)
        {
            var h = ToKvMap(hostEntries);
            var c = ToKvMap(clientEntries);
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in h.Keys) keys.Add(k);
            foreach (var k in c.Keys) keys.Add(k);
            foreach (var k in keys)
            {
                h.TryGetValue(k, out var hv);
                c.TryGetValue(k, out var cv);
                if (!string.Equals(hv, cv, StringComparison.Ordinal))
                    yield return $"Setting {modId}.{k}: host={hv ?? "(absent)"} client={cv ?? "(absent)"}";
            }
        }

        private static Dictionary<string, string> ToKvMap(List<string> entries)
            => ParityAutoApply.ToMap(entries); // one splitter, shared with the auto-apply
    }
}
