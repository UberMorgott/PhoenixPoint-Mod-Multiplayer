using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.MessageLayer
{
    public static class MessageSerializer
    {
        /// <summary>Hard sanity cap for the trailing parity-manifest block inside a JOIN (FA-0012). The block
        /// length is read raw off the wire and fed to <c>BinaryReader.ReadBytes(len)</c>, which allocates
        /// <c>new byte[len]</c> eagerly — a crafted JOIN could declare ~2e9 and force a multi-GB alloc on the
        /// host. A real manifest (DLC + mods + settings) is a few KB; 64 KB rejects only hostile/corrupt
        /// lengths while clearing even a large legitimate modlist.</summary>
        public const int MaxManifestBytes = 64 * 1024;

        // ─── Lobby / Identity Messages ─────────────────────────────────────

        // JOIN (reuses ConnectionRequest payload): persistent identity on connect.
        public static byte[] SerializeJoin(JoinMessage join)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(join.PlayerGuid.ToByteArray());
                bw.Write(join.Nickname ?? "");
                // FIX-4: append the parity manifest as a trailing, backward-compatible block. A present
                // flag lets a legacy (pre-FIX-4) JOIN — which has no trailing bytes — deserialize with a
                // null Manifest, which the host treats as "unverifiable parity" (a mod-version mismatch).
                if (join.Manifest != null)
                {
                    bw.Write(true);
                    var mb = SerializeParityManifest(join.Manifest);
                    bw.Write(mb.Length);
                    bw.Write(mb);
                }
                else
                {
                    bw.Write(false);
                }
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
                // FIX-4: trailing parity manifest (backward-compatible). Only read the present-flag when
                // the stream still has a byte, so a legacy 2-field JOIN parses with Manifest == null.
                if (ms.Position < ms.Length && br.ReadBoolean())
                {
                    var len = br.ReadInt32();
                    // FA-0012: bound the wire-declared block length BEFORE ReadBytes allocates new byte[len].
                    // Must be non-negative, within the manifest sanity cap, and actually present in the stream.
                    if (len < 0 || len > MaxManifestBytes || len > ms.Length - ms.Position)
                        throw new InvalidDataException("JOIN: implausible manifest block length " + len);
                    msg.Manifest = DeserializeParityManifest(br.ReadBytes(len));
                }
                return msg;
            }
        }

        // ─── Parity manifest (FIX-4): rides inside JOIN; host compares vs its own. ──────────
        public static byte[] SerializeParityManifest(Parity.ParityManifest m)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(m.Dlc.Count);
                foreach (var d in m.Dlc) bw.Write(d ?? "");

                bw.Write(m.Mods.Count);
                foreach (var mod in m.Mods)
                {
                    bw.Write(mod.Id ?? "");
                    bw.Write(mod.Version ?? "");
                }

                bw.Write(m.Settings.Count);
                foreach (var s in m.Settings)
                {
                    bw.Write(s.ModId ?? "");
                    bw.Write(s.Hash);
                    bw.Write(s.Entries.Count);
                    foreach (var e in s.Entries) bw.Write(e ?? "");
                }
                return ms.ToArray();
            }
        }

        public static Parity.ParityManifest DeserializeParityManifest(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var m = new Parity.ParityManifest();

                // FA-0012: every wire count below drives a ReadString loop; each entry consumes >=1 byte, so a
                // count larger than the bytes still in the stream is impossible → reject before looping (else a
                // huge count spins an allocate-and-throw loop on the host). Keyed off the live stream position.
                void GuardCount(int c)
                {
                    if (c < 0 || c > ms.Length - ms.Position)
                        throw new InvalidDataException("ParityManifest: implausible collection count " + c);
                }

                var dlcCount = br.ReadInt32();
                GuardCount(dlcCount);
                for (var i = 0; i < dlcCount; i++) m.Dlc.Add(br.ReadString());

                var modCount = br.ReadInt32();
                GuardCount(modCount);
                for (var i = 0; i < modCount; i++)
                    m.Mods.Add(new Parity.ModRef { Id = br.ReadString(), Version = br.ReadString() });

                var setCount = br.ReadInt32();
                GuardCount(setCount);
                for (var i = 0; i < setCount; i++)
                {
                    var s = new Parity.ModSettings { ModId = br.ReadString(), Hash = br.ReadUInt32() };
                    var entryCount = br.ReadInt32();
                    GuardCount(entryCount);
                    for (var j = 0; j < entryCount; j++) s.Entries.Add(br.ReadString());
                    m.Settings.Add(s);
                }
                return m;
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
                // Parity soft-gate: per-peer diff text as a TRAILING block (append-only, backward-
                // compatible — a legacy reader stops after the entries and never touches it). Aligned
                // by entry index; "" = parity OK.
                bw.Write(true);
                foreach (var peer in peers)
                    bw.Write(peer.ParityDiffs ?? "");
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
                // Trailing parity block (see SerializePeerList) — absent on a legacy sender.
                if (ms.Position < ms.Length && br.ReadBoolean())
                    for (var i = 0; i < n; i++)
                        peers[i].ParityDiffs = br.ReadString();
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

        // ─── EntryTransferAbort (tac-entry save transfer will never complete) ────────
        // A single reason string — diagnostics only; the receiving client's abort action is unconditional.
        public static byte[] SerializeEntryTransferAbort(string reason)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(reason ?? "");
                return ms.ToArray();
            }
        }

        public static string DeserializeEntryTransferAbort(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                return br.ReadString();
            }
        }

    }

    // ─── Action Data Types ─────────────────────────────────────────────────

    public class JoinMessage
    {
        public Guid PlayerGuid { get; set; }
        public string Nickname { get; set; }
        /// <summary>FIX-4: the joiner's co-op parity manifest (DLC + mods + settings). Null on a legacy
        /// pre-FIX-4 JOIN → the host treats it as an unverifiable-parity (mod-version) mismatch.</summary>
        public Parity.ParityManifest Manifest { get; set; }
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
        /// <summary>Parity soft-gate: exact host-computed diff text ("" = parity OK). Drives the roster
        /// warning badge + the client-side Ready lock; the host also gates Ready authoritatively.</summary>
        public string ParityDiffs { get; set; } = "";
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
