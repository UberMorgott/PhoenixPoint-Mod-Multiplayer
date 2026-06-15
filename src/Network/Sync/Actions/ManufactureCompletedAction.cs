using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Host-driven manufacturing completion. Wire payload: <c>string itemDefId, int queueIndex</c>.
    /// The queue item has no stable instance id, so we key by item def GUID with the queue index as a
    /// deterministic disambiguation fallback (see <see cref="ManufactureReflection.Complete"/>).
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

        public void Apply(GeoRuntime rt) => ManufactureReflection.Complete(rt, _itemDefId, _queueIndex);
    }
}
