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
        public string[] SiteIds;
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
                var ids = p.SiteIds ?? new string[0];
                bw.Write(ids.Length);
                foreach (var id in ids) bw.Write(id ?? "");
                return ms.ToArray();
            }
        }

        public static StartTravelPayload DecodeStartTravel(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var p = new StartTravelPayload { VehicleId = br.ReadString() };
                var count = br.ReadInt32();
                p.SiteIds = new string[count];
                for (var i = 0; i < count; i++) p.SiteIds[i] = br.ReadString();
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
