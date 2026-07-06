using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.MessageLayer
{
    public static class MessageSerializer
    {
        // ─── Lobby / Identity Messages ─────────────────────────────────────

        // JOIN (reuses ConnectionRequest payload): persistent identity on connect.
        public static byte[] SerializeJoin(JoinMessage join)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(join.PlayerGuid.ToByteArray());
                bw.Write(join.Nickname ?? "");
                return ms.ToArray();
            }
        }

        public static JoinMessage DeserializeJoin(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new JoinMessage();
                msg.PlayerGuid = new Guid(br.ReadBytes(16));
                msg.Nickname = br.ReadString();
                return msg;
            }
        }

        // PEER_LIST (PlayerListUpdate): authoritative lobby roster.
        public static byte[] SerializePeerList(List<PeerListEntry> peers)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(peers.Count);
                foreach (var peer in peers)
                {
                    bw.Write(peer.SteamId);
                    bw.Write(peer.PlayerGuid.ToByteArray());
                    bw.Write(peer.Nickname ?? "");
                    bw.Write(peer.Permissions);
                    bw.Write((byte)(peer.Ready ? 1 : 0));
                    bw.Write((byte)(peer.IsHost ? 1 : 0));
                    bw.Write(peer.SlotIndex);
                }
                return ms.ToArray();
            }
        }

        public static List<PeerListEntry> DeserializePeerList(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var n = br.ReadInt32();
                var peers = new List<PeerListEntry>(n);
                for (var i = 0; i < n; i++)
                {
                    peers.Add(new PeerListEntry
                    {
                        SteamId = br.ReadUInt64(),
                        PlayerGuid = new Guid(br.ReadBytes(16)),
                        Nickname = br.ReadString(),
                        Permissions = br.ReadInt32(),
                        Ready = br.ReadByte() != 0,
                        IsHost = br.ReadByte() != 0,
                        SlotIndex = br.ReadByte()
                    });
                }
                return peers;
            }
        }

        // LEAVE (ClientLeave): graceful lobby/session leave.
        public static byte[] SerializeLeave(ulong peerSteamId)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(peerSteamId);
                return ms.ToArray();
            }
        }

        public static ulong DeserializeLeave(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return br.ReadUInt64();
            }
        }

        // RENAME (PlayerRename): live nickname edit.
        public static byte[] SerializeRename(ulong peerSteamId, string newNickname)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(peerSteamId);
                bw.Write(newNickname ?? "");
                return ms.ToArray();
            }
        }

        public static (ulong steamId, string newNickname) DeserializeRename(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var steamId = br.ReadUInt64();
                var name = br.ReadString();
                return (steamId, name);
            }
        }

        // ─── Chat / Set-Save Messages ──────────────────────────────────────

        // CHAT (ChatMessage): user/system lobby chat line. Host stamps the authoritative sender.
        public static byte[] SerializeChat(ChatMessageData chat)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(chat.SenderSteamId);
                bw.Write(chat.SenderNick ?? "");
                bw.Write(chat.Text ?? "");
                bw.Write((byte)(chat.IsSystem ? 1 : 0));
                return ms.ToArray();
            }
        }

        public static ChatMessageData DeserializeChat(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return new ChatMessageData
                {
                    SenderSteamId = br.ReadUInt64(),
                    SenderNick = br.ReadString(),
                    Text = br.ReadString(),
                    IsSystem = br.ReadByte() != 0
                };
            }
        }

        // SET_SAVE (SetSave, H→all): the chosen save's display name+meta, read-only on clients.
        public static byte[] SerializeSetSave(string saveName, string saveMeta)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(saveName ?? "");
                bw.Write(saveMeta ?? "");
                return ms.ToArray();
            }
        }

        public static (string saveName, string saveMeta) DeserializeSetSave(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var name = br.ReadString();
                var meta = br.ReadString();
                return (name, meta);
            }
        }

        // ─── Save-Transfer Messages ────────────────────────────────────────

        // SAVE_CHUNK (SaveChunk): one slice of the save blob.
        public static byte[] SerializeSaveChunk(SaveChunkMessage chunk)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(chunk.TransferId.ToByteArray());
                bw.Write(chunk.TotalBytes);
                bw.Write(chunk.Offset);
                bw.Write(chunk.Chunk?.Length ?? 0);
                if (chunk.Chunk != null && chunk.Chunk.Length > 0)
                    bw.Write(chunk.Chunk);
                return ms.ToArray();
            }
        }

        public static SaveChunkMessage DeserializeSaveChunk(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new SaveChunkMessage();
                msg.TransferId = new Guid(br.ReadBytes(16));
                msg.TotalBytes = br.ReadInt64();
                msg.Offset = br.ReadInt64();
                var len = br.ReadInt32();
                msg.Chunk = len > 0 ? br.ReadBytes(len) : null;
                return msg;
            }
        }

        // SAVE_DONE (SaveDone): transfer complete; client verifies length/crc.
        public static byte[] SerializeSaveDone(Guid transferId, long totalBytes, string fileExtension, uint crc32,
            bool onDemandJoin = false)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(transferId.ToByteArray());
                bw.Write(totalBytes);
                bw.Write(fileExtension ?? ".zsav");
                bw.Write(crc32);
                // Trailing versioned flag (append-only, backward-compatible): true marks a MID-SESSION
                // on-demand joiner transfer — the receiver enters the level immediately + reveals
                // natively (no lobby BEGIN barrier, no co-op RevealAll hold). A pre-flag 4-field SaveDone
                // (or an explicit false) deserializes to onDemandJoin=false = the unchanged lobby/F2 path.
                bw.Write(onDemandJoin);
                return ms.ToArray();
            }
        }

        public static (Guid transferId, long totalBytes, string fileExtension, uint crc32, bool onDemandJoin) DeserializeSaveDone(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var id = new Guid(br.ReadBytes(16));
                var total = br.ReadInt64();
                var ext = br.ReadString();
                var crc = br.ReadUInt32();
                // Backward-compatible read: the onDemandJoin flag is a trailing append. Only read it when
                // the stream still has a byte, so a legacy 4-field SaveDone parses with onDemandJoin=false.
                var onDemandJoin = ms.Position < ms.Length && br.ReadBoolean();
                return (id, total, ext, crc, onDemandJoin);
            }
        }

        // PROGRESS (LoadProgress): per-peer loading progress. phase 0=download,1=load.
        public static byte[] SerializeLoadProgress(ulong peerSteamId, byte phase, byte percent)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(peerSteamId);
                bw.Write(phase);
                bw.Write(percent);
                return ms.ToArray();
            }
        }

        public static (ulong steamId, byte phase, byte percent) DeserializeLoadProgress(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var steamId = br.ReadUInt64();
                var phase = br.ReadByte();
                var percent = br.ReadByte();
                return (steamId, phase, percent);
            }
        }

        // ROSTER_PROGRESS (RosterProgress, 0x1D): host-aggregated per-slot snapshot, UNRELIABLE.
        public static byte[] SerializeRosterProgress(List<ProgressRow> rows)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)rows.Count);
                foreach (var r in rows)
                {
                    bw.Write(r.SlotIndex);
                    bw.Write(r.Phase);
                    bw.Write(r.Percent);
                }
                return ms.ToArray();
            }
        }

        public static List<ProgressRow> DeserializeRosterProgress(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var n = br.ReadByte();
                var rows = new List<ProgressRow>(n);
                for (var i = 0; i < n; i++)
                    rows.Add(new ProgressRow
                    {
                        SlotIndex = br.ReadByte(),
                        Phase = br.ReadByte(),
                        Percent = br.ReadByte()
                    });
                return rows;
            }
        }

        // LOAD_COMPLETE (LoadComplete, 0x1E): a slot's load truly finished, RELIABLE.
        public static byte[] SerializeLoadComplete(byte slotIndex, Guid transferId)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(slotIndex);
                bw.Write(transferId.ToByteArray());
                return ms.ToArray();
            }
        }

        public static (byte slotIndex, Guid transferId) DeserializeLoadComplete(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var slot = br.ReadByte();
                var id = new Guid(br.ReadBytes(16));
                return (slot, id);
            }
        }

        // LOADED (ClientLoaded): client finished loading the save; barrier ack.
        public static byte[] SerializeClientLoaded(ulong peerSteamId, Guid transferId, bool ok)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(peerSteamId);
                bw.Write(transferId.ToByteArray());
                bw.Write((byte)(ok ? 1 : 0));
                return ms.ToArray();
            }
        }

        public static (ulong steamId, Guid transferId, bool ok) DeserializeClientLoaded(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var steamId = br.ReadUInt64();
                var id = new Guid(br.ReadBytes(16));
                var ok = br.ReadByte() != 0;
                return (steamId, id, ok);
            }
        }

        // BEGIN (SessionBegin): barrier release — all enter geoscape simultaneously.
        public static byte[] SerializeSessionBegin(long serverStartTicks)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(serverStartTicks);
                return ms.ToArray();
            }
        }

        public static long DeserializeSessionBegin(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return br.ReadInt64();
            }
        }


        // ─── RevealAll (second barrier: synchronized geoscape reveal) ────────────────
        // Mirrors SessionBegin exactly: a single long serverTicks (DateTime.UtcNow.Ticks at send),
        // written via BinaryWriter.Write(long) and read back via BinaryReader.ReadInt64().
        public static byte[] SerializeRevealAll(long serverTicks)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(serverTicks);
                return ms.ToArray();
            }
        }

        public static long DeserializeRevealAll(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return br.ReadInt64();
            }
        }

    }

    // ─── Action Data Types ─────────────────────────────────────────────────

    public class JoinMessage
    {
        public Guid PlayerGuid { get; set; }
        public string Nickname { get; set; }
    }

    public class PeerListEntry
    {
        public ulong SteamId { get; set; }
        public Guid PlayerGuid { get; set; }
        public string Nickname { get; set; }
        public int Permissions { get; set; }
        public bool Ready { get; set; }
        public bool IsHost { get; set; }   // true for the host's own self-entry in the roster
        public byte SlotIndex { get; set; }   // host-assigned stable slot; 0 = host
    }

    /// <summary>One row of the host-aggregated RosterProgress snapshot (~3 bytes on the wire).</summary>
    public struct ProgressRow
    {
        public byte SlotIndex;
        public byte Phase;     // 0 = download, 1 = native load
        public byte Percent;   // 0..100
    }

    public class SaveChunkMessage
    {
        public Guid TransferId { get; set; }
        public long TotalBytes { get; set; }
        public long Offset { get; set; }
        public byte[] Chunk { get; set; }
    }

    public class ChatMessageData
    {
        public ulong SenderSteamId { get; set; }
        public string SenderNick { get; set; }
        public string Text { get; set; }
        public bool IsSystem { get; set; }
    }
}
