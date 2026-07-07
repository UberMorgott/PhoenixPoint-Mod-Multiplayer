namespace Multiplayer.Util
{
    /// <summary>
    /// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) — byte-for-byte the algorithm that has always
    /// guarded the save-transfer blob. Moved here from <c>SaveTransferCoordinator</c> (which now delegates)
    /// so the Inc5 divergence probe (<c>CrcSubsetCrc</c>) reuses the SAME implementation — one polynomial,
    /// one table, one truth. Pure BCL; pinned by the standard check vector CRC32("123456789") = 0xCBF43926.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] _table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        /// <summary>CRC-32 of the whole buffer. Null is treated as empty (CRC 0x00000000).</summary>
        public static uint Compute(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            if (data != null)
                for (var i = 0; i < data.Length; i++)
                    crc = _table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
