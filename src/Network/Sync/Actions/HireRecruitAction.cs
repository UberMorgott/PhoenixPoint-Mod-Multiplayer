using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// PS4 client-edit intent: HIRE a recruit into a base. The recruit source is either a HAVEN
    /// (<c>sourceKind=0</c>, keyed by the haven <c>SiteId</c> whose <c>AvailableRecruit</c> descriptor is
    /// mirrored on #10) or the NAKED pool (<c>sourceKind=1</c>, keyed by pool ordinal). The host resolves the
    /// source <c>GeoUnitDescriptor</c> + the destination base <c>Site</c> and runs the authoritative
    /// <c>GeoPhoenixFaction.HireNakedRecruit</c> (the exact recruit-screen call, UIStateRosterRecruits.cs:301);
    /// the new soldier's roster landing mirrors back on the #9/#6 membership channels and its removal from the
    /// pool on #10. <see cref="IHostOnlyApply"/>. Category Recruitment (ManageRecruitment) — a pool hire has no
    /// per-soldier owner yet, so no ownership check. Wire: <c>i32 sourceKind, i32 sourceId, i32 destBaseSiteId</c>.
    /// </summary>
    public sealed class HireRecruitAction : ISyncedAction, IHostOnlyApply
    {
        private readonly int _sourceKind;
        private readonly int _sourceId;
        private readonly int _destBaseSiteId;

        public HireRecruitAction(int sourceKind, int sourceId, int destBaseSiteId)
        {
            _sourceKind = sourceKind;
            _sourceId = sourceId;
            _destBaseSiteId = destBaseSiteId;
        }

        public int SourceKind => _sourceKind;
        public int SourceId => _sourceId;
        public int DestBaseSiteId => _destBaseSiteId;

        public ushort ActionId => SyncedActionIds.HireRecruit;
        public ActionCategory Category => ActionCategory.Recruitment;

        public void Write(BinaryWriter w)
        {
            w.Write(_sourceKind);
            w.Write(_sourceId);
            w.Write(_destBaseSiteId);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            int sourceKind = r.ReadInt32();
            int sourceId = r.ReadInt32();
            int destBaseSiteId = r.ReadInt32();
            return new HireRecruitAction(sourceKind, sourceId, destBaseSiteId);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.Hire(rt, _sourceKind, _sourceId, _destBaseSiteId);
    }
}
