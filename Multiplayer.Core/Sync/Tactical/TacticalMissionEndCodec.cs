using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) wire codec for the MISSION-CONCLUSION mirror surface <c>tac.missionend</c>
    /// (0x95, host→all, RELIABLE — spec TS4). Closes the "the battle can't END in sync" blind spot: the host
    /// broadcasts the game-over / result + evac-zone + in-battle objective state at the native
    /// <c>TacticalLevelController.GameOver()</c> chokepoint (fires GameWrappingUpEvent then GameOverEvent), so the
    /// client leaves the battle when the host does — riding the NATIVE end-of-mission flow (it just flips the
    /// native <c>IsGameOver</c> flag the tactical View state machine already watches; no custom teardown).
    ///
    /// OUTCOME DISPLAY OWNERSHIP (no double-outcome): the post-mission GEOSCAPE result modal is ALREADY mirrored
    /// to the client by the geoscape popup-mirror rail (MissionOutcome 0x69, deferred + non-occupying in the
    /// display sequencer per Batch-3). TS4 owns ONLY the TACTICAL-side close + the in-battle objective/evac state
    /// repaint — it NEVER shows an outcome modal, so it cannot double the 0x69 display (see
    /// <see cref="TacticalMissionEndGate.ShouldDisplayOutcome"/>). The <c>TacMissionResult</c> blob is carried for
    /// parity / an authoritative record; the DISPLAY stays owned by 0x69.
    ///
    /// WIRE (host→all, carries LiveSeq):
    ///   [seq:u32][phase:u8 wrappingup=0/gameover=1][outcome:i32]
    ///   [resultLen:i32][TacMissionResult blob]              — the native GetMissionResult() graph (empty on wrappingup)
    ///   [evacCount:u16]{ [zoneId:i32][unlocked:u8] }*        — evac-zone unlock state
    ///   [objCount:u16]{ [idLen:u16][id:utf8][state:u8] }*    — per-objective FactionObjectiveState (id = ordinal)
    ///   [statsCount:u16]{ [netId:i32][xpEarned:i32][newSp:i32] }*   — OPTIONAL tail: compact per-soldier
    ///   [factionSpDelta:i32]                                        —   mission XP/SP for the client summary
    ///   [objXpCount:u16]{ [ordinal:u16][xp:i32] }*                  — OPTIONAL: per-objective ACTUAL XP as the
    ///     host renders it (BaseExperienceReward × GetCompletion × mult — completion counters live host-only,
    ///     so the client mirrors the final number, keyed by the same ordinal as the objective list)
    ///     (all tail segments are tolerant — a legacy decoder ignores them, a new decoder on a legacy payload
    ///     reads empty stats / 0 delta / empty objXp; NOT the ~93KB result blob — just the summary numbers)
    ///
    /// <c>outcome</c> = the player faction's terminal <c>TacFactionState</c> as an int (<see cref="OutcomeUnknown"/>
    /// when unresolved) — a diagnostic / HUD hint; the authoritative outcome rides the 0x69 modal + the result blob.
    /// The <c>TacMissionResult</c> blob is produced/consumed ONLY via the game Serializer
    /// (<c>TacticalDeploySync.SerializeGraph</c>), never a hand-mapped DTO (spec R2).
    ///
    /// BACKWARD-TOLERANT: fixed header + a length-prefixed blob + two self-describing length-prefixed lists; a
    /// decoder reads exactly the fields it knows and IGNORES any trailing bytes (forward-compat). Truncation / a
    /// corrupt count → clean <c>false</c> (no partial accept), exactly like <see cref="TacticalDeployCodec"/> /
    /// <see cref="TacticalSurfaceCodec"/> — the reliable transport guarantees full delivery.
    /// </summary>
    public static class TacticalMissionEndCodec
    {
        /// <summary>Conclusion phase (u8). <see cref="PhaseWrappingUp"/> is a lightweight pre-close notify;
        /// <see cref="PhaseGameOver"/> is the terminal beat on which the client closes the tactical scene.</summary>
        public const byte PhaseWrappingUp = 0;   // GameWrappingUpEvent moment — pre-close notify (no heavy blob)
        public const byte PhaseGameOver = 1;     // GameOverEvent moment — client ends the mission (rides native flow)

        /// <summary>Sentinel for an unresolvable player-faction outcome (diagnostic only).</summary>
        public const int OutcomeUnknown = -1;

        /// <summary>One evac-zone unlock record (zone id + unlocked flag).</summary>
        public sealed class EvacRec
        {
            public int ZoneId;
            public bool Unlocked;
            public EvacRec() { }
            public EvacRec(int zoneId, bool unlocked) { ZoneId = zoneId; Unlocked = unlocked; }
        }

        /// <summary>One objective-state record: a stable ordinal id (index within the shared mission's objective
        /// list) + the <c>FactionObjectiveState</c> as a byte (InProgress/Achieved/Failed).</summary>
        public sealed class ObjectiveRec
        {
            public string ObjectiveId;
            public byte State;
            public ObjectiveRec() { }
            public ObjectiveRec(string objectiveId, byte state) { ObjectiveId = objectiveId ?? ""; State = state; }
        }

        /// <summary>One per-soldier mission-stats record (what the BattleSummary numbers need): the XP earned
        /// this mission (<c>LevelProgression.ExperienceEarned</c>) + the personal SP earned
        /// (<c>CharacterProgression.NewSkillPoints</c>), keyed by the mirror netId.</summary>
        public sealed class StatsRec
        {
            public int NetId;
            public int XpEarned;
            public int NewSp;
            public StatsRec() { }
            public StatsRec(int netId, int xpEarned, int newSp) { NetId = netId; XpEarned = xpEarned; NewSp = newSp; }
        }

        /// <summary>One per-objective ACTUAL-XP record: the number the host's summary renders for an Achieved
        /// objective (completion counters never mirror, so the client shows the host's final value), keyed by
        /// the same ordinal the objective list uses.</summary>
        public sealed class ObjXpRec
        {
            public ushort Ordinal;
            public int Xp;
            public ObjXpRec() { }
            public ObjXpRec(ushort ordinal, int xp) { Ordinal = ordinal; Xp = xp; }
        }

        /// <summary>The decoded conclusion payload.</summary>
        public sealed class MissionEndPayload
        {
            public uint Seq;
            public byte Phase;
            public int Outcome;
            public byte[] ResultBlob;
            public List<EvacRec> EvacZones;
            public List<ObjectiveRec> Objectives;
            public List<StatsRec> Stats;
            public int FactionSpDelta;
            public List<ObjXpRec> ObjectiveXp;

            public MissionEndPayload() { ResultBlob = new byte[0]; EvacZones = new List<EvacRec>(); Objectives = new List<ObjectiveRec>(); Stats = new List<StatsRec>(); ObjectiveXp = new List<ObjXpRec>(); }

            public MissionEndPayload(uint seq, byte phase, int outcome, byte[] resultBlob,
                List<EvacRec> evacZones, List<ObjectiveRec> objectives,
                List<StatsRec> stats = null, int factionSpDelta = 0, List<ObjXpRec> objectiveXp = null)
            {
                Seq = seq;
                Phase = phase;
                Outcome = outcome;
                ResultBlob = resultBlob ?? new byte[0];
                EvacZones = evacZones ?? new List<EvacRec>();
                Objectives = objectives ?? new List<ObjectiveRec>();
                Stats = stats ?? new List<StatsRec>();
                FactionSpDelta = factionSpDelta;
                ObjectiveXp = objectiveXp ?? new List<ObjXpRec>();
            }
        }

        // ─── Encode / Decode ─────────────────────────────────────────────────

        public static byte[] Encode(MissionEndPayload p)
        {
            var result = (p != null ? p.ResultBlob : null) ?? new byte[0];
            var evac = (p != null ? p.EvacZones : null) ?? new List<EvacRec>();
            var obj = (p != null ? p.Objectives : null) ?? new List<ObjectiveRec>();

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(p != null ? p.Seq : 0u);
                w.Write(p != null ? p.Phase : PhaseGameOver);
                w.Write(p != null ? p.Outcome : OutcomeUnknown);

                w.Write(result.Length);
                if (result.Length > 0) w.Write(result);

                w.Write((ushort)evac.Count);
                foreach (var z in evac)
                {
                    w.Write(z.ZoneId);
                    w.Write((byte)(z.Unlocked ? 1 : 0));
                }

                w.Write((ushort)obj.Count);
                foreach (var o in obj)
                {
                    var idBytes = Encoding.UTF8.GetBytes(o.ObjectiveId ?? "");
                    w.Write((ushort)idBytes.Length);
                    if (idBytes.Length > 0) w.Write(idBytes);
                    w.Write(o.State);
                }

                // Tolerant tail: per-soldier mission stats + the faction SP delta (see class summary).
                var stats = (p != null ? p.Stats : null) ?? new List<StatsRec>();
                w.Write((ushort)stats.Count);
                foreach (var s in stats)
                {
                    w.Write(s.NetId);
                    w.Write(s.XpEarned);
                    w.Write(s.NewSp);
                }
                w.Write(p != null ? p.FactionSpDelta : 0);

                var objXp = (p != null ? p.ObjectiveXp : null) ?? new List<ObjXpRec>();
                w.Write((ushort)objXp.Count);
                foreach (var o in objXp)
                {
                    w.Write(o.Ordinal);
                    w.Write(o.Xp);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decode a 0x95 conclusion payload. Returns false (no partial accept) on any truncation or a
        /// count/length exceeding the remaining buffer (guards a corrupt huge count from a wild allocation).
        /// Trailing bytes after the known fields are ignored (forward-compat).</summary>
        public static bool TryDecode(byte[] data, out MissionEndPayload payload)
        {
            payload = null;
            // Minimum: u32 seq + u8 phase + i32 outcome + i32 resultLen + u16 evacCount + u16 objCount.
            if (data == null || data.Length < 4 + 1 + 4 + 4 + 2 + 2) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte phase = r.ReadByte();
                    int outcome = r.ReadInt32();

                    int resultLen = r.ReadInt32();
                    if (resultLen < 0 || ms.Length - ms.Position < resultLen) return false;
                    var result = resultLen > 0 ? r.ReadBytes(resultLen) : new byte[0];
                    if (result.Length != resultLen) return false;

                    if (ms.Length - ms.Position < 2) return false;
                    int evacCount = r.ReadUInt16();
                    // Each evac record is fixed 5 bytes (i32 + u8); guard the count against the remaining buffer.
                    if ((long)evacCount * 5 > ms.Length - ms.Position) return false;
                    var evac = new List<EvacRec>(evacCount);
                    for (int i = 0; i < evacCount; i++)
                    {
                        int zoneId = r.ReadInt32();
                        byte unlocked = r.ReadByte();
                        evac.Add(new EvacRec(zoneId, unlocked != 0));
                    }

                    if (ms.Length - ms.Position < 2) return false;
                    int objCount = r.ReadUInt16();
                    var obj = new List<ObjectiveRec>(objCount);
                    for (int i = 0; i < objCount; i++)
                    {
                        if (ms.Length - ms.Position < 2) return false;   // idLen
                        int idLen = r.ReadUInt16();
                        if (idLen < 0 || ms.Length - ms.Position < (long)idLen + 1) return false;   // id + state
                        string id = idLen > 0 ? Encoding.UTF8.GetString(r.ReadBytes(idLen)) : "";
                        byte state = r.ReadByte();
                        obj.Add(new ObjectiveRec(id, state));
                    }

                    // OPTIONAL stats tail (absent on a legacy payload → empty stats / 0 delta; a corrupt
                    // count → drop just the tail, never the whole conclusion).
                    var stats = new List<StatsRec>();
                    int spDelta = 0;
                    var objXp = new List<ObjXpRec>();
                    if (ms.Length - ms.Position >= 2)
                    {
                        int statsCount = r.ReadUInt16();
                        if ((long)statsCount * 12 <= ms.Length - ms.Position)
                        {
                            for (int i = 0; i < statsCount; i++)
                                stats.Add(new StatsRec(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                            if (ms.Length - ms.Position >= 4) spDelta = r.ReadInt32();
                            // OPTIONAL per-objective actual-XP segment (same tolerance rules).
                            if (ms.Length - ms.Position >= 2)
                            {
                                int objXpCount = r.ReadUInt16();
                                if ((long)objXpCount * 6 <= ms.Length - ms.Position)
                                    for (int i = 0; i < objXpCount; i++)
                                        objXp.Add(new ObjXpRec(r.ReadUInt16(), r.ReadInt32()));
                            }
                        }
                        else stats.Clear();
                    }

                    payload = new MissionEndPayload(seq, phase, outcome, result, evac, obj, stats, spDelta, objXp);
                    return true;   // trailing bytes (a newer peer's extra fields) are intentionally ignored
                }
            }
            catch { return false; }
        }
    }
}
