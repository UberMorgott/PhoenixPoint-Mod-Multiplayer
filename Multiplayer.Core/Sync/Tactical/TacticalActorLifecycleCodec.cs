using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE wire codec for the mid-battle actor LIFECYCLE surfaces (spec TS1): <c>tac.actor.spawn</c> (0x92,
    /// host→all) and <c>tac.actor.despawn</c> (0x93, host→all). Engine-free (BinaryWriter/Reader only) so it
    /// unit-tests in isolation, exactly like <see cref="TacticalDeployCodec"/>.
    ///
    /// SPAWN wire:
    ///   [seq:u32][netId:i32][factionIndex:i32][posX:f32][posY:f32][posZ:f32]
    ///   [createLen:i32][createBlob:N]   — game-Serializer bytes of the actor's <c>ActorCreateData</c>
    ///                                      (carries the runtime ComponentSetDef BY VALUE — see below)
    ///   [instLen:i32][instBlob:N]       — game-Serializer bytes of the actor's <c>SerializationData</c>
    ///                                      (ActorInstanceData: health/statuses/equipment/faction/pos)
    ///
    /// R1 (componentSetDef resolution) — the def a spawned actor was built from is a RUNTIME def
    /// (<c>GenerateInstanceComponentSetDef → DefRepository.CreateRuntimeDef</c>), whose guid is NOT resolvable
    /// on the client. So TS1 does NOT ship a componentSetDefGuid string (the spec's provisional field); it ships
    /// the actor's <c>ActorCreateData</c> serialized with <c>BaseDef.SerializeDefContents=true</c> — the native
    /// save mechanism that embeds a runtime def's full members BY VALUE. The client's game Serializer then
    /// reconstructs the ComponentSetDef locally (BaseDef.ResolveOrCreateBaseDef → CreateRuntimeDef + members),
    /// exactly as a mid-battle-saved game reloads a spawned Pandoran. The blobs themselves are produced/consumed
    /// by the engine <c>Serializer</c> in <see cref="Multiplayer.Sync.Tactical.TacticalActorLifecycleSync"/>;
    /// this codec only frames them + the header. <c>factionIndex</c>/<c>pos</c> are diagnostic / fallback keys
    /// (the authoritative faction + position ride the instance-data blob).
    ///
    /// DESPAWN wire: [seq:u32][netId:i32][reason:u8]. Reason is diagnostic only (0 removed / 1 evacuated /
    /// 2 morphed / 3 retrieved / 4 refreshed); the client behaviour is always "remove the mirror actor +
    /// registry cleanup".
    /// Both codecs reject truncation with a clean <c>false</c> (no partial accept) — the reliable transport
    /// guarantees full delivery, so a short buffer is a drop.
    /// </summary>
    public static class TacticalActorLifecycleCodec
    {
        /// <summary>Despawn reason (diagnostic only — the client always just removes the mirror).</summary>
        public const byte ReasonRemoved   = 0;
        public const byte ReasonEvacuated = 1;
        public const byte ReasonMorphed   = 2;
        public const byte ReasonRetrieved = 3;
        /// <summary>gap-turret-crate-loot: NOT a real removal — the first half of a host CONTENT-REFRESH
        /// (despawn + immediate re-spawn at the SAME netId) that re-sends a registered ground container's blob
        /// after its inventory changed post-EnterPlay (a dropped item lands AFTER the 0x92 spawn serialized an
        /// empty container — TacticalItem.Drop:523→532). Diagnostic; the client removal is identical.</summary>
        public const byte ReasonRefreshed = 4;

        // ─── Spawn (0x92) ─────────────────────────────────────────────────

        public sealed class SpawnPayload
        {
            public uint Seq;
            public int NetId;
            public int FactionIndex;
            public float PosX, PosY, PosZ;
            public byte[] CreateBlob;   // Serializer bytes of ActorCreateData (ComponentSetDef embedded by value)
            public byte[] InstBlob;     // Serializer bytes of ActorInstanceData (state)

            public SpawnPayload() { }

            public SpawnPayload(uint seq, int netId, int factionIndex, float px, float py, float pz,
                byte[] createBlob, byte[] instBlob)
            {
                Seq = seq; NetId = netId; FactionIndex = factionIndex;
                PosX = px; PosY = py; PosZ = pz;
                CreateBlob = createBlob ?? new byte[0];
                InstBlob = instBlob ?? new byte[0];
            }
        }

        public static byte[] EncodeSpawn(SpawnPayload p)
        {
            var create = p.CreateBlob ?? new byte[0];
            var inst = p.InstBlob ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p.Seq);
                w.Write(p.NetId);
                w.Write(p.FactionIndex);
                w.Write(p.PosX);
                w.Write(p.PosY);
                w.Write(p.PosZ);
                w.Write(create.Length);
                if (create.Length > 0) w.Write(create);
                w.Write(inst.Length);
                if (inst.Length > 0) w.Write(inst);
                return ms.ToArray();
            }
        }

        /// <summary>Decode a spawn payload. Returns false (no partial accept) on any truncation.</summary>
        public static bool TryDecodeSpawn(byte[] data, out SpawnPayload payload)
        {
            payload = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    int faction = r.ReadInt32();
                    float px = r.ReadSingle();
                    float py = r.ReadSingle();
                    float pz = r.ReadSingle();

                    int createLen = r.ReadInt32();
                    if (createLen < 0 || ms.Length - ms.Position < createLen) return false;
                    var create = createLen > 0 ? r.ReadBytes(createLen) : new byte[0];
                    if (create.Length != createLen) return false;

                    int instLen = r.ReadInt32();
                    if (instLen < 0 || ms.Length - ms.Position < instLen) return false;
                    var inst = instLen > 0 ? r.ReadBytes(instLen) : new byte[0];
                    if (inst.Length != instLen) return false;

                    payload = new SpawnPayload(seq, netId, faction, px, py, pz, create, inst);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Despawn (0x93) ───────────────────────────────────────────────

        public struct DespawnPayload
        {
            public uint Seq;
            public int NetId;
            public byte Reason;

            public DespawnPayload(uint seq, int netId, byte reason)
            {
                Seq = seq; NetId = netId; Reason = reason;
            }
        }

        public static byte[] EncodeDespawn(uint seq, int netId, byte reason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(netId);
                w.Write(reason);
                return ms.ToArray();
            }
        }

        /// <summary>Decode a despawn payload. Fixed 9 bytes (u32 + i32 + u8); anything shorter is a clean drop.</summary>
        public static bool TryDecodeDespawn(byte[] data, out DespawnPayload payload)
        {
            payload = default;
            if (data == null || data.Length < 9) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    byte reason = r.ReadByte();
                    payload = new DespawnPayload(seq, netId, reason);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
