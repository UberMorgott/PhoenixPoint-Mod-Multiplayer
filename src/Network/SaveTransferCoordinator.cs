using System;
using System.Collections.Generic;
using System.Linq;
using Base.Core;
using Base.Platforms;
using Base.Serialization;
using Base.Utils;
using HarmonyLib;
using Multipleer.Network.MessageLayer;
using Multipleer.Transport;
using Multipleer.UI;
using PhoenixPoint.Common.Game;
using PhoenixPoint.Common.Levels.Params;
using PhoenixPoint.Common.Saves;
using UnityEngine;

namespace Multipleer.Network
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

        // Barrier timeout after SaveDone is sent / the barrier opens. On expiry the host kicks peers
        // that have not reported LOADED and begins with whoever is ready (required, not optional).
        private const long BarrierTimeoutMs = 60_000;

        private readonly NetworkEngine _engine;

        // ─── Host transfer/barrier state ──────────────────────────────────
        private Guid _transferId;
        private bool _barrierOpen;
        private long _barrierOpenedAtMs;
        private readonly HashSet<ulong> _loadedPeers = new HashSet<ulong>();

        // ─── Client reassembly state ──────────────────────────────────────
        private Guid _rxTransferId;
        private long _rxTotalBytes;
        private byte[] _rxBuffer;
        private long _rxReceived;
        private int _lastReportedDownloadPct = -1;
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

        /// <summary>Shared receiver-side roster progress for the overlay UI.</summary>
        public RosterProgressTracker Tracker => _tracker;

        public SaveTransferCoordinator(NetworkEngine engine)
        {
            _engine = engine;
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
                ? Multipleer.UI.NativeWidgetFactory.CaptureLiveProgressBar()
                : null;
        }

        /// <summary>
        /// True while a save transfer/load is in flight: the host has opened the barrier, or this
        /// client is mid-download / has a prepared save awaiting BEGIN. Used to gate progress display.
        /// </summary>
        public bool TransferActive =>
            (_engine.IsHost && _barrierOpen) || _rxTotalBytes > 0 || IsBarrierPending;

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

        public void HostStartSession(SavegameMetaData chosen)
        {
            if (!_engine.IsHost)
            {
                Debug.LogWarning("[Multipleer] HostStartSession called on a non-host peer; ignored.");
                return;
            }

            if (chosen == null)
            {
                Debug.LogError("[Multipleer] HostStartSession called with no chosen save; aborting.");
                return;
            }

            Debug.Log($"[Multipleer] HostStartSession: transport={_engine.Transport?.TransportType} save={chosen?.Name}");

            // Honest-scope limitation: reliable save-transfer is supported on Steam (reliable P2P) and
            // DirectIP (length-prefixed TCP). The Stun/WAN path sends raw UDP with no sequencing/ACK/
            // retransmit, so 32 KB chunks fragment at the IP layer and any lost fragment fails the
            // transfer. Warn once at start; do not change Steam/Direct behaviour.
            if (_engine.Transport != null && _engine.Transport.TransportType == TransportType.StunUDP)
            {
                Debug.LogWarning("[Multipleer] Save transfer over the Stun/WAN (UDP) transport is " +
                                 "BEST-EFFORT only: chunks fragment over UDP with no retransmit, so the " +
                                 "transfer may fail on packet loss. Reliable transfer is supported on " +
                                 "Steam and DirectIP.");
            }

            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) return;

            // The chosen save is supplied by the lobby save-picker (PhoenixSaveManager.GetSaves()),
            // selected AFTER the lobby Play press — never auto-selected here (lobby-first invariant).
            var timing = GetTiming();
            if (timing == null) return;

            _transferId = Guid.NewGuid();
            timing.Start(HostSerializeAndSendCrt(game, chosen));
        }

        // Coroutine: read the save to bytes, then chunk+send, then prepare host entry + open barrier.
        private IEnumerator<NextUpdate> HostSerializeAndSendCrt(PhoenixGame game, SavegameMetaData metaData)
        {
            var result = new ByRef<byte[]>();
            yield return Timing.Current.Call(game.SaveManager.Serializer.ReadSavegameBinary(metaData, result));

            var blob = result.Value;
            if (blob == null || blob.Length == 0)
            {
                Debug.LogError("[Multipleer] Save serialization produced no bytes; aborting transfer.");
                yield break;
            }

            var ext = System.IO.Path.GetExtension(metaData.Path);
            if (string.IsNullOrEmpty(ext)) ext = SerializationComponent.DefaultExtension;

            SendBlob(blob, ext);

            // Host prepares its own entry from the SAME bytes (in memory), then waits at the barrier.
            yield return Timing.Current.Call(PrepareEntryFromBlobCrt(game, blob, ext));

            OpenBarrier();
            // Host counts as loaded immediately.
            _loadedPeers.Add(_engine.LocalSteamId);
            TryReleaseBarrier();
        }

        // Split the blob into SaveChunk messages (sequence by offset), then a SaveDone with crc32.
        private void SendBlob(byte[] blob, string ext)
        {
            var crc = Crc32(blob);

            var chunkCount = (int)((blob.Length + ChunkSize - 1) / ChunkSize);
            Debug.Log($"[Multipleer] SendBlob: bytes={blob.Length} chunks={chunkCount} crc=0x{crc:X8}");

            long offset = 0;
            while (offset < blob.Length)
            {
                var len = (int)Math.Min(ChunkSize, blob.Length - offset);
                var chunk = new byte[len];
                Array.Copy(blob, offset, chunk, 0, len);

                var msg = new SaveChunkMessage
                {
                    TransferId = _transferId,
                    TotalBytes = blob.Length,
                    Offset = offset,
                    Chunk = chunk
                };
                var payload = MessageSerializer.SerializeSaveChunk(msg);
                _engine.BroadcastToAll(new NetworkMessage(PacketType.SaveChunk, payload));
                offset += len;
            }

            var donePayload = MessageSerializer.SerializeSaveDone(_transferId, blob.Length, ext, crc);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.SaveDone, donePayload));
            Debug.Log("[Multipleer] SendBlob: all chunks + SaveDone broadcast sent");
        }

        private void OpenBarrier()
        {
            _barrierOpen = true;
            _barrierOpenedAtMs = NowMs();
            _loadedPeers.Clear();
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
            Debug.Log($"[Multipleer] LOADED barrier open, host self-added id={_engine.LocalSteamId}.");
        }

        // ══════════════════════════════════════════════════════════════════
        //  CLIENT: receive chunks, reassemble, verify, load in-memory
        // ══════════════════════════════════════════════════════════════════

        public void OnSaveChunk(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var chunk = MessageSerializer.DeserializeSaveChunk(msg.Payload);

            // First chunk of a transfer (re)initialises the reassembly buffer.
            if (_rxBuffer == null || _rxTransferId != chunk.TransferId)
            {
                _rxTransferId = chunk.TransferId;
                _rxTotalBytes = chunk.TotalBytes;
                _rxBuffer = new byte[chunk.TotalBytes];
                _rxReceived = 0;
                _lastReportedDownloadPct = -1;
                // Chunks are emitted at fixed ChunkSize offsets (SendBlob), so the index is exact.
                var chunkCount = (int)((chunk.TotalBytes + ChunkSize - 1) / ChunkSize);
                _rxChunkSeen = new bool[chunkCount];
                _rxChunksRemaining = chunkCount;
                Debug.Log($"[Multipleer] OnSaveChunk FIRST: transfer={chunk.TransferId} total={chunk.TotalBytes} chunks={chunkCount}");
            }

            if (chunk.Chunk != null && chunk.Offset >= 0 &&
                chunk.Offset + chunk.Chunk.Length <= _rxBuffer.Length)
            {
                // Copy is idempotent; only count a chunk once toward coverage/progress even if the
                // transport redelivers it (Stun duplicates reliable packets).
                Array.Copy(chunk.Chunk, 0, _rxBuffer, chunk.Offset, chunk.Chunk.Length);

                var index = (int)(chunk.Offset / ChunkSize);
                if (_rxChunkSeen != null && index >= 0 && index < _rxChunkSeen.Length && !_rxChunkSeen[index])
                {
                    _rxChunkSeen[index] = true;
                    _rxChunksRemaining--;
                    _rxReceived += chunk.Chunk.Length;
                    ReportDownloadProgress();
                    // Throttled progress trace: every 64 chunks (and at completion). Not per-chunk.
                    if (_rxChunksRemaining == 0 || (_rxChunksRemaining % 64) == 0)
                        Debug.Log($"[Multipleer] OnSaveChunk: received={_rxReceived}/{_rxTotalBytes} remaining={_rxChunksRemaining}");
                }
            }
        }

        public void OnSaveDone(NetworkMessage msg)
        {
            if (_engine.IsHost) return;
            var (transferId, totalBytes, ext, crc32) = MessageSerializer.DeserializeSaveDone(msg.Payload);

            Debug.Log($"[Multipleer] OnSaveDone: transfer={transferId} total={totalBytes} remaining={_rxChunksRemaining}");

            if (_rxBuffer == null || transferId != _rxTransferId)
            {
                Debug.LogError("[Multipleer] SaveDone for an unknown transfer; ignoring.");
                SendLoaded(transferId, false);
                return;
            }

            // Completion is decided by chunk coverage, NOT a running byte counter: every chunk index
            // must be present. A redelivered chunk does not inflate this (see OnSaveChunk).
            if (totalBytes != _rxBuffer.Length || _rxChunksRemaining != 0)
            {
                Debug.LogError($"[Multipleer] Save transfer incomplete: got {_rxReceived}/{totalBytes} bytes, " +
                               $"{_rxChunksRemaining} chunk(s) still missing.");
                SendLoaded(transferId, false);
                ResetRx();
                return;
            }

            var actualCrc = Crc32(_rxBuffer);
            if (actualCrc != crc32)
            {
                Debug.LogError($"[Multipleer] Save transfer crc mismatch: 0x{actualCrc:X8} != 0x{crc32:X8}.");
                SendLoaded(transferId, false);
                ResetRx();
                return;
            }

            // Verified blob — load it in memory, but DEFER entering the level until BEGIN.
            PhoenixGame game;
            PhoenixSaveManager saveManager;
            if (!TryGetGame(out game, out saveManager)) { SendLoaded(transferId, false); return; }

            var blob = _rxBuffer;
            var loadExt = string.IsNullOrEmpty(ext) ? SerializationComponent.DefaultExtension : ext;
            ResetRx();

            var timing = GetTiming();
            if (timing == null) { SendLoaded(transferId, false); return; }
            Debug.Log("[Multipleer] OnSaveDone: verified OK → ClientLoadCrt");
            timing.Start(ClientLoadCrt(game, blob, loadExt, transferId));
        }

        private IEnumerator<NextUpdate> ClientLoadCrt(PhoenixGame game, byte[] blob, string ext, Guid transferId)
        {
            Debug.Log("[Multipleer] ClientLoadCrt: preparing entry");
            yield return Timing.Current.Call(PrepareEntryFromBlobCrt(game, blob, ext));

            var ok = _pendingResult != null;
            Debug.Log($"[Multipleer] ClientLoadCrt: prepared ok={ok} → SendLoaded");
            // Ack the barrier AFTER the load is prepared but BEFORE FinishLevel.
            SendLoaded(transferId, ok);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Shared: build the loaded scene binding in memory (no temp file)
        //  Mirrors PhoenixSaveManager.LoadCurrentGeoscape (PhoenixSaveManager.cs:380-398).
        // ══════════════════════════════════════════════════════════════════

        private IEnumerator<NextUpdate> PrepareEntryFromBlobCrt(PhoenixGame game, byte[] blob, string ext)
        {
            Debug.Log("[Multipleer] PrepareEntryFromBlobCrt: start");
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
                Debug.LogError("[Multipleer] Transferred save metadata could not be read.");
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
            Debug.Log("[Multipleer] PrepareEntryFromBlobCrt: _pendingResult ready");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Barrier: LOADED collection (host) + BEGIN
        // ══════════════════════════════════════════════════════════════════

        private void SendLoaded(Guid transferId, bool ok)
        {
            Debug.Log($"[Multipleer] SendLoaded: transfer={transferId} ok={ok} → host");
            var payload = MessageSerializer.SerializeClientLoaded(_engine.LocalSteamId, transferId, ok);
            _engine.SendToHost(new NetworkMessage(PacketType.ClientLoaded, payload));
        }

        public void OnClientLoaded(NetworkMessage msg)
        {
            if (!_engine.IsHost) return;
            var (steamId, transferId, ok) = MessageSerializer.DeserializeClientLoaded(msg.Payload);

            Debug.Log($"[Multipleer] LOADED ack rx: sender={msg.SenderSteamId} payloadId={steamId} " +
                      $"transferId={transferId} (current {_transferId}) ok={ok}.");

            // Ignore a stale ack from a prior transfer: it must match the current transfer id.
            if (transferId != _transferId)
            {
                Debug.LogWarning($"[Multipleer] LOADED REJECTED (stale transfer): sender={msg.SenderSteamId} " +
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
                Debug.Log($"[Multipleer] LOADED ACCEPTED: added sender={msg.SenderSteamId} to barrier set.");
                TryReleaseBarrier();
            }
            else
            {
                Debug.LogWarning($"[Multipleer] LOADED REJECTED (ok=false): sender={msg.SenderSteamId} " +
                                 $"failed to load the transferred save.");
            }
        }

        // Host: release the barrier once every connected peer (+ host) has reported LOADED.
        private void TryReleaseBarrier()
        {
            if (!_engine.IsHost || !_barrierOpen) return;

            // Expected = host + all currently connected clients.
            var expected = 1; // host
            foreach (var _ in _engine.Session.GetConnectedClients()) expected++;

            var release = _loadedPeers.Count >= expected;
            Debug.Log($"[Multipleer] TryReleaseBarrier: loadedPeers={_loadedPeers.Count} " +
                      $"expected={expected} release={release}.");

            if (release)
                Begin();
        }

        /// <summary>This peer's load is truly finished (event-driven done) — tell the host, reliably.</summary>
        public void SendLoadComplete()
        {
            if (_loadCompleteSent) return;
            _loadCompleteSent = true;
            Debug.Log("[Multipleer] SendLoadComplete fired slot=" + _engine.Session.LocalSlotIndex);
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
            Debug.Log($"[Multipleer] OnReachedPlaying slot={_engine.Session.LocalSlotIndex} " +
                      $"→ hold + SendLoadComplete");
            // Idempotent: guarantees done is reported even if LoadingProgress never went null.
            SendLoadComplete();
        }

        /// <summary>All peers: host says everyone is loaded → lift the held overlay now.</summary>
        public void OnRevealAll(NetworkMessage msg)
        {
            Debug.Log("[Multipleer] OnRevealAll received → PerformDeferredLift");
            PerformDeferredLift();
        }

        // Lift the held synced reveal (native curtain we suppressed + the mod overlay roster) so the
        // already-loaded world appears on every peer at once. Once-guarded FIRST; never throws.
        private void PerformDeferredLift()
        {
            if (_revealed) return;
            _revealed = true;
            Debug.Log("[Multipleer] PerformDeferredLift → reveal (native LiftCurtain + hide overlay)");
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
            catch (Exception e) { Debug.LogError("[Multipleer] native LiftCurtain failed: " + e.Message); }
            // Hide the mod overlay roster.
            try { MultiplayerUI.Instance?.HideLoadOverlay(); }
            catch (Exception e) { Debug.LogError("[Multipleer] HideLoadOverlay failed: " + e.Message); }
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
            Debug.Log($"[Multipleer] RosterProgress RECV [{recvDetail}]");
            foreach (var r in rows) _tracker.Merge(r.SlotIndex, r.Phase, r.Percent);
        }

        // Host broadcasts BEGIN; every peer (incl. host) then enters its prepared level.
        private void Begin()
        {
            if (!_engine.IsHost || _begun) return;
            _barrierOpen = false;
            // Phase-2 (world load) starts now; keep snapshots flowing until the roster is all-done.
            _loadPhaseActive = true;
            // Host forced-reveal deadline: if a peer errors / never reports done, reveal anyway.
            _phase2DeadlineMs = NowMs() + BarrierTimeoutMs;

            Debug.Log("[Multipleer] BEGIN broadcast.");
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
                Debug.LogWarning("[Multipleer] BEGIN received but no save was prepared; cannot enter level.");
                return;
            }

            _begun = true;
            PhoenixGame game;
            PhoenixSaveManager sm;
            if (!TryGetGame(out game, out sm)) return;

            // Single convergence point for both load paths (PhoenixGame.cs:263). The FinishLevel
            // Harmony gate (SaveLoadPatches) holds any vanilla-initiated call until this fires.
            Debug.Log("[Multipleer] EnterLevel → FinishLevel.");
            game.FinishLevel(_pendingResult);
            // Confirm PrepareLoadGame state was applied (was 0 → empty geoscape; expect >0 now).
            var dlcLen = sm.EnabledDlc != null ? sm.EnabledDlc.Length : 0;
            Debug.Log($"[Multipleer] co-op load: SaveManager.EnabledDlc.Length={dlcLen}");
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
                        var fill = Multipleer.UI.NativeWidgetFactory.GetProgressFill(_liveProgressBar);
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
                        Debug.Log($"[Multipleer] phase-2 pump: slot={_engine.Session.LocalSlotIndex} " +
                                  $"pct={pct} (src={(_liveProgressBar != null ? "nativeBar" : "levelProgress")})");
                        ReportLoadProgress(pct);
                    }
                }
                else if (_lastReportedLoadPct >= 0)
                {
                    // Native load finished (LoadingProgress went null) → event-driven done.
                    _lastReportedLoadPct = -1;
                    Debug.Log("[Multipleer] phase-2 pump: LoadingProgress null → SendLoadComplete");
                    SendLoadComplete();
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
                PerformDeferredLift();
            }
            // Per-peer self-reveal: this peer is holding (reached Playing) but the RevealAll never
            // arrived (dead host). After the hold timeout, reveal locally so it isn't stuck forever.
            if (_reachedPlaying && !_revealed && NowMs() - _revealHoldStartedMs > BarrierTimeoutMs)
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
                Debug.Log("[Multipleer] co-op load: roster all-done — stopping phase-2 snapshots.");

                // Second barrier satisfied: every peer is loaded → reveal the world simultaneously.
                if (_engine.IsHost && !_revealAllSent)
                {
                    _revealAllSent = true;
                    Debug.Log("[Multipleer] AllDone → broadcast RevealAll");
                    _engine.BroadcastToAll(new NetworkMessage( // reliable
                        PacketType.RevealAll, MessageSerializer.SerializeRevealAll(DateTime.UtcNow.Ticks)));
                    PerformDeferredLift(); // host reveals at the same instant
                }
            }

            // Timeout/kick + Begin only apply while the LOADED barrier is still open (phase-1).
            if (!_barrierOpen) return;
            if (NowMs() - _barrierOpenedAtMs <= BarrierTimeoutMs) return;

            // Timeout: kick every connected peer that has not reported LOADED, then begin with the rest.
            var stragglers = new List<ulong>();
            foreach (var clientId in _engine.Session.GetConnectedClients())
                if (!_loadedPeers.Contains(clientId))
                    stragglers.Add(clientId);

            foreach (var clientId in stragglers)
            {
                Debug.LogWarning($"[Multipleer] Peer {clientId} did not load in time — kicking.");
                _engine.Session.RemoveClient(clientId);
            }

            // Host is always in _loadedPeers; begin with whoever remains (at least the host).
            Begin();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════

        // Serialize the host's current per-slot aggregate and broadcast it unreliably to all peers.
        private void BroadcastSnapshot()
        {
            var rows = new List<ProgressRow>(_slotProgress.Count);
            foreach (var kv in _slotProgress)
                rows.Add(new ProgressRow { SlotIndex = kv.Key, Phase = kv.Value.phase, Percent = kv.Value.percent });
            var sendDetail = string.Join(",", rows.Select(r => $"s{r.SlotIndex}:{r.Phase}/{r.Percent}"));
            Debug.Log($"[Multipleer] RosterProgress SEND [{sendDetail}]");
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
                    Debug.LogError("[Multipleer] co-op load: metadata is not PPSavegameMetaData; " +
                                   "cannot apply PrepareLoadGame state (EnabledDlc/GameId/Difficulty).");
                    return;
                }

                var t = typeof(PhoenixSaveManager);
                // LatestLoad setter (private) also assigns _currentGameId + IsIronmanMode (cs:70-78).
                var latestLoadProp = AccessTools.Property(t, "LatestLoad");
                latestLoadProp?.SetValue(saveManager, pp, null);

                AccessTools.Field(t, "_currentGameId").SetValue(saveManager, pp.GameId);
                AccessTools.Field(t, "_currentDifficulty").SetValue(saveManager, pp.DifficultyDef);
                AccessTools.Field(t, "_enabledDlc")
                    .SetValue(saveManager, pp.EnabledDlc ?? new EntitlementDef[0]);
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] co-op load: failed to apply PrepareLoadGame state: " + e);
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
                    Debug.LogError("[Multipleer] PhoenixGame/SaveManager not available.");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[Multipleer] Failed to resolve PhoenixGame: " + e.Message);
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
                Debug.LogError("[Multipleer] Failed to resolve Timing: " + e.Message);
                return null;
            }
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        // CRC-32 (IEEE 802.3, reflected) — small local impl, no external dependency.
        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < data.Length; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
