using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>The relative queue move the player clicked. Wire value is the byte below.</summary>
    public enum ResearchReorderKind : byte
    {
        ToTop = 0,   // Research.PutInFromOfQueue  — move the element to the front of the queue
        Up = 1,      // Research.PutUpInQueue      — move the element one slot toward the front
        Down = 2,    // Research.PutDownInQueue    — move the element one slot toward the back
    }

    /// <summary>
    /// Client-initiated REORDER of a queued research element (move up / down / to-top). The client is
    /// frozen and does not simulate, so the local move must reach the host (the ResearchChannel echo then
    /// reconciles every peer's queue ORDER). Wire payload: <c>string researchId</c> + <c>byte kind</c>
    /// (the <see cref="ResearchReorderKind"/>).
    ///
    /// Host <c>Apply</c> replays the EXACT native relative move the client clicked
    /// (<c>Research.PutInFromOfQueue</c> / <c>PutUpInQueue</c> / <c>PutDownInQueue</c>) on the
    /// authoritative queue; the host and client queues are kept identical by ch2, so a relative move
    /// yields the same result on both. There is no move-to-bottom button in the vanilla UI, so only the
    /// three native ops above are carried.
    ///
    /// IHostOnlyApply: same single-source-of-truth contract as <see cref="StartResearchAction"/> /
    /// <see cref="CancelResearchAction"/>. The reorder is the client-&gt;host REQUEST trigger; the host
    /// applies it in <c>OnActionRequest</c> (which auto-marks ch2 dirty for the Research category) and the
    /// resulting queue ORDER reaches every peer through the <c>ResearchChannel</c> echo (the snapshot
    /// carries the ordered queue, and the client reconcile enforces that order via
    /// <c>Research.InsertAtPosition</c>). Marking this IHostOnlyApply stops the client double-applying the
    /// move via BOTH the action replay AND ch2.
    /// </summary>
    public sealed class ReorderResearchAction : ISyncedAction, IHostOnlyApply
    {
        private readonly string _researchId;
        private readonly ResearchReorderKind _kind;

        public ReorderResearchAction(string researchId, ResearchReorderKind kind)
        {
            _researchId = researchId;
            _kind = kind;
        }

        public ushort ActionId => SyncedActionIds.ReorderResearch;
        public ActionCategory Category => ActionCategory.Research;

        public void Write(BinaryWriter w)
        {
            w.Write(_researchId ?? "");
            w.Write((byte)_kind);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            string id = r.ReadString();
            var kind = (ResearchReorderKind)r.ReadByte();
            return new ReorderResearchAction(id, kind);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_researchId) && rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt) => ResearchStateReflection.Reorder(rt, _researchId, _kind);
    }
}
