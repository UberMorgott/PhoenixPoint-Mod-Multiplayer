using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Network.Sync.State;
using UnityEngine;

namespace Multiplayer.Network.Sync
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
        // Host: the last absolute wallet snapshot actually broadcast (poll baseline). Updated only at the two
        // broadcast sites (the Tick dirty-flush + BroadcastFullWallet) so the snapshot-diff poll never re-fires
        // what was just sent. See WalletSnapshotDiff + the Tick poll backstop below.
        private List<(int, float)> _lastWalletBroadcast;
        private int _walletPollTick;   // host: frame counter throttling the absolute snapshot-diff poll
        // Run the binding-independent wallet snapshot-diff poll every Nth Tick only, so the 11 reflection
        // GetAmount reads don't run every frame — the event path + bind/ready belts catch the common case
        // instantly; the poll is just the convergence backstop for a missed/stale-bound ResourcesChanged.
        private const int WalletPollTickInterval = 15;
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

        // ─── Inc4 S2 host-driven travel mirror (GeoVehiclePos 0xA5) ─────────
        // Shared per-surface seq for the geoscape LIVE host→all mirror surfaces (host authors Next, client
        // guards ShouldApply/Mark). Instance-scoped → a fresh session (new SyncEngine) resets it. Today it
        // carries only the vehicle-position mirror; other live geoscape surfaces can share it.
        private readonly SurfaceSeq _geoLiveSeq = new SurfaceSeq();
        private int _vehiclePollTick;   // host: frame counter throttling the vehicle-placement poll
        // Poll moving-vehicle placements every Nth Tick. The cadence lives in VehicleEmitScheduler (single source of
        // truth: GeoVehicleMirror's derived interp delay must track this rate), now 6 ticks / ~10 Hz @60fps (was 15
        // / ~4 Hz) for tighter perceived latency. The per-vehicle signature skip makes a parked-vehicle tick ~free;
        // only moving vehicles ship bytes, so faster polling costs nothing at rest.
        private const int VehiclePollTickInterval = VehicleEmitScheduler.EmitTickInterval;

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
            // Fresh session → drop any travel-mirror state carried in GeoVehicleMirror's static caches (host
            // signature + client interpolation buffers), so a new session never inherits a prior one's snapshots.
            State.GeoVehicleMirror.ResetForNewSession();
            State.GeoVehicleTravelMirror.ResetForNewSession();   // route-line metadata mirror (0xA6) host sig cache
            State.GeoVehicleExploreMirror.ResetForNewSession();  // exploration-progress mirror (0xA7) host sig cache
            SyncRegistration.RegisterAll();   // registers every action reader (legacy 0x60/0x61 relay)
            // Rail-unify: arm the SurfaceRouter geoscape fast-path so a geoscape envelope surface (0xA0+) routes
            // to this engine's appliers. Phase 1 retired the legacy 0x63/0x64 sends, so wallet (0xA0) + state
            // (0xA1) now ride this envelope rail ONLY (host emits them unconditionally; see BroadcastFullWallet/
            // FlushChannel/Tick).
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
            // Host-LOCAL vehicle order (travel/explore) just changed authoritative travel state → ship the mirror
            // now instead of waiting up to a full poll interval (route line + first placement feel instant).
            if (VehicleEmitScheduler.TriggersImmediateEmit(a.Category)) RequestImmediateVehicleEmit();
        }

        /// <summary>Host: collapse the vehicle-mirror poll latency to the NEXT Tick after an order that just changed
        /// a vehicle's authoritative travel state (StartTravel / StartExploringCurrentSite), so the 0xA5 placement +
        /// 0xA6 route-line meta ship at once instead of up to a full poll interval later. No-op off-host / freeze-OFF
        /// (nothing polls then). Idempotent; the existing Tick poll path does the actual read+broadcast next frame
        /// (not mid-apply — the transform hasn't moved yet at StartTravel time; the 0xA6 meta change is what ships).</summary>
        public void RequestImmediateVehicleEmit()
        {
            if (_engine == null || !_engine.IsHost || !ClientSimFreeze.Enabled) return;
            _vehiclePollTick = VehicleEmitScheduler.ArmImmediate(VehiclePollTickInterval);
        }

        // ─── Inbound: host ────────────────────────────────────────────────

        public void OnActionRequest(ulong senderPeerId, byte[] data)
        {
            if (!_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeActionRequest(data, out var id, out var nonce, out var payload)) return;

            // Host-side dedup: the reliable transport sends every packet twice, so the same request
            // arrives twice. Apply each (peerId, nonce) exactly once on the authority; drop the repeat.
            if (_seenRequests.IsDuplicate(senderPeerId, nonce)) return;

            // A BLOCKING host prompt is pending (mandatory ambush brief): natively the whole geoscape is modal —
            // NOTHING may happen until it resolves. The client's mirrored modal is view-locked too, but an intent
            // may already be in flight when the prompt raised (UI lock is raceable) → authoritative belt: reject
            // EVERY client intent while armed. Armed in ReportModalMirror.HostBroadcast (modal open), released in
            // BlockingModalReleasePatch (ModalResultCallback — mission start or any other resolve); normal relay
            // flow resumes after. The rejected client action stays suppressed locally (standard reject path).
            if (HostBlockingPromptGate.ShouldRejectIntent(_engine.IsHost, _engine.IsActiveSession))
            {
                Debug.Log("[Multiplayer] HOST reject ActionRequest id=" + id + " (blocking prompt pending, modalType="
                          + HostBlockingPromptGate.ArmedModalType + ")");
                _engine.SendToClient(senderPeerId, new NetworkMessage(PacketType.ActionReject,
                    SyncProtocol.EncodeActionReject(nonce, 2, "host blocking prompt (ambush) pending")));
                return;
            }

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
                catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnActionRequest answer resolve failed: " + ex.Message); }
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
                catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnActionRequest apply failed: " + ex.Message); }
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
            // Client-relayed vehicle order (travel/explore) just applied authoritatively → ship the mirror now
            // instead of waiting up to a full poll interval (tightens the click→visible-motion latency).
            if (VehicleEmitScheduler.TriggersImmediateEmit(action.Category)) RequestImmediateVehicleEmit();
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
                // TODO(multiplayer): non-channelled event outcomes (site reveal / mission spawn / faction-
                // diplomacy flag / direct research unlock) are NOT yet synced to the client — visible gap.
                Debug.Log("[Multiplayer] SyncEngine.OnActionApply: client suppressing host-only-apply action "
                    + "(id=" + id + "); non-channelled outcomes may be unsynced. TODO(multiplayer).");
                return;
            }
            try { using (SyncApplyScope.Enter()) action.Apply(GeoRuntime.Instance); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnActionApply failed: " + ex.Message); }
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
            Debug.Log("[Multiplayer] action rejected (" + code + "): " + reason);
            // v1: log only; UI feedback hook later.
        }

        // ─── Currency (mechanism A) ───────────────────────────────────────

        /// <summary>Host: WalletWatcher callback when the player wallet changes (coalesced in Tick).</summary>
        public void MarkWalletDirty()
        {
            // DIAG (wallet rail): log the clean→dirty transition only — ResourcesChanged can fire several
            // times inside one flush window; the Tick flush logs the amounts actually shipped. No behavior change.
            if (!_walletDirty)
                Debug.Log("[Multiplayer] Wallet marked dirty (ResourcesChanged echo) — coalesced flush next Tick");
            _walletDirty = true;
            // Host BAR repaint kick (cosmetic): the host's persistent top resource bar (UIModuleInfoBar) repaints
            // ONLY off the native View.FactionResourcesChanged event and lags while an event modal is open — so
            // after an event GRANT the host bar shows the stale pre-grant total even though its model already
            // granted (the client converges + already repaints via OnWalletSync). RefreshPersistentBars was
            // previously CLIENT-only; drive it on the HOST here too so the host bar repaints its OWN wallet change
            // without waiting for modal-close/the next native repaint. WalletWatcher subscribes this host-only, on
            // the Unity main thread (Wallet.ResourcesChanged), so the direct UI call is main-thread-safe. NO model
            // change (both sides already agree). Self-guarded (null/IsOpen-gated + try/catch INSIDE
            // RefreshPersistentBars) → harmless no-op when no geoscape view is shown. Mirrors the client path.
            GeoUiRefresh.RefreshPersistentBars(GeoRuntime.Instance);
        }

        public void OnWalletSync(byte[] data)
        {
            // DIAG (wallet rail): every silent drop below gets one distinguishable guard= line, and every
            // apply logs received amounts + local before→after — all rare (one inbound per host broadcast).
            // No behavior change.
            if (_engine.IsHost)
            {
                Debug.Log("[Multiplayer] Wallet sync dropped guard=is-host (authority never applies an echo)");
                return;   // host is the authority; never applies an echo
            }
            if (!SyncProtocol.TryDecodeWalletSync(data, out var ver, out var slots))
            {
                Debug.Log("[Multiplayer] Wallet sync dropped guard=decode-failed len=" + (data == null ? -1 : data.Length));
                return;
            }
            if (!_tracker.ShouldApplyWallet(ver))
            {
                Debug.Log("[Multiplayer] Wallet sync dropped guard=stale-version ver=" + ver);
                return;
            }
            _tracker.MarkWallet(ver);
            var before = WalletApplier.Snapshot(GeoRuntime.Instance);
            try { using (SyncApplyScope.Enter()) WalletApplier.Apply(GeoRuntime.Instance, slots); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnWalletSync failed: " + ex.Message); }
            if (before == null)
                Debug.Log("[Multiplayer] Wallet sync apply no-op guard=wallet-null ver=" + ver
                          + " recv=" + WalletSlotsString(slots) + " (client wallet not live yet; version already marked)");
            else
                Debug.Log("[Multiplayer] Wallet sync applied ver=" + ver + " recv=" + WalletSlotsString(slots)
                          + " localΔ=" + WalletDiffString(before, WalletApplier.Snapshot(GeoRuntime.Instance)));
            // The persistent top resource bar (UIModuleInfoBar) repaints only from native Wallet model
            // events, which the reflective WalletApplier.Apply write doesn't trip — so the synced money sat
            // stale until the client's next local action. Re-drive the native repaint now (no-op if no view).
            GeoUiRefresh.RefreshPersistentBars(GeoRuntime.Instance);
        }

        /// <summary>Host: push a full versioned wallet snapshot (geoscape became active / late joiner ready).</summary>
        public void BroadcastFullWallet()
        {
            // DIAG (wallet rail): guard= lines for the silent drops + one line per actual push (all rare:
            // watcher (re)bind seed + session ready re-broadcast). No behavior change.
            if (!_engine.IsHost)
            {
                Debug.Log("[Multiplayer] Wallet full-broadcast skipped guard=not-host");
                return;
            }
            var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
            if (slots == null)
            {
                Debug.Log("[Multiplayer] Wallet full-broadcast skipped guard=wallet-null (geoscape wallet not live yet)");
                return;
            }
            ulong ver = ++_walletVersion;
            // Rail-unify phase 1: the legacy 0x63 WalletSync send is RETIRED — the versioned full-wallet snapshot
            // now rides ONLY the unified 0x67 envelope rail under the GeoWallet (0xA0) surface. The inner bytes are
            // the IDENTICAL EncodeWalletSync(ver, slots) the legacy 0x63 carried; the client applier (OnWalletSync,
            // version-guarded) is unchanged, reached via HandleGeoscapeEnvelope. Sole rail, emitted unconditionally.
            _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                    SyncProtocol.EncodeWalletSync(ver, slots))));
            Debug.Log("[Multiplayer] Wallet full-broadcast ver=" + ver + " slots=" + WalletSlotsString(slots));
            // Baseline = what we just sent, so the Tick snapshot-diff poll won't re-fire this push.
            _lastWalletBroadcast = slots;
        }

        // ─── Wallet diag formatting (pure string helpers for the rail logs above/below) ───

        /// <summary>Human name for a vanilla ResourceType flag value (see <see cref="WalletApplier"/>).</summary>
        private static string WalletResName(int type)
        {
            switch (type)
            {
                case 1: return "Supplies";
                case 2: return "Materials";
                case 4: return "Tech";
                case 8: return "AICore1";
                case 0x10: return "AICore2";
                case 0x20: return "AICore3";
                case 0x40: return "Research";
                case 0x80: return "Production";
                case 0x100: return "Mutagen";
                case 0x200: return "LivingCrystals";
                case 0x400: return "Orichalcum";
                case 0x800: return "ProteanMutane";
                default: return "Res" + type;
            }
        }

        /// <summary>All slots as "[Supplies=120 Materials=45 …]"; "(null)" when no snapshot.</summary>
        private static string WalletSlotsString(List<(int type, float value)> slots)
        {
            if (slots == null) return "(null)";
            var sb = new StringBuilder("[");
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(WalletResName(slots[i].type)).Append('=').Append(slots[i].value.ToString("0.##"));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>Only the slots that moved, as "Supplies=100→120"; "(none)" when equal; explicit
        /// seed marker when there is no baseline yet. Eps mirrors <see cref="WalletSnapshotDiff"/>.</summary>
        private static string WalletDiffString(List<(int type, float value)> from, List<(int type, float value)> to)
        {
            if (to == null) return "(to=null)";
            if (from == null) return "seed(no-baseline) now=" + WalletSlotsString(to);
            var old = new Dictionary<int, float>(from.Count);
            foreach (var (t, v) in from) old[t] = v;
            var sb = new StringBuilder();
            foreach (var (t, v) in to)
            {
                if (old.TryGetValue(t, out float ov) && Math.Abs(v - ov) <= 0.0001f) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(WalletResName(t)).Append('=')
                  .Append(old.TryGetValue(t, out float o) ? o.ToString("0.##") : "?")
                  .Append('→').Append(v.ToString("0.##"));
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
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
            // Rail-unify phase 1: the legacy 0x64 StateSync send is RETIRED — the per-channel state echo now rides
            // ONLY the unified 0x67 envelope rail under the GeoState (0xA1) surface. The inner bytes are the
            // IDENTICAL EncodeStateSync(id, v, payload) the legacy 0x64 carried, so the client applier (OnStateSync,
            // per-channel version-guarded) is unchanged, reached via HandleGeoscapeEnvelope. Sole rail, unconditional.
            _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                SyncProtocol.EncodeEnvelope(SurfaceIds.GeoState, SyncKind.StateSnapshot, stateBytes)));
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
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnStateSync apply failed: " + ex.Message); return; }
            // Best-effort: rebuild the open UI for this channel's screen. Channels 1/2 map to a single
            // screen (targeted Refresh). The unlock (3) + diplomacy (4) channels span multiple modules (an
            // unlock shows in BOTH the manufacturing list AND the base-layout facility picker; diplomacy has
            // no commonly-open module), so drive the full needs-kick fan-out for ids ≥ 3 — each Refresh
            // no-ops if that module is closed, so a redundant kick is harmless.
            var screen = _channels.ScreenFor(channelId);
            if (screen.HasValue) GeoUiRefresh.Refresh(GeoRuntime.Instance, screen.Value);
            else if (channelId >= 3) GeoUiRefresh.RefreshNeedsKick(GeoRuntime.Instance);
            // The persistent bottom section bar's Research progress segment (UIModuleGeoSectionBar) +
            // the top resource bar repaint only from native model events / the hourly progress coroutine,
            // which the reflective channel apply doesn't trip — so research progress + any resource refund
            // on a synced research/state change stayed stale until the next local action. Re-drive the
            // native persistent-bar repaints now (idempotent; each is null-guarded + no-op if no view).
            GeoUiRefresh.RefreshPersistentBars(GeoRuntime.Instance);
        }

        /// <summary>Host: drop all channel change-event subscriptions (session end). Idempotent.</summary>
        public void DetachAllChannels()
        {
            foreach (var ch in _channels.All) ch.DetachHost();
            // Teardown belt: never carry a deferred report across a session boundary (its modalData is dead).
            lock (_deferredReports) _deferredReports.Clear();
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
            if (!SyncProtocol.TryDecodeEventRaised(data, out var occId, out var eventId, out var siteId, out var vehicleId, out var hasIdentity, out var identity, out var singleChoice, out var oneWindow, out var wireTitle, out var wireNarrative)) return;
            if (string.IsNullOrEmpty(eventId)) return;
            try
            {
                // The gate decides whether a single-choice event MIRRORS the host's window-1 prompt (ON) or keeps
                // the legacy unconditional jump to the result page (OFF): off-gate we pass singleChoice=false so
                // EventCorrelator.Raised takes its byte-for-byte legacy ShowResultPage branch.
                bool mirrorSingleChoice = EventMirrorFixGate.Enabled && singleChoice;
                // 1-WINDOW single-choice (host's IsSingleChoiceEncounter()==true: empty outcome text → host shows
                // reward+narrative in ONE combined window): resolve STRAIGHT to the result page (skip the phantom
                // reward-less prompt) so the client matches the host's single window. Gate-coupled (off-gate stays
                // legacy). A 2-window single-choice-WITH-outcome (oneWindow=false) keeps the prompt-mirror+advance.
                bool oneWindowMirror = EventMirrorFixGate.Enabled && oneWindow;
                var decision = _eventCorrelator.Raised(occId, eventId, mirrorSingleChoice, oneWindowMirror);
                Debug.Log("[Multiplayer] CLIENT OnEventRaised occId=" + occId + " eventId=" + eventId +
                          " siteId=" + siteId + " vehicleId=" + vehicleId + " singleChoice=" + singleChoice +
                          " oneWindow=" + oneWindow + " mirror=" + mirrorSingleChoice + " oneWindowMirror=" + oneWindowMirror +
                          " decision=" + decision.Kind +
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
                            Debug.Log("[Multiplayer] CLIENT singleChoice prompt-mirror occId=" + occId + " eventId=" + eventId +
                                      " choiceIndex=" + decision.ChoiceIndex + " → showing PROMPT, awaiting host advance (reward stashed)");
                        ShowRaisedDialog(rt, occId, eventId, siteId, vehicleId, hasIdentity, identity, wireTitle, wireNarrative);
                        break;
                    }
                    case State.EventCorrelator.ActionKind.Enqueue:
                        // Single-slot client display is busy showing another event → DEFER this raise (the correlator
                        // queued it in occId order). Stash its build payload; it is released + shown when the current
                        // dialog is dismissed (DrainQueuedRaises), so bursts/transport-reorders never overwrite the
                        // shown dialog or display out of host emission order.
                        _queuedRaises[occId] = new QueuedRaise(eventId, siteId, vehicleId, hasIdentity, identity, wireTitle, wireNarrative);
                        Debug.Log("[Multiplayer] CLIENT OnEventRaised occId=" + occId + " eventId=" + eventId +
                                  " → ENQUEUED behind shown dialog (queued=" + _eventCorrelator.QueuedCount + ")");
                        break;
                    case State.EventCorrelator.ActionKind.Ignore:
                        // Transport double-send of an already-shown/queued/resolved raise → idempotent no-op (no
                        // duplicate dialog). Drop any stale stashed reward for this occurrence so it can't leak.
                        DropBufferedReward(occId);
                        Debug.Log("[Multiplayer] CLIENT OnEventRaised occId=" + occId + " eventId=" + eventId + " → IGNORED (duplicate raise)");
                        break;
                    case State.EventCorrelator.ActionKind.ShowResultPage:
                    {
                        // Out-of-order dismiss already buffered for this occurrence → jump straight to its
                        // result page. The reward lines + wire texts were carried on the dismiss and stashed at
                        // buffer time; THIS raise's narrative backfills a text-less dismiss (VoidOmen: the
                        // result body IS the raise narrative).
                        var buffered = TakeBufferedDismiss(occId);
                        string narrative = !string.IsNullOrEmpty(buffered.WireNarrative) ? buffered.WireNarrative : wireNarrative;
                        ResolveToResultPage(rt, occId, eventId, decision.ChoiceIndex, buffered.Reward, siteId, buffered.WireOutcome, narrative, wireTitle);
                        break;
                    }
                    case State.EventCorrelator.ActionKind.DropNoop:
                        // A close-only dismiss beat its raise → nothing to display; drop any stashed reward.
                        DropBufferedReward(occId);
                        break;
                }
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnEventRaised failed: " + ex.Message); }
        }

        // ─── Out-of-order dismiss stash (keyed by occurrence id) ─────────────────────────────
        // When a dismiss arrives BEFORE its raise its reward snapshot AND host-resolved wire texts must be held
        // until the raise builds the result page (so the ReferenceEquals-armed render still lands and a
        // runtime-narrative def still gets its host text). Bounded by the correlator's own pending buffer cap
        // (we only stash for buffered dismisses), pruned on resolve/drop.
        private readonly struct BufferedDismiss
        {
            public readonly RewardDisplaySnapshot Reward;
            public readonly string WireOutcome;
            public readonly string WireNarrative;
            public BufferedDismiss(RewardDisplaySnapshot reward, string wireOutcome, string wireNarrative)
            { Reward = reward; WireOutcome = wireOutcome; WireNarrative = wireNarrative; }
        }
        private readonly Dictionary<ushort, BufferedDismiss> _bufferedRewards = new Dictionary<ushort, BufferedDismiss>();

        private void StashBufferedReward(ushort occId, RewardDisplaySnapshot reward, string wireOutcome = null, string wireNarrative = null)
        {
            bool hasReward = reward != null && !reward.IsEmpty;
            bool hasTexts = !string.IsNullOrEmpty(wireOutcome) || !string.IsNullOrEmpty(wireNarrative);
            if (!hasReward && !hasTexts) { _bufferedRewards.Remove(occId); return; }
            _bufferedRewards[occId] = new BufferedDismiss(hasReward ? reward : null, wireOutcome, wireNarrative);
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
        private BufferedDismiss TakeBufferedDismiss(ushort occId)
        {
            if (_bufferedRewards.TryGetValue(occId, out var d)) { _bufferedRewards.Remove(occId); return d; }
            return default(BufferedDismiss);
        }
        private void DropBufferedReward(ushort occId) => _bufferedRewards.Remove(occId);

        // ─── Deferred-raise stash (client FIFO mirror, keyed by occurrence id) ──────────────────────
        // The pure EventCorrelator decides the ORDER (occId-ascending) and dedup; this holds the Unity/wire build
        // payload for each DEFERRED raise so the released event can be rebuilt + shown exactly as the in-order path.
        private readonly struct QueuedRaise
        {
            public readonly string EventId;
            public readonly int SiteId;
            public readonly int VehicleId;
            public readonly bool HasIdentity;
            public readonly GeoSiteState Identity;
            public readonly string WireTitle;
            public readonly string WireNarrative;
            public QueuedRaise(string eventId, int siteId, int vehicleId, bool hasIdentity, GeoSiteState identity, string wireTitle, string wireNarrative)
            {
                EventId = eventId; SiteId = siteId; VehicleId = vehicleId; HasIdentity = hasIdentity; Identity = identity;
                WireTitle = wireTitle; WireNarrative = wireNarrative;
            }
        }
        private readonly Dictionary<ushort, QueuedRaise> _queuedRaises = new Dictionary<ushort, QueuedRaise>();

        /// <summary>
        /// Save-load / co-op save-transfer boundary reset for the CLIENT event-mirror — the sibling of the host-side
        /// <see cref="Arbiter"/>.Reset() (both are driven from <c>SaveTransferCoordinator.PrepareEntryFromBlobCrt</c>).
        /// The SyncEngine — hence its <see cref="State.EventCorrelator"/> and the two Unity-side stashes it drives —
        /// is NOT recreated on a mid-session reload (only on full session teardown). Occurrence ids are
        /// process-lifetime MONOTONIC (<c>EventOccurrenceIds._counter</c> never resets in production; ResetForTests
        /// has no production callers), so ids are NOT reused across a reload — the real hazard is STALE IN-FLIGHT
        /// display state: a busy single slot (<c>_shownSlot</c>) and deferred-raise queue whose dismisses/advances
        /// will NEVER arrive after the reload (the pre-reload host occurrences are gone). Without this reset every
        /// post-reload raise would defer behind the wedged slot forever and the client stops showing ALL geoscape
        /// events. Clears the pure correlator, the two build/reward stashes it drives, and the EventDisplay
        /// open-occurrence record. No-op on the host (it never populates the client mirror), exactly as
        /// Arbiter.Reset() is a no-op on a client.
        /// </summary>
        public void ResetEventMirror()
        {
            _eventCorrelator.Reset();
            _queuedRaises.Clear();
            _bufferedRewards.Clear();
            State.EventDisplay.ResetOpenOccurrence();
            // Boundary belt: a save-transfer/reload must never inherit a stale blocking-prompt arm (the modal it
            // guarded is gone with the old geoscape). Re-arms naturally if the restored host reopens the prompt.
            HostBlockingPromptGate.Reset();
            // Same belt for pending research-nav overrides (their mirrored popups died with the old geoscape).
            State.ResearchNavMirror.Reset();
        }

        // Build + show a host-raised geoscape-event dialog (shared by the in-order ShowDialog path and the released
        // deferred path). Spawns an inert mirror site first when the in-play site is absent on this sim-frozen client
        // so BuildEvent renders the correct backdrop/subtitle (not StartingBase).
        private void ShowRaisedDialog(GeoRuntime rt, ushort occId, string eventId, int siteId, int vehicleId, bool hasIdentity, GeoSiteState identity, string wireTitle = null, string wireNarrative = null)
        {
            if (hasIdentity && EventReflection.ShouldSpawnMirror(
                    hasIdentity, State.GeoSiteReflection.ResolveSiteById(rt, siteId) != null))
                State.GeoSiteReflection.SpawnMirrorSite(rt, identity);
            var geoEvent = EventReflection.BuildEvent(rt, eventId, siteId, vehicleId,
                hasIdentity ? (GeoSiteState?)identity : null, wireTitle, wireNarrative);
            if (geoEvent != null) State.EventDisplay.Show(rt, geoEvent, occId, eventId);
        }

        // After a dismiss frees the single client slot, release the next deferred raise (lowest occId = earliest host
        // emission) and show it — one at a time, so a burst is mirrored in host order without overwriting a dialog.
        private void DrainQueuedRaises(GeoRuntime rt)
        {
            // Release deferred raises while the single client slot is free. LOOP: a TERMINAL resolution (a
            // buffered-dismiss single-choice → ShowResultPage / DropNoop) does NOT re-occupy the slot, so the next
            // deferred raise can surface in the SAME drain; a plain / single-choice-prompt ShowDialog DOES occupy it,
            // so TryDequeueNext returns false the next iteration and the loop stops. Released in occId (host) order.
            while (_eventCorrelator.TryDequeueNext(out var next))
            {
                ushort occId = next.OccurrenceId;
                if (!_queuedRaises.TryGetValue(occId, out var q))
                {
                    // DEFENSIVE (should never happen: the stash is written at Enqueue and dropped only on
                    // resolve/reset): a released raise with NO build stash cannot be shown — a default
                    // QueuedRaise (null eventId) would occupy the correlator slot with a dialog that never
                    // renders and whose dismiss the host never re-sends → slot wedged, all later dialogs
                    // starved. Log + skip; for a slot-occupying ShowDialog also abort it in the correlator
                    // (frees the slot + terminal dedup) so the drain can continue.
                    Debug.LogError("[Multiplayer] CLIENT released queued event occId=" + occId + " decision=" + next.Kind +
                                   " but its build stash is MISSING → skipped (slot freed)");
                    if (next.Kind == State.EventCorrelator.ActionKind.ShowDialog)
                        _eventCorrelator.AbortShow(occId);
                    DropBufferedReward(occId);
                    continue;
                }
                _queuedRaises.Remove(occId);
                Debug.Log("[Multiplayer] CLIENT releasing queued event occId=" + occId + " eventId=" + q.EventId +
                          " decision=" + next.Kind + " (remaining queued=" + _eventCorrelator.QueuedCount + ")");
                switch (next.Kind)
                {
                    case State.EventCorrelator.ActionKind.ShowDialog:
                        // Plain in-order OR single-choice prompt mirror (ChoiceIndex>=0 → the reward stays stashed for
                        // the host's later advance). Build + show; occupies the slot → the loop ends next iteration.
                        ShowRaisedDialog(rt, occId, q.EventId, q.SiteId, q.VehicleId, q.HasIdentity, q.Identity, q.WireTitle, q.WireNarrative);
                        break;
                    case State.EventCorrelator.ActionKind.ShowResultPage:
                    {
                        // Buffered-dismiss single-choice released straight to its result page (reusing the reward +
                        // wire texts stashed at the earlier out-of-order dismiss; the deferred raise's narrative
                        // backfills a text-less dismiss). Terminal → slot stays free, drain continues.
                        var buffered = TakeBufferedDismiss(occId);
                        string narrative = !string.IsNullOrEmpty(buffered.WireNarrative) ? buffered.WireNarrative : q.WireNarrative;
                        ResolveToResultPage(rt, occId, q.EventId, next.ChoiceIndex, buffered.Reward, q.SiteId, buffered.WireOutcome, narrative, q.WireTitle);
                        break;
                    }
                    case State.EventCorrelator.ActionKind.DropNoop:
                        // Close-only buffered dismiss released → nothing to show; drop any stashed reward.
                        DropBufferedReward(occId);
                        break;
                }
            }
        }

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
            if (!SyncProtocol.TryDecodeEventDismiss(data, out var occId, out var eventId, out var choiceIndex, out var rewardBlob, out var siteId, out var wireOutcome, out var wireNarrative)) return;
            try
            {
                var rt = GeoRuntime.Instance;
                // Decode the reward delta-line snapshot (empty blob → empty snapshot → no-op render). A NON-empty
                // blob that fails to decode (null) is a corrupt/version-mismatched reward — log it (the codec is
                // pure/Unity-free, so this boundary is where the malformed-blob visibility log belongs).
                var reward = RewardDisplaySnapshot.Decode(rewardBlob);
                if (reward == null && rewardBlob != null && rewardBlob.Length > 0)
                    Debug.LogError("[Multiplayer] reward decode failed (malformed blob, " + rewardBlob.Length + " bytes) — result card shown without reward lines");

                var decision = _eventCorrelator.Dismissed(occId, eventId, choiceIndex);
                Debug.Log("[Multiplayer] CLIENT OnEventDismiss occId=" + occId + " eventId=" + eventId +
                          " choiceIndex=" + choiceIndex + " rewardBytes=" + (rewardBlob?.Length ?? 0) +
                          " rewardEmpty=" + (reward == null || reward.IsEmpty) + " decision=" + decision.Kind +
                          " open=" + _eventCorrelator.OpenCount + " pending=" + _eventCorrelator.PendingCount);
                switch (decision.Kind)
                {
                    case State.EventCorrelator.ActionKind.ShowResultInPlace:
                        _queuedRaises.Remove(occId);   // if this dismiss resolved a still-deferred raise, drop its stash
                        ResolveToResultPage(rt, occId, eventId, choiceIndex, reward, siteId, wireOutcome, wireNarrative);
                        break;
                    case State.EventCorrelator.ActionKind.CloseDialog:
                        _queuedRaises.Remove(occId);   // ditto for a close-only resolution of a deferred raise
                        State.EventDisplay.Dismiss(rt, occId, eventId);   // close-only
                        break;
                    case State.EventCorrelator.ActionKind.BufferDismiss:
                        // Raise hasn't arrived yet → hold the reward + wire texts until OnEventRaised resolves
                        // this occurrence.
                        StashBufferedReward(occId, reward, wireOutcome, wireNarrative);
                        break;
                    case State.EventCorrelator.ActionKind.Ignore:
                        // Transport double-send of an already-resolved dismiss → idempotent no-op.
                        Debug.Log("[Multiplayer] CLIENT OnEventDismiss occId=" + occId + " eventId=" + eventId + " → IGNORED (duplicate dismiss)");
                        break;
                }
                // The shown dialog (if any) just closed → release the next deferred raise in occId order.
                DrainQueuedRaises(rt);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnEventDismiss failed: " + ex.Message); }
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
                Debug.Log("[Multiplayer] CLIENT OnEventAdvanceResult occId=" + occId + " eventId=" + eventId +
                          " choiceIndex=" + choiceIndex + " siteId=" + siteId + " decision=" + decision.Kind +
                          " promptMirror=" + _eventCorrelator.PromptMirrorCount +
                          " pendingAdvance=" + _eventCorrelator.PendingAdvanceCount);
                // Mirroring the prompt → advance to the result page (reward + wire texts = the ones stashed at
                // the earlier out-of-order dismiss). Otherwise the advance was BUFFERED (it beat the raise) →
                // no-op now; the upcoming raise resolves it straight to the result page.
                if (decision.Kind == State.EventCorrelator.ActionKind.ShowResultPage)
                {
                    var buffered = TakeBufferedDismiss(occId);
                    ResolveToResultPage(rt, occId, eventId, choiceIndex, buffered.Reward, siteId, buffered.WireOutcome, buffered.WireNarrative);
                }
                else if (decision.Kind == State.EventCorrelator.ActionKind.Ignore)
                {
                    // Terminal-occId dedup: this occurrence was already resolved-and-closed on this client (its
                    // result page shown via a prior advance, or an in-place/buffered dismiss). A duplicate/late
                    // EventAdvanceResult (transport double-send / raced host click) must never re-open the
                    // window. The FIRST advance for a live prompt mirror is never deduped (ShowResultPage above).
                    Debug.Log("[Multiplayer] CLIENT OnEventAdvanceResult occId=" + occId + " eventId=" + eventId +
                              " → IGNORED (duplicate/late advance for an already-resolved occurrence)");
                }
                // An advance that resolved a prompt mirror just FREED the single slot (EventCorrelator.Advanced
                // cleared _shownSlot) → release the next deferred raise in occId order, exactly as a dismiss does.
                // (A buffered/no-op advance leaves the slot busy → TryDequeueNext is a no-op — harmless.)
                DrainQueuedRaises(rt);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnEventAdvanceResult failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: this player OK'd a SINGLE-CHOICE prompt mirror — ask the host to advance ITS prompt too
        /// (<c>PacketType.EventAdvanceRequest</c> 0x6B, EventDismiss codec, occId+eventId only). The event
        /// auto-completed on the host at trigger, so the AnswerEventAction relay cannot advance the host UI
        /// (TryHostNativeResolve no-ops on IsCompleted); this dedicated signal drives the host's native click
        /// path instead. The client's own modal already closed locally (unchanged localClose); the result page
        /// arrives via the host's EventAdvanceResult broadcast as before. Sent only when
        /// <see cref="State.SingleChoiceAdvanceGate.ShouldRelayClientAdvance"/> said so (caller-side). No-op on host.
        /// </summary>
        public void SendEventAdvanceRequest(ushort occurrenceId, string eventId)
        {
            if (_engine.IsHost) return;
            _engine.SendToHost(new NetworkMessage(PacketType.EventAdvanceRequest,
                SyncProtocol.EncodeEventDismiss(occurrenceId, eventId)));
        }

        /// <summary>
        /// Host: a client OK'd its single-choice prompt mirror — drive OUR open native prompt to its result page
        /// exactly as a local host click would (<see cref="EventReflection.TryHostNativeAdvanceSingleChoice"/>:
        /// native OnChoiceSelected → SetClosingEncounter → SingleChoiceAdvancePatch broadcasts EventAdvanceResult
        /// to everyone). First-wins idempotent: the advanced-occurrence mark (set by the host's own click OR an
        /// earlier driven advance) plus the modal/occurrence/completed/single-choice guards make a raced host
        /// click, a duplicate transport delivery, or a stale/foreign occId a logged no-op — the requesting
        /// client's modal already closed locally either way. Never completes/re-completes any event and never
        /// touches the multi-choice AnswerEventAction path.
        /// </summary>
        public void OnEventAdvanceRequest(byte[] data)
        {
            if (!_engine.IsHost) return;
            if (!SyncProtocol.TryDecodeEventDismiss(data, out var occId, out var eventId, out _)) return;
            try
            {
                bool drove = EventReflection.TryHostNativeAdvanceSingleChoice(GeoRuntime.Instance, occId, eventId);
                Debug.Log("[Multiplayer] HOST OnEventAdvanceRequest occId=" + occId + " eventId=" + eventId +
                          " → " + (drove ? "drove native prompt→result advance" : "no-op (not showing / already advanced)"));
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnEventAdvanceRequest failed: " + ex.Message); }
        }

        /// <summary>
        /// Client: rebuild the chosen choice's RESULT/OUTCOME page and replace the (possibly already-resolved)
        /// dialog with it, arming the reward render keyed to THIS synthetic event instance right before the show
        /// so the ReferenceEquals-correlated RewardRenderPatch lands on the correct page exactly once. Falls back
        /// to a plain close when the page can't be rebuilt. Shared by the in-order (ShowResultInPlace) and the
        /// buffered-then-raised (ShowResultPage) paths.
        /// </summary>
        private void ResolveToResultPage(GeoRuntime rt, ushort occId, string eventId, int choiceIndex, RewardDisplaySnapshot reward, int siteId = -1, string wireOutcome = null, string wireNarrative = null, string wireTitle = null)
        {
            var resultEvent = EventReflection.BuildResultEvent(rt, eventId, choiceIndex, siteId, wireOutcome, wireNarrative, wireTitle);
            Debug.Log("[Multiplayer] CLIENT ResolveToResultPage occId=" + occId + " eventId=" + eventId +
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

        /// <summary>Host: broadcast a show event-dialog packet to all peers, carrying the occurrence id, an
        /// optional absent-site identity block (so a client without the site degrades gracefully, not StartingBase)
        /// and the host-resolved wire texts (title + raise narrative) so a runtime-narrative def (TFTV VoidOmen,
        /// empty loc keys) still renders on a client whose local def resolution yields a BLANK window.</summary>
        public void BroadcastEventRaised(ushort occurrenceId, string eventId, int siteId, int vehicleId, GeoSiteState? identity = null, bool singleChoice = false, bool oneWindow = false, string wireTitle = null, string wireNarrative = null)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventRaised,
                SyncProtocol.EncodeEventRaised(occurrenceId, eventId, siteId, vehicleId, identity, singleChoice, oneWindow, wireTitle, wireNarrative)));
        }

        /// <summary>
        /// Host: tell clients the answer was applied. <paramref name="occurrenceId"/> matches the raise so clients
        /// correlate even when two occurrences share a def-id. <paramref name="choiceIndex"/> is the picked
        /// choice's index within EventData.Choices (&gt;= 0 → clients rebuild + show its RESULT/OUTCOME page
        /// natively; -1 → close-only, for a pure-INFO host-OK / decline). The reward STATE itself rides the
        /// wallet/research/items/diplomacy channels — this carries only the UI index + the display blob.
        /// <paramref name="siteId"/> is the event's GeoSite.SiteId (-1 = none) so the client result card resolves
        /// the REAL event site instead of falling back to StartingBase. <paramref name="wireOutcome"/> /
        /// <paramref name="wireNarrative"/> are the host-resolved result texts (SelectedChoice outcome +
        /// Description.Last narrative) — non-empty wire text beats the client's local-def resolution, which is
        /// EMPTY for runtime-narrative defs (TFTV VoidOmen).
        /// </summary>
        public void BroadcastEventDismiss(ushort occurrenceId, string eventId, int choiceIndex = -1, byte[] rewardBlob = null, int siteId = -1, string wireOutcome = null, string wireNarrative = null)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.EventDismiss,
                SyncProtocol.EncodeEventDismiss(occurrenceId, eventId, choiceIndex, rewardBlob, siteId, wireOutcome, wireNarrative)));
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

        // ─── deferred report-modal broadcast (read-timing fix) ────────────────────────────────────────
        // The Research report's payload read (ResearchElement.UnlocksResearches → the "new research
        // available" nav flag) is only correct AFTER the completion cascade settles — but the OpenModal
        // Postfix that broadcasts the report runs INSIDE that cascade (see
        // ReportModalClassifier.ShouldDeferHostBroadcast). The opener queues the raw (modalType, modalData,
        // priority) here; the next host Tick — by which time the same-call-stack cascade has finished —
        // builds the payload (fresh reflection read) and broadcasts it. The client is unaffected by the
        // one-tick delay: its mirrored popup is opened BY this payload, so the nav flag always arrives with it.
        private readonly List<(int modalType, object modalData, int priority)> _deferredReports
            = new List<(int, object, int)>();

        /// <summary>HOST: queue a report whose payload must be read after the current sim dispatch settles.</summary>
        public void QueueDeferredReportModal(int modalType, object modalData, int priority)
        {
            lock (_deferredReports) _deferredReports.Add((modalType, modalData, priority));
        }

        /// <summary>Host Tick: build + broadcast every deferred report with a POST-cascade payload read.</summary>
        private void FlushDeferredReportModals()
        {
            (int modalType, object modalData, int priority)[] pending;
            lock (_deferredReports)
            {
                if (_deferredReports.Count == 0) return;
                pending = _deferredReports.ToArray();
                _deferredReports.Clear();
            }
            foreach (var (modalType, modalData, priority) in pending)
            {
                try
                {
                    if (!State.ReportModalReflection.TryBuildPayload(modalType, modalData, priority, out var payload)) continue;
                    Debug.Log("[Multiplayer] HOST BroadcastReportModal (deferred, post-cascade read) modalType=" + modalType +
                              " variant=" + payload.Variant + " defId=" + payload.DefId + " shareLevel=" + payload.ShareLevel +
                              " priority=" + payload.Priority);
                    BroadcastReportModal(payload);
                }
                catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.FlushDeferredReportModals failed: " + ex.Message); }
            }
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
                        // Mirror the HOST's native "new research available" line: the flag rides ShareLevel
                        // (ResearchNavMirror tri-state); ResearchNavGroupMirrorPatch consumes it at bind time.
                        // Unknown/legacy → not armed → the client's bind stays native (fail-open).
                        State.ResearchNavMirror.Arm(p.DefId, p.ShareLevel);
                        break;
                    case State.ReportModalVariant.Diplomacy:
                        modalData = State.ReportModalReflection.BuildDiplomacyData(rt, p.DefId, p.ExtraIds, p.ShareLevel);
                        break;
                    case State.ReportModalVariant.AmbushBrief:
                        // Rebuild a DISPLAY-ONLY GeoAmbushMission(site, missionDef) — never attached to the site
                        // (no SetActiveMission / no producers on the frozen client sim); it only feeds the native
                        // modal's data bind. The window is view-locked client-side (BlockingModalClientLockPatches)
                        // and closes solely on the host's resolve (ReportModalHide) or the tactical transition.
                        modalData = State.ReportModalReflection.BuildAmbushMission(rt, p.SiteId, p.DefId);
                        if (modalData == null) return;   // unresolved site/def → don't show an empty brief
                        break;
                    case State.ReportModalVariant.SiteMissionBrief:
                        // Same display-only rebuild contract as AmbushBrief, concrete class by modalType
                        // (scavenge / ancient-site deploy briefs). View-locked; closes on the host's resolve
                        // (Confirm → tactical co-op deploy flow; Cancel → ReportModalHide) — never locally.
                        modalData = State.ReportModalReflection.BuildSiteMissionBrief(rt, p.ModalType, p.SiteId, p.DefId);
                        if (modalData == null) return;   // unresolved site/def → don't show an empty brief
                        break;
                    default:
                        return;   // Phase-B (MissionOutcome) / unknown variant → ignore this phase
                }
                bool persistent = State.ReportModalClassifier.IsPersistent(p.Variant);
                Debug.Log("[Multiplayer] CLIENT OnReportModalShow modalType=" + p.ModalType + " variant=" + p.Variant +
                          " siteId=" + p.SiteId + " defId=" + p.DefId + " extras=" + (p.ExtraIds?.Count ?? 0) +
                          " shareLevel=" + p.ShareLevel + " priority=" + p.Priority + " persistent=" + persistent +
                          " hasData=" + (modalData != null));
                using (SyncApplyScope.Enter())
                    State.GeoModalDisplay.Show(rt, p.ModalType, modalData, p.Priority, persistent);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnReportModalShow failed: " + ex.Message); }
        }

        /// <summary>
        /// Host: the blocking report modal (ambush brief) just RESOLVED on the authority (ModalResultCallback —
        /// Confirm→LaunchMission or any other result). Tell every client to close its mirrored view-locked copy
        /// so normal flow resumes (on Confirm the tactical co-op deploy flow takes over as today). No-op off-host.
        /// </summary>
        public void BroadcastReportModalHide(byte modalType)
        {
            if (!_engine.IsHost) return;
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ReportModalHide,
                SyncProtocol.EncodeReportModalHide(modalType)));
        }

        /// <summary>
        /// Client: the host resolved its blocking modal → close the mirrored copy IF it is the currently-shown
        /// modal of that type (type-matched inside <see cref="State.GeoModalDisplay.CloseBlocking"/> so a stray
        /// hide never pops an unrelated window). Idempotent: nothing open → no-op. Runs under
        /// <see cref="SyncApplyScope"/> so the client-side view-lock (which passes engine-driven closes) stays out
        /// of the way and no patched opener re-broadcasts.
        /// </summary>
        public void OnReportModalHide(byte[] data)
        {
            if (_engine.IsHost) return;   // authority closed natively; it never applies its own broadcast
            if (!SyncProtocol.TryDecodeReportModalHide(data, out var modalType)) return;
            try
            {
                using (SyncApplyScope.Enter())
                    State.GeoModalDisplay.CloseBlocking(GeoRuntime.Instance, modalType);
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] SyncEngine.OnReportModalHide failed: " + ex.Message); }
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
            if (!_engine.IsHost)
            {
                // CLIENT per-frame: drive the geoscape vehicle travel-mirror INTERPOLATION (Inc4 S2 smoothing).
                // Reuses this existing per-frame hook (NetworkEngine.Update → Sync.Tick) — no new MonoBehaviour.
                // Self-gated on ClientSimFreeze inside; flag-OFF / not-frozen = no-op (clears any stale buffers).
                State.GeoVehicleMirror.ClientInterpolateTick(_engine);
                return;
            }

            // Host: broadcast any report deferred for a post-cascade payload read (research nav flag) —
            // by this tick the completion dispatch that queued it has fully settled.
            FlushDeferredReportModals();

            // Host: bind the wallet watcher once the geoscape (and its wallet) is live. Attach is
            // idempotent — it early-returns until the wallet exists, then once it is bound. Mirrors the
            // deferred world-load: the wallet only appears frames after EnterLevel→FinishLevel.
            WalletWatcher.Attach(_engine);

            // Host: ABSOLUTE wallet snapshot-diff POLL — the binding-independent currency convergence backstop.
            // The event path (WalletWatcher → Wallet.ResourcesChanged → MarkWalletDirty) catches the common case
            // instantly, but a host wallet change that misses ResourcesChanged or fires on a stale-bound instance
            // (the binding has bitten us before) would leave _walletDirty unset → the client stays stale. So re-
            // derive dirtiness from absolute truth: if the live snapshot drifted from the last one we broadcast,
            // arm _walletDirty and let the SINGLE existing flush path below send it. Throttled (every
            // WalletPollTickInterval ticks) so the 11 reflection reads don't run every frame; the poll only flags
            // dirty — it never broadcasts directly (the dirty-flush + BroadcastFullWallet stay the only senders).
            if (++_walletPollTick >= WalletPollTickInterval)
            {
                _walletPollTick = 0;
                var polled = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (polled != null && WalletSnapshotDiff.Changed(_lastWalletBroadcast, polled))
                {
                    _walletDirty = true;
                    // DIAG (wallet rail): fires ONLY on drift, i.e. the ResourcesChanged event path missed
                    // this change (or no baseline was ever broadcast). The dirty-flush below logs the
                    // resulting broadcast. No behavior change, no per-tick spam.
                    Debug.Log("[Multiplayer] Wallet poll drift detected (event path missed it) Δ="
                              + WalletDiffString(_lastWalletBroadcast, polled) + " — arming dirty-flush");
                }
            }

            // Host: bind every state channel's change-event the same way (idempotent per channel).
            foreach (var ch in _channels.All) ch.AttachHost(this);

            if (_walletDirty)
            {
                _walletDirty = false;
                var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (slots != null)
                {
                    ulong ver = ++_walletVersion;
                    // Rail-unify phase 1: legacy 0x63 WalletSync send RETIRED — the coalesced dirty-flush snapshot
                    // now rides ONLY the unified 0x67 envelope rail (GeoWallet 0xA0 surface), same inner
                    // EncodeWalletSync bytes, same version-guarded OnWalletSync applier. Sole rail, unconditional.
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                        SyncProtocol.EncodeEnvelope(SurfaceIds.GeoWallet, SyncKind.StateSnapshot,
                            SyncProtocol.EncodeWalletSync(ver, slots))));
                    // DIAG (wallet rail): one line per coalesced flush (event path or poll backstop).
                    Debug.Log("[Multiplayer] Wallet dirty-flush broadcast ver=" + ver
                              + " slots=" + WalletSlotsString(slots)
                              + " Δvs-last=" + WalletDiffString(_lastWalletBroadcast, slots));
                    // Baseline = exactly what we just sent, so the poll won't immediately re-fire it (covers both
                    // the event-path and poll-path dirty, regardless of whether the poll ran this tick).
                    _lastWalletBroadcast = slots;
                }
                else
                {
                    // DIAG (wallet rail): the dirty flag is consumed but nothing shipped — the wallet vanished
                    // (left geoscape / mid-load). The poll or watcher rebind re-arms once it returns.
                    Debug.Log("[Multiplayer] Wallet dirty-flush skipped guard=wallet-null (dirty flag dropped; poll/rebind re-arms when wallet returns)");
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

            // Inc4 S2 — host-driven travel mirror. Throttled poll of every MOVING vehicle's world placement,
            // broadcast on the GeoVehiclePos (0xA5) surface so a sim-frozen client (S1) still sees vehicles
            // travel (it applies the absolute position; it never re-navigates). Gated on the SAME sim-freeze
            // feature flag as S1 so flag-OFF rollback = ZERO new traffic (the client then simulates travel
            // locally, legacy path). Idle vehicles ship 0 bytes (per-vehicle signature skip in GeoVehicleMirror).
            if (ClientSimFreeze.Enabled && ++_vehiclePollTick >= VehiclePollTickInterval)
            {
                _vehiclePollTick = 0;
                State.GeoVehicleMirror.HostPollAndBroadcast(_engine, _geoLiveSeq);
                // Inc4 S2 — travel-METADATA mirror (0xA6): same throttle/gate as the 0xA5 position poll. Ships only
                // on a genuine travel transition (signature-skip), so it is near-silent; it feeds the native yellow
                // route line on the frozen client (Symptom B). Client never simulates — display-only mirror.
                State.GeoVehicleTravelMirror.HostPollAndBroadcast(_engine, _geoLiveSeq);
                // Inc4 S2 — site-exploration-PROGRESS mirror (0xA7): same throttle/gate. Ships each exploring
                // vehicle's bar fill (signature-skip on whole-percent progress → ~free at rest, ~100 updates over an
                // exploration). Polled AFTER the 0xA6 travel-meta so the client applies CurrentSite before the bar
                // parents to it. Feeds the native exploration progress bar on the frozen client (Symptom: no bar).
                State.GeoVehicleExploreMirror.HostPollAndBroadcast(_engine, _geoLiveSeq);
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
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] geo wallet envelope failed: " + ex.Message); }
                return true;
            }
            if (surfaceId == SurfaceIds.GeoState)
            {
                // Behavior-identical to the legacy 0x64 path: OnStateSync is host-guarded + per-channel
                // version-guarded (SequenceTracker.ShouldApplyChannel), so applying via the envelope is
                // idempotent — a same-version duplicate from the legacy packet (or a re-send) drops.
                try { OnStateSync(payload); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] geo state envelope failed: " + ex.Message); }
                return true;
            }
            if (surfaceId == SurfaceIds.GeoVehiclePos)
            {
                // Inc4 S2 host-driven travel mirror: the client applies each moving vehicle's absolute world
                // placement (Surface.position/rotation) ONLY while its sim is frozen (GeoVehicleMirror gates on
                // ClientSimFreeze.ShouldFreeze); the host never receives its own broadcast. Seq-guarded (dup/stale drop).
                try { State.GeoVehicleMirror.HandleVehiclePos(payload, _geoLiveSeq); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] geo vehiclepos envelope failed: " + ex.Message); }
                return true;
            }
            if (surfaceId == SurfaceIds.GeoVehicleTravel)
            {
                // Inc4 S2 route-line metadata mirror: the frozen client writes each vehicle's display-only travel
                // state (Travelling/CurrentSite/DestinationSites) so the native yellow route line renders correctly
                // (GeoVehicleTravelMirror gates on ClientSimFreeze.ShouldFreeze). Seq-guarded (dup/stale drop).
                try { State.GeoVehicleTravelMirror.HandleTravelMeta(payload, _geoLiveSeq); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] geo vehicletravel envelope failed: " + ex.Message); }
                return true;
            }
            if (surfaceId == SurfaceIds.GeoVehicleExplore)
            {
                // Inc4 S2 exploration-progress mirror: the frozen client renders the native site-exploration bar at
                // the host fraction (GeoVehicleExploreMirror gates on ClientSimFreeze.ShouldFreeze). Seq-guarded (dup/stale drop).
                try { State.GeoVehicleExploreMirror.HandleExplore(payload, _geoLiveSeq); }
                catch (Exception ex) { Debug.LogError("[Multiplayer][geo] geo vehicleexplore envelope failed: " + ex.Message); }
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
