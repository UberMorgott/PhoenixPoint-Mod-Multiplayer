using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multipleer.Util;
using UnityEngine;

namespace Multipleer.Harmony
{
    // Per-instance TFTV log file. The local 2-instance co-op test rig shares the TFTV mod folder via a
    // junction, so BOTH PP instances would otherwise open the single <TFTV>/TFTV.log and clobber each
    // other (Initialize truncates it; every write reopens the same handle — see TFTVLogger.cs:17,50,63,
    // 80,100). We redirect ONLY the FILENAME for the SECONDARY same-machine instance (TFTV.log ->
    // TFTV-2.log); the folder junction, mod, configs and saves all stay shared and untouched.
    //
    // SEAM: prefix on TFTVLogger.Initialize(string logPath, bool, string, string) rewriting the by-ref
    // logPath BEFORE the body stores it (line 17) and before the in-body Cleanup()/Always() banner
    // writes run — so even the first synchronous truncate/banner go to the redirected file. A postfix
    // mirrors the value into TFTVMain.LogPath (internal static, TFTVMain.cs:57) which the error dialog
    // text interpolates (TFTVLogger.cs:70), so the dialog reports the right file too.
    //
    // GATE (stays INERT for single instance AND real cross-machine co-op): we are "secondary" only when
    // an EARLIER same-machine instance already holds our canonical primary lock file. Detected first via
    // the live MultipleerLog signal (its own log fell back to multipleer-N.log) and, order-independently,
    // via an exclusive-open lock probe on <persistentDataPath>/Multipleer/multipleer.log. A lone instance
    // or a separate-machine peer never finds that local file locked => no redirect. The decision lives in
    // the shared TftvLogInstanceGate below so the PRMLogger sibling patch reuses the SAME gate (no fork).
    [HarmonyPatch]
    public static class TftvLogPerInstancePatch
    {
        public static bool Prepare()
        {
            // Silently no-op when TFTV is not installed — never PatchAll-bomb on a missing dependency.
            return AccessTools.TypeByName("TFTV.TFTVLogger") != null;
        }

        public static MethodBase TargetMethod()
        {
            var loggerType = AccessTools.TypeByName("TFTV.TFTVLogger");
            if (loggerType == null) return null;
            // Pin the 4-arg signature: Initialize(string logPath, bool debugLevel, string modDirectory, string modName).
            return AccessTools.Method(loggerType, "Initialize",
                new[] { typeof(string), typeof(bool), typeof(string), typeof(string) });
        }

        // Rewrite the by-ref logPath argument before the original body stores/uses it.
        public static void Prefix(ref string logPath)
        {
            TftvLogInstanceGate.RedirectIfSecondary(ref logPath, "TFTV log");
        }

        // Mirror the redirected path into TFTVMain.LogPath so the TFTV error dialog reports the right file.
        public static void Postfix(string logPath)
        {
            try
            {
                var mainType = AccessTools.TypeByName("TFTV.TFTVMain");
                if (mainType == null) return;
                var field = AccessTools.Field(mainType, "LogPath");
                if (field == null) return;
                field.SetValue(null, logPath); // logPath here is the (possibly rewritten) value the body stored.
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multipleer] TFTV log redirect postfix (TFTVMain.LogPath) failed: " + e.Message);
            }
        }
    }

    // Sibling patch for TFTV's bundled PRMBetterClasses.PRMLogger, which TFTVMain.cs:116 initializes with
    // the SAME shared LogPath (TFTV.log) as TFTVLogger — PRMLogger.Initialize stores it in its own
    // _logPath (PRMLogger.cs:17) then synchronously Cleanup()s (truncates) + Always() banners that file
    // (PRMLogger.cs:22-25). Without this, two same-machine instances both open TFTV.log via PRMLogger and
    // hit the sharing violation even though TFTVLogger was already redirected. We rewrite the by-ref path
    // to the SAME per-instance filename (TFTV-N.log) using the SAME gate + helper as the TFTVLogger patch
    // (no forked decision logic). Note PRMLogger.Initialize's 2nd param is `int debugLevel` (not bool), so
    // this is a distinct overload bound by parameter NAME below.
    [HarmonyPatch]
    public static class PrmLogPerInstancePatch
    {
        public static bool Prepare()
        {
            // Silently no-op when the TFTV-bundled PRMBetterClasses is not present.
            return AccessTools.TypeByName("PRMBetterClasses.PRMLogger") != null;
        }

        public static MethodBase TargetMethod()
        {
            var loggerType = AccessTools.TypeByName("PRMBetterClasses.PRMLogger");
            if (loggerType == null) return null;
            // Pin the 4-arg signature: Initialize(string logPath, int debugLevel, string modDirectory, string modName).
            return AccessTools.Method(loggerType, "Initialize",
                new[] { typeof(string), typeof(int), typeof(string), typeof(string) });
        }

        // Bind by parameter NAME (no positional __N injection); rewrite before the body stores/truncates.
        public static void Prefix(ref string logPath)
        {
            TftvLogInstanceGate.RedirectIfSecondary(ref logPath, "PRM log");
        }
    }

    // Shared secondary-instance gate + redirect step, used identically by BOTH the TFTVLogger and the
    // PRMLogger prefixes so the decision logic is defined exactly once. Pure path math lives in
    // TftvLogRedirect; this layer only resolves instance-ness (init-order-independently) and applies it.
    internal static class TftvLogInstanceGate
    {
        private const string DirName = "Multipleer";
        private const string PrimaryLogName = "multipleer.log";

        // Rewrite logPath to the per-instance filename iff we are a secondary same-machine instance.
        // Any exception falls through to the original path — never break the caller's logger init.
        public static void RedirectIfSecondary(ref string logPath, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath))
                    return;

                if (!TryResolveInstance(out var isSecondary, out var index) || !isSecondary)
                    return;

                var redirected = TftvLogRedirect.ResolveRedirectedPath(logPath, isSecondary: true, instanceIndex: index);
                if (!string.Equals(redirected, logPath, StringComparison.Ordinal))
                {
                    Debug.Log("[Multipleer] secondary instance: redirecting " + label + " " +
                              Path.GetFileName(logPath) + " -> " + Path.GetFileName(redirected));
                    logPath = redirected;
                }
            }
            catch (Exception e)
            {
                // Never let a logging redirect break logger init — fall through to original path on any fault.
                Debug.LogWarning("[Multipleer] " + label + " redirect prefix failed: " + e.Message);
            }
        }

        // Decide secondary-ness + which instance suffix to use, init-order-independently.
        private static bool TryResolveInstance(out bool isSecondary, out int index)
        {
            isSecondary = false;
            index = 1;

            // 1) Prefer the live signal: if our own MultipleerLog already fell back to multipleer-N.log,
            //    reuse that N for consistency (same suffix across both our log and TFTV's).
            var ownLog = MultipleerLog.LogPath;
            if (!string.IsNullOrEmpty(ownLog))
            {
                var n = ParseOwnInstanceIndex(ownLog);
                if (n > 1)
                {
                    isSecondary = true;
                    index = n;
                    return true;
                }
                // ownLog present and unsuffixed => MultipleerLog ran and we are the PRIMARY. Stay inert.
                return true;
            }

            // 2) Order-independent fallback (TFTV may init before our OnModEnabled): probe the canonical
            //    primary lock file with exclusive open. Locked => an earlier same-machine instance exists.
            var primaryLock = TryGetPrimaryLockPath();
            if (primaryLock == null)
                return false; // cannot determine path safely => do not redirect.

            if (TftvLogRedirect.ProbePrimaryLocked(primaryLock))
            {
                isSecondary = true;
                index = TftvLogRedirect.ResolveSecondaryIndex(MultipleerLog.LogPath ?? primaryLock);
                if (index < 2) index = 2;
                return true;
            }

            return true; // primary / lone / cross-machine => not secondary.
        }

        // Our own log filename "multipleer-N.log" => N; "multipleer.log" or unparseable => 1.
        private static int ParseOwnInstanceIndex(string ownLogPath)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(ownLogPath); // e.g. "multipleer-2"
                var dash = name.LastIndexOf('-');
                if (dash <= 0 || dash == name.Length - 1)
                    return 1;
                var tail = name.Substring(dash + 1);
                return int.TryParse(tail, out var n) && n >= 2 ? n : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static string TryGetPrimaryLockPath()
        {
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, DirName);
                return Path.Combine(dir, PrimaryLogName);
            }
            catch
            {
                return null;
            }
        }
    }
}
