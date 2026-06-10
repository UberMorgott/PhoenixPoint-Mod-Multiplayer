using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Network.MessageLayer
{
    public static class MessageSerializer
    {
        // ─── Tactical Action Messages ──────────────────────────────────────

        public static byte[] SerializeTacticalAction(TacticalActionMessage action)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(action.ActionId.ToByteArray());
                bw.Write((byte)action.ActionType);
                bw.Write(action.ActorGeoId);
                bw.Write(action.AbilityDefId ?? "");
                bw.Write(action.TargetData?.Length ?? 0);
                if (action.TargetData != null && action.TargetData.Length > 0)
                    bw.Write(action.TargetData);
                bw.Write(action.Timestamp);
                return ms.ToArray();
            }
        }

        public static TacticalActionMessage DeserializeTacticalAction(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new TacticalActionMessage();
                msg.ActionId = new Guid(br.ReadBytes(16));
                msg.ActionType = (TacticalActionType)br.ReadByte();
                msg.ActorGeoId = br.ReadInt32();
                msg.AbilityDefId = br.ReadString();
                var targetLen = br.ReadInt32();
                msg.TargetData = targetLen > 0 ? br.ReadBytes(targetLen) : null;
                msg.Timestamp = br.ReadInt64();
                return msg;
            }
        }

        // ─── Campaign Action Messages ─────────────────────────────────────

        public static byte[] SerializeCampaignAction(CampaignActionMessage action)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(action.ActionId.ToByteArray());
                bw.Write((byte)action.ActionType);
                bw.Write(action.TargetId ?? "");
                bw.Write(action.Payload?.Length ?? 0);
                if (action.Payload != null && action.Payload.Length > 0)
                    bw.Write(action.Payload);
                bw.Write(action.Timestamp);
                return ms.ToArray();
            }
        }

        public static CampaignActionMessage DeserializeCampaignAction(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var msg = new CampaignActionMessage();
                msg.ActionId = new Guid(br.ReadBytes(16));
                msg.ActionType = (CampaignActionType)br.ReadByte();
                msg.TargetId = br.ReadString();
                var payloadLen = br.ReadInt32();
                msg.Payload = payloadLen > 0 ? br.ReadBytes(payloadLen) : null;
                msg.Timestamp = br.ReadInt64();
                return msg;
            }
        }

        // ─── Permission Messages ──────────────────────────────────────────

        // PERMISSION (reshaped): per-flag toggle keyed by playerGUID.
        // flagBit = the bit index (0..9) of the CampaignPermission flag, NOT a mask.
        public static byte[] SerializePermissionUpdate(Guid playerGuid, byte flagBit, bool value)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(playerGuid.ToByteArray());
                bw.Write(flagBit);
                bw.Write((byte)(value ? 1 : 0));
                return ms.ToArray();
            }
        }

        public static (Guid playerGuid, byte flagBit, bool value) DeserializePermissionUpdate(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var guid = new Guid(br.ReadBytes(16));
                var flagBit = br.ReadByte();
                var value = br.ReadByte() != 0;
                return (guid, flagBit, value);
            }
        }

        // ─── Game State ────────────────────────────────────────────────────

        public static byte[] SerializeGameState(string levelName, byte[] stateData)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(levelName ?? "");
                bw.Write(stateData?.Length ?? 0);
                if (stateData != null && stateData.Length > 0)
                    bw.Write(stateData);
                return ms.ToArray();
            }
        }

        public static (string levelName, byte[] stateData) DeserializeGameState(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var levelName = br.ReadString();
                var stateLen = br.ReadInt32();
                var stateData = stateLen > 0 ? br.ReadBytes(stateLen) : null;
                return (levelName, stateData);
            }
        }

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
                        IsHost = br.ReadByte() != 0
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
        public static byte[] SerializeSaveDone(Guid transferId, long totalBytes, string fileExtension, uint crc32)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(transferId.ToByteArray());
                bw.Write(totalBytes);
                bw.Write(fileExtension ?? ".zsav");
                bw.Write(crc32);
                return ms.ToArray();
            }
        }

        public static (Guid transferId, long totalBytes, string fileExtension, uint crc32) DeserializeSaveDone(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var id = new Guid(br.ReadBytes(16));
                var total = br.ReadInt64();
                var ext = br.ReadString();
                var crc = br.ReadUInt32();
                return (id, total, ext, crc);
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

        // ─── Ownership Messages ────────────────────────────────────────────

        // ASSIGN_OWNER (SoldierAssignment): soldierID→playerGUID ownership.
        // Guid.Empty owner = unassign.
        public static byte[] SerializeAssignOwner(int geoUnitId, Guid ownerPlayerGuid)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(geoUnitId);
                bw.Write(ownerPlayerGuid.ToByteArray());
                return ms.ToArray();
            }
        }

        public static (int geoUnitId, Guid ownerPlayerGuid) DeserializeAssignOwner(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var id = br.ReadInt32();
                var owner = new Guid(br.ReadBytes(16));
                return (id, owner);
            }
        }
    }

    // ─── Action Data Types ─────────────────────────────────────────────────

    public enum TacticalActionType : byte
    {
        Move = 0,
        Shoot = 1,
        Reload = 2,
        UseAbility = 3,
        InventoryTransfer = 4,
        Overwatch = 5,
        Interact = 6,
        UseItem = 7,
        Standby = 8
    }

    public enum CampaignActionType : byte
    {
        StartResearch = 0,
        QueueManufacturing = 1,
        CancelManufacturing = 2,
        ConstructFacility = 3,
        RemoveFacility = 4,
        RepairFacility = 5,
        EquipSoldier = 6,
        EquipVehicle = 7,
        DeployAircraft = 8,
        HireRecruit = 9,
        DismissSoldier = 10,
        AssignSoldier = 11,
        RemoveSoldier = 12,
        StartTravel = 13
    }

    public class TacticalActionMessage
    {
        public Guid ActionId { get; set; }
        public TacticalActionType ActionType { get; set; }
        public int ActorGeoId { get; set; }
        public string AbilityDefId { get; set; }
        public byte[] TargetData { get; set; }
        public long Timestamp { get; set; }
    }

    public class CampaignActionMessage
    {
        public Guid ActionId { get; set; }
        public CampaignActionType ActionType { get; set; }
        public string TargetId { get; set; }
        public byte[] Payload { get; set; }
        public long Timestamp { get; set; }
    }

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
