using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Platforms;
using Base.Serialization;
using Base.Utils;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Transport;
using Multiplayer.UI;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Common.Saves;
using UnityEngine;

namespace Multiplayer.Network
{
    /// <summary>
    /// Owns the session-start save transfer + LOADED/BEGIN barrier (foundation #1, Phase B).
    ///
    /// Flow (host = coordinator):
    ///   HOST  : serialize current savegame → byte[] (SerializationComponent.ReadSavegameBinary),
    ///           chunk it into SaveChunk msgs, then SaveDone (size + crc32). Host prepares its own
    ///           LoadLevelGameResult locally and waits at the barrier.
    ///   CLIENT: reassemble chunks by offset, verify total + crc32 on SaveDone, build the loaded
    ///           scene binding IN MEMORY (mirrors PhoenixSaveManager.LoadCurrentGeoscape), then send
    ///           ClientLoaded. It does NOT enter the level — FinishLevel is deferred until BEGIN.
    ///   BARRIER: host collects ClientLoaded from every connected peer (or kicks on timeout), then
    ///            broadcasts SessionBegin. On BEGIN every peer (host + clients) calls FinishLevel with
    ///            its prepared LoadLevelGameResult at the same instant → simultaneous entry.
    ///
    /// All game-save types are referenced directly (Assembly-CSharp is a compile-time reference) and
    /// were verified against the decompile: SerializationComponent.ReadSavegameBinary (cs:280),
    /// PhoenixSaveManager.LoadCurrentGeoscape in-memory pattern (cs:380-398), PhoenixGame.FinishLevel
    /// (cs:263). Coroutines are driven through Timing.Start exactly like the vanilla load path
    /// (UIModuleSaveGame.cs:170 / UIModuleMainMenuButtons.cs:241).
    /// </summary>
    public class SaveTransferCoordinator
    {
        // Chunk size. 32 KB is safe on the two RELIABLE transports: SteamTransport reliable P2P allows
        // ~1 MB and DirectTransport is length-prefixed TCP (unbounded). It is NOT a single datagram on
        // StunTransport: that path sends one raw UDP packet per message (UdpClient.Send,
        // StunTransport.cs:322), so a 32 KB payload is split by IP into ~22 fragments, and Stun has no
        // sequencing/ACK/retransmit — a single lost fragment drops the whole chunk unrecoverably. The
        // Stun/WAN path is therefore best-effort only (see HostStartSession warning); reliable
        // save-transfer is supported on Steam and DirectIP. Reducing ChunkSize below the path MTU would
        // not make Stun reliable without an ACK/retransmit layer (out of foundation scope), so we keep
        // 32 KB and document the limitation rather than branch the chunk size per transport.
        public const int ChunkSize = 32 * 1024;

        // Phase-1 (LOADED barrier) timeout: time the host waits, after the barrier opens, for every
        // connected peer to download + prepare its save (ack LOADED) before kicking the stragglers and
        // beginning with whoever is ready. This window covers the chunked transfer AND the in-memory
        // PrepareEntryFromBlobCrt (metadata/level-param read of a 1–5 MB geoscape blob), which on a slow
        // disk can take well over a minute — so it is deliberately generous (3 min). It does NOT cover
        // the native world-load (that is phase-2). Splitting this off the old single 60 s constant
        // stops a slow-but-healthy client being kicked mid-prepare (review fix #3).
        private const long Phase1LoadTimeoutMs = 180_000;

        // Phase-2 reveal deadline: how long the held (opaque) overlay stays up — i.e. how long both
        // reveal fallbacks wait for every peer to finish its NATIVE world-load and report done — before
        // forcing the synchronized reveal anyway. Used by BOTH fallbacks: the host forced-reveal
        // (a peer errored / never reported done) and the per-peer self-reveal (RevealAll never arrived,
        // dead host). A 1–5 MB geoscape world-load on an HDD can exceed 60 s, so under the old single
        // 60 s constant a healthy-but-slow client got force-revealed MID-LOAD (black globe). 3 min gives
        // a real world-load room to finish while still bounding a genuinely stuck peer (review fix #3).
        private const long RevealDeadlineMs = 180_000;

        private readonly NetworkEngine _engine;

        // ─── Host transfer/barrier state ──────────────────────────────────
        private Guid _transferId;
        private bool _barrierOpen;
        private long _barrierOpenedAtMs;
        // CLIENT loaded-acks only — keyed by the authoritative transport sender id (msg.SenderSteamId).
        // The host's OWN loaded-state is tracked separately in _hostLoaded (NOT added here): on
        // DirectIP/no-Steam LocalSteamId==0, so if the transport ever handed a client peerId 0 its ack
        // would collide with a host self-entry in this set → barrier under-counts → 60 s stall. Keeping
        // the host out of this set makes host and client entries structurally un-collidable (fix #2).
        private readonly HashSet<ulong> _loadedPeers = new HashSet<ulong>();
        // The host's own loaded-state, tracked under a dedicated flag instead of an id key in
        // _loadedPeers — see above. Set when the host finishes preparing its own entry, reset in
        // OpenBarrier.
        private bool _hostLoaded;

        // ─── Client reassembly state ──────────────────────────────────────
        private Guid _rxTransferId;
        private long _rxTotalBytes;
        private byte[] _rxBuffer;
        private long _rxReceived;
        private int _lastReportedDownloadPct = -1;
        // The id of the transfer the client has already FINISHED (SaveDone processed). A late chunk of
        // that transfer arriving AFTER a new F2 transfer began must be ignored — otherwise it would
        // re-enter the first-chunk branch (its id != the new _rxTransferId) and reset _begun/buffers
        // mid-new-download. Set on completion in OnSaveDone; checked at the top of OnSaveChunk.
        private Guid _completedTransferId;
        // Coverage tracking so reassembly is idempotent to duplicate / out-of-order chunks.
        // StunTransport duplicates every reliable packet (StunTransport.cs:130), so a chunk can
        // arrive more than once; copying into _rxBuffer is already idempotent, but a running byte
        // counter would over-count. We instead track which chunk indices have been covered and
        // declare the blob complete only when every index is present (+ CRC matches).
        private bool[] _rxChunkSeen;
        private int _rxChunksRemaining;

        // ─── Per-peer prepared entry point (host + client) ────────────────
        // The loaded scene to enter; built before the barrier, consumed on BEGIN.
        private LoadLevelGameResult _pendingResult;
        private bool _begun;

        // ─── Per-peer download progress (host view) ───────────────────────
        // Keyed by the authoritative transport sender id (msg.SenderSteamId), so it lines up with the
        // roster's client SteamIds and is reliable even when LocalSteamId collides on DirectIP.
        private readonly Dictionary<ulong, int> _peerDownloadPct = new Dictionary<ulong, int>();

        // ─── Co-op load overlay state ─────────────────────────────────────
        // Host aggregate: per-slot (phase, percent), keyed by host-assigned slotIndex (never the
        // transport peer id). The host serializes this into the RosterProgress snapshot each tick.
        private readonly Dictionary<byte, (byte phase, byte percent)> _slotProgress
            = new Dictionary<byte, (byte, byte)>();
        // Shared receiver-side view (host + every client): monotonic-max merge + event-driven done-set.
        private readonly RosterProgressTracker _tracker = new RosterProgressTracker();
        private long _lastSnapshotMs = -1;
        private const long SnapshotIntervalMs = 50; // ≈20 Hz — re-broadcast the smooth real fillAmount frequently
        private bool _loadCompleteSent;
        // Phase-2 native-load driver state (moved here from LoadOverlayController): last percent this
        // peer reported, so the session-scoped pump throttles to whole-percent steps and detects the
        // load finishing (LoadingProgress→null) independently of overlay visibility. -1 = not reporting.
        private int _lastReportedLoadPct = -1;
        // The Level instance currently loading. Captured from CurtainShowPatch's OnLevelStateChanged
        // hook (newState==Loading), cleared on Playing/Loaded. The phase-2 pump reads .LoadingProgress
        // off THIS, NOT GameUtl.CurrentLevel(): during a geoscape load CurrentLevel() is null (the old
        // level is cleared at Game.cs:191, the new one assigned only at Game.cs:211 AFTER LoadCrt), so
        // the pump would read null every frame and never report phase-1 progress. The loading Level's
        // LoadingProgress is non-null from load-start (Level.cs:137) until done (Level.cs:149→null).
        private Base.Levels.Level _loadingLevel;
        // The LIVE native ProgressBarController component for the bar currently on screen. Its
        // ProgressFill.fillAmount is the REAL eased on-screen value (the game eases it toward the
        // coarse LoadingProgress.Progress). Captured once when phase-2 begins (SetLoadingLevel with a
        // non-null level), cleared on Playing/Loaded + OpenBarrier. The pump prefers this over the
        // coarse lp.Progress so peers see the same smooth bar the source player sees.
        private UnityEngine.Component _liveProgressBar;
        // True from Begin() (barrier closes, phase-2 world-load starts) until the roster is all-done.
        // Keeps the host's RosterProgress snapshot broadcast alive through phase-2: _barrierOpen is
        // cleared in Begin() BEFORE FinishLevel runs phase-2, so without this every peer's tracker
        // would freeze at the phase-1 value. Does NOT re-block FinishLevel (the Harmony gate keys on
        // IsBarrierPending, not _barrierOpen). Cleared on all-done and reset in OpenBarrier.
        private bool _loadPhaseActive;

        // ─── Second barrier: synchronized geoscape reveal (BUG D) ─────────
        // The native curtain auto-lifts on Loaded→Playing; the mod overlay (opaque) is held as the
        // real synchronized cover and dropped together via RevealAll. _reachedPlaying: this peer hit
        // Playing (CurtainShowPatch); _revealHoldStartedMs: when the hold began (self-reveal fallback
        // baseline); _phase2DeadlineMs: host forced-reveal deadline; _revealed: lift performed once;
        // _revealAllSent: host broadcast-once guard.
        private bool _reachedPlaying;
        private long _revealHoldStartedMs;
        private long _phase2DeadlineMs;
        private bool _revealed;
        private bool _revealAllSent;
        // Batch 2 (entry-via-save): the host is holding its loading screen for a tactical-ENTRY transfer.
        // Set at LAUNCH (OpenTacticalEntryBarrier), cleared at reveal (PerformDeferredLift). Unlike F2/lobby
        // the host stays in its already-live tactical level (no self-enter), so _begun is NOT reset to false
        // on this path (keeps SessionStarted true → curtain hold engages + mid-tactical F2 still works). This
        // flag lets Begin() still fire (broadcast SessionBegin for the client) despite _begun being true.
        private bool _hostEntryHold;

        // ─── rca-4: host post-reload full re-seed (once per F2 mid-session reload) ───
        // Armed ONLY when HostStartSessionInGame actually launches a reload transfer; consumed ONCE at the
        // RevealAll moment (HostReseedAfterReveal). The lobby FIRST start never arms it — the transferred
        // save itself is the seed there. Pure once-latch (Core), pinned by SaveTransferBarrierTests.
        private readonly ReseedOnceGate _reseedGate = new ReseedOnceGate();

        // ─── P1 mid-session on-demand joiner (CLIENT side) ─────────────────
        // True on a brand-new peer that joined AFTER the session started and is being onboarded via the
        // per-peer on-demand transfer (SaveDone.onDemandJoin). Such a joiner does NOT wait for a lobby
        // BEGIN and does NOT hold for a co-op RevealAll — it enters the level as soon as its blob is
        // prepared and reveals natively when its own load finishes (there is no simultaneity to honour;
        // the already-connected peers are long past their reveal). Set in ClientLoadCrt for a join
        // transfer, reset per-transfer in OnSaveChunk's first-chunk branch. Host is never a joiner.
        private bool _onDemandJoiner;

        // ─── Client save-download native loading-screen driver ─────────────
        // True from the first received save chunk until the download hands off to the real level-load
        // (phase-2, SetLoadingLevel captures a loading Level) or aborts. While set, the per-frame Update
        // drives the NATIVE bottom bar with the download fraction (via NativeWidgetFactory), so the
        // client sees the game's own loading screen during the WAN transfer instead of the lobby. Never
        // set on the host (OnSaveChunk is client-only).
        private bool _downloadCurtain;

        /// <summary>Shared receiver-side roster progress for the overlay UI.</summary>
        public RosterProgressTracker Tracker => _tracker;

        public SaveTransferCoordinator(NetworkEngine engine)
        {
            _engine = engine;
            // Fix #1: react to a peer dropping mid-load. We subscribe to the EXISTING id-only disconnect
            // event NetworkEngine already exposes (Action<ulong> peerId) — the same one HostLeaveHandler
            // (named variant) and SessionNotifier ride. It fires in NetworkEngine.OnPeerDisconnected
            // AFTER Session.RemoveClient(peerId), so by the time our handler runs the dropped peer is
            // already out of GetConnectedClients()/GetRosterSlots() — the expected count self-corrects.
            if (_engine != null)
                _engine.OnClientDisconnected += OnPeerDisconnectedDuringLoad;
        }

        // Fix #1: unsubscribe so a coordinator instance does not leak on the engine's long-lived event.
        // NetworkEngine re-creates this coordinator each Initialize() (and nulls it on Shutdown/TearDown)
        // WITHOUT calling Detach, so we ALSO self-detach defensively inside the handler (see there); this
        // public hook lets a future engine teardown drop the subscription explicitly.
        public void Detach()
        {
            if (_engine != null)
                _engine.OnClientDisconnected -= OnPeerDisconnectedDuringLoad;
        }

        // Fix #1: a peer dropped. If it drops AFTER OpenBarrier but BEFORE its LOADED ack, the barrier
        // would otherwise wait out the full phase-1 timeout. Remove any stale loaded-entry for the gone
        // peer and re-evaluate release immediately. Session.RemoveClient already ran (event ordering), so
        // GetConnectedClients()/GetRosterSlots() no longer count this peer → expected drops by one and the
        // phase-2 reveal's AllDone(GetRosterSlots()) no longer waits on it. No tracker mutation needed:
        // AllDone only consults REQUIRED (remaining) slots, and the dropped slot is already gone.
        private void OnPeerDisconnectedDuringLoad(ulong peerId)
        {
            // Self-detach guard: NetworkEngine replaces this coordinator on re-Initialize without calling
            // Detach, so a stale instance could still be subscribed. If we are no longer the live
            // coordinator, unsubscribe and bail — never act on behalf of a dead session.
            if (_engine == null || !ReferenceEquals(_engine.SaveTransfer, this))
            {
                if (_engine != null)
                    _engine.OnClientDisconnected -= OnPeerDisconnectedDuringLoad;
                return;
            }
            // _loadedPeers holds client ids only; the host (_hostLoaded) is never keyed by id, so this
            // can never drop the host. Removing a not-yet-loaded peer is a harmless no-op.
            _loadedPeers.Remove(peerId);
            // Phase-1: release now if the remaining connected peers are all loaded (no-op once begun).
            TryReleaseBarrier();
        }

        /// <summary>
        /// Host: drop every per-peer trace of a STALE peer id (Inc5 part 2 — returning-peer rejoin
        /// prune, SessionManager.HandleConnectionRequest). Mirrors <see cref="OnPeerDisconnectedDuringLoad"/>
        /// for a death the transport never reported: the dead id's download-progress row and any LOADED
        /// ack are residue of the old connection. The caller removes the peer from the roster BEFORE
        /// calling this, so a barrier now releasable with the remaining peers is re-evaluated here
        /// (TryReleaseBarrier self-guards — no-op when no barrier is open). Idempotent; the returning
        /// peer's NEW connection re-registers under its own (possibly identical) id from scratch.
        /// </summary>
        public void ForgetPeer(ulong peerId)
        {
            _peerDownloadPct.Remove(peerId);
            _loadedPeers.Remove(peerId);
            TryReleaseBarrier();
        }

        /// <summary>True while a peer has a save prepared but must wait for BEGIN before entering.</summary>
        public bool IsBarrierPending => _pendingResult != null && !_begun;

        /// <summary>True once BEGIN has released this peer into the level (session has started).</summary>
        public bool SessionStarted => _begun;

        /// <summary>True once the deferred reveal (native LiftCurtain + overlay hide) has run; used by
        /// CurtainShowPatch.Prefix so a later Loaded→Playing after RevealAll is NOT suppressed.</summary>
        public bool Revealed => _revealed;

        /// <summary>True while this peer is in phase-2 native world-load (begun, not yet done).</summary>
        public bool InPhase2 => RosterProgressTracker.InPhase2(_begun, _loadCompleteSent);

        /// <summary>
        /// True once the NATIVE loading curtain has actually entered "Loading" (mission load-start) and
        /// not yet handed off to Playing/Loaded — i.e. a loading Level is captured (<see cref="_loadingLevel"/>).
        /// This is the real "mission loading started" seam, distinct from <see cref="TransferActive"/>
        /// which goes true at COMMAND time (barrier open / download) while the host is still in the lobby.
        /// The overlay visibility gate keys on THIS so the load overlay never pops up in the lobby on the
        /// PLAY press — only when the curtain drops for the load. Set/cleared exclusively via
        /// <see cref="SetLoadingLevel"/> from CurtainShowPatch (Loading→non-null, Playing/Loaded→null).
        /// </summary>
        public bool LoadPhaseStarted => _loadingLevel != null;

        /// <summary>
        /// CurtainShowPatch passes the loading Level on Loading (capture) and null on Playing/Loaded
        /// (clear). The phase-2 pump reads progress off this — see <see cref="_loadingLevel"/>.
        /// Typed object so the patch needs no hard Level ref; unboxed to Base.Levels.Level here.
        /// </summary>
        public void SetLoadingLevel(object level)
        {
            _loadingLevel = level as Base.Levels.Level;
            // Capture the LIVE native bar when phase-2 begins (Loading), clear it on Playing/Loaded.
            // Done ONCE here (not per-frame) so the pump never FindObjectOfType's every tick.
            _liveProgressBar = _loadingLevel != null
                ? Multiplayer.UI.NativeWidgetFactory.CaptureLiveProgressBar()
                : null;

            // Download → level-load hand-off: the real load just started, so the native path already
            // reassigned the bottom bar's source (SceneFadeController.DropCurtainInstant(level) →
            // ProgressBar.SetLoadingLevel). Stop our download driver + restore the native loading label
            // so phase-2 shows the level-load progress with the native text. Client-only (host never
            // set _downloadCurtain).
            if (_loadingLevel != null && _downloadCurtain)
            {
                _downloadCurtain = false;
                Multiplayer.UI.NativeWidgetFactory.EndDownloadBar();
            }
        }

        // Never-silent: the download failed while the native loading screen was up (bad blob / checksum /
        // prepare fail). Clear the download driver and hand off to the UI, which lifts the curtain + shows
        // the staged failure dialog so the client is not stranded on a stuck bar. No-op if no curtain was up.
        private void AbortDownloadCurtain(string stage)
        {
            if (!_downloadCurtain) return;
            _downloadCurtain = false;
            Multiplayer.UI.NativeWidgetFactory.EndDownloadBar();
            Multiplayer.UI.MultiplayerUI.Instance?.OnClientTransferFailed(stage);
        }

        /// <summary>
        /// True while a save transfer/load is in flight: the host has opened the barrier, or this
        /// client is mid-download / has a prepared save awaiting BEGIN. Used to gate progress display.
        /// </summary>
        public bool TransferActive =>
            (_engine.IsHost && _barrierOpen) || _rxTotalBytes > 0 || IsBarrierPending;

        /// <summary>
        /// FIX-3: true only on a CLIENT that is actively receiving a save blob (mid-download, before the
        /// curtain "Loading" and phase-2 world-load). Drives the load overlay's DOWNLOAD-phase visibility
        /// so a slow WAN save download isn't a blank screen. False on the host (it holds the blob locally)
        /// and false once the blob is fully received (ResetRx clears _rxTotalBytes at SaveDone).
        /// </summary>
        public bool IsDownloading => !_engine.IsHost && _rxTotalBytes > 0;

        /// <summary>This peer's own download percent (0..100), or -1 when not downloading.</summary>
        public int LocalDownloadPercent
        {
            get
            {
                if (_engine.IsHost) return 100;            // host has the blob locally; no download
                if (_rxTotalBytes <= 0) return -1;
                return (int)(100L * _rxReceived / _rxTotalBytes);
            }
        }

        /// <summary>Host view of a connected client's last-reported download percent (0..100).</summary>
        public bool TryGetPeerDownloadPercent(ulong peerKey, out int pct)
            => _peerDownloadPct.TryGetValue(peerKey, out pct);

        // ══════════════════════════════════════════════════════════════════
        //  HOST: start the session — serialize + send the save, open the barrier
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Begin the host→clients session start. Returns true iff the start is now IN FLIGHT
        /// (serialize+send coroutine launched); returns false on every abort path (non-host, no save,
        /// start gate closed, or a downstream game/timing failure) so the caller can reopen a lobby it
        /// already locked via LobbyController.CommitStart() — never leaving it permanently dead-locked.
        /// </summary>
        public bool HostStartSession(SavegameMetaData chosen)
        {
            if (!_engine.IsHost)
            {
                Debug.LogWarning("[Multiplayer] HostStartSession called on a non-host peer; ignored.");
                return false;
            }

            if (chosen == null)
            {
                Debug.LogError("[Multiplayer] HostStartSession called with no chosen save; aborting.");
                return false;
            }

            // Defense-in-depth (Bug B): the lobby start gate must also hold HERE, not only in the
            // caller — no path may start a session while the host is alone or any client is un-ready.
            // We re-derive the gate from the authoritative roster (host self-entry excluded).
            var roster = _engine.Session?.GetLobbyRoster();
            int clientCount = _engine.Session?.ClientCount ?? 0;
            bool gateOpen = clientCount >= 1 && AllClientsReadyRoster(roster);
            if (!gateOpen)
            {
                Debug.LogWarning("[Multiplayer] HostStartSession blocked: start gate closed " +
                    $"(clients={clientCount}, allReady={AllClientsReadyRoster(roster)}); ignoring start.");
                return false;
            }

            return LaunchTransfer(chosen);
        }

        /// <summary>
        /// F2 mid-session host load: re-run the EXACT same chunked transfer + 2-phase barrier as the
        /// lobby start, but gated by the IN-GAME guard instead of the lobby ready-gate (mid-session
        /// there is no lobby "Ready" state — clients follow the host-authoritative load unconditionally).
        /// The guard (host / active session / already-started / >=1 client / no transfer in flight) is
        /// re-validated here as defense-in-depth; the transfer machinery itself is reused VERBATIM.
        /// </summary>
        public bool HostStartSessionInGame(SavegameMetaData chosen)
        {
            if (!_engine.IsHost)
            {
                Debug.LogWarning("[Multiplayer] HostStartSessionInGame called on a non-host peer; ignored.");
                return false;
            }

            if (chosen == null)
            {
                Debug.LogError("[Multiplayer] HostStartSessionInGame called with no chosen save; aborting.");
                return false;
            }

            bool gateOpen = SessionLifecycle.HostLoadGuard(
                isHost: _engine.IsHost,
                isActiveSession: _engine.IsActiveSession,
                sessionStarted: SessionStarted,
                connectedClientCount: _engine.Session?.ClientCount ?? 0,
                transferActive: TransferActive);
            if (!gateOpen)
            {
                Debug.LogWarning("[Multiplayer] HostStartSessionInGame blocked: in-game load guard closed " +
                    $"(clients={_engine.Session?.ClientCount ?? 0}, started={SessionStarted}, " +
                    $"transferActive={TransferActive}); ignoring load.");
                return false;
            }

            // A mid-session re-transfer reuses the SAME barrier/reveal state machine — clear the prior
            // run's terminal flags so the second transfer's OpenBarrier/Begin/reveal run clean.
            _begun = false;
            _loadCompleteSent = false;
            _revealAllSent = false;
            bool launched = LaunchTransfer(chosen);
            // rca-4: arm the post-reload full re-seed for THIS reload only when the transfer is actually in
            // flight; it is consumed once at the RevealAll moment (HostReseedAfterReveal). Channels converge
            // only lazily on the next dirty-mark after a reload, so any host state the save-load itself did
            // not carry perfectly would otherwise stay stale on clients until then.
            if (launched) _reseedGate.Arm();
            return launched;
        }

        // Shared launch tail for both the lobby start and the mid-session load: warn on the best-effort
        // Stun path, resolve game + timing, then kick the serialize+send+barrier coroutine. Callers own
        // the guard (lobby ready-gate vs in-game guard); this is guard-free.
        private bool LaunchTransfer(SavegameMetaData chosen)
        {
            Debug.Log($"[Multiplayer] LaunchTransfer: transport={_engine.Transport?.TransportType} save={chosen?.Name}");

            // Honest-scope limitation: reliable save-transfer is supported on Steam (reliable P2P) and
            // DirectIP (length-prefixed TCP). The Stun/WAN path sends raw UDP with no sequencing/ACK/
            // retransmit, so 32 KB chunks fragment at the IP layer and any lost fragment fails the
            // transfer. Warn once at start; do not change Steam/Direct behaviour.
            if (_engine.Transport != null && _engine.Transport.TransportType == TransportType.StunUDP)
            {
                Debug.LogWarning("[Multiplayer] Save transfer over the Stun/WAN (UDP) transport is " +
                                 "BEST-EFFORT only: chunks fragment over UDP with no retransmit, so the " +
                                 "transfer may fail on packet loss. Reliable transfer is supported on " +
                                 "Steam and DirectIP.");
            }

            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return false;

            var timing = GetTiming();
            if (timing == null) return false;

            _transferId = Guid.NewGuid();
            timing.Start(HostSerializeAndSendCrt(game, chosen));
            return true;
        }

        // Start-gate helper: project the roster to non-host ready flags and delegate to the ONE shared
        // rule (LobbyController.AllClientsReady) — >=1 NON-host peer AND every non-host peer ready. The
        // host self-entry is the starter, not a ready-gated player, so it is excluded. Single source of
        // truth shared with the lobby VISUAL + press-time guard so no copy can drift.
        private static bool AllClientsReadyRoster(List<PeerListEntry> roster)
        {
            if (roster == null) return false;
            var nonHostReady = new List<bool>();
            foreach (var p in roster)
                if (!p.IsHost) nonHostReady.Add(p.Ready);
            return LobbyController.AllClientsReady(nonHostReady);
        }

        // Coroutine: read the save to bytes, then chunk+send, then prepare host entry + open barrier.
        private IEnumerator<NextUpdate> HostSerializeAndSendCrt(PhoenixGame game, SavegameMetaData metaData)
        {
            var result = new ByRef<byte[]>();
            yield return Timing.Current.Call(game.SaveManager.Serializer.ReadSavegameBinary(metaData, result));

            var blob = result.Value;
            if (blob == null || blob.Length == 0)
            {
                Debug.LogError("[Multiplayer] Save serialization produced no bytes; aborting transfer.");
                yield break;
            }

            var ext = System.IO.Path.GetExtension(metaData.Path);
            if (string.IsNullOrEmpty(ext)) ext = SerializationComponent.DefaultExtension;

            SendBlob(blob, ext);

            // Host prepares its own entry from the SAME bytes (in memory), then waits at the barrier.
            yield return Timing.Current.Call(PrepareEntryFromBlobCrt(game, blob, ext));

            OpenBarrier();
            // Host counts as loaded immediately — under the dedicated sentinel flag, NOT an id key in
            // _loadedPeers, so it can never collide with a peerId-0 client ack on DirectIP (fix #2).
            _hostLoaded = true;
            TryReleaseBarrier();
        }

        // Split the blob into SaveChunk messages (sequence by offset), then a SaveDone with crc32.
        private void SendBlob(byte[] blob, string ext)
        {
            var crc = Crc32(blob);
            var chunkCount = (int)((blob.Length + ChunkSize - 1) / ChunkSize);
            Debug.Log($"[Multiplayer] SendBlob: bytes={blob.Length} chunks={chunkCount} crc=0x{crc:X8}");
            SendBlobCore(blob, ext, _transferId, crc, onDemandJoin: false, m => _engine.BroadcastToAll(m));
            Debug.Log("[Multiplayer] SendBlob: all chunks + SaveDone broadcast sent");
        }

        // Shared chunking loop for the broadcast (SendBlob) and unicast (SendBlobTo) transfers: split blob
        // into SaveChunk messages (sequenced by offset) then a SaveDone(crc), routing each through `send`.
        private void SendBlobCore(byte[] blob, string ext, Guid transferId, uint crc, bool onDemandJoin, Action<NetworkMessage> send)
        {
            long offset = 0;
            while (offset < blob.Length)
            {
                var len = (int)Math.Min(ChunkSize, blob.Length - offset);
                var chunk = new byte[len];
                Array.Copy(blob, offset, chunk, 0, len);

                var msg = new SaveChunkMessage
                {
                    TransferId = transferId,
                    TotalBytes = blob.Length,
                    Offset = offset,
                    Chunk = chunk
                };
                var payload = MessageSerializer.SerializeSaveChunk(msg);
                send(new NetworkMessage(PacketType.SaveChunk, payload));
                offset += len;
            }

            var donePayload = MessageSerializer.SerializeSaveDone(transferId, blob.Length, ext, crc, onDemandJoin);
            send(new NetworkMessage(PacketType.SaveDone, donePayload));
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOST: Batch-1 tactical mission ENTRY via mid-tactical save transfer
        //  Ship a byte-identical mid-tactical save so a client BUILDS its battle from the host's exact
        //  state (positions/loot/objectives/turn) instead of self-launching + reconciling. Reuses the
        //  F2/lobby machinery VERBATIM (SendBlob + OpenBarrier + LOADED/BEGIN barrier + client load path);
        //  the ONLY difference from HostSerializeAndSendCrt is (a) a tactical-safe writer instead of
        //  ReadSavegameBinary(chosenMeta), and (b) the host does NOT re-enter from the blob — it is already
        //  live in this tactical level.
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Dedicated transient name for the host-only mid-tactical transfer save. NOT "autosave"/
        /// "quicksave" (never clobber a user save); deleted immediately after read-back.</summary>
        public const string TacticalTransferSaveName = "coop_tac_xfer";

        /// <summary>
        /// HOST (entry-via-save, Batch 1): at deploy-ready, write a byte-identical mid-tactical save and
        /// ship it over the SAME chunked transfer + LOADED/BEGIN barrier the F2 reload uses, so the client
        /// builds its tactical level from the host's exact bytes. Self-gated (flag + host + tactical + no
        /// transfer in flight, via <see cref="Sync.Tactical.TacticalEntryTransferGate"/>). Unlike F2/lobby
        /// the host does NOT re-enter from the blob (it is already in this live tactical level). Returns
        /// true iff the write+send coroutine launched.
        /// </summary>
        public bool HostBeginTacticalEntryTransfer()
        {
            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return false;

            bool go = Multiplayer.Sync.Tactical.TacticalEntryTransferGate.ShouldSendTacticalSave(
                isHost: _engine.IsHost,
                sessionActive: _engine.IsActiveSession,
                isTactical: saveManager.IsTactical,
                transferActive: TransferActive,
                flagOn: Multiplayer.Sync.Tactical.TacticalDeploySync.UseSaveTransferEntry);
            if (!go)
            {
                Debug.LogWarning("[Multiplayer] HostBeginTacticalEntryTransfer blocked: gate closed " +
                    $"(host={_engine.IsHost}, session={_engine.IsActiveSession}, tactical={saveManager.IsTactical}, " +
                    $"transferActive={TransferActive}, flag={Multiplayer.Sync.Tactical.TacticalDeploySync.UseSaveTransferEntry}).");
                return false;
            }

            var timing = GetTiming();
            if (timing == null) return false;

            // Fresh transfer run. NB: unlike F2/lobby we do NOT reset _begun=false here — the host stays in
            // its already-live tactical level (it never re-enters), and the reveal-hold armed at LAUNCH
            // (OpenTacticalEntryBarrier) needs SessionStarted (_begun) to STAY true so the curtain keeps
            // holding until every client loads. Begin() still fires for the client via the _hostEntryHold
            // relaxation. The coroutine's OpenBarrier resets the rest of the LOADED-barrier state per run.
            _loadCompleteSent = false;
            _revealAllSent = false;
            _transferId = Guid.NewGuid();
            timing.Start(HostTacticalEntryTransferCrt(saveManager));
            return true;
        }

        // Coroutine (deploy-ready half): write the mid-tactical save → bytes, ship it, and OPEN THE LOADED
        // barrier (the chunk-transfer half). The reveal-HOLD was already armed at LAUNCH
        // (OpenTacticalEntryBarrier, Batch 2), so the host is already holding behind its native loading
        // screen; here it only opens the LOADED barrier for the client's download, marks itself loaded +
        // done, and never re-enters (no PrepareEntryFromBlobCrt / EnterLevel — it is already live in this
        // tactical level). The synchronized reveal then fires on AllDone once the client also finishes, or
        // via the forced/self-reveal fallbacks if the client dies mid-load.
        private IEnumerator<NextUpdate> HostTacticalEntryTransferCrt(PhoenixSaveManager saveManager)
        {
            var bytes = new ByRef<byte[]>();
            var t0 = NowMs();
            yield return Timing.Current.Call(HostWriteTacticalSaveCrt(saveManager, bytes));

            var blob = bytes.Value;
            if (blob == null || blob.Length == 0)
            {
                Debug.LogError("[Multiplayer] tac-entry: no mid-tactical save bytes produced; aborting entry transfer.");
                yield break;
            }
            Debug.Log($"[Multiplayer] tac-entry: host mid-tactical save written bytes={blob.Length} ms={NowMs() - t0}");

            SendBlob(blob, SerializationComponent.DefaultExtension);

            OpenBarrier();        // open the LOADED barrier (reveal-hold already armed at launch; _hostEntryHold untouched)
            _hostLoaded = true;   // host holds its state locally (already in the level) → counts as loaded
            SendLoadComplete();   // host is past Playing → mark its slot done (+ TryReleaseBarrier: client not loaded yet)
            Debug.Log("[Multiplayer] tac-entry: blob sent, LOADED barrier open, host marked loaded/done " +
                      "(reveal-hold armed at launch, no self-enter)");
        }

        // Write a mid-tactical save (QuickSave's tactical branch: IsTactical-tagged, TacticalGameParams.
        // GlobalTime; showCurtain:false so no save-curtain flash on the live host screen), read it back to
        // bytes via the game's CONFIGURED serializer (native SaveGame/ReadSavegameBinary — no manual
        // Serializer round-trip, respecting pp-serializer-context-and-pump), then delete the transient
        // host-only file. The metadata is built from PUBLIC SaveManager API exactly like the game's own
        // manual save (UIModuleSaveGame.NewSaveGame cs:190-208) — which works mid-tactical. SaveType.
        // ManualSave (NOT Quicksave/Autosave) so UpdateSpecialSaves never tracks it as a special save.
        private IEnumerator<NextUpdate> HostWriteTacticalSaveCrt(PhoenixSaveManager saveManager, ByRef<byte[]> outBytes)
        {
            outBytes.Value = null;

            string name = saveManager.EnsureUnique(TacticalTransferSaveName);
            // Tactical global time (QuickSave pattern). Metadata only — the battle state lives in the
            // serialized level, not the save's timestamp; default is harmless for a transient transfer.
            System.DateTime ingameTime = default;
            var tgp = GameUtl.CurrentLevel()?.LevelParams as TacticalGameParams;
            if (tgp != null) ingameTime = tgp.GlobalTime;

            var meta = new PPSavegameMetaData(
                name, saveManager.Serializer.SavegameVersion, saveManager.CurrentGameId, name, ingameTime, "",
                SaveType.ManualSave, saveManager.IsTactical, saveManager.CurrentDifficulty, saveManager.EnabledDlc);

            var written = new ByRef<bool>(value: false);
            var ex = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(
                saveManager.SaveGame(meta, SerializationComponent.DefaultExtension, written, showCurtain: false), ex);
            if (ex.Value != null || !written.Value)
            {
                Debug.LogError("[Multiplayer] tac-entry: mid-tactical save write failed: " +
                               (ex.Value != null ? ex.Value.Message : "written=false"));
                yield break;
            }

            // Read the just-written file back to bytes (WriteSavegame set meta.Path → ReadSavegameBinary(meta)
            // reads it — same read-back the on-demand join uses). CallSafe: a read throw must NOT escape and
            // skip the DeleteSaveGame below, or the transient coop_tac_xfer file leaks into the save list.
            var result = new ByRef<byte[]>();
            var readEx = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(saveManager.Serializer.ReadSavegameBinary(meta, result), readEx);
            if (readEx.Value != null)
                Debug.LogError("[Multiplayer] tac-entry: save read-back failed: " + readEx.Value.Message);
            else
                outBytes.Value = result.Value;

            // Transient host-only file — ALWAYS delete it (even if read-back failed) so it never litters the
            // player's save list.
            var delEx = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(saveManager.DeleteSaveGame(meta), delEx);
            if (delEx.Value != null)
                Debug.LogWarning("[Multiplayer] tac-entry: transient save delete failed: " + delEx.Value.Message);
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOST: P1 mid-session on-demand join — unicast the CURRENT state to ONE new peer
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Onboard a brand-new peer that connected AFTER the session started (P1 fix #2). Captures the
        /// host's CURRENT live state (autosave → read back to bytes) and UNICASTS it to just this peer,
        /// tagged as an on-demand join so the joiner enters + reveals on its own. Deliberately does NOT
        /// open the global LOADED barrier and does NOT reset any host counter/tracker — the already-
        /// connected peers are untouched by a join (invariant: existing clients see no re-transfer, no
        /// reset, no modal replay). The caller (SessionManager.HandleConnectionRequest) has already
        /// enforced the geoscape boundary; the guard is re-checked here as defense-in-depth. Returns true
        /// iff the capture+send coroutine launched.
        /// </summary>
        public bool HostOnDemandJoin(ulong peerId)
        {
            bool geoscape = Sync.GeoRuntime.Instance.IsGeoscapeActive;
            if (!SessionLifecycle.MidSessionJoinGuard(
                    isHost: _engine.IsHost,
                    sessionStarted: SessionStarted,
                    geoscapeActive: geoscape,
                    transferActive: TransferActive))
            {
                Debug.LogWarning($"[Multiplayer] HostOnDemandJoin({peerId}) blocked: guard closed " +
                    $"(host={_engine.IsHost}, started={SessionStarted}, geoscape={geoscape}, " +
                    $"transferActive={TransferActive}); the peer is not onboarded.");
                return false;
            }

            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return false;
            var timing = GetTiming();
            if (timing == null) return false;

            Debug.Log($"[Multiplayer] HostOnDemandJoin({peerId}): capturing current state → per-peer transfer.");
            timing.Start(HostOnDemandJoinCrt(peerId, saveManager));
            return true;
        }

        // Coroutine: autosave the CURRENT live state, read those bytes back, then unicast them to the joiner
        // tagged onDemandJoin. AutosaveGame (showCurtain:false) is the game's own state-capture path — it
        // does NOT flash the host screen, unlike QuickSave. On a fresh capture SaveManager.AutoSave is a NEW
        // metadata instance; if it did not advance (ironman substitutes an ironman save, or the write failed)
        // we abort + log rather than ship a stale blob (degrade-to-notify — the joiner must rejoin from a
        // manual save). Reuses ReadSavegameBinary exactly like the lobby/F2 HostSerializeAndSendCrt.
        private IEnumerator<NextUpdate> HostOnDemandJoinCrt(ulong peerId, PhoenixSaveManager saveManager)
        {
            var oldAutoSave = saveManager.AutoSave;
            var ex = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(saveManager.AutosaveGame(), ex);
            if (ex.Value != null)
            {
                Debug.LogError("[Multiplayer] HostOnDemandJoin: autosave capture failed: " + ex.Value.Message);
                yield break;
            }

            var meta = saveManager.AutoSave;
            if (!SessionLifecycle.FreshAutosaveCaptured(oldAutoSave, meta))
            {
                Debug.LogError("[Multiplayer] HostOnDemandJoin: no fresh autosave captured (ironman mode or a " +
                               "write failure) — cannot onboard the mid-session joiner; it must rejoin from a manual save.");
                yield break;
            }

            var result = new ByRef<byte[]>();
            yield return Timing.Current.Call(saveManager.Serializer.ReadSavegameBinary(meta, result));
            var blob = result.Value;
            if (blob == null || blob.Length == 0)
            {
                Debug.LogError("[Multiplayer] HostOnDemandJoin: captured save produced no bytes; aborting join transfer.");
                yield break;
            }

            var ext = System.IO.Path.GetExtension(meta.Path);
            if (string.IsNullOrEmpty(ext)) ext = SerializationComponent.DefaultExtension;

            var joinTransferId = Guid.NewGuid();
            SendBlobTo(peerId, blob, ext, joinTransferId, onDemandJoin: true);
            Debug.Log($"[Multiplayer] HostOnDemandJoin({peerId}): unicast current-state blob sent (bytes={blob.Length}).");
        }

        // Per-peer UNICAST variant of SendBlob (P1): split the blob into SaveChunk messages + a SaveDone,
        // addressed to ONE peer only (SendToClient), tagged onDemandJoin so the joiner enters immediately +
        // reveals natively. Uses an EXPLICIT transferId param (NOT the global barrier _transferId field) so a
        // join never disturbs the lobby/F2 barrier state or the already-connected peers.
        private void SendBlobTo(ulong peerId, byte[] blob, string ext, Guid transferId, bool onDemandJoin)
        {
            var crc = Crc32(blob);
            var chunkCount = (int)((blob.Length + ChunkSize - 1) / ChunkSize);
            Debug.Log($"[Multiplayer] SendBlobTo peer={peerId}: bytes={blob.Length} chunks={chunkCount} " +
                      $"crc=0x{crc:X8} join={onDemandJoin}");
            SendBlobCore(blob, ext, transferId, crc, onDemandJoin, m => _engine.SendToClient(peerId, m));
            Debug.Log($"[Multiplayer] SendBlobTo peer={peerId}: all chunks + SaveDone unicast sent");
        }

        /// <summary>
        /// Host: a mid-session on-demand joiner reached the live geoscape (JoinReady) → re-seed it with the
        /// current authoritative wallet + every state channel. Both are versioned ABSOLUTE snapshots, so the
        /// already-connected clients re-apply the same state idempotently (no modal replay, no reset) — the
        /// same convergence the lobby ready re-broadcast already relies on (SessionManager.SetClientReady).
        /// </summary>
        public void OnJoinReady(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            Debug.Log($"[Multiplayer] OnJoinReady from {msg.SenderSteamId} → re-seed wallet + channels.");
            // Rejoin belt (rca-3 audit b): the joiner's Steam id is STABLE, but its fresh engine restarts the
            // intent nonce counter at 1 — drop ITS old (peer, surface, nonce) dedup window so its first
            // post-join intents aren't eaten as "duplicates". Per-peer: every other client's window (and its
            // reliable double-send protection) stays intact. No-op for a first-time joiner.
            _engine.Sync?.ResetIntentDedupForPeer(msg.SenderSteamId);
            _engine.Sync?.BroadcastFullWallet();
            _engine.Sync?.BroadcastAllChannels();
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOST: P0 new-campaign co-op bootstrap — native new game runs to the first playable
        //  geoscape frame, then autosave + the SAME chunked transfer + 2-phase barrier as any start
        // ══════════════════════════════════════════════════════════════════

        // Pure single-shot latch (Core, pinned by NewCampaignBootstrapTests): armed at the native
        // new-game CONFIRM (NewCampaignInterceptPatch), consumed exactly once at the first playable
        // geoscape frame (CurtainShowPatch "Playing" seam → OnNewCampaignPlayableFrame).
        private readonly NewCampaignBootstrap _newCampaign = new NewCampaignBootstrap();

        /// <summary>True while a host new-campaign bootstrap is armed (native confirm ran, geoscape
        /// not reached yet). Read by NewCampaignInterceptPatch to force the tutorial OFF on the
        /// campaign being created (the bootstrap waits for a GEOSCAPE playable frame).</summary>
        public bool NewCampaignPending => _newCampaign.Armed;

        /// <summary>
        /// Arm the bootstrap: the HOST is creating a fresh campaign through the NATIVE new-game flow;
        /// when it reaches its first playable geoscape frame the coordinator autosaves and re-runs the
        /// EXISTING transfer + barrier so every client loads the byte-identical campaign start. The
        /// caller (NewCampaignInterceptPatch) owns the guard (NewCampaignArmGuard for the lobby start,
        /// the EXISTING HostLoadGuard for a mid-session second fresh campaign); this only latches and
        /// notifies the waiting clients over the existing chat rail. Re-arming is idempotent (TFTV's
        /// warning flow can re-invoke the native confirm) — the notice is sent once per arm edge.
        /// </summary>
        public void ArmNewCampaignBootstrap()
        {
            if (!_newCampaign.Armed)
                _engine.Session?.SystemChat(NewCampaignCreatingNotice);
            _newCampaign.Arm();
            Debug.Log("[Multiplayer] New-campaign co-op bootstrap ARMED — native campaign creation " +
                      "runs on the host; transfer fires at the first playable geoscape frame.");
        }

        /// <summary>The lobby system-chat line clients see while the host creates the campaign.</summary>
        public const string NewCampaignCreatingNotice =
            "— host is creating a new campaign; you will join automatically when it is ready —";

        /// <summary>Drop a pending bootstrap (host backed out of the native new-game settings).</summary>
        public void DisarmNewCampaignBootstrap()
        {
            if (!_newCampaign.Armed) return;
            _newCampaign.Disarm();
            Debug.Log("[Multiplayer] New-campaign co-op bootstrap disarmed.");
        }

        /// <summary>
        /// CurtainShowPatch "Playing" seam: this peer just reached a playable frame. Single
        /// consumption point of the armed bootstrap: on the first playable GEOSCAPE frame the latch
        /// disarms and — when the fire guard is open (still host, session live, no transfer in
        /// flight) — the host autosaves the freshly created campaign and feeds that autosave into the
        /// EXISTING chunked transfer + LOADED/BEGIN barrier (LaunchTransfer), exactly like a lobby
        /// start / F2 reload. Non-geoscape playable frames (a tutorial mission would be one — the
        /// intercept forces the tutorial off; this is belt+braces) keep the latch armed until the
        /// geoscape is reached. No-op on every peer that never armed (clients, single-player).
        /// </summary>
        public void OnNewCampaignPlayableFrame()
        {
            if (!_newCampaign.Armed) return;
            bool geoscape = Sync.GeoRuntime.Instance.IsGeoscapeActive;
            // Captured BEFORE the transfer resets _begun: a mid-session second fresh campaign is an
            // F2-analog reload (clients hold pre-existing channel state) → arm the rca-4 re-seed.
            bool wasStarted = SessionStarted;
            if (!_newCampaign.TryFire(_engine.IsHost, _engine.IsActiveSession, geoscape, TransferActive))
            {
                if (geoscape)
                    Debug.LogWarning("[Multiplayer] New-campaign bootstrap reached the geoscape but the " +
                                     "fire guard is closed (transfer in flight or session gone) — dropped.");
                return;
            }

            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return;
            var timing = GetTiming();
            if (timing == null) return;

            Debug.Log("[Multiplayer] New-campaign bootstrap: first playable geoscape frame → autosave + transfer.");
            timing.Start(NewCampaignAutosaveAndTransferCrt(saveManager, reseedAfterReveal: wasStarted));
        }

        // Coroutine: autosave the freshly created campaign (AutosaveGame, the game's own state-capture
        // path — same as the P1 on-demand join capture), verify a FRESH autosave was produced
        // (SessionLifecycle.FreshAutosaveCaptured — never ship a stale blob), then hand its meta to
        // the EXISTING LaunchTransfer: the same chunked broadcast + LOADED/BEGIN barrier +
        // synchronized reveal every other session start uses. The host itself re-enters via the
        // barrier (PrepareEntryFromBlobCrt + FinishLevel on BEGIN), so every peer — host included —
        // starts from the byte-identical autosave. reseedAfterReveal: only the mid-session second
        // fresh campaign arms the rca-4 post-reveal re-seed; on a lobby FIRST start the transferred
        // save itself is the seed (same rule as HostStartSession vs HostStartSessionInGame).
        private IEnumerator<NextUpdate> NewCampaignAutosaveAndTransferCrt(
            PhoenixSaveManager saveManager, bool reseedAfterReveal)
        {
            var oldAutoSave = saveManager.AutoSave;
            var ex = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(saveManager.AutosaveGame(), ex);
            if (ex.Value != null)
            {
                Debug.LogError("[Multiplayer] New-campaign bootstrap: autosave capture failed: " + ex.Value.Message);
                yield break;
            }

            var meta = saveManager.AutoSave;
            if (!SessionLifecycle.FreshAutosaveCaptured(oldAutoSave, meta))
            {
                Debug.LogError("[Multiplayer] New-campaign bootstrap: no fresh autosave captured (write " +
                               "failure?) — clients were NOT started; use CHOOSE SAVE with a manual save instead.");
                yield break;
            }

            // Same terminal-flag reset as HostStartSessionInGame: a mid-session second fresh campaign
            // re-runs the SAME barrier/reveal state machine; on a lobby first start these are already
            // false (no-op). OpenBarrier itself resets the per-run state per fresh barrier.
            _begun = false;
            _loadCompleteSent = false;
            _revealAllSent = false;
            if (!LaunchTransfer(meta))
            {
                Debug.LogError("[Multiplayer] New-campaign bootstrap: transfer launch failed (see prior log).");
                yield break;
            }
            if (reseedAfterReveal) _reseedGate.Arm();
        }

        private void OpenBarrier()
        {
            _barrierOpen = true;
            _barrierOpenedAtMs = NowMs();
            _loadedPeers.Clear();
            _hostLoaded = false; // fix #2: host self-loaded flag reset per fresh barrier
            _peerDownloadPct.Clear();
            _slotProgress.Clear();
            _tracker.Reset(); // fresh session: drop stale progress/done so 2nd co-op run starts clean
            _lastSnapshotMs = -1;
            _loadCompleteSent = false;
            _lastReportedLoadPct = -1; // fresh session: phase-2 driver not reporting yet
            _loadingLevel = null;      // fresh session: no level captured yet
            _liveProgressBar = null;   // fresh session: live native bar not captured yet
            _loadPhaseActive = false; // fresh session: phase-2 not started yet
            // Second barrier (reveal) state — fresh session.
            _reachedPlaying = false;
            _revealed = false;
            _revealAllSent = false;
            _revealHoldStartedMs = 0;
            _phase2DeadlineMs = 0;
            Debug.Log($"[Multiplayer] LOADED barrier open, host self-added id={_engine.LocalSteamId}.");
        }

        /// <summary>
        /// Batch 2 host reveal-hold: arm the SYNCHRONIZED-REVEAL barrier at tactical LAUNCH (before the host
        /// reaches tactical Playing), so CurtainShowPatch.Prefix / CurtainLiftGatePatch hold the host behind
        /// its native loading screen until every client reports load-complete (RevealAll at AllDone, or the
        /// forced/self-reveal fallbacks). Ordering-critical (plan Risk #3): _revealed MUST be reset to false
        /// BEFORE the Loaded→Playing transition, else CurtainShowPatch.Prefix lets the native auto-lift
        /// through and the host reveals the battle on its own. Only the reveal-hold state is touched here —
        /// the LOADED barrier (chunk transfer) opens later at deploy-ready in HostTacticalEntryTransferCrt,
        /// so its phase-1 timeout clock covers only the client's real download+load window. _begun
        /// (SessionStarted) is deliberately LEFT set (the host is already in a live co-op level) so the
        /// curtain hold engages and mid-tactical F2 keeps working; Begin() still fires via the _hostEntryHold
        /// relaxation. Gated by <see cref="Sync.Tactical.TacticalEntryBarrierGate"/> at the call site.
        /// </summary>
        public void OpenTacticalEntryBarrier()
        {
            _hostEntryHold = true;
            _revealed = false;        // ordering-critical: arm the hold before Loaded→Playing
            _reachedPlaying = false;  // so OnReachedPlaying fires again at the tactical Playing (label + host done-mark)
            Debug.Log($"[Multiplayer] host reveal-hold armed (tac-entry): sessionStarted={SessionStarted} " +
                      $"revealed={_revealed} — host holds its loading screen until all clients load-complete.");
        }

        // ══════════════════════════════════════════════════════════════════
        //  CLIENT: receive chunks, reassemble, verify, load in-memory
        // ══════════════════════════════════════════════════════════════════

        public void OnSaveChunk(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var chunk = MessageSerializer.DeserializeSaveChunk(msg.Payload);

            // Stale-chunk guard (F2): a late chunk of a transfer we ALREADY finished must never
            // re-start reassembly. Without this, an old-transfer chunk arriving after a new F2 transfer
            // began (its id != the active _rxTransferId) would re-enter the first-chunk branch below and
            // wipe _begun/buffers mid-new-download. _completedTransferId is the last finished id.
            if (_completedTransferId != Guid.Empty && chunk.TransferId == _completedTransferId)
            {
                Debug.Log($"[Multiplayer] OnSaveChunk: ignoring stale chunk from completed transfer {chunk.TransferId}.");
                return;
            }

            // First chunk of a transfer (re)initialises the reassembly buffer.
            if (_rxBuffer == null || _rxTransferId != chunk.TransferId)
            {
                _rxTransferId = chunk.TransferId;
                _rxTotalBytes = chunk.TotalBytes;
                _rxBuffer = new byte[chunk.TotalBytes];
                _rxReceived = 0;
                _lastReportedDownloadPct = -1;
                // F2 mid-session reload: a NEW transfer id while we are already in-game means the host
                // is loading a different save and pulling us into it. Clear the prior run's terminal
                // barrier/reveal flags so EnterLevel (gated by _begun) and the reveal run again for the
                // new save — otherwise the client would download + prepare it but never enter the level.
                _begun = false;
                _loadCompleteSent = false;
                _reachedPlaying = false;
                _revealed = false;
                _revealAllSent = false;
                _onDemandJoiner = false;   // P1: fresh transfer; set true only if this SaveDone tags a join
                _pendingResult = null;
                // Reset symmetry (overlay robustness fix): mirror the host's OpenBarrier reset on the CLIENT
                // transfer-entry path so a 2nd consecutive client load starts clean. Without this the phase-2
                // driver (_lastReportedLoadPct/_loadingLevel/_liveProgressBar) keeps the prior run's stale
                // values, and the per-peer done-set keeps the prior run's done slots — which would make the
                // state-driven overlay predicate (LoadOverlayVisibility.ShouldShow, fed by tracker.IsDone)
                // read "all peers already done" at the new transfer's start and never show the overlay.
                _lastReportedLoadPct = -1;
                _loadingLevel = null;
                _liveProgressBar = null;
                _tracker.Reset(); // drop prior run's per-slot progress + done so the new load shows from 0
                // Enter the game's NATIVE loading screen for this download RIGHT NOW: drop the curtain +
                // start driving the bottom bar with the download %, hide the lobby. The client no longer
                // sits in the lobby with only a top-right plaque during the WAN transfer; the bar hands
                // off to the real level-load progress at phase-2 (SetLoadingLevel). Client-only (OnSaveChunk
                // returns early on the host). Once per transfer (this first-chunk branch runs once per id).
                _downloadCurtain = true;
                // Arm the geo->tactical (and any co-op save-apply) transition gate: this transfer will tear the
                // current level down + rebuild it, during which late host wallet syncs would repaint a half-torn
                // info bar (TFTV RefreshResourceText NRE). This is the PRODUCTION client entry (save-transfer);
                // the LaunchTacticalGameGatePatch SET only covers the UseSaveTransferEntry=false path. Cleared at
                // tactical level-ready / geoscape reload. Skipped wallet syncs recover on level re-entry.
                Multiplayer.Network.Sync.State.GeoTransitionGate.InTransition = true;
                Multiplayer.UI.MultiplayerUI.Instance?.EnterDownloadLoadingScreen();
                // Chunks are emitted at fixed ChunkSize offsets (SendBlob), so the index is exact.
                var chunkCount = (int)((chunk.TotalBytes + ChunkSize - 1) / ChunkSize);
                _rxChunkSeen = new bool[chunkCount];
                _rxChunksRemaining = chunkCount;
                Debug.Log($"[Multiplayer] OnSaveChunk FIRST: transfer={chunk.TransferId} total={chunk.TotalBytes} chunks={chunkCount}");
            }

            // Fix #4: validate the chunk maps to a clean grid index BEFORE indexing _rxChunkSeen.
            // Chunks are emitted at fixed ChunkSize offsets (SendBlob), so a well-formed offset is a
            // multiple of ChunkSize and within bounds. A malformed/hostile chunk (offset not on the grid,
            // or out of range) must be rejected rather than mis-mapped onto the wrong coverage index.
            if (chunk.Chunk != null &&
                TryChunkIndex(chunk.Offset, chunk.Chunk.Length, _rxBuffer.Length, ChunkSize, out var index))
            {
                // Copy is idempotent; only count a chunk once toward coverage/progress even if the
                // transport redelivers it (Stun duplicates reliable packets).
                Array.Copy(chunk.Chunk, 0, _rxBuffer, chunk.Offset, chunk.Chunk.Length);

                if (_rxChunkSeen != null && index >= 0 && index < _rxChunkSeen.Length && !_rxChunkSeen[index])
                {
                    _rxChunkSeen[index] = true;
                    _rxChunksRemaining--;
                    _rxReceived += chunk.Chunk.Length;
                    ReportDownloadProgress();
                    // Throttled progress trace: every 64 chunks (and at completion). Not per-chunk.
                    if (_rxChunksRemaining == 0 || (_rxChunksRemaining % 64) == 0)
                        Debug.Log($"[Multiplayer] OnSaveChunk: received={_rxReceived}/{_rxTotalBytes} remaining={_rxChunksRemaining}");
                }
            }
            else if (chunk.Chunk != null)
            {
                Debug.LogWarning($"[Multiplayer] OnSaveChunk: rejecting malformed chunk " +
                                 $"(offset={chunk.Offset} len={chunk.Chunk.Length} total={_rxBuffer.Length} " +
                                 $"chunkSize={ChunkSize}) — not on the ChunkSize grid or out of bounds.");
            }
        }

        public void OnSaveDone(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var (transferId, totalBytes, ext, crc32, onDemandJoin) = MessageSerializer.DeserializeSaveDone(msg.Payload);

            Debug.Log($"[Multiplayer] OnSaveDone: transfer={transferId} total={totalBytes} remaining={_rxChunksRemaining}");

            if (_rxBuffer == null || transferId != _rxTransferId)
            {
                Debug.LogError("[Multiplayer] SaveDone for an unknown transfer; ignoring.");
                SendLoaded(transferId, false);
                return;
            }

            // Completion is decided by chunk coverage, NOT a running byte counter: every chunk index
            // must be present. A redelivered chunk does not inflate this (see OnSaveChunk).
            if (totalBytes != _rxBuffer.Length || _rxChunksRemaining != 0)
            {
                Debug.LogError($"[Multiplayer] Save transfer incomplete: got {_rxReceived}/{totalBytes} bytes, " +
                               $"{_rxChunksRemaining} chunk(s) still missing.");
                SendLoaded(transferId, false);
                ResetRx();
                AbortDownloadCurtain("download incomplete");
                return;
            }

            var actualCrc = Crc32(_rxBuffer);
            if (actualCrc != crc32)
            {
                Debug.LogError($"[Multiplayer] Save transfer crc mismatch: 0x{actualCrc:X8} != 0x{crc32:X8}.");
                SendLoaded(transferId, false);
                ResetRx();
                AbortDownloadCurtain("checksum mismatch");
                return;
            }

            // Verified blob — load it in memory, but DEFER entering the level until BEGIN.
            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) { SendLoaded(transferId, false); AbortDownloadCurtain("load init"); return; }

            var blob = _rxBuffer;
            var loadExt = string.IsNullOrEmpty(ext) ? SerializationComponent.DefaultExtension : ext;
            // Mark this transfer FINISHED so any late chunk of it (arriving after a subsequent F2
            // transfer starts) is rejected by the stale-chunk guard in OnSaveChunk.
            _completedTransferId = transferId;
            ResetRx();

            var timing = GetTiming();
            if (timing == null) { SendLoaded(transferId, false); return; }
            Debug.Log($"[Multiplayer] OnSaveDone: verified OK → ClientLoadCrt (onDemandJoin={onDemandJoin})");
            timing.Start(ClientLoadCrt(game, blob, loadExt, transferId, onDemandJoin));
        }

        private IEnumerator<NextUpdate> ClientLoadCrt(PhoenixGame game, byte[] blob, string ext, Guid transferId, bool onDemandJoin)
        {
            Debug.Log($"[Multiplayer] ClientLoadCrt: preparing entry (onDemandJoin={onDemandJoin})");
            yield return Timing.Current.Call(PrepareEntryFromBlobCrt(game, blob, ext));

            var ok = _pendingResult != null;

            // P1 mid-session join: there is NO lobby BEGIN barrier and NO co-op RevealAll hold — the
            // already-connected peers are long past their synchronized entry, so there is nothing to
            // synchronize with. Enter the level immediately; the native curtain lifts + the overlay hides
            // on our OWN load finish (OnReachedPlaying, join branch), which also pings JoinReady so the host
            // re-seeds our wallet + channels onto the now-live geoscape.
            if (onDemandJoin)
            {
                _onDemandJoiner = true;
                Debug.Log($"[Multiplayer] ClientLoadCrt: on-demand join prepared ok={ok} → EnterLevel (no barrier)");
                if (ok) EnterLevel();
                else
                {
                    Debug.LogError("[Multiplayer] on-demand join: entry prepare FAILED; joiner cannot enter the level.");
                    AbortDownloadCurtain("prepare");
                }
                yield break;
            }

            Debug.Log($"[Multiplayer] ClientLoadCrt: prepared ok={ok} → SendLoaded");
            // Ack the barrier AFTER the load is prepared but BEFORE FinishLevel.
            SendLoaded(transferId, ok);
            // Prepare failed: the barrier will never get our LOADED(true). Don't strand us on the curtain.
            if (!ok) AbortDownloadCurtain("prepare");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Shared: build the loaded scene binding in memory (no temp file)
        //  Mirrors PhoenixSaveManager.LoadCurrentGeoscape (PhoenixSaveManager.cs:380-398).
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator<NextUpdate> PrepareEntryFromBlobCrt(PhoenixGame game, byte[] blob, string ext)
        {
            Debug.Log("[Multiplayer] PrepareEntryFromBlobCrt: start");

            // Save-load / co-op save-transfer boundary: this coroutine is the SHARED host+client reload-entry
            // hook (host: HostSerializeAndSendCrt, client: ClientLoadCrt incl. the on-demand join path), and
            // the SyncEngine is NOT recreated on a mid-session reload (only on full session teardown) — so
            // every in-flight engine-state holder that references the dying geoscape resets HERE, in the ONE
            // aggregated rca-3 sweep (choice arbiter + event mirror + geo intent dedup + pending/coalesce
            // marks + vehicle mirrors + tactical mission state + client time-sync re-arm; the audited list
            // lives in the SyncEngine ctor). Every entry is idempotent and safe for a first-time on-demand
            // joiner (empty state → no-op). Version counters / last-seen trackers deliberately PERSIST on
            // both sides — symmetric continuity, pinned by ReloadBoundaryVersionContinuityTests.
            _engine.Sync?.ResetForReloadBoundary();

            var serializer = game.SaveManager.Serializer;
            var slice = new TimeSlice(serializer.SerializeTimeSlice);

            // 1. Read metadata (gives the LevelScene binding template).
            var metaRef = new ByRef<SavegameMetaData>();
            using (var ms = new System.IO.MemoryStream(blob))
            {
                yield return Timing.Current.Call(serializer.ReadMetaData(ms, ext, metaRef, slice));
            }

            var meta = metaRef.Value;
            if (meta == null || meta.LevelScene == null)
            {
                Debug.LogError("[Multiplayer] Transferred save metadata could not be read.");
                yield break;
            }

            // 1b. Replicate PhoenixSaveManager.PrepareLoadGame's state side-effects from the blob's
            // metadata BEFORE FinishLevel runs (EnterLevel). The native LoadGame path calls
            // PrepareLoadGame (PhoenixSaveManager.cs:623-647) which sets _enabledDlc/_currentGameId/
            // _currentDifficulty/LatestLoad; we never go through that path here, so without this the
            // SaveManager keeps _enabledDlc empty → PhoenixGame.IsDlcEnabled(FesteringSkies) false →
            // GeoMap.GenerateSitePathData leaves _landConnectedSites null → GeoBehemothActor NRE →
            // LevelCrt aborts → empty globe, no UI. We do NOT call PrepareLoadGame directly: it is a
            // private IEnumerator coroutine that ALSO does IronmanSave() + tactical content reads
            // (cs:625-637), i.e. far more than field-setting — so we replicate ONLY the field set.
            ApplyPrepareLoadGameState(game.SaveManager, meta);

            // 2. Read level params from the same bytes.
            var paramsSource = new Base.Levels.BinaryDataLevelParamsSource(blob, ext);
            var levelParams = new ByRef<Base.Levels.ILevelParams>();
            yield return Timing.Current.Call(paramsSource.ReadLevelParamsAsync(serializer, levelParams));

            // 3. Build the scene binding from the in-memory data source.
            var serializedParam = new Base.Levels.LevelSerializedParam(
                levelParams.Value,
                new Base.Levels.BinaryDataLevelSerializedDataSource(blob, ext));
            var binding = meta.LevelScene.CreateSceneBinding(serializedParam);

            _pendingResult = new LoadLevelGameResult(binding);
            Debug.Log("[Multiplayer] PrepareEntryFromBlobCrt: _pendingResult ready");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Barrier: LOADED collection (host) + BEGIN
        // ══════════════════════════════════════════════════════════════════

        private void SendLoaded(Guid transferId, bool ok)
        {
            Debug.Log($"[Multiplayer] SendLoaded: transfer={transferId} ok={ok} → host");
            var payload = MessageSerializer.SerializeClientLoaded(_engine.LocalSteamId, transferId, ok);
            _engine.SendToHost(new NetworkMessage(PacketType.ClientLoaded, payload));
        }

        public void OnClientLoaded(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            var (steamId, transferId, ok) = MessageSerializer.DeserializeClientLoaded(msg.Payload);

            Debug.Log($"[Multiplayer] LOADED ack rx: sender={msg.SenderSteamId} payloadId={steamId} " +
                      $"transferId={transferId} (current {_transferId}) ok={ok}.");

            // Ignore a stale ack from a prior transfer: it must match the current transfer id.
            if (transferId != _transferId)
            {
                Debug.LogWarning($"[Multiplayer] LOADED REJECTED (stale transfer): sender={msg.SenderSteamId} " +
                                 $"transfer {transferId} (current {_transferId}); ignoring.");
                return;
            }

            if (ok)
            {
                // Key the barrier set by the AUTHORITATIVE transport id (msg.SenderSteamId), NOT the
                // self-reported payload steamId — mirrors _peerDownloadPct (line ~80): robust to
                // LocalSteamId collision on DirectIP / the local 2-instance test rig. The payload
                // steamId can collide across peers and stall release at Count=1.
                _loadedPeers.Add(msg.SenderSteamId);
                Debug.Log($"[Multiplayer] LOADED ACCEPTED: added sender={msg.SenderSteamId} to barrier set.");
                TryReleaseBarrier();
            }
            else
            {
                Debug.LogWarning($"[Multiplayer] LOADED REJECTED (ok=false): sender={msg.SenderSteamId} " +
                                 $"failed to load the transferred save.");
            }
        }

        // Host: release the barrier once the host AND every currently-connected client has reported
        // LOADED. Expected-client count is read LIVE from the roster, so a peer that dropped mid-load is
        // already absent (Session.RemoveClient ran before the disconnect event) → release happens with
        // whoever remains (fix #1). Host vs client counting is kept structurally separate (fix #2).
        private void TryReleaseBarrier()
        {
            if (!_engine.IsHost || !_barrierOpen) return;

            // Expected CLIENTS = all currently connected clients (host is counted via _hostLoaded).
            var expectedClients = 0;
            foreach (var _ in _engine.Session.GetConnectedClients()) expectedClients++;

            var release = BarrierReleased(_hostLoaded, _loadedPeers.Count, expectedClients);
            Debug.Log($"[Multiplayer] TryReleaseBarrier: hostLoaded={_hostLoaded} " +
                      $"loadedClients={_loadedPeers.Count} expectedClients={expectedClients} release={release}.");

            if (release)
                Begin();
        }

        /// <summary>
        /// Pure barrier-release predicate (fix #1/#2, unit-testable): the LOADED barrier releases iff the
        /// host has prepared AND every currently-expected client has acked. The host is counted via a
        /// dedicated flag, never an id in <paramref name="loadedClientCount"/>, so a peerId-0 client can
        /// never masquerade as the host. When a not-yet-loaded peer drops, the caller passes the reduced
        /// live <paramref name="expectedClientCount"/>, so the barrier releases early with the rest.
        /// </summary>
        internal static bool BarrierReleased(bool hostLoaded, int loadedClientCount, int expectedClientCount)
            => SaveTransferMath.BarrierReleased(hostLoaded, loadedClientCount, expectedClientCount);

        /// <summary>This peer's load is truly finished (event-driven done) — tell the host, reliably.</summary>
        public void SendLoadComplete()
        {
            if (_loadCompleteSent) return;
            _loadCompleteSent = true;
            Debug.Log("[Multiplayer] SendLoadComplete fired slot=" + _engine.Session.LocalSlotIndex);
            var slot = _engine.Session.LocalSlotIndex;
            _tracker.MarkDone(slot); // local self-done
            if (_engine.IsHost) { TryReleaseBarrier(); return; }
            var payload = MessageSerializer.SerializeLoadComplete(slot, _rxTransferId);
            _engine.SendToHost(new NetworkMessage(PacketType.LoadComplete, payload));
        }


        // ══════════════════════════════════════════════════════════════════
        //  Second barrier: synchronized geoscape reveal (BUG D)
        //  The native curtain auto-lifts on Loaded→Playing; the mod's opaque overlay is held as the
        //  real cover and dropped together on RevealAll so every peer reveals the world at once.
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Called by CurtainShowPatch when this peer hits Playing during a co-op session.
        /// Marks the hold start + reports this peer's load done (idempotent), but does NOT lift —
        /// the overlay stays up until RevealAll (or a fallback).</summary>
        public void OnReachedPlaying()
        {
            if (_reachedPlaying) return;
            _reachedPlaying = true;
            _revealHoldStartedMs = NowMs();

            // P1 mid-session join: reveal on our OWN load finish — there is no host RevealAll to wait for
            // (the already-connected peers revealed long ago). Lift immediately + tell the host we are live
            // (JoinReady) so it re-seeds our wallet + channels onto the now-live geoscape.
            if (_onDemandJoiner)
            {
                Debug.Log($"[Multiplayer] OnReachedPlaying slot={_engine.Session.LocalSlotIndex} (on-demand join) " +
                          "→ reveal now + JoinReady");
                PerformDeferredLift();
                _engine.SendToHost(new NetworkMessage(PacketType.JoinReady));
                return;
            }

            Debug.Log($"[Multiplayer] OnReachedPlaying slot={_engine.Session.LocalSlotIndex} " +
                      $"→ hold + SendLoadComplete");
            // This peer is done but HELD (curtain gate parks every native lift until Revealed).
            // Label the held native loading screen so the wait reads as intentional.
            Multiplayer.UI.NativeWidgetFactory.SetCurtainLabel("Waiting for players…");
            // Done is reported HERE and only here (Playing = actually in the level, curtain-liftable).
            SendLoadComplete();
        }

        /// <summary>All peers: host says everyone is loaded → lift the held overlay now.</summary>
        public void OnRevealAll(NetworkMessage msg)
        {
            Debug.Log("[Multiplayer] OnRevealAll received → PerformDeferredLift");
            PerformDeferredLift();
        }

        // Lift the held synced reveal (native curtain we suppressed + the mod overlay roster) so the
        // already-loaded world appears on every peer at once. Once-guarded FIRST; never throws.
        private void PerformDeferredLift()
        {
            if (_revealed) return;
            _revealed = true;
            _hostEntryHold = false; // Batch 2: reveal done → drop the entry-hold flag (next Begin re-guards on _begun)
            Debug.Log("[Multiplayer] PerformDeferredLift → reveal (native LiftCurtain + hide overlay)");
            // Restore the native loading label ("Waiting for players…" → original) before the lift runs.
            // Setting _revealed above already opened the curtain gate, so any PARKED lift resumes now.
            try { Multiplayer.UI.NativeWidgetFactory.RestoreCurtainLabel(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] RestoreCurtainLabel failed: " + e.Message); }
            // Lift the native curtain we suppressed (animated alpha→0, unpauses rendering, fires
            // OnCurtainLifted → GeoscapeView unlocks input + enables sound). Reflection: mod can't ref the type.
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("Base.Utils.LevelSwitchCurtainController");
                if (t != null)
                {
                    var ctrl = UnityEngine.Object.FindObjectOfType(t);
                    if (ctrl != null)
                    {
                        var m = HarmonyLib.AccessTools.Method(t, "LiftCurtain", new System.Type[0]);
                        m?.Invoke(ctrl, null);
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] native LiftCurtain failed: " + e.Message); }
            // Hide the mod overlay roster.
            try { MultiplayerUI.Instance?.HideLoadOverlay(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] HideLoadOverlay failed: " + e.Message); }
        }

        /// <summary>Host: a client reported its load complete (RELIABLE, event-driven done).</summary>
        public void OnLoadComplete(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            var (slot, _) = MessageSerializer.DeserializeLoadComplete(msg.Payload);
            _tracker.MarkDone(slot);
            TryReleaseBarrier();
        }

        /// <summary>All peers: merge a host RosterProgress snapshot into the shared tracker for the overlay.</summary>
        public void OnRosterProgress(NetworkMessage msg)
        {
            var rows = MessageSerializer.DeserializeRosterProgress(msg.Payload);
            var recvDetail = string.Join(",", rows.Select(r => $"s{r.SlotIndex}:{r.Phase}/{r.Percent}"));
            Debug.Log($"[Multiplayer] RosterProgress RECV [{recvDetail}]");
            foreach (var r in rows) _tracker.Merge(r.SlotIndex, r.Phase, r.Percent);
        }

        // Host broadcasts BEGIN; every peer (incl. host) then enters its prepared level.
        private void Begin()
        {
            if (!_engine.IsHost) return;
            // Entry-via-save (Batch 2): the host is ALREADY in its live tactical level (_begun stayed true so
            // mid-tactical F2 keeps working), yet must STILL broadcast SessionBegin so the CLIENT enters its
            // prepared level. _barrierOpen (cleared just below) is the true single-fire guard for BOTH paths
            // — TryReleaseBarrier and the phase-1 timeout both bail on !_barrierOpen — so relaxing the _begun
            // guard on the entry path cannot double-fire. EnterLevel() no-ops on the host (its own _begun
            // guard), so the host never re-enters the level it already built.
            if (_begun && !_hostEntryHold) return;
            _barrierOpen = false;
            // Phase-2 (world load) starts now; keep snapshots flowing until the roster is all-done.
            _loadPhaseActive = true;
            // Host forced-reveal deadline: if a peer errors / never reports done, reveal anyway.
            // Uses the phase-2 reveal deadline (NOT the phase-1 load timeout) so a long native world-load
            // is not force-revealed mid-load (fix #3).
            _phase2DeadlineMs = NowMs() + RevealDeadlineMs;

            Debug.Log("[Multiplayer] BEGIN broadcast.");
            var startTicks = DateTime.UtcNow.Ticks;
            var payload = MessageSerializer.SerializeSessionBegin(startTicks);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.SessionBegin, payload));

            EnterLevel();
        }

        // All peers: BEGIN received → enter the prepared level simultaneously.
        public void OnBegin(NetworkMessage msg)
        {
            EnterLevel();
        }

        private void EnterLevel()
        {
            if (_begun) return;
            if (_pendingResult == null)
            {
                Debug.LogWarning("[Multiplayer] BEGIN received but no save was prepared; cannot enter level.");
                return;
            }

            _begun = true;
            PhoenixGame game;
            PhoenixSaveManager sm;
            if (!TryGetGame(out game, out sm)) return;

            // Single convergence point for both load paths (PhoenixGame.cs:263). The FinishLevel
            // Harmony gate (SaveLoadPatches) holds any vanilla-initiated call until this fires.
            Debug.Log("[Multiplayer] EnterLevel → FinishLevel.");
            game.FinishLevel(_pendingResult);
            // Confirm PrepareLoadGame state was applied (was 0 → empty geoscape; expect >0 now).
            var dlcLen = sm.EnabledDlc != null ? sm.EnabledDlc.Length : 0;
            Debug.Log($"[Multiplayer] co-op load: SaveManager.EnabledDlc.Length={dlcLen}");
            _pendingResult = null;
            // NOTE: FinishLevel is fire-and-return (PhoenixGame.cs:263-267 pulses a monitor; the
            // game coroutine loads the world on LATER frames). Do NOT hide the overlay here — the
            // phase-2 world-load happens after this returns. The overlay is hidden on the curtain
            // LIFT (Loaded→Playing) by CurtainShowPatch instead.
        }

        // ══════════════════════════════════════════════════════════════════
        //  Progress reporting (download exact; load is a coarse phase flag)
        //  Real in-game load % is an OPEN SDK item — no 0..1 float is exposed
        //  (docs/specs/03-open-questions-sdk.md "Loading Progress Hook").
        // ══════════════════════════════════════════════════════════════════

        private void ReportDownloadProgress()
        {
            if (_rxTotalBytes <= 0) return;
            var pct = (int)(100L * _rxReceived / _rxTotalBytes);
            if (pct == _lastReportedDownloadPct) return;
            // Throttle to whole-percent steps to avoid flooding the link.
            _lastReportedDownloadPct = pct;
            var payload = MessageSerializer.SerializeLoadProgress(_engine.LocalSteamId, 0, (byte)pct);
            _engine.SendToHost(new NetworkMessage(PacketType.LoadProgress, payload));
        }

        /// <summary>Client/host: report this peer's phase-2 (native load) percent to the host.</summary>
        public void ReportLoadProgress(byte percent)
        {
            var payload = MessageSerializer.SerializeLoadProgress(_engine.LocalSteamId, 1, percent);
            if (_engine.IsHost)
            {
                // Host has no host→host hop: aggregate its own slot 0 (phase 1) directly.
                _slotProgress[0] = (1, percent);
                _tracker.Merge(0, 1, percent);
            }
            else
            {
                _engine.SendToHost(new NetworkMessage(PacketType.LoadProgress, payload));
                // Also merge into our OWN local tracker so the client shows its own phase-2 bar
                // immediately — the host's echo can't help us (the host snapshot carries other
                // slots), and previously the host echo was dead during phase-2 anyway. Mirrors the
                // host merging its own slot 0 above.
                _tracker.Merge(_engine.Session.LocalSlotIndex, 1, percent);
            }
        }

        public void OnLoadProgress(NetworkMessage msg)
        {
            // Host-only aggregation. Each peer reports its OWN (phase, percent); the host maps the
            // authoritative transport sender id to that peer's stable slotIndex and aggregates the
            // co-op overlay snapshot monotonic-max per (slot, phase). The lobby download display still
            // keys phase-0 by SenderSteamId via _peerDownloadPct (read by LobbyPanel).
            if (!_engine.IsHost) return;

            var (_, phase, percent) = MessageSerializer.DeserializeLoadProgress(msg.Payload);

            // Phase 0 = download (exact) — keep the existing per-peer download view for the lobby.
            if (phase == 0)
                _peerDownloadPct[msg.SenderSteamId] = percent;

            // Map the sender to its slot via the roster, then aggregate per-slot for the snapshot.
            if (_engine.Session.TryGetSlotForPeer(msg.SenderSteamId, out var slot))
            {
                _slotProgress[slot] = (phase, percent);
                _tracker.Merge(slot, phase, percent);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Per-frame: host barrier timeout / kick
        // ══════════════════════════════════════════════════════════════════

        public void Update()
        {
            // Phase-1 (download) native bottom-bar driver — client only. While the save blob is arriving,
            // feed the game's own loading-screen bar the exact download fraction so it fills 0..100% under
            // the dropped curtain. When the download finishes we hold the bar full + relabel "Waiting for
            // players…" through the prepare + LOADED-barrier gap; phase-2 (SetLoadingLevel) then hands the
            // bar to the real level-load progress and clears this driver.
            if (_downloadCurtain)
            {
                if (IsDownloading)
                {
                    Multiplayer.UI.NativeWidgetFactory.SetDownloadBar(
                        _rxTotalBytes > 0 ? (float)_rxReceived / _rxTotalBytes : 0f);
                }
                else
                {
                    Multiplayer.UI.NativeWidgetFactory.SetCurtainLabel("Waiting for players…");
                    Multiplayer.UI.NativeWidgetFactory.SetDownloadBar(1f);
                }
            }

            // Phase-2 progress pump — runs on EVERY peer (host + clients) regardless of overlay visibility.
            // Decoupled from the UI: the overlay being hidden must NOT stall progress/done reporting.
            if (InPhase2)
            {
                // Read the captured loading Level (NOT GameUtl.CurrentLevel(), which is null mid-load
                // — see _loadingLevel). _loadingLevel goes null either when its LoadingProgress ends
                // OR when CurtainShowPatch clears it on Playing/Loaded; both routes mean "done".
                var lp = _loadingLevel != null ? _loadingLevel.LoadingProgress : null;
                if (lp != null)
                {
                    // Prefer the REAL on-screen value: the live native bar's eased ProgressFill.fillAmount
                    // (the game eases it toward the coarse lp.Progress). Fall back to lp.Progress only if
                    // the live bar wasn't captured. Done is NOT derived from fillAmount (it holds ~1.0 and
                    // won't null) — see the else branch on LoadingProgress==null.
                    byte pct;
                    if (_liveProgressBar != null)
                    {
                        var fill = Multiplayer.UI.NativeWidgetFactory.GetProgressFill(_liveProgressBar);
                        pct = fill != null
                            ? RosterProgressTracker.ProgressByte(fill.fillAmount)
                            : RosterProgressTracker.ProgressByte(lp.Progress);
                    }
                    else
                    {
                        pct = RosterProgressTracker.ProgressByte(lp.Progress);
                    }
                    if (pct != _lastReportedLoadPct)
                    {
                        _lastReportedLoadPct = pct;
                        Debug.Log($"[Multiplayer] phase-2 pump: slot={_engine.Session.LocalSlotIndex} " +
                                  $"pct={pct} (src={(_liveProgressBar != null ? "nativeBar" : "levelProgress")})");
                        ReportLoadProgress(pct);
                    }
                }
                else if (_lastReportedLoadPct >= 0)
                {
                    // Native DATA load finished (LoadingProgress went null) — but the peer is NOT yet
                    // playable: scene instantiate/init still runs until Loaded→Playing. Done is reported
                    // ONLY at OnReachedPlaying (curtain-liftable), so the all-loaded reveal can never fire
                    // while a peer is still initializing (that early RevealAll opened the curtain gate
                    // before the slow peer was actually in — the barrier bug, live RCA 2026-07-11).
                    _lastReportedLoadPct = -1;
                    Debug.Log("[Multiplayer] phase-2 pump: LoadingProgress null → data loaded, awaiting Playing");
                }
            }

            // Reveal deadlock fallbacks — run on EVERY peer every frame (above the host-only return).
            // Host forced reveal: a peer errored / never reported done before the deadline. Broadcast
            // RevealAll anyway so nobody is stuck behind the held overlay forever.
            if (_engine.IsHost && _loadPhaseActive && !_revealAllSent && NowMs() > _phase2DeadlineMs)
            {
                _revealAllSent = true;
                _engine.BroadcastToAll(new NetworkMessage(
                    PacketType.RevealAll, MessageSerializer.SerializeRevealAll(DateTime.UtcNow.Ticks)));
                _loadPhaseActive = false;
                Debug.LogWarning($"[Multiplayer] host reveal released: timeout fallback ({RevealDeadlineMs}ms) — " +
                                 $"revealing without all clients (loadedClients={_loadedPeers.Count}).");
                PerformDeferredLift();
                HostReseedAfterReveal(); // rca-4: forced-reveal path still re-seeds a reloaded session
            }
            // Per-peer self-reveal: this peer is holding (reached Playing) but the RevealAll never
            // arrived (dead host). After the hold timeout, reveal locally so it isn't stuck forever.
            if (_reachedPlaying && !_revealed && NowMs() - _revealHoldStartedMs > RevealDeadlineMs)
            {
                PerformDeferredLift();
            }

            // Snapshots must flow through BOTH phases: the LOADED barrier window (_barrierOpen) AND
            // the phase-2 world-load (_loadPhaseActive, set in Begin() where _barrierOpen is cleared).
            // Without _loadPhaseActive every peer's tracker would freeze the instant phase-2 begins.
            if (!_engine.IsHost || (!_barrierOpen && !_loadPhaseActive)) return;

            // Broadcast the aggregated per-slot snapshot at ≤5 Hz. This runs ABOVE the timeout return
            // below so snapshots keep flowing for the whole load (done-tracking is event-driven via
            // LoadComplete, not a percent==100 threshold).
            var now = NowMs();
            if (now - _lastSnapshotMs >= SnapshotIntervalMs)
            {
                _lastSnapshotMs = now;
                BroadcastSnapshot();
            }

            // During phase-2, end the load-phase broadcast once every roster slot has reported
            // LoadComplete (consumes the existing done-set + LoadComplete mechanism). Send one final
            // snapshot so peers see the terminal state, then stop.
            if (_loadPhaseActive && _tracker.AllDone(_engine.Session.GetRosterSlots()))
            {
                BroadcastSnapshot();
                _loadPhaseActive = false;
                Debug.Log("[Multiplayer] co-op load: roster all-done — stopping phase-2 snapshots.");

                // Second barrier satisfied: every peer is loaded → reveal the world simultaneously.
                if (_engine.IsHost && !_revealAllSent)
                {
                    _revealAllSent = true;
                    Debug.Log("[Multiplayer] AllDone → broadcast RevealAll");
                    _engine.BroadcastToAll(new NetworkMessage( // reliable
                        PacketType.RevealAll, MessageSerializer.SerializeRevealAll(DateTime.UtcNow.Ticks)));
                    Debug.Log($"[Multiplayer] host reveal released: AllDone — every roster slot load-complete " +
                              $"(loadedClients={_loadedPeers.Count}).");
                    PerformDeferredLift(); // host reveals at the same instant
                    HostReseedAfterReveal(); // rca-4: every peer entered the loaded level → re-seed now
                }
            }

            // Timeout/kick + Begin only apply while the LOADED barrier is still open (phase-1).
            // Uses the generous phase-1 load timeout so a slow-but-healthy download/prepare is not kicked
            // (fix #3).
            if (!_barrierOpen) return;
            if (NowMs() - _barrierOpenedAtMs <= Phase1LoadTimeoutMs) return;

            // Timeout: kick every connected peer that has not reported LOADED, then begin with the rest.
            var stragglers = new List<ulong>();
            foreach (var clientId in _engine.Session.GetConnectedClients())
                if (!_loadedPeers.Contains(clientId))
                    stragglers.Add(clientId);

            foreach (var clientId in stragglers)
            {
                Debug.LogWarning($"[Multiplayer] Peer {clientId} did not load in time — kicking.");
                _engine.Session.RemoveClient(clientId);
            }

            // The host's own loaded-state is tracked by _hostLoaded (never keyed in _loadedPeers), so it
            // is unaffected by the straggler kicks above; begin with whoever remains (at least the host).
            Begin();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════

        // rca-4 (P0): host full-channel re-seed after a mid-session F2 reload. The save blob carries most
        // state, but any host-side channel state the save-load did not carry perfectly converges only
        // lazily on the next dirty-mark — stale on clients until then. Fire the SAME idempotent versioned
        // ABSOLUTE snapshots the mid-session joiner path already relies on (OnJoinReady): full wallet +
        // every state channel, plus a fresh reliable time anchor so clients derive the loaded save's clock
        // immediately (not on the next scrub-detect heartbeat). Runs at the RevealAll moment, i.e. AFTER
        // every peer entered the loaded level (never during phase-2 native load); safe repeats for
        // already-connected peers by construction (versioned snapshots). A save that landed in TACTICAL
        // has no live geoscape wallet/channels/clock — each call self-guards to a no-op — and instead the
        // tactical deploy seed re-runs against the live tactical level (rca-6 coordination seam).
        // Once-latched per reload: the lobby FIRST start never arms the gate (the save itself is the
        // seed), and the double reveal-release cannot double-reseed (SaveTransferBarrierTests).
        private void HostReseedAfterReveal()
        {
            if (!_engine.IsHost || !_reseedGate.TryConsume()) return;
            Debug.Log("[Multiplayer] post-reload re-seed → full wallet + all channels + time re-anchor (+ tactical seed if tactical)");
            _engine.Sync?.BroadcastFullWallet();
            _engine.Sync?.BroadcastAllChannels();
            _engine.TimeSync?.HostReAnchorNow();
            try { Multiplayer.Sync.Tactical.TacticalDeploySync.HostReseedAfterLoad(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] post-reload tactical re-seed failed: " + e.Message); }
        }

        // Serialize the host's current per-slot aggregate and broadcast it unreliably to all peers.
        private void BroadcastSnapshot()
        {
            var rows = new List<ProgressRow>(_slotProgress.Count);
            foreach (var kv in _slotProgress)
                rows.Add(new ProgressRow { SlotIndex = kv.Key, Phase = kv.Value.phase, Percent = kv.Value.percent });
            var sendDetail = string.Join(",", rows.Select(r => $"s{r.SlotIndex}:{r.Phase}/{r.Percent}"));
            Debug.Log($"[Multiplayer] RosterProgress SEND [{sendDetail}]");
            var payload = MessageSerializer.SerializeRosterProgress(rows);
            _engine.BroadcastUnreliable(new NetworkMessage(PacketType.RosterProgress, payload));
        }

        private void ResetRx()
        {
            _rxBuffer = null;
            _rxReceived = 0;
            _rxTotalBytes = 0;
            _rxTransferId = Guid.Empty;
            _lastReportedDownloadPct = -1;
            _rxChunkSeen = null;
            _rxChunksRemaining = 0;
        }

        // Replicate PhoenixSaveManager.PrepareLoadGame's field set (cs:639-642) on the live
        // SaveManager from the transferred metadata, via reflection (the fields + the LatestLoad
        // setter are private). Matches the native order/values exactly:
        //   LatestLoad = metaData;                                  // setter also sets _currentGameId + IsIronmanMode
        //   _currentGameId    = metaData.GameId;
        //   _currentDifficulty= metaData.DifficultyDef;
        //   _enabledDlc       = metaData.EnabledDlc ?? new EntitlementDef[0];
        // The DLC array is the load-bearing one (empty → empty geoscape); the rest keep save/ironman
        // bookkeeping consistent. EnabledDlc/GameId/DifficultyDef live on PPSavegameMetaData (the
        // concrete runtime type the serializer produces), not the SavegameMetaData base.
        private static void ApplyPrepareLoadGameState(PhoenixSaveManager saveManager, SavegameMetaData meta)
        {
            if (saveManager == null) return;
            try
            {
                var pp = meta as PPSavegameMetaData;
                if (pp == null)
                {
                    Debug.LogError("[Multiplayer] co-op load: metadata is not PPSavegameMetaData; " +
                                   "cannot apply PrepareLoadGame state (EnabledDlc/GameId/Difficulty).");
                    return;
                }

                var t = typeof(PhoenixSaveManager);
                // LatestLoad setter (private) also assigns _currentGameId + IsIronmanMode (cs:70-78).
                var latestLoadProp = AccessTools.Property(t, "LatestLoad");
                var currentGameIdField = AccessTools.Field(t, "_currentGameId");
                var currentDifficultyField = AccessTools.Field(t, "_currentDifficulty");
                var enabledDlcField = AccessTools.Field(t, "_enabledDlc");

                // Reflection can return null if PP/TFTV renames a member; warn specifically (instead of
                // letting .SetValue NRE into the generic catch → silent empty geoscape) and apply the rest.
                if (latestLoadProp == null)
                    Debug.LogWarning("[Multiplayer] co-op load: PrepareLoadGame property 'LatestLoad' not found " +
                                     "via reflection (PP/TFTV version mismatch?) — geoscape state may not apply.");
                if (currentGameIdField == null)
                    Debug.LogWarning("[Multiplayer] co-op load: PrepareLoadGame field '_currentGameId' not found " +
                                     "via reflection (PP/TFTV version mismatch?) — geoscape state may not apply.");
                if (currentDifficultyField == null)
                    Debug.LogWarning("[Multiplayer] co-op load: PrepareLoadGame field '_currentDifficulty' not found " +
                                     "via reflection (PP/TFTV version mismatch?) — geoscape state may not apply.");
                if (enabledDlcField == null)
                    Debug.LogWarning("[Multiplayer] co-op load: PrepareLoadGame field '_enabledDlc' not found " +
                                     "via reflection (PP/TFTV version mismatch?) — geoscape state may not apply.");

                latestLoadProp?.SetValue(saveManager, pp, null);
                currentGameIdField?.SetValue(saveManager, pp.GameId);
                currentDifficultyField?.SetValue(saveManager, pp.DifficultyDef);
                enabledDlcField?.SetValue(saveManager, pp.EnabledDlc ?? new EntitlementDef[0]);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] co-op load: failed to apply PrepareLoadGame state: " + e);
            }
        }

        private static bool TryGetGame(out PhoenixGame game, out PhoenixSaveManager saveManager)
        {
            game = null;
            saveManager = null;
            try
            {
                game = GameUtl.GameComponent<PhoenixGame>();
                saveManager = game?.SaveManager;
                if (game == null || saveManager == null || saveManager.Serializer == null)
                {
                    Debug.LogError("[Multiplayer] PhoenixGame/SaveManager not available.");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] Failed to resolve PhoenixGame: " + e.Message);
                return false;
            }
        }

        private static Timing GetTiming()
        {
            try
            {
                var ts = GameUtl.GameComponent<TimeSource>();
                return ts?.Timing;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multiplayer] Failed to resolve Timing: " + e.Message);
                return null;
            }
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Pure chunk-grid validator (fix #4, unit-testable): a well-formed chunk sits exactly on the
        /// <paramref name="chunkSize"/> grid (offset a non-negative multiple of chunkSize) and lies fully
        /// within [0, <paramref name="totalLen"/>). Returns the grid index (offset/chunkSize) only when
        /// all hold; rejects (false, index=-1) a malformed/out-of-range offset instead of mis-mapping it.
        /// </summary>
        internal static bool TryChunkIndex(long offset, int chunkLen, int totalLen, int chunkSize, out int index)
            => SaveTransferMath.TryChunkIndex(offset, chunkLen, totalLen, chunkSize, out index);

        // CRC-32 (IEEE 802.3, reflected). The ONE shared implementation lives in Multiplayer.Core
        // (Multiplayer.Util.Crc32 — moved verbatim from here, pinned by the standard check vector) so the
        // Inc5 divergence probe reuses the exact same polynomial/table. Thin delegate keeps every existing
        // call-site byte-identical.
        private static uint Crc32(byte[] data) => Multiplayer.Util.Crc32.Compute(data);
    }
}
