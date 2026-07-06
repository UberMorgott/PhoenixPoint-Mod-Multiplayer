using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) wire codec for the mirrored resource-harvest FLOAT (Batch-2 P6 of the 2026-07-05
    /// unified popup-mirror spec). Native rail: <c>GeoscapeView.PxFaction_OnResourcesHarvested</c>
    /// (GeoscapeView.cs:1956) → <c>GeoSite.ShowResourceHarvested(ResourcePack)</c> (GeoSite.cs:931), which
    /// renders ONLY the FIRST ResourceUnit's Type + RoundedValue as a site-anchored float. The host mirrors
    /// exactly that tuple: {occId, siteId, resourceType(raw enum int), value(float)} — PURE ids, no objects.
    /// Rides the unified 0x67 envelope on <c>SurfaceIds.GeoHarvestFloat</c> (0xA8), display-only: the client
    /// replays its own native <c>ShowResourceHarvested</c> at the same site — it NEVER credits resources
    /// (the wallet 0xA0 rail stays the one silent balance writer; one writer per field, spec §4).
    ///
    /// Wire: [occId:u16][siteId:i32][resourceType:i32][value:f32].
    /// occId is a host-monotonic occurrence counter (same pattern as EventOccurrenceIds) so the STUN reliable
    /// transport's deliberate double-send dedups to ONE float (<see cref="HarvestFloatDedup"/>).
    /// </summary>
    public static class HarvestFloatCodec
    {
        public static byte[] Encode(ushort occId, int siteId, int resourceType, float value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occId);
                w.Write(siteId);
                w.Write(resourceType);
                w.Write(value);
                return ms.ToArray();
            }
        }

        public static bool TryDecode(byte[] data, out ushort occId, out int siteId, out int resourceType, out float value)
        {
            occId = 0; siteId = -1; resourceType = 0; value = 0f;
            if (data == null || data.Length < sizeof(ushort) + sizeof(int) + sizeof(int) + sizeof(float)) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    occId = r.ReadUInt16();
                    siteId = r.ReadInt32();
                    resourceType = r.ReadInt32();
                    value = r.ReadSingle();
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// HOST: monotonic per-process occurrence-id authority for harvest floats (u16, wraps naturally).
    /// Mirrors <c>EventOccurrenceIds</c>' counter contract: never reset in production; the client dedup window
    /// (<see cref="HarvestFloatDedup"/>) is far smaller than the 65536 wrap so a wrapped id can never alias a
    /// still-remembered one.
    /// </summary>
    public static class HarvestFloatIds
    {
        private static int _counter;

        public static ushort Next() => (ushort)Interlocked.Increment(ref _counter);

        /// <summary>Test-only: reset the counter (no production callers).</summary>
        public static void ResetForTests() => _counter = 0;
    }

    /// <summary>
    /// CLIENT: bounded recent-occurrence dedup set for harvest floats — the STUN reliable transport
    /// deliberately sends twice, and a doubled float would visibly stutter/stack at the site. Remembers the
    /// last <see cref="Capacity"/> occurrence ids in FIFO order; a repeat inside the window is a no-op.
    /// Floats are cosmetic + ephemeral, so eviction (an id older than the window re-applying) is harmless by
    /// construction — the transport double-send is always back-to-back, never Capacity deliveries apart.
    /// Reset at the save-transfer boundary with the other mirror state.
    /// </summary>
    public sealed class HarvestFloatDedup
    {
        public const int Capacity = 64;

        private readonly HashSet<ushort> _seen = new HashSet<ushort>();
        private readonly Queue<ushort> _order = new Queue<ushort>();

        /// <summary>True iff <paramref name="occId"/> was NOT seen within the recent window (it is then recorded).</summary>
        public bool ShouldApply(ushort occId)
        {
            if (_seen.Contains(occId)) return false;
            _seen.Add(occId);
            _order.Enqueue(occId);
            while (_order.Count > Capacity) _seen.Remove(_order.Dequeue());
            return true;
        }

        /// <summary>Boundary reset (save-transfer / reload).</summary>
        public void Reset()
        {
            _seen.Clear();
            _order.Clear();
        }
    }
}
