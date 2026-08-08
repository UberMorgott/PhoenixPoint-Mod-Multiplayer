using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Input;
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
using PhoenixPoint.Geoscape.Levels;
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

        // Upper bound on a transfer's declared TotalBytes (first-chunk sizing). The first chunk's
        // network-supplied length sizes the reassembly buffer; without a bound a hostile/garbled
        // large-positive value faults the alloc (leaving _rxBuffer null with _rxTotalBytes>0 =
        // stuck heartbeat-suspension) or commits a huge buffer. A real PP save is well under this.
        public const long MaxTransferBytes = 64L * 1024 * 1024;


        // NO BARRIER GRACE CONSTANT LIVES HERE ANY MORE, ON PURPOSE. Every version of it — the flat 180 s
        // wall clock, then the 60 s liveness grace — was a way for one player to be released while another
        // was still loading. The reveal barrier now has no clock at all: it opens when every roster slot has
        // reported load-complete, and a peer that is genuinely gone opens it by leaving the ROSTER.
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
        // CLIENT: a SessionBegin that arrived before this peer had prepared its save. See EnterLevel.
        private bool _beginPending;

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
        // A REVEAL HOLD IS ARMED AND UNRELEASED. ONE meaning, and the reason it is now only one: it used to
        // ALSO stand in for "the previous load's aggregation has ended", which made OpenTacticalEntryBarrier
        // set it FALSE — while BOTH reveal-release branches in Update() (the AllDone reveal and the
        // BarrierLivenessGraceMs give-up) were gated ON it. So for the whole host geo→tac entry load, and
        // FOREVER if the L122 guard returned early or the deploy-ready coroutine never reached the transfer,
        // an armed hold had NO release path at all, while the clients' self-reveal belt stayed shut because
        // the host was demonstrably alive. A three-peer hang with no way out, which the standing mandate
        // forbids: mission loading is the only legitimate synchronous wait, and even it must time out and
        // leave the stuck peer behind.
        //
        // So: TRUE wherever _revealed is re-armed to false (OpenBarrier, OpenTacticalEntryBarrier,
        // ArmSelfLoadBarrier, OnSaveChunk's first chunk), FALSE at PerformDeferredLift — the one release —
        // and the liveness timeout therefore covers EVERY barrier window for every caller, instead of one
        // more per-path belt. RailCheck L94 arm (k) executes that invariant. It keeps its second job for
        // free: while a hold is armed, the host's RosterProgress snapshot broadcast stays alive (_barrierOpen
        // is cleared in Begin() BEFORE FinishLevel runs phase-2, so without it every peer's tracker would
        // freeze at the phase-1 value). Does NOT re-block FinishLevel (the Harmony gate keys on
        // IsBarrierPending, not _barrierOpen).
        private bool _loadPhaseActive;

        // ─── Second barrier: synchronized geoscape reveal (BUG D) ─────────
        // The native curtain auto-lifts on Loaded→Playing; the mod overlay (opaque) is held as the
        // real synchronized cover and dropped together via RevealAll. _reachedPlaying: this peer hit
        // Playing (CurtainShowPatch); the hold's start (no longer read: the barrier has no clock);
        // _revealed: lift performed once; _revealAllSent: host broadcast-once guard.
        private bool _reachedPlaying;
        private bool _revealed;
        private bool _revealAllSent;
        // Batch 2 (entry-via-save): the host is holding its loading screen for a tactical-ENTRY transfer.
        // Set at LAUNCH (OpenTacticalEntryBarrier), cleared at reveal (PerformDeferredLift). Unlike F2/lobby
        // the host stays in its already-live tactical level (no self-enter), so _begun is NOT reset to false
        // on this path (keeps SessionStarted true → curtain hold engages + mid-tactical F2 still works). This
        // flag lets Begin() still fire (broadcast SessionBegin for the client) despite _begun being true.
        private bool _hostEntryHold;

        // THE HOST HAS TOLD EVERY PEER TO WAIT, SO IT OWES THEM A NUMBER. Armed in
        // BroadcastLoadBoundaryBegin — the ONE place a boundary is announced by helper — and cleared at the
        // reveal / on the abort that takes the same curtain back down. _hostEntryHold covers only the
        // tac-ENTRY boundary (OpenTacticalEntryBarrier sets it and emits 0x48 inline), so the other two
        // seams the helper serves — the lobby PLAY press and the new-campaign arm — had no publish window at
        // all: measured 2026-08-06, host announced at 22:25:13.567 and its first RosterProgress SEND was
        // 22:25:27.734, 14.2 s in which every client's mirrored bar sat on BeginDownloadBar's 0f under
        // "Host is loading…". This flag is that window; the disjunction in HostEntryLoad is what makes the
        // publish rule "every announced boundary" instead of "one of them".
        private bool _loadBoundaryAnnounced;

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

        // WHICH WAIT THE CURTAIN IS SHOWING. _downloadCurtain says "our curtain is up"; it does NOT say
        // whose turn it is to be slow. Two very different waits share it:
        //   (a) curtain up, no bytes yet  → we are waiting for the HOST to reach deploy-ready and write its
        //       mid-tactical save (13.0 s in the 2026-07-31 run, law L71),
        //   (c) download finished         → we are waiting for the OTHER PLAYERS to finish loading.
        // False through (a) and (b), true from the first chunk onward. Deliberately NOT cleared in ResetRx:
        // ResetRx runs at SaveDone, which is precisely the (b)→(c) edge — clearing it there would flip the
        // label back to "waiting for host" at the exact moment the host is done. Cleared only where a NEW
        // pre-transfer wait begins, i.e. OnEntryTransferBegin.
        private bool _rxStarted;

        /// <summary>Shared receiver-side roster progress for the overlay UI.</summary>
        public RosterProgressTracker Tracker => _tracker;

        /// <summary>
        /// Transfer-progress clock for the liveness-suspension deadline (SessionManager.TransferStallMs):
        /// bumped on every observable transfer/load progress event — chunk received, roster snapshot,
        /// barrier/phase flag edge, phase-2 percent step. SessionManager suspends its host/client-loss
        /// detectors ONLY while this keeps moving; a silently dead peer stops it, and after the stall
        /// window the detectors re-arm and fire the normal host-loss teardown.
        /// </summary>
        public long LastProgressMs { get; private set; } = NowMs();
        private void NoteProgress() => LastProgressMs = NowMs();

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

        /// <summary>
        /// THE CURTAIN GATE'S ARM — the single input <see cref="Multiplayer.Harmony.CurtainTakedownGate"/>
        /// asks, and deliberately WIDER than <see cref="SessionStarted"/>. A host creating a fresh campaign
        /// natively reaches an interactive geoscape seconds before <c>Begin()</c> exists to be started, so
        /// gating on <c>_begun</c> alone let the host play a live world while the clients were still in the
        /// lobby (see <see cref="SaveTransferMath.CurtainHoldArmed"/> for the measured run). Reading the
        /// pending bootstrap here — and not adding a second gate for it — keeps ONE predicate behind all
        /// three loading-screen take-down paths.
        /// </summary>
        public bool CurtainHoldArmed =>
            SaveTransferMath.CurtainHoldArmed(_begun, _newCampaign.Armed, _loadBoundaryAnnounced);

        /// <summary>THE THIRD INPUT OF <see cref="CurtainHoldArmed"/>, READABLE — exposed for the gate's
        /// evidence line and for nothing else. 17bf9fe made the announcement an input of the arm and left
        /// <c>CurtainTakedownGate.State()</c> naming only the other two, so every "curtain lift PASSED gate
        /// unheld" line in the 2026-08-07 session reports <c>holdArmed=</c> and then a parenthesis that
        /// accounts for two of three terms. On a lobby FIRST start this is the ONLY term that can be true,
        /// so the one boundary the newest fix exists for is the one the log cannot speak about — and two
        /// captured logs could not settle whether the host's screen was held. Law L173.</summary>
        public bool LoadBoundaryAnnounced => _loadBoundaryAnnounced;

        /// <summary>True once the deferred reveal (native LiftCurtain + overlay hide) has run; used by
        /// CurtainShowPatch.Prefix so a later Loaded→Playing after RevealAll is NOT suppressed.</summary>
        public bool Revealed => _revealed;

        /// <summary>True while this peer is in phase-2 native world-load (begun, not yet done).</summary>
        public bool InPhase2 => RosterProgressTracker.InPhase2(_begun, _loadCompleteSent);

        /// <summary>
        /// THE HOST, LOADING ITS OWN geo→tactical LEVEL — the window <see cref="InPhase2"/> cannot see.
        /// <c>OpenTacticalEntryBarrier</c> does not reset <c>_loadCompleteSent</c> (the host is already
        /// SessionStarted and its last load marked it done), so <c>InPhase2</c> is FALSE for the whole
        /// ~13 s the host spends building the mission: the phase-2 pump never sampled and the snapshot
        /// broadcast gate returned early. The host was radio-silent through its own load and every client
        /// stared at a bar nothing was driving. This is that window, and it ends the moment the curtain
        /// hands over (<see cref="LoadPhaseStarted"/> → false at Playing/Loaded).
        ///
        /// AND IT IS EVERY BOUNDARY, NOT ONE. <c>_hostEntryHold</c> is armed only by
        /// <see cref="OpenTacticalEntryBarrier"/>, so the two seams that announce through
        /// <see cref="BroadcastLoadBoundaryBegin"/> — the lobby PLAY press and the new-campaign arm — fell
        /// outside this window and the host published nothing through them (see
        /// <c>_loadBoundaryAnnounced</c> for the measurement). The disjunction is the whole fix: same pump,
        /// same widget, same phase, one more way in.
        /// </summary>
        private bool HostEntryLoad =>
            _engine.IsHost && (_hostEntryHold || _loadBoundaryAnnounced) && LoadPhaseStarted;

        /// <summary>
        /// The phase number the host's own entry load publishes under — 0, never 1 and never 2.
        /// <see cref="RosterProgressTracker.Merge"/> is MONOTONE ON PHASE, and
        /// <c>HostTacticalEntryTransferCrt</c> writes the terminal <c>(1, 100)</c> at deploy-ready; a
        /// higher number here makes that terminal unreachable, so the host row would freeze mid-load on
        /// every client forever. A field and not a const so RailCheck L118 can see the value is actually
        /// read by the publisher.
        /// </summary>
        internal static readonly byte HostEntryPhase = 0;

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
            NoteProgress(); // flag edge (native load started / finished)
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

        /// <summary>
        /// Overlay fix (2026-07-13): true on the HOST while a MID-SESSION co-op load window is live — the
        /// LOADED barrier is open (clients downloading/preparing) or phase-2 snapshots are flowing — even
        /// though the host itself shows NO local load signal (on tac-entry it sits behind the held curtain
        /// with LoadPhaseStarted/InPhase2/IsDownloading all false, so the overlay never showed and the host
        /// saw none of the clients' progress). Gated on SessionStarted (_begun): the lobby PLAY window has
        /// _begun false, so this can never resurrect the early lobby-popup bug ShouldShow's loadStarted gate
        /// was introduced to fix. Ends when the barrier closes and the roster all-done stops phase-2.
        /// </summary>
        public bool HostWaitingOnPeers => _engine.IsHost && _begun && (_barrierOpen || _loadPhaseActive);

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
            // caller — no path may start a session while the host is alone. NO ready quorum any more
            // (N=50 mandate): the host starts on its OWN readiness and every peer converges through the
            // per-peer join path, so an un-ready peer costs that peer a later arrival, never everyone
            // else the session.
            int clientCount = _engine.Session?.ClientCount ?? 0;
            if (clientCount < 1)
            {
                Debug.LogWarning("[Multiplayer] HostStartSession blocked: host is alone " +
                    $"(clients={clientCount}); ignoring start.");
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
            // run's terminal flags so the second transfer's OpenBarrier/Begin/reveal run clean. But
            // LaunchTransfer can still fail (no game / no timing): restore the flags on that path, or a
            // mid-game host is stranded with SessionStarted==false while still IN the live co-op level
            // (curtain patches inert, host-load guards closed).
            bool wasBegun = _begun, wasLoadComplete = _loadCompleteSent, wasRevealAll = _revealAllSent;
            _begun = false;
            _loadCompleteSent = false;
            _revealAllSent = false;
            bool launched = LaunchTransfer(chosen);
            if (!launched)
            {
                _begun = wasBegun;
                _loadCompleteSent = wasLoadComplete;
                _revealAllSent = wasRevealAll;
                return false;
            }
            // rca-4: arm the post-reload full re-seed for THIS reload (the transfer is now in flight); it
            // is consumed once at the RevealAll moment (HostReseedAfterReveal). Channels converge only
            // lazily on the next dirty-mark after a reload, so any host state the save-load itself did
            // not carry perfectly would otherwise stay stale on clients until then.
            _reseedGate.Arm();
            return true;
        }

        // Shared launch tail for both the lobby start and the mid-session load: warn on the best-effort
        // Stun path, resolve game + timing, then kick the serialize+send+barrier coroutine. Callers own
        // the guard (lobby ready-gate vs in-game guard); this is guard-free.
        /// <param name="hostHoldsThisWorld">THE ONE FLAG THAT REMOVES THE HOST'S SECOND LOAD, and the whole
        /// revert: pass <c>false</c> here (its default, and what both save-loading callers pass) and the host
        /// re-enters from the blob exactly as it always did. Only the new-campaign bootstrap passes true,
        /// because only there is the blob an autosave OF THE WORLD THE HOST IS STANDING IN.</param>
        private bool LaunchTransfer(SavegameMetaData chosen, bool hostHoldsThisWorld = false)
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
            timing.Start(HostSerializeAndSendCrt(game, chosen, hostHoldsThisWorld));
            return true;
        }


        // Coroutine: read the save to bytes, then chunk+send, then prepare host entry + open barrier.
        private IEnumerator<NextUpdate> HostSerializeAndSendCrt(PhoenixGame game, SavegameMetaData metaData,
                                                               bool hostHoldsThisWorld = false)
        {
            var result = new ByRef<byte[]>();
            yield return Timing.Current.Call(game.SaveManager.Serializer.ReadSavegameBinary(metaData, result));

            var blob = result.Value;
            if (blob == null || blob.Length == 0)
            {
                // The boundary was ANNOUNCED before this coroutine started (lobby PLAY press / new-campaign
                // arm), so every peer — this host included, since CurtainHoldArmed reads the announcement —
                // is already behind a curtain waiting for bytes that will never exist. Take it back down.
                BroadcastLoadBoundaryAbort("the save produced no bytes");
                Debug.LogError("[Multiplayer] Save serialization produced no bytes; aborting transfer.");
                yield break;
            }

            var ext = System.IO.Path.GetExtension(metaData.Path);
            if (string.IsNullOrEmpty(ext)) ext = SerializationComponent.DefaultExtension;

            SendBlob(blob, ext);

            if (hostHoldsThisWorld)
            {
                // ONE BOUNDARY, ONE LOAD PER PEER — the host does not re-enter a world it IS (law L174).
                // This is the SAME refusal HostBeginTacticalEntryTransfer makes (:708 "the host does NOT
                // re-enter from the blob (it is already in this live tactical level)"), adopted on the one
                // geoscape boundary that has the same property: the new-campaign bootstrap's blob is an
                // AUTOSAVE OF THE HOST'S OWN LIVE CAMPAIGN, taken seconds earlier by
                // NewCampaignAutosaveAndTransferCrt. Re-entering re-deserialized the world the host had just
                // built, which is the "creating a new game makes the HOST load twice" report: measured
                // 2026-08-07 on the host log — geoscape playable 23:09:04.174, then BEGIN 23:09:07.437 and a
                // second Playing at 23:09:16.631, 9.2 s of loading for bytes the host already was. 17bf9fe
                // removed the interactive geoscape that used to FLASH between the two; it could not remove
                // the second load, because the second load is not a bug in the curtain.
                //
                // WHAT PAYS FOR THE SAME-BYTES INVARIANT NOW. Every other peer still builds from the blob, so
                // only the host keeps a graph it minted rather than deserialized. That is exactly what the
                // rail's law-7 drift backstop already polices, and it needed no new mechanism: every client
                // CRCs one geoscape root per second with the host's own canonical walk
                // (GenericApplier.ClientCrcTick → DiffEngine.RootCrc) and the host compares against its live
                // graph in DiffEngine.HandleCrcReport — "the ONE thing in the rail that ever compares host and
                // client state". A keying or structural disagreement between the world the host created and
                // the world the clients loaded therefore surfaces within a sweep as
                // "CRC backstop: root '<key>' DIVERGED on peer <id>" and force-re-emits, instead of becoming
                // a mystery three sessions later.
                _engine.Sync?.ResetForReloadBoundary(); // the ONE side effect of PrepareEntryFromBlobCrt the
                                                       // host still owes: its geoscape WAS replaced at this
                                                       // boundary — by the native creation, not by a re-entry
                                                       // — so the in-flight state pointing at the old one goes
                                                       // now. Idempotent by its own contract.
                _begun = true;      // never left the level ⇒ Begin()'s EnterLevel() self-guards to a no-op,
                                    // while the barrier flag still vetoes BeginSuppressed so SessionBegin —
                                    // which is what releases the CLIENTS into their load — still broadcasts.
                OpenBarrier();      // clears _loadCompleteSent/_reachedPlaying/_tracker/_slotProgress, so
                                    // everything below must come after it.
                _hostLoaded = true;
                // No Loaded→Playing edge is coming for this peer, so OnReachedPlaying will never fire and
                // AllDone could never hold. Report in explicitly — the same pair the tac-entry tail uses
                // (:786/:791) for the same reason — and publish the terminal row so clients render the host
                // bar complete instead of stuck at 0%.
                SendLoadComplete();
                _slotProgress[_engine.Session.LocalSlotIndex] = (1, 100);
            }
            else
            {
                // Host prepares its own entry from the SAME bytes (in memory), then waits at the barrier.
                yield return Timing.Current.Call(PrepareEntryFromBlobCrt(game, blob, ext));

                OpenBarrier();
                // Host counts as loaded immediately — under the dedicated sentinel flag, NOT an id key in
                // _loadedPeers, so it can never collide with a peerId-0 client ack on DirectIP (fix #2).
                _hostLoaded = true;
            }
            TryReleaseBarrier();
        }

        // Split the blob into SaveChunk messages (sequence by offset), then a SaveDone with crc32.
        private void SendBlob(byte[] blob, string ext)
        {
            var crc = Crc32(blob);
            var chunkCount = (int)((blob.Length + ChunkSize - 1) / ChunkSize);
            _blobPending.Clear();
            _blobInFlight.Clear();
            foreach (var peer in _engine.Session.GetConnectedClients()) _blobPending.Enqueue(peer);
            _blob = blob; _blobExt = ext; _blobCrc = crc; _blobTransferId = _transferId;
            Debug.Log($"[Multiplayer] SendBlob: bytes={blob.Length} chunks={chunkCount} crc=0x{crc:X8} " +
                      $"peers={_blobPending.Count} concurrency={MaxConcurrentBlobPeers}");
            PumpBlobQueue();
        }

        // How many peers may have the save blob sitting in their send queue at once. BroadcastToAll used
        // to enqueue the WHOLE blob for EVERY peer in a single synchronous burst: at a 5 MB save and 50
        // players that is 250 MB of host-side send queue committed in one frame — the memory spike, the
        // uplink stall that makes everyone's first minute feel like a dead connection, and the only
        // realistic way DirectTransport's per-peer queue cap is ever reached. Staggering changes NOTHING
        // on the wire per peer (same chunks, same order, same SaveDone) and nothing on any client; it only
        // stops the host committing the whole roster's bytes at once. Four is a plain bound, not a tuned
        // one: it keeps the host's uplink saturated while capping the commitment at ~4 blobs.
        private const int MaxConcurrentBlobPeers = 4;
        private readonly Queue<ulong> _blobPending = new Queue<ulong>();
        private readonly List<ulong> _blobInFlight = new List<ulong>();
        private byte[] _blob; private string _blobExt; private uint _blobCrc; private Guid _blobTransferId;

        /// <summary>Hand the start blob to the next few peers. Driven from <see cref="Update"/>, so a peer
        /// that joins/leaves mid-distribution needs no special case: a departed peer settles out of the
        /// in-flight set on the next pump and a new one arrives through the ordinary on-demand join.</summary>
        private void PumpBlobQueue()
        {
            if (_blob == null) return;
            _blobInFlight.RemoveAll(BlobSettled);
            while (_blobInFlight.Count < MaxConcurrentBlobPeers && _blobPending.Count > 0)
            {
                var peer = _blobPending.Dequeue();
                if (!_engine.Session.Clients.ContainsKey(peer)) continue; // left before its turn
                SendBlobCore(_blob, _blobExt, _blobTransferId, _blobCrc, onDemandJoin: false,
                             m => _engine.SendToClient(peer, m));
                _blobInFlight.Add(peer);
                Debug.Log($"[Multiplayer] SendBlob: blob queued for peer={peer} " +
                          $"(inFlight={_blobInFlight.Count} pending={_blobPending.Count})");
            }
            if (_blobPending.Count == 0 && _blobInFlight.Count == 0)
            {
                Debug.Log("[Multiplayer] SendBlob: every peer served — releasing the blob.");
                _blob = null; _blobExt = null; // the bytes are the biggest thing this class holds
            }
        }

        /// <summary>A peer stops occupying a concurrency slot once it has the whole blob (download 100%),
        /// has acked LOADED, or is no longer on the roster. Never a timeout: a slow peer keeps its slot
        /// and keeps downloading — the mandate forbids giving up on it (N=50).</summary>
        private bool BlobSettled(ulong peer)
            => !_engine.Session.Clients.ContainsKey(peer)
               || _loadedPeers.Contains(peer)
               || (_peerDownloadPct.TryGetValue(peer, out var pct) && pct >= 100);

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
        /// transfer in flight — checked inline below). Unlike F2/lobby
        /// the host does NOT re-enter from the blob (it is already in this live tactical level). Returns
        /// true iff the write+send coroutine launched.
        /// </summary>
        // ── LIVE since tactical arc A1 ───────────────────────────────────────────────────────
        // Caller: Tactical/TacticalEntry.cs TacDeployReadyCapture (host, at deploy-ready). The launch
        // half (OpenTacticalEntryBarrier) is armed by TacLaunchGate on the same file. A1 deliberately
        // adds NO wire surface: a geo→tac transition is a join into a level, so it rides law 1's native
        // save-loader transfer rather than a snapshot on the delta path.
        public bool HostBeginTacticalEntryTransfer()
        {
            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return false;

            // Same preconditions the F2/lobby path enforces, plus the tactical-only ones. Refusing is
            // never silent: the caller turns a false into AbortTacticalEntryTransfer, because the
            // reveal-hold armed at launch would otherwise park every peer forever.
            if (!_engine.IsHost || !SessionStarted || TransferActive || !saveManager.IsTactical)
            {
                Debug.LogWarning($"[Multiplayer] HostBeginTacticalEntryTransfer blocked: host={_engine.IsHost} " +
                                 $"sessionStarted={SessionStarted} transferActive={TransferActive} " +
                                 $"tactical={saveManager.IsTactical}");
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
            // ARM SYNCHRONOUSLY, NOT INSIDE THE COROUTINE. The mid-tactical save write below takes ~1.15 s
            // (live: bytes=1580544 ms=1151), and the barrier used to open only AFTER it — a window in which
            // the previous load's LoadCompletes were still landing in a barrier nobody owned. Opening here
            // means every ack that arrives during the write is already keyed to THIS entry: the tracker is
            // clean, and AllDone cannot fire because the host does not mark itself done until the write
            // finishes (SendLoadComplete). _loadPhaseActive is re-stated on top of OpenBarrier's own arm
            // because THIS is the method the law pins for it: the hold must be armed synchronously here, not
            // one save-write later, or the liveness give-up in Update() has no window to cover.
            OpenBarrier();
            _loadPhaseActive = true;
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
                // Release the reveal-hold + tell clients — otherwise every peer wedges (live 2026-07-13).
                AbortTacticalEntryTransfer("no save bytes (mid-tactical save write failed)");
                yield break;
            }
            Debug.Log($"[Multiplayer] tac-entry: host mid-tactical save written bytes={blob.Length} ms={NowMs() - t0}");

            SendBlob(blob, SerializationComponent.DefaultExtension);

            // (OpenBarrier already ran SYNCHRONOUSLY in HostBeginTacticalEntryTransfer, before this write —
            // see the comment there. Re-opening here would wipe the acks that arrived during the write.)
            _hostLoaded = true;   // host holds its state locally (already in the level) → counts as loaded
            SendLoadComplete();   // host is past Playing → mark its slot done (+ TryReleaseBarrier: client not loaded yet)
            // Overlay fix (2026-07-13): on tac-entry the host never runs the phase-2 pump (_loadCompleteSent is
            // already true), so the RosterProgress snapshot carried NO host row and clients rendered the host
            // bar stuck at 0% forever. Publish the terminal value once (OpenBarrier just cleared the aggregate);
            // every ≤20 Hz snapshot then ships it and clients render the host row COMPLETE.
            _slotProgress[_engine.Session.LocalSlotIndex] = (1, 100);
            Debug.Log("[Multiplayer] tac-entry: blob sent, LOADED barrier open, host marked loaded/done " +
                      "(reveal-hold armed at launch, no self-enter)");
        }

        /// <summary>
        /// HOST: the tac-entry transfer can never complete (mid-tactical save write failed / transfer never
        /// started). The reveal-hold armed at LAUNCH (<see cref="OpenTacticalEntryBarrier"/>) would otherwise
        /// park the host's curtain forever waiting for clients that will never load, while every client wedges
        /// on "Downloading mission…" until its 60s watchdog fires a self-launch a sim-frozen mirror cannot honor
        /// (live failure 2026-07-13: StructuralTarget save-write NRE → all three peers stuck). Release the hold
        /// NOW (<see cref="PerformDeferredLift"/>: un-park + self-reveal, once-guarded) and broadcast
        /// <see cref="PacketType.EntryTransferAbort"/> so every client immediately drops its stashed deploy and
        /// lifts its curtain back to the live geoscape mirror. Host-only; safe to call more than once.
        /// </summary>
        public void AbortTacticalEntryTransfer(string reason)
        {
            if (!_engine.IsHost) return;
            Debug.LogError("[Multiplayer] tac-entry transfer ABORT (" + reason + ") — self-reveal + notify clients");
            try
            {
                _engine.BroadcastToAll(new NetworkMessage(PacketType.EntryTransferAbort,
                    MessageSerializer.SerializeEntryTransferAbort(reason)));
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] EntryTransferAbort broadcast failed: " + e.Message); }
            // Close the barrier this entry opened (HostBeginTacticalEntryTransfer opens it synchronously,
            // BEFORE the save write that can fail into here). Leaving it open would pin TransferActive true
            // and make every LATER mission refuse to start — the abort must not outlive itself.
            _barrierOpen = false;
            _loadPhaseActive = false;
            // The entry this abort ends must not leave its arm behind for the next battle (law L122). Stated
            // here as well as in PerformDeferredLift because that one is once-guarded on _revealed: an abort
            // arriving after something else already lifted would otherwise skip the expiry.
            Multiplayer.Tactical.TacLaunchGate.DisarmSessionEntry();
            PerformDeferredLift();
        }

        /// <summary>
        /// CLIENT: the host's tac-entry transfer aborted — no save is coming to build our tactical level.
        /// Drop the stashed deploy + stall watchdog and lift the curtain (the live geoscape mirror is still
        /// underneath). The host ignores its own broadcast.
        /// </summary>
        public void OnEntryTransferAbort(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            string reason = "";
            try { reason = MessageSerializer.DeserializeEntryTransferAbort(msg.Payload); } catch { /* reason is diagnostics-only */ }
            Debug.LogError("[Multiplayer] tac-entry transfer aborted by host (" + reason + ") — lifting curtain");
            // No deploy stash to drop (A1 ships no snapshot surface — the save transfer IS the entry). What
            // MUST be undone is the curtain this client dropped on the first chunk: no native level-load is
            // coming to lift it, and OnSaveChunk also cleared _revealed, so the curtain GATE would park
            // every lift forever. PerformDeferredLift is the RCA-hardened release (opens the gate, then
            // resumes the PARKED native lift instead of racing it with a second direct LiftCurtain — see
            // its own comment). MultiplayerUI.TacLoadAbort is deliberately NOT used here: its raw
            // LiftCurtainEarly is exactly the competing-tail bug that latched the input lock.
            ResetRx();
            if (_downloadCurtain)
            {
                _downloadCurtain = false;
                Multiplayer.UI.NativeWidgetFactory.EndDownloadBar();
            }
            PerformDeferredLift();
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
        /// <summary>
        /// A peer that JOINED at a moment the host could not serve it (mid-battle, mid-load, or already
        /// transferring). NOBODY IS REFUSED (N=50 mandate) — it keeps its lobby seat and is served the
        /// instant the host can, one at a time. Sequencing is the point: fifty joiners arriving together
        /// used to mean fifty simultaneous 5 MB captures, which is the same burst the start path had.
        /// </summary>
        public void DeferOnDemandJoin(ulong peerId)
        {
            if (!_deferredJoins.Contains(peerId)) _deferredJoins.Add(peerId);
        }

        private readonly List<ulong> _deferredJoins = new List<ulong>();

        /// <summary>Serve ONE waiting joiner per pump, and only when the host is somewhere it can produce
        /// a geoscape save with nothing else in flight. Driven from <see cref="Update"/>.</summary>
        private void PumpDeferredJoins()
        {
            if (_deferredJoins.Count == 0 || !_engine.IsHost) return;
            if (TransferActive || !Sync.GeoRuntime.Instance.IsGeoscapeActive) return;
            var peer = _deferredJoins[0];
            _deferredJoins.RemoveAt(0);
            if (!_engine.Session.Clients.ContainsKey(peer)) return; // left while waiting
            Debug.Log($"[Multiplayer] Deferred join: host is back on the Geoscape → onboarding peer={peer} " +
                      $"({_deferredJoins.Count} still waiting).");
            HostOnDemandJoin(peer);
        }

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

        // Pure single-shot latch (Core, pinned EXECUTED by RailCheck L134): armed at the native new-game
        // CONFIRM (NewCampaignInterceptPatch), attempted at the geoscape-READY seam (GeoscapeReadyPatch →
        // OnGeoscapeReady), and consumed by an OUTCOME — ConcludeNewCampaignBootstrap, never by an
        // evaluation. A refused evaluation stays armed; a failed attempt says so out loud.
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
            {
                _engine.Session?.SystemChat(NewCampaignCreatingNotice);
                // THE ENTER HALF. A chat line is not a loading screen: everything after this call is native
                // campaign creation on the HOST, which curtains alone, and the clients' only enter used to be
                // the arrival of the first save byte (:1473-1475) — 11.4 s later in the 2026-08-06 session.
                // Same instant, same arm edge, same idempotence as the notice above.
                BroadcastLoadBoundaryBegin("new-campaign");
            }
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
            // The arm curtained every client; the disarm must un-curtain them, or a host that backs out of the
            // native new-game settings leaves two peers on a loading screen for a campaign nobody is creating.
            BroadcastLoadBoundaryAbort("host left the new-campaign flow");
            Debug.Log("[Multiplayer] New-campaign co-op bootstrap disarmed.");
        }

        /// <summary>The lobby system-chat line clients see when the bootstrap could not be completed.</summary>
        public const string NewCampaignFailedNotice =
            "— the host could not start the new campaign; you are still in the lobby, the host can try again —";

        /// <summary>
        /// CurtainShowPatch "Playing" seam: this peer just reached a playable frame. THE BOOTSTRAP DOES
        /// NOT FIRE HERE, and that is the whole fix. Level.State.Playing is the edge on which
        /// GeoLevelController.OnLevelStart merely STARTS LevelCrt (GeoLevelController.cs:377-379 → :464):
        /// not one line of it has run, so no faction sub-manager exists yet and AutosaveGame() throws
        /// NullReferenceException in GeoAlienFaction.RecordExtendedInstanceData (AlienRaidManager is
        /// constructed later, at GeoLevelController.cs:651-653 → GeoFaction.cs:418 → GeoAlienFaction.cs:246).
        /// Worse, an autosave started here runs on the geoscape Timing INTERLEAVED with LevelCrt's whole
        /// init block — the collection-modified exception that killed LevelCrt two seconds later.
        ///
        /// All this seam does now is ARM the liveness deadline; the attempt starts at
        /// <see cref="OnGeoscapeReady"/>. No-op on every peer that never armed (clients, single-player).
        /// </summary>
        public void OnNewCampaignPlayableFrame()
        {
            if (!_newCampaign.Armed || _newCampaign.Firing) return;
            if (!Sync.GeoRuntime.Instance.IsGeoscapeActive) return;
            _newCampaign.NoteGeoscapeEntered(NowMs());
            Debug.Log("[Multiplayer] New-campaign bootstrap: geoscape playable — waiting for the geoscape-ready " +
                      "seam (ModManager.OnGeoscapeStart), deadline " + NewCampaignBootstrap.ReadyTimeoutMs + " ms.");
        }

        /// <summary>
        /// THE geoscape-READY seam — the game's OWN "the geoscape is fully initialised" callback,
        /// GeoLevelController.cs:757 (<c>ModManager.GetInstance().OnGeoscapeStart(this)</c>), the last line
        /// of LevelCrt before it parks in its GameOverCheck loop (:762). Everything a savegame write walks
        /// is already built by then: factions and their sub-managers (:651-661), the event + objective
        /// systems (:655-657), the scanner history and Phoenixpedia (:672-673), the mission scheduler
        /// (:682) and the view state (:735). No frame counting, no polling a hand-picked field, and it
        /// stays correct however long a mod makes that init take — the game itself decides when it is done.
        ///
        /// Single START point of the armed bootstrap: when the fire guard is open (still host, session
        /// live, no transfer in flight) the host autosaves the freshly created campaign and feeds that
        /// autosave into the EXISTING chunked transfer + LOADED/BEGIN barrier (LaunchTransfer), exactly
        /// like a lobby start / F2 reload. The latch is NOT consumed here — see NewCampaignBootstrap.
        /// </summary>
        public void OnGeoscapeReady()
        {
            if (!_newCampaign.Armed) return;
            bool geoscape = Sync.GeoRuntime.Instance.IsGeoscapeActive;
            // Captured BEFORE the transfer resets _begun: a mid-session second fresh campaign is an
            // F2-analog reload (clients hold pre-existing channel state) → arm the rca-4 re-seed.
            bool wasStarted = SessionStarted;
            if (!_newCampaign.TryFire(_engine.IsHost, _engine.IsActiveSession, geoscape, TransferActive))
            {
                if (geoscape && !_newCampaign.Firing)
                    Debug.LogWarning("[Multiplayer] New-campaign bootstrap reached the READY seam but the " +
                                     "fire guard is closed (transfer in flight or session gone) — still armed; " +
                                     "the liveness deadline releases everyone if it never opens.");
                return;
            }

            PhoenixGame game;
            PhoenixSaveManager saveManager;
            var timing = TryGetGame(out game, out saveManager) ? GetTiming() : null;
            if (timing == null)
            {
                ConcludeNewCampaignBootstrap("the game's SaveManager/Timing could not be resolved at the " +
                                             "geoscape-ready seam");
                return;
            }

            Debug.Log("[Multiplayer] New-campaign bootstrap: geoscape READY → autosave + transfer.");
            timing.Start(NewCampaignAutosaveAndTransferCrt(saveManager, reseedAfterReveal: wasStarted));
        }

        // Host liveness backstop, pumped from Update: the geoscape reached a playable frame but never
        // reported ready (a mod threw inside LevelCrt, the level was torn down mid-init, …). Postulate 2
        // forbids a permanent wait, so the deadline concludes the bootstrap out loud instead.
        private void PumpNewCampaignWatchdog()
        {
            if (!_newCampaign.ReadyWaitExpired(NowMs())) return;
            ConcludeNewCampaignBootstrap("the geoscape never reported ready (ModManager.OnGeoscapeStart) " +
                                         "within " + NewCampaignBootstrap.ReadyTimeoutMs +
                                         " ms of its playable frame");
        }

        /// <summary>
        /// THE one place the bootstrap's single shot is spent — on the launch and on every failure path
        /// alike. NO SILENT SWALLOW (this repo's dominant bug class): a failure is an ERROR line naming the
        /// real cause, a notice to the clients on the SAME system-chat rail that told them the campaign was
        /// being created, and — stated as evidence, not assumed — the host's own curtain gate, which holds
        /// only while a session is STARTED and un-revealed and is therefore already open on every path that
        /// gets here (no barrier was opened: LaunchTransfer either never ran or restored the flags it
        /// touched). The host stays playable solo; the clients stay in an unlocked lobby.
        /// </summary>
        private void ConcludeNewCampaignBootstrap(string failure)
        {
            _newCampaign.Conclude();
            if (failure == null)
            {
                Debug.Log("[Multiplayer] New-campaign bootstrap concluded: transfer launched.");
                return;
            }
            // THE ARM CURTAINED EVERY PEER; A FAILURE MUST TAKE THAT CURTAIN BACK DOWN. Only the BACK
            // button used to abort the announced boundary, so a bootstrap that died anywhere else left
            // every client on a loading screen for a campaign nobody was creating any more, told about it
            // in one chat line they could not see behind that screen — an unbounded wait with no reason
            // named where the waiting happens. It also clears _loadBoundaryAnnounced, which the host's own
            // CurtainHoldArmed now reads: without this the host would strand itself the same way.
            BroadcastLoadBoundaryAbort("new-campaign bootstrap failed");
            Debug.LogError("[Multiplayer] New-campaign co-op bootstrap FAILED: " + failure +
                           " — no transfer was launched. Host continues solo (curtain gate: " +
                           Multiplayer.Harmony.CurtainTakedownGate.State() + "); clients told over system chat.");
            _engine.Session?.SystemChat(NewCampaignFailedNotice);
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
        // SINGLE EXIT BY CONSTRUCTION. Every former `yield break` is now a `failure` string that falls
        // through to the ONE ConcludeNewCampaignBootstrap call at the bottom, and the whole non-yielding
        // tail sits in a try/catch — because the defect this fixes was not the autosave throwing, it was
        // an exit path that spent the one shot and told nobody. There is no yield after the CallSafe, so
        // the try/catch is legal C# here and covers a LaunchTransfer that throws as well.
        private IEnumerator<NextUpdate> NewCampaignAutosaveAndTransferCrt(
            PhoenixSaveManager saveManager, bool reseedAfterReveal)
        {
            var oldAutoSave = saveManager.AutoSave;
            var ex = new ByRef<Exception>();
            yield return Timing.Current.CallSafe(saveManager.AutosaveGame(), ex);

            string failure = null;
            try
            {
                var meta = saveManager.AutoSave;
                if (ex.Value != null)
                {
                    failure = "autosave capture failed: " + ex.Value;
                }
                else if (!SessionLifecycle.FreshAutosaveCaptured(oldAutoSave, meta))
                {
                    failure = "no fresh autosave was captured (write failure?) — use CHOOSE SAVE with a " +
                              "manual save instead";
                }
                else
                {
                    // Same terminal-flag reset as HostStartSessionInGame: a mid-session second fresh campaign
                    // re-runs the SAME barrier/reveal state machine; on a lobby first start these are already
                    // false (no-op). OpenBarrier itself resets the per-run state per fresh barrier. Same
                    // restore-on-failure too: a failed launch must not strand a mid-session host with
                    // SessionStarted==false inside a live co-op level.
                    bool wasBegun = _begun, wasLoadComplete = _loadCompleteSent, wasRevealAll = _revealAllSent;
                    _begun = false;
                    _loadCompleteSent = false;
                    _revealAllSent = false;
                    // hostHoldsThisWorld: THIS is the boundary where the blob is an autosave of the world the
                    // host is standing in (taken by the CallSafe above, seconds ago) — so the host must not
                    // load it back. Law L174; flip this one argument to false to restore the re-entry.
                    if (!LaunchTransfer(meta, hostHoldsThisWorld: true))
                    {
                        _begun = wasBegun;
                        _loadCompleteSent = wasLoadComplete;
                        _revealAllSent = wasRevealAll;
                        failure = "the transfer launch was refused (see the prior log line)";
                    }
                    else if (reseedAfterReveal) _reseedGate.Arm();
                }
            }
            catch (Exception e)
            {
                failure = "unexpected exception after the autosave: " + e;
            }
            ConcludeNewCampaignBootstrap(failure);
        }

        private void OpenBarrier()
        {
            NoteProgress(); // flag edge (host barrier opens)
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
            _loadPhaseActive = true;  // a reveal hold is armed on the next line — and stays armed until it lifts
            // Second barrier (reveal) state — fresh session.
            _reachedPlaying = false;
            _revealed = false;
            _revealAllSent = false;
            _lastBarrierWaitLogMs = 0;
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
        /// relaxation. Caller (arc A1): Tactical/TacticalEntry.cs TacLaunchGate, host arm.
        /// </summary>
        public void OpenTacticalEntryBarrier()
        {
            _hostEntryHold = true;
            _revealed = false;        // ordering-critical: arm the hold before Loaded→Playing
            _reachedPlaying = false;  // so OnReachedPlaying fires again at the tactical Playing (label + host done-mark)
            // A NEW load starts HERE, so the LAST one's terminal rows must not survive into it. Merge is
            // monotone: a stale slot-0 (1,100) rejects every phase-0 sample the pump is about to publish,
            // and a client merging nothing renders the host row FULL from the first frame of a load that
            // has not begun. Same reset OpenBarrier / ArmSelfLoadBarrier already do at their own arm
            // points — this path had none, because it never published anything to go stale before.
            _slotProgress.Clear();
            _tracker.Reset();
            _lastSnapshotMs = -1;
            _lastReportedLoadPct = -1;
            // THE HOLD IS ARMED, SO SAY SO — this used to be `false`, and that is what made the hang possible.
            // The write was standing in for "the PREVIOUS load's aggregation has ended", because with the
            // aggregation still running its AllDone reveals THIS entry for us (the 2026-08-05 live run: host
            // revealed at frame 2705, clients parked for 3 minutes). But both release branches in Update()
            // were gated on this same flag, so clearing it here left the hold armed with nothing able to
            // release it — for the whole ~13 s entry load, and permanently whenever the L122 guard returns
            // early or the deploy-ready coroutine never reaches the transfer.
            //
            // The stale-aggregation half is NOT lost: _tracker.Reset() above drops the previous load's done
            // set, and the AllDone branch below is gated on the HOST'S OWN done-mark, which this path clears
            // and does not restore until deploy-ready (SendLoadComplete in HostTacticalEntryTransferCrt). A
            // peer still finishing the previous load may re-fill its own row all it likes; AllDone cannot
            // hold while slot 0 is missing from it.
            _loadPhaseActive = true;
            // ONE REVEAL ENDS ONE ENTRY (law L122). The arm used to live on TacLaunchGate.Prefix and be
            // cleared ONLY by a consume, so a launch that never reached tactical Playing — aborted
            // deployment, refused launch, failed level load — left it set for the rest of the process and the
            // NEXT battle loaded from a save consumed it, which is the double-load bug returning by the back
            // door. Its lifetime is exactly this barrier's, so the barrier owns both ends of it (armed here,
            // expired in PerformDeferredLift).
            Multiplayer.Tactical.TacLaunchGate.ArmSessionEntry();
            Debug.Log($"[Multiplayer] host reveal-hold armed (tac-entry): sessionStarted={SessionStarted} " +
                      $"revealed={_revealed} — host holds its loading screen until all clients load-complete.");

            // Law L71 — THE CURTAIN IS EVERYONE'S, and it falls when the LOAD starts, not when this peer's
            // own bytes start arriving. Everything above is host-LOCAL state; before this line the clients
            // learned a battle was starting only in OnSaveChunk's first-chunk branch, which is after the
            // host's deploy-ready wait + mid-tactical save write: 13.0 s of fully interactive geoscape in
            // the 2026-07-31 run (host 00:24:06.037 "reveal-hold armed" vs clients 00:24:19.040/00:24:19.061
            // "OnSaveChunk FIRST"). A peer that can still click for 13 s is not merely a UX complaint — it
            // is how a peer ends up INSIDE a sub-screen when its level is torn down, which is law L70's
            // blocker. So this broadcast is the preventive half of that fix, not decoration.
            // Emitted INLINE and not through BroadcastLoadBoundaryBegin below: L71's `never-announced` arm
            // (Program.cs:9222) asserts this seam by DIRECT callee (`Reaches` walks CalleeSequence, not the
            // transitive graph), so hiding the send behind a helper would take that law down with it. Same
            // packet, same instant, same meaning as the helper — L143 asserts the three seams together.
            Debug.Log("[Multiplayer] load boundary (tac-entry): broadcasting EntryTransferBegin — every peer curtains NOW.");
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EntryTransferBegin));
        }

        /// <summary>
        /// HOST, the ENTER half of "everyone enters the loading screen at the same time and leaves it at the
        /// same time". The LEAVE half is the reveal barrier (L94 arm (n)); this is its mirror, and it is the
        /// SAME mechanism at every load boundary — <see cref="PacketType.EntryTransferBegin"/> (0x48), whose
        /// body was already fully generic, only ever wired to the tactical entry.
        ///
        /// BROADCAST-AND-GO: there is no ack, no quorum and no wait of any kind here (postulate 2). The host
        /// tells every peer to curtain and then curtains itself in the very next statement; a peer that is
        /// gone leaves the ROSTER, and a peer that is merely slow is waited on by the EXISTING exit barrier
        /// (<c>AllDone(GetRosterSlots())</c>) and by nothing added here.
        ///
        /// Call it at the instant the boundary is DECIDED, not when this peer's own bytes start moving — the
        /// missing enters were exactly the two seams where the host's decision produced host-local work first:
        /// the lobby PLAY press and the new-campaign arm. Measured cost of the gap in the 2026-08-06 session:
        /// host curtained at 21:16:58.750, client only at 21:17:10.126 — 11.4 s of one screen loading alone.
        /// </summary>
        public void BroadcastLoadBoundaryBegin(string seam)
        {
            if (!_engine.IsHost) return;
            // Law L71 — THE CURTAIN IS EVERYONE'S, and it falls when the LOAD starts, not when this peer's own
            // bytes start arriving. A peer that can still click while another peer loads is not merely a UX
            // complaint — it is how a peer ends up INSIDE a sub-screen when its level is torn down (law L70).
            Debug.Log("[Multiplayer] load boundary (" + seam +
                      "): broadcasting EntryTransferBegin — every peer curtains NOW.");
            // THE ANNOUNCEMENT AND THE PUBLISH WINDOW ARE THE SAME EVENT. Telling every peer to curtain and
            // then saying nothing for the next 14 s is what put them on an empty bar; from here the phase-2
            // pump samples the host's own native load and the snapshot gate lets it out (HostEntryLoad).
            // The two resets are the client-side pair of OnEntryTransferBegin's _tracker.Reset(): Merge is
            // monotone, so the PREVIOUS load's terminal (1,100) for this slot would reject every phase-0
            // sample the pump is about to publish and the bar would read FULL from the first frame —
            // L118 arm (c), which OpenTacticalEntryBarrier already obeys on its own path.
            _loadBoundaryAnnounced = true;
            _slotProgress.Clear();
            _tracker.Reset();
            _lastSnapshotMs = -1;
            _lastReportedLoadPct = -1;
            try { _engine.BroadcastToAll(new NetworkMessage(PacketType.EntryTransferBegin)); }
            catch (Exception e) { Debug.LogError("[Multiplayer] EntryTransferBegin broadcast failed: " + e.Message); }
        }

        /// <summary>
        /// HOST: the boundary announced by <see cref="BroadcastLoadBoundaryBegin"/> will never produce a load
        /// (the start failed at the press, or the host backed out of the new-game flow). Take the curtain back
        /// down on every peer over the EXISTING abort packet, whose client handler already un-curtains through
        /// the RCA-hardened <c>PerformDeferredLift</c>.
        ///
        /// Deliberately NOT <see cref="AbortTacticalEntryTransfer"/>: that one also tears down a barrier and
        /// self-reveals, and at these two seams no barrier was ever opened — there is nothing to close and
        /// lifting a curtain the host itself never held would be a second bug.
        /// </summary>
        public void BroadcastLoadBoundaryAbort(string reason)
        {
            if (!_engine.IsHost) return;
            Debug.LogWarning("[Multiplayer] load boundary ABORT (" + reason + ") — every peer un-curtains.");
            // The announced load will never happen, so the publish window it opened closes with it.
            _loadBoundaryAnnounced = false;
            try
            {
                _engine.BroadcastToAll(new NetworkMessage(PacketType.EntryTransferAbort,
                    MessageSerializer.SerializeEntryTransferAbort(reason)));
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] EntryTransferAbort broadcast failed: " + e.Message); }
        }

        /// <summary>
        /// CLIENT: a tactical entry has begun on the host. Drop the native curtain immediately — no save
        /// bytes exist yet (the host still has to reach deploy-ready and write its mid-tactical save), so
        /// this uses <c>EnterTacLoadCurtain</c> and NOT <c>EnterDownloadLoadingScreen</c>: the latter's
        /// "Downloading save…" label would be a lie for the next ~13 s, and its lobby hide is a menu-only
        /// concern with nothing to do here (this fires IN-GAME, on a live geoscape). That is exactly the
        /// case <c>EnterTacLoadCurtain</c> was written for and never wired to.
        /// The first-chunk drop in <see cref="OnSaveChunk"/> stays as it is and simply relabels when the
        /// real download starts — both halves are idempotent (DropCurtainEarly is instant-and-idempotent,
        /// BeginDownloadBar reassigns, and SetCurtainLabel captures the native string only on the FIRST
        /// call so RestoreCurtainLabel still puts the right text back).
        /// <c>_downloadCurtain</c> is set here on purpose: it is the flag <see cref="OnEntryTransferAbort"/>
        /// tests to take our bar back down, and an early curtain with the flag unset would leave a peer
        /// staring at OUR label after an abort lifted the curtain out from under it.
        /// </summary>
        public void OnEntryTransferBegin(NetworkMessage msg)
        {
            if (_engine.IsHost) return;   // the host ignores its own broadcast (0x47 does the same)
            if (_downloadCurtain) return; // already curtained (duplicate delivery / a transfer in flight)
            Debug.Log("[Multiplayer] load boundary BEGUN on the host — dropping the curtain now (no bytes yet).");
            _downloadCurtain = true;
            _rxStarted = false;   // (a): the curtain is up and NOT one byte has been sent yet
            // The client half of the host's reset above: our tracker still carries the LAST load's
            // terminal (1,100) for slot 0, and Merge would reject every phase-0 host sample against it —
            // the bar would read 100% before the host had loaded a thing.
            _tracker.Reset();
            Multiplayer.UI.MultiplayerUI.Instance?.EnterTacLoadCurtain(LoadBoundaryWaitLabel);
        }

        /// <summary>The label every 0x48 curtain wears, whatever boundary raised it. Deliberately says nothing
        /// about a battle: the same packet now announces the lobby PLAY press and the new-campaign arm, and
        /// "the host is working, you are waiting" is the one true statement at all three.</summary>
        public const string LoadBoundaryWaitLabel = "Waiting for host…";

        /// <summary>
        /// Tactical→geoscape RETURN barrier: re-arm the synchronized-reveal hold on THIS peer at tactical
        /// teardown (TacticalLevelEndPatch fires on host AND clients). The return has NO save transfer
        /// (clients ride the native mission-end to the geoscape), so nothing else resets _revealed — the
        /// first peer to finish its geoscape load used to lift instantly while the rest still loaded
        /// (live RCA 2026-07-16). Re-arming here re-enters the EXISTING machinery unchanged:
        /// OnReachedPlaying → hold + LoadComplete; host Update() aggregates → AllDone → RevealAll →
        /// simultaneous lift. InPhase2 (=_begun &amp;&amp; !_loadCompleteSent) goes true again, so the
        /// overlay + per-slot progress pump also work for free. The only opener is roster shrink on
        /// peer-left; there is no timed belt, by ruling.
        ///
        /// ONE CALLER, AND IT IS THE FUNNEL: <c>LoadBarrierGate</c> on <c>PhoenixGame.FinishLevel</c>. The
        /// second arm this used to have (<c>TacLevelEndBarrier</c>, on the tactical level leaving Playing)
        /// was always the no-op half — FinishLevel runs first on that very path — and is deleted.
        /// </summary>
        public void OpenReturnBarrier()
        {
            if (!_revealed || _barrierOpen) return; // already armed / an entry transfer owns the state
            ArmSelfLoadBarrier("tac→geo return");
        }

        /// <summary>
        /// THE SELF-LOAD ARM: every peer loads its OWN level natively and nothing ships a save, so there is no
        /// chunk transfer to open the LOADED barrier and no <c>OnSaveChunk</c> to re-arm the reveal hold — the
        /// two things this method does instead. Extracted from <see cref="OpenReturnBarrier"/> (whose whole
        /// body it was) because the native tactical ENTRY experiment (law L103,
        /// <c>NativeTacticalEntry.Enabled</c>) is the same shape in the other direction: geo→tac with every
        /// peer building locally. Same machinery downstream, unchanged: <c>OnReachedPlaying</c> → hold +
        /// LoadComplete, host <c>Update()</c> aggregates → <c>AllDone</c> → <c>RevealAll</c> → simultaneous
        /// lift, with the existing three openers (roster shrink on peer-left, the host's 180 s forced reveal,
        /// each peer's own self-reveal) as the belts.
        /// </summary>
        public void ArmSelfLoadBarrier(string why)
        {
            if (_engine == null || !_engine.IsActive || !SessionStarted) return; // live co-op sessions only
            _revealed = false;
            _reachedPlaying = false;
            _loadCompleteSent = false;
            _lastReportedLoadPct = -1;
            _loadingLevel = null;
            _liveProgressBar = null;
            _slotProgress.Clear();
            _tracker.Reset();
            _lastSnapshotMs = -1;
            // Host aggregation + liveness-based release (same Update() path as phase-2; unused off-host).
            _loadPhaseActive = true;
            _lastBarrierWaitLogMs = 0;
            _revealAllSent = false;
            Debug.Log($"[Multiplayer] self-load barrier armed ({why}): host={_engine.IsHost} — " +
                      "holding reveal until every roster slot reports load-complete.");
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

            NoteProgress(); // every arriving chunk = live transfer (liveness-suspension deadline)

            // First chunk of a transfer (re)initialises the reassembly buffer.
            if (_rxBuffer == null || _rxTransferId != chunk.TransferId)
            {
                // Bound the network-supplied length BEFORE allocating: a large-positive TotalBytes
                // otherwise faults `new byte[TotalBytes]` AFTER _rxTotalBytes is set (buffer stays
                // null, throw swallowed) leaving _rxTotalBytes>0 → TransferActive pins heartbeat
                // suspension forever. Abort like the incomplete/CRC branches in OnSaveDone.
                if (chunk.TotalBytes <= 0 || chunk.TotalBytes > MaxTransferBytes)
                {
                    Debug.LogError($"[Multiplayer] OnSaveChunk: rejecting transfer {chunk.TransferId} — " +
                                   $"declared TotalBytes={chunk.TotalBytes} out of bounds (0, {MaxTransferBytes}].");
                    ResetRx();
                    AbortDownloadCurtain("invalid transfer size");
                    return;
                }
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
                _loadPhaseActive = true;   // this re-arms the reveal hold, so the hold is armed and unreleased
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
                _rxStarted = true;   // (b): bytes are moving — from here the wait is ours, then the players'
                Multiplayer.UI.MultiplayerUI.Instance?.EnterDownloadLoadingScreen();
                // PHASE EDGE — the mirrored host load hands over to OUR download, and the new phase starts
                // at 0%. The native controller only ever RAISES fillAmount (ProgressBarController.Update:71
                // `ProgressFill.fillAmount < num`), so without this the bar keeps the host's last fill and
                // sits there until the download passes it. BeginDownloadBar above only zeroes the SOURCE.
                // (The next edge, download→level-load, resets itself: native SetLoadingLevel:52 writes
                // fillAmount = LowestProgress.)
                Multiplayer.UI.NativeWidgetFactory.ResetBarFill();
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
            NoteProgress(); // transfer completing = progress
            var (transferId, totalBytes, ext, crc32, onDemandJoin) = MessageSerializer.DeserializeSaveDone(msg.Payload);

            Debug.Log($"[Multiplayer] OnSaveDone: transfer={transferId} total={totalBytes} remaining={_rxChunksRemaining}");

            if (_rxBuffer == null || transferId != _rxTransferId)
            {
                Debug.LogError("[Multiplayer] SaveDone for an unknown transfer; ignoring.");
                SendPrepared(transferId, false);
                // Match the incomplete/CRC branches below: a faulting-alloc transfer left _rxBuffer
                // null with _rxTotalBytes>0 — without ResetRx TransferActive/IsDownloading stay true
                // and pin the client's host-heartbeat/half-open detector off forever.
                ResetRx();
                return;
            }

            // Completion is decided by chunk coverage, NOT a running byte counter: every chunk index
            // must be present. A redelivered chunk does not inflate this (see OnSaveChunk).
            if (totalBytes != _rxBuffer.Length || _rxChunksRemaining != 0)
            {
                Debug.LogError($"[Multiplayer] Save transfer incomplete: got {_rxReceived}/{totalBytes} bytes, " +
                               $"{_rxChunksRemaining} chunk(s) still missing.");
                SendPrepared(transferId, false);
                ResetRx();
                AbortDownloadCurtain("download incomplete");
                return;
            }

            var actualCrc = Crc32(_rxBuffer);
            if (actualCrc != crc32)
            {
                Debug.LogError($"[Multiplayer] Save transfer crc mismatch: 0x{actualCrc:X8} != 0x{crc32:X8}.");
                SendPrepared(transferId, false);
                ResetRx();
                AbortDownloadCurtain("checksum mismatch");
                return;
            }

            // Verified blob — load it in memory, but DEFER entering the level until BEGIN.
            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) { SendPrepared(transferId, false); AbortDownloadCurtain("load init"); return; }

            var blob = _rxBuffer;
            var loadExt = string.IsNullOrEmpty(ext) ? SerializationComponent.DefaultExtension : ext;
            // Mark this transfer FINISHED so any late chunk of it (arriving after a subsequent F2
            // transfer starts) is rejected by the stale-chunk guard in OnSaveChunk.
            _completedTransferId = transferId;
            ResetRx();

            var timing = GetTiming();
            if (timing == null) { SendPrepared(transferId, false); return; }
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

            Debug.Log($"[Multiplayer] ClientLoadCrt: prepared ok={ok} → SendPrepared");
            // Ack the barrier AFTER the load is prepared but BEFORE FinishLevel.
            SendPrepared(transferId, ok);
            // Prepare failed: nothing will get our LOADED(true). Don't strand us on the curtain.
            if (!ok) { AbortDownloadCurtain("prepare"); yield break; }
            // The host no longer waits for us, so its BEGIN may already have come and gone (see
            // EnterLevel's latch). Enter now rather than waiting for a broadcast that will not repeat.
            if (_beginPending) { Debug.Log("[Multiplayer] ClientLoadCrt: latched BEGIN → EnterLevel"); EnterLevel(); }
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

            // 1c. Tactical transfer save (co-op mission entry): also read the embedded "Geoscape" section
            // into PhoenixSaveManager._currentGeoscapeSection — the native LoadGame path does this in
            // PrepareLoadGame (PhoenixSaveManager.cs:621-625) and the post-mission return REQUIRES it:
            // LoadCurrentGeoscape (cs:382) does ContentObjects.First(), and a null section kills the master
            // game coroutine (MenuCrt→GeoscapeGameCrt) — the client is then stranded in tactical forever
            // (RCA 2026-07-15 mission-end). The host's tactical transfer save always embeds the section
            // (SaveGameCrt:204-207 adds it to every tactical save).
            if (meta is PPSavegameMetaData ppMeta && ppMeta.IsTacticalSave)
            {
                var geoSection = AccessTools.Field(typeof(PhoenixSaveManager), "_currentGeoscapeSection")
                    ?.GetValue(game.SaveManager) as SavegameContentSection;
                if (geoSection == null)
                {
                    Debug.LogError("[Multiplayer] co-op load: _currentGeoscapeSection not reachable via " +
                                   "reflection (PP/TFTV version mismatch?) — post-mission geoscape return will fail.");
                }
                else
                {
                    var geoObjects = new ByRef<IEnumerable<object>>();
                    using (var ms = new System.IO.MemoryStream(blob))
                    {
                        yield return Timing.Current.Call(serializer.Serializer.Read(
                            geoObjects, slice, ext, ms, disposeStream: false, section: geoSection.SectionName));
                    }
                    geoSection.ContentObjects = geoObjects.Value;
                    Debug.Log("[Multiplayer] co-op load: geoscape return snapshot " +
                              (geoObjects.Value != null ? "captured from transfer blob"
                                                        : "MISSING in transfer blob — post-mission return will fail"));
                }
            }

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

        /// <summary>
        /// Ack the barrier. NAMED "prepared", not "loaded", because that is what it means: every caller
        /// fires at the END OF <c>PrepareEntryFromBlobCrt</c> — the save is deserialized and
        /// <c>_pendingResult</c> is built, but <c>FinishLevel</c> has NOT been called and no world has been
        /// loaded (ClientLoadCrt says so in its own comment: "AFTER the load is prepared but BEFORE
        /// FinishLevel"). The wire id stays <c>PacketType.ClientLoaded</c> and the host still calls its
        /// collection the LOADED barrier — renaming the protocol would be a compatibility break for a
        /// naming bug — but the barrier is a PREPARE barrier and the next reader should not have to
        /// re-derive that from the call sites, as the 2026-07-31 RCA did.
        /// </summary>
        private void SendPrepared(Guid transferId, bool ok)
        {
            Debug.Log($"[Multiplayer] SendPrepared (barrier ack — prepared, NOT yet loaded): " +
                      $"transfer={transferId} ok={ok} → host");
            var payload = MessageSerializer.SerializeClientLoaded(_engine.LocalSteamId, transferId, ok);
            _engine.SendToHost(new NetworkMessage(PacketType.ClientLoaded, payload));
        }

        public void OnClientLoaded(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            NoteProgress(); // a client acked LOADED = live transfer (host-side clock)
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

        // Host: release the barrier the moment the HOST has prepared its own entry. NO peer quorum
        // (N=50 mandate) — see SaveTransferMath.BarrierReleased. _loadedPeers is still tracked, but only
        // as progress telemetry for the roster overlay; nothing reads its COUNT to make a decision.
        private void TryReleaseBarrier()
        {
            if (!_engine.IsHost || !_barrierOpen) return;
            if (!BarrierReleased(_hostLoaded)) return;
            Debug.Log($"[Multiplayer] TryReleaseBarrier: hostLoaded={_hostLoaded} " +
                      $"(loadedClients={_loadedPeers.Count}, no quorum) → begin.");
            Begin();
        }

        /// <summary>Pure barrier-release predicate (unit-testable): the host's own readiness, nothing
        /// else. See <see cref="SaveTransferMath.BarrierReleased"/> for why no peer is counted.</summary>
        internal static bool BarrierReleased(bool hostLoaded) => SaveTransferMath.BarrierReleased(hostLoaded);

        // THE REPORT WAS A LIE ABOUT READINESS, AND THAT IS THE WHOLE 2026-08-05 REPORT (host out first,
        // clients trickling after). A peer reaches Playing in the MIDDLE of one enormous blocking
        // mission-start frame and reported done from inside it, with seconds of main-thread work still
        // queued behind the call. Live run, frame counters straight from the three logs:
        //   host      frame 1722 spans 21:42:01.314→03.4, SendLoadComplete at 02.168 — 0.8 s in;
        //   client-3  frame 1243 spans 21:42:02.502→04.76, SendLoadComplete at 03.321 — 0.8 s in.
        // Every slot was therefore "loaded" by 03.4, the host's AllDone fired on its very next frame
        // (1723, 03.541) and it lifted — CORRECTLY, by the barrier's own rule — while both clients were
        // still inside frame 1243/their own and could not so much as READ the RevealAll until 04.930 and
        // 05.081. The barrier never released early; its INPUT was wrong. Note what this is not: not the
        // latch, not a timeout, not the arm — those were all working in that run.
        //
        // The only honest proof that a peer can show the world is a COMPLETED FRAME past the point it
        // claims to be at, so the report is due on the next one. Every caller — host and client, tactical
        // and geoscape — arms through this one method, and the report leaves from FlushLoadComplete alone.
        //
        // A frame NUMBER, not a flag and not a clock (law L94 e2 forbids clocks, rightly): it fires on the
        // very next Update or not at all, so it cannot outlive its own load boundary and needs no clearing
        // at any of the six re-arm sites. `+ 1` is what makes it strictly a LATER frame, so a flush that
        // runs after this call within the same frame still waits.
        private int _loadCompleteDueFrame;

        /// <summary>This peer's load is truly finished (event-driven done) — tell the host, reliably,
        /// on the next frame boundary (see <see cref="_loadCompleteDueFrame"/>).</summary>
        public void SendLoadComplete()
        {
            if (_loadCompleteSent || _loadCompleteDueFrame != 0) return;
            _loadCompleteDueFrame = Time.frameCount + 1;
        }

        private void FlushLoadComplete()
        {
            if (_loadCompleteDueFrame == 0 || Time.frameCount < _loadCompleteDueFrame) return;
            _loadCompleteDueFrame = 0;
            if (_loadCompleteSent) return;
            NoteProgress(); // flag edge (this peer's phase-2 load finished)
            _loadCompleteSent = true;
            // Warm the walk-time ownership set HERE: every peer passes this exactly once per load
            // boundary (loading peers via OnReachedPlaying, the tac-entry host directly), world fully
            // loaded (runtime defs minted) and the curtain/overlay still up — so the ~0.3-1.5 s
            // full-def-graph walk never fires lazily inside a mid-play walk/apply slice.
            Sync.DefOwnership.Warm();
            Debug.Log("[Multiplayer] SendLoadComplete fired slot=" + _engine.Session.LocalSlotIndex +
                      " (frame boundary past Playing — this peer can actually render now)");
            var slot = _engine.Session.LocalSlotIndex;
            _tracker.MarkDone(slot); // local self-done
            if (_engine.IsHost) { TryReleaseBarrier(); return; }
            // _rxTransferId is usually Guid.Empty by now (ResetRx ran at load-prepare) — harmless:
            // OnLoadComplete discards the transferId and keys done-tracking on the slot alone.
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
            // Label the held native loading screen so the wait reads as intentional. Update() re-writes
            // this every frame from here on (see the held-wait bar block) — a one-shot label loses to any
            // native rewrite, which is why the host looked like it was showing nothing at all.
            Multiplayer.UI.NativeWidgetFactory.SetCurtainLabel(WaitingForPlayersLabel);
            // Done is ARMED here and only here — and it LEAVES one frame later, because Playing is reached
            // in the middle of a multi-second blocking frame and "loaded" said from inside that frame is
            // what let the host out first (see _loadCompleteDueFrame for the measured run).
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
            _revealedAtMs = NowMs();
            _loadPhaseActive = false; // THE one release of the hold this flag names (see its declaration)
            _hostEntryHold = false; // Batch 2: reveal done → drop the entry-hold flag (next Begin re-guards on _begun)
            _loadBoundaryAnnounced = false; // the announced boundary is over — close its publish window too
            // THE LATCH THAT SKIPPED THE NEXT BOUNDARY'S ARM. _barrierOpen used to be cleared ONLY by
            // Begin() and AbortTacticalEntryTransfer, so any reveal reached without Begin() having run left
            // it stuck true — and OpenReturnBarrier's own guard reads it, so EVERY later boundary silently
            // skipped its arm: _revealed stayed true, HoldCurtain never held, and each peer lifted the
            // instant its OWN load finished. The reveal ends the barrier; it must end all of the barrier's
            // flags, here, at the one release every path routes through.
            _barrierOpen = false;
            // …and the entry arm expires with the reveal that ended it (law L122). A launch that died before
            // tactical Playing never reaches TacDeployReadyCapture's consume, so without this the arm outlives
            // its own entry and the NEXT battle — loaded from a save every peer already holds — consumes it.
            Multiplayer.Tactical.TacLaunchGate.DisarmSessionEntry();
            // …and so does the on-demand JOIN arm, for exactly the same reason (law L122's pattern, c46d920).
            // _onDemandJoiner is cleared ONLY in OnSaveChunk's first-chunk branch, i.e. only by a transfer.
            // But the tac→geo RETURN and the native tactical entry (L103) arm through ArmSelfLoadBarrier and
            // ship NO save at all, so a peer that joined mid-session carried this flag into every later load
            // boundary — and OnReachedPlaying's join branch lifts ALONE, by design. One mid-session joiner is
            // therefore enough to reproduce the report verbatim at the end of the very next battle: its
            // screen comes down while the other two still load. The arm's lifetime is its OWN entry, which
            // this reveal is the end of.
            _onDemandJoiner = false;
            Debug.Log("[Multiplayer] PerformDeferredLift → reveal (native LiftCurtain + hide overlay)");
            // Restore the native loading label ("Waiting for players…" → original) before the lift runs.
            // Setting _revealed above already opened the curtain gate, so any PARKED lift resumes now.
            try { Multiplayer.UI.NativeWidgetFactory.RestoreCurtainLabel(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] RestoreCurtainLabel failed: " + e.Message); }
            // Lift the native curtain we suppressed (animated alpha→0, unpauses rendering, fires
            // OnCurtainLifted → GeoscapeView unlocks input + enables sound). Reflection: mod can't ref the type.
            // ROOT FIX (input-lock latch): when CurtainLiftGatePatch holds a PARKED native lift, opening
            // the gate above already resumes it with its FULL tail (OnCurtainLifted → input unlock) — a
            // second direct LiftCurtain here made the two compete, and LiftCurtain's own
            // _currentFadingRoutine.Stop() killed one tail, latching the loading-screen input override.
            // Single tail: skip the direct call and let the parked lift finish the reveal.
            try
            {
                if (Multiplayer.Harmony.CurtainLiftGatePatch.ParkedLiftLive)
                {
                    Debug.Log("[Multiplayer] PerformDeferredLift: parked native lift resuming — " +
                              "skipping direct LiftCurtain (single tail).");
                }
                else
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
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] native LiftCurtain failed: " + e.Message); }
            // Hide the mod overlay roster.
            try { MultiplayerUI.Instance?.HideLoadOverlay(); }
            catch (Exception e) { Debug.LogError("[Multiplayer] HideLoadOverlay failed: " + e.Message); }
        }

        // The reveal machinery above REMOVES the game's only input-unlock edge; RevealInputLock puts it back
        // and HOLDS it — see that file for the whole mechanism. This is only the reporting shell: the decision
        // and the clear are level-agnostic and live on the game-scoped InputController, so ONE convergence
        // loop covers the geoscape, tactical, the pre-view-state window and the geo→tac carry of a latch.
        // Engine-scoped by construction: keyed on _revealed, which EVERY peer sets in PerformDeferredLift —
        // no IsHost branch here or anywhere below it.

        // When the last reveal happened — lets the repair belt below distinguish the harmless
        // early-clear during the normal ~0.5 s lift fade from a genuinely LOST lift tail (metric).
        private long _revealedAtMs;

        private void RepairRevealInputLock()
        {
            try
            {
                if (!RevealInputLock.Converge(_revealed)) return;
                // METRIC: firing LATE (past any plausible lift fade) means a lift tail was lost DESPITE
                // the parked-lift root fix in PerformDeferredLift — loud, so a regression is unmissable.
                // An early clear within the fade window stays quiet (it is exactly what the fade's own
                // tail is about to do a frame later).
                var sinceRevealMs = NowMs() - _revealedAtMs;
                if (sinceRevealMs > 2000)
                    Debug.LogWarning($"[Multiplayer] reveal input-lock repair fired {sinceRevealMs}ms after " +
                                     "reveal — curtain lift tail LOST (should be rare→never after the " +
                                     "parked-lift root fix); investigate.");
                else
                    Debug.Log("[Multiplayer] reveal input-lock repair: cleared LoadingScreenInputSet " +
                              "(early clear during lift fade — benign)");
            }
            catch (Exception e) { Debug.LogError("[Multiplayer] RepairRevealInputLock failed: " + e.Message); }
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
            // A roster snapshot proves the HOST is alive through the whole barrier + phase-2 window
            // (it broadcasts ≤20 Hz while _barrierOpen/_loadPhaseActive) — the key progress signal
            // for a client parked at IsBarrierPending waiting for BEGIN/RevealAll.
            NoteProgress();
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
            // LIVE RCA 2026-08-05 — the _hostEntryHold relaxation alone is NOT enough, because the flag is
            // not ours alone to hold: PerformDeferredLift clears it (:1735), and the PREVIOUS load's AllDone
            // can fire during the 1151 ms mid-tactical save write (host log frames 2705 vs 2707). Begin()
            // then early-returned, SessionBegin was never broadcast, and both clients sat at _begun=false
            // for the whole battle — every client tactical command dropped silently in
            // TacticalCommandSync.LiveEngine(). So guard on the flag that is actually OURS: _barrierOpen,
            // which the comment above already names the true single-fire guard, is set by the OpenBarrier of
            // THIS transfer and cleared on the next line. TryReleaseBarrier (the only caller) bails on
            // !_barrierOpen, so today this makes the early return unreachable — deliberately: a barrier that
            // was opened must always produce its BEGIN.
            if (SaveTransferMath.BeginSuppressed(_begun, _hostEntryHold, _barrierOpen)) return;
            _barrierOpen = false;
            // Phase-2 (world load) starts now; keep snapshots flowing until the roster is all-done.
            _loadPhaseActive = true;
            // Phase-2 release runs on LIVENESS (NoLiveLoaderLeft), so a long native world-load is never
            // force-revealed mid-load — it is waited out. Restart every slot's grace window here.
            _lastBarrierWaitLogMs = 0;

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
                // LATCH, do not drop. With the LOADED quorum gone (N=50 mandate) the host begins on its
                // OWN readiness, so SessionBegin routinely overtakes a slower peer's download+prepare —
                // and this branch used to just log and return, stranding that peer behind its curtain
                // forever with no retry anywhere. Remember the BEGIN instead; ClientLoadCrt re-enters the
                // moment its prepare lands. This is what "nobody is kicked, a slow peer catches up"
                // actually costs: one bool.
                _beginPending = true;
                Debug.Log("[Multiplayer] BEGIN received before this peer's save was prepared — latched; " +
                          "entering as soon as the prepare finishes.");
                return;
            }
            _beginPending = false;

            NoteProgress(); // flag edge (BEGIN released this peer into the level)
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
        //  Progress reporting — download AND load are both exact 0..1.
        //  (The "no 0..1 float is exposed" note that used to sit here was wrong:
        //   Base.Levels.Level.LoadingProgress is a LoadingProgressWithSteps with a
        //   real float Progress, live from Level.cs:134 until it is nulled at :146.)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// HOST, geo→tactical entry: publish its OWN native level-load percent into the roster snapshot
        /// so clients can mirror it. Slot 0 (the host's) at <see cref="HostEntryPhase"/>, which the
        /// terminal <c>(1, 100)</c> written at deploy-ready can still overtake. Writing into
        /// <c>_slotProgress</c> is also what keeps the host's own slot visibly
        /// ALIVE to the reveal barrier's liveness clock instead of merely silent.
        /// Not <see cref="ReportLoadProgress"/>: that one is hard-wired to phase 1.
        /// </summary>
        private void PublishHostEntryLoad(byte percent)
        {
            var slot = _engine.Session.LocalSlotIndex;
            _slotProgress[slot] = (HostEntryPhase, percent);
            _tracker.Merge(slot, HostEntryPhase, percent);
        }

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
            NoteProgress(); // a client reported progress = live transfer (host-side clock)

            var (_, phase, percent) = MessageSerializer.DeserializeLoadProgress(msg.Payload);

            // Phase 0 = download (exact) — keep the existing per-peer download view for the lobby.
            if (phase == 0)
                _peerDownloadPct[msg.SenderSteamId] = percent;

            // Map the sender to its slot via the roster, then aggregate per-slot for the snapshot.
            if (_engine.Session.TryGetSlotForPeer(msg.SenderSteamId, out var slot))
            {
                _slotProgress[slot] = (phase, percent);
                // A forward MOVE (not just a packet) is the barrier's progress signal for this peer.
                _tracker.Merge(slot, phase, percent);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Per-frame: reveal barrier (no clock at all)
        // ══════════════════════════════════════════════════════════════════

        // How often the host restates WHO the barrier is waiting for. The wait is unbounded, so without this
        // line an operator reading the log cannot tell "waiting for a slow peer" from "hung" — and a silent
        // unbounded wait is this codebase's dominant bug shape.
        private const long BarrierWaitLogIntervalMs = 5_000;
        private long _lastBarrierWaitLogMs;

        /// <summary>The one held-at-the-barrier caption. Plain English constant like every other curtain
        /// label in this file — the mod has no loc table for runtime strings.</summary>
        internal const string WaitingForPlayersLabel = "Waiting for players…";

        /// <summary>
        /// HOST, diagnostics only: name the slots the reveal is still waiting on, throttled. Deliberately has
        /// NO return value and NO release power. THE WAIT IS UNBOUNDED (user ruling 2026-08-05, superseding
        /// the earlier liveness give-up): if a peer never reports, everyone keeps waiting. A peer that is
        /// genuinely gone leaves the ROSTER, and <c>AllDone(GetRosterSlots())</c> — re-evaluated every frame —
        /// then holds on its own: that is the release, and it is an event, not a deadline.
        /// </summary>
        private void LogBarrierWait()
        {
            var now = NowMs();
            if (now - _lastBarrierWaitLogMs <= BarrierWaitLogIntervalMs) return;
            List<string> waiting = null;
            foreach (var slot in _engine.Session.GetRosterSlots())
            {
                if (_tracker.IsDone(slot)) continue;
                var st = _tracker.Get(slot);
                if (waiting == null) waiting = new List<string>();
                waiting.Add($"s{slot} {(st.phase == 0 ? "downloading" : "loading")} {st.percent}%");
            }
            if (waiting == null) return;   // everyone loaded → the AllDone release owns it
            _lastBarrierWaitLogMs = now;
            Debug.Log("[Multiplayer] reveal barrier holding for " + string.Join(", ", waiting) +
                      " — everyone waits until the last peer is in; there is no deadline on this wait.");
        }

        public void Update()
        {
            // Converge the reveal input-lock invariant before anything else: a peer that has revealed must
            // never be left holding the geoscape loading-screen input override (see RepairRevealInputLock).
            RepairRevealInputLock();

            // The one place "this peer is loaded" leaves this peer — runs on EVERY peer, above every
            // host-only return below, and one frame later than the SendLoadComplete that armed it.
            FlushLoadComplete();

            // Host: keep handing the start blob out, a few peers at a time (see PumpBlobQueue), and
            // serve anyone who joined at a moment we could not onboard them. Both are cheap no-ops once
            // there is nothing left to hand out.
            if (_engine.IsHost) { PumpBlobQueue(); PumpDeferredJoins(); PumpNewCampaignWatchdog(); }

            // Phase-1 (download) native bottom-bar driver — client only. While the save blob is arriving,
            // feed the game's own loading-screen bar the exact download fraction so it fills 0..100% under
            // the dropped curtain. When the download finishes we hold the bar full + relabel "Waiting for
            // players…" through the prepare + LOADED-barrier gap; phase-2 (SetLoadingLevel) then hands the
            // bar to the real level-load progress and clears this driver.
            //
            // THE ELSE BRANCH SERVES TWO OPPOSITE WAITS and must not confuse them (live bug, fixed here):
            // OnEntryTransferBegin raises the curtain BEFORE any bytes exist, and IsDownloading (= client &&
            // _rxTotalBytes > 0) is false then — so this branch used to overwrite that curtain's label with
            // "Waiting for players…" and a FULL bar within one frame, telling every client it was waiting on
            // its peers for the whole ~13 s it was actually waiting on the host. _rxStarted is which side of
            // the transfer we are on; the bar only claims completion once something completed.
            if (_downloadCurtain)
            {
                if (IsDownloading)
                {
                    Multiplayer.UI.NativeWidgetFactory.SetDownloadBar(
                        _rxTotalBytes > 0 ? (float)_rxReceived / _rxTotalBytes : 0f);
                }
                else if (_rxStarted)
                {
                    Multiplayer.UI.NativeWidgetFactory.SetCurtainLabel(WaitingForPlayersLabel);
                    Multiplayer.UI.NativeWidgetFactory.SetDownloadBar(1f);
                }
                else
                {
                    // (a) NOT ONE BYTE EXISTS YET — the host is still building the mission. This branch used
                    // to write a label and nothing else, so the bar stayed on BeginDownloadBar's
                    // UpdateProgress(0f) for the whole wait: an EMPTY bar, the reported bug. MIRROR THE
                    // HOST'S OWN BAR instead — it publishes its real eased fillAmount into slot 0 / phase 0
                    // at ≤20 Hz (HostEntryLoad pump below), so this is the host's actual load and not an
                    // animation. A stale host simply parks the bar at the last mirrored value; it can never
                    // claim 100% (only the host's terminal (1,100) does that), and EntryTransferAbort is the
                    // belt.
                    Multiplayer.UI.NativeWidgetFactory.SetCurtainLabel("Host is loading…");
                    Multiplayer.UI.NativeWidgetFactory.SetDownloadBar(_tracker.Get(0).percent / 100f);
                }
            }

            // HELD AT THE REVEAL BARRIER, OWN LOAD FINISHED — the window the host spends waiting for its
            // clients. It used to be driven from here too (an aggregate AVERAGE written into the native
            // bottom bar), which was a THIRD writer of the same _tracker and never worked: the block was
            // gated on _reachedPlaying, which a new-campaign host never sets (no Loaded→Playing edge — see
            // the SendLoadComplete branch above) and which OpenBarrier clears two seconds into the mission
            // path. Deleted. The per-peer LoadOverlayController — named bar + percent for every OTHER peer,
            // repainted from this same tracker every frame — is the one display of this wait, and it keys
            // on HostWaitingOnPeers, which is live-true across BOTH windows. The held-wait CAPTION is still
            // written once at OnReachedPlaying; the overlay carries everything that moves.

            // Phase-2 progress pump — runs on EVERY peer (host + clients) regardless of overlay visibility.
            // Decoupled from the UI: the overlay being hidden must NOT stall progress/done reporting.
            // …AND on the host through its OWN geo→tactical entry load, which InPhase2 cannot see
            // (see HostEntryLoad). Same sampling, same widget, different phase number.
            if (InPhase2 || HostEntryLoad)
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
                        NoteProgress(); // own phase-2 load advancing = progress
                        Debug.Log($"[Multiplayer] phase-2 pump: slot={_engine.Session.LocalSlotIndex} " +
                                  $"pct={pct} (src={(_liveProgressBar != null ? "nativeBar" : "levelProgress")})");
                        if (InPhase2) ReportLoadProgress(pct);
                        else PublishHostEntryLoad(pct);
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

            // THERE IS NO TIMED RELEASE, AND THAT IS THE RULE, NOT AN OMISSION (user ruling 2026-08-05).
            // Two clocks used to live here — the host's 60 s liveness give-up and each peer's own 60 s
            // self-reveal — and each of them was a way for one player's screen to come down while the others
            // were still loading. Nobody leaves the loading screen until EVERY roster slot has reported
            // load-complete. The only openers are events, never deadlines: the AllDone reveal below, a peer
            // LEAVING the roster (GetRosterSlots shrinks, AllDone then holds on the very next frame), and
            // session teardown (HoldCurtain goes false with the engine inactive). Say who we are waiting on.
            if (_engine.IsHost && _loadPhaseActive && !_revealAllSent) LogBarrierWait();

            // Snapshots must flow through BOTH phases: the LOADED barrier window (_barrierOpen) AND
            // the phase-2 world-load (_loadPhaseActive — an armed, unreleased reveal hold, which spans it).
            // Without _loadPhaseActive every peer's tracker would freeze the instant phase-2 begins.
            // HostEntryLoad stays in the disjunction on its own merits: it is the one window the host's
            // native level-load owns, and sampling without broadcasting is still silence.
            if (!_engine.IsHost || (!_barrierOpen && !_loadPhaseActive && !HostEntryLoad)) return;

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
            //
            // GATED ON THE HOST'S OWN DONE-MARK, NOT ON _loadPhaseActive — and it already is, without a
            // second term: GetRosterSlots() yields slot 0 first (the host), so AllDone cannot hold until the
            // host has marked ITSELF done via SendLoadComplete — which, on the tac-entry path, cannot happen
            // before deploy-ready: OpenTacticalEntryBarrier clears the tracker but deliberately leaves
            // _loadCompleteSent SET (see InPhase2/HostEntryLoad), so the host's OnReachedPlaying at tactical
            // Playing early-returns and only HostBeginTacticalEntryTransfer's `_loadCompleteSent = false` +
            // SendLoadComplete pair puts slot 0 back in the set. That is the property the old
            // `_loadPhaseActive &&` was standing in for after the 2026-08-05 mid-entry reveal, and reading it
            // off the roster instead frees the flag to mean one thing (see its declaration) so the liveness
            // give-up above can cover every barrier window instead of none of the entry's.
            if (_tracker.AllDone(_engine.Session.GetRosterSlots()))
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
                    HostReplayIntroCinematic(); // co-op campaign intro: AFTER the reveal, or the mirror drops it
                }
            }

            // NO STRAGGLER KICK (N=50 mandate). This used to wait three minutes for every peer to
            // report LOADED and then ConnectionRejected + DisconnectPeer everyone who had not — the single
            // most user-hostile line in the mod, and pure collateral: the barrier it protected is gone
            // (TryReleaseBarrier releases on the host alone), so there is nothing left to be late FOR. A
            // peer whose download is slow keeps downloading and enters when it lands.
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
        }

        // ── The co-op campaign INTRO, owed from creation and paid at the reveal ───────────────
        // Armed in NewCampaignInterceptPatch.CreateSceneBinding_Prefix (the only place that still knows
        // whether the game mode wanted an intro at all — the flag is def data and the prefix clears it so
        // the host does not play it alone into a lobby). ONE-SHOT: consumed here whatever happens next, so
        // a failed or impossible replay can never re-fire on a later, unrelated reveal.
        private bool _introCinematicOwed;

        /// <summary>Record whether the suppressed campaign creation owed an intro cinematic. False is a
        /// legitimate answer (the game mode does not carry one) and must clear any stale arm.</summary>
        public void NoteIntroCinematicOwed(bool owed) => _introCinematicOwed = owed;

        /// <summary>True while an intro replay is outstanding — read by RailCheck's one-shot arm.</summary>
        public bool IntroCinematicOwed => _introCinematicOwed;

        /// <summary>Consume the arm and return whether it was set (one-shot, same shape as
        /// <see cref="ReseedOnceGate"/>).</summary>
        public bool ConsumeIntroCinematicOwed()
        {
            bool owed = _introCinematicOwed;
            _introCinematicOwed = false;
            return owed;
        }

        // AFTER the reveal, and that ordering is the whole risk: CutsceneMirror drops a raise with "no live
        // GeoscapeView", and before the reveal the peers are not in the level yet. Called from the same two
        // host reveal branches as HostReseedAfterReveal, right behind PerformDeferredLift.
        private void HostReplayIntroCinematic()
        {
            if (!_engine.IsHost || !ConsumeIntroCinematicOwed()) return;
            Sync.CutsceneMirror.ReplayCampaignIntro();
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
