using System;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// CLIENT loading INDICATOR for host-driven level transitions (surface 0x9C <c>tac.load.phase</c>). Before
    /// this, a client sat on a FROZEN screen with zero feedback while the host loaded a tactical level +
    /// serialized + broadcast the deploy snapshot (geoscape→tactical), and again while the host loaded the
    /// geoscape after a mission (tactical→geoscape). Three client stages:
    ///   (1) HOST-LOADING — the host pings <c>tac.load.phase(progress)</c> while it loads; the client shows the
    ///       game's NATIVE loading curtain + bottom bar "Host is loading the mission…" eased toward the fraction.
    ///   (2) DOWNLOADING  — the client relabels to "Downloading mission…" and drives the bar by
    ///       received-chunks/total as the deploy snapshot (tac.deploy / tac.deployChunk) arrives.
    ///   (3) HAND-OFF     — when the client kicks its OWN native level load, OUR bar hand-off's to the native
    ///       level-load bar (EndDownloadBar, curtain stays down) — the SaveTransferCoordinator.SetLoadingLevel idiom.
    ///
    /// This is PURELY additive DISPLAY. It NEVER touches the lobby-load / save-transfer flow: while a
    /// <c>SaveTransferCoordinator</c> transfer is active (TransferActive/InPhase2) the client backs off entirely
    /// and lets that flow own the curtain — so the tactical→geoscape DOWNLOAD stage (already driven by
    /// <c>SaveTransferCoordinator.OnSaveChunk</c>) and the session-start load are untouched. A client watchdog
    /// lifts the curtain if the host goes silent, and any session end / mission boundary tears it down, so the
    /// client is never stranded on a stuck curtain. Strings are hardcoded English (mod pattern); the HOST only
    /// SENDS — it never shows these client labels.
    /// </summary>
    public static class TacticalLoadPhaseSync
    {
        // How often the host pings progress, and the fraction-model tunables.
        private const float SendInterval = 0.5f;    // ~0.5s between host pings
        private const float CreepSeconds = 30f;     // time-creep denominator when no native progress is readable
        private const float CreepCeiling = 0.95f;   // never let the creep pretend the load finished
        private const float HostMaxSeconds = 45f;   // host heartbeat self-terminates after this (reverse backstop)
        private const float WatchdogSeconds = 120f; // client lifts the curtain after this much host silence

        // ─── HOST send state ───────────────────────────────────────────────
        private static bool _hostLoading;
        private static float _hostStart;
        private static float _hostLastSend;
        private static float _hostLastProgress;

        // ─── CLIENT curtain state ──────────────────────────────────────────
        private static bool _curtainUp;
        private static float _lastActivity;
        // Download-chunk progress tracking, keyed by deploy generation (chunks arrive unordered + duplicated).
        private static int _dlGen = int.MinValue;
        private static System.Collections.Generic.HashSet<int> _dlSeen = new System.Collections.Generic.HashSet<int>();

        // ═══════════════════════════════════════════════════════════════════
        //  HOST — send tac.load.phase while loading a level
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>HOST: a level load just began (geoscape→tactical launch, OR tactical→geoscape return).
        /// Start pinging <c>tac.load.phase</c> so the client shows a loading curtain. Idempotent; host-only
        /// (a client never sends). Sends the initial (0, 0) immediately.</summary>
        public static void HostBeginLoad()
        {
            var e = NetworkEngine.Instance;
            if (e == null || !e.IsActive || !e.IsHost) return;
            _hostLoading = true;
            _hostStart = Now;
            _hostLastSend = Now;
            _hostLastProgress = 0f;
            Broadcast(e, 0f);
        }

        /// <summary>HOST: stop pinging (the deploy was broadcast / the load is over). Idempotent.</summary>
        public static void HostEndLoad() => _hostLoading = false;

        /// <summary>HOST per-frame driver (from <c>MultiplayerUI.Update</c>): rate-limited progress ping.
        /// Self-terminates once the deploy is broadcast (forward), or a save-transfer takes over the client
        /// feedback (reverse), or a hard time budget elapses — so a stuck load never heartbeats forever.</summary>
        private static void HostTick()
        {
            if (!_hostLoading) return;
            var e = NetworkEngine.Instance;
            if (e == null || !e.IsActive || !e.IsHost) { _hostLoading = false; return; }

            var coord = e.SaveTransfer;
            if (TacticalDeploySync.HostHasBroadcastDeploy                    // forward: deploy snapshot sent
                || (coord != null && coord.TransferActive)                  // reverse: SaveTransferCoordinator owns feedback now
                || (Now - _hostStart) > HostMaxSeconds)                     // hard backstop
            {
                _hostLoading = false;
                return;
            }

            if (Now - _hostLastSend < SendInterval) return;
            _hostLastSend = Now;

            float p = ReadHostProgress();
            if (p < _hostLastProgress) p = _hostLastProgress;   // monotonic — never rewind the client bar
            _hostLastProgress = p;
            Broadcast(e, p);
        }

        // Prefer the host's OWN native loading-bar fill (its game is really loading this level); fall back to a
        // time-based creep so the bar still moves when the native bar isn't up yet (early launch frames).
        private static float ReadHostProgress()
        {
            if (TryReadNativeFill(out float fill)) return fill;
            float creep = (Now - _hostStart) / CreepSeconds;
            return creep < CreepCeiling ? creep : CreepCeiling;
        }

        // Read SceneFadeController.ProgressBar.ProgressFill.fillAmount — the SAME reflection idiom
        // NativeWidgetFactory.CaptureLiveProgressBar uses, minus its per-call log (this polls every ~0.5s).
        // Reuses the (non-logging) GetProgressFill extractor for the last hop.
        private static bool TryReadNativeFill(out float fill)
        {
            fill = 0f;
            try
            {
                var sfcType = AccessTools.TypeByName("Base.Utils.SceneFadeController");
                if (sfcType == null) return false;
                var sfc = UnityEngine.Object.FindObjectOfType(sfcType);
                if (sfc == null) return false;
                var pbc = AccessTools.Field(sfcType, "ProgressBar").GetValue(sfc) as Component;
                var img = NativeWidgetFactory.GetProgressFill(pbc);
                if (img == null) return false;
                fill = Mathf.Clamp01(img.fillAmount);
                return true;
            }
            catch { return false; }
        }

        private static void Broadcast(NetworkEngine e, float progress01)
        {
            try
            {
                byte[] payload = TacLoadPhaseCodec.Encode(TacLoadPhaseCodec.PhaseHostLoading, progress01);
                TacticalMoveSync.BroadcastToAll(e, TacticalSurfaceIds.TacLoadPhase, payload);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TacLoadPhase broadcast failed: " + ex); }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CLIENT — show / drive / tear down the loading curtain
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>CLIENT inbound (surface 0x9C): stage-1 host-loading ping → show/keep the curtain and ease
        /// the bar toward the fraction. Idempotent — repeated pings just update the bar.</summary>
        public static void HandleLoadPhase(byte[] payload)
        {
            if (!TacLoadPhaseCodec.TryDecode(payload, out _, out float progress))
            {
                Debug.LogError("[Multiplayer][tac] TacLoadPhase decode failed (" + (payload?.Length ?? 0) + " bytes)");
                return;
            }
            if (!ClientGuardOpen()) return;
            EnsureCurtain("Host is loading the mission…");
            NativeWidgetFactory.SetDownloadBar(progress);
        }

        /// <summary>CLIENT: a chunked deploy fragment arrived → stage-2 "Downloading mission…" driven by
        /// received-chunks/total. Shows the curtain here too if the stage-1 ping was missed (same guard).</summary>
        public static void ClientOnDeployChunk(int deployGeneration, int chunkIndex, int chunkCount)
        {
            if (!ClientGuardOpen()) return;
            if (_dlGen != deployGeneration)
            {
                _dlGen = deployGeneration;
                _dlSeen = new System.Collections.Generic.HashSet<int>();
            }
            if (chunkIndex >= 0) _dlSeen.Add(chunkIndex);
            float frac = chunkCount > 0 ? (float)_dlSeen.Count / chunkCount : 1f;
            EnsureCurtain("Downloading mission…");
            NativeWidgetFactory.SetDownloadBar(frac);
        }

        /// <summary>CLIENT: a single-envelope deploy arrived (no chunking) → stage-2 relabel + bar to 1.0.
        /// Shows the curtain here too if the stage-1 ping was missed (same guard).</summary>
        public static void ClientOnDeploySingle()
        {
            if (!ClientGuardOpen()) return;
            EnsureCurtain("Downloading mission…");
            NativeWidgetFactory.SetDownloadBar(1f);
        }

        /// <summary>CLIENT stage-3 HAND-OFF: the client kicked its OWN native level load, so stop driving our
        /// bar and let the native level-load bar + default label take over the SAME curtain (do NOT lift it).
        /// The SaveTransferCoordinator.SetLoadingLevel idiom. No-op if our curtain was never up.</summary>
        public static void ClientHandoff()
        {
            if (!_curtainUp) return;
            _curtainUp = false;
            ResetDownloadTracking();
            MultiplayerUI.Instance?.TacLoadHandoff();   // EndDownloadBar only — native load lifts the curtain
        }

        /// <summary>CLIENT: tear the curtain DOWN and lift it (no native load will) — the hydrate-existing path
        /// (already in tactical), the watchdog, and session/mission teardown. Never leaves a stuck curtain.</summary>
        public static void ClientAbortCurtain(string reason)
        {
            if (!_curtainUp) { ResetDownloadTracking(); return; }
            _curtainUp = false;
            ResetDownloadTracking();
            Debug.LogWarning("[Multiplayer][tac] lifting co-op loading curtain (" + reason + ")");
            MultiplayerUI.Instance?.TacLoadAbort();     // EndDownloadBar + LiftCurtain
        }

        // Guard: only a synced-session CLIENT that is NOT mid-live-battle and NOT inside a SaveTransferCoordinator
        // transfer may show our curtain (that flow owns the download feedback — we must never double-drive it).
        private static bool ClientGuardOpen()
        {
            var e = NetworkEngine.Instance;
            if (e == null || !e.IsActive || e.IsHost) return false;
            if (TacticalDeploySync.IsClientMirroring) return false;   // don't cover a live mirrored battle
            return !SaveTransferActive(e);
        }

        private static bool SaveTransferActive(NetworkEngine e)
        {
            var c = e.SaveTransfer;
            return c != null && (c.TransferActive || c.InPhase2);
        }

        private static void EnsureCurtain(string label)
        {
            if (!_curtainUp)
            {
                MultiplayerUI.Instance?.EnterTacLoadCurtain(label);
                _curtainUp = true;
            }
            else
            {
                NativeWidgetFactory.SetCurtainLabel(label);   // stage-1 → stage-2 relabel (early-outs if unchanged)
            }
            _lastActivity = Now;
        }

        private static void ResetDownloadTracking()
        {
            _dlGen = int.MinValue;
            _dlSeen = new System.Collections.Generic.HashSet<int>();
        }

        private static void ClientTick()
        {
            if (!_curtainUp) return;
            var e = NetworkEngine.Instance;
            if (e == null || !e.IsActive) { ClientAbortCurtain("session ended"); return; }
            if (SaveTransferActive(e))
            {
                // SaveTransferCoordinator took over the download feedback → hand the SAME native curtain to it
                // (its OnSaveChunk re-begins the bar with its own label). Don't fight it, don't watchdog it.
                _curtainUp = false;
                ResetDownloadTracking();
                return;
            }
            if (Now - _lastActivity > WatchdogSeconds)
                ClientAbortCurtain("watchdog: no host progress for " + (int)WatchdogSeconds + "s");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Per-frame driver for BOTH sides (from <c>MultiplayerUI.Update</c>). Cheap: each side
        /// early-outs when idle. Runs regardless of session state so a disconnect mid-load tears the curtain down.</summary>
        public static void Tick()
        {
            HostTick();
            ClientTick();
        }

        /// <summary>Reset at a mission boundary (<c>TacticalDeploySync.OnMissionExit</c>): stop any host
        /// heartbeat and lift any lingering client curtain. Idempotent.</summary>
        public static void Reset()
        {
            _hostLoading = false;
            ClientAbortCurtain("mission boundary");
        }

        private static float Now => Time.realtimeSinceStartup;
    }
}
