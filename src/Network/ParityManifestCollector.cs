using System;
using System.Collections.Generic;
using System.Globalization;
using Base.Core;
using Base.Platforms;
using Multiplayer.Network.Parity;
using PhoenixPoint.Common.Game;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// FIX-4 — build the co-op parity manifest from the LIVE game: the DLC the user is entitled to, the
    /// ENABLED mods (id + version), and each enabled mod's SETTINGS. The pure comparison/hash logic lives
    /// in Multiplayer.Core (<see cref="ParityManifest"/> / <see cref="ParityComparer"/>); this collector
    /// only reads the game and feeds plain primitives in, so the Core stays Unity-free + unit-testable.
    ///
    /// LIMITATION (documented in docs/WAN-COOP.md): mod settings are read generically via ModConfig's
    /// public config fields and stringified without a JSON dependency, so scalar settings
    /// (bool/int/float/enum/string) diff exactly, but complex/array config values are compared by their
    /// stable type-string only (equal values still hash equal; their contents are not shown in a diff).
    /// Settings are checked, NOT auto-synced — mods read config at load time, so distribution is a later
    /// increment.
    /// </summary>
    public static class ParityManifestCollector
    {
        public static ParityManifest Collect()
        {
            var dlc = new List<string>();
            var mods = new List<(string id, string version)>();
            var settings = new List<(string modId, IEnumerable<(string key, string value)> entries)>();

            try
            {
                var game = GameUtl.GameComponent<PhoenixGame>();

                // Owned DLC (independent of any loaded save): enumerate all EntitlementDefs and keep those
                // the platform says the user is entitled to. The stable def name is the wire identifier.
                try
                {
                    var ent = GameUtl.GameComponent<PlatformComponent>()?.Platform?.GetPlatformEntitlement();
                    if (ent != null)
                        foreach (var e in ent.GetAllEntitlements())
                            if (e != null && ent.IsUserEntitledFor(e))
                                dlc.Add(e.name);
                }
                catch (Exception e) { Debug.LogWarning("[Multiplayer] parity: DLC enumeration failed: " + e.Message); }

                // Enabled mods + their settings.
                var mm = game?.ModManager;
                if (mm != null)
                {
                    foreach (var mod in mm.Mods)
                    {
                        if (mod == null || !mod.Enabled) continue;
                        var version = mod.MetaData?.Version?.ToString() ?? "0.0";
                        mods.Add((mod.ID, version));

                        if (mod.HasConfig && mod.Instance?.Config != null)
                        {
                            var kv = new List<(string key, string value)>();
                            try
                            {
                                foreach (var f in mod.Instance.Config.GetConfigFields())
                                {
                                    if (f == null || !f.CanRead) continue;
                                    string val;
                                    try { val = SerializeValue(f.GetValue()); }
                                    catch { val = "<unreadable>"; }
                                    kv.Add((f.ID, val));
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"[Multiplayer] parity: settings of '{mod.ID}' failed: {e.Message}");
                            }
                            settings.Add((mod.ID, kv));
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] parity manifest collect failed: " + e.Message); }

            return ParityManifest.Build(dlc, mods, settings);
        }

        // Deterministic, dependency-free value → string (no Newtonsoft ref in the mod). Scalars stringify
        // identically on host + client; floats use round-trip invariant formatting so 0.30 == 0.3. Complex
        // values fall through to Convert.ToString (typically the type name — stable across peers).
        private static string SerializeValue(object v)
        {
            if (v == null) return "null";
            if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? v.GetType().FullName;
        }
    }
}
