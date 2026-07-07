using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Thread-scoped guard set ONLY while the client replays a host-mirrored geoscape-log entry
    /// (<see cref="GeoscapeLogMirror.ApplyMirroredEntry"/>). The client suppress-prefix on
    /// <c>GeoscapeLog.AddEntry</c> allows an entry through iff THIS scope is active — a plain
    /// <see cref="SyncApplyScope"/> is too broad: a state-channel apply (e.g. mirrored attack-schedule) also runs
    /// under it and would let the native toast slip through and DOUBLE with the host mirror.
    /// </summary>
    internal static class GeoLogMirrorScope
    {
        [ThreadStatic] private static int _depth;
        public static bool Active => _depth > 0;
        public static IDisposable Enter() { _depth++; return new Handle(); }
        private sealed class Handle : IDisposable
        {
            private bool _done;
            public void Dispose() { if (_done) return; _done = true; _depth--; }
        }
    }

    /// <summary>
    /// Reflection glue for the geoscape-log toast mirror. The host resolves each new entry to its final display
    /// line (<c>GeoscapeLogEntry.GenerateMessage()</c>) and ships it; the client rebuilds a display-only entry
    /// (a <c>doNotLocalize</c> raw string, no typed parameters) and feeds it back through the native
    /// <c>GeoscapeLog.AddEntry</c> so the native notification + log panel render it exactly like a local raise.
    /// </summary>
    internal static class GeoscapeLogMirror
    {
        private static bool _tried;
        private static bool _ready;

        private static PropertyInfo _logProp;          // GeoLevelController.Log { get; }
        private static MethodInfo _addEntry;           // GeoscapeLog.AddEntry(GeoscapeLogEntry, GeoActor)
        private static Type _entryType;                // GeoscapeLogEntry
        private static ConstructorInfo _entryCtor;     // new GeoscapeLogEntry()
        private static FieldInfo _entryTextField;      // GeoscapeLogEntry.Text
        private static FieldInfo _entryHighPriField;   // GeoscapeLogEntry.HighPriority
        private static ConstructorInfo _ltbCtor;       // new LocalizedTextBind(string, bool)

        internal static bool Ensure()
        {
            if (_tried) return _ready;
            _tried = true;
            try
            {
                var geoLevelType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoLevelController");
                var logType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoscapeLog");
                _entryType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoscapeLogEntry");
                var ltbType = AccessTools.TypeByName("Base.UI.LocalizedTextBind");
                if (geoLevelType == null || logType == null || _entryType == null || ltbType == null) return false;

                _logProp = AccessTools.Property(geoLevelType, "Log");
                _addEntry = AccessTools.Method(logType, "AddEntry");
                _entryCtor = AccessTools.Constructor(_entryType, Type.EmptyTypes);
                _entryTextField = AccessTools.Field(_entryType, "Text");
                _entryHighPriField = AccessTools.Field(_entryType, "HighPriority");
                _ltbCtor = AccessTools.Constructor(ltbType, new[] { typeof(string), typeof(bool) });

                _ready = _logProp != null && _addEntry != null && _entryCtor != null
                         && _entryTextField != null && _entryHighPriField != null && _ltbCtor != null;
                return _ready;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoscapeLogMirror.Ensure failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Client: rebuild the mirrored entry and add it to the live GeoscapeLog (under the mirror scope so
        /// the client suppress-prefix lets exactly this one through). Best-effort; never throws into game code.</summary>
        internal static void ApplyMirroredEntry(GeoRuntime rt, string text, bool highPriority)
        {
            if (string.IsNullOrEmpty(text) || !Ensure()) return;
            var geoLevel = rt?.GeoLevel();
            if (geoLevel == null) return;
            var log = _logProp.GetValue(geoLevel, null);
            if (log == null) return;

            var entry = _entryCtor.Invoke(null);
            _entryTextField.SetValue(entry, _ltbCtor.Invoke(new object[] { text, true /*doNotLocalize*/ }));
            _entryHighPriField.SetValue(entry, highPriority);
            using (SyncApplyScope.Enter())
            using (GeoLogMirrorScope.Enter())
            {
                // AddEntry(entry, actor=null): host authority; the actor (sound/context) is not carried on the wire.
                _addEntry.Invoke(log, new object[] { entry, null });
            }
        }
    }

    /// <summary>
    /// The single chokepoint for every geoscape-log toast: <c>GeoscapeLog.AddEntry</c>.
    ///   • CLIENT (native raise): the client geoscape sim is frozen and domain state is written by silent
    ///     state-channel applies (never the native mutation events GeoscapeLog subscribes to), so these fire rarely
    ///     and inconsistently — SUPPRESS them and let the host mirror be the sole source (pure-mirror canon). The
    ///     one exception path is the mirror replay itself (<see cref="GeoLogMirrorScope"/> active) → allowed.
    ///   • HOST: after the entry is actually logged (respecting <c>LoggingEnabled</c>), broadcast its resolved line
    ///     to every peer (<c>GeoLogNotice</c> 0x6D). Fires for host-local sim AND for entries produced while the
    ///     host applies a client intent (host-authoritative state → must mirror to all).
    /// Timed countdown entries (AddTimedEntry, e.g. the pre-attack timer) are intentionally NOT patched — they ride
    /// mirrored attack-schedule state and self-update, so they stay native on the client.
    /// </summary>
    [HarmonyPatch]
    public static class GeoscapeLogAddEntryPatch
    {
        private static MethodBase _target;
        private static FieldInfo _loggingEnabledField;   // GeoscapeLog.LoggingEnabled
        private static MethodInfo _generateMessage;      // GeoscapeLogEntry.GenerateMessage()
        private static FieldInfo _highPriorityField;     // GeoscapeLogEntry.HighPriority

        public static bool Prepare()
        {
            var logType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoscapeLog");
            var entryType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Levels.GeoscapeLogEntry");
            if (logType == null || entryType == null) return false;
            _target = AccessTools.Method(logType, "AddEntry");
            _loggingEnabledField = AccessTools.Field(logType, "LoggingEnabled");
            _generateMessage = AccessTools.Method(entryType, "GenerateMessage");
            _highPriorityField = AccessTools.Field(entryType, "HighPriority");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // CLIENT suppress: skip a native local raise; allow only the mirror replay (GeoLogMirrorScope active).
        public static bool Prefix()
        {
            try
            {
                if (!EventDialogClientGuard.IsClient) return true;   // host / single-player: native
                if (GeoLogMirrorScope.Active) return true;           // the mirrored entry itself → allow
                return false;                                        // any other client raise → suppress (host mirrors)
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoscapeLogAddEntryPatch.Prefix failed: " + ex.Message); return true; }
        }

        // HOST broadcast: after a real add, ship the resolved line to every peer.
        // __instance = GeoscapeLog; __0 = the GeoscapeLogEntry.
        public static void Postfix(object __instance, object __0)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                if (GeoLogMirrorScope.Active) return;   // never re-broadcast a mirror replay (belt; host never enters it)
                if (__0 == null) return;
                // The original honors LoggingEnabled and returns without adding when false → don't mirror a
                // toast the host itself never showed.
                if (_loggingEnabledField != null && _loggingEnabledField.GetValue(__instance) is bool en && !en) return;

                string text = _generateMessage?.Invoke(__0, null) as string;
                if (string.IsNullOrEmpty(text)) return;
                bool highPriority = _highPriorityField != null && _highPriorityField.GetValue(__0) is bool hp && hp;
                engine.Sync?.BroadcastGeoLogNotice(text, highPriority);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoscapeLogAddEntryPatch.Postfix failed: " + ex.Message); }
        }
    }
}
