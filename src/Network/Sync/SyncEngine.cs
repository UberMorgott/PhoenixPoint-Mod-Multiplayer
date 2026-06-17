using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.Sync.State;
using UnityEngine;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Action-sync subsystem, mirrors <c>TimeSyncManager</c>: created in <c>NetworkEngine.Initialize()</c>,
    /// ticked from <c>NetworkEngine.Update()</c>, fed inbound packets from <c>NetworkEngine.RouteMessage</c>.
    ///
    /// Two mechanisms, one engine:
    ///   A. Currency echo — host subscribes <c>Wallet.ResourcesChanged</c> (via <see cref="WalletWatcher"/>),
    ///      coalesces in <see cref="Tick"/>, broadcasts a versioned full-wallet snapshot; clients apply as
    ///      signed diffs (last-version-wins) inside <see cref="SyncApplyScope"/>.
    ///   B. Action relay — generic discrete-command bus (<see cref="ISyncedAction"/>): client requests →
    ///      host validates + sequences (last-writer-wins) + broadcasts apply → all clients replay.
    /// </summary>
    public sealed class SyncEngine : ISyncSink
    {
        private readonly NetworkEngine _engine;
        private readonly SequenceTracker _tracker = new SequenceTracker();
        private ulong _hostSequence;   // host-assigned, monotonic action sequence
        private ulong _walletVersion;  // host-assigned, monotonic wallet version
        private uint _nonceCounter;    // client request correlation
        private bool _walletDirty;     // host: wallet changed since last flush
        private readonly Dictionary<uint, ISyncedAction> _pending = new Dictionary<uint, ISyncedAction>();
        private readonly Queue<uint> _pendingOrder = new Queue<uint>();   // FIFO eviction order for _pending (bounds growth)

        // Host: inbound-request dedup keyed by (peerId, nonce). The reliable transport deliberately sends
        // every reliable packet TWICE, so each client ActionRequest arrives twice and would otherwise be
        // applied twice on the authority (double manufacture/answer/construct). Bounded FIFO. See RequestDedup.
        private const int MaxPending = 512;
        private readonly RequestDedup _seenRequests = new RequestDedup(512);

        // ─── Generic state-channel echo (StateChannel infra) ───────────────
        private readonly StateChannelRegistry _channels = new StateChannelRegistry();
        private readonly Dictionary<byte, ulong> _channelVersion = new Dictionary<byte, ulong>(); // host: per-channel monotonic version
        private readonly HashSet<byte> _channelDirty = new HashSet<byte>();                        // host: channels changed since last flush

        // ─── Unified surface router (Phase 1 chokepoint) ───────────────────
        // Additive: lives ALONGSIDE the legacy action path. Dormant until something SENDS a
        // SyncEnvelope (Task 7 flip); shares the engine's existing _tracker + _seenRequests so the
        // envelope path's ordering/dedup stays consistent with the still-live legacy path.
        private readonly SurfaceRegistry _surfaces = new SurfaceRegistry();
        private readonly SurfaceRouter _router;

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            SyncRegistration.RegisterAll();   // registers every action reader (later batch)
            SyncRegistration.RegisterSurfaces(_surfaces);
            _router = new SurfaceRouter(_surfaces, _tracker, _seenRequests);
        }

        // ─── Outbound (called by interceptors) ────────────────────────────

        /// <summary>Client: send a discrete action request to the host (block local apply, await echo).</summary>
        public void SendActionRequest(ISyncedAction a)
        {
            if (a == null) return;
            uint nonce = ++_nonceCounter;
            // Track for reject-correlation, but bound it: ActionApply carries seq (not nonce), so the
            // success path never clears _pending — age the oldest out so it can't grow unbounded.
            _pending[nonce] = a;
            _pendingOrder.Enqueue(nonce);
            while (_pendingOrder.Count > MaxPending)
            {
                var old = _pendingOrder.Dequeue();
                _pending.Remove(old);
            }
            var payload = WriteAction(a);
            _engine.SendToHost(new NetworkMessage(PacketType.ActionRequest,
                SyncProtocol.EncodeActionRequest(a.ActionId, nonce, payload)));
        }

        /// <summary>Host: the local interceptor will let the original run; sequence + broadcast the apply to all.</summary>
        public void BroadcastHostAction(ISyncedAction a)
        {
            if (a == null) return;
            ulong seq = ++_hostSequence;
            _tracker.Mark(seq);
            var payload = WriteAction(a);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ActionApply,
                SyncProtocol.EncodeActionApply(a.ActionId, seq, payload)));
        }

        // ─── Inbound: host ────────────────────────────────────────────────

        public void OnActionRequest(ulong senderPeerId, byte[] data)
        {
            if (!_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeActionRequest(data, out var id, out var nonce, out var payload)) return;

            // Host-side dedup: the reliable transport sends every packet twice, so the same request
            // arrives twice. Apply each (peerId, nonce) exactly once on the authority; drop the repeat.
            if (_seenRequests.IsDuplicate(senderPeerId, nonce)) return;

            var action = ReadAction(id, payload);
            if (action == null) return;

            Guid actor = ResolveActor(senderPeerId);
            var rt = GeoRuntime.Instance;
            // Fail CLOSED for an unmapped / forged peer (or no session): ResolveActor returns Guid.Empty,
            // and a permissive HasCampaignPermission default must never let an unknown actor through.
            if (actor == Guid.Empty || !PermissionGate.CheckFor(actor, action.Category) || !action.Validate(rt, actor))
            {
                _engine.SendToClient(senderPeerId, new NetworkMessage(PacketType.ActionReject,
                    SyncProtocol.EncodeActionReject(nonce, 1, "rejected")));
                return;
            }

            try { using (SyncApplyScope.Enter()) action.Apply(rt); }   // host executes authoritative mutation
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionRequest apply failed: " + ex.Message); }

            // Research has no faction-level cancel event: a client-relayed cancel mutates the queue with no
            // change-event to mark the channel dirty. Force a research-channel echo so the new authoritative
            // queue reaches every peer (idempotent reconcile). Start/complete already self-mark via events.
            // TASK1 — instant event-driven research reveal: a geoscape EVENT answer (ActionCategory.Dialogs)
            // can REVEAL research (FIX#2 ch2 carries Research.Visible), but the answer fires no research
            // event, so the reveal otherwise waited for the next in-game HourTicked (frozen while paused).
            // Marking ch2 dirty here for a client-relayed Dialogs answer ships the reveal immediately (Tick
            // flushes in real time). Host-LOCAL answers are covered by CompleteEventPatch.Postfix. Idempotent.
            if (action.Category == ActionCategory.Research || action.Category == ActionCategory.Dialogs)
                MarkChannelDirty(2);

            // The host applies a client request authoritatively but never replays its own echo, so its own
            // open geoscape module never rebuilds — a client-initiated research cancel/start stayed visually
            // stale on the host until it re-entered the screen. GeoUiRefresh was only driven on client-inbound
            // paths (OnActionApply / OnStateSync, both gated to non-host); native host-initiated cancel
            // self-refreshes via UIModuleResearch, which is why host->client looked fine. Re-drive the host's
            // open action-driven modules here, mirroring the client OnActionApply path; each call no-ops if
            // that module isn't open. RefreshNeedsKick fans out over every needs-kick module (research +
            // manufacturing + base-layout facility grid) so e.g. a client facility construct/repair rebuilds
            // the host's open base grid too.
            GeoUiRefresh.RefreshNeedsKick(rt);

            ulong seq = ++_hostSequence;
            _tracker.Mark(seq);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ActionApply,
                SyncProtocol.EncodeActionApply(id, seq, payload)));
        }

        // ─── Inbound: client ──────────────────────────────────────────────

        public void OnActionApply(byte[] data)
        {
            if (_engine.IsHost) return;   // host is the authority; it never replays its own broadcast echo
            if (!SyncProtocol.TryDecodeActionApply(data, out var id, out var seq, out var payload)) return;
            if (!_tracker.ShouldApply(seq)) return;   // last-writer-wins / dedupe
            _tracker.Mark(seq);
            var action = ReadAction(id, payload);
            if (action == null) return;
            // Host-only-apply actions (e.g. event-answer outcomes): the client must NOT replay the
            // outcome side-effects — they would double-apply / diverge from the authoritative host. The
            // host already applied once; synced consequences reconverge via the wallet/inventory/research
            // echoes. We still consume the sequence above so ordering stays correct.
            if (action is IHostOnlyApply)
            {
                // TODO(multipleer): non-channelled event outcomes (site reveal / mission spawn / faction-
                // diplomacy flag / direct research unlock) are NOT yet synced to the client — visible gap.
                Debug.Log("[Multipleer] SyncEngine.OnActionApply: client suppressing host-only-apply action "
                    + "(id=" + id + "); non-channelled outcomes may be unsynced. TODO(multipleer).");
                return;
            }
            try { using (SyncApplyScope.Enter()) action.Apply(GeoRuntime.Instance); }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionApply failed: " + ex.Message); }
            // The open geoscape UI modules rebuild only on (re)Init, so a model mutation from an applied
            // action (e.g. a host research/manufacture START) is invisible until the player re-enters the
            // screen — unlike the state-channel echoes, which already re-drive the open module in OnStateSync.
            // An action carries no screen id, so re-drive every needs-kick module (research + manufacturing +
            // base-layout facility grid) via RefreshNeedsKick; each call no-ops if that module isn't open. This
            // makes host->client action applies reactive (incl. facility construct/repair/complete), matching
            // the remove path.
            GeoUiRefresh.RefreshNeedsKick(GeoRuntime.Instance);
        }

        public void OnActionReject(byte[] data)
        {
            if (!SyncProtocol.TryDecodeActionReject(data, out var nonce, out var code, out var reason)) return;
            _pending.Remove(nonce);
            Debug.Log("[Multipleer] action rejected (" + code + "): " + reason);
            // v1: log only; UI feedback hook later.
        }

        // ─── Currency (mechanism A) ───────────────────────────────────────

        /// <summary>Host: WalletWatcher callback when the player wallet changes (coalesced in Tick).</summary>
        public void MarkWalletDirty() => _walletDirty = true;

        public void OnWalletSync(byte[] data)
        {
            if (_engine.IsHost) return;   // host is the authority; never applies an echo
            if (!SyncProtocol.TryDecodeWalletSync(data, out var ver, out var slots)) return;
            if (!_tracker.ShouldApplyWallet(ver)) return;
            _tracker.MarkWallet(ver);
            try { using (SyncApplyScope.Enter()) WalletApplier.Apply(GeoRuntime.Instance, slots); }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnWalletSync failed: " + ex.Message); }
        }

        /// <summary>Host: push a full versioned wallet snapshot (geoscape became active / late joiner ready).</summary>
        public void BroadcastFullWallet()
        {
            if (!_engine.IsHost) return;
            var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
            if (slots == null) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                SyncProtocol.EncodeWalletSync(++_walletVersion, slots)));
        }

        // ─── Generic state-channel echo (mechanism C) ────────────────────

        /// <summary>Host: a channel's change-event fired; coalesced flush in <see cref="Tick"/>.</summary>
        public void MarkChannelDirty(byte channelId) => _channelDirty.Add(channelId);

        /// <summary>Host: snapshot + version-bump + broadcast a single channel. No-op if snapshot unavailable.</summary>
        private void FlushChannel(IStateChannel channel)
        {
            var payload = channel.Snapshot(GeoRuntime.Instance);
            if (payload == null) return;
            byte id = channel.ChannelId;
            _channelVersion.TryGetValue(id, out var v);
            v++;
            _channelVersion[id] = v;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.StateSync,
                SyncProtocol.EncodeStateSync(id, v, payload)));
        }

        /// <summary>Host: push every channel's current state (geoscape became active / late joiner ready).</summary>
        public void BroadcastAllChannels()
        {
            if (!_engine.IsHost) return;
            foreach (var ch in _channels.All) FlushChannel(ch);
        }

        public void OnStateSync(byte[] data)
        {
            if (_engine.IsHost) return;   // host is the authority; never applies its own echo
            if (!SyncProtocol.TryDecodeStateSync(data, out var channelId, out var ver, out var payload)) return;
            var channel = _channels.Get(channelId);
            if (channel == null) return;
            if (!_tracker.ShouldApplyChannel(channelId, ver)) return;   // per-channel last-version drop
            _tracker.MarkChannel(channelId, ver);
            try { using (SyncApplyScope.Enter()) channel.Apply(GeoRuntime.Instance, payload); }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnStateSync apply failed: " + ex.Message); return; }
            // Best-effort: rebuild the open UI for this channel's screen. Channels 1/2 map to a single
            // screen (targeted Refresh). The unlock (3) + diplomacy (4) channels span multiple modules (an
            // unlock shows in BOTH the manufacturing list AND the base-layout facility picker; diplomacy has
            // no commonly-open module), so drive the full needs-kick fan-out for ids ≥ 3 — each Refresh
            // no-ops if that module is closed, so a redundant kick is harmless.
            var screen = _channels.ScreenFor(channelId);
            if (screen.HasValue) GeoUiRefresh.Refresh(GeoRuntime.Instance, screen.Value);
            else if (channelId >= 3) GeoUiRefresh.RefreshNeedsKick(GeoRuntime.Instance);
        }

        /// <summary>Host: drop all channel change-event subscriptions (session end). Idempotent.</summary>
        public void DetachAllChannels()
        {
            foreach (var ch in _channels.All) ch.DetachHost();
        }

        // ─── Geoscape event display (host->all show/dismiss) ───────────────

        /// <summary>Client: host raised a geoscape event → reconstruct + show the dialog (PauseGame=false).</summary>
        public void OnEventRaised(byte[] data)
        {
            if (_engine.IsHost) return;   // host shows it via its own local sim
            if (!SyncProtocol.TryDecodeEventRaised(data, out var eventId, out var siteId)) return;
            if (string.IsNullOrEmpty(eventId)) return;
            try
            {
                var rt = GeoRuntime.Instance;
                var geoEvent = EventReflection.BuildEvent(rt, eventId, siteId);
                if (geoEvent != null) State.EventDisplay.Show(rt, geoEvent);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnEventRaised failed: " + ex.Message); }
        }

        /// <summary>Client: host's answer was applied → close the open geoscape-event dialog.</summary>
        public void OnEventDismiss(byte[] data)
        {
            if (_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeEventDismiss(data, out var eventId)) return;
            try { State.EventDisplay.Dismiss(GeoRuntime.Instance, eventId); }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnEventDismiss failed: " + ex.Message); }
        }

        /// <summary>Host: broadcast a show/dismiss event-dialog packet to all peers.</summary>
        public void BroadcastEventRaised(string eventId, int siteId)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventRaised,
                SyncProtocol.EncodeEventRaised(eventId, siteId)));
        }

        public void BroadcastEventDismiss(string eventId)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventDismiss,
                SyncProtocol.EncodeEventDismiss(eventId)));
        }

        // ─── Per-frame tick (from NetworkEngine.Update) ───────────────────

        public void Tick()
        {
            if (_engine == null || !_engine.IsActive) return;
            if (!_engine.IsHost) return;

            // Host: bind the wallet watcher once the geoscape (and its wallet) is live. Attach is
            // idempotent — it early-returns until the wallet exists, then once it is bound. Mirrors the
            // deferred world-load: the wallet only appears frames after EnterLevel→FinishLevel.
            WalletWatcher.Attach(_engine);

            // Host: bind every state channel's change-event the same way (idempotent per channel).
            foreach (var ch in _channels.All) ch.AttachHost(this);

            if (_walletDirty)
            {
                _walletDirty = false;
                var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (slots != null)
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                        SyncProtocol.EncodeWalletSync(++_walletVersion, slots)));
            }

            // Coalesced per-channel flush: snapshot + ++version + broadcast each dirty channel once.
            if (_channelDirty.Count > 0)
            {
                foreach (var id in _channelDirty)
                {
                    var ch = _channels.Get(id);
                    if (ch != null) FlushChannel(ch);
                }
                _channelDirty.Clear();
            }
        }

        // ─── Unified envelope inbound + ISyncSink outbound ─────────────────
        // Additive (Phase 1, Task 6): the SurfaceRouter receive path lives ALONGSIDE the legacy
        // OnActionRequest/OnActionApply path, which stays primary. Dormant until a SyncEnvelope is
        // actually sent (the Task-7 flip wires SendActionRequest → SendActionRequestViaEnvelope).

        /// <summary>Inbound: a unified surface envelope arrived. Routes through the single chokepoint.</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] data) => _router.OnInbound(senderPeerId, data, this);

        /// <summary>Client: send an action as a request envelope (new unified path; not yet wired to interceptors).</summary>
        public void SendActionRequestViaEnvelope(ISyncedAction a)
        {
            if (a == null) return;
            byte surfaceId = (byte)a.ActionId;   // action surface ids mirror SyncedActionIds (== SurfaceIds)
            var payload = WriteAction(a);
            _engine.SendToHost(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionRequest, payload)));
        }

        bool ISyncSink.IsHost => _engine.IsHost;
        GeoRuntime ISyncSink.Runtime => GeoRuntime.Instance;
        Guid ISyncSink.ResolveActor(ulong peerId) => ResolveActor(peerId);

        void ISyncSink.RejectTo(ulong peerId, byte surfaceId)
            => _engine.SendToClient(peerId, new NetworkMessage(PacketType.ActionReject,
                SyncProtocol.EncodeActionReject(0u, 1, "rejected")));

        void ISyncSink.RebroadcastActionApply(byte surfaceId, ulong sequence, byte[] payload)
            => _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(surfaceId, SyncKind.ActionApply,
                    SurfaceRouter.EncodeApplyPayload(sequence, payload))));

        void ISyncSink.MarkSurfaceDirty(byte surfaceId)
        {
            // Phase 1: mirror the legacy OnActionRequest special-case — research surfaces re-echo
            // channel 2 (research has no faction-level cancel event to self-mark the channel dirty).
            if (surfaceId == SurfaceIds.StartResearch || surfaceId == SurfaceIds.CancelResearch
                || surfaceId == SurfaceIds.ResearchCompleted || surfaceId == SurfaceIds.ReorderResearch)
                MarkChannelDirty(2);
        }

        /// <summary>After a synced apply, re-drive the open needs-kick geoscape modules (mirrors legacy GeoUiRefresh fan-out).</summary>
        void ISyncSink.RefreshUi() => GeoUiRefresh.RefreshNeedsKick(GeoRuntime.Instance);

        // ─── Helpers ──────────────────────────────────────────────────────

        private static byte[] WriteAction(ISyncedAction a)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                a.Write(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static ISyncedAction ReadAction(ushort id, byte[] payload)
        {
            using (var ms = new MemoryStream(payload ?? new byte[0]))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
                return SyncedActionRegistry.Read(id, r);
        }

        private Guid ResolveActor(ulong peerId)
            => _engine.Session != null && _engine.Session.Clients.TryGetValue(peerId, out var ci)
                ? ci.PlayerGuid
                : Guid.Empty;
    }
}
