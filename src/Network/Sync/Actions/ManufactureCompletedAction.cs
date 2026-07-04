using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven manufacturing completion. Wire payload: <c>string itemDefId, int queueIndex</c>.
    /// The queue item has no stable instance id, so we key by item def GUID with the queue index as a
    /// deterministic disambiguation fallback (see <see cref="ManufactureReflection.RemoveFromQueueEchoOnly"/>).
    ///
    /// NOT IHostOnlyApply: unlike research, the completion has a non-channelled STRUCTURAL effect the client
    /// still needs — the QUEUE REMOVAL — so a full client suppression would leave a stale queue entry. But
    /// the client must NOT re-run the reward part. The host already ran the real <c>FinishManufactureItem</c>
    /// (its progression hit the patched original directly — this <c>Apply</c> is client-only), which GRANTS
    /// the item via <c>RelatedItemDef.OnManufacture</c>; on the client that produced item converges through
    /// the authoritative <c>InventoryChannel</c>. So the client <c>Apply</c> does a REWARD-SUPPRESSED replay:
    /// it removes the finished item from the queue WITHOUT granting (no <c>FinishManufactureItem</c>) and
    /// WITHOUT refunding (no <c>Cancel</c>/<c>Wallet.Give</c>) — see
    /// <see cref="ManufactureReflection.RemoveFromQueueEchoOnly"/>. Same double-grant class as the research
    /// CRIT, but solved with targeted reward-suppression instead of full suppression because the queue is not
    /// channelled.
    /// </summary>
    public sealed class ManufactureCompletedAction : ISyncedAction
    {
        private readonly string _itemDefId;
        private readonly int _queueIndex;

        public ManufactureCompletedAction(string itemDefId, int queueIndex)
        {
            _itemDefId = itemDefId;
            _queueIndex = queueIndex;
        }

        public ushort ActionId => SyncedActionIds.ManufactureCompleted;
        public ActionCategory Category => ActionCategory.Manufacturing;

        public void Write(BinaryWriter w)
        {
            w.Write(_itemDefId ?? "");
            w.Write(_queueIndex);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new ManufactureCompletedAction(r.ReadString(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_itemDefId) && rt != null && rt.IsGeoscapeActive;

        // Client-only path (host completes via the patched original). Reward-suppressed: remove the queue
        // item without granting/refunding; the produced item converges via InventoryChannel.
        public void Apply(GeoRuntime rt) => ManufactureReflection.RemoveFromQueueEchoOnly(rt, _itemDefId, _queueIndex);
    }
}
