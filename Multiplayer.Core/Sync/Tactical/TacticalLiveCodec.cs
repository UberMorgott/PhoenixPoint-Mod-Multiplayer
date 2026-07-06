using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codecs + seq/dedup for the LIVE tactical outcome rail (spec §5, Inc 2/4):
    /// soldier MOVE + END-TURN. BinaryWriter/Reader only, so it unit-tests in isolation alongside
    /// <see cref="TacticalDeployCodec"/>. The engine glue (<c>TacticalMoveSync</c> / <c>TacticalTurnSync</c>)
    /// binds the live game types via reflection and is NOT in the test assembly.
    ///
    /// Why a SELF-CONTAINED tactical seq (not the geoscape <c>SequenceTracker</c>): the deploy rail already
    /// rides the SurfaceRouter tactical FAST-PATH that deliberately bypasses the action relay's shared global
    /// seq (a request-free host push has no correct seq there, and reusing it would poison post-mission
    /// geoscape action ordering). The live outcome rail rides the SAME fast-path, so it carries its OWN
    /// monotonic seq stamped by the host and a per-surface last-writer-wins guard on the client.
    ///
    /// Frames:
    ///   tac.intent.move      [netId:i32][x:f32][y:f32][z:f32][nonce:u32]                  (client→host)
    ///   tac.move.start       [seq:u32][netId:i32][x:f32][y:f32][z:f32]                    (host→all)
    ///   tac.move             [seq:u32][netId:i32][x:f32][y:f32][z:f32][stopReason:i32]    (host→all)
    ///   tac.intent.endturn   [nonce:u32]                                                  (client→host)
    ///   tac.turn             [seq:u32][currentFactionIndex:i32][turnNumber:i32][factionDefGuid:string]
    /// </summary>
    public static partial class TacticalLiveCodec
    {
        // ─── tac.intent.move (client→host) ────────────────────────────────
        public struct MoveIntent
        {
            public int NetId;
            public float X, Y, Z;
            public uint Nonce;
            public MoveIntent(int netId, float x, float y, float z, uint nonce)
            { NetId = netId; X = x; Y = y; Z = z; Nonce = nonce; }
        }

        public static byte[] EncodeMoveIntent(int netId, float x, float y, float z, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveIntent(byte[] data, out MoveIntent intent)
        {
            intent = default(MoveIntent);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    uint nonce = r.ReadUInt32();
                    intent = new MoveIntent(netId, x, y, z, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move.start (host→all) ────────────────────────────────────
        // Broadcast at the MOMENT the host BEGINS a move (its own click or a relayed client intent), so the
        // client mirror animates CONCURRENTLY with the host instead of waiting for the END outcome. Mirrors
        // the EncodeMove layout MINUS stopReason (the destination is the requested cell, not the landed one).
        public struct MoveStart
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public MoveStart(uint seq, int netId, float x, float y, float z)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; }
        }

        public static byte[] EncodeMoveStart(uint seq, int netId, float x, float y, float z)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveStart(byte[] data, out MoveStart start)
        {
            start = default(MoveStart);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    start = new MoveStart(seq, netId, x, y, z);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move (host→all) ──────────────────────────────────────────
        public struct MoveOutcome
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public int StopReason;
            public MoveOutcome(uint seq, int netId, float x, float y, float z, int stopReason)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; StopReason = stopReason; }
        }

        public static byte[] EncodeMove(uint seq, int netId, float x, float y, float z, int stopReason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(stopReason);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMove(byte[] data, out MoveOutcome outcome)
        {
            outcome = default(MoveOutcome);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    int stop = r.ReadInt32();
                    outcome = new MoveOutcome(seq, netId, x, y, z, stop);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.intent.endturn (client→host) ─────────────────────────────
        public static byte[] EncodeEndTurnIntent(uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEndTurnIntent(byte[] data, out uint nonce)
        {
            nonce = 0;
            if (data == null || data.Length < 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.turn (host→all) ──────────────────────────────────────────
        public struct TurnOutcome
        {
            public uint Seq;
            public int CurrentFactionIndex;
            public int TurnNumber;
            public string FactionDefGuid;
            public TurnOutcome(uint seq, int idx, int turnNumber, string guid)
            { Seq = seq; CurrentFactionIndex = idx; TurnNumber = turnNumber; FactionDefGuid = guid ?? ""; }
        }

        public static byte[] EncodeTurn(uint seq, int currentFactionIndex, int turnNumber, string factionDefGuid)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(currentFactionIndex);
                w.Write(turnNumber);
                w.Write(factionDefGuid ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryDecodeTurn(byte[] data, out TurnOutcome outcome)
        {
            outcome = default(TurnOutcome);
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int idx = r.ReadInt32();
                    int turn = r.ReadInt32();
                    string guid = r.ReadString();
                    outcome = new TurnOutcome(seq, idx, turn, guid);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
