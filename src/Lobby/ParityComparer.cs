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

            // ── THIS MOD's OWN VERSION, first and by name. It already rode the generic mod loop below as
            // one anonymous "Mod version differs" line among DLC, settings and every other mod — true, but
            // unreadable, and it is the ONE diff that explains all the others (two Multiplayer builds do not
            // agree on the wire, so every later symptom is downstream of it). Named here, first, so the
            // roster badge and the client's join notice both lead with the thing to actually fix.
            if (MultiplayerVersionMismatch(host, client))
                diffs.Add($"Multiplayer mod version differs: host v{MultiplayerVersion(host)} != " +
                          $"client v{MultiplayerVersion(client)}. Both players must run the SAME " +
                          "Multiplayer mod version — update the older install.");

            // ── GAME BUILD: the identity the mod list cannot carry. Both peers may run the same mods on
            // different Phoenix Point builds; join then rides the native save loader (mandate L6) across a
            // build boundary the game itself keys saves on (SavegameMetaData.BuildRevisionNumber), fields
            // and defs move under the diff rail, and the campaign diverges mid-session with nothing said.
            // Compared ONLY when both sides state a version: an unknown means the other peer's Multiplayer
            // build predates this field, which its MOD VERSION already diffs below — so a blank never
            // rejects a peer that is actually fine.
            if (!string.IsNullOrEmpty(host.GameVersion) && !string.IsNullOrEmpty(client.GameVersion) &&
                !string.Equals(host.GameVersion, client.GameVersion, StringComparison.Ordinal))
                diffs.Add($"Phoenix Point build differs: host {host.GameVersion} != client {client.GameVersion}. " +
                          "Co-op needs the same game build on both machines (check for a pending Steam update).");

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
                // Our own version already has its own named line above — say it ONCE. (Missing/extra
                // still falls through generically: "Multiplayer is absent" is a different sentence.)
                else if (!string.Equals(kv.Value, cv, StringComparison.Ordinal) &&
                         !string.Equals(kv.Key, ParityManifest.MultiplayerModId, StringComparison.Ordinal))
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

        /// <summary>This peer's OWN Multiplayer-mod version, pulled out of the manifest's ordinary mod list
        /// (<see cref="ParityManifest.MultiplayerModId"/>). "" = UNKNOWN, which happens when the collector
        /// could not read the mod list at all, or the peer's manifest predates a shape we can read. Unknown
        /// is never a mismatch — see <see cref="MultiplayerVersionMismatch"/>.</summary>
        public static string MultiplayerVersion(ParityManifest m)
        {
            if (m?.Mods == null) return "";
            foreach (var mod in m.Mods)
                if (mod != null && string.Equals(mod.Id, ParityManifest.MultiplayerModId, StringComparison.Ordinal))
                    return mod.Version ?? "";
            return "";
        }

        /// <summary>THE version gate, one decision, read by the host diff AND by the client's join notice.
        /// A mismatch needs BOTH sides to actually state a version: an unknown means we failed to read a
        /// version, not that the other install is wrong, and refusing on it would lock out a peer who is
        /// fine. (A peer whose Multiplayer really is absent is a different, generically-diffed sentence.)</summary>
        public static bool MultiplayerVersionMismatch(ParityManifest host, ParityManifest client)
        {
            var h = MultiplayerVersion(host);
            var c = MultiplayerVersion(client);
            return h.Length > 0 && c.Length > 0 && !string.Equals(h, c, StringComparison.Ordinal);
        }

        /// <summary>The CLIENT-facing join notice ("" when the versions match or either is unknown): shown
        /// natively the moment the host's manifest lands on the accept — before the lobby ever opens, so a
        /// mismatched player learns it at the door instead of after picking a seat. Names BOTH versions and
        /// the action; second-person because only the client is ever shown this string (the host reads the
        /// neutral roster diff from <see cref="Compare"/>). The peer stays CONNECTED — nobody is thrown out
        /// (L84) — it simply cannot READY until the versions match.</summary>
        public static string VersionNoticeForClient(ParityManifest host, ParityManifest client)
            => !MultiplayerVersionMismatch(host, client) ? ""
             : "MULTIPLAYER MOD VERSION MISMATCH\n\n" +
               $"The host is running Multiplayer v{MultiplayerVersion(host)}.\n" +
               $"You are running Multiplayer v{MultiplayerVersion(client)}.\n\n" +
               $"Update your Multiplayer mod to v{MultiplayerVersion(host)} (or ask the host to match yours). " +
               "You stay connected and can chat, but READY stays locked until both versions are the same.";

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
