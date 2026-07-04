using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Util;
using UnityEngine;

namespace Multiplayer.Harmony
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
    // GATE (stays INERT for single instance AND real cross-machine co-op): the redirect is driven SOLELY
    // by MultiplayerLog.InstanceIndex — the 1-based same-machine index the PROVEN MultiplayerLog.Init fallback
    // loop already computes (multiplayer.log => 1, multiplayer-2.log => 2, …). MultiplayerMain.OnModEnabled
    // (which runs that loop) sorts before TFTV (mod ID "Morgott.Multiplayer" < "phoenixrising.tftv"), so the
    // index is set BEFORE TFTVLogger/PRMLogger.Initialize run. index 1 => leave TFTV.log unchanged (solo /
    // first instance = vanilla, no behavior change for normal play); index N>=2 => redirect to TFTV-N.log in
    // the same directory. This is BILATERAL in effect: the two instances get DISTINCT files (TFTV.log vs
    // TFTV-2.log) so neither blocks the other. No exclusive-open probe and no LogPath string-parse: the prior
    // version's race-prone probe could leave an instance un-redirected, so both wrote the shared junctioned
    // TFTV.log and the IOException sharing-violation dialog returned. The decision lives in the shared
    // TftvLogInstanceGate below so the PRMLogger sibling patch reuses the SAME gate (no fork).
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
                Debug.LogWarning("[Multiplayer] TFTV log redirect postfix (TFTVMain.LogPath) failed: " + e.Message);
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

    // Shared per-instance gate + redirect step, used identically by BOTH the TFTVLogger and the PRMLogger
    // prefixes so the decision logic is defined exactly once. The instance signal is MultiplayerLog.InstanceIndex
    // (computed by the proven MultiplayerLog.Init fallback loop); pure path math lives in TftvLogRedirect.
    internal static class TftvLogInstanceGate
    {
        // Rewrite logPath to the per-instance filename when this is same-machine instance N>=2.
        // index 1 => unchanged (vanilla TFTV.log). Any exception falls through to the original path —
        // never break the caller's logger init.
        public static void RedirectIfSecondary(ref string logPath, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath))
                    return;

                // Authoritative same-machine index from the proven MultiplayerLog.Init fallback loop,
                // which has already run (Multiplayer sorts before TFTV in mod load order).
                var index = MultiplayerLog.InstanceIndex;

                if (index <= 1)
                {
                    // Solo / first instance: keep vanilla TFTV.log untouched. Confirm what happened.
                    Debug.Log("[Multiplayer] " + label + ": instance 1, no redirect (" +
                              Path.GetFileName(logPath) + ")");
                    return;
                }

                var redirected = TftvLogRedirect.ResolveRedirectedPath(logPath, isSecondary: true, instanceIndex: index);
                if (!string.Equals(redirected, logPath, StringComparison.Ordinal))
                {
                    logPath = redirected;
                    Debug.Log("[Multiplayer] " + label + " redirect: instance " + index + " -> " +
                              Path.GetFileName(redirected));
                }
            }
            catch (Exception e)
            {
                // Never let a logging redirect break logger init — fall through to original path on any fault.
                Debug.LogWarning("[Multiplayer] " + label + " redirect prefix failed: " + e.Message);
            }
        }
    }

    // Deferred installer for the TFTV/PRM log redirect.
    //
    // WHY: Multiplayer.OnModEnabled runs harmony.PatchAll BEFORE TFTV is enabled (PP enables mods
    // sequentially; "Morgott.Multiplayer" sorts before "phoenixrising.tftv"). At PatchAll time the
    // TFTV.TFTVLogger / PRMBetterClasses.PRMLogger types are NOT yet loaded, so the [HarmonyPatch]
    // classes above hit their Prepare()/TargetMethod() null-gate and are SILENTLY skipped forever —
    // TFTV then inits its logger unpatched and both same-machine instances clobber the shared TFTV.log
    // (IOException sharing-violation popups). See commit e08a57a regression notes.
    //
    // FIX: subscribe to AppDomain.AssemblyLoad. That event fires synchronously while the CLR loads
    // TFTV's assembly (and lazily the bundled PRMBetterClasses assembly when TFTVMain.OnModEnabled is
    // JITted), which necessarily precedes the execution of TFTV's synchronous *Logger.Initialize calls.
    // The moment each logger type resolves we install the SAME existing Prefix/Postfix via harmony.Patch
    // — no forked redirect logic, the gate/redirect math is unchanged. A static guard per target makes
    // the patch idempotent across the multiple AssemblyLoad events.
    //
    // If TFTV is ALREADY loaded when we install (unexpected load-order), the [HarmonyPatch]/PatchAll path
    // above already covered it; we set the guards and do nothing, so there is never a double patch.
    internal static class TftvLogDeferredInstaller
    {
        private static readonly object _lock = new object();
        private static HarmonyLib.Harmony _harmony;
        private static AssemblyLoadEventHandler _handler;
        private static bool _tftvLogPatched;
        private static bool _prmLogPatched;

        // Called from MultiplayerMain.OnModEnabled right after PatchAll, with the mod's Harmony instance.
        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (harmony == null)
                return;

            _harmony = harmony;

            // Already loaded (e.g. load order changed)? Then PatchAll's [HarmonyPatch] classes already
            // patched it — mark as handled and do NOT patch again (avoid a duplicate prefix).
            if (AccessTools.TypeByName("TFTV.TFTVLogger") != null)
            {
                _tftvLogPatched = true;
                _prmLogPatched = true; // PRM (if present) was likewise covered by PatchAll at this point.
                Debug.Log("[Multiplayer] TFTV already loaded at PatchAll time; redirect handled by PatchAll.");
                return;
            }

            _handler = (sender, args) => OnAssemblyLoad();
            AppDomain.CurrentDomain.AssemblyLoad += _handler;
            Debug.Log("[Multiplayer] deferred TFTV-log redirect armed (instance " +
                      MultiplayerLog.InstanceIndex + "); waiting for TFTV assembly load.");
        }

        private static void OnAssemblyLoad()
        {
            // NEVER throw into an AppDomain event — that can destabilize the host. Swallow everything.
            try
            {
                lock (_lock)
                {
                    TryPatchTftvLogger();
                    TryPatchPrmLogger();

                    if (_tftvLogPatched && _prmLogPatched)
                        Unsubscribe();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Multiplayer] deferred TFTV-log redirect handler failed: " + e.Message);
            }
        }

        private static void TryPatchTftvLogger()
        {
            if (_tftvLogPatched)
                return;

            var target = TftvLogPerInstancePatch.TargetMethod(); // null until TFTV.TFTVLogger resolves.
            if (target == null)
                return;

            _harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(TftvLogPerInstancePatch), nameof(TftvLogPerInstancePatch.Prefix)),
                postfix: new HarmonyMethod(typeof(TftvLogPerInstancePatch), nameof(TftvLogPerInstancePatch.Postfix)));

            _tftvLogPatched = true;
            Debug.Log("[Multiplayer] deferred TFTV-log redirect installed (instance " +
                      MultiplayerLog.InstanceIndex + ").");
        }

        private static void TryPatchPrmLogger()
        {
            if (_prmLogPatched)
                return;

            var target = PrmLogPerInstancePatch.TargetMethod(); // null until PRMBetterClasses.PRMLogger resolves.
            if (target == null)
                return;

            _harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(PrmLogPerInstancePatch), nameof(PrmLogPerInstancePatch.Prefix)));

            _prmLogPatched = true;
            Debug.Log("[Multiplayer] deferred PRM-log redirect installed (instance " +
                      MultiplayerLog.InstanceIndex + ").");
        }

        private static void Unsubscribe()
        {
            if (_handler == null)
                return;
            AppDomain.CurrentDomain.AssemblyLoad -= _handler;
            _handler = null;
        }
    }
}
