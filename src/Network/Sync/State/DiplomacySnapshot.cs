using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Decoded faction DIPLOMACY snapshot: the per-relation reputation integer for every
    /// faction-to-faction relation, keyed by <c>(ownerFactionDef guid, withPartyDef guid)</c>. The host
    /// applies <c>ResearchElement.Complete</c> → <c>RewardReputation()</c> →
    /// <c>PartyDiplomacy.ModifyDiplomacy</c> (it bumps the reputation of OTHER factions toward the player
    /// faction), which is never mirrored today. This is a VALUE-ONLY mirror, like the wallet echo: the
    /// client overwrites each relation's stored int to the host value (no event cascade).
    ///
    /// This is the pure data + wire codec for the diplomacy state channel (#4) — free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="ResearchSnapshot"/>).
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 count]{[u16 ownerLen][ownerGuid utf8][u16 withLen][withGuid utf8][i32 value]}*
    ///
    /// Only relations whose <c>WithParty</c> key is a Def (a <c>PPFactionDef</c>, which has a stable Guid)
    /// are carried; non-Def keys (e.g. haven-leader keys) are skipped host-side — faction-vs-faction
    /// reputation is the set the research reward path mutates.
    /// </summary>
    public sealed class DiplomacySnapshot
    {
        public readonly List<(string owner, string with, int value)> Relations = new List<(string, string, int)>();

        public static byte[] Encode(DiplomacySnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Relations.Count);
                foreach (var (owner, with, value) in snap.Relations)
                {
                    WriteStr(w, owner);
                    WriteStr(w, with);
                    w.Write(value);
                }
                return ms.ToArray();
            }
        }

        public static DiplomacySnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new DiplomacySnapshot();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        string owner = ReadStr(r);
                        string with = ReadStr(r);
                        int value = r.ReadInt32();
                        snap.Relations.Add((owner, with, value));
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (DiplomacyChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw); verify the
            // full length was read, else throw → caught by Decode → null (rejected, not garbage).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("DiplomacySnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
