using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    public static partial class TacticalLiveCodec
    {
        // ─── tac.item.destroy (host→all, rca-inventory part 3 — phantom throwable/consumable fix) ─────
        // The host broadcasts this when a throwable/consumable TacticalItem is DESTROYED on a registered actor
        // (grenade thrown, or a consumable auto-destroyed at 0 charges — both funnel through the ONE native
        // chokepoint TacticalItem.Destroy()). Every client removes the SAME item from its mirror inventory so no
        // phantom (re-throwable) item survives. Item identity mirrors the loot surface (0x9A/0x9B): (ItemDef guid,
        // index among items of that def in the SOURCE inventory, PRE-removal) — both sides mirror the same contents,
        // so the Nth item of a def is the same logical item host↔client. Slot names which of the actor's two
        // inventories holds it (0 = backpack Inventory, 1 = Equipments), matching TacticalInventoryTransferCodec.
        //   [seq:u32][actorNetId:i32][slot:u8][itemDefGuid:string][defIndex:i32]
        public struct ItemDestroy
        {
            public uint Seq;
            public int ActorNetId;
            public byte Slot;            // 0 = backpack Inventory, 1 = Equipments (matches TacticalInventoryTransferCodec.Slot*)
            public string ItemDefGuid;
            public int DefIndex;         // index among items sharing ItemDefGuid in the slot inventory, PRE-removal
            public ItemDestroy(uint seq, int actorNetId, byte slot, string itemDefGuid, int defIndex)
            {
                Seq = seq; ActorNetId = actorNetId; Slot = slot;
                ItemDefGuid = itemDefGuid ?? ""; DefIndex = defIndex;
            }
        }

        public static byte[] EncodeItemDestroy(uint seq, int actorNetId, byte slot, string itemDefGuid, int defIndex)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(actorNetId);
                w.Write(slot);
                w.Write(itemDefGuid ?? "");
                w.Write(defIndex);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeItemDestroy(byte[] data, out ItemDestroy o)
        {
            o = default(ItemDestroy);
            // Minimum: u32 seq + i32 actor + u8 slot + at least a 1-byte length-prefixed string + i32 defIndex.
            if (data == null || data.Length < 4 + 4 + 1 + 1 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int actorNetId = r.ReadInt32();
                    byte slot = r.ReadByte();
                    string guid = r.ReadString();
                    int defIndex = r.ReadInt32();
                    o = new ItemDestroy(seq, actorNetId, slot, guid, defIndex);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
