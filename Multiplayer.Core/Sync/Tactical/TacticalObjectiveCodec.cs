using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the LIVE mission-objective mirror surface <c>tac.objective</c>
    /// (0x99, host→all, RELIABLE). Closes the tactical audit gap D21 "in-battle objectives do not sync":
    /// scripted/custom missions (story, TFTV) flip <c>FactionObjective</c> state MID-battle (kill target,
    /// reach zone, defend N turns, activate console) — TS4 (0x95) repaints objectives only at mission END,
    /// so the client objective HUD stays stale for the whole battle. This surface mirrors the host player
    /// faction's objective STATE + PROGRESS live, and carries an ADD record for objectives that scripts
    /// append mid-mission (the GeoMissionRecord P1 add-record pattern).
    ///
    /// KEYING: objective lists are built from the SAME mission def (or the same save on a reload) on both
    /// sides at mission start → records are keyed by ORDINAL index with a CLASS-NAME sanity discriminator;
    /// a class mismatch (index drift / unknown TFTV subclass) degrades to a per-record skip, never a
    /// mis-stamp. PROGRESS = the subclass's int fields (TurnsRemaining, _picked, CurrentIntegrity, …) in a
    /// deterministic order — value-stamped only, both sides run the same assembly.
    ///
    /// WIRE (host→all, carries LiveSeq):
    ///   [seq:u32][recCount:u16] { [recLen:u16][record] }*
    ///   record: [kind:u8 state=0/add=1][index:u16][state:u8]
    ///           [clsLen:u8][className:utf8][descLen:u16][descKey:utf8]
    ///           [progCount:u8]{ [i32] }*
    /// <c>recLen</c> frames each record → an unknown kind or a longer newer record is SKIPPED cleanly
    /// (forward-compat), exactly like <see cref="TacticalStructDamageCodec"/>. Truncation / a count or
    /// length exceeding the remaining buffer → clean <c>false</c> (no partial accept).
    /// </summary>
    public static class TacticalObjectiveCodec
    {
        /// <summary>Record kind (u8). STATE = value-stamp an existing (index-keyed) objective;
        /// ADD = a mid-mission scripted addition the client mirror-appends (resolved via the shared
        /// NextOnSuccess/NextOnFail def graph by class name + description key).</summary>
        public const byte KindState = 0;
        public const byte KindAdd = 1;

        /// <summary>Safety cap on progress ints per record (u8 on the wire; real objectives carry &lt;10).</summary>
        public const int MaxProgress = 32;

        /// <summary>One objective record (both kinds share the body; <see cref="Kind"/> picks the apply).</summary>
        public sealed class ObjectiveRec
        {
            public byte Kind;
            public int Index;          // ordinal index in the host player faction's objective list
            public byte State;         // FactionObjectiveState (InProgress=0/Achieved=1/Failed=2)
            public string ClassName;   // concrete FactionObjective class name (sanity discriminator)
            public string DescKey;     // Description LocalizationKey ("" when none) — add-resolution discriminator
            public int[] Progress;     // subclass int-field values, deterministic order

            public ObjectiveRec() { ClassName = ""; DescKey = ""; Progress = new int[0]; }

            public ObjectiveRec(byte kind, int index, byte state, string className, string descKey, int[] progress)
            {
                Kind = kind;
                Index = index;
                State = state;
                ClassName = className ?? "";
                DescKey = descKey ?? "";
                Progress = progress ?? new int[0];
            }
        }

        /// <summary>The decoded 0x99 payload.</summary>
        public sealed class ObjectiveBatch
        {
            public uint Seq;
            public List<ObjectiveRec> Records;
            public ObjectiveBatch() { Records = new List<ObjectiveRec>(); }
            public ObjectiveBatch(uint seq, List<ObjectiveRec> records)
            {
                Seq = seq;
                Records = records ?? new List<ObjectiveRec>();
            }
        }

        // ─── Encode / Decode ─────────────────────────────────────────────────

        public static byte[] Encode(ObjectiveBatch batch)
        {
            var records = (batch != null ? batch.Records : null) ?? new List<ObjectiveRec>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(batch != null ? batch.Seq : 0u);
                w.Write((ushort)records.Count);
                foreach (var r in records)
                {
                    byte[] body = EncodeRecord(r);
                    w.Write((ushort)body.Length);
                    w.Write(body);
                }
                return ms.ToArray();
            }
        }

        private static byte[] EncodeRecord(ObjectiveRec r)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(r.Kind);
                w.Write((ushort)r.Index);
                w.Write(r.State);

                var cls = Encoding.UTF8.GetBytes(r.ClassName ?? "");
                if (cls.Length > byte.MaxValue) { var t = new byte[byte.MaxValue]; System.Array.Copy(cls, t, t.Length); cls = t; }
                w.Write((byte)cls.Length);
                if (cls.Length > 0) w.Write(cls);

                var desc = Encoding.UTF8.GetBytes(r.DescKey ?? "");
                if (desc.Length > ushort.MaxValue) { var t = new byte[ushort.MaxValue]; System.Array.Copy(desc, t, t.Length); desc = t; }
                w.Write((ushort)desc.Length);
                if (desc.Length > 0) w.Write(desc);

                var prog = r.Progress ?? new int[0];
                int n = prog.Length > MaxProgress ? MaxProgress : prog.Length;
                w.Write((byte)n);
                for (int i = 0; i < n; i++) w.Write(prog[i]);
                return ms.ToArray();
            }
        }

        /// <summary>Decode a 0x99 payload. Returns false (no partial accept) on truncation or a count/length
        /// exceeding the remaining buffer. Per-record <c>recLen</c> framing: bytes beyond the known fields of a
        /// record are ignored (forward-compat), and trailing bytes after the last record are ignored.</summary>
        public static bool TryDecode(byte[] data, out ObjectiveBatch batch)
        {
            batch = null;
            if (data == null || data.Length < 4 + 2) return false;   // u32 seq + u16 recCount
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int count = r.ReadUInt16();
                    var records = new List<ObjectiveRec>(count);
                    for (int i = 0; i < count; i++)
                    {
                        if (ms.Length - ms.Position < 2) return false;
                        int recLen = r.ReadUInt16();
                        if (ms.Length - ms.Position < recLen) return false;
                        long recEnd = ms.Position + recLen;

                        var rec = DecodeRecord(r, recEnd);
                        if (rec == null) return false;   // corrupt record body → drop the whole batch
                        records.Add(rec);

                        ms.Position = recEnd;   // framed skip: ignore unknown trailing record bytes
                    }
                    batch = new ObjectiveBatch(seq, records);
                    return true;   // trailing bytes after the last record are intentionally ignored
                }
            }
            catch { return false; }
        }

        private static ObjectiveRec DecodeRecord(BinaryReader r, long recEnd)
        {
            var ms = r.BaseStream;
            if (recEnd - ms.Position < 1 + 2 + 1 + 1) return null;   // kind + index + state + clsLen
            byte kind = r.ReadByte();
            int index = r.ReadUInt16();
            byte state = r.ReadByte();

            int clsLen = r.ReadByte();
            if (recEnd - ms.Position < clsLen) return null;
            string cls = clsLen > 0 ? Encoding.UTF8.GetString(r.ReadBytes(clsLen)) : "";

            if (recEnd - ms.Position < 2) return null;
            int descLen = r.ReadUInt16();
            if (recEnd - ms.Position < descLen) return null;
            string desc = descLen > 0 ? Encoding.UTF8.GetString(r.ReadBytes(descLen)) : "";

            if (recEnd - ms.Position < 1) return null;
            int progCount = r.ReadByte();
            if (recEnd - ms.Position < (long)progCount * 4) return null;
            var prog = new int[progCount];
            for (int i = 0; i < progCount; i++) prog[i] = r.ReadInt32();

            return new ObjectiveRec(kind, index, state, cls, desc, prog);
        }
    }
}
