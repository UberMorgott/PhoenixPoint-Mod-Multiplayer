using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE wire codec for the <c>tac.load.phase</c> heartbeat (surface 0x9C): a host→ALL loading-PROGRESS
    /// ping shown on the CLIENT while the host loads a level and the client would otherwise sit on a frozen
    /// screen with no feedback — the geoscape→tactical ride, plus stage-1 of the tactical→geoscape return
    /// (the return's DOWNLOAD stage is already owned by <c>SaveTransferCoordinator</c>). It carries NO game
    /// state — DISPLAY ONLY — so it rides the tactical fast-path with no seq/dedup wrapper (the client just
    /// eases its native bar toward the latest fraction; a dropped or out-of-order ping is harmless).
    ///
    /// Wire (engine-free, BinaryWriter/Reader only → unit-testable): [phase:u8][progress01:f32].
    ///   phase    — load-stage discriminator; only 0 (HostLoading) is defined today (a byte leaves room to
    ///              grow without a wire break).
    ///   progress — 0..1 load fraction; clamped on BOTH encode and decode (a NaN/out-of-range value coerces
    ///              to a clean clamped fraction rather than corrupting the bar).
    /// </summary>
    public static class TacLoadPhaseCodec
    {
        /// <summary>The only defined phase today: the host is loading the destination level.</summary>
        public const byte PhaseHostLoading = 0;

        /// <summary>The fixed payload size in bytes: 1 (phase) + 4 (float progress).</summary>
        public const int Size = 5;

        /// <summary>Encode a load-phase heartbeat. <paramref name="progress01"/> is clamped to 0..1.</summary>
        public static byte[] Encode(byte phase, float progress01)
        {
            progress01 = Clamp01(progress01);
            using (var ms = new MemoryStream(Size))
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(phase);
                w.Write(progress01);
                return ms.ToArray();
            }
        }

        /// <summary>Decode a load-phase heartbeat. Returns false (no partial accept) on truncation; a
        /// NaN/out-of-range progress is coerced to a clamped 0..1 value. Trailing bytes past the fixed
        /// payload are ignored (forward-tolerant).</summary>
        public static bool TryDecode(byte[] data, out byte phase, out float progress01)
        {
            phase = 0;
            progress01 = 0f;
            if (data == null || data.Length < Size) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    phase = r.ReadByte();
                    progress01 = Clamp01(r.ReadSingle());
                    return true;
                }
            }
            catch { return false; }
        }

        // Clamp to 0..1, coercing NaN to 0 (float comparisons against NaN are all false, so the < branch
        // catches it). No UnityEngine.Mathf here — this assembly is strictly BCL-only.
        private static float Clamp01(float v)
        {
            if (!(v > 0f)) return 0f;   // v <= 0 OR NaN
            return v > 1f ? 1f : v;
        }
    }
}
