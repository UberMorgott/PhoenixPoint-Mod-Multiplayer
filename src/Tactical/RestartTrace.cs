using System;
using System.IO;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Harmony;
using Multiplayer.Network;
using Multiplayer.Util;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// INSTRUMENTATION ONLY — no gate, no fix, no behaviour of its own. It exists because of one report we
    /// could not diagnose: a peer restarted a tactical mission from the in-mission pause menu and a TFTV
    /// error popup appeared on that peer, and by the time anyone looked the evidence was gone.
    ///
    /// WHY THE EVIDENCE WAS GONE, precisely. TFTV writes its exceptions to its OWN file and nowhere else —
    /// <c>TFTVLogger.Error(Exception)</c> (<c>refs/TFTV-src/TFTV/TFTVLogger.cs</c>:59-73) appends the message
    /// and stack to <c>TFTVMain.LogPath</c> and then shows the "An error has occurred in the Terror from the
    /// Void mod!" prompt; nothing of it reaches Unity's log, so nothing of it reaches <c>multiplayer.log</c>.
    /// And <c>Cleanup()</c> (:48-56) TRUNCATES that file on every game launch, so two launches later the
    /// exception no longer exists anywhere. On the local two-instance rig it is worse still: both instances
    /// share the TFTV mod folder through a junction and therefore the SAME <c>TFTV.log</c>
    /// (<see cref="TftvLogRedirect"/> exists but is currently only wired to <c>ClientIdentity</c>'s paths, not
    /// to TFTV's own), so the two peers overwrite each other's evidence live. <see cref="TftvErrorMirror"/>
    /// is the answer to that half; this class is the answer to the other half — WHAT OUR CODE WAS DOING at
    /// the time.
    ///
    /// WHAT THE RESTART PATH ACTUALLY IS, and why it is worth tracing at all: a restart has no method of its
    /// own anywhere in the engine. <c>UIModulePauseScreen.OnRestartConfirmed</c>:203-211 calls
    /// <c>PhoenixGame.FinishLevel(new RestartGameResult(CurrentLevel().LevelParams))</c> and
    /// <c>TacticalGameCrt</c>:574-580 loops that RESULT TYPE back into <c>RunGameLevel</c>. So every step
    /// below is one of ours sitting on a shared funnel, in an order nothing enforces — which is exactly the
    /// kind of thing a log has to show rather than a reader having to reconstruct.
    ///
    /// THE TRACE IS ONE STOPWATCH AND A PREFIX. Every line is <c>[MP][restart]</c> + elapsed + this peer's
    /// role, so `grep "\[MP\]\[restart\]"` on either peer's <c>multiplayer.log</c> is the whole story of one
    /// restart in order, and the two peers' copies line up against each other. Rare by construction (a human
    /// pressing a menu button), so verbosity here costs nothing; nothing in it runs per frame or per actor.
    ///
    /// <see cref="Mark"/> OPENS the trace, <see cref="Note"/> only writes into an open one — the difference
    /// matters because some of the seams below (the teardown prefix, the barrier arm, a TFTV exception) are
    /// shared with every other level change in the game and must stay silent outside a restart. A trace that
    /// never closes (a client's press that the host refuses) goes stale after <see cref="StaleMs"/> rather
    /// than stamping the next restart with a nonsense elapsed.
    /// </summary>
    internal static class RestartTrace
    {
        /// <summary>THE grep marker. One string, every line of the restart path, both peers.</summary>
        internal const string Tag = "[MP][restart]";

        private const long StaleMs = 120000;
        private static readonly Stopwatch Watch = new Stopwatch();
        private static long _tftvLogBytesAtEntry = -1;

        internal static bool Open => Watch.IsRunning && Watch.ElapsedMilliseconds < StaleMs;

        /// <summary>Log a step, opening the trace (or replacing a stale one) if none is running.</summary>
        internal static void Mark(string step)
        {
            if (!Open) Watch.Restart();
            Write(step);
        }

        /// <summary>Log a step ONLY if a restart is in flight. For seams every level change passes.</summary>
        internal static void Note(string step)
        {
            if (Open) Write(step);
        }

        /// <summary>The head of the trace: who is restarting, what level, and what we hold about TFTV.</summary>
        internal static void Enter(ILevelParams result)
        {
            Mark("ENTER — PhoenixGame.FinishLevel was handed a RestartGameResult (the only thing in the " +
                 "engine that distinguishes a restart from any other level change).");
            Note("level params: " + Describe(result));
            Note("TFTV patches: " + TftvLateBinder.BoundSummary);
            _tftvLogBytesAtEntry = TftvLogBytes();
            Note("TFTV log: " + TftvLogPath() + " (" + Bytes(_tftvLogBytesAtEntry) + ")");
        }

        /// <summary>The tail: the reloaded level is up. Closes the trace.</summary>
        internal static void Exit(string why)
        {
            if (!Open) return;
            long now = TftvLogBytes();
            long grew = _tftvLogBytesAtEntry >= 0 && now >= _tftvLogBytesAtEntry ? now - _tftvLogBytesAtEntry : -1;
            Write("EXIT — " + why + ". TFTV log " + Bytes(now) +
                  (grew < 0 ? " (growth unknown)"
                            : grew == 0 ? " — TFTV wrote NOTHING during this restart"
                                        : " — TFTV wrote " + grew + " byte(s) during this restart; if a TFTV " +
                                          "error popup appeared, its text is in that file and in the " +
                                          "[MP][tftv] lines above"));
            Watch.Reset();
            _tftvLogBytesAtEntry = -1;
        }

        private static void Write(string step) =>
            MpLog.Log(Tag + " +" + Watch.ElapsedMilliseconds + "ms " + Role() + " " + step);

        /// <summary>Role plus the same-machine instance index, so the two halves of the local co-op rig are
        /// distinguishable when their logs are read side by side.</summary>
        private static string Role()
        {
            string role;
            try
            {
                var engine = NetworkEngine.Instance;
                role = engine == null || !engine.IsActiveSession ? "SOLO" : engine.IsHost ? "HOST" : "CLIENT";
            }
            catch { role = "ROLE?"; }
            return "[" + role + " i" + MultiplayerLog.InstanceIndex + "]";
        }

        /// <summary>Everything about the level being reloaded that identifies it — the map seed above all,
        /// because two peers that restart into different seeds are the divergence L328 exists about, and
        /// nothing else in either log would say so.</summary>
        private static string Describe(ILevelParams result)
        {
            try
            {
                if (result == null) return "<null>";
                var inner = (result as RestartGameResult)?.RestartGameParams;
                var name = result.GetType().Name + (inner == null ? "" : "(" + inner.GetType().Name + ")");
                if (!(inner is TacticalGameParams tac)) return name;
                return name +
                       " location='" + (tac.LocationName ?? "?") + "'" +
                       " mission=" + (tac.MissionData?.MissionType == null ? "?" : tac.MissionData.MissionType.name) +
                       " missionId=" + (tac.MissionData == null ? "?" : tac.MissionData.MissionId.ToString()) +
                       " seed=" + (tac.MapPlotGenerationData == null ? "?"
                                                                     : tac.MapPlotGenerationData.RandomSeed.ToString()) +
                       " mist=" + tac.IsInMist + " corruption=" + tac.IsCorruptionActive;
            }
            catch (Exception e) { return "<unreadable: " + e.Message + ">"; }
        }

        /// <summary>TFTV's own log file, read the way TFTV names it (<c>TFTVMain.LogPath</c>, an internal
        /// static string set at <c>TFTVMain.cs</c>:114) — reflected, because this mod holds no reference to
        /// TFTV and must run identically without it.</summary>
        internal static string TftvLogPath()
        {
            try
            {
                var t = AccessTools.TypeByName("TFTV.TFTVMain");
                if (t == null) return "<TFTV not loaded>";
                return AccessTools.Field(t, "LogPath")?.GetValue(null) as string ?? "<unset>";
            }
            catch (Exception e) { return "<unreadable: " + e.Message + ">"; }
        }

        private static long TftvLogBytes()
        {
            try
            {
                var p = TftvLogPath();
                return string.IsNullOrEmpty(p) || p[0] == '<' || !File.Exists(p) ? -1 : new FileInfo(p).Length;
            }
            catch { return -1; }
        }

        private static string Bytes(long n) => n < 0 ? "size unknown" : n + " bytes";
    }

    /// <summary>
    /// The tail of <see cref="RestartTrace"/>. <c>TacticalLevelController.OnLevelStateChanged</c> is the
    /// game's own level-state listener (TacticalLevelController.cs:419) and <c>Playing</c> is the first
    /// moment the reloaded level exists — the same edge <c>TacDeployReadyCapture</c> already waits on, which
    /// is why it is the honest end of "how long did the restart take on this peer". A third postfix on that
    /// method is deliberate: neither of the two that were already there is about restarts, and folding this
    /// into one of them would tie an instrumentation line to a law's seam.
    /// </summary>
    [HarmonyPatch(typeof(TacticalLevelController), "OnLevelStateChanged")]
    internal static class RestartTraceExit
    {
        private static void Postfix(Level.State state)
        {
            if (state != Level.State.Playing) return;
            RestartTrace.Exit("the reloaded tactical level reached Playing on this peer");
        }
    }
}
