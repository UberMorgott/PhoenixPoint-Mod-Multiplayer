using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multipleer.Network.MessageLayer;
using Multipleer.Network.Sync.Actions;
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

        // ─── Unified 0x67 envelope router (LIVE tactical fast-path only) ───────────────────
        // Thin dispatcher to the tactical replication hook (SurfaceRouter.TacticalInbound, armed by
        // TacticalDeploySync.ArmInboundHook). The geoscape ACTION relay rides the LEGACY 0x60/0x61/0x62 path
        // above (OnActionRequest/OnActionApply/OnActionReject); the dead 0x67 action-relay was never wired
        // (zero senders) and has been removed.
        private readonly SurfaceRouter _router = new SurfaceRouter();

        // ─── Client geoscape-event raise/dismiss correlation (occurrence-id keyed) ─────────
        // Pure, Unity-free ordering brain: keys raise/dismiss on the host-synthesized per-occurrence id so two
        // occurrences of the same reusable EventID def-name never collide, and a Dismiss that arrives before its
        // Raise is buffered then resolved straight to the result page (fixes the "EX20" collision/ordering bug).
        private readonly State.EventCorrelator _eventCorrelator = new State.EventCorrelator();

        // First-click-wins arbiter for geoscape-event choices, keyed by per-occurrence id. WIRED: the
        // CompleteEventPatch.Prefix host gate (the universal chokepoint both a host click AND a client-relayed
        // answer converge on, via native CompleteEvent) reaches it through NetworkEngine.Instance.Sync.Arbiter
        // and Claim()s the occId — the FIRST claim per occurrence proceeds (one RNG roll, one EventDismiss
        // broadcast); every later claim is skipped (no second roll/broadcast, no native-CompleteEvent throw).
        // Instance-scoped so it resets automatically when NetworkEngine recreates Sync on session teardown.
        private readonly State.ChoiceArbiter _choiceArbiter = new State.ChoiceArbiter();

        /// <summary>Host-side first-click-wins arbiter for event choices (reached from CompleteEventPatch.Prefix).</summary>
        public State.ChoiceArbiter Arbiter => _choiceArbiter;

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            SyncRegistration.RegisterAll();   // registers every action reader (legacy 0x60/0x61 relay)
            // Inc1 rail-unify: arm the SurfaceRouter geoscape fast-path so a geoscape envelope surface (0xA0+)
            // routes to this engine's appliers. Inert for traffic the host never sends (gated by GeoRailGate).
            _router.GeoscapeInbound = HandleGeoscapeEnvelope;
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
            var answer = action as AnswerEventAction;
            // Fail CLOSED for an unmapped / forged peer (or no session): ResolveActor returns Guid.Empty.
            // PERMISSION (user directive): event choices (ActionCategory.Dialogs) are NOT permission-gated for now —
            // everyone may click, last-write-wins (the permission system is deferred, its code kept for later). All
            // OTHER categories still go through PermissionGate.CheckFor. Validate still applies to every action.
            bool permitted = action.Category == ActionCategory.Dialogs   // event answers un-gated (AnswerEventAction is Dialogs)
                             || PermissionGate.CheckFor(actor, action.Category);
            if (actor == Guid.Empty || !permitted || !action.Validate(rt, actor))
            {
                _engine.SendToClient(senderPeerId, new NetworkMessage(PacketType.ActionReject,
                    SyncProtocol.EncodeActionReject(nonce, 1, "rejected")));
                return;
            }

            // A REMOTE client answered a geoscape event. First-click-wins arbitration is enforced one layer down at
            // the CompleteEvent chokepoint (CompleteEventPatch.Prefix → Arbiter.Claim(occId)), which both this
            // relayed answer and a host-local click pass through — so a lost near-simultaneous double is skipped
            // there (no second roll/broadcast). Prefer driving the host's OWN open native
            // modal through the exact native click path (TryHostNativeResolve) → the host shows the native
            // result/reward page + OK-closes + auto-broadcasts the dismiss, identical to a host click. If the host
            // isn't showing that event (TryHostNativeResolve == false), fall back to the model-only reflected resolve
            // (action.Apply → CompleteEventByOccurrence) so authoritative state still converges + the dismiss
            // broadcasts to clients (host just won't render a result page in that edge case). Both run OUTSIDE
            // SyncApplyScope so CompleteEventDismissPatch.Postfix (early-returns under IsApplying) fires its broadcast.
            if (answer != null)
            {
                try
                {
                    if (!EventReflection.TryHostNativeResolve(rt, answer.OccurrenceId, answer.EventId, answer.ChoiceIndex))
                        action.Apply(rt);   // fallback: model-only reflected resolve (IResolvesOutsideScope → no scope)
                }
                catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionRequest answer resolve failed: " + ex.Message); }
            }
            else
            {
                try
                {
                    // IResolvesOutsideScope actions run OUTSIDE SyncApplyScope; every other action runs INSIDE so its
                    // interceptors pass through (engine-driven replay).
                    if (action is IResolvesOutsideScope) action.Apply(rt);
                    else using (SyncApplyScope.Enter()) action.Apply(rt);   // host executes authoritative mutation
                }
                catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionRequest apply failed: " + ex.Message); }
            }

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
            ulong ver = ++_walletVersion;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                SyncProtocol.EncodeWalletSync(ver, slots)));
            // Inc1 rail-unify (additive, default OFF): ALSO mirror the SAME versioned snapshot onto the unified
            // 0x67 envelope rail. Same version ⇒ the client applies whichever arrives first and drops the other
            // (ShouldApplyWallet is strict >). Retiring the legacy 0x63 send above is a later, in-game-verified slice.
            if (GeoRailGate.Enabled)
            {
                _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                        SyncProtocol.EncodeWalletSync(ver, slots))));
            }
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
            var stateBytes = SyncProtocol.EncodeStateSync(id, v, payload);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.StateSync, stateBytes));
            // Inc1 rail-unify slice 2 (additive, gated): ALSO mirror the SAME per-channel state echo onto the
            // unified 0x67 envelope rail under the GeoState surface. The inner bytes are the IDENTICAL
            // EncodeStateSync(id, v, payload) the legacy 0x64 carries, so OnStateSync's per-channel version
            // guard (SequenceTracker.ShouldApplyChannel, strict >) drops whichever of the two arrives second —
            // applying twice is a no-op. Legacy 0x64 above is retained (retirement deferred to a later slice).
            if (GeoRailGate.Enabled)
            {
                _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot, stateBytes)));
            }
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

        /// <summary>
        /// Client: host raised a geoscape event. The raise is correlated by its per-OCCURRENCE id
        /// (<see cref="State.EventCorrelator"/>): a normal raise builds + shows the dialog; a raise that matches
        /// a BUFFERED out-of-order dismiss resolves straight to the result page (no orphan choice dialog); a
        /// buffered close-only dismiss is a no-op (the player never saw a dialog).
        /// </summary>
        public void OnEventRaised(byte[] data)
        {
            if (_engine.IsHost) return;   // host shows it via its own local sim
            if (!SyncProtocol.TryDecodeEventRaised(data, out var occId, out var eventId, out var siteId, out var vehicleId, out var hasIdentity, out var identity, out var singleChoice)) return;
            if (string.IsNullOrEmpty(eventId)) return;
            try
            {
                // The gate decides whether a single-choice event MIRRORS the host's window-1 prompt (ON) or keeps
                // the legacy unconditional jump to the result page (OFF): off-gate we pass singleChoice=false so
                // EventCorrelator.Raised takes its byte-for-byte legacy ShowResultPage branch.
                bool mirrorSingleChoice = EventMirrorFixGate.Enabled && singleChoice;
                var decision = _eventCorrelator.Raised(occId, eventId, mirrorSingleChoice);
                Debug.Log("[Multipleer] CLIENT OnEventRaised occId=" + occId + " eventId=" + eventId +
                          " siteId=" + siteId + " vehicleId=" + vehicleId + " singleChoice=" + singleChoice +
                          " mirror=" + mirrorSingleChoice + " decision=" + decision.Kind +
                          " open=" + _eventCorrelator.OpenCount + " pending=" + _eventCorrelator.PendingCount +
                          " promptMirror=" + _eventCorrelator.PromptMirrorCount);
                var rt = GeoRuntime.Instance;
                switch (decision.Kind)
                {
                    case State.EventCorrelator.ActionKind.ShowDialog:
                    {
                        // A normal in-order raise: NOTHING was buffered for this occurrence. (A result-bearing
                        // out-of-order dismiss now resolves via ShowResultPage, and a close-only one via DropNoop —
                        // see EventCorrelator.Raised; neither lands here. The old "single-choice flavor-mirror"
                        // comment predates that correlator change.) DropBufferedReward is therefore a no-op here,
                        // but it WOULD silently discard a reward if a stash ever existed, so the EventMirrorFix gate
                        // drops the call entirely (never throw a reward away from under a page the client will show).
                        // Byte-for-byte legacy when the gate is OFF.
                        if (!EventMirrorFixGate.Enabled) DropBufferedReward(occId);
                        // Single-choice prompt-MIRROR (gate ON): ChoiceIndex>=0 marks a buffered-dismiss raise that
                        // EventCorrelator re-showed as the host's window-1 PROMPT (not a jump to the result page).
                        // The reward stashed from the earlier out-of-order dismiss is intentionally LEFT in place
                        // (not dropped above) so the host's later advance (OnEventAdvanceResult) can render it.
                        if (decision.ChoiceIndex >= 0)
                            Debug.Log("[Multipleer] CLIENT singleChoice prompt-mirror occId=" + occId + " eventId=" + eventId +
                                      " choiceIndex=" + decision.ChoiceIndex + " → showing PROMPT, awaiting host advance (reward stashed)");
                        // Case B: an in-play site this sim-frozen client never created is ABSENT. Spawn an inert
                        // mirror site from the carried identity BEFORE building the event so BuildEvent's own
                        // site resolution finds it and renders the correct backdrop/subtitle (not StartingBase).
                        if (hasIdentity && EventReflection.ShouldSpawnMirror(
                                hasIdentity, State.GeoSiteReflection.ResolveSiteById(rt, siteId) != null))
                            State.GeoSiteReflection.SpawnMirrorSite(rt, identity);
                        var geoEvent = EventReflection.BuildEvent(rt, eventId, siteId, vehicleId,
                            hasIdentity ? (GeoSiteState?)identity : null);
                        if (geoEvent != null) State.EventDisplay.Show(rt, geoEvent, occId, eventId);
                        break;
                    }
                    case State.EventCorrelator.ActionKind.ShowResultPage:
                        // Out-of-order dismiss already buffered for this occurrence → jump straight to its
                        // result page. The reward lines were carried on the dismiss and stashed at buffer time.
                        ResolveToResultPage(rt, occId, eventId, decision.ChoiceIndex, TakeBufferedReward(occId), siteId);
                        break;
                    case State.EventCorrelator.ActionKind.DropNoop:
                        // A close-only dismiss beat its raise → nothing to display; drop any stashed reward.
                        DropBufferedReward(occId);
                        break;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnEventRaised failed: " + ex.Message); }
        }

        // ─── Out-of-order reward stash (keyed by occurrence id) ─────────────────────────────
        // When a dismiss arrives BEFORE its raise the reward snapshot must be held until the raise builds the
        // result page (so the ReferenceEquals-armed render still lands). Bounded by the correlator's own pending
        // buffer cap (we only stash for buffered dismisses), pruned on resolve/drop.
        private readonly Dictionary<ushort, RewardDisplaySnapshot> _bufferedRewards = new Dictionary<ushort, RewardDisplaySnapshot>();

        private void StashBufferedReward(ushort occId, RewardDisplaySnapshot reward)
        {
            if (reward == null || reward.IsEmpty) { _bufferedRewards.Remove(occId); return; }
            _bufferedRewards[occId] = reward;
            // Hard cap mirrors the correlator's pending-dismiss buffer: a stash whose buffered dismiss got
            // evicted (its raise never came) would otherwise linger. Drop the excess (arbitrary entry) so this
            // map can never outgrow the bounded correlator state.
            if (_bufferedRewards.Count > State.EventCorrelator.MaxPendingDismiss)
            {
                foreach (var stale in new List<ushort>(_bufferedRewards.Keys))
                {
                    if (_bufferedRewards.Count <= State.EventCorrelator.MaxPendingDismiss) break;
                    if (stale != occId) _bufferedRewards.Remove(stale);
                }
            }
        }
        private RewardDisplaySnapshot TakeBufferedReward(ushort occId)
        {
            if (_bufferedRewards.TryGetValue(occId, out var r)) { _bufferedRewards.Remove(occId); return r; }
            return null;
        }
        private void DropBufferedReward(ushort occId) => _bufferedRewards.Remove(occId);

        /// <summary>
        /// Client: host's answer was applied. The dismiss is correlated by its per-OCCURRENCE id: when its dialog
        /// is open it is resolved in place (choiceIndex &gt;= 0 → rebuild + show the RESULT/OUTCOME page;
        /// choiceIndex == -1 → close-only); when the matching raise hasn't arrived yet the dismiss is BUFFERED
        /// (reward stashed) and resolved the instant the raise lands. If a result page can't be rebuilt, fall
        /// back to a plain close so the modal never stays stuck open.
        /// </summary>
        public void OnEventDismiss(byte[] data)
        {
            if (_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeEventDismiss(data, out var occId, out var eventId, out var choiceIndex, out var rewardBlob, out var siteId)) return;
            try
            {
                var rt = GeoRuntime.Instance;
                // Decode the reward delta-line snapshot (empty blob → empty snapshot → no-op render). A NON-empty
                // blob that fails to decode (null) is a corrupt/version-mismatched reward — log it (the codec is
                // pure/Unity-free, so this boundary is where the malformed-blob visibility log belongs).
                var reward = RewardDisplaySnapshot.Decode(rewardBlob);
                if (reward == null && rewardBlob != null && rewardBlob.Length > 0)
                    Debug.LogError("[Multipleer] reward decode failed (malformed blob, " + rewardBlob.Length + " bytes) — result card shown without reward lines");

                var decision = _eventCorrelator.Dismissed(occId, eventId, choiceIndex);
                Debug.Log("[Multipleer] CLIENT OnEventDismiss occId=" + occId + " eventId=" + eventId +
                          " choiceIndex=" + choiceIndex + " rewardBytes=" + (rewardBlob?.Length ?? 0) +
                          " rewardEmpty=" + (reward == null || reward.IsEmpty) + " decision=" + decision.Kind +
                          " open=" + _eventCorrelator.OpenCount + " pending=" + _eventCorrelator.PendingCount);
                switch (decision.Kind)
                {
                    case State.EventCorrelator.ActionKind.ShowResultInPlace:
                        ResolveToResultPage(rt, occId, eventId, choiceIndex, reward, siteId);
                        break;
                    case State.EventCorrelator.ActionKind.CloseDialog:
                        State.EventDisplay.Dismiss(rt, occId, eventId);   // close-only
                        break;
                    case State.EventCorrelator.ActionKind.BufferDismiss:
                        // Raise hasn't arrived yet → hold the reward until OnEventRaised resolves this occurrence.
                        StashBufferedReward(occId, reward);
                        break;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnEventDismiss failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: the host advanced a SINGLE-CHOICE event from its window-1 PROMPT to its window-2 RESULT page
        /// (the host player clicked the lone prompt button). Because the event auto-completed at trigger, that
        /// click runs no native CompleteEvent — so no EventDismiss fires — and THIS dedicated signal is how the
        /// client learns to follow. Correlated by occurrence id (<see cref="State.EventCorrelator.Advanced"/>):
        /// if the client is mirroring the prompt it advances to the result page (reusing the reward stashed from
        /// the earlier out-of-order dismiss); if the advance beat the raise it is buffered until the raise lands.
        /// Reuses the EventDismiss codec on the wire (no reward blob). Host never applies its own broadcast.
        /// Inert when <c>EventMirrorFixGate</c> is OFF: the host emits no such packet.
        /// </summary>
        public void OnEventAdvanceResult(byte[] data)
        {
            if (_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeEventDismiss(data, out var occId, out var eventId, out var choiceIndex, out _, out var siteId)) return;
            try
            {
                var rt = GeoRuntime.Instance;
                var decision = _eventCorrelator.Advanced(occId, eventId, choiceIndex);
                Debug.Log("[Multipleer] CLIENT OnEventAdvanceResult occId=" + occId + " eventId=" + eventId +
                          " choiceIndex=" + choiceIndex + " siteId=" + siteId + " decision=" + decision.Kind +
                          " promptMirror=" + _eventCorrelator.PromptMirrorCount +
                          " pendingAdvance=" + _eventCorrelator.PendingAdvanceCount);
                // Mirroring the prompt → advance to the result page (reward = the one stashed at the earlier
                // out-of-order dismiss). Otherwise the advance was BUFFERED (it beat the raise) → no-op now; the
                // upcoming raise resolves it straight to the result page.
                if (decision.Kind == State.EventCorrelator.ActionKind.ShowResultPage)
                    ResolveToResultPage(rt, occId, eventId, choiceIndex, TakeBufferedReward(occId), siteId);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnEventAdvanceResult failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: rebuild the chosen choice's RESULT/OUTCOME page and replace the (possibly already-resolved)
        /// dialog with it, arming the reward render keyed to THIS synthetic event instance right before the show
        /// so the ReferenceEquals-correlated RewardRenderPatch lands on the correct page exactly once. Falls back
        /// to a plain close when the page can't be rebuilt. Shared by the in-order (ShowResultInPlace) and the
        /// buffered-then-raised (ShowResultPage) paths.
        /// </summary>
        private void ResolveToResultPage(GeoRuntime rt, ushort occId, string eventId, int choiceIndex, RewardDisplaySnapshot reward, int siteId = -1)
        {
            var resultEvent = EventReflection.BuildResultEvent(rt, eventId, choiceIndex, siteId);
            Debug.Log("[Multipleer] CLIENT ResolveToResultPage occId=" + occId + " eventId=" + eventId +
                      " choiceIndex=" + choiceIndex + " builtResult=" + (resultEvent != null) +
                      " rewardEmpty=" + (reward == null || reward.IsEmpty) +
                      " branch=" + (resultEvent != null ? "ShowResult" : "fallback-Dismiss"));
            if (resultEvent != null)
            {
                // Arm the reward render BEFORE showing, keyed to THIS synthetic event instance. The native
                // UIModuleSiteEncounters.ShowEncounter Postfix (RewardRenderPatch) consumes it by reference
                // identity when our page is built — exactly once, onto the correct module.
                if (reward != null && !reward.IsEmpty)
                    State.RewardDisplayReflection.SetPending(resultEvent, reward);
                else
                    State.RewardDisplayReflection.ClearPending();
                State.EventDisplay.ShowResult(rt, resultEvent, occId, eventId);
                return;
            }
            // Result page couldn't be rebuilt → no page to attach reward lines to; clear any armed slot + close.
            State.RewardDisplayReflection.ClearPending();
            State.EventDisplay.Dismiss(rt, occId, eventId);
        }

        /// <summary>Host: broadcast a show event-dialog packet to all peers, carrying the occurrence id and an
        /// optional absent-site identity block (so a client without the site degrades gracefully, not StartingBase).</summary>
        public void BroadcastEventRaised(ushort occurrenceId, string eventId, int siteId, int vehicleId, GeoSiteState? identity = null, bool singleChoice = false)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventRaised,
                SyncProtocol.EncodeEventRaised(occurrenceId, eventId, siteId, vehicleId, identity, singleChoice)));
        }

        /// <summary>
        /// Host: tell clients the answer was applied. <paramref name="occurrenceId"/> matches the raise so clients
        /// correlate even when two occurrences share a def-id. <paramref name="choiceIndex"/> is the picked
        /// choice's index within EventData.Choices (&gt;= 0 → clients rebuild + show its RESULT/OUTCOME page
        /// natively; -1 → close-only, for a pure-INFO host-OK / decline). The reward STATE itself rides the
        /// wallet/research/items/diplomacy channels — this carries only the UI index + the display blob.
        /// <paramref name="siteId"/> is the event's GeoSite.SiteId (-1 = none) so the client result card resolves
        /// the REAL event site instead of falling back to StartingBase.
        /// </summary>
        public void BroadcastEventDismiss(ushort occurrenceId, string eventId, int choiceIndex = -1, byte[] rewardBlob = null, int siteId = -1)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventDismiss,
                SyncProtocol.EncodeEventDismiss(occurrenceId, eventId, choiceIndex, rewardBlob, siteId)));
        }

        /// <summary>
        /// Host: tell clients a SINGLE-CHOICE event advanced from its window-1 PROMPT to its window-2 RESULT page.
        /// Used ONLY for the single-choice-with-outcome case where the host's prompt click runs no native
        /// CompleteEvent (the event auto-completed at trigger) — so no <see cref="BroadcastEventDismiss"/> fires
        /// to advance the client. Reuses the EventDismiss wire codec (occId/eventId/<paramref name="choiceIndex"/>/
        /// <paramref name="siteId"/>; NO reward blob — the client reuses the reward stashed from the earlier
        /// dismiss). Emitted by <c>SingleChoiceAdvancePatch</c> only when <c>EventMirrorFixGate</c> is ON; no-op
        /// off-host.
        /// </summary>
        public void BroadcastEventAdvanceResult(ushort occurrenceId, string eventId, int choiceIndex, int siteId)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventAdvanceResult,
                SyncProtocol.EncodeEventDismiss(occurrenceId, eventId, choiceIndex, null, siteId)));
        }

        // ─── Geoscape report-window mirror (host->all show, Phase-A) ───────

        /// <summary>
        /// Host: broadcast a report window (mission/research/base/reveal/diplomacy outcome modal) to all peers.
        /// The payload was built by <c>ReportModalClassifier.TryBuild</c> at the host chokepoint. Mirrors
        /// <see cref="BroadcastEventRaised"/>. Gated upstream on <c>ReportMirrorGate.Enabled</c>; no-op off-host.
        /// </summary>
        public void BroadcastReportModal(State.ReportModalPayload payload)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ReportModalShow,
                SyncProtocol.EncodeReportModal(payload)));
        }

        /// <summary>
        /// Client: the host opened a report window. Decode + reconstruct the modalData from already-synced ids by
        /// variant, then replay the native modal under <see cref="SyncApplyScope"/> (so any patched opener that
        /// fires during the push is treated as engine-driven, exactly like the EventRaised path). Mirrors
        /// <see cref="OnEventRaised"/>. Authority guard: a host never applies its own broadcast.
        /// </summary>
        public void OnReportModalShow(byte[] data)
        {
            if (_engine.IsHost) return;   // host shows it via its own local sim
            if (!SyncProtocol.TryDecodeReportModal(data, out var p)) return;
            try
            {
                var rt = GeoRuntime.Instance;
                object modalData;
                switch (p.Variant)
                {
                    case State.ReportModalVariant.NullData:
                        modalData = null;
                        break;
                    case State.ReportModalVariant.SiteOnly:
                        // Resolve the revealed site by id (null → the native no-site PandoranRevealResult path).
                        modalData = State.ReportModalReflection.ResolveSite(rt, p.SiteId);
                        break;
                    case State.ReportModalVariant.Research:
                        modalData = State.ReportModalReflection.BuildResearchCompleteData(rt, p.DefId);
                        if (modalData == null) return;   // element unresolved → don't show an empty card
                        break;
                    case State.ReportModalVariant.Diplomacy:
                        modalData = State.ReportModalReflection.BuildDiplomacyData(rt, p.DefId, p.ExtraIds, p.ShareLevel);
                        break;
                    default:
                        return;   // Phase-B (MissionOutcome) / unknown variant → ignore this phase
                }
                bool persistent = State.ReportModalClassifier.IsPersistent(p.Variant);
                Debug.Log("[Multipleer] CLIENT OnReportModalShow modalType=" + p.ModalType + " variant=" + p.Variant +
                          " siteId=" + p.SiteId + " defId=" + p.DefId + " extras=" + (p.ExtraIds?.Count ?? 0) +
                          " shareLevel=" + p.ShareLevel + " priority=" + p.Priority + " persistent=" + persistent +
                          " hasData=" + (modalData != null));
                using (SyncApplyScope.Enter())
                    State.GeoModalDisplay.Show(rt, p.ModalType, modalData, p.Priority, persistent);
            }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnReportModalShow failed: " + ex.Message); }
        }

        // The host's own event-choice click is PURE NATIVE — the click patch lets the native
        // UIModuleSiteEncounters.OnChoiceSelected run untouched, which renders the host's result/reward page and
        // broadcasts the dismiss. First-click-wins arbitration lives one layer down at the universal CompleteEvent
        // chokepoint (CompleteEventPatch.Prefix → Arbiter.Claim(occId)): both a host click and a client-relayed
        // answer (TryHostNativeResolve drives the same native OnChoiceSelected → CompleteEvent) pass through that
        // single host gate, so the FIRST to complete an occurrence wins and any near-simultaneous double is skipped.

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
                {
                    ulong ver = ++_walletVersion;
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                        SyncProtocol.EncodeWalletSync(ver, slots)));
                    // Inc1 rail-unify (additive, default OFF): mirror the same versioned snapshot onto the 0x67
                    // envelope rail. Idempotent (version-guarded) so both paths can run live during verification.
                    if (GeoRailGate.Enabled)
                    {
                        _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                            SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                                SyncProtocol.EncodeWalletSync(ver, slots))));
                    }
                }
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

        // ─── Unified 0x67 envelope inbound (LIVE tactical fast-path) ─────────────────
        // The SurfaceRouter dispatches the decoded envelope to the tactical replication hook
        // (SurfaceRouter.TacticalInbound). The geoscape ACTION relay rides the LEGACY 0x60/0x61/0x62 path
        // above (OnActionRequest/OnActionApply/OnActionReject), which stays primary.

        /// <summary>Inbound: a unified 0x67 envelope arrived. Routes to the tactical fast-path chokepoint.</summary>
        public void OnSyncEnvelope(ulong senderPeerId, byte[] data) => _router.OnInbound(senderPeerId, data, this);

        /// <summary>SurfaceRouter geoscape fast-path: returns true if this surface is a geoscape surface it
        /// consumed (so the router stops). Mirrors the tactical HandleTacticalEnvelope switch. The inner payload
        /// is the surface's own bytes (e.g. EncodeWalletSync output), routed to the EXISTING applier.</summary>
        private bool HandleGeoscapeEnvelope(byte surfaceId, byte[] payload)
        {
            if (surfaceId == SurfaceIds.GeoWallet)
            {
                // Behavior-identical to the legacy 0x63 path: OnWalletSync is host-guarded + version-guarded, so
                // applying via the envelope is idempotent (a same-version duplicate from the legacy packet drops).
                try { OnWalletSync(payload); }
                catch (Exception ex) { Debug.LogError("[Multipleer][geo] geo wallet envelope failed: " + ex.Message); }
                return true;
            }
            if (surfaceId == SurfaceIds.GeoState)
            {
                // Behavior-identical to the legacy 0x64 path: OnStateSync is host-guarded + per-channel
                // version-guarded (SequenceTracker.ShouldApplyChannel), so applying via the envelope is
                // idempotent — a same-version duplicate from the legacy packet (or a re-send) drops.
                try { OnStateSync(payload); }
                catch (Exception ex) { Debug.LogError("[Multipleer][geo] geo state envelope failed: " + ex.Message); }
                return true;
            }
            return false;
        }

        bool ISyncSink.IsHost => _engine.IsHost;
        GeoRuntime ISyncSink.Runtime => GeoRuntime.Instance;
        Guid ISyncSink.ResolveActor(ulong peerId) => ResolveActor(peerId);

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
