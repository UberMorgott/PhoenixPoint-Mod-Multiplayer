using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Wire codecs for the ActionSync packet group. The header/envelope is handled by
    /// <c>NetworkMessage</c>; these encode/decode only the payload bytes.
    /// Mirrors the existing <c>MessageSerializer</c> BinaryWriter/Reader idiom.
    /// </summary>
    public static class SyncProtocol
    {
        public static byte[] EncodeActionRequest(ushort actionId, uint nonce, byte[] payload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actionId);
                w.Write(nonce);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionRequest(byte[] data, out ushort actionId, out uint nonce, out byte[] payload)
        {
            actionId = 0; nonce = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    actionId = r.ReadUInt16();
                    nonce = r.ReadUInt32();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeActionApply(ushort actionId, ulong sequence, byte[] payload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(actionId);
                w.Write(sequence);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionApply(byte[] data, out ushort actionId, out ulong sequence, out byte[] payload)
        {
            actionId = 0; sequence = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    actionId = r.ReadUInt16();
                    sequence = r.ReadUInt64();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeActionReject(uint nonce, byte reasonCode, string reason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                w.Write(reasonCode);
                w.Write(reason ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryDecodeActionReject(byte[] data, out uint nonce, out byte reasonCode, out string reason)
        {
            nonce = 0; reasonCode = 0; reason = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    reasonCode = r.ReadByte();
                    reason = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeWalletSync(ulong version, List<(int type, float value)> slots)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(version);
                w.Write((byte)slots.Count);
                foreach (var (t, v) in slots)
                {
                    w.Write(t);
                    w.Write(v);
                }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeWalletSync(byte[] data, out ulong version, out List<(int type, float value)> slots)
        {
            version = 0; slots = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    version = r.ReadUInt64();
                    int n = r.ReadByte();
                    slots = new List<(int, float)>(n);
                    for (int i = 0; i < n; i++)
                        slots.Add((r.ReadInt32(), r.ReadSingle()));
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Generic per-channel state echo (StateChannel infra) ───────────
        // Wire: [channelId:u8][version:u64][len:u16][payload:N]. The channel id selects an
        // IStateChannel; version is host-monotonic per channel (client drops anything not newer).

        public static byte[] EncodeStateSync(byte channelId, ulong version, byte[] payload)
        {
            payload = payload ?? new byte[0];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(channelId);
                w.Write(version);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeStateSync(byte[] data, out byte channelId, out ulong version, out byte[] payload)
        {
            channelId = 0; version = 0; payload = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    channelId = r.ReadByte();
                    version = r.ReadUInt64();
                    payload = r.ReadBytes(r.ReadUInt16());
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── Geoscape event display (separate from channels) ───────────────
        // Both raise and dismiss lead with a host-synthesized per-OCCURRENCE id (u16, see EventOccurrenceIds):
        // the reusable GeoscapeEvent.EventID def-name collides when two occurrences of the same def fire, so the
        // occurrence id is the real correlation key (def-name is carried only for the native rebuild + logging).
        // Both host and client run the SAME build, so the occurrence id is a clean REQUIRED leading field (no
        // cross-version optionality); the trailing fields below keep their in-build optionality.
        //
        // EventDismiss: [occId:u16][eventId:string][choiceIndex:i32]?([u16 rewardLen][rewardBlob:N]?)([siteId:i32]?)
        // — host tells clients the answer was applied. choiceIndex is the index of the picked choice within
        // EventData.Choices: >= 0 means the choice produced a follow-up RESULT/OUTCOME page (clients rebuild +
        // show it natively); -1 means close-only (pure-INFO host-OK / decline). ALL trailing groups are OPTIONAL:
        //   • a 2-field [occId][eventId] payload decodes with choiceIndex = -1 (close-only);
        //   • a 3-field [occId][eventId][choiceIndex] payload decodes with an EMPTY reward blob and siteId = -1;
        // so the decoder never throws on a short buffer. The reward blob is a RewardDisplaySnapshot (the native
        // ShowReward delta lines) carried so the client mirrors the reward card; it is appended ONLY when
        // non-empty, keeping the no-reward 3-field wire byte-stable. siteId is GeoSite.SiteId (-1 = none → the
        // client result card falls back to StartingBase) — the SAME id the raise resolves (EventRaised's siteId);
        // it is appended ONLY when >= 0 (the no-site wire stays byte-stable). When a siteId follows a missing
        // reward, the u16 rewardLen is still written as 0 so the trailing siteId is unambiguous to the decoder.


        private static void WriteWireStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadWireStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("EventRaised identity: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }

        // ─── GeoLogNotice (0x6D) — small geoscape LOG toast mirror ────────────────────────────────────
        // Payload: [u8 highPriority][u16 len + UTF8 text]. The host ships the PRE-RESOLVED display line
        // (GeoscapeLogEntry.GenerateMessage()), not a def guid + typed params, because the client sim is frozen
        // and the native GeoscapeLog handlers never fire client-side — there is nothing to re-localize against.
        public static byte[] EncodeGeoLogNotice(string text, bool highPriority)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((byte)(highPriority ? 1 : 0));
                WriteWireStr(w, text);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeGeoLogNotice(byte[] data, out string text, out bool highPriority)
        {
            text = null; highPriority = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    highPriority = r.ReadByte() != 0;
                    text = ReadWireStr(r);
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex = -1)
            => EncodeEventDismiss(occurrenceId, eventId, choiceIndex, null);

        // Reward+site overload: appends [u16 rewardLen][rewardBlob] ONLY when the blob is non-empty and
        // [i32 siteId] ONLY when siteId >= 0, so a null/empty reward with no site yields the EXACT 3-field bytes
        // (no trailing length) — keeps the no-reward/no-site wire stable. When a siteId follows a MISSING reward,
        // the u16 rewardLen is still written as 0 so the trailing siteId is unambiguous on decode. rewardBlob is
        // a RewardDisplaySnapshot-encoded payload; siteId is GeoSite.SiteId (-1 = none, the SAME id the raise uses).
        // texts-less shim: callers that don't ship host-resolved wire texts (advance / fallback close).
        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex, byte[] rewardBlob, int siteId = -1)
            => EncodeEventDismiss(occurrenceId, eventId, choiceIndex, rewardBlob, siteId, null, null);

        // Wire-text overload: appends [u16len+UTF8 wireOutcome][u16len+UTF8 wireNarrative] at the END, ONLY when
        // at least one string is non-empty (keeps every no-text wire byte-identical). Because the texts trail the
        // OPTIONAL reward blob + siteId, their presence forces BOTH preceding optionals onto the wire (rewardLen 0
        // marker when no reward; siteId even when -1) so the trailing strings are unambiguous on decode.
        // wireOutcome = host-resolved SelectedChoice.Outcome.OutcomeText.GetText(context); wireNarrative =
        // host-resolved Description.Last().GetText(context) (the native SetClosingEncounter :332-336 pair) — the
        // client prefers these over local-def resolution, which is EMPTY for runtime-narrative defs (TFTV VoidOmen).
        public static byte[] EncodeEventDismiss(ushort occurrenceId, string eventId, int choiceIndex, byte[] rewardBlob, int siteId, string wireOutcome, string wireNarrative)
        {
            bool hasReward = rewardBlob != null && rewardBlob.Length > 0;
            bool hasTexts = !string.IsNullOrEmpty(wireOutcome) || !string.IsNullOrEmpty(wireNarrative);
            bool hasSite = siteId >= 0 || hasTexts;   // texts force the siteId slot (unambiguous trailing reads)
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(occurrenceId);
                w.Write(eventId ?? "");
                w.Write(choiceIndex);
                // Write the reward-length field when there is a reward OR a trailing siteId (so the siteId is
                // never mistaken for a reward length on decode); length is 0 when no reward is present.
                if (hasReward || hasSite)
                {
                    if (hasReward)
                    {
                        // The wire length field is u16; refuse to silently truncate an oversized reward blob.
                        if (rewardBlob.Length > ushort.MaxValue)
                            throw new ArgumentOutOfRangeException(nameof(rewardBlob),
                                "Reward blob exceeds the u16 length field (" + rewardBlob.Length + " > " + ushort.MaxValue + ").");
                        w.Write((ushort)rewardBlob.Length);
                        w.Write(rewardBlob);
                    }
                    else
                    {
                        w.Write((ushort)0);   // no reward, but a siteId follows → empty-length marker
                    }
                }
                if (hasSite) w.Write(siteId);
                if (hasTexts)
                {
                    // Host-resolved result texts at the END of the payload.
                    WriteWireStr(w, wireOutcome);
                    WriteWireStr(w, wireNarrative);
                }
                return ms.ToArray();
            }
        }

        // 3-out overload (occId, eventId, choiceIndex) — kept for callers/tests that ignore the reward blob + site.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out _, out _);

        // 4-out overload (… rewardBlob) — kept for callers/tests that ignore the trailing siteId.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out rewardBlob, out _);

        // 5-out overload (… siteId) — kept for callers/tests that ignore the trailing wire texts.
        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob, out int siteId)
            => TryDecodeEventDismiss(data, out occurrenceId, out eventId, out choiceIndex, out rewardBlob, out siteId, out _, out _);

        public static bool TryDecodeEventDismiss(byte[] data, out ushort occurrenceId, out string eventId, out int choiceIndex, out byte[] rewardBlob, out int siteId, out string wireOutcome, out string wireNarrative)
        {
            occurrenceId = 0; eventId = null; choiceIndex = -1; rewardBlob = new byte[0]; siteId = -1;
            wireOutcome = ""; wireNarrative = "";
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    occurrenceId = r.ReadUInt16();
                    eventId = r.ReadString();
                    // Optional trailing field: absent in a 2-field payload → leave choiceIndex = -1
                    // (close-only), so a close-only dismiss still decodes on a short buffer.
                    if (ms.Length - ms.Position >= sizeof(int)) choiceIndex = r.ReadInt32();
                    // Optional trailing reward blob: [u16 len][len bytes]. Absent in a 3-field payload
                    // → leave an empty blob (reward-less result card). Only accept it when the FULL declared
                    // length is present (no partial accept). A len of 0 (empty-length marker that precedes a
                    // trailing siteId) consumes the u16 only, leaving the siteId for the read below.
                    if (ms.Length - ms.Position >= sizeof(ushort))
                    {
                        int len = r.ReadUInt16();
                        if (len > 0 && ms.Length - ms.Position >= len) rewardBlob = r.ReadBytes(len);
                    }
                    // Optional trailing siteId: absent in an old payload → leave siteId = -1 (no site → the
                    // result card falls back to StartingBase). Mirrors EventRaised's trailing-optional fields.
                    if (ms.Length - ms.Position >= sizeof(int)) siteId = r.ReadInt32();
                    // Optional trailing wire texts: [u16len+UTF8 wireOutcome][u16len+UTF8 wireNarrative]. Absent
                    // → empty strings (client keeps its local-def fallback). Guarded reads (never partial-accept,
                    // never throw) so a truncated text block still yields the leading fields.
                    wireOutcome = TryReadWireStr(r, ms);
                    wireNarrative = TryReadWireStr(r, ms);
                    return true;
                }
            }
            catch { return false; }
        }

        // Length-guarded optional wire string: "" when absent/truncated (never throws, never partial-accepts).
        private static string TryReadWireStr(BinaryReader r, MemoryStream ms)
        {
            if (ms.Length - ms.Position < sizeof(ushort)) return "";
            int len = r.ReadUInt16();
            if (len == 0) return "";
            if (ms.Length - ms.Position < len) return "";
            return Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        // The bespoke geoscape event CHOICE CLAIM codec (client->host [occId:u16][choiceIndex:i32]) was retired:
        // event-choice resolution now rides AnswerEventAction over the research-style ActionRequest/ActionApply
        // relay (occId on the action wire). The host resolves a client's answer by driving its OWN native modal
        // (EventReflection.TryHostNativeResolve), falling back to the model-only CompleteEventByOccurrence.

        // ─── Geoscape report-window mirror (host->all show, Phase-A) ───────
        // Wire: [modalType:u8][variantTag:u8][siteId:i32][priority:i32][shareLevel:i32]
        //       [defId: u16len+UTF8][extraCount:u16][(u16len+UTF8) * extraCount]
        // A one-way notice (no dismiss packet): each peer closes its own copy locally with OK. variantTag
        // (ReportModalVariant) selects the client rebuild path; only the fields a variant needs are non-default
        // (siteId = -1, priority/shareLevel = 0, defId = "", no extras). Host + client run the SAME build, so
        // every field is a fixed REQUIRED field (no cross-version optionality this phase).
        // MISSION-OUTCOME TAIL (Batch-2 P3): a MissionOutcome payload — AND ONLY that variant — appends
        //   [missionClass:u8][outcomeState:i32][u16 rewardLen][rewardBlob:N]
        // behind the extras: the class discriminator + outcome state + RewardDisplaySnapshot display blob ride
        // the MESSAGE so the client rebuild never depends on the (possibly already tombstoned) P1 site mirror.
        // Every other variant's wire is BYTE-IDENTICAL to the Phase-A format (wire pin kept). Decode of the tail
        // is length-guarded: a truncated tail yields the defaults (class 0 → the rebuild skips gracefully).
        // BATCH-3 TAIL (P4+P5): every stamped payload appends [occId:u16][displaySeq:u32] at the very END
        // (after the variant tail): the P5 report occurrence id (client ReportOccurrenceDedup → STUN
        // double-send idempotent) + the P4 unified display-order stamp (nativePriority already rides the
        // existing Priority field — the modal opener's priority IS the view-switch request priority). Written
        // ONLY when stamped (occId/displaySeq non-zero) so every pre-Batch-3 wire stays byte-identical; decode
        // is length-guarded → a legacy payload yields 0/0 (no dedup, direct display path).


        /// <summary>
        /// Host → all: close the mirrored BLOCKING report modal (ambush brief) on clients — the host resolved
        /// it (ModalResultCallback: Confirm→LaunchMission / any other result). Wire: [modalType:u8] plus the
        /// Batch-3 P5 optional tail ([occId:u16-LE], written only when non-zero) so the client dedups a STUN
        /// double-sent hide exactly like a show; a legacy 1-byte wire decodes occId 0 (never deduped).
        /// </summary>
        public static byte[] EncodeReportModalHide(byte modalType) => new[] { modalType };

        public static byte[] EncodeReportModalHide(byte modalType, ushort occId)
        {
            if (occId == 0) return new[] { modalType };
            return new[] { modalType, (byte)(occId & 0xFF), (byte)(occId >> 8) };
        }

        public static bool TryDecodeReportModalHide(byte[] data, out byte modalType)
            => TryDecodeReportModalHide(data, out modalType, out _);

        public static bool TryDecodeReportModalHide(byte[] data, out byte modalType, out ushort occId)
        {
            modalType = 0;
            occId = 0;
            if (data == null || data.Length < 1) return false;
            modalType = data[0];
            if (data.Length >= 3) occId = (ushort)(data[1] | (data[2] << 8));
            return true;
        }

        // ─── Unified surface envelope (SurfaceRouter chokepoint) ───────────
        // Wire: [surfaceId:u8][kind:u8][len:u16][payload:N]. surfaceId selects a registered surface;
        // kind (SyncKind) selects request/apply/snapshot/delta. The inner payload is the surface's
        // own bytes (e.g. an action's Write output) — unchanged from the legacy per-packet format.

        public static byte[] EncodeEnvelope(byte surfaceId, SyncKind kind, byte[] payload)
        {
            payload = payload ?? new byte[0];
            // The wire length field is u16; refuse to silently truncate an oversized payload.
            if (payload.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(payload),
                    "Envelope payload exceeds the u16 length field (" + payload.Length + " > " + ushort.MaxValue + ").");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(surfaceId);
                w.Write((byte)kind);
                w.Write((ushort)payload.Length);
                w.Write(payload);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEnvelope(byte[] data, out byte surfaceId, out SyncKind kind, out byte[] payload)
        {
            surfaceId = 0; kind = SyncKind.ActionRequest; payload = null;
            // Require the full 4-byte header [surfaceId:u8][kind:u8][len:u16] before reading anything.
            if (data == null || data.Length < 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    byte sid = r.ReadByte();
                    byte kindByte = r.ReadByte();
                    // Forward-compat: an undefined kind byte is a graceful drop, never a crash.
                    if (!Enum.IsDefined(typeof(SyncKind), kindByte)) return false;
                    ushort len = r.ReadUInt16();
                    // No partial accept: the declared payload length must actually be present.
                    if (ms.Length - ms.Position < len) return false;
                    surfaceId = sid;
                    kind = (SyncKind)kindByte;
                    payload = r.ReadBytes(len);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
