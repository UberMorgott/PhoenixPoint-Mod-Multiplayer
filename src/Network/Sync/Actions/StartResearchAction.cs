using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Adds a research element to the player faction's research queue.
    /// Wire payload: <c>string researchId</c> (the <c>ResearchElement.ResearchID</c>).
    ///
    /// IHostOnlyApply: a start is the client-&gt;host REQUEST trigger (SendActionRequest from
    /// AddResearchToQueuePatch); the host still applies it in <c>SyncEngine.OnActionRequest</c> (that path
    /// never checks the marker). The marker suppresses only the CLIENT's inbound <c>OnActionApply</c> replay.
    /// Without it, a start replicates to the client via BOTH this action (OnActionApply + research refresh)
    /// AND the <c>ResearchChannel</c> state echo (OnStateSync + refresh) → two UIModuleResearch.Init rebuilds
    /// against different intermediate states → visible flicker (the started item lingers in Available showing
    /// a partial progress chunk before entering the queue). Making the channel the single client source of
    /// truth (this action suppressed on the client, like <see cref="ResearchCompletedAction"/>) yields one
    /// refresh, no bounce. The host marks ch2 dirty on every queue add so the channel reaches the client.
    /// </summary>
    public sealed class StartResearchAction : ISyncedAction, IHostOnlyApply
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
