using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
        // ─── tac.vision (host→all, Inc Vision) ────────────────────────────
        // A full RECONCILE snapshot of the player/viewer faction's KnownActors: which enemy/actor netIds the
        // host currently knows, and at what state (2=Revealed/red — seen+LOS; 1=Located/grey — position known).
        // The host pushes this on every FactionKnowledgeChangedEvent for the player faction; the client RECONCILES
        // its mirror of that faction's vision to the snapshot (set listed, forget absent). Self-contained tactical
        // seq (last-writer-wins) like tac.move / tac.turn. Engine-free codec → unit-testable.
        //   [seq:u32][viewerFactionIndex:i32][count:i32]  then per entry: [netId:i32][knownState:i32]
        public struct VisionEntry
        {
            public int NetId;
            public int KnownState;   // 2 = Revealed (red), 1 = Located (grey)
            public VisionEntry(int netId, int knownState) { NetId = netId; KnownState = knownState; }
        }

        public struct VisionSnapshot
        {
            public uint Seq;
            public int ViewerFactionIndex;
            public List<VisionEntry> Entries;
            public VisionSnapshot(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
            { Seq = seq; ViewerFactionIndex = viewerFactionIndex; Entries = entries ?? new List<VisionEntry>(); }
        }

        /// <summary>Wire state values (also the engine→client mapping). Higher = stronger knowledge.</summary>
        public const int VisionStateRevealed = 2;   // RED  — seen + line of sight
        public const int VisionStateLocated = 1;     // GREY — position known, no LOS

        public static byte[] EncodeVision(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
        {
            entries = entries ?? new List<VisionEntry>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(viewerFactionIndex);
                w.Write(entries.Count);
                foreach (var e in entries) { w.Write(e.NetId); w.Write(e.KnownState); }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeVision(byte[] data, out VisionSnapshot snapshot)
        {
            snapshot = default(VisionSnapshot);
            // Minimum: seq + viewerFactionIndex + count = 12 bytes.
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int viewerFactionIndex = r.ReadInt32();
                    int count = r.ReadInt32();
                    if (count < 0 || count > 4096) return false;
                    // Each entry is fixed 8 bytes (2*i32); guard the count against the remaining buffer so a
                    // corrupt huge count can't allocate wildly (mirrors TacticalDeployCodec's actor-table guard).
                    if ((long)count * 8 > ms.Length - ms.Position) return false;
                    var entries = new List<VisionEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int netId = r.ReadInt32();
                        int state = r.ReadInt32();
                        entries.Add(new VisionEntry(netId, state));
                    }
                    snapshot = new VisionSnapshot(seq, viewerFactionIndex, entries);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
