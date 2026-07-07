using System;
using System.IO;
using Multiplayer.Network.Sync.State;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Containment client intent: KILL a captured Pandoran (the containment-screen "kill" button —
    /// <c>UIStateRosterAliens.OnKillAlienDialogCallback</c> → <c>GeoPhoenixFaction.KillCapturedUnit</c>,
    /// UIStateRosterAliens.cs:256). The host runs the native call authoritatively; removal mirrors back on
    /// the #10 captured full-set (the existing <c>PhoenixKillCapturedUnitPoolDirtyPatch</c> seam).
    /// <see cref="IHostOnlyApply"/>. Category Recruitment (the recruit/containment-pool family gate —
    /// a captive has no per-soldier owner, so no ownership check; the Hire precedent). The captive is keyed
    /// by <see cref="ContainmentTarget"/> ordinal+fingerprint; an unknown captive (host trimmed it since the
    /// last mirror) resolves null → logged no-op. Wire: <c>i32 ordinal, string templateGuid</c>.
    /// </summary>
    public sealed class KillCapturedUnitAction : ISyncedAction, IHostOnlyApply
    {
        private readonly int _ordinal;
        private readonly string _templateGuid;

        public KillCapturedUnitAction(int ordinal, string templateGuid)
        {
            _ordinal = ordinal;
            _templateGuid = templateGuid ?? string.Empty;
        }

        public int Ordinal => _ordinal;
        public string TemplateGuid => _templateGuid;

        public ushort ActionId => SyncedActionIds.KillCapturedUnit;
        public ActionCategory Category => ActionCategory.Recruitment;

        public void Write(BinaryWriter w)
        {
            w.Write(_ordinal);
            w.Write(_templateGuid);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            int ordinal = r.ReadInt32();
            string templateGuid = r.ReadString();
            return new KillCapturedUnitAction(ordinal, templateGuid);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive;

        public void Apply(GeoRuntime rt)
            => PersonnelEditReflection.KillCaptured(rt, _ordinal, _templateGuid);
    }
}
