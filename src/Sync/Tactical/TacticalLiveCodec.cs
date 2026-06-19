using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codecs + seq/dedup for the LIVE tactical outcome rail (spec §5, Inc 2/4):
    /// soldier MOVE + END-TURN. BinaryWriter/Reader only, so it unit-tests in isolation alongside
    /// <see cref="TacticalDeployCodec"/>. The engine glue (<c>TacticalMoveSync</c> / <c>TacticalTurnSync</c>)
    /// binds the live game types via reflection and is NOT in the test assembly.
    ///
    /// Why a SELF-CONTAINED tactical seq (not the geoscape <c>SequenceTracker</c>): the deploy rail already
    /// rides the SurfaceRouter tactical FAST-PATH that deliberately bypasses the action relay's shared global
    /// seq (a request-free host push has no correct seq there, and reusing it would poison post-mission
    /// geoscape action ordering). The live outcome rail rides the SAME fast-path, so it carries its OWN
    /// monotonic seq stamped by the host and a per-surface last-writer-wins guard on the client.
    ///
    /// Frames:
    ///   tac.intent.move      [netId:i32][x:f32][y:f32][z:f32][nonce:u32]                  (client→host)
    ///   tac.move.start       [seq:u32][netId:i32][x:f32][y:f32][z:f32]                    (host→all)
    ///   tac.move             [seq:u32][netId:i32][x:f32][y:f32][z:f32][stopReason:i32]    (host→all)
    ///   tac.intent.endturn   [nonce:u32]                                                  (client→host)
    ///   tac.turn             [seq:u32][currentFactionIndex:i32][turnNumber:i32][factionDefGuid:string]
    /// </summary>
    public static class TacticalLiveCodec
    {
        // ─── tac.intent.move (client→host) ────────────────────────────────
        public struct MoveIntent
        {
            public int NetId;
            public float X, Y, Z;
            public uint Nonce;
            public MoveIntent(int netId, float x, float y, float z, uint nonce)
            { NetId = netId; X = x; Y = y; Z = z; Nonce = nonce; }
        }

        public static byte[] EncodeMoveIntent(int netId, float x, float y, float z, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveIntent(byte[] data, out MoveIntent intent)
        {
            intent = default(MoveIntent);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    uint nonce = r.ReadUInt32();
                    intent = new MoveIntent(netId, x, y, z, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move.start (host→all) ────────────────────────────────────
        // Broadcast at the MOMENT the host BEGINS a move (its own click or a relayed client intent), so the
        // client mirror animates CONCURRENTLY with the host instead of waiting for the END outcome. Mirrors
        // the EncodeMove layout MINUS stopReason (the destination is the requested cell, not the landed one).
        public struct MoveStart
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public MoveStart(uint seq, int netId, float x, float y, float z)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; }
        }

        public static byte[] EncodeMoveStart(uint seq, int netId, float x, float y, float z)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMoveStart(byte[] data, out MoveStart start)
        {
            start = default(MoveStart);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    start = new MoveStart(seq, netId, x, y, z);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.move (host→all) ──────────────────────────────────────────
        public struct MoveOutcome
        {
            public uint Seq;
            public int NetId;
            public float X, Y, Z;
            public int StopReason;
            public MoveOutcome(uint seq, int netId, float x, float y, float z, int stopReason)
            { Seq = seq; NetId = netId; X = x; Y = y; Z = z; StopReason = stopReason; }
        }

        public static byte[] EncodeMove(uint seq, int netId, float x, float y, float z, int stopReason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq); w.Write(netId); w.Write(x); w.Write(y); w.Write(z); w.Write(stopReason);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeMove(byte[] data, out MoveOutcome outcome)
        {
            outcome = default(MoveOutcome);
            if (data == null || data.Length < 4 + 4 + 4 + 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int netId = r.ReadInt32();
                    float x = r.ReadSingle(); float y = r.ReadSingle(); float z = r.ReadSingle();
                    int stop = r.ReadInt32();
                    outcome = new MoveOutcome(seq, netId, x, y, z, stop);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.intent.endturn (client→host) ─────────────────────────────
        public static byte[] EncodeEndTurnIntent(uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeEndTurnIntent(byte[] data, out uint nonce)
        {
            nonce = 0;
            if (data == null || data.Length < 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    nonce = r.ReadUInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.turn (host→all) ──────────────────────────────────────────
        public struct TurnOutcome
        {
            public uint Seq;
            public int CurrentFactionIndex;
            public int TurnNumber;
            public string FactionDefGuid;
            public TurnOutcome(uint seq, int idx, int turnNumber, string guid)
            { Seq = seq; CurrentFactionIndex = idx; TurnNumber = turnNumber; FactionDefGuid = guid ?? ""; }
        }

        public static byte[] EncodeTurn(uint seq, int currentFactionIndex, int turnNumber, string factionDefGuid)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(currentFactionIndex);
                w.Write(turnNumber);
                w.Write(factionDefGuid ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryDecodeTurn(byte[] data, out TurnOutcome outcome)
        {
            outcome = default(TurnOutcome);
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int idx = r.ReadInt32();
                    int turn = r.ReadInt32();
                    string guid = r.ReadString();
                    outcome = new TurnOutcome(seq, idx, turn, guid);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.intent.ability (client→host, Inc 3a) ─────────────────────
        // A client SHOOT intent: which actor shoots which ability (by def guid) at which target. The target
        // is carried as BOTH a netId (actor target; -1 sentinel when the shot is at a bare position) AND a
        // position (always present — the host rebuilds a TacticalAbilityTarget from whichever is valid).
        public struct IntentAbility
        {
            public int ShooterNetId;
            public string AbilityDefGuid;
            public int TargetNetId;          // -1 sentinel when the target is a position, not an actor
            public float TX, TY, TZ;
            public uint Nonce;
            public IntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
                float tx, float ty, float tz, uint nonce)
            {
                ShooterNetId = shooterNetId; AbilityDefGuid = abilityDefGuid ?? ""; TargetNetId = targetNetId;
                TX = tx; TY = ty; TZ = tz; Nonce = nonce;
            }
        }

        public const int TargetNetIdNone = -1;   // sentinel: the shot target is a position, not an actor

        public static byte[] EncodeIntentAbility(int shooterNetId, string abilityDefGuid, int targetNetId,
            float tx, float ty, float tz, uint nonce)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(shooterNetId);
                w.Write(abilityDefGuid ?? "");
                w.Write(targetNetId);
                w.Write(tx); w.Write(ty); w.Write(tz);
                w.Write(nonce);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeIntentAbility(byte[] data, out IntentAbility intent)
        {
            intent = default(IntentAbility);
            // Minimum: i32 shooter + at least a 1-byte length-prefixed string + i32 target + 3*f32 + u32.
            if (data == null || data.Length < 4 + 1 + 4 + 12 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int shooter = r.ReadInt32();
                    string guid = r.ReadString();
                    int targetNetId = r.ReadInt32();
                    float tx = r.ReadSingle(); float ty = r.ReadSingle(); float tz = r.ReadSingle();
                    uint nonce = r.ReadUInt32();
                    intent = new IntentAbility(shooter, guid, targetNetId, tx, ty, tz, nonce);
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.damage (host→all, Inc 3a) ────────────────────────────────
        // A flattened DamageResult (DamageResult.cs) — the FINAL applied result the host captured in its
        // ApplyDamage postfix. The client rebuilds a DamageResult from this and applies it to the target
        // mirror, so HP/armor/stun/status/effects/stat-mods all converge. ImpactHit is intentionally omitted
        // (left default on rebuild — it only drives local hit FX, not authoritative state). The shooter's
        // post-shot AP/WP ride along so the client mirror can set the shooter's spent AP/WP exactly.
        public struct DamageStatus { public string DefGuid; public float Value; public int SourceNetId; }
        public struct DamageStatMod { public string StatName; public int ModKind; public float Value; }

        public sealed class DamagePayload
        {
            public uint Seq;
            public int TargetNetId;
            public int SourceNetId;            // -1 sentinel when the damage source is unresolved/null
            public float HealthDamage, ArmorDamage, ArmorMitigatedDamage, StunValue, HealValue;
            public float IfX, IfY, IfZ;        // ImpactForce
            public float DoX, DoY, DoZ;        // DamageOrigin
            public bool ForceHurt;
            public string DamageTypeDefGuid;
            public List<DamageStatus> Statuses = new List<DamageStatus>();
            public List<string> EffectGuids = new List<string>();
            public List<DamageStatMod> StatMods = new List<DamageStatMod>();
            public int ShooterNetId;           // -1 when the source is not the shooter / not carried
            public float ShooterApAfter, ShooterWpAfter;
        }

        public static byte[] EncodeDamage(DamagePayload p)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p.Seq);
                w.Write(p.TargetNetId);
                w.Write(p.SourceNetId);
                w.Write(p.HealthDamage); w.Write(p.ArmorDamage); w.Write(p.ArmorMitigatedDamage);
                w.Write(p.StunValue); w.Write(p.HealValue);
                w.Write(p.IfX); w.Write(p.IfY); w.Write(p.IfZ);
                w.Write(p.DoX); w.Write(p.DoY); w.Write(p.DoZ);
                w.Write(p.ForceHurt);
                w.Write(p.DamageTypeDefGuid ?? "");

                var statuses = p.Statuses ?? new List<DamageStatus>();
                w.Write(statuses.Count);
                foreach (var s in statuses) { w.Write(s.DefGuid ?? ""); w.Write(s.Value); w.Write(s.SourceNetId); }

                var effects = p.EffectGuids ?? new List<string>();
                w.Write(effects.Count);
                foreach (var e in effects) w.Write(e ?? "");

                var mods = p.StatMods ?? new List<DamageStatMod>();
                w.Write(mods.Count);
                foreach (var m in mods) { w.Write(m.StatName ?? ""); w.Write(m.ModKind); w.Write(m.Value); }

                w.Write(p.ShooterNetId);
                w.Write(p.ShooterApAfter); w.Write(p.ShooterWpAfter);
                return ms.ToArray();
            }
        }

        public static bool TryDecodeDamage(byte[] data, out DamagePayload payload)
        {
            payload = null;
            // Minimum fixed-size prefix (before the var-length string/lists): seq + target + source +
            // 5 dmg floats + 3+3 vector floats + bool. The strings/lists are guarded by the reader.
            if (data == null || data.Length < 4 + 4 + 4 + (5 * 4) + (6 * 4) + 1) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var p = new DamagePayload
                    {
                        Seq = r.ReadUInt32(),
                        TargetNetId = r.ReadInt32(),
                        SourceNetId = r.ReadInt32(),
                        HealthDamage = r.ReadSingle(),
                        ArmorDamage = r.ReadSingle(),
                        ArmorMitigatedDamage = r.ReadSingle(),
                        StunValue = r.ReadSingle(),
                        HealValue = r.ReadSingle(),
                        IfX = r.ReadSingle(), IfY = r.ReadSingle(), IfZ = r.ReadSingle(),
                        DoX = r.ReadSingle(), DoY = r.ReadSingle(), DoZ = r.ReadSingle(),
                        ForceHurt = r.ReadBoolean(),
                        DamageTypeDefGuid = r.ReadString(),
                    };

                    int statusCount = r.ReadInt32();
                    if (statusCount < 0 || statusCount > 4096) return false;
                    for (int i = 0; i < statusCount; i++)
                        p.Statuses.Add(new DamageStatus { DefGuid = r.ReadString(), Value = r.ReadSingle(), SourceNetId = r.ReadInt32() });

                    int effectCount = r.ReadInt32();
                    if (effectCount < 0 || effectCount > 4096) return false;
                    for (int i = 0; i < effectCount; i++) p.EffectGuids.Add(r.ReadString());

                    int modCount = r.ReadInt32();
                    if (modCount < 0 || modCount > 4096) return false;
                    for (int i = 0; i < modCount; i++)
                        p.StatMods.Add(new DamageStatMod { StatName = r.ReadString(), ModKind = r.ReadInt32(), Value = r.ReadSingle() });

                    p.ShooterNetId = r.ReadInt32();
                    p.ShooterApAfter = r.ReadSingle();
                    p.ShooterWpAfter = r.ReadSingle();
                    payload = p;
                    return true;
                }
            }
            catch { return false; }
        }

        // ─── tac.vision (host→all, Inc Vision) ────────────────────────────
        // A full RECONCILE snapshot of the player/viewer faction's KnownActors: which enemy/actor netIds the
        // host currently knows, and at what state (2=Revealed/red — seen+LOS; 1=Located/grey — position known).
        // The host pushes this on every FactionKnowledgeChangedEvent for the player faction; the client RECONCILES
        // its mirror of that faction's vision to the snapshot (set listed, forget absent). Self-contained tactical
        // seq (last-writer-wins) like tac.move / tac.turn. Engine-free codec → unit-testable.
        //   [seq:u32][viewerFactionIndex:i32][count:i32]  then per entry: [netId:i32][knownState:i32]
        public struct VisionEntry
        {
            public int NetId;
            public int KnownState;   // 2 = Revealed (red), 1 = Located (grey)
            public VisionEntry(int netId, int knownState) { NetId = netId; KnownState = knownState; }
        }

        public struct VisionSnapshot
        {
            public uint Seq;
            public int ViewerFactionIndex;
            public List<VisionEntry> Entries;
            public VisionSnapshot(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
            { Seq = seq; ViewerFactionIndex = viewerFactionIndex; Entries = entries ?? new List<VisionEntry>(); }
        }

        /// <summary>Wire state values (also the engine→client mapping). Higher = stronger knowledge.</summary>
        public const int VisionStateRevealed = 2;   // RED  — seen + line of sight
        public const int VisionStateLocated = 1;     // GREY — position known, no LOS

        public static byte[] EncodeVision(uint seq, int viewerFactionIndex, List<VisionEntry> entries)
        {
            entries = entries ?? new List<VisionEntry>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(seq);
                w.Write(viewerFactionIndex);
                w.Write(entries.Count);
                foreach (var e in entries) { w.Write(e.NetId); w.Write(e.KnownState); }
                return ms.ToArray();
            }
        }

        public static bool TryDecodeVision(byte[] data, out VisionSnapshot snapshot)
        {
            snapshot = default(VisionSnapshot);
            // Minimum: seq + viewerFactionIndex + count = 12 bytes.
            if (data == null || data.Length < 4 + 4 + 4) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    int viewerFactionIndex = r.ReadInt32();
                    int count = r.ReadInt32();
                    if (count < 0 || count > 4096) return false;
                    // Each entry is fixed 8 bytes (2*i32); guard the count against the remaining buffer so a
                    // corrupt huge count can't allocate wildly (mirrors TacticalDeployCodec's actor-table guard).
                    if ((long)count * 8 > ms.Length - ms.Position) return false;
                    var entries = new List<VisionEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int netId = r.ReadInt32();
                        int state = r.ReadInt32();
                        entries.Add(new VisionEntry(netId, state));
                    }
                    snapshot = new VisionSnapshot(seq, viewerFactionIndex, entries);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// HOST: monotonic per-surface seq source for live outcomes. CLIENT: last-writer-wins guard. PURE
    /// (no engine types). One instance per live mission on each side; reset on mission exit / re-deploy.
    ///
    /// Seq is assigned PER SURFACE (tac.move and tac.turn each get an independent monotonic stream) so a
    /// turn outcome never suppresses a move outcome and vice-versa. The host emits over a reliable, per-peer
    /// ORDERED transport, so a strictly-greater check is sufficient last-writer-wins (a stale duplicate or
    /// re-send is dropped; nothing newer can be overtaken).
    /// </summary>
    public sealed class TacticalLiveSeq
    {
        private readonly Dictionary<ushort, uint> _hostNext = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _clientLast = new Dictionary<ushort, uint>();

        /// <summary>HOST: take the next monotonic seq for a surface (starts at 1).</summary>
        public uint Next(ushort surfaceId)
        {
            _hostNext.TryGetValue(surfaceId, out var cur);
            uint next = cur + 1;
            _hostNext[surfaceId] = next;
            return next;
        }

        /// <summary>CLIENT: true if this seq is newer than the last applied for the surface. Does NOT mark
        /// (call <see cref="Mark"/> after a successful apply) so a failed apply can be retried by a re-send.</summary>
        public bool ShouldApply(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            return seq > last;
        }

        /// <summary>CLIENT: record the last applied seq for a surface.</summary>
        public void Mark(ushort surfaceId, uint seq)
        {
            _clientLast.TryGetValue(surfaceId, out var last);
            if (seq > last) _clientLast[surfaceId] = seq;
        }

        public void Reset()
        {
            _hostNext.Clear();
            _clientLast.Clear();
        }
    }

    /// <summary>
    /// HOST-side intent de-duplicator: the reliable transport can double-send a client intent envelope; a
    /// double-applied MOVE would step the actor twice. Keyed by the intent's (surfaceId, nonce); a bounded
    /// ring drops the oldest so memory stays flat over a long battle. PURE (no engine types).
    /// </summary>
    public sealed class TacticalIntentDedup
    {
        private readonly int _capacity;
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly Queue<ulong> _order = new Queue<ulong>();

        public TacticalIntentDedup(int capacity = 512) { _capacity = capacity < 16 ? 16 : capacity; }

        private static ulong Key(ushort surfaceId, uint nonce) => ((ulong)surfaceId << 32) | nonce;

        /// <summary>True the FIRST time a (surface,nonce) is offered; false on any repeat (drop it).</summary>
        public bool IsNew(ushort surfaceId, uint nonce)
        {
            ulong k = Key(surfaceId, nonce);
            if (_seen.Contains(k)) return false;
            _seen.Add(k);
            _order.Enqueue(k);
            if (_order.Count > _capacity) _seen.Remove(_order.Dequeue());
            return true;
        }

        public void Reset()
        {
            _seen.Clear();
            _order.Clear();
        }
    }
}
