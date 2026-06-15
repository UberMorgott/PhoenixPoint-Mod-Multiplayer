using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Adds a research element to the player faction's research queue.
    /// Wire payload: <c>string researchId</c> (the <c>ResearchElement.ResearchID</c>).
    /// </summary>
    public sealed class StartResearchAction : ISyncedAction
    {
        private readonly string _researchId;

        public StartResearchAction(string researchId) { _researchId = researchId; }

        public ushort ActionId => SyncedActionIds.StartResearch;
        public ActionCategory Category => ActionCategory.Research;

        public void Write(BinaryWriter w) => w.Write(_researchId ?? "");
        public static ISyncedAction Read(BinaryReader r) => new StartResearchAction(r.ReadString());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_researchId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => ResearchReflection.AddToQueue(rt, _researchId);
    }
}
