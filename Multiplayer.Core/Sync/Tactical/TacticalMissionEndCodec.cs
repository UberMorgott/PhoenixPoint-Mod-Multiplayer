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

        /// <summary>The decoded conclusion payload.</summary>
        public sealed class MissionEndPayload
        {
            public uint Seq;
            public byte Phase;
            public int Outcome;
            public byte[] ResultBlob;
            public List<EvacRec> EvacZones;
            public List<ObjectiveRec> Objectives;

            public MissionEndPayload() { ResultBlob = new byte[0]; EvacZones = new List<EvacRec>(); Objectives = new List<ObjectiveRec>(); }

            public MissionEndPayload(uint seq, byte phase, int outcome, byte[] resultBlob,
                List<EvacRec> evacZones, List<ObjectiveRec> objectives)
            {
                Seq = seq;
                Phase = phase;
                Outcome = outcome;
                ResultBlob = resultBlob ?? new byte[0];
                EvacZones = evacZones ?? new List<EvacRec>();
                Objectives = objectives ?? new List<ObjectiveRec>();
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

                    payload = new MissionEndPayload(seq, phase, outcome, result, evac, obj);
                    return true;   // trailing bytes (a newer peer's extra fields) are intentionally ignored
                }
            }
            catch { return false; }
        }
    }
}
