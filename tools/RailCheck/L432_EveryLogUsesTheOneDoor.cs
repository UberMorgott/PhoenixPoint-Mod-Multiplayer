using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer;
using Multiplayer.Network;
using Multiplayer.Util;

namespace RailCheck
{
    /// <summary>
    /// L432 — EVERY MOD LOG USES ONE DOOR.
    /// Direct Unity/SDK logging bypasses destination settings and can restore the historical split between
    /// [MP] and [Multiplayer]. MpLog is the only production emitter; it canonicalizes the prefix and routes
    /// independently to multiplayer.log and Player.log. Detailed traces are controlled by the same live config.
    ///
    /// POSITIVE CONTROL: MpLog itself must call UnityEngine.Debug, otherwise a sweep finding no forbidden calls
    /// could pass after Player.log routing was deleted altogether.
    /// </summary>
    internal static class L432_EveryLogUsesTheOneDoor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var log = typeof(MpLog);
            var config = typeof(MultiplayerConfig);
            var write = log.GetMethod("Write", All);
            var normalize = log.GetMethod("Normalize", All);
            var dedicated = config.GetField("WriteDedicatedLog", All);
            var player = config.GetField("WritePlayerLog", All);
            var diagnostic = config.GetField("EnableDiagnosticLogging", All);
            var diagGetter = typeof(MpDiag).GetProperty("On", All)?.GetGetMethod(true);
            if (write == null || normalize == null || dedicated == null || player == null ||
                diagnostic == null || diagGetter == null)
            {
                yield return "L432 premise-changed: MpLog.Write/Normalize, the three logging settings, or " +
                             "MpDiag.On no longer resolves. The single logging door and its live switches are unchecked.";
                yield break;
            }

            var defaults = new MultiplayerConfig();
            if (!defaults.WriteDedicatedLog || !defaults.WritePlayerLog || !defaults.EnableDiagnosticLogging)
                yield return "L432 defaults-not-on: dedicated log, Player.log duplication, and detailed diagnostics " +
                             "must all default to enabled.";

            var canonical = (string)normalize.Invoke(null, new object[] { "[Multiplayer][tac] test" });
            if (canonical != "[MP][tac] test")
                yield return "L432 prefix-not-canonical: legacy input normalized to '" + canonical +
                             "' instead of '[MP][tac] test'.";

            var writeFields = Program.FieldRefs(write).ToList();
            if (!writeFields.Contains(dedicated) || !writeFields.Contains(player))
                yield return "L432 routing-switch-bypassed: MpLog.Write does not read both destination settings.";
            if (!Program.FieldRefs(diagGetter).Contains(diagnostic))
                yield return "L432 diagnostic-switch-bypassed: MpDiag.On does not read EnableDiagnosticLogging.";

            var methods = log.Assembly.GetTypes().SelectMany(t =>
            {
                try { return t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                              .Concat(t.GetConstructors(All | BindingFlags.DeclaredOnly)); }
                catch { return Enumerable.Empty<MethodBase>(); }
            }).ToList();

            bool positive = false;
            foreach (var method in methods)
            {
                foreach (var callee in Program.CalleeSequence(method))
                {
                    var owner = callee.DeclaringType?.FullName ?? "";
                    var unity = owner == "UnityEngine.Debug" && callee.Name.StartsWith("Log", StringComparison.Ordinal);
                    var sdk = owner == "PhoenixPoint.Modding.ModLogger" && callee.Name.StartsWith("Log", StringComparison.Ordinal);
                    if (!unity && !sdk) continue;
                    if (method.DeclaringType == log) { positive = true; continue; }
                    // Dedicated-log initialization cannot call MpLog after its own writer failed; this one
                    // canonical Player.log fallback is the sole recursion-safe exception.
                    if (method.DeclaringType == typeof(MultiplayerLog) && method.Name == "Init") continue;
                    yield return "L432 logging-door-bypassed: " + method.DeclaringType?.FullName + "." +
                                 method.Name + " calls " + owner + "." + callee.Name + " directly.";
                }
            }
            if (!positive)
                yield return "L432 player-route-vacuous: MpLog no longer reaches UnityEngine.Debug, so the " +
                             "Player.log checkbox cannot emit anything.";
        }
    }
}
