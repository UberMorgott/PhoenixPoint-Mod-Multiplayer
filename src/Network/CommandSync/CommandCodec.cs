using System.IO;

namespace Multipleer.Network.CommandSync
{
    // Pure, Unity-free. Encodes the StartTravel ACTION PAYLOAD only; the CampaignActionMessage
    // envelope (ActionId/Type/Timestamp) is handled by MessageSerializer. The vehicle and the
    // ordered destination sites are sent as stable string ids — the host/clients resolve them
    // back to live GeoVehicle/GeoSite by id at apply time (no engine types cross the wire).
    public struct StartTravelPayload
    {
        public string VehicleId;
        // INC-3a: the owning faction's Def.Guid, set by the client-side intercept Prefix from the
        // vehicle's Owner. The host resolves a client-originated craft by (OwnerFactionGuid, VehicleID)
        // via GeoBridge.FindVehicleByFactionAndId instead of the Phoenix-only FindVehicleById, so a
        // client can order a non-Phoenix craft to travel. Empty -> host strict-resolves to Phoenix
        // (the legacy Phoenix-manufactured-aircraft case).
        public string OwnerFactionGuid;
        public string[] SiteIds;
        // PIVOT Step A start-time alignment. The host stamps the geoscape game-time (seconds, DOUBLE) and
        // the vehicle's RangeRemaining (meters) at the instant it ran StartTravel, so the client's own native
        // NavigateRoutine — which captures its progress origin as a LOCAL startTime = Timing.Now at the call
        // moment (GeoNavComponent.NavigateRoutine) — can be reconciled against the SAME host origin (no
        // constant offset). DOUBLE for StartGameTime: the geoscape clock reaches ~6.4e10 game-seconds where a
        // float32 ULP (~8192 s) dwarfs the inter-sample gap; a float cast would collapse the value (same
        // reason TimeBridge.GetHostNowSeconds reads TimeSpan.TotalSeconds, never (float)TimeUnit). 0 => absent
        // (older sender / unresolved clock) -> client falls back to its own local startTime capture.
        public double StartGameTime;
        public float StartRangeRemaining;
    }

    public struct SetTimePayload
    {
        public bool Paused;
        public int PresetIndex;
    }

    public struct TimeStatePayload
    {
        public bool Paused;
        public float Scale;
        public long StartTimeTicks;
        public long StartFixedTicks;
        public long OwnNowTicks;
        public long OwnFixedTicks;
    }

    public static class CommandCodec
    {
        public static byte[] EncodeStartTravel(StartTravelPayload p)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(p.VehicleId ?? "");
                bw.Write(p.OwnerFactionGuid ?? "");
                var ids = p.SiteIds ?? new string[0];
                bw.Write(ids.Length);
                foreach (var id in ids) bw.Write(id ?? "");
                // Appended AFTER the variable-length site list so existing fixed fields keep their offsets.
                bw.Write(p.StartGameTime);       // double (8B) — geoscape game-time origin, see field doc
                bw.Write(p.StartRangeRemaining); // float  (4B) — vehicle range-remaining at the host StartTravel
                return ms.ToArray();
            }
        }

        public static StartTravelPayload DecodeStartTravel(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var p = new StartTravelPayload { VehicleId = br.ReadString(), OwnerFactionGuid = br.ReadString() };
                var count = br.ReadInt32();
                p.SiteIds = new string[count];
                for (var i = 0; i < count; i++) p.SiteIds[i] = br.ReadString();
                // Trailing start-time alignment fields. Guard EOF so a pre-pivot sender (no trailer) still
                // decodes — missing trailer -> StartGameTime=0 (absent) -> client uses its local capture.
                if (ms.Position < ms.Length) p.StartGameTime = br.ReadDouble();
                if (ms.Position < ms.Length) p.StartRangeRemaining = br.ReadSingle();
                return p;
            }
        }

        public static byte[] EncodeSetTime(SetTimePayload p)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(p.Paused);
                bw.Write(p.PresetIndex);
                return ms.ToArray();
            }
        }

        public static SetTimePayload DecodeSetTime(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new SetTimePayload { Paused = br.ReadBoolean(), PresetIndex = br.ReadInt32() };
            }
        }

        public static byte[] EncodeTimeState(TimeStatePayload p)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(p.Paused);
                bw.Write(p.Scale);
                bw.Write(p.StartTimeTicks);
                bw.Write(p.StartFixedTicks);
                bw.Write(p.OwnNowTicks);
                bw.Write(p.OwnFixedTicks);
                return ms.ToArray();
            }
        }

        public static TimeStatePayload DecodeTimeState(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new TimeStatePayload
                {
                    Paused = br.ReadBoolean(),
                    Scale = br.ReadSingle(),
                    StartTimeTicks = br.ReadInt64(),
                    StartFixedTicks = br.ReadInt64(),
                    OwnNowTicks = br.ReadInt64(),
                    OwnFixedTicks = br.ReadInt64()
                };
            }
        }
    }
}
