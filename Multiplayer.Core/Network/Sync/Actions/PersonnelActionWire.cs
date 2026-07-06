using System.Collections.Generic;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Shared wire helpers for the PS4 personnel client-edit intents: a length-prefixed def-guid list
    /// (u16 count, then each guid as a length-prefixed UTF8 string via <see cref="BinaryWriter.Write(string)"/>).
    /// Payloads key by GeoUnitId + stable def guids only — never object graphs (spec §5.2).
    /// </summary>
    public static class PersonnelActionWire
    {
        public static void WriteGuids(BinaryWriter w, string[] guids)
        {
            guids = guids ?? System.Array.Empty<string>();
            w.Write((ushort)guids.Length);
            foreach (var g in guids) w.Write(g ?? string.Empty);
        }

        public static string[] ReadGuids(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var guids = new string[n];
            for (int i = 0; i < n; i++) guids[i] = r.ReadString();
            return guids;
        }
    }
}
