using System;
using System.IO;

namespace Multipleer.Network.Sync.Actions
{
    /// <summary>
    /// Relays a geoscape EXPLORE-SITE order (the "Разведать точки интереса" / Explore point-of-interest button →
    /// <c>ExploreSiteAbility.ActivateInternal</c> :9 → <c>GeoVehicle.StartExploringCurrentSite()</c> GeoVehicle.cs:414).
    /// Same frozen-client death as <see cref="MoveVehicleAction"/>: on a co-op CLIENT the geoscape sim clock is
    /// paused (Inc4 S1: Timing.Paused), so a local StartExploringCurrentSite schedules its exploration timer on the
    /// paused geo Timing (the <c>_explorationUpdateable</c> never fires) AND never reaches the host — the order
    /// silently died. So the CLIENT relays this intent + BLOCKS the local order; the HOST runs the authoritative
    /// timed exploration on its live clock.
    /// <list type="bullet">
    ///   <item>Wire payload: <c>i32 ownerId, i32 vehicleId</c> — NO site id. StartExploringCurrentSite explores the
    ///   vehicle's OWN <c>CurrentSite</c>, and the host's vehicle is the authoritative position (it arrived at the
    ///   site via the mirrored travel), so the host needs only the vehicle identity.</item>
    ///   <item><c>ownerId</c> = <see cref="Multipleer.Network.Sync.State.GeoVehiclePos.StableOwnerKey"/> of the owner
    ///   faction def asset name; <c>vehicleId</c> = <c>GeoVehicle.VehicleID</c> — the SAME composite key the travel
    ///   relay + 0xA5 position mirror use, so the host resolves the SAME live vehicle.</item>
    /// </list>
    /// <see cref="IHostOnlyApply"/>: the client NEVER replays it (canon: client = pure mirror, sim frozen). The
    /// exploration's visible OUTCOME — completion fires <c>GeoVehicle.SiteExplored</c> (GeoVehicle.cs:477) → the
    /// site reveal / encounter event — converges through the existing geoscape event replication + wallet/
    /// inventory/site echoes, exactly as the host's OWN explore order does. (The transient in-progress exploration
    /// VISUAL is sim-driven and not mirrored; cosmetic only.)
    /// </summary>
    public sealed class ExploreSiteAction : ISyncedAction, IHostOnlyApply
    {
        private readonly int _ownerId;
        private readonly int _vehicleId;

        public ExploreSiteAction(int ownerId, int vehicleId)
        {
            _ownerId = ownerId;
            _vehicleId = vehicleId;
        }

        public int OwnerId => _ownerId;
        public int VehicleId => _vehicleId;

        public ushort ActionId => SyncedActionIds.ExploreSite;
        public ActionCategory Category => ActionCategory.VehicleTravel;

        public void Write(BinaryWriter w)
        {
            w.Write(_ownerId);
            w.Write(_vehicleId);
        }

        public static ISyncedAction Read(BinaryReader r)
        {
            int ownerId = r.ReadInt32();
            int vehicleId = r.ReadInt32();
            return new ExploreSiteAction(ownerId, vehicleId);
        }

        public bool Validate(GeoRuntime rt, Guid actor)
            => rt != null && rt.IsGeoscapeActive;

        // Host authoritative: resolve the vehicle and run the native StartExploringCurrentSite (inside
        // SyncApplyScope on the OnActionRequest path, so the interceptor passes through). The client replay is
        // suppressed (IHostOnlyApply) — its display converges via event replication when exploration completes.
        public void Apply(GeoRuntime rt)
            => VehicleTravelReflection.StartExploringCurrentSite(rt, _ownerId, _vehicleId);
    }
}
