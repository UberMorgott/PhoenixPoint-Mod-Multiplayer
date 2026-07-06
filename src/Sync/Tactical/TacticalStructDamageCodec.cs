using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the STRUCTURAL-DESTRUCTION mirror surface <c>tac.structdamage</c>
    /// (0x96, host→all — spec TS6). Closes the destructibles blind spot: the frozen client's walls / floors /
    /// props stay solid, so cover / line-of-fire / navigation diverge from the host. TS6 mirrors destruction
    /// EVENTS (re-applies the SAME native damage to the SAME destructible on the client) rather than a full
    /// voxel-state dump — causally correct + far cheaper (the native destruction cascade then runs identically
    /// → cover removed, LoS opened, nav mesh updated natively).
    ///
    /// CAPTURE MODEL — the combat-damage funnel. Every shot / burst / grenade / explosion / overwatch hit to a
    /// wall / floor / prop funnels through the one leaf <c>DestructableDamageReceiver.ApplyDamage(DamageResult)</c>
    /// (each affected TILE of a <c>Destructable</c> wall/floor, and the single receiver of a <c>Breakable</c> prop,
    /// gets it — verified in the decompile). This is DISJOINT from TS3's ground-hazard voxels (fire/goo/mist ride
    /// <c>TacticalVoxel.SetVoxelType</c>, a DIFFERENT system) — TS3 (0x94) and TS6 (0x96) never touch the same leaf.
    /// The host postfixes that funnel, buffers the hits per flush heartbeat, and broadcasts them here; the client
    /// re-applies the native damage to the same destructible → identical geometry.
    ///
    /// CROSS-SIDE IDENTITY (R2 — deterministic). A destructible is keyed by its <c>SceneObjectId.GuidString</c>
    /// (<c>DestructableBase.GuidInScene</c>) — the game's OWN save/restore key for destructibles (native
    /// <c>DestructableBase.FindDestructableObject</c> resolves by exactly this id via
    /// <c>SceneObjectIdsComponent.GetObjectById</c>). It is baked/authored into the scene, so for a SHARED map
    /// (same <c>Site.MapPlotInstanceData</c>/<c>RandomSeed</c>) a given wall's guid is IDENTICAL host↔client — NOT a
    /// fragile FindObjectsOfType index. The <c>point</c> is the damaged receiver's aim-point WORLD position: for a
    /// <c>Destructable</c> it selects the exact TILE (the client feeds it back through the native
    /// <c>GetDamageReceiverForHit</c>, which re-derives the same tile from the same mesh/transform → no independent
    /// grid-index math to drift); for a <c>Breakable</c> it is the object center (the single receiver, point-agnostic).
    ///
    /// WIRE (host→all, carries LiveSeq):
    ///   [seq:u32][hitCount:u16]  then per hit:
    ///     [recLen:u16]                 // bytes of THIS record after this field — lets an older/newer peer SKIP an
    ///                                  //   unknown targetKind without desync (backward/forward-tolerant)
    ///     [targetKind:u8]              // 1 = destructible-receiver-hit (the only kind emitted; 0 reserved)
    ///     -- kind==1 payload:
    ///     [guidLen:u16][guid:UTF8]     // SceneObjectId.GuidString of the DestructableBase
    ///     [px:f32][py:f32][pz:f32]     // receiver aim-point world pos (tile/object key + replay impact point)
    ///     [healthDamage:f32]           // the native DamageResult.HealthDamage (respects per-tile toughness →
    ///                                  //   PARTIAL-damage state also converges, not just destruction)
    ///
    /// STRUCT-DAMAGE IS ADDITIVE (unlike TS3's last-write-wins voxel type): every captured hit is a distinct damage
    /// application, so hits are NOT coalesced/deduped — they ship in capture order and re-apply in order. A large
    /// explosion's many tile-hits coalesce only in the sense of packing into as few 0x96 messages as the cap allows.
    ///
    /// BACKWARD-TOLERANT: <c>hitCount</c> + per-hit <c>recLen</c> are self-describing length prefixes → an unknown
    /// future targetKind still frames its bytes so a decoder skips exactly its record. Truncation / a corrupt count →
    /// clean <c>false</c> (no partial accept), like <see cref="TacticalSurfaceCodec"/> — reliable transport
    /// guarantees full delivery.
    /// </summary>
    public static class TacticalStructDamageCodec
    {
        /// <summary>Target-kind discriminator (u8). 1 = a hit on a DestructableBase's damage receiver (the only kind
        /// emitted today). 0 is reserved so a future voxel/other kind can be added under the recLen-skip contract.</summary>
        public const byte KindDestructible = 1;

        /// <summary>Cap on hits packed into ONE 0x96 message (keeps the encoded payload well under the u16 envelope
        /// cap: 512 * ~60 B + framing ≈ 31 KB &lt; 65535). A larger flush window splits across messages (R3).</summary>
        public const int MaxHitsPerMessage = 512;

        /// <summary>One mirrored structural-damage hit: which destructible (guid), where (aim-point world pos, tile
        /// key + replay impact point), and how much health damage.</summary>
        public sealed class StructHit
        {
            public byte TargetKind;
            public string Guid;
            public float Px, Py, Pz;
            public float HealthDamage;

            public StructHit() { TargetKind = KindDestructible; Guid = string.Empty; }
            public StructHit(string guid, float px, float py, float pz, float healthDamage, byte targetKind = KindDestructible)
            {
                TargetKind = targetKind;
                Guid = guid ?? string.Empty;
                Px = px; Py = py; Pz = pz;
                HealthDamage = healthDamage;
            }
        }

        /// <summary>A batch of hits sharing one LiveSeq (one flush heartbeat / one packed message).</summary>
        public sealed class StructBatch
        {
            public uint Seq;
            public List<StructHit> Hits = new List<StructHit>();

            public StructBatch() { }
            public StructBatch(uint seq, List<StructHit> hits)
            {
                Seq = seq; Hits = hits ?? new List<StructHit>();
            }
        }

        // ─── Encode / Decode ─────────────────────────────────────────────────

        public static byte[] EncodeStructDamage(StructBatch batch)
        {
            var hits = batch?.Hits ?? new List<StructHit>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(batch != null ? batch.Seq : 0u);
                w.Write((ushort)hits.Count);
                foreach (var h in hits)
                {
                    // Build the record body first so we can length-prefix it (recLen = bytes AFTER the recLen field).
                    byte[] guidBytes = Encoding.UTF8.GetBytes(h.Guid ?? string.Empty);
                    using (var rec = new MemoryStream())
                    using (var rw = new BinaryWriter(rec, Encoding.UTF8))
                    {
                        rw.Write(h.TargetKind);
                        rw.Write((ushort)guidBytes.Length);
                        rw.Write(guidBytes);
                        rw.Write(h.Px);
                        rw.Write(h.Py);
                        rw.Write(h.Pz);
                        rw.Write(h.HealthDamage);
                        byte[] body = rec.ToArray();
                        w.Write((ushort)body.Length);
                        w.Write(body);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a 0x96 struct-damage batch. Returns false (no partial accept) on any truncation or a
        /// record/guid length exceeding the remaining buffer (guards a corrupt huge count). An unknown targetKind
        /// is SKIPPED via its recLen (forward-tolerant) — the hit is simply not emitted into the result.</summary>
        public static bool TryDecodeStructDamage(byte[] data, out StructBatch batch)
        {
            batch = null;
            // Minimum: u32 seq + u16 hitCount.
            if (data == null || data.Length < 6) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int hitCount = r.ReadUInt16();
                    var hits = new List<StructHit>(hitCount);
                    for (int i = 0; i < hitCount; i++)
                    {
                        if (ms.Length - ms.Position < 2) return false;        // recLen
                        int recLen = r.ReadUInt16();
                        long recStart = ms.Position;
                        if (recLen > ms.Length - recStart) return false;      // record claims more than remains
                        if (recLen < 1) return false;                         // must at least hold a targetKind

                        byte kind = r.ReadByte();
                        if (kind == KindDestructible)
                        {
                            // kind==1 known layout: guid + point(3f) + hp(f). Bounds-check against the record end.
                            if (ms.Position - recStart + 2 > recLen) return false;
                            int guidLen = r.ReadUInt16();
                            long afterGuid = ms.Position + guidLen;
                            if (guidLen < 0 || afterGuid - recStart > recLen) return false;
                            // Fixed tail after the guid: 3f point + 1f hp = 16 bytes.
                            if (afterGuid + 16 - recStart > recLen) return false;
                            string guid = Encoding.UTF8.GetString(r.ReadBytes(guidLen));
                            float px = r.ReadSingle();
                            float py = r.ReadSingle();
                            float pz = r.ReadSingle();
                            float hp = r.ReadSingle();
                            hits.Add(new StructHit(guid, px, py, pz, hp, kind));
                        }
                        // Always resync to the record boundary — tolerate a known record's future trailing bytes
                        // AND fully skip an unknown targetKind (its bytes are framed by recLen).
                        ms.Position = recStart + recLen;
                    }
                    batch = new StructBatch(seq, hits);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
