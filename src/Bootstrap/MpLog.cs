using System;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// The mod's only logging door. Every emitted line has one canonical
    /// <c>[MP][category]</c> prefix and is routed independently to the dedicated mod log and
    /// Unity's Player.log according to <see cref="MultiplayerConfig"/>.
    /// </summary>
    public static class MpLog
    {
        public static void Log(object message) => Write(LogType.Log, Normalize(message?.ToString()));
        public static void LogWarning(object message) => Write(LogType.Warning, Normalize(message?.ToString()));
        public static void LogError(object message) => Write(LogType.Error, Normalize(message?.ToString()));

        public static void Log(string category, string message) =>
            Write(LogType.Log, Format(category, message));

        public static void LogWarning(string category, string message) =>
            Write(LogType.Warning, Format(category, message));

        public static void LogError(string category, string message) =>
            Write(LogType.Error, Format(category, message));

        public static void LogError(string message, Exception exception)
        {
            var line = Normalize(message);
            if (exception != null) line += Environment.NewLine + exception;
            Write(LogType.Error, line);
        }

        private static void Write(LogType type, string line)
        {
            var config = MultiplayerMain.Instance?.Config;
            var dedicated = config?.WriteDedicatedLog ?? true;
            var player = config?.WritePlayerLog ?? true;

            if (dedicated)
            {
                Multiplayer.Util.MultiplayerLog.Write(line, type);
            }

            if (!player) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Debug.LogError(line);
            else if (type == LogType.Warning)
                Debug.LogWarning(line);
            else
                Debug.Log(line);
        }

        private static string Format(string category, string message)
        {
            if (string.IsNullOrWhiteSpace(category)) category = "general";
            category = category.Trim().Trim('[', ']').ToLowerInvariant();
            return "[MP][" + category + "] " + (message ?? "");
        }

        // Keeps old call sites readable during the migration while making their output canonical.
        private static string Normalize(string message)
        {
            if (string.IsNullOrEmpty(message)) return Format("general", "");

            string rest;
            if (message.StartsWith("[Multiplayer]", StringComparison.Ordinal))
                rest = message.Substring("[Multiplayer]".Length);
            else if (message.StartsWith("[MP]", StringComparison.Ordinal))
                rest = message.Substring("[MP]".Length);
            else
                return Format("general", message);

            rest = rest.TrimStart();
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var end = rest.IndexOf(']');
                if (end > 1)
                    return Format(rest.Substring(1, end - 1), rest.Substring(end + 1).TrimStart());
            }
            return Format("general", rest);
        }
    }
}
