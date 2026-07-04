using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Inc4 S2 — one vehicle's mirrored SITE-EXPLORATION PROGRESS: the authoritative fraction of the native
    /// "explore point-of-interest" progress bar the sim-frozen client can NOT derive on its own. The bar is a
    /// per-vehicle <c>GeoActorProgressionVisualController</c> (<c>GeoVehicle._explorationVisuals</c>, instantiated
    /// from <c>GeoVehicleDef.ExplorationVisualsPrefab</c> onto <c>CurrentSite.Surface</c> inside the PRIVATE
    /// <c>GeoVehicle.ExploreCurrentSite(start,end)</c>). Its fill is
    /// <c>Progression = (Timing.Now - Start).TotalMinutes / (End - Start).TotalMinutes</c> — driven by the geoscape
    /// <c>Timing.Now</c>. On a co-op CLIENT the geo Timing is PAUSED (Inc4 S1: <c>Timing.Paused</c>, <c>Now</c>
    /// frozen) AND the client never runs <c>StartExploringCurrentSite</c> (host-authoritative, sim frozen), so the
    /// bar is never instantiated and never advances → absent entirely (Symptom: no exploration progress bar on the
    /// client while the host shows one).
    ///
    /// The HOST reads each exploring vehicle's live bar fill and pushes {exploring, exploredSiteId, progress} host→
    /// client. The CLIENT re-creates the SAME native bar (reusing <c>ExploreCurrentSite</c>) and, because its
    /// <c>Timing.Now</c> is frozen, anchors the bar's Start/End AROUND that frozen now so the native
    /// <c>Progression</c> evaluates to exactly the host fraction — feeding the widget a static-but-per-poll-updated
    /// value (the bar STEPS forward at the mirror poll cadence; the host's advances continuously). Display-only,
    /// never drives the frozen sim (canon: client = pure mirror). Sites are carried by <c>GeoSite.SiteId</c>, the
    /// vehicle by the composite (OwnerId, VehicleId) key shared with <see cref="GeoVehiclePos"/> /
    /// <see cref="GeoVehicleTravelMeta"/>.
    ///
    /// Pure value type + wire codec (no UnityEngine / SyncEngine dependency) so the round-trip and change signature
    /// are directly unit-testable. The engine glue (host read + client apply) lives in
    /// <c>GeoVehicleExploreMirror</c> / <c>GeoVehicleExploreReflection</c> (game-bound reflection).
    /// </summary>
    public readonly struct GeoVehicleExploreMeta : IEquatable<GeoVehicleExploreMeta>
    {
        public readonly int OwnerId;      // StableOwnerKey(owner faction def name) — shared with GeoVehiclePos
        public readonly int VehicleId;    // per-faction VehicleID
        public readonly bool Exploring;   // GeoVehicle.IsExploringSite
        public readonly int SiteId;       // GeoSite.SiteId of the explored CurrentSite, or -1 (not exploring)
        public readonly float Progress;   // bar fill 0..1 (GeoActorProgressionVisualController.Progression, clamped)

        public GeoVehicleExploreMeta(int ownerId, int vehicleId, bool exploring, int siteId, float progress)
        {
            OwnerId = ownerId;
            VehicleId = vehicleId;
            Exploring = exploring;
            SiteId = siteId;
            Progress = progress;
        }

        /// <summary>Composite mirror key — shared key-space with <see cref="GeoVehiclePos"/> so every vehicle
        /// surface resolves the SAME live vehicle.</summary>
        public long Key => GeoVehiclePos.MakeKey(OwnerId, VehicleId);

        /// <summary>Progress quantized to whole percent (0..100) — the granularity the change signature (and the
        /// visible bar) actually cares about, so a sub-1% drift each ~10 Hz poll ships 0 bytes.</summary>
        public static int Percent(float progress)
        {
            int p = (int)Math.Round(progress * 100f);
            return p < 0 ? 0 : (p > 100 ? 100 : p);
        }

        public bool Equals(GeoVehicleExploreMeta o)
            => OwnerId == o.OwnerId && VehicleId == o.VehicleId && Exploring == o.Exploring
               && SiteId == o.SiteId && Progress.Equals(o.Progress);

        public override bool Equals(object obj) => obj is GeoVehicleExploreMeta o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = OwnerId;
                h = (h * 397) ^ VehicleId;
                h = (h * 397) ^ (Exploring ? 1 : 0);
                h = (h * 397) ^ SiteId;
                h = (h * 397) ^ Progress.GetHashCode();
                return h;
            }
        }

        /// <summary>Change signature: the HOST skips a vehicle whose exploration state is unchanged since the last
        /// flush. Quantized on WHOLE-PERCENT progress so a moving bar ships ~100 updates over the whole exploration
        /// (not one per ~10 Hz poll); a non-exploring vehicle collapses to a single "off" token (0 bytes at rest).</summary>
        public static string Signature(GeoVehicleExploreMeta v)
            => v.Exploring ? (v.SiteId + "|" + Percent(v.Progress)) : "off";
    }

    /// <summary>
    /// Decoded GeoVehicle exploration-progress batch (Inc4 S2 — surface <see cref="SurfaceIds.GeoVehicleExplore"/>
    /// 0xA7): each CHANGED vehicle's {exploring, siteId, progress} pushed host→all so the frozen client renders the
    /// native site-exploration progress bar. Pure data + wire codec — unit-testable, no game refs.
    ///
    /// Wire payload (inside the 0x67 envelope, surface GeoVehicleExplore):
    ///   [u32 seq][u16 count]{ [i32 OwnerId][i32 VehicleId][u8 exploring][i32 SiteId][f32 progress] }*
    /// The leading seq is the host's per-surface <see cref="SurfaceSeq"/> value (client drops a stale/dup seq).
    /// </summary>
    public static class GeoVehicleExploreSnapshot
    {
        public static byte[] Encode(uint seq, IList<GeoVehicleExploreMeta> vehicles)
        {
            vehicles = vehicles ?? new List<GeoVehicleExploreMeta>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write((ushort)vehicles.Count);
                foreach (var v in vehicles)
                {
                    w.Write(v.OwnerId);
                    w.Write(v.VehicleId);
                    w.Write((byte)(v.Exploring ? 1 : 0));
                    w.Write(v.SiteId);
                    w.Write(v.Progress);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode an exploration-progress batch. Returns false (no partial accept) on any truncation — the
        /// reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out uint seq, out List<GeoVehicleExploreMeta> vehicles)
        {
            seq = 0; vehicles = null;
            if (data == null) return false;
            // Fixed 17 bytes per record (i32 owner + i32 vehId + u8 exploring + i32 siteId + f32 progress) — guard
            // the declared count against the remaining buffer so a corrupt count can't allocate wildly.
            const int recordBytes = 4 + 4 + 1 + 4 + 4;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    seq = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    if ((long)n * recordBytes > ms.Length - ms.Position) return false;
                    var list = new List<GeoVehicleExploreMeta>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int owner = r.ReadInt32();
                        int id = r.ReadInt32();
                        bool exploring = r.ReadByte() != 0;
                        int siteId = r.ReadInt32();
                        float progress = r.ReadSingle();
                        list.Add(new GeoVehicleExploreMeta(owner, id, exploring, siteId, progress));
                    }
                    vehicles = list;
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
