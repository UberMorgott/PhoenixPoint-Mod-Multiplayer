using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Tactical replication surface ids + wire codecs (spec §5). Increment 1 defines only the
    /// <c>tac.deploy</c> surface (host→all deploy snapshot). Later increments add tac.move / tac.damage /
    /// tac.turn / tac.ability on adjacent ids.
    ///
    /// RAIL (spec §3.6, with a grounded Inc-1 adaptation): tac.deploy is a host→ALL one-way push of a
    /// large, idempotent snapshot — NOT a request/apply action. It rides the SAME 0x67 SyncEnvelope
    /// inbound chokepoint the geoscape sync uses (<see cref="Multiplayer.Network.Sync.SurfaceRouter"/>),
    /// but via that router's tactical FAST-PATH hook (<c>SurfaceRouter.TacticalInbound</c>) which bypasses
    /// the action relay's shared <c>SequenceTracker</c>. Why not the action-apply path: that path gates on
    /// a SINGLE global monotonic seq shared with geoscape; a request-free host push has no correct fresh
    /// seq to assign from the tactical module, and forcing one would poison post-mission geoscape ordering.
    /// The hook is null unless tactical init arms it → inert for the geoscape/event sync. tac.deploy is NOT
    /// routed onto the dead legacy 0x20-0x24 tactical path.
    /// </summary>
    public static class TacticalSurfaceIds
    {
        // Tactical surface ids live in a HIGH, non-overlapping byte range (0x80+) so they never collide the
        // geoscape action surfaces (1-30) or the state-channel ids (1-5). The SurfaceRouter tactical hook
        // keys on this id to claim the envelope.
        public const ushort TacDeploy = 0x80;        // 128: host→all full deploy snapshot (single envelope, fits the cap)
        public const ushort TacDeployChunk = 0x81;   // 129: host→all deploy snapshot FRAGMENT (one of N chunks, over-cap path)

        // ─── Increment 2/4 LIVE outcome + intent surfaces (move + end-turn) ───────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path as tac.deploy, but these carry
        // a self-contained tactical seq (TacticalLiveSeq) instead of the deploy generation. Intents are
        // client→host (SendToHost); outcomes are host→all (BroadcastToAll). Kept in the 0x82+ tactical
        // byte range so they never collide the geoscape action/state surfaces.
        public const ushort TacIntentMove = 0x82;    // 130: client→host  "move actor netId to pos"  (intent, carries nonce)
        public const ushort TacMove = 0x83;          // 131: host→all     "actor netId landed at pos" (outcome, carries seq)
        public const ushort TacIntentEndTurn = 0x84; // 132: client→host  "end the current turn"      (intent, carries nonce)
        public const ushort TacTurn = 0x85;          // 133: host→all     "current faction advanced"  (outcome, carries seq)
        public const ushort TacMoveStart = 0x86;     // 134: host→all     "actor netId begins move to pos" (start, carries own seq) — client animates CONCURRENTLY with host; tac.move (0x83) END reconciles the exact final cell

        // ─── Increment 3a LIVE combat/damage surfaces (shoot intent + damage outcome) ───────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Inc3a host-authoritative
        // shot replication: a client sends a SHOOT intent, the host runs the real shot (roll → projectile
        // → ApplyDamage), then the host broadcasts the FINAL applied DamageResult so every peer's mirror
        // applies identical damage (the client's own roll chain is suppressed by FireWeaponPatch). All
        // damage funnels through TacticalActorBase.ApplyDamage, so this one surface covers shots, melee,
        // overwatch, AI, and the death cascade alike.
        public const ushort TacIntentAbility = 0x87; // 135: client→host  "actor netId shoots ability@guid at target" (intent, carries nonce)
        public const ushort TacDamage = 0x88;        // 136: host→all     "actor netId took this DamageResult" (outcome, carries seq)

        // ─── Inc Vision: host→client player-faction vision replication ──────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The client mirror suppresses local
        // perception, so the shared player faction's TacticalFactionVision.KnownActors stays empty → spotted-enemy
        // icons / RED-GREY target markers / the shoot target-gate all read empty. The host snapshots its player
        // faction's KnownActors on every FactionKnowledgeChangedEvent and pushes a full RECONCILE snapshot here;
        // the client sets/forgets to match. Outcome-style host→all push, carries its own TacticalLiveSeq.
        public const ushort TacVision = 0x89;        // 137: host→all     "player-faction vision snapshot" (reconcile, carries seq)

        // ─── Inc Equip: host-authoritative WEAPON/EQUIPMENT-SWAP replication ────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Mirrors the move/shoot model:
        // a CLIENT switching a soldier's active weapon/equipment is suppressed and sends a tac.intent.equip
        // (carries the equipment SLOT INDEX, stable across host/client via the shared save); the HOST re-invokes
        // the real EquipmentComponent.SetSelectedEquipment, then broadcasts tac.equip so every peer mirrors the
        // selected equipment (updating BOTH the visible weapon and the abilities the actor exposes). Selecting a
        // weapon is FREE (no AP/WP), so the outcome carries no AP-after. Self-contained tactical seq (last-writer-
        // wins) like tac.move / tac.turn / tac.vision.
        public const ushort TacIntentEquip = 0x8A;   // 138: client→host  "actor netId selects equipment@slot" (intent, carries nonce)
        public const ushort TacEquip = 0x8B;         // 139: host→all     "actor netId now has equipment@slot selected" (outcome, carries seq)

        // ─── Inc Overwatch: host-authoritative OVERWATCH-ARM replication ────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. In co-op a client putting a
        // soldier on overwatch ran ONLY locally — the HOST (the authority that runs enemy turns) never armed
        // that soldier, so the host never triggered the reaction fire, and the watch cone never showed on peers.
        // Mirrors the move/shoot/equip model: a CLIENT arming overwatch is suppressed and sends a
        // tac.intent.overwatch (carries the flattened watch CONE — built client-side, the host can't re-derive
        // it); the HOST rebuilds the cone, re-invokes OverwatchAbility.Activate so it is authoritatively armed
        // (→ it triggers reaction fire on enemy moves; the reaction DAMAGE already replicates via tac.damage),
        // then broadcasts tac.overwatch.state on every SetCone (arm AND clear/consume) so every peer mirrors the
        // cone cosmetically (the client mirror is INERT — client enemy-moves carry TriggerOverwatch=false, so a
        // client-side OverwatchStatus never double reaction-fires). Self-contained tactical seq (last-writer-wins).
        public const ushort TacIntentOverwatch = 0x8C;   // 140: client→host  "actor netId arms overwatch watching cone" (intent, carries nonce + cone)
        public const ushort TacOverwatchState = 0x8D;    // 141: host→all     "actor netId overwatch armed(with cone)/cleared" (state, carries seq)

        // ─── TS2: GENERIC (non shoot/melee) ability-INTENT relay ──────────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Where 0x87 tac.intent.ability is the
        // shoot/melee DAMAGE-DEALER relay (limb-snap-tuned, UNTOUCHED), 0x8E is the RICHER generic client→host
        // intent for OWN-soldier abilities BEYOND shoot/bash (heal, recover-will, rally, psychic scream, …). It
        // carries a target-KIND discriminator (none/self · actor · pos · equipment-slot · object) so ONE surface
        // expresses every non-damage ability's target shape. The client SUPPRESSES the local Activate (frozen sim)
        // and relays this intent; the host re-resolves the ability by def guid + Activates it authoritatively; the
        // outcome rides the ALREADY-SHIPPED surfaces (0x8F AP/WP/Health/status + tac.damage 0x88 + TS1 spawn).
        // Peer-keyed IntentDedup (like 0x87). See TacticalGenericIntentCodec / TacticalCombatSync.HostOnGenericIntent.
        public const ushort TacIntentGeneric = 0x8E;     // 142: client→host  "actor netId activates ability@guid on <target-kind>" (intent, carries nonce)

        // ─── Inc T1: GENERIC per-actor STATE-DELTA spine (state-spine design §9) ────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The REUSABLE spine: a per-actor
        // STATE-DELTA (host→all) that mirrors mutable actor fields so any field/stat/status syncs by default.
        // T1 payload = AP/WP + the generic STATUS SET (buffs/debuffs/stances/disables). EXTENSIBLE: each
        // per-actor record carries a u16 fieldMask, so later increments fold in position/facing/health/armor/
        // selected-equip/overwatch-cone bits WITHOUT a wire break. Host computes a per-actor signature each
        // flush tick and broadcasts ONLY changed actors (idle actor = 0 bytes); client applies ABSOLUTE values
        // under a re-entrancy flag. Runs ALONGSIDE the existing per-action surfaces (additive convergence layer
        // — the AP/WP + targeted statuses it carries have no existing owner, so no conflict). Self-contained
        // tactical seq (last-writer-wins) like the other live surfaces. (0x8E = TacIntentGeneric, TS2 — allocated above.)
        public const ushort TacActorState = 0x8F;        // 143: host→all     "per-actor AP/WP + status-set delta" (state, carries seq)

        // ─── Feature C: client-side ATTACK ANIMATION (tac.fire.start) ────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The host broadcasts this at the
        // MOMENT an actor BEGINS a shoot/grenade attack (ShootAbility — melee is a documented follow-on), so the
        // client mirror plays the shooting/throw animation CONCURRENTLY with the host. ANIMATION-ONLY: DAMAGE stays owned by tac.damage
        // (0x88); the client replays FireWeaponAtTargetCrt with AttackType.Synced + a neutered FireProjectile
        // (no projectile → ZERO client damage) under a camera-hint guard (no camera fly). (0x8E = TacIntentGeneric
        // TS2; 0x8F = TacActorState → this takes the next free id 0x90.)
        public const ushort TacFireStart = 0x90;         // 144: host→all     "actor netId begins attack@guid at target" (start, carries seq)

        // ─── Feature C (melee): client-side MELEE ATTACK ANIMATION (tac.melee.start) ──────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. The MELEE counterpart of
        // tac.fire.start (0x90): the host broadcasts this at the MOMENT an actor BEGINS a melee swing
        // (BashAbility — which animates via its OWN BashCrt, NOT FireWeaponAtTargetCrt, so it needs its own
        // surface). ANIMATION-ONLY: DAMAGE stays owned by tac.damage (0x88). Phase 1 is the WIRE FOUNDATION
        // only — the client replays a STUB no-op (logs); the BashCrt-shaped replay + damage/cost neuter is a
        // follow-on. Wire = tac.fire.start MINUS shotCount (a melee is one swing). Takes the next free id 0x91.
        public const ushort TacMeleeStart = 0x91;        // 145: host→all     "actor netId begins melee swing@guid at target" (start, carries seq)

        // ─── TS1: mid-battle actor SPAWN / DESPAWN mirror ─────────────────────────────────────────────────
        // Same 0x67 envelope rail + SurfaceRouter.TacticalInbound fast-path. Closes the structural blind spot
        // "things that are NOT deploy-time actors" (reinforcements, egg hatch, siren summon, turret/shield deploy,
        // resurrect, morph). Both host→ALL (3+ player safe), carry their own TacticalLiveSeq (last-writer-wins).
        //   • spawn (0x92): [seq][netId][faction][pos][ActorCreateData blob][ActorInstanceData blob] — the client
        //     materializes a mirror actor via ActorSpawner.SpawnActor and binds the host netId; it then joins the
        //     0x8F delta + tac.damage streams. The ComponentSetDef rides the ActorCreateData blob BY VALUE
        //     (BaseDef.SerializeDefContents), since a spawned actor's def is a runtime def (R1). See
        //     TacticalActorLifecycleSync / TacticalActorLifecycleCodec.
        //   • despawn (0x93): [seq][netId][reason] — non-damage removal (evac/morph/off-map/expiry); the client
        //     removes the mirror + registry cleanup. Damage-death stays owned by tac.damage (0x88).
        // 0x8E stays RESERVED for the future generic ability-INTENT (TS2). These take the next free ids 0x92/0x93.
        public const ushort TacActorSpawn = 0x92;        // 146: host→all     "materialize mid-battle actor@netId (blob)" (carries seq)
        public const ushort TacActorDespawn = 0x93;      // 147: host→all     "remove actor@netId (non-damage despawn)"   (carries seq)
    }

    /// <summary>
    /// PURE chunking layer for the <c>tac.deploy</c> snapshot (FIX 1). The full <see cref="TacticalDeployCodec"/>
    /// payload is hundreds of KB, but <c>SyncProtocol.EncodeEnvelope</c> caps an envelope payload at a u16
    /// (65535 B) and THROWS on overflow. We must NOT touch that hot file, so we split the codec payload into
    /// fragments that each fit comfortably under the cap, send each as its OWN <c>tac.deployChunk</c> envelope
    /// over the SAME tactical rail, and REASSEMBLE on the receive side before handing the whole payload to
    /// <see cref="TacticalDeployCodec.TryDecode"/>.
    ///
    /// Fragment wire (engine-free, BinaryWriter/Reader only → unit-testable):
    ///   [siteId:i32][deployGeneration:i32][chunkIndex:i32][chunkCount:i32][totalLen:i32][fragLen:i32][frag:N]
    ///
    /// Reassembly is ORDER-INDEPENDENT (Stun transport is unordered) and IDEMPOTENT (a duplicate chunk is
    /// ignored; a (siteId,deployGeneration) set reassembles exactly once). <see cref="ChunkReassembler"/> is a
    /// pure buffer keyed by (siteId,deployGeneration) so it unit-tests with no engine types.
    /// </summary>
    public static class TacticalDeployChunkCodec
    {
        /// <summary>Fragment payload size (bytes of the inner deploy-codec blob per chunk). Chosen well under
        /// the u16 envelope cap (65535): one fragment envelope payload = this + the 24-byte chunk header +
        /// the 4-byte envelope header = 49180 B &lt; 65535. A round 48 KiB keeps a comfortable margin.</summary>
        public const int FragmentSize = 48 * 1024;   // 49152

        /// <summary>The fixed chunk-header size in bytes: 6 × i32.</summary>
        public const int HeaderSize = 6 * 4;

        /// <summary>True when the whole codec payload fits in a SINGLE envelope (no chunking needed). The
        /// threshold leaves headroom under the 65535 cap for the 4-byte envelope header + slack.</summary>
        public const int SingleEnvelopeMax = 60000;

        /// <summary>The decoded header + fragment of one chunk.</summary>
        public sealed class Fragment
        {
            public int SiteId;
            public int DeployGeneration;
            public int ChunkIndex;
            public int ChunkCount;
            public int TotalLen;
            public byte[] Data;
        }

        /// <summary>Split a full deploy-codec payload into ordered chunk-envelope payloads (each ready to hand
        /// to <c>EncodeEnvelope(TacDeployChunk, …)</c>). Always produces ≥1 chunk (an empty/small payload is a
        /// single chunk). The caller decides whether to chunk at all via <see cref="SingleEnvelopeMax"/>.</summary>
        public static List<byte[]> Split(int siteId, int deployGeneration, byte[] full)
        {
            full = full ?? new byte[0];
            int frag = FragmentSize;
            int count = full.Length == 0 ? 1 : (full.Length + frag - 1) / frag;
            var chunks = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int off = i * frag;
                int len = System.Math.Min(frag, full.Length - off);
                if (len < 0) len = 0;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(siteId);
                    w.Write(deployGeneration);
                    w.Write(i);
                    w.Write(count);
                    w.Write(full.Length);
                    w.Write(len);
                    if (len > 0) w.Write(full, off, len);
                    chunks.Add(ms.ToArray());
                }
            }
            return chunks;
        }

        /// <summary>Decode one chunk-envelope payload. Returns false (no partial accept) on truncation.</summary>
        public static bool TryDecode(byte[] data, out Fragment fragment)
        {
            fragment = null;
            if (data == null || data.Length < HeaderSize) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int siteId = r.ReadInt32();
                    int gen = r.ReadInt32();
                    int idx = r.ReadInt32();
                    int count = r.ReadInt32();
                    int totalLen = r.ReadInt32();
                    int fragLen = r.ReadInt32();
                    if (count <= 0 || idx < 0 || idx >= count || totalLen < 0 || fragLen < 0) return false;
                    if (ms.Length - ms.Position < fragLen) return false;
                    var buf = fragLen > 0 ? r.ReadBytes(fragLen) : new byte[0];
                    if (buf.Length != fragLen) return false;
                    fragment = new Fragment
                    {
                        SiteId = siteId, DeployGeneration = gen, ChunkIndex = idx,
                        ChunkCount = count, TotalLen = totalLen, Data = buf
                    };
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// PURE order-independent, idempotent reassembler for chunked <c>tac.deploy</c> payloads (FIX 1). Feed it
    /// decoded <see cref="TacticalDeployChunkCodec.Fragment"/>s in any order, with duplicates; when the last
    /// missing fragment of a (siteId,deployGeneration) set arrives it returns the fully concatenated payload
    /// ONCE (subsequent or duplicate chunks for that key return null). A higher deployGeneration for the same
    /// site discards a stale partial set (re-deploy / resync). Engine-free → unit-testable.
    /// </summary>
    public sealed class ChunkReassembler
    {
        private sealed class Pending
        {
            public int Generation;
            public int Count;
            public int TotalLen;
            public byte[][] Frags;
            public bool[] Seen;
            public int SeenCount;
            public bool Completed;   // guards exactly-once completion (idempotent dup handling)
        }

        // Keyed by siteId. One in-flight assembly per site (a newer generation replaces an older partial).
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        // Last fully-assembled generation per site — so re-fed chunks of an ALREADY-completed set (reliable
        // double-send) are dropped instead of re-completing. (The pending buffer is freed on completion to
        // save memory; this tiny per-site int is the exactly-once guard that survives.)
        private readonly Dictionary<int, int> _completedGeneration = new Dictionary<int, int>();

        /// <summary>Accept one fragment. Returns the fully reassembled payload exactly once (when the set
        /// becomes complete), else null. Idempotent: duplicate / post-completion chunks return null.</summary>
        public byte[] Accept(TacticalDeployChunkCodec.Fragment f)
        {
            if (f == null) return null;

            // Already assembled this (or a newer) generation for this site → drop any straggler/duplicate
            // chunk of a completed set (reliable transport double-send) without re-completing.
            if (_completedGeneration.TryGetValue(f.SiteId, out var doneGen) && f.DeployGeneration <= doneGen)
                return null;

            if (!_pending.TryGetValue(f.SiteId, out var p) || p.Generation != f.DeployGeneration)
            {
                // New site, or a newer generation for this site → start (replace) the assembly. Ignore a
                // stale OLDER generation entirely.
                if (p != null && f.DeployGeneration < p.Generation) return null;
                p = new Pending
                {
                    Generation = f.DeployGeneration,
                    Count = f.ChunkCount,
                    TotalLen = f.TotalLen,
                    Frags = new byte[f.ChunkCount][],
                    Seen = new bool[f.ChunkCount],
                    SeenCount = 0,
                    Completed = false
                };
                _pending[f.SiteId] = p;
            }

            if (p.Completed) return null;                       // already hydrated this set → idempotent drop
            if (f.ChunkIndex < 0 || f.ChunkIndex >= p.Count) return null;
            if (p.Seen[f.ChunkIndex]) return null;              // duplicate chunk → idempotent drop

            p.Seen[f.ChunkIndex] = true;
            p.Frags[f.ChunkIndex] = f.Data ?? new byte[0];
            p.SeenCount++;
            if (p.SeenCount < p.Count) return null;             // not complete yet

            // Complete: concat in index order into a TotalLen buffer.
            p.Completed = true;
            var outBuf = new byte[p.TotalLen];
            int off = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var d = p.Frags[i] ?? new byte[0];
                int n = System.Math.Min(d.Length, p.TotalLen - off);
                if (n > 0) System.Array.Copy(d, 0, outBuf, off, n);
                off += d.Length;
            }
            _pending.Remove(f.SiteId);                          // free the buffer
            _completedGeneration[f.SiteId] = f.DeployGeneration; // remember exactly-once for re-fed chunks
            return outBuf;
        }
    }

    /// <summary>
    /// PURE wire codec for the <c>tac.deploy</c> payload (spec §5). Engine-free (BinaryWriter/Reader only),
    /// so it unit-tests in isolation. Frames:
    ///   [missionSiteId:i32]
    ///   [gameParamsLen:i32][gameParams:N]   — native Serializer bytes of TacticalGameParams
    ///   [snapshotLen:i32][snapshot:N]       — native Serializer bytes of TacLevelInstanceData
    ///   [actorCount:i32]  then per actor: [netId:i32][geoUnitId:i32][x:f32][y:f32][z:f32]
    /// Big blobs are length-prefixed exactly like the existing <c>MessageSerializer</c> idiom (int32 len +
    /// bytes). The two blobs are produced/consumed by the native game <c>Serializer</c> on the engine side
    /// (see <c>TacticalDeploySync</c>); this codec only frames them + the actor table.
    /// </summary>
    public static class TacticalDeployCodec
    {
        /// <summary>The decoded tac.deploy header + actor table (blobs handed to the native Serializer).</summary>
        public sealed class DeployPayload
        {
            public int MissionSiteId;
            public byte[] GameParamsBytes;
            public byte[] SnapshotBytes;
            public List<TacticalActorRegistry.ActorRow> ActorTable;

            public DeployPayload(int missionSiteId, byte[] gameParamsBytes, byte[] snapshotBytes,
                List<TacticalActorRegistry.ActorRow> actorTable)
            {
                MissionSiteId = missionSiteId;
                GameParamsBytes = gameParamsBytes ?? new byte[0];
                SnapshotBytes = snapshotBytes ?? new byte[0];
                ActorTable = actorTable ?? new List<TacticalActorRegistry.ActorRow>();
            }
        }

        public static byte[] Encode(DeployPayload p)
        {
            var gameParams = p.GameParamsBytes ?? new byte[0];
            var snapshot = p.SnapshotBytes ?? new byte[0];
            var table = p.ActorTable ?? new List<TacticalActorRegistry.ActorRow>();

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p.MissionSiteId);

                w.Write(gameParams.Length);
                if (gameParams.Length > 0) w.Write(gameParams);

                w.Write(snapshot.Length);
                if (snapshot.Length > 0) w.Write(snapshot);

                w.Write(table.Count);
                foreach (var row in table)
                {
                    w.Write(row.NetId);
                    w.Write(row.GeoUnitId);
                    w.Write(row.X);
                    w.Write(row.Y);
                    w.Write(row.Z);
                }
                return ms.ToArray();
            }
        }

        public static byte[] Encode(int missionSiteId, byte[] gameParamsBytes, byte[] snapshotBytes,
            List<TacticalActorRegistry.ActorRow> actorTable)
            => Encode(new DeployPayload(missionSiteId, gameParamsBytes, snapshotBytes, actorTable));

        /// <summary>Decode a tac.deploy payload. Returns false (no partial accept) on any truncation —
        /// the reliable transport guarantees full delivery, so a short buffer is a clean drop.</summary>
        public static bool TryDecode(byte[] data, out DeployPayload payload)
        {
            payload = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int siteId = r.ReadInt32();

                    int gpLen = r.ReadInt32();
                    if (gpLen < 0 || ms.Length - ms.Position < gpLen) return false;
                    var gameParams = gpLen > 0 ? r.ReadBytes(gpLen) : new byte[0];
                    if (gameParams.Length != gpLen) return false;

                    int snapLen = r.ReadInt32();
                    if (snapLen < 0 || ms.Length - ms.Position < snapLen) return false;
                    var snapshot = snapLen > 0 ? r.ReadBytes(snapLen) : new byte[0];
                    if (snapshot.Length != snapLen) return false;

                    int n = r.ReadInt32();
                    if (n < 0) return false;
                    // Each row is fixed 5*4 = 20 bytes; guard the count against the remaining buffer so a
                    // corrupt huge count can't allocate wildly.
                    if ((long)n * 20 > ms.Length - ms.Position) return false;
                    var table = new List<TacticalActorRegistry.ActorRow>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int netId = r.ReadInt32();
                        int geoId = r.ReadInt32();
                        float x = r.ReadSingle();
                        float y = r.ReadSingle();
                        float z = r.ReadSingle();
                        table.Add(new TacticalActorRegistry.ActorRow(netId, geoId, x, y, z));
                    }

                    payload = new DeployPayload(siteId, gameParams, snapshot, table);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
