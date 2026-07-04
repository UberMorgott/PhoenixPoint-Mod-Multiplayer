using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Decoded research snapshot: completed research ids + the ordered queue of (id, progress) + the set
    /// of non-completed, non-queued elements the host has Revealed/Unlocked (each with its authoritative
    /// <c>ResearchState</c> byte). This is the pure data + wire codec for the research state channel (#2) —
    /// kept free of any <c>IStateChannel</c>/<c>SyncEngine</c> dependency so it is directly unit-testable.
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 completedCount]{[u16 idLen][id utf8]}*
    ///   [u16 queueCount]{[u16 idLen][id utf8][f32 progress]}*
    ///   [u16 stateCount]{[u16 idLen][id utf8][u8 state]}*        ← OPTIONAL trailing block (v2)
    ///
    /// The third (state) block is TRAILING + length-tolerant: a v1 payload (no block) is read as zero
    /// states (the decoder only reads the block when bytes remain), and a v1 decoder reading a v2 payload
    /// harmlessly ignores the trailing bytes. <c>state</c> mirrors <c>ResearchState</c>
    /// (Hidden=0, Revealed=1, Unlocked=2, Completed=3); only Revealed/Unlocked are carried here (completed
    /// elements live in <see cref="Completed"/>, queued elements in <see cref="Queue"/>). This lets the
    /// client mirror the host's Available (left) list reactively: Revealed shows without the "research now"
    /// affordance, Unlocked shows with it.
    /// </summary>
    public sealed class ResearchSnapshot
    {
        public readonly List<string> Completed = new List<string>();
        public readonly List<(string id, float progress)> Queue = new List<(string, float)>();
        // Non-completed, non-queued elements the host has Revealed/Unlocked, with the authoritative state byte.
        public readonly List<(string id, byte state)> States = new List<(string, byte)>();

        public static byte[] Encode(ResearchSnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Completed.Count);
                foreach (var id in snap.Completed) WriteStr(w, id);
                w.Write((ushort)snap.Queue.Count);
                foreach (var (id, progress) in snap.Queue)
                {
                    WriteStr(w, id);
                    w.Write(progress);
                }
                // v2 trailing block. Always written (empty → just a u16 zero) so the wire is stable.
                w.Write((ushort)snap.States.Count);
                foreach (var (id, state) in snap.States)
                {
                    WriteStr(w, id);
                    w.Write(state);
                }
                return ms.ToArray();
            }
        }

        public static ResearchSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new ResearchSnapshot();
                    int nc = r.ReadUInt16();
                    for (int i = 0; i < nc; i++) snap.Completed.Add(ReadStr(r));
                    int nq = r.ReadUInt16();
                    for (int i = 0; i < nq; i++)
                    {
                        string id = ReadStr(r);
                        float progress = r.ReadSingle();
                        snap.Queue.Add((id, progress));
                    }
                    // v2 trailing block — read ONLY if bytes remain, so a v1 payload (no block) decodes as
                    // zero states rather than throwing. A partial/garbled trailing block still throws via
                    // ReadStr's length check → whole payload rejected (null), preserving the all-or-nothing
                    // contract callers rely on.
                    if (ms.Position < ms.Length)
                    {
                        int nsBytes = r.ReadUInt16();
                        for (int i = 0; i < nsBytes; i++)
                        {
                            string id = ReadStr(r);
                            byte state = r.ReadByte();
                            snap.States.Add((id, state));
                        }
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (ResearchChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
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
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw), so a
            // truncated payload would decode to garbage rather than being rejected. Verify the full
            // length was read; otherwise throw → caught by Decode's try/catch → null (rejected).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("ResearchSnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
