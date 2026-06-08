using System;
using System.IO;
using System.IO.Compression;

namespace Multipleer.Serialization
{
    public static class StateSnapshot
    {
        // ─── Tactical State Helpers ───────────────────────────────────────
        // These will serialize the current tactical mission state for
        // initial sync when a client joins mid-mission.

        public static byte[] CompressState(byte[] rawData)
        {
            if (rawData == null || rawData.Length == 0)
                return Array.Empty<byte>();

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    gzip.Write(rawData, 0, rawData.Length);
                }
                return output.ToArray();
            }
        }

        public static byte[] DecompressState(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();

            using (var input = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        // ─── Delta Compression ────────────────────────────────────────────
        // Simple version: send full state. Future: compute delta.

        public static byte[] CreateFullSnapshot(byte[] serializedGameState)
        {
            // Wrap with metadata
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(DateTime.UtcNow.Ticks); // snapshot timestamp
                bw.Write(serializedGameState.Length);
                bw.Write(serializedGameState);
                return ms.ToArray();
            }
        }
    }
}
