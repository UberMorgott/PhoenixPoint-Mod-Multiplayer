using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Decoded faction OBJECTIVES + event-system VARIABLES snapshot for state channel #7 (spec §P7:
    /// TFTV quest lines / DLC5 / critical path all ride <c>GeoFaction.Objectives</c> +
    /// <c>GeoscapeEventSystem._customVariables</c>, and neither is mirrored today). Host snapshots the
    /// PLAYER faction's objective list as small per-class value records plus the full custom-variable
    /// table; the client reconciles its own list to match (rebuild via the same vanilla classes) and
    /// overwrites the variable table (pure value mirror — no <c>VariableSet</c> cascade).
    ///
    /// This is the pure data + wire codec — free of any IStateChannel/SyncEngine/Unity dependency so it
    /// is directly unit-testable (mirrors <see cref="DiplomacySnapshot"/>);
    /// <see cref="ObjectivesReflection"/> is the game bridge.
    ///
    /// CARRIED CLASSES (exact type match, discriminators below): the four vanilla objective classes
    /// whose state is PURE (def guids / strings / bools) — the full set TFTV quest code instantiates
    /// (verified TFTV-src: EventGeoFactionObjective, DiplomaticGeoFactionObjective,
    /// MissionGeoFactionObjective; + vanilla ResearchGeoFactionObjective for the research critical
    /// path). NOT carried (left untouched on the client — they converge via save-transfer):
    /// StoryMissionGeoFactionObjective (filtered OUT of the objectives panel anyway,
    /// UIModuleGeoObjectives.cs:82-83, and rebuilding it would mutate site tags), tutorial/AI-faction
    /// classes, and any unknown/mod subclass. TFTV-ABSENT TOLERANCE lives in APPLY, not decode: a
    /// record whose EventID/def guid/research id doesn't resolve client-side is SKIPPED per record,
    /// never a throw (a TFTV-injected event def can't resolve on a TFTV-less client).
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 objCount]{[u8 disc][u8 flags]
    ///                  [u16 len][givenByFactionDefGuid utf8]
    ///                  [u16 len][titleKey utf8][u16 len][descKey utf8]
    ///                  [u16 len][payload utf8][i32 aux]}*
    ///   [u16 varCount]{[u16 len][name utf8][i32 value]}*
    /// disc-specific payload/aux: Event → EventID/0; Diplomatic → forFactionDef guid/0;
    /// Research → ResearchID/0; Mission → ""/siteId.
    /// An UNKNOWN disc value decodes fine and is skipped at apply (forward-tolerant); the variable
    /// table is always present (new channel — no legacy tail contract to preserve).
    /// </summary>
    public sealed class ObjectivesSnapshot
    {
        // ─── class discriminators (exact vanilla type per disc — never subclasses) ───
        public const byte DiscEvent = 1;        // EventGeoFactionObjective {EventID, _completed}
        public const byte DiscDiplomatic = 2;   // DiplomaticGeoFactionObjective {ForFaction def}
        public const byte DiscResearch = 3;     // ResearchGeoFactionObjective {Research element}
        public const byte DiscMission = 4;      // MissionGeoFactionObjective {Mission → Site.SiteId}

        // ─── flags bits ───
        public const byte FlagCritical = 1 << 0;      // GeoFactionObjective.IsCriticalPath
        public const byte FlagCompleted = 1 << 1;     // disc 1 only: EventGeoFactionObjective._completed
        public const byte FlagTitleNoLoc = 1 << 2;    // Title LocalizedTextBind._doNotLocalize
        public const byte FlagDescNoLoc = 1 << 3;     // Description LocalizedTextBind._doNotLocalize
        public const byte FlagTitlePresent = 1 << 4;  // Title != null (distinguishes null from empty key)
        public const byte FlagDescPresent = 1 << 5;   // Description != null

        public sealed class ObjectiveRecord
        {
            public byte Disc;
            public byte Flags;
            public string GivenByGuid = "";
            public string TitleKey = "";
            public string DescKey = "";
            public string Payload = "";
            public int Aux;

            public bool Critical => (Flags & FlagCritical) != 0;
            public bool Completed => (Flags & FlagCompleted) != 0;

            /// <summary>Reconcile identity: everything that makes two records "the same objective"
            /// across snapshots. Mutable display bits (critical/completed flags) stay OUT so a flag
            /// flip stamps in place instead of remove+re-add churn; TitleKey stays IN because TFTV
            /// quest steps are distinct DiplomaticGeoFactionObjective instances differing only by
            /// title (void omens / quest-line steps).</summary>
            public string MatchKey()
                => Disc + "|" + GivenByGuid + "|" + Payload + "|" + Aux + "|" + TitleKey;
        }

        public readonly List<ObjectiveRecord> Objectives = new List<ObjectiveRecord>();
        public readonly List<(string name, int value)> Variables = new List<(string, int)>();

        /// <summary>PURE apply decision: is this a discriminator the client knows how to rebuild?
        /// Unknown (future/foreign) values are skipped at apply, never rejected at decode.</summary>
        public static bool IsCarriedDisc(byte disc) => disc >= DiscEvent && disc <= DiscMission;

        public static byte[] Encode(ObjectivesSnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Objectives.Count);
                foreach (var o in snap.Objectives)
                {
                    w.Write(o.Disc);
                    w.Write(o.Flags);
                    WriteStr(w, o.GivenByGuid);
                    WriteStr(w, o.TitleKey);
                    WriteStr(w, o.DescKey);
                    WriteStr(w, o.Payload);
                    w.Write(o.Aux);
                }
                w.Write((ushort)snap.Variables.Count);
                foreach (var (name, value) in snap.Variables)
                {
                    WriteStr(w, name);
                    w.Write(value);
                }
                return ms.ToArray();
            }
        }

        public static ObjectivesSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new ObjectivesSnapshot();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        var o = new ObjectiveRecord
                        {
                            Disc = r.ReadByte(),
                            Flags = r.ReadByte(),
                            GivenByGuid = ReadStr(r),
                            TitleKey = ReadStr(r),
                            DescKey = ReadStr(r),
                            Payload = ReadStr(r),
                            Aux = r.ReadInt32(),
                        };
                        snap.Objectives.Add(o);
                    }
                    int nv = r.ReadUInt16();
                    for (int i = 0; i < nv; i++)
                    {
                        string name = ReadStr(r);
                        int value = r.ReadInt32();
                        snap.Variables.Add((name, value));
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (ObjectivesChannel.Apply) treat null as "no-op".
            catch (Exception) { return null; }
        }

        private static void WriteStr(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        private static string ReadStr(BinaryReader r)
        {
            int len = r.ReadUInt16();
            // BinaryReader.ReadBytes silently returns FEWER bytes at end-of-stream (no throw); verify the
            // full length was read, else throw → caught by Decode → null (rejected, not garbage).
            var bytes = r.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("ObjectivesSnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
