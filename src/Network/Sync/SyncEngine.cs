using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multipleer.Network.MessageLayer;
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
    public sealed class SyncEngine
    {
        private readonly NetworkEngine _engine;
        private readonly SequenceTracker _tracker = new SequenceTracker();
        private ulong _hostSequence;   // host-assigned, monotonic action sequence
        private ulong _walletVersion;  // host-assigned, monotonic wallet version
        private uint _nonceCounter;    // client request correlation
        private bool _walletDirty;     // host: wallet changed since last flush
        private readonly Dictionary<uint, ISyncedAction> _pending = new Dictionary<uint, ISyncedAction>();

        public SyncEngine(NetworkEngine engine)
        {
            _engine = engine;
            SyncRegistration.RegisterAll();   // registers every action reader (later batch)
        }

        // ─── Outbound (called by interceptors) ────────────────────────────

        /// <summary>Client: send a discrete action request to the host (block local apply, await echo).</summary>
        public void SendActionRequest(ISyncedAction a)
        {
            if (a == null) return;
            uint nonce = ++_nonceCounter;
            _pending[nonce] = a;
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
            var action = ReadAction(id, payload);
            if (action == null) return;

            Guid actor = ResolveActor(senderPeerId);
            var rt = GeoRuntime.Instance;
            if (!PermissionGate.CheckFor(actor, action.Category) || !action.Validate(rt, actor))
            {
                _engine.SendToClient(senderPeerId, new NetworkMessage(PacketType.ActionReject,
                    SyncProtocol.EncodeActionReject(nonce, 1, "rejected")));
                return;
            }

            try { using (SyncApplyScope.Enter()) action.Apply(rt); }   // host executes authoritative mutation
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionRequest apply failed: " + ex.Message); }

            ulong seq = ++_hostSequence;
            _tracker.Mark(seq);
            _engine.BroadcastToAll(new NetworkMessage(PacketType.ActionApply,
                SyncProtocol.EncodeActionApply(id, seq, payload)));
        }

        // ─── Inbound: client ──────────────────────────────────────────────

        public void OnActionApply(byte[] data)
        {
            if (!SyncProtocol.TryDecodeActionApply(data, out var id, out var seq, out var payload)) return;
            if (!_tracker.ShouldApply(seq)) return;   // last-writer-wins / dedupe
            _tracker.Mark(seq);
            var action = ReadAction(id, payload);
            if (action == null) return;
            try { using (SyncApplyScope.Enter()) action.Apply(GeoRuntime.Instance); }
            catch (Exception ex) { Debug.LogError("[Multipleer] SyncEngine.OnActionApply failed: " + ex.Message); }
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

        // ─── Per-frame tick (from NetworkEngine.Update) ───────────────────

        public void Tick()
        {
            if (_engine == null || !_engine.IsActive) return;
            if (!_engine.IsHost) return;

            // Host: bind the wallet watcher once the geoscape (and its wallet) is live. Attach is
            // idempotent — it early-returns until the wallet exists, then once it is bound. Mirrors the
            // deferred world-load: the wallet only appears frames after EnterLevel→FinishLevel.
            WalletWatcher.Attach(_engine);

            if (_walletDirty)
            {
                _walletDirty = false;
                var slots = WalletApplier.Snapshot(GeoRuntime.Instance);
                if (slots != null)
                    _engine.BroadcastToAll(new NetworkMessage(PacketType.WalletSync,
                        SyncProtocol.EncodeWalletSync(++_walletVersion, slots)));
            }
        }

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
