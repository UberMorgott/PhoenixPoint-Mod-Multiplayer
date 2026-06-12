using System;
using System.IO;
using UnityEngine;

namespace Multipleer.Util
{
    /// <summary>
    /// Dedicated, mod-only log file. A passive SINK that subscribes to
    /// <see cref="Application.logMessageReceived"/> and mirrors every entry whose message
    /// contains the "[Multipleer]" prefix into a clean file, so debugging no longer means
    /// scrolling the shared (huge, engine-spammy) Player.log.
    ///
    /// It does NOT change how lines are emitted — existing Logger.Log* / Debug.Log* calls are
    /// untouched. It captures everything they already write (plus mod-thrown exception
    /// stacktraces), past and future, with zero migration of call sites.
    ///
    /// File: &lt;Application.persistentDataPath&gt;/Multipleer/multipleer.log
    /// (same "Multipleer" dir used by ClientIdentity). On each launch the previous run's log is
    /// rotated to multipleer-prev.log, mirroring Unity's Player.log / Player-prev.log scheme.
    /// </summary>
    public static class MultipleerLog
    {
        private const string Prefix = "[Multipleer]";
        private const string DirName = "Multipleer";
        private const string LogName = "multipleer.log";
        private const string PrevName = "multipleer-prev.log";
        // Same-machine instance cap for the suffixed-file fallback (multipleer-2.log … -N.log) when
        // the primary log is locked by an earlier instance (co-op client on the local 2-instance rig).
        private const int MaxInstances = 5;

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static bool _initialized;

        /// <summary>Resolved log file path; null until <see cref="Init"/> succeeds.</summary>
        public static string LogPath { get; private set; }

        /// <summary>
        /// Idempotent. Rotates the previous log, opens a fresh one, writes a launch header and
        /// subscribes the sink. Safe to call more than once (subsequent calls are no-ops).
        /// </summary>
        public static void Init()
        {
            lock (Gate)
            {
                if (_initialized)
                    return;
                _initialized = true; // set first: even on partial failure we never retry/double-subscribe.

                try
                {
                    var dir = Path.Combine(Application.persistentDataPath, DirName);
                    Directory.CreateDirectory(dir);

                    var logPath = Path.Combine(dir, LogName);
                    var prevPath = Path.Combine(dir, PrevName);
                    LogPath = logPath;

                    // Rotate: keep exactly one previous run (multipleer-prev.log).
                    try
                    {
                        if (File.Exists(logPath))
                        {
                            if (File.Exists(prevPath))
                                File.Delete(prevPath);
                            File.Move(logPath, prevPath);
                        }
                    }
                    catch
                    {
                        // Rotation is best-effort; fall back to truncating the fresh file below.
                    }

                    // append:false -> truncate/create fresh; AutoFlush so a crash still leaves data on disk.
                    // A 2nd same-machine instance (the co-op client test rig) finds multipleer.log
                    // locked by instance 1 → IOException (sharing violation). Fall back to an
                    // instance-suffixed file (multipleer-2.log, -3.log, … up to MaxInstances) so the
                    // client gets its OWN dedicated log instead of silently writing nothing.
                    var fellBack = false;
                    for (var instance = 1; instance <= MaxInstances && _writer == null; instance++)
                    {
                        var attemptName = instance == 1
                            ? LogName
                            : "multipleer-" + instance + ".log";
                        var attemptPath = Path.Combine(dir, attemptName);
                        try
                        {
                            _writer = new StreamWriter(attemptPath, append: false) { AutoFlush = true };
                            LogPath = attemptPath;
                            fellBack = instance > 1;
                        }
                        catch (IOException)
                        {
                            // Locked by another instance — try the next suffix. If we exhaust the cap
                            // the outer catch reports it and logging degrades to the engine log.
                            if (instance == MaxInstances) throw;
                        }
                    }

                    _writer.WriteLine(
                        "=== Multipleer log — launch " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                    if (fellBack)
                        _writer.WriteLine(
                            "[Multipleer] primary log was locked (another instance) — using fallback file: " +
                            Path.GetFileName(LogPath));
                }
                catch (Exception e)
                {
                    // Never let logging setup take down the mod; report once via the engine log.
                    Debug.LogWarning("[Multipleer] MultipleerLog.Init failed: " + e.Message);
                    _writer = null;
                }

                // Subscribe regardless: even if the file failed, Handler is a safe no-op then.
                Application.logMessageReceived += Handler;
            }
        }

        /// <summary>Unsubscribe and close the file. Call from the mod's disable/unload hook.</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
                Application.logMessageReceived -= Handler;

                if (_writer != null)
                {
                    try
                    {
                        _writer.Flush();
                        _writer.Dispose();
                    }
                    catch
                    {
                        // ignore — nothing useful to do on close failure.
                    }
                    _writer = null;
                }

                _initialized = false;
            }
        }

        // logMessageReceived can fire off the main thread; everything here is under Gate and
        // wrapped so a logging fault can never throw back into the engine.
        private static void Handler(string condition, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(condition) || condition.IndexOf(Prefix, StringComparison.Ordinal) < 0)
                return;

            lock (Gate)
            {
                if (_writer == null)
                    return;

                try
                {
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine(ts + " " + type + " " + condition);

                    if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) &&
                        !string.IsNullOrEmpty(stackTrace))
                    {
                        _writer.WriteLine(stackTrace);
                    }
                }
                catch
                {
                    // Swallow: a failed write must never propagate into the game loop.
                }
            }
        }
    }
}
