using System;
using System.IO;
using System.Runtime.CompilerServices;
using Multiplayer.Util;
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
    ///
    /// PER-INSTANCE FILE: two same-machine instances (the local 2-instance co-op test rig) share
    /// persistentDataPath, so a single identity.json would give BOTH the same guid → the host assigns
    /// the joining client its own slot 0 and per-player ownership collapses. To avoid that WITHOUT the
    /// manual <c>MULTIPLAYER_IDENTITY</c> override, the file is keyed off the authoritative same-machine
    /// index <see cref="MultiplayerLog.InstanceIndex"/>: identity.json for instance 1, identity-2.json
    /// for instance 2, … (reusing the ONE canonical "-N before extension" suffixer the log file uses).
    /// A real cross-machine peer is always instance 1 → identity.json on its own machine — unchanged.
    /// </summary>
    public static class ClientIdentity
    {
        private const string DirName = "Multiplayer";
        private const string FileName = "identity.json";
        private const string GuidPrefix = "\"playerGUID\":\"";
        private const string NicknameKey = "Multiplayer_Nickname";

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

        /// <summary>
        /// Local player's chosen lobby display name, persisted across restarts via Unity PlayerPrefs
        /// (one shared key — multiple same-machine instances intentionally share the stored nick).
        /// Returns null when never set, so callers apply their own default (host "Host",
        /// client SystemInfo.deviceName). Wrapped like the file's IO — never throws to the caller.
        /// </summary>
        public static string LocalNickname
        {
            get
            {
                try
                {
                    var v = ReadNicknamePref();
                    return string.IsNullOrEmpty(v) ? null : v;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Multiplayer] ClientIdentity nickname load failed: " + e.Message);
                    return null;
                }
            }
            set
            {
                try
                {
                    WriteNicknamePref(value ?? "");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Multiplayer] ClientIdentity nickname save failed: " + e.Message);
                }
            }
        }

        // PlayerPrefs.* are direct InternalCall externs. A method that references one fails to JIT in a
        // headless harness (real UnityEngine.CoreModule.dll, no Unity native runtime) with SecurityException
        // "ECall methods must be packed in a system module" — thrown when JITing the *referencing* method,
        // BEFORE any try/catch inside it runs. Isolating each ECall in its own NoInlining method keeps that
        // JIT failure out of LocalNickname's body, so the getter/setter try/catch above actually catches it
        // (surfaced at the call site) and the "never throws to the caller" contract holds. NoInlining is
        // required: inlining would fold the ECall reference back into the accessor and reintroduce the fault.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadNicknamePref() => PlayerPrefs.GetString(NicknameKey, "");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteNicknamePref(string value)
        {
            PlayerPrefs.SetString(NicknameKey, value);
            PlayerPrefs.Save();
        }

        private static string Directory => Path.Combine(Application.persistentDataPath, DirName);

        // Per-instance identity file. instance 1 → identity.json (shared/primary); instance N>1 →
        // identity-N.json, keyed off MultiplayerLog.InstanceIndex (the authoritative same-machine index,
        // resolved in MultiplayerLog.Init). Reuses TftvLogRedirect — the ONE canonical "-N before
        // extension" suffixer the per-instance log file uses — so identity and log share identical
        // instance-suffix semantics. Load() calls MultiplayerLog.Init() first, so the index is resolved
        // before this getter is read (order-safe even if a caller touches PlayerGuid before mod startup).
        private static string FilePath
        {
            get
            {
                var basePath = Path.Combine(Directory, FileName);
                var index = MultiplayerLog.InstanceIndex;
                return TftvLogRedirect.ResolveRedirectedPath(basePath, index > 1, index);
            }
        }

        private static void Load()
        {
            _loaded = true;

            // Order-safe: guarantees MultiplayerLog.InstanceIndex is resolved (via the log-lock fallback
            // loop) BEFORE FilePath picks the per-instance identity file. Idempotent — a no-op in normal
            // startup where MultiplayerMain.OnModEnabled already ran it as its first step.
            MultiplayerLog.Init();

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
                    Debug.Log($"[Multiplayer] ClientIdentity: using MULTIPLAYER_IDENTITY override {_playerGuid} " +
                              $"(instance {MultiplayerLog.InstanceIndex}).");
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
                        Debug.Log($"[Multiplayer] ClientIdentity: loaded {_playerGuid} from " +
                                  $"{Path.GetFileName(FilePath)} (instance {MultiplayerLog.InstanceIndex}).");
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
            Debug.Log($"[Multiplayer] ClientIdentity: generated new {_playerGuid} → " +
                      $"{Path.GetFileName(FilePath)} (instance {MultiplayerLog.InstanceIndex}).");
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
