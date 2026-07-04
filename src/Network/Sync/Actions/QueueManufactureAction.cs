using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Queues a manufacturable item in the player faction's production queue.
    /// Wire payload: <c>string itemDefId</c> (the <c>ItemDef.Guid</c>).
    /// </summary>
    public sealed class QueueManufactureAction : ISyncedAction
    {
        private readonly string _itemDefId;

        public QueueManufactureAction(string itemDefId) { _itemDefId = itemDefId; }

        public ushort ActionId => SyncedActionIds.QueueManufacture;
        public ActionCategory Category => ActionCategory.Manufacturing;

        public void Write(BinaryWriter w) => w.Write(_itemDefId ?? "");
        public static ISyncedAction Read(BinaryReader r) => new QueueManufactureAction(r.ReadString());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_itemDefId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => ManufactureReflection.Queue(rt, _itemDefId);
    }
}
