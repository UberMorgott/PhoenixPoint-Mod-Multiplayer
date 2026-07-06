using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE wire codec for the <c>tac.deploy</c> payload (spec §5). Engine-free (BinaryWriter/Reader only),
    /// so it unit-tests in isolation. Frames:
    ///   [missionSiteId:i32]
    ///   [gameParamsLen:i32][gameParams:N]   — native Serializer bytes of TacticalGameParams
    ///   [snapshotLen:i32][snapshot:N]       — native Serializer bytes of TacLevelInstanceData
    ///   [actorCount:i32]  then per actor: [netId:i32][geoUnitId:i32][x:f32][y:f32][z:f32]
    /// Big blobs are length-prefixed exactly like the existing <c>MessageSerializer</c> idiom (int32 len +
    /// bytes). The two blobs are produced/consumed by the native game <c>Serializer</c> on the engine side
    /// (see <c>TacticalDeploySync</c>); this codec only frames them + the actor table.
    /// </summary>
    public static class TacticalDeployCodec
    {
        /// <summary>The decoded tac.deploy header + actor table (blobs handed to the native Serializer).</summary>
        public sealed class DeployPayload
        {
            public int MissionSiteId;
            public byte[] GameParamsBytes;
            public byte[] SnapshotBytes;
            public List<TacticalActorRegistry.ActorRow> ActorTable;

            public DeployPayload(int missionSiteId, byte[] gameParamsBytes, byte[] snapshotBytes,
                List<TacticalActorRegistry.ActorRow> actorTable)
            {
                MissionSiteId = missionSiteId;
                GameParamsBytes = gameParamsBytes ?? new byte[0];
                SnapshotBytes = snapshotBytes ?? new byte[0];
                ActorTable = actorTable ?? new List<TacticalActorRegistry.ActorRow>();
            }
        }

        public static byte[] Encode(DeployPayload p)
        {
            var gameParams = p.GameParamsBytes ?? new byte[0];
            var snapshot = p.SnapshotBytes ?? new byte[0];
            var table = p.ActorTable ?? new List<TacticalActorRegistry.ActorRow>();

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p.MissionSiteId);

                w.Write(gameParams.Length);
                if (gameParams.Length > 0) w.Write(gameParams);

                w.Write(snapshot.Length);
                if (snapshot.Length > 0) w.Write(snapshot);

                w.Write(table.Count);
                foreach (var row in table)
                {
                    w.Write(row.NetId);
                    w.Write(row.GeoUnitId);
                    w.Write(row.X);
                    w.Write(row.Y);
                    w.Write(row.Z);
                }
                return ms.ToArray();
            }
        }

        public static byte[] Encode(int missionSiteId, byte[] gameParamsBytes, byte[] snapshotBytes,
            List<TacticalActorRegistry.ActorRow> actorTable)
            => Encode(new DeployPayload(missionSiteId, gameParamsBytes, snapshotBytes, actorTable));

        /// <summary>Decode a tac.deploy payload. Returns false (no partial accept) on any truncation —
        /// the reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out DeployPayload payload)
        {
            payload = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int siteId = r.ReadInt32();

                    int gpLen = r.ReadInt32();
                    if (gpLen < 0 || ms.Length - ms.Position < gpLen) return false;
                    var gameParams = gpLen > 0 ? r.ReadBytes(gpLen) : new byte[0];
                    if (gameParams.Length != gpLen) return false;

                    int snapLen = r.ReadInt32();
                    if (snapLen < 0 || ms.Length - ms.Position < snapLen) return false;
                    var snapshot = snapLen > 0 ? r.ReadBytes(snapLen) : new byte[0];
                    if (snapshot.Length != snapLen) return false;

                    int n = r.ReadInt32();
                    if (n < 0) return false;
                    // Each row is fixed 5*4 = 20 bytes; guard the count against the remaining buffer so a
                    // corrupt huge count can't allocate wildly.
                    if ((long)n * 20 > ms.Length - ms.Position) return false;
                    var table = new List<TacticalActorRegistry.ActorRow>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int netId = r.ReadInt32();
                        int geoId = r.ReadInt32();
                        float x = r.ReadSingle();
                        float y = r.ReadSingle();
                        float z = r.ReadSingle();
                        table.Add(new TacticalActorRegistry.ActorRow(netId, geoId, x, y, z));
                    }

                    payload = new DeployPayload(siteId, gameParams, snapshot, table);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
