using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the mid-mission INVENTORY-TRANSFER surfaces (tactical loot UI
    /// re-enable): <c>tac.intent.inventory</c> (0x9A, client→host) + <c>tac.inventory</c> (0x9B, host→all).
    ///
    /// The native tactical inventory UI is DEFERRED-COMMIT: drags mutate scratch <c>InventoryQuery</c> copies
    /// and the real transfer commits once, at <c>UIStateInventory.ExitState → ApplyInventoryActions →
    /// InventoryQuery.SyncItems</c>, which funnels every mutation through <c>InventoryComponent.RemoveItem</c> /
    /// <c>AddItem</c>. So ONE commit yields a BATCH of cross-inventory MOVES. A move is expressed by STABLE
    /// cross-side identity — no live refs on the wire:
    ///   • endpoint = (actor netId, slot) — slot 0 = the actor's backpack <c>Inventory</c>, slot 1 = its
    ///     <c>Equipments</c> (soldiers/vehicles); a ground/crate/dead-body-drop <c>ItemContainer</c> is slot 0.
    ///     EVERY loot endpoint is a registered actor (soldier or spawned container), so netId always resolves.
    ///   • item = (ItemDef guid, index among items of that def in the SOURCE, pre-move). Both sides mirror the
    ///     same source contents, so the Nth item of a def is the same logical item host↔client. Carrying the def
    ///     guid (not a bare slot index) is the DropItem FOLLOW-UP: host matches by def, never a blind index.
    ///
    ///   intent  (0x9A): [actingNetId:i32][applyCost:u8][moves][nonce:u32][cellTail?]
    ///   apply   (0x9B): [moves][seq:u32][cellTail?]
    ///   moves        : [count:u16] then count × [srcNetId:i32][srcSlot:u8][dstNetId:i32][dstSlot:u8]
    ///                                            [itemDefGuid:str][srcDefIndex:i32]
    ///   cellTail     : [count:u16 == moves.count] then count × [dstUiCell:i16] — the destination UI-list cell
    ///                  of each move (-1 = unknown). Appended AFTER the fixed trailer so an OLD peer ignores it
    ///                  (trailing-bytes tolerance below); a new peer reading old bytes defaults every cell to -1.
    ///                  A move with srcEndpoint == dstEndpoint is a pure UI REORDER (cell move within one list):
    ///                  membership is unchanged, the receiver must NOT remove/add on the model — see
    ///                  <see cref="IsReorder"/>.
    ///
    /// <c>actingNetId</c>/<c>applyCost</c> carry the looting soldier + whether the inventory ability's AP cost is
    /// due, so the host spends it authoritatively (AP itself rides the 0x8F actor-state delta, not this surface).
    /// The apply surface omits them — a client re-runs ONLY the item moves; AP reconciles via 0x8F.
    ///
    /// TRUNCATION / a count over <see cref="MaxMoves"/> → clean <c>false</c> (no partial accept), like the sibling
    /// tactical codecs — the reliable transport guarantees full delivery. Trailing bytes past the fixed trailer are
    /// ignored (forward-tolerant: a future field appended after nonce/seq still decodes on an old peer).
    /// </summary>
    public static class TacticalInventoryTransferCodec
    {
        /// <summary>Endpoint slot discriminator: which <c>InventoryComponent</c> of the endpoint actor.</summary>
        public const byte SlotInventory  = 0;   // backpack (TacticalActorBase.Inventory) / container inventory
        public const byte SlotEquipments = 1;   // ready slots (TacticalActor.Equipments)

        /// <summary>Sentinel netId when no acting soldier is carried (e.g. a container-only rearrange).</summary>
        public const int NoActor = -1;

        /// <summary>Reject a batch claiming more moves than any real loot session — a garbage/hostile length must
        /// not drive a large allocation or loop. A single inventory-view commit moves at most a few dozen items.</summary>
        public const int MaxMoves = 512;

        public struct Move
        {
            public int SrcNetId;
            public byte SrcSlot;
            public int DstNetId;
            public byte DstSlot;
            public string ItemDefGuid;
            public int SrcDefIndex;   // index among items sharing ItemDefGuid in the SOURCE inventory (pre-move)
            public int DstUiCell;     // destination UI-list cell (slot index); -1 = unknown (old peer / not captured)

            public Move(int srcNetId, byte srcSlot, int dstNetId, byte dstSlot, string itemDefGuid, int srcDefIndex,
                        int dstUiCell = -1)
            {
                SrcNetId = srcNetId; SrcSlot = srcSlot;
                DstNetId = dstNetId; DstSlot = dstSlot;
                ItemDefGuid = itemDefGuid ?? "";
                SrcDefIndex = srcDefIndex;
                DstUiCell = dstUiCell;
            }
        }

        /// <summary>TRUE when the move's endpoints are identical — a pure UI cell REORDER within one list.
        /// Membership is unchanged: the receiver skips the model remove/add and only repositions the UI cell.</summary>
        public static bool IsReorder(Move m) => m.SrcNetId == m.DstNetId && m.SrcSlot == m.DstSlot;

        public sealed class Intent
        {
            public int ActingNetId;
            public bool ApplyCost;
            public List<Move> Moves = new List<Move>();
            public uint Nonce;
        }

        public sealed class Apply
        {
            public List<Move> Moves = new List<Move>();
            public uint Seq;
        }

        public static byte[] EncodeIntent(int actingNetId, bool applyCost, IList<Move> moves, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actingNetId);
                w.Write((byte)(applyCost ? 1 : 0));
                WriteMoves(w, moves);
                w.Write(nonce);
                WriteCellTail(w, moves);
                return ms.ToArray();
            }
        }

        public static byte[] EncodeApply(IList<Move> moves, uint seq)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteMoves(w, moves);
                w.Write(seq);
                WriteCellTail(w, moves);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeIntent(byte[] data, out Intent intent)
        {
            intent = null;
            if (data == null || data.Length < 4 + 1 + 2 + 4) return false;   // actingNetId + applyCost + count + nonce
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int actingNetId = r.ReadInt32();
                    bool applyCost = r.ReadByte() != 0;
                    if (!ReadMoves(r, out var moves)) return false;
                    uint nonce = r.ReadUInt32();
                    ReadCellTail(r, moves);
                    intent = new Intent { ActingNetId = actingNetId, ApplyCost = applyCost, Moves = moves, Nonce = nonce };
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool TryDecodeApply(byte[] data, out Apply apply)
        {
            apply = null;
            if (data == null || data.Length < 2 + 4) return false;   // count + seq
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (!ReadMoves(r, out var moves)) return false;
                    uint seq = r.ReadUInt32();
                    ReadCellTail(r, moves);
                    apply = new Apply { Moves = moves, Seq = seq };
                    return true;
                }
            }
            catch { return false; }
        }

        private static void WriteMoves(BinaryWriter w, IList<Move> moves)
        {
            int count = moves?.Count ?? 0;
            w.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                var m = moves[i];
                w.Write(m.SrcNetId);
                w.Write(m.SrcSlot);
                w.Write(m.DstNetId);
                w.Write(m.DstSlot);
                w.Write(m.ItemDefGuid ?? "");
                w.Write(m.SrcDefIndex);
            }
        }

        /// <summary>Versioned tail: per-move destination UI cells, appended after the fixed trailer.</summary>
        private static void WriteCellTail(BinaryWriter w, IList<Move> moves)
        {
            int count = moves?.Count ?? 0;
            w.Write((ushort)count);
            for (int i = 0; i < count; i++) w.Write((short)moves[i].DstUiCell);   // slot indices are tiny; -1 = unknown
        }

        /// <summary>Best-effort tail read: absent (old peer) / short / count-mismatched trailing bytes leave every
        /// cell at -1 — the tail can never fail an otherwise-valid decode.</summary>
        private static void ReadCellTail(BinaryReader r, List<Move> moves)
        {
            try
            {
                if (r.BaseStream.Length - r.BaseStream.Position < 2) return;
                int count = r.ReadUInt16();
                if (count != moves.Count || r.BaseStream.Length - r.BaseStream.Position < 2L * count) return;
                for (int i = 0; i < count; i++)
                {
                    var m = moves[i];
                    m.DstUiCell = r.ReadInt16();
                    moves[i] = m;
                }
            }
            catch { /* tail is optional by contract */ }
        }

        private static bool ReadMoves(BinaryReader r, out List<Move> moves)
        {
            moves = new List<Move>();
            int count = r.ReadUInt16();
            if (count > MaxMoves) return false;   // garbage/hostile length → clean drop (no partial accept)
            for (int i = 0; i < count; i++)
            {
                int srcNetId = r.ReadInt32();
                byte srcSlot = r.ReadByte();
                int dstNetId = r.ReadInt32();
                byte dstSlot = r.ReadByte();
                string guid = r.ReadString();
                int srcDefIndex = r.ReadInt32();
                moves.Add(new Move(srcNetId, srcSlot, dstNetId, dstSlot, guid, srcDefIndex));
            }
            return true;
        }
    }
}
