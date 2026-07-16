using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Multiplayer.Network.Parity
{
    /// <summary>
    /// FIX-4 — pure, Unity-free co-op parity snapshot exchanged at join: the set of enabled/owned DLC,
    /// the enabled mods (id + version), and a per-mod SETTINGS fingerprint (sorted key=value lines + a
    /// crc32 hash of them). The game-facing collector (src/Network/ParityManifestCollector) feeds plain
    /// primitives into <see cref="Build"/>; comparison/formatting lives in <see cref="ParityComparer"/>.
    /// Everything is deterministically sorted so host and client that are genuinely identical produce
    /// byte-identical manifests (and equal hashes).
    /// </summary>
    public sealed class ParityManifest
    {
        /// <summary>Sorted DLC def-names the peer owns/has enabled.</summary>
        public List<string> Dlc = new List<string>();

        /// <summary>Enabled mods, sorted by Id.</summary>
        public List<ModRef> Mods = new List<ModRef>();

        /// <summary>Per-enabled-mod settings fingerprint, sorted by ModId.</summary>
        public List<ModSettings> Settings = new List<ModSettings>();

        public static ParityManifest Build(
            IEnumerable<string> dlc,
            IEnumerable<(string id, string version)> mods,
            IEnumerable<(string modId, IEnumerable<(string key, string value)> entries)> settings)
        {
            var m = new ParityManifest();

            if (dlc != null)
                m.Dlc = dlc.Where(d => !string.IsNullOrEmpty(d))
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(d => d, StringComparer.Ordinal)
                           .ToList();

            if (mods != null)
                m.Mods = mods.Where(x => !string.IsNullOrEmpty(x.id))
                             .GroupBy(x => x.id, StringComparer.Ordinal)
                             .Select(g => g.First())
                             .OrderBy(x => x.id, StringComparer.Ordinal)
                             .Select(x => new ModRef { Id = x.id, Version = x.version ?? "" })
                             .ToList();

            if (settings != null)
                foreach (var s in settings.Where(x => !string.IsNullOrEmpty(x.modId))
                                          .OrderBy(x => x.modId, StringComparer.Ordinal))
                {
                    var entries = (s.entries ?? Enumerable.Empty<(string key, string value)>())
                        .Where(e => e.key != null)
                        .OrderBy(e => e.key, StringComparer.Ordinal)
                        .Select(e => e.key + "=" + (e.value ?? ""))
                        .ToList();
                    m.Settings.Add(new ModSettings
                    {
                        ModId = s.modId,
                        Entries = entries,
                        Hash = HashEntries(entries)
                    });
                }

            return m;
        }

        /// <summary>crc32 over the canonical "key=value\n…" form (stable, deterministic).</summary>
        public static uint HashEntries(IEnumerable<string> sortedEntries)
        {
            var canonical = string.Join("\n", sortedEntries ?? Enumerable.Empty<string>());
            return Multiplayer.Util.Crc32.Compute(Encoding.UTF8.GetBytes(canonical));
        }
    }

    public sealed class ModRef
    {
        public string Id;
        public string Version;
    }

    public sealed class ModSettings
    {
        public string ModId;
        /// <summary>Sorted "key=value" lines (value is a deterministic string form of the config value).</summary>
        public List<string> Entries = new List<string>();
        /// <summary>crc32 of the joined entries — fast settings equality check before diffing.</summary>
        public uint Hash;
    }
}
