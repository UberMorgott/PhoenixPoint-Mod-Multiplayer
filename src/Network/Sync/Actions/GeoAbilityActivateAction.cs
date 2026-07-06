using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Generic GEOSCAPE ability-activation relay (the geoscape analogue of the tactical 0x8E generic relay). ONE
    /// action carries the INTENT for every sim-mutating geoscape ability that flows through
    /// <c>GeoAbility.Activate(GeoAbilityTarget)</c> — Harvest / Excavate / EmergencyRepair / Scan /
    /// AncientSiteProbe / ActivateBase / AncientGuardianGuard (see
    /// <see cref="Multiplayer.Sync.Geoscape.GeoAbilityRelay"/> for the allowlist + rationale). A mirroring
    /// client's sim is frozen, so a local activation dies silently; the client suppresses it and relays this
    /// action. The host re-resolves the live actor + its GeoAbility by def guid and runs the authoritative
    /// <c>Activate</c>; the result mirrors back on the existing geoscape state channels (site state, faction /
    /// wallet, mid-session actor spawn) — the host's native apply fires the very change-events those channels
    /// already subscribe to.
    ///
    /// Wire payload (fixed, kind-discriminated):
    /// <code>
    ///   u8  actorKind                    // 1 = vehicle, 2 = site
    ///     vehicle: i32 ownerId, i32 vehicleId     (composite key, == the position/travel mirrors' key)
    ///     site:    i32 siteId
    ///   str abilityDefGuid               // GeoAbilityDef.Guid — stable across host/client
    ///   u8  targetKind                   // 0 = none/self, 1 = site, 2 = vehicle, 3 = pos
    ///     site:    i32 targetSiteId
    ///     vehicle: i32 targetOwnerId, i32 targetVehicleId
    ///     pos:     f32 x, f32 y, f32 z
    ///   str targetFactionGuid            // "" = no override; else the acting faction's Def.Guid (ActivateBase /
    ///                                    //    Guard build the target with Faction = the Phoenix faction, which
    ///                                    //    the actor-site's own faction is NOT while abandoned)
    /// </code>
    ///
    /// <see cref="IHostOnlyApply"/>: the client NEVER replays the intent (canon: client = pure mirror, sim
    /// frozen) — its display converges through the dedicated host→client state channels, exactly as the host's
    /// OWN activation does. Backward-tolerant: an older peer without this id registered drops the intent /
    /// outcome cleanly at <c>ReadAction</c> (unregistered-id → null), never mis-parsing bytes.
    /// </summary>
    public sealed class GeoAbilityActivateAction : ISyncedAction, IHostOnlyApply
    {
        // actorKind
        public const byte ActorVehicle = 1;
        public const byte ActorSite = 2;
        // targetKind
        public const byte TargetNone = 0;    // no payload — host rebuilds the target from the actor itself
        public const byte TargetSite = 1;    // i32 siteId
        public const byte TargetVehicle = 2; // i32 ownerId, i32 vehicleId
        public const byte TargetPos = 3;     // f32 x, f32 y, f32 z

        private readonly byte _actorKind;
        private readonly int _actorOwnerId;
        private readonly int _actorVehicleId;
        private readonly int _actorSiteId;
        private readonly string _abilityDefGuid;
        private readonly byte _targetKind;
        private readonly int _targetSiteId;
        private readonly int _targetOwnerId;
        private readonly int _targetVehicleId;
        private readonly float _tx;
        private readonly float _ty;
        private readonly float _tz;
        private readonly string _targetFactionGuid;

        public GeoAbilityActivateAction(byte actorKind, int actorOwnerId, int actorVehicleId, int actorSiteId,
            string abilityDefGuid, byte targetKind, int targetSiteId, int targetOwnerId, int targetVehicleId,
            float tx, float ty, float tz, string targetFactionGuid)
        {
            _actorKind = actorKind;
            _actorOwnerId = actorOwnerId;
            _actorVehicleId = actorVehicleId;
            _actorSiteId = actorSiteId;
            _abilityDefGuid = abilityDefGuid ?? string.Empty;
            _targetKind = targetKind;
            _targetSiteId = targetSiteId;
            _targetOwnerId = targetOwnerId;
            _targetVehicleId = targetVehicleId;
            _tx = tx;
            _ty = ty;
            _tz = tz;
            _targetFactionGuid = targetFactionGuid ?? string.Empty;
        }

        public byte ActorKind => _actorKind;
        public int ActorOwnerId => _actorOwnerId;
        public int ActorVehicleId => _actorVehicleId;
        public int ActorSiteId => _actorSiteId;
        public string AbilityDefGuid => _abilityDefGuid;
        public byte TargetKind => _targetKind;
        public int TargetSiteId => _targetSiteId;
        public int TargetOwnerId => _targetOwnerId;
        public int TargetVehicleId => _targetVehicleId;
        public float TX => _tx;
        public float TY => _ty;
        public float TZ => _tz;
        public string TargetFactionGuid => _targetFactionGuid;

        public ushort ActionId => SyncedActionIds.GeoAbilityActivate;
        public ActionCategory Category => ActionCategory.GeoAbility;

        public void Write(BinaryWriter w)
        {
            w.Write(_actorKind);
            if (_actorKind == ActorVehicle) { w.Write(_actorOwnerId); w.Write(_actorVehicleId); }
            else { w.Write(_actorSiteId); }
            w.Write(_abilityDefGuid ?? string.Empty);
            w.Write(_targetKind);
            switch (_targetKind)
            {
                case TargetSite: w.Write(_targetSiteId); break;
                case TargetVehicle: w.Write(_targetOwnerId); w.Write(_targetVehicleId); break;
                case TargetPos: w.Write(_tx); w.Write(_ty); w.Write(_tz); break;
                // TargetNone: no payload
            }
            w.Write(_targetFactionGuid ?? string.Empty);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            byte actorKind = r.ReadByte();
            int actorOwnerId = 0, actorVehicleId = 0, actorSiteId = 0;
            if (actorKind == ActorVehicle) { actorOwnerId = r.ReadInt32(); actorVehicleId = r.ReadInt32(); }
            else { actorSiteId = r.ReadInt32(); }
            string abilityDefGuid = r.ReadString();
            byte targetKind = r.ReadByte();
            int targetSiteId = 0, targetOwnerId = 0, targetVehicleId = 0;
            float tx = 0f, ty = 0f, tz = 0f;
            switch (targetKind)
            {
                case TargetSite: targetSiteId = r.ReadInt32(); break;
                case TargetVehicle: targetOwnerId = r.ReadInt32(); targetVehicleId = r.ReadInt32(); break;
                case TargetPos: tx = r.ReadSingle(); ty = r.ReadSingle(); tz = r.ReadSingle(); break;
                // TargetNone: no payload
            }
            string targetFactionGuid = r.ReadString();
            return new GeoAbilityActivateAction(actorKind, actorOwnerId, actorVehicleId, actorSiteId,
                abilityDefGuid, targetKind, targetSiteId, targetOwnerId, targetVehicleId, tx, ty, tz,
                targetFactionGuid);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive
               && (_actorKind == ActorVehicle || _actorKind == ActorSite)
               && !string.IsNullOrEmpty(_abilityDefGuid);

        /// <summary>
        /// Host-apply provider, wired by the SyncEngine ctor to <c>GeoAbilityRelayReflection.Activate</c> (this
        /// file stays game-glue-free so the pure wire/round-trip tests can link it — the project's pure-core /
        /// game-glue split, same seam as <see cref="RemoveFacilityAction.IsAuthoritativeHost"/>). Null in the
        /// pure test build (Apply is never invoked there).
        /// </summary>
        public static Action<GeoRuntime, GeoAbilityActivateAction> ApplyProvider;

        // Host authoritative: resolve the live actor + its GeoAbility by def guid, rebuild the target, and run
        // the native Activate (inside SyncApplyScope on the OnActionRequest path, so the generic Activate
        // interceptor passes through). The client replay is suppressed (IHostOnlyApply) — its display converges
        // via the existing geoscape state channels.
        public void Apply(GeoRuntime rt) => ApplyProvider?.Invoke(rt, this);
    }
}
