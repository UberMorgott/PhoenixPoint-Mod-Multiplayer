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
        /// lengths while clearing even a large legitimate modlist. The same hazard rides on every string:
        /// <c>BinaryReader.ReadString</c> pre-sizes a StringBuilder from the wire-declared length before the
        /// payload exists, so every read here goes through <see cref="ReadBoundedString"/> instead.</summary>
        public const int MaxManifestBytes = 64 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // FA-0012, the ReadString half: BinaryReader.ReadString pre-sizes a StringBuilder from the wire-declared
        // byte length before reading the payload, so a 20-byte JOIN declaring 2^28 forces a ~512 MB allocation on
        // the host — pre-auth, and long before the roster's nickname cap ever sees the string. Read the 7-bit
        // prefix ourselves and reject a length the stream cannot back, exactly as the ReadBytes guards below do.
        //
        // INTERNAL, and it STAYS ON THIS TYPE. RailCheck L414 arm (b) resolves it by name off
        // MessageSerializer and proves the serializer still reaches it; moving it to a neutral helper
        // class would blind that law. So the other readers in this mod — GenericApplier's value rail,
        // EventSync's decoders — call in here instead of keeping their own copy.
        // <paramref name="maxBytes"/> is an ADDITIONAL ceiling on top of "the stream can back it", for
        // callers that know a real value's order of magnitude; the default leaves the stream bound alone.
        internal static string ReadBoundedString(BinaryReader br, int maxBytes = int.MaxValue)
        {
            int len = 0, shift = 0;
            while (true)
            {
                if (shift > 28) throw new InvalidDataException("string: malformed length prefix");
                var b = br.ReadByte();
                len |= (b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            var s = br.BaseStream;
            if (len < 0 || len > maxBytes || len > s.Length - s.Position)
                throw new InvalidDataException("string: implausible length " + len);
            return len == 0 ? string.Empty : StrictUtf8.GetString(br.ReadBytes(len));
        }

        // FA-0012, the COUNT half — the level above ReadBoundedString. A string ceiling does not bound the
        // array count in front of it: a decoder that reads `int n = r.ReadInt32()` and loops n times spins
        // allocate-and-throw until the stream is exhausted, and any `new List<T>(n)` in between pre-sizes a
        // multi-GB backing array on the sender's word alone before the first entry is read.
        //
        // The bound is the same one DeserializeParityManifest's GuardCount uses and needs no invented magic
        // number: every entry consumes at least one byte, so a count larger than the bytes REMAINING in the
        // stream is impossible by construction. <paramref name="minBytesPerEntry"/> tightens that to the
        // entry's real floor where the shape gives one honestly — count only the fields every iteration reads
        // unconditionally, a string as its 1-byte length prefix, and nothing behind an `if`.
        //
        // Lives here beside ReadBoundedString and NOT on WireString for two reasons: this is the FA-0012 home
        // L414 arm (b) points at, and L322 arm (c) counts "a string read" as anything declared on WireString,
        // so a count helper over there would read as a wire string to a law that counts fields.
        internal static int ReadBoundedCount(BinaryReader br, int minBytesPerEntry = 1)
        {
            var n = br.ReadInt32();
            var s = br.BaseStream;
            if (n < 0 || (long)n * minBytesPerEntry > s.Length - s.Position)
                throw new InvalidDataException("collection: implausible count " + n);
            return n;
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
                msg.Nickname = ReadBoundedString(br);
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

                // APPENDED, on purpose: a peer running a Multiplayer build from before the game-build gate
                // sends a manifest that simply ends here, and the reader below treats the absent field as
                // "unknown" instead of throwing "implausible manifest" at a peer whose only real problem is
                // an old mod version — which its own ModRef already diffs.
                bw.Write(m.GameVersion ?? "");
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
                for (var i = 0; i < dlcCount; i++) m.Dlc.Add(ReadBoundedString(br));

                var modCount = br.ReadInt32();
                GuardCount(modCount);
                for (var i = 0; i < modCount; i++)
                    m.Mods.Add(new Parity.ModRef { Id = ReadBoundedString(br), Version = ReadBoundedString(br) });

                var setCount = br.ReadInt32();
                GuardCount(setCount);
                for (var i = 0; i < setCount; i++)
                {
                    var s = new Parity.ModSettings { ModId = ReadBoundedString(br), Hash = br.ReadUInt32() };
                    var entryCount = br.ReadInt32();
                    GuardCount(entryCount);
                    for (var j = 0; j < entryCount; j++) s.Entries.Add(ReadBoundedString(br));
                    m.Settings.Add(s);
                }
                if (ms.Position < ms.Length) m.GameVersion = ReadBoundedString(br); // see the writer's note
                return m;
            }
        }

        // PEER_LIST (PlayerListUpdate): authoritative lobby roster.
        // <paramref name="inviteCode"/> is the HOST's unified invite code, a SESSION-level fact carried
        // on the roster rather than on a packet of its own — see the trailing block at the bottom.
        public static byte[] SerializePeerList(List<PeerListEntry> peers, string inviteCode = null)
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
                // Player panel: per-peer STATUS as one byte, appended the same way and for the same reason.
                // Both flags are already per-peer roster bookkeeping the host maintains; they ride the
                // roster because the roster is the message that already carries every other per-peer fact,
                // is already coalesced to one flush per frame, and is already re-broadcast on pause/resume.
                bw.Write(true);
                foreach (var peer in peers)
                    bw.Write((byte)((peer.Paused ? 1 : 0) | (peer.TacReady ? 2 : 0)));
                // HOST INVITE CODE — ONE string for the whole message, not one per peer. It rides the
                // roster for the same reason the two blocks above do: the roster is already the
                // host→all broadcast that is already coalesced to one flush per frame, so carrying it
                // here costs no new packet id, no new handler and no new dispatch arm. Append-only, so
                // a legacy reader stops before it.
                bw.Write(true);
                bw.Write(inviteCode ?? "");
                return ms.ToArray();
            }
        }

        public static List<PeerListEntry> DeserializePeerList(byte[] data)
            => DeserializePeerList(data, out _);

        public static List<PeerListEntry> DeserializePeerList(byte[] data, out string inviteCode)
        {
            inviteCode = "";
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var n = br.ReadInt32();
                // FA-0012, same guard as DeserializeParityManifest: each entry consumes >= 1 byte, so a
                // count larger than the bytes still in the stream is impossible — reject it before
                // new List<>(n) pre-allocates a capacity the wire never has to back.
                if (n < 0 || n > ms.Length - ms.Position)
                    throw new InvalidDataException("PeerList: implausible peer count " + n);
                var peers = new List<PeerListEntry>(n);
                for (var i = 0; i < n; i++)
                {
                    peers.Add(new PeerListEntry
                    {
                        SteamId = br.ReadUInt64(),
                        PlayerGuid = new Guid(br.ReadBytes(16)),
                        Nickname = ReadBoundedString(br),
                        Permissions = br.ReadInt32(),
                        Ready = br.ReadByte() != 0,
                        IsHost = br.ReadByte() != 0,
                        SlotIndex = br.ReadByte()
                    });
                }
                // Trailing parity block (see SerializePeerList) — absent on a legacy sender.
                if (ms.Position < ms.Length && br.ReadBoolean())
                    for (var i = 0; i < n; i++)
                        peers[i].ParityDiffs = ReadBoundedString(br);
                // Trailing status block (see the writer). Absent on a sender that predates the player panel;
                // its rows then read as connected + not-ready, which is what an unknown flag should look like.
                if (ms.Position < ms.Length && br.ReadBoolean())
                    for (var i = 0; i < n; i++)
                    {
                        var status = br.ReadByte();
                        peers[i].Paused = (status & 1) != 0;
                        peers[i].TacReady = (status & 2) != 0;
                    }
                // Trailing host-invite-code block (see the writer). Absent on a sender that predates it,
                // which reads as "no code yet" — exactly what an unknown value should look like.
                if (ms.Position < ms.Length && br.ReadBoolean())
                    inviteCode = ReadBoundedString(br);
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
                var name = ReadBoundedString(br);
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
                // KIND, not a bool: 0 player line, 1 system line, 2 system line that is ALSO an event
                // every peer must SEE (a player left / lost connection / came back). Same byte, same
                // length — a reader that only knows 0/1 still classifies a notice as a system line.
                bw.Write((byte)(chat.IsNotice ? 2 : chat.IsSystem ? 1 : 0));
                // IDENTITY, appended last so the frame stays back-compatible. The host stamps every line it
                // fans out with a session-monotonic number and the BACKLOG keeps it, so the replayed copy of
                // a line is byte-identical in identity to the live one and a receiver can tell them apart
                // from nothing else — see SessionManager._seenChatSeq. 0 = unstamped (never dedup'd).
                bw.Write(chat.Seq);
                return ms.ToArray();
            }
        }

        public static ChatMessageData DeserializeChat(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                var senderSteamId = br.ReadUInt64();
                var senderNick = ReadBoundedString(br);
                var text = ReadBoundedString(br);
                var kind = br.ReadByte();
                var seq = ms.Position < ms.Length ? br.ReadUInt32() : 0u; // unstamped sender → 0 → never dedup'd
                return new ChatMessageData
                {
                    SenderSteamId = senderSteamId,
                    SenderNick = senderNick,
                    Text = text,
                    IsSystem = kind != 0,
                    IsNotice = kind == 2,
                    Seq = seq
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
                var name = ReadBoundedString(br);
                var meta = ReadBoundedString(br);
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
                // FA-0012, same guard as DeserializeJoin: bound the wire-declared length against the bytes
                // actually present BEFORE ReadBytes allocates new byte[len] — a 22-byte SaveChunk declaring
                // len≈2^31 otherwise forces a 2 GB allocation attempt, long before MaxTransferBytes
                // (SaveTransferCoordinator:1646) ever sees the message.
                if (len < 0 || len > ms.Length - ms.Position)
                    throw new InvalidDataException("SaveChunk: implausible chunk length " + len);
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
                var ext = ReadBoundedString(br);
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

        // ─── RevealAll (second barrier: synchronized geoscape reveal) ────────────────
        // Boundary identity makes a delayed reliable/UDP duplicate unable to release the next load.
        // The second field is the COMMON REVEAL INSTANT on the host's PingTable clock (law L551) — the
        // instant every peer including the host lifts at. It used to be the send timestamp, which the
        // receiver deserialized and discarded, which is why the host's 1.33–1.55 s head start was
        // invisible in every log for the life of the mod.
        public static byte[] SerializeRevealAll(Guid boundaryId, long revealDueHostMs)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(boundaryId.ToByteArray());
                bw.Write(revealDueHostMs);
                return ms.ToArray();
            }
        }

        public static (Guid boundaryId, long revealDueHostMs) DeserializeRevealAll(byte[] data)
        {
            using (var br = new BinaryReader(new MemoryStream(data ?? Array.Empty<byte>())))
                return (new Guid(br.ReadBytes(16)), br.ReadInt64());
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
                return ReadBoundedString(br);
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
        /// <summary>This peer stopped answering and is being HELD, not removed (SessionManager.PausePeer).
        /// Renders as the grey "dropped out" marker on the player panel. The ONE thing that reads it as a
        /// fact is <see cref="Multiplayer.Network.LobbyController.IsLivePeer"/> — the single definition
        /// BOTH halves of the lobby start gate go through (L181) — and it reads it to EXCLUDE this row:
        /// a paused peer still blocks nobody (L84), it just stops being waited for and stops standing in
        /// for a player who is present.</summary>
        public bool Paused { get; set; }
        /// <summary>The tactical "I have finished moving" advisory flag. Same non-decision status as
        /// <see cref="Multiplayer.Network.ClientInfo.TacReady"/> it is copied from — read by the player
        /// panel's status column and by nothing that decides anything (L119).</summary>
        public bool TacReady { get; set; }
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

        /// <summary>A system line that is also a session EVENT — a player left, lost connection, or came
        /// back. Implies <see cref="IsSystem"/>. Notices are surfaced on every peer's screen (native toast
        /// in the geoscape, native prompt in tactical) on top of landing in the chat log; ordinary system
        /// lines stay in the log. Replayed history is deliberately shipped with this FALSE — a backlog is
        /// not news, and toasting it would fire the whole session's events at a late joiner at once.</summary>
        public bool IsNotice { get; set; }

        /// <summary>THE LINE'S IDENTITY. Host-stamped, session-monotonic, assigned once in
        /// <c>BroadcastChat</c> and CARRIED BY THE BACKLOG, so the replay of a line and its live fan-out
        /// are the same line and a receiver can say so. 0 = unstamped (an old sender) → rendered, never
        /// dedup'd.</summary>
        public uint Seq { get; set; }
    }
}
