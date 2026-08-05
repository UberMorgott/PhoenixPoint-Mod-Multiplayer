using System;
using System.IO;
using UnityEngine;

namespace Multiplayer.Util
{
    /// <summary>
    /// Dedicated, mod-only log file. A passive SINK that subscribes to
    /// <see cref="Application.logMessageReceived"/> and mirrors every entry whose message
    /// contains the "[Multiplayer]" prefix into a clean file, so debugging no longer means
    /// scrolling the shared (huge, engine-spammy) Player.log.
    ///
    /// It does NOT change how lines are emitted — existing Logger.Log* / Debug.Log* calls are
    /// untouched. It captures everything they already write (plus mod-thrown exception
    /// stacktraces), past and future, with zero migration of call sites.
    ///
    /// File: &lt;Application.persistentDataPath&gt;/Multiplayer/multiplayer.log
    /// (same "Multiplayer" dir used by ClientIdentity). On each launch the previous run's log is
    /// rotated to multiplayer-prev.log, mirroring Unity's Player.log / Player-prev.log scheme.
    /// </summary>
    public static class MultiplayerLog
    {
        /// <summary>Every prefix the mod's own <c>Debug.Log</c> call sites actually use. The original
        /// single <c>"[Multiplayer]"</c> literal matched the bootstrap/lobby banner only, so the WHOLE
        /// rail — every <c>[MP][events]</c>, <c>[MP][rail]</c>, <c>[MP][vehicle]</c> line — was filtered
        /// OUT of multiplayer*.log and existed solely in Unity's Player.log. Diagnosis then needed two
        /// files, and the one the mod ships was the one missing the evidence.</summary>
        private static readonly string[] Prefixes = { "[MP]", "[Multiplayer]" };
        private const string DirName = "Multiplayer";
        private const string LogName = "multiplayer.log";
        // Same-machine instance cap for the suffixed-file fallback (multiplayer-2.log … -N.log) when
        // the primary log is locked by an earlier instance (co-op client on the local 2-instance rig).
        private const int MaxInstances = 5;

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static bool _initialized;

        /// <summary>Resolved log file path; null until <see cref="Init"/> succeeds.</summary>
        public static string LogPath { get; private set; }

        /// <summary>
        /// 1-based same-machine instance index, computed by the proven <see cref="Init"/> fallback loop:
        /// the FIRST instance opens multiplayer.log => 1; a SECOND same-machine instance finds it locked,
        /// falls back to multiplayer-2.log => 2; and so on. Defaults to 1 (single instance / before Init).
        /// This is the SINGLE authoritative instance signal — other subsystems (e.g. the TFTV per-instance
        /// log redirect) must read THIS rather than re-deriving instance-ness with their own race-prone probe.
        /// </summary>
        public static int InstanceIndex { get; private set; } = 1;

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

                    LogPath = Path.Combine(dir, LogName);

                    // append:false -> truncate/create fresh; AutoFlush so a crash still leaves data on disk.
                    // A 2nd same-machine instance (the co-op client test rig) finds multiplayer.log
                    // locked by instance 1 → IOException (sharing violation). Fall back to an
                    // instance-suffixed file (multiplayer-2.log, -3.log, … up to MaxInstances) so the
                    // client gets its OWN dedicated log instead of silently writing nothing.
                    var fellBack = false;
                    for (var instance = 1; instance <= MaxInstances && _writer == null; instance++)
                    {
                        var attemptName = instance == 1
                            ? LogName
                            : "multiplayer-" + instance + ".log";
                        var attemptPath = Path.Combine(dir, attemptName);

                        // Rotate HERE, per suffix — keep exactly one previous run each
                        // (multiplayer-prev.log, multiplayer-2-prev.log, …). This used to sit before the
                        // loop and so only ever rotated multiplayer.log: the -N suffix is picked by LOCK
                        // ORDER at runtime, not by instance folder, so every fallback instance overwrote
                        // its own history on launch and two investigations were run without it.
                        // Best-effort by design — a first launch has nothing to move and a locked file
                        // must cost a log, never the boot.
                        try
                        {
                            if (File.Exists(attemptPath))
                            {
                                var prevPath = Path.Combine(
                                    dir, Path.ChangeExtension(attemptName, null) + "-prev.log");
                                if (File.Exists(prevPath))
                                    File.Delete(prevPath);
                                File.Move(attemptPath, prevPath);
                            }
                        }
                        catch
                        {
                            // Locked or unmovable; the append:false open below still truncates it.
                        }

                        try
                        {
                            _writer = new StreamWriter(attemptPath, append: false) { AutoFlush = true };
                            LogPath = attemptPath;
                            InstanceIndex = instance; // authoritative 1-based same-machine index for all subsystems.
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
                        "=== Multiplayer log — launch " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                    if (fellBack)
                        _writer.WriteLine(
                            "[Multiplayer] primary log was locked (another instance) — using fallback file: " +
                            Path.GetFileName(LogPath));
                }
                catch (Exception e)
                {
                    // Never let logging setup take down the mod; report once via the engine log.
                    Debug.LogWarning("[Multiplayer] MultiplayerLog.Init failed: " + e.Message);
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

        // Explicit loop, not Array.Exists: this runs on EVERY Unity log line in the game, and a
        // closure capturing `condition` would allocate once per line.
        private static bool IsOurs(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return false;
            for (int i = 0; i < Prefixes.Length; i++)
                if (condition.IndexOf(Prefixes[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // logMessageReceived can fire off the main thread; everything here is under Gate and
        // wrapped so a logging fault can never throw back into the engine.
        private static void Handler(string condition, string stackTrace, LogType type)
        {
            if (!IsOurs(condition))
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
