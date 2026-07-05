using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Decoded faction DIPLOMACY snapshot: the per-relation reputation integer for every
    /// faction-to-faction relation, keyed by <c>(ownerFactionDef guid, withPartyDef guid)</c>. The host
    /// applies <c>ResearchElement.Complete</c> → <c>RewardReputation()</c> →
    /// <c>PartyDiplomacy.ModifyDiplomacy</c> (it bumps the reputation of OTHER factions toward the player
    /// faction), which is never mirrored today. This is a VALUE-ONLY mirror, like the wallet echo: the
    /// client overwrites each relation's stored int to the host value (no event cascade).
    ///
    /// This is the pure data + wire codec for the diplomacy state channel (#4) — free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="ResearchSnapshot"/>).
    ///
    /// Wire payload (inside StateSync):
    ///   [u16 count]{[u16 ownerLen][ownerGuid utf8][u16 withLen][withGuid utf8][i32 value]}*
    ///
    /// WA-3 FORCED-STATE TAIL (versioned optional extension — audit gap 4e): appended AFTER the record
    /// array, and ONLY when ≥1 relation carries a valid forced state — a no-state payload stays
    /// byte-identical to the pre-WA-3 wire. An older decoder never reads past the record array (trailing
    /// bytes ignored); a newer decoder reads the tail only when bytes remain (older payload →
    /// <see cref="ForcedStates"/> empty = nothing carried). Layout:
    ///   [u16 stateCount == count][u8 forcedState]*      (index-aligned with the record array)
    /// forcedState = the raw <c>PartyDiplomacyState</c> value (0 Conflict … 7 Allied) of the relation's
    /// FORCED diplomacy cap — host-read as <c>PartyDiplomacy.GetDiplomacyState(relation.MaxValue)</c>, the
    /// same read native <c>SetMaxDiplomacyState</c> uses for its previousState (FactionDiplomacy.cs:57).
    /// <see cref="StateNotCarried"/> (255) = unreadable/not carried → the client skips that relation.
    ///
    /// Only relations whose <c>WithParty</c> key is a Def (a <c>PPFactionDef</c>, which has a stable Guid)
    /// are carried; non-Def keys (e.g. haven-leader keys) are skipped host-side — faction-vs-faction
    /// reputation is the set the research reward path mutates.
    /// </summary>
    public sealed class DiplomacySnapshot
    {
        /// <summary>Sentinel: no forced state carried for this relation (host read miss / legacy payload).</summary>
        public const byte StateNotCarried = 255;

        /// <summary>Highest valid raw <c>PartyDiplomacyState</c> value (Allied = 7; enum spans Conflict 0 … Allied 7,
        /// None = -1 is never carried).</summary>
        public const byte MaxValidState = 7;

        public readonly List<(string owner, string with, int value)> Relations = new List<(string, string, int)>();

        /// <summary>WA-3 forced-state bytes, index-aligned with <see cref="Relations"/>. Empty = not carried
        /// (legacy payload / pre-WA-3 peer). A misaligned list is treated as absent by <see cref="Encode"/>.</summary>
        public readonly List<byte> ForcedStates = new List<byte>();

        /// <summary>PURE apply decision: the client stamps a relation's forced diplomacy cap iff the wire byte
        /// is a valid raw <c>PartyDiplomacyState</c> (0..7). 255 (not carried) and any unknown future value
        /// are skipped — never a guess, never a clear.</summary>
        public static bool ShouldApplyForcedState(byte wireState) => wireState <= MaxValidState;

        public static byte[] Encode(DiplomacySnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Relations.Count);
                foreach (var (owner, with, value) in snap.Relations)
                {
                    WriteStr(w, owner);
                    WriteStr(w, with);
                    w.Write(value);
                }
                // WA-3 forced-state tail — written ONLY when count-aligned AND ≥1 state is carried, so a
                // no-state payload stays byte-identical to the pre-WA-3 wire (existing pins hold).
                if (snap.ForcedStates.Count == snap.Relations.Count && snap.Relations.Count > 0)
                {
                    bool any = false;
                    foreach (var s in snap.ForcedStates) if (s != StateNotCarried) { any = true; break; }
                    if (any)
                    {
                        w.Write((ushort)snap.ForcedStates.Count);
                        foreach (var s in snap.ForcedStates) w.Write(s);
                    }
                }
                return ms.ToArray();
            }
        }

        public static DiplomacySnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new DiplomacySnapshot();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        string owner = ReadStr(r);
                        string with = ReadStr(r);
                        int value = r.ReadInt32();
                        snap.Relations.Add((owner, with, value));
                    }
                    // WA-3 forced-state tail — read ONLY if bytes remain (an older payload without it decodes
                    // with ForcedStates empty). A count mismatch or truncation throws → whole payload rejected
                    // (null), preserving the all-or-nothing contract (index alignment is the wire invariant).
                    if (ms.Position < ms.Length)
                    {
                        int ns = r.ReadUInt16();
                        if (ns != n)
                            throw new InvalidDataException("DiplomacySnapshot: forced-state tail count " + ns
                                                           + " != relation count " + n);
                        // BinaryReader.ReadByte throws at end-of-stream → truncated tail rejects the payload.
                        for (int i = 0; i < ns; i++) snap.ForcedStates.Add(r.ReadByte());
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (DiplomacyChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
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
                throw new EndOfStreamException("DiplomacySnapshot: truncated string (wanted " + len + ", got " + bytes.Length + ")");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
