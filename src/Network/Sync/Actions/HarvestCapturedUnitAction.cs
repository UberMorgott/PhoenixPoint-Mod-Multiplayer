using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Containment client intent: HARVEST (dismantle) a captured Pandoran for resources — the
    /// containment-screen food/mutagen buttons (<c>UIStateRosterAliens.OnDismantpleFor*DialogCallback</c> →
    /// <c>GeoPhoenixFaction.HarvestCapturedUnit(unit, ResourceType)</c>, UIStateRosterAliens.cs:275/296).
    /// The host runs the native call authoritatively; it funnels through <c>KillCapturedUnit</c>
    /// (GeoPhoenixFaction.cs:893) so removal mirrors on #10 via the existing dirty seam, and the
    /// <c>Wallet.Give</c> yield rides the 0xA0 wallet snapshot (the sole balance writer).
    /// <see cref="IHostOnlyApply"/>. Category Recruitment (pool family, no per-soldier owner). Captive keyed
    /// by <see cref="ContainmentTarget"/> ordinal+fingerprint; unknown captive → logged no-op.
    /// Wire: <c>i32 ordinal, string templateGuid, i32 resourceType</c> (native ResourceType numeric value).
    /// </summary>
    public sealed class HarvestCapturedUnitAction : ISyncedAction, IHostOnlyApply
    {
        private readonly int _ordinal;
        private readonly string _templateGuid;
        private readonly int _resourceType;

        public HarvestCapturedUnitAction(int ordinal, string templateGuid, int resourceType)
        {
            _ordinal = ordinal;
            _templateGuid = templateGuid ?? string.Empty;
            _resourceType = resourceType;
        }

        public int Ordinal => _ordinal;
        public string TemplateGuid => _templateGuid;
        public int ResourceType => _resourceType;

        public ushort ActionId => SyncedActionIds.HarvestCapturedUnit;
        public ActionCategory Category => ActionCategory.Recruitment;

        public void Write(BinaryWriter w)
        {
            w.Write(_ordinal);
            w.Write(_templateGuid);
            w.Write(_resourceType);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            int ordinal = r.ReadInt32();
            string templateGuid = r.ReadString();
            int resourceType = r.ReadInt32();
            return new HarvestCapturedUnitAction(ordinal, templateGuid, resourceType);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.HarvestCaptured(rt, _ordinal, _templateGuid, _resourceType);
    }
}
