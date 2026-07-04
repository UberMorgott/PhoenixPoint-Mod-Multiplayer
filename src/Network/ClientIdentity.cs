using System;
using System.IO;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// Persistent per-user identity for the local player.
    /// The playerGUID is generated once on first run and reused across sessions; it is the
    /// stable key for permissions/ownership (independent of the per-session transport peerID).
    ///
    /// Storage: &lt;Application.persistentDataPath&gt;/Multiplayer/identity.json — INTERIM location.
    /// NOTE (OPEN SDK Q): the PP per-user config dir that survives a mod update is unconfirmed
    /// (docs/specs/03-open-questions-sdk.md → "Persistent Config Location"). persistentDataPath is
    /// the grounded always-available Unity default; revisit before release.
    /// </summary>
    public static class ClientIdentity
    {
        private const string DirName = "Multiplayer";
        private const string FileName = "identity.json";
        private const string GuidPrefix = "\"playerGUID\":\"";

        private static Guid _playerGuid = Guid.Empty;
        private static bool _loaded;

        /// <summary>Local persistent playerGUID; generated + persisted on first access.</summary>
        public static Guid PlayerGuid
        {
            get
            {
                if (!_loaded)
                    Load();
                return _playerGuid;
            }
        }

        private static string Directory => Path.Combine(Application.persistentDataPath, DirName);
        private static string FilePath => Path.Combine(Directory, FileName);

        private static void Load()
        {
            _loaded = true;

            // Local 2-instance test seam: a distinct GUID can be injected via the
            // MULTIPLAYER_IDENTITY env var so a 2nd instance on the same machine (which
            // shares persistentDataPath/identity.json) gets its own player identity.
            // The override is process-scoped only; it is never persisted to identity.json.
            try
            {
                var envOverride = System.Environment.GetEnvironmentVariable("MULTIPLAYER_IDENTITY");
                if (!string.IsNullOrEmpty(envOverride) &&
                    Guid.TryParse(envOverride, out var fromEnv) && fromEnv != Guid.Empty)
                {
                    _playerGuid = fromEnv;
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer] ClientIdentity env override failed: " + e.Message);
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    if (TryParseGuid(json, out var parsed) && parsed != Guid.Empty)
                    {
                        _playerGuid = parsed;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer] ClientIdentity load failed: " + e.Message);
            }

            // First run (or unreadable/empty file): generate once and persist.
            _playerGuid = Guid.NewGuid();
            Save();
        }

        private static void Save()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory))
                    System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(FilePath, "{ " + GuidPrefix + _playerGuid + "\" }");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer] ClientIdentity save failed: " + e.Message);
            }
        }

        // Minimal hand parse (avoid taking a JSON dependency for a single field).
        private static bool TryParseGuid(string json, out Guid guid)
        {
            guid = Guid.Empty;
            if (string.IsNullOrEmpty(json)) return false;

            var i = json.IndexOf(GuidPrefix, StringComparison.Ordinal);
            if (i < 0) return false;
            i += GuidPrefix.Length;
            var end = json.IndexOf('"', i);
            if (end < 0) return false;

            return Guid.TryParse(json.Substring(i, end - i), out guid);
        }
    }
}
