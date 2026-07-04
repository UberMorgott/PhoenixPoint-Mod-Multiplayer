using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — one vehicle's mirrored TRAVEL METADATA: the display-feeding native nav state the sim-frozen
    /// client can NOT derive on its own (its <c>GeoNavComponent</c> never runs, so <c>DestinationSites</c> /
    /// <c>CurrentSite</c> / <c>Travelling</c> stay frozen at join while the pivot moves via the 0xA5 position
    /// mirror). The native geoscape route-line renderer (<c>UIStateVehicleSelected.DrawCurrentPath</c> →
    /// <c>GeoscapeView.DrawVehiclePathLinks</c>/<c>UpdateVehicleFirstPathLink</c>) reads EXACTLY these fields:
    ///   * <c>Travelling</c>  — gate: the line only draws while travelling.
    ///   * <c>CurrentSite</c> — the line's ORIGIN when parked at a site (null in transit → origin = live
    ///     WorldPosition, which IS mirrored). A stale non-null CurrentSite pins the origin to the wrong site.
    ///   * <c>DestinationSites</c> — the remaining waypoints (drawn as the yellow ConfirmedSiteLink chain).
    /// Mirroring these host→client (display-only, NEVER driving the frozen sim) makes the native yellow line
    /// draw from the same authoritative state the host reads. Sites are carried by <c>GeoSite.SiteId</c> (stable
    /// int), the vehicle by the composite (OwnerId, VehicleId) key shared with <see cref="GeoVehiclePos"/>.
    ///
    /// Pure value type + wire codec (no UnityEngine / SyncEngine dependency) so the round-trip and change
    /// signature are directly unit-testable (mirrors <see cref="GeoVehiclePos"/> / <see cref="GeoVehicleSnapshot"/>).
    /// The engine glue (host read + client apply) lives in <c>GeoVehicleTravelMirror</c> (game-bound reflection).
    /// </summary>
    public readonly struct GeoVehicleTravelMeta : IEquatable<GeoVehicleTravelMeta>
    {
        public readonly int OwnerId;        // StableOwnerKey(owner faction def name) — shared with GeoVehiclePos
        public readonly int VehicleId;      // per-faction VehicleID
        public readonly bool Travelling;    // GeoVehicle.Travelling
        public readonly int CurrentSiteId;  // GeoSite.SiteId of CurrentSite, or -1 (null / in transit)
        public readonly int[] DestSiteIds;  // remaining DestinationSites' SiteIds, in order (never null)

        public GeoVehicleTravelMeta(int ownerId, int vehicleId, bool travelling, int currentSiteId, int[] destSiteIds)
        {
            OwnerId = ownerId;
            VehicleId = vehicleId;
            Travelling = travelling;
            CurrentSiteId = currentSiteId;
            DestSiteIds = destSiteIds ?? Array.Empty<int>();
        }

        /// <summary>The composite mirror key (VehicleID alone is only per-faction unique) — shared key-space
        /// with <see cref="GeoVehiclePos"/> so both surfaces resolve the SAME live vehicle.</summary>
        public long Key => GeoVehiclePos.MakeKey(OwnerId, VehicleId);

        public bool Equals(GeoVehicleTravelMeta o)
        {
            if (OwnerId != o.OwnerId || VehicleId != o.VehicleId || Travelling != o.Travelling
                || CurrentSiteId != o.CurrentSiteId) return false;
            if (DestSiteIds.Length != o.DestSiteIds.Length) return false;
            for (int i = 0; i < DestSiteIds.Length; i++)
                if (DestSiteIds[i] != o.DestSiteIds[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is GeoVehicleTravelMeta o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = OwnerId;
                h = (h * 397) ^ VehicleId;
                h = (h * 397) ^ (Travelling ? 1 : 0);
                h = (h * 397) ^ CurrentSiteId;
                foreach (var id in DestSiteIds) h = (h * 397) ^ id;
                return h;
            }
        }

        /// <summary>Order-stable change signature: the HOST skips a vehicle whose travel metadata is unchanged
        /// since the last flush (parked/unchanged = 0 bytes), so 0xA6 only ships on a genuine travel transition
        /// (start / waypoint passed / stop). Distinct from the 0xA5 position signature (which fires ~every poll
        /// while moving) — metadata changes rarely.</summary>
        public static string Signature(GeoVehicleTravelMeta v)
        {
            var sb = new StringBuilder(24 + v.DestSiteIds.Length * 6);
            sb.Append(v.Travelling ? '1' : '0').Append('|').Append(v.CurrentSiteId).Append('|');
            for (int i = 0; i < v.DestSiteIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v.DestSiteIds[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Decoded GeoVehicle travel-metadata batch (Inc4 S2 — surface <see cref="SurfaceIds.GeoVehicleTravel"/> 0xA6):
    /// each CHANGED vehicle's {travelling, currentSiteId, destSiteIds} pushed host→all so the frozen client's
    /// native route-line renderer reads correct nav state. Pure data + wire codec — unit-testable, no game refs.
    ///
    /// Wire payload (inside the 0x67 envelope, surface GeoVehicleTravel):
    ///   [u32 seq][u16 count]{ [i32 OwnerId][i32 VehicleId][u8 travelling][i32 currentSiteId][u16 destCount][i32 destSiteId]* }*
    /// The leading seq is the host's per-surface <see cref="SurfaceSeq"/> value (client drops a stale/dup seq).
    /// </summary>
    public static class GeoVehicleTravelSnapshot
    {
        public static byte[] Encode(uint seq, IList<GeoVehicleTravelMeta> vehicles)
        {
            vehicles = vehicles ?? new List<GeoVehicleTravelMeta>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((ushort)vehicles.Count);
                foreach (var v in vehicles)
                {
                    w.Write(v.OwnerId);
                    w.Write(v.VehicleId);
                    w.Write((byte)(v.Travelling ? 1 : 0));
                    w.Write(v.CurrentSiteId);
                    var dests = v.DestSiteIds ?? Array.Empty<int>();
                    w.Write((ushort)dests.Length);
                    foreach (var id in dests) w.Write(id);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a travel-metadata batch. Returns false (no partial accept) on any truncation — the
        /// reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out uint seq, out List<GeoVehicleTravelMeta> vehicles)
        {
            seq = 0; vehicles = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    var list = new List<GeoVehicleTravelMeta>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int owner = r.ReadInt32();
                        int id = r.ReadInt32();
                        bool travelling = r.ReadByte() != 0;
                        int currentSiteId = r.ReadInt32();
                        int destCount = r.ReadUInt16();
                        // Guard the dest count against the remaining buffer so a corrupt count can't allocate wildly.
                        if ((long)destCount * 4 > ms.Length - ms.Position) return false;
                        var dests = new int[destCount];
                        for (int d = 0; d < destCount; d++) dests[d] = r.ReadInt32();
                        list.Add(new GeoVehicleTravelMeta(owner, id, travelling, currentSiteId, dests));
                    }
                    vehicles = list;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
