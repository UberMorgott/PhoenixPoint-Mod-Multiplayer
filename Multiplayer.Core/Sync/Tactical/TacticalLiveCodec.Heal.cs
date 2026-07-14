using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
        // ─── tac.heal.start (host→all, Feature C heal — client-side HEAL PRESENTATION) ────────────────
        // The heal counterpart of tac.fire.start (0x90) / tac.melee.start (0x91). The host broadcasts this when it
        // runs a heal (own click OR a relayed client heal), so every peer REPLAYS the native HealAbility.HealTargetCrt
        // PRESENTATION concurrently with the host — animation only. HP is owned by the 0x8F actor-state Health bit and
        // medkit charge by the host; the peer replay neuters both (BaseStat.Add + CommonItemData.ModifyCharges) so the
        // heal animates without any double-apply. A heal always targets an ACTOR (self or ally), so no world position
        // is carried — a self-heal sets targetNetId == healerNetId. Layout mirrors MeleeStart MINUS the position tail.
        //   [seq:u32][healerNetId:i32][abilityDefGuid:string][targetNetId:i32]
        public struct HealStart
        {
            public uint Seq;
            public int HealerNetId;
            public string AbilityDefGuid;
            public int TargetNetId;      // the healed actor (self-heal → == HealerNetId); always resolvable
            public HealStart(uint seq, int healerNetId, string abilityDefGuid, int targetNetId)
            {
                Seq = seq; HealerNetId = healerNetId; AbilityDefGuid = abilityDefGuid ?? ""; TargetNetId = targetNetId;
            }
        }

        public static byte[] EncodeHealStart(uint seq, int healerNetId, string abilityDefGuid, int targetNetId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(healerNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeHealStart(byte[] data, out HealStart start)
        {
            start = default(HealStart);
            // Minimum: u32 seq + i32 healer + at least a 1-byte length-prefixed string + i32 target.
            if (data == null || data.Length < 4 + 4 + 1 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int healer = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    start = new HealStart(seq, healer, guid, targetNetId);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
