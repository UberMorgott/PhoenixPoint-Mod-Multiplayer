using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Applies a geoscape event choice. Wire payload: <c>string eventId, i32 choiceIndex</c>.
    /// eventId = <c>GeoscapeEvent.EventID</c>; choiceIndex = index into <c>EventData.Choices</c>
    /// (-1 = the null "decline" choice).
    /// </summary>
    public sealed class AnswerEventAction : ISyncedAction
    {
        private readonly string _eventId;
        private readonly int _choiceIndex;

        public AnswerEventAction(string eventId, int choiceIndex)
        {
            _eventId = eventId;
            _choiceIndex = choiceIndex;
        }

        public ushort ActionId => SyncedActionIds.AnswerEvent;
        public ActionCategory Category => ActionCategory.Dialogs;

        public void Write(BinaryWriter w)
        {
            w.Write(_eventId ?? "");
            w.Write(_choiceIndex);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new AnswerEventAction(r.ReadString(), r.ReadInt32());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_eventId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => EventReflection.CompleteEvent(rt, _eventId, _choiceIndex);
    }
}
