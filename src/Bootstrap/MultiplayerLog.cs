using System;
using System.IO;
using UnityEngine;

namespace Multiplayer.Util
{
    /// <summary>
    /// Dedicated, mod-only rotating log file. <see cref="MpLog"/> writes here directly, independently
    /// of Player.log, so the two destinations can be enabled separately in the mod settings.
    ///
    /// File: &lt;Application.persistentDataPath&gt;/Multiplayer/multiplayer.log
    /// (same "Multiplayer" dir used by ClientIdentity). On each launch the previous runs are shifted
    /// down a bounded ring — multiplayer-prev.log … multiplayer-prev9.log — so a co-op session's log
    /// survives later launches. Unity's single Player-prev.log scheme was the model and it is the wrong
    /// one here: a sweep of the 13:30 co-op session found the whole HOST side gone, overwritten by two
    /// IDLE solo relaunches at 14:06 and 14:08. One slot means two boots erase the authoritative half of
    /// every session, and this project debugs from these files.
    /// </summary>
    public static class MultiplayerLog
    {
        private const string DirName = "Multiplayer";
        private const string LogName = "multiplayer.log";
        // Same-machine instance cap for the suffixed-file fallback (multiplayer-2.log … -N.log) when
        // the primary log is locked by an earlier instance (co-op client on the local 2-instance rig).
        private const int MaxInstances = 5;
        // Rotation ring per base name: how many previous runs to keep, and a total byte cap across them
        // so a long-log session can never let the ring grow without bound. Oldest is pruned first.
        private const int PrevKeep = 9;
        private const long PrevTotalCapBytes = 64L * 1024 * 1024;

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

                        // Rotate HERE, per suffix — each base name keeps its OWN ring
                        // (multiplayer-prev*.log, multiplayer-2-prev*.log, …). This used to sit before the
                        // loop and so only ever rotated multiplayer.log: the -N suffix is picked by LOCK
                        // ORDER at runtime, not by instance folder, so every fallback instance overwrote
                        // its own history on launch and two investigations were run without it.
                        // Best-effort by design — a first launch has nothing to move and a locked file
                        // must cost a log, never the boot.
                        if (File.Exists(attemptPath))
                            RotateRing(dir, Path.ChangeExtension(attemptName, null), attemptPath);

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
                    try { Debug.LogWarning("[MP][logging] dedicated log initialization failed: " + e.Message); }
                    catch { /* Unity ECalls are unavailable in the headless RailCheck host. */ }
                    _writer = null;
                }

            }
        }

        // Slot 1 keeps the historical "-prev.log" name so existing notes and habits still find the last
        // run; 2..PrevKeep are "-prev2.log" … Deliberately NOT "-2.log": that space belongs to the
        // same-machine INSTANCE fallback above and colliding the two would make a client's live log
        // look like the primary's history.
        private static string PrevPath(string dir, string baseName, int slot)
            => Path.Combine(dir, baseName + (slot == 1 ? "-prev.log" : "-prev" + slot + ".log"));

        // Every step is individually best-effort: one locked ring member must not cost the rotation of
        // the file we are about to truncate (that is the run we would lose).
        private static void TryMove(string from, string to)
        {
            try { if (File.Exists(from)) File.Move(from, to); } catch { }
        }

        private static void RotateRing(string dir, string baseName, string currentPath)
        {
            try { File.Delete(PrevPath(dir, baseName, PrevKeep)); } catch { }
            for (var slot = PrevKeep - 1; slot >= 1; slot--)
                TryMove(PrevPath(dir, baseName, slot), PrevPath(dir, baseName, slot + 1));
            TryMove(currentPath, PrevPath(dir, baseName, 1));

            // Byte cap, newest-first: keep adding until the next file would breach it, then drop that one
            // and everything older. Walking 1→PrevKeep is oldest-pruned-first because slot 1 is newest.
            long total = 0;
            for (var slot = 1; slot <= PrevKeep; slot++)
            {
                var path = PrevPath(dir, baseName, slot);
                try
                {
                    if (!File.Exists(path)) continue;
                    var len = new FileInfo(path).Length;
                    if (total + len > PrevTotalCapBytes) File.Delete(path);
                    else total += len;
                }
                catch { }
            }
        }

        /// <summary>Unsubscribe and close the file. Call from the mod's disable/unload hook.</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
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

        /// <summary>Writes one already-normalized entry. Logging failures never escape into gameplay.</summary>
        internal static void Write(string condition, LogType type)
        {
            lock (Gate)
            {
                if (_writer == null)
                    return;

                try
                {
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine(ts + " " + type + " " + condition);

                }
                catch
                {
                    // Swallow: a failed write must never propagate into the game loop.
                }
            }
        }
    }
}
