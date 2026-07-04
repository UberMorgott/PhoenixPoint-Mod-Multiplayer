using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Relays a geoscape vehicle TRAVEL ORDER (Inc4 S2). A client's sim is frozen, so a local
    /// <c>GeoVehicle.StartTravel</c> can neither advance (the paused <c>NavigateRoutine</c> never runs) nor be
    /// broadcast — the order simply died (Symptom A). This action carries the INTENT to the host, which runs the
    /// authoritative <c>StartTravel</c>; the resulting motion mirrors back on the 0xA5 position surface and the
    /// route-line metadata on the 0xA6 surface. Wire payload: <c>i32 ownerId, i32 vehicleId, u16 destCount,
    /// i32 destSiteId*</c>.
    /// <list type="bullet">
    ///   <item><c>ownerId</c> = <see cref="Multiplayer.Network.Sync.State.GeoVehiclePos.StableOwnerKey"/> of the
    ///   owner faction's def asset name — the composite-key half that disambiguates per-faction VehicleIDs
    ///   (shared with the position mirror so the host resolves the SAME live vehicle).</item>
    ///   <item><c>vehicleId</c> = <c>GeoVehicle.VehicleID</c> (per-faction).</item>
    ///   <item><c>destSiteIds</c> = the ordered <c>GeoSite.SiteId</c>s of the travel path (as computed by the
    ///   client's <c>MoveVehicleAbility.ActivateInternal</c> → <c>StartTravel(List&lt;GeoSite&gt;)</c>), so
    ///   chained/multi-hop routes replay faithfully instead of re-pathfinding host-side.</item>
    /// </list>
    /// <see cref="IHostOnlyApply"/>: the client NEVER replays the order (canon: client = pure mirror, sim frozen).
    /// Its travel display converges through the dedicated host→client mirrors (0xA5 position + 0xA6 metadata),
    /// exactly as the host's OWN travel order does — one authoritative writer, no client-side simulation.
    /// </summary>
    public sealed class MoveVehicleAction : ISyncedAction, IHostOnlyApply
    {
        private readonly int _ownerId;
        private readonly int _vehicleId;
        private readonly int[] _destSiteIds;

        public MoveVehicleAction(int ownerId, int vehicleId, int[] destSiteIds)
        {
            _ownerId = ownerId;
            _vehicleId = vehicleId;
            _destSiteIds = destSiteIds ?? Array.Empty<int>();
        }

        public int OwnerId => _ownerId;
        public int VehicleId => _vehicleId;
        public int[] DestSiteIds => _destSiteIds;

        public ushort ActionId => SyncedActionIds.MoveVehicle;
        public ActionCategory Category => ActionCategory.VehicleTravel;

        public void Write(BinaryWriter w)
        {
            w.Write(_ownerId);
            w.Write(_vehicleId);
            w.Write((ushort)_destSiteIds.Length);
            foreach (var id in _destSiteIds) w.Write(id);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            int ownerId = r.ReadInt32();
            int vehicleId = r.ReadInt32();
            int n = r.ReadUInt16();
            var dests = new int[n];
            for (int i = 0; i < n; i++) dests[i] = r.ReadInt32();
            return new MoveVehicleAction(ownerId, vehicleId, dests);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive && _destSiteIds != null && _destSiteIds.Length > 0;

        // Host authoritative: resolve the vehicle + destination sites and run the native StartTravel (inside
        // SyncApplyScope on the OnActionRequest path, so the StartTravel interceptor passes through). The client
        // replay is suppressed (IHostOnlyApply) — its display converges via the 0xA5/0xA6 mirrors.
        public void Apply(GeoRuntime rt)
            => VehicleTravelReflection.StartTravel(rt, _ownerId, _vehicleId, _destSiteIds);
    }
}
