using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE record of one Phoenix base/site's roster COMPOSITION — the ORDERED <c>GeoUnitId</c>s in its
    /// <c>_tacUnits</c> (PS1 of the 2026-07-05 personnel-sync spec §2.2). An EMPTY id list is honest
    /// ("this site holds no soldiers"), never a tombstone-skip: the client reconciles the container to
    /// the exact mirrored set + order.
    /// </summary>
    public sealed class PersonnelSiteRoster : IEquatable<PersonnelSiteRoster>
    {
        public readonly int SiteId;
        public readonly long[] UnitIds;   // ordered GeoUnitIds (GeoCharacter.Id GeoTacUnitId int, widened to i64 on the wire)

        public PersonnelSiteRoster(int siteId, long[] unitIds)
        {
            SiteId = siteId;
            UnitIds = unitIds ?? new long[0];
        }

        public bool Equals(PersonnelSiteRoster other)
        {
            if (other == null || SiteId != other.SiteId || UnitIds.Length != other.UnitIds.Length) return false;
            for (int i = 0; i < UnitIds.Length; i++)
                if (UnitIds[i] != other.UnitIds[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PersonnelSiteRoster o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = SiteId;
                foreach (var id in UnitIds) h = (h * 397) ^ id.GetHashCode();
                return h;
            }
        }

        public override string ToString() => $"SiteRoster({SiteId} units=[{string.Join(",", UnitIds)}])";
    }

    /// <summary>
    /// PURE record of one soldier's PS2 live-state tail: the whole <c>GeoCharacter</c> written by the
    /// game's configured Serializer (the <c>TailHasCharacterBlob</c> bit-0 payload). Blob bytes are
    /// opaque to the codec — serialize/deserialize is the game-bound <c>PersonnelBlob</c> glue.
    /// </summary>
    public sealed class PersonnelSoldierState
    {
        public readonly long UnitId;
        public readonly byte[] Blob;   // whole-GeoCharacter game-Serializer graph (never null on emit)

        public PersonnelSoldierState(long unitId, byte[] blob)
        {
            UnitId = unitId;
            Blob = blob;
        }
    }

    /// <summary>
    /// Decoded personnel snapshot for state channel #9 — pure data + wire codec, free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="GeoSiteSnapshot"/>). PS1 = the site-roster membership block; PS2 = the per-soldier
    /// live-state block (versioned optional tail, bit0 <see cref="TailHasCharacterBlob"/> = whole
    /// GeoCharacter blob). Bits 1-7 stay RESERVED: payloads append after known ones in ascending bit
    /// order, and the recLen skip keeps an older decoder tolerant (parse-known-then-skip).
    ///
    /// Wire payload (inside EncodeStateSync(channelId=9, ver, payload)) — BOTH blocks always present
    /// (fixed order), each independently empty:
    ///   [u16 siteCount] { [i32 siteId] [u16 nUnits] [i64 GeoUnitId × nUnits] }*     // PS1 membership, ordered
    ///   [u16 stateCount]{ [i64 GeoUnitId] [u16 recLen] [u8 tailFlags][payloads…] }* // PS2 live-state tail
    ///   bit0 payload = [u32 blobLen][blob bytes]
    ///   recLen = byte length of tailFlags + payloads, so an older decoder skips a record whose flags
    ///   carry unknown (future, higher) bits; a truncated KNOWN payload throws → whole payload rejected
    ///   (all-or-nothing, the GeoSiteSnapshot extras contract).
    /// </summary>
    public sealed class PersonnelSnapshot
    {
        /// <summary>tailFlags bit0: record carries a whole-GeoCharacter Serializer blob (PS2).</summary>
        public const byte TailHasCharacterBlob = 0x01;

        /// <summary>Faction-SP trailing-block flags bit0: block carries the shared
        /// <c>GeoPhoenixFaction.Skillpoints</c> pool value.</summary>
        public const byte FactionTailHasSkillpoints = 0x01;

        public readonly List<PersonnelSiteRoster> Sites = new List<PersonnelSiteRoster>();
        public readonly List<PersonnelSoldierState> States = new List<PersonnelSoldierState>();

        /// <summary>Optional PS2 faction-SP tail: the shared <c>GeoPhoenixFaction.Skillpoints</c> pool
        /// (value-only mirror). <c>null</c> = not carried this flush (or an older payload with no tail) —
        /// the client leaves the pool untouched. Rides the #9 flush whenever the host value changed
        /// (one writer, no new rail); see <c>PersonnelChannel</c>.</summary>
        public int? FactionSkillpoints;

        public static byte[] Encode(PersonnelSnapshot snap)
        {
            if (snap == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write((ushort)snap.Sites.Count);
                foreach (var s in snap.Sites)
                {
                    w.Write(s.SiteId);
                    int n = s.UnitIds.Length > ushort.MaxValue ? ushort.MaxValue : s.UnitIds.Length;
                    w.Write((ushort)n);
                    for (int i = 0; i < n; i++) w.Write(s.UnitIds[i]);
                }
                // PS2 live-state block: ALWAYS present (fixed order). A null/oversize blob can't be
                // framed (recLen is u16) — dropped defensively here; the flush core (MaxBlobWireBytes)
                // is the real gate, so this branch is unreachable in the live channel.
                var emittable = new List<PersonnelSoldierState>();
                if (snap.States != null)
                    foreach (var st in snap.States)
                        if (st?.Blob != null && 1 + 4 + st.Blob.Length <= ushort.MaxValue)
                            emittable.Add(st);
                w.Write((ushort)emittable.Count);
                foreach (var st in emittable)
                {
                    w.Write(st.UnitId);
                    w.Write((ushort)(1 + 4 + st.Blob.Length));   // recLen = flags + blobLen + blob
                    w.Write(TailHasCharacterBlob);
                    w.Write(st.Blob.Length);
                    w.Write(st.Blob);
                }
                // Faction-SP TRAILING block (optional, LAST): written only when carried, so a payload without
                // it is byte-identical to the pre-tail format. An older decoder ignores trailing bytes; a newer
                // decoder tolerates its absence (the position<length guard in Decode) — back-compat both ways.
                if (snap.FactionSkillpoints.HasValue)
                {
                    w.Write(FactionTailHasSkillpoints);
                    w.Write(snap.FactionSkillpoints.Value);
                }
                return ms.ToArray();
            }
        }

        public static PersonnelSnapshot Decode(byte[] data)
        {
            if (data == null) return null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    var snap = new PersonnelSnapshot();
                    int nSites = r.ReadUInt16();
                    for (int i = 0; i < nSites; i++)
                    {
                        int siteId = r.ReadInt32();
                        int n = r.ReadUInt16();
                        var ids = new long[n];
                        for (int j = 0; j < n; j++) ids[j] = r.ReadInt64();
                        snap.Sites.Add(new PersonnelSiteRoster(siteId, ids));
                    }
                    // PS2 live-state block: parse the known bit0 payload, then skip whatever a future
                    // higher bit appended (recLen-bounded). A truncated KNOWN payload throws → whole
                    // payload rejected (all-or-nothing); unknown-bits-only records skip silently.
                    int nStates = r.ReadUInt16();
                    for (int i = 0; i < nStates; i++)
                    {
                        long unitId = r.ReadInt64();
                        int recLen = r.ReadUInt16();
                        int consumed = 0;
                        if (recLen >= 1)
                        {
                            byte flags = r.ReadByte();
                            consumed = 1;
                            if ((flags & TailHasCharacterBlob) != 0)
                            {
                                if (recLen - consumed < 4)
                                    throw new EndOfStreamException("PersonnelSnapshot: state record too short for blobLen (recLen=" + recLen + ")");
                                int blobLen = r.ReadInt32();
                                consumed += 4;
                                if (blobLen < 0 || blobLen > recLen - consumed)
                                    throw new EndOfStreamException("PersonnelSnapshot: blobLen " + blobLen + " exceeds record (recLen=" + recLen + ")");
                                var blob = r.ReadBytes(blobLen);
                                if (blob.Length != blobLen)
                                    throw new EndOfStreamException("PersonnelSnapshot: truncated blob (wanted " + blobLen + ", got " + blob.Length + ")");
                                consumed += blobLen;
                                snap.States.Add(new PersonnelSoldierState(unitId, blob));
                            }
                        }
                        int skip = recLen - consumed;
                        if (skip > 0)
                        {
                            var rest = r.ReadBytes(skip);
                            if (rest.Length != skip)
                                throw new EndOfStreamException("PersonnelSnapshot: truncated state record (wanted " + skip + " more, got " + rest.Length + ")");
                        }
                    }
                    // Optional faction-SP trailing block: present only in a newer payload that carried it.
                    // Absent (older payload / not carried) → FactionSkillpoints stays null. We read only the
                    // known bit0; any future higher bit / trailing bytes are tolerated (never a throw here).
                    if (ms.Position < ms.Length)
                    {
                        byte facFlags = r.ReadByte();
                        if ((facFlags & FactionTailHasSkillpoints) != 0)
                            snap.FactionSkillpoints = r.ReadInt32();
                    }
                    return snap;
                }
            }
            // Pure/Unity-free (unit-testable): swallow malformed payloads and return null. Callers
            // (PersonnelChannel.Apply) treat null as "no-op". No UnityEngine.Debug dependency here.
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// PURE host-side PS2 flush core: which dirty soldiers actually SHIP this flush. Per distinct id it
    /// serializes (via the injected delegate), hash-compares against the last-EMITTED blob (FNV-1a 64 —
    /// the GeoVehiclePos.Signature/MistSnapshot.ContentHash change-detect pattern) and emits only changed
    /// soldiers, bounded by a per-flush byte budget (EncodeStateSync's u16 len is a hard 65535 wire cap;
    /// the channel drains overflow next tick via AttachHost re-mark — the MistChannel pump pattern).
    /// Outcomes per id: EMIT (changed, fits) / SkippedUnchanged (hash equal) / Deferred (budget hit —
    /// stays dirty, NOT hash-stamped, re-blobbed next flush so latest state wins) / Failed (serialize
    /// returned null — vanished or serializer unavailable; NOT deferred so a dead id can't spin the
    /// drain loop) / Oversized (blob can't fit the u16 record frame — dropped, caller logs).
    /// Unity-free → directly unit-testable with fake blobbers.
    /// </summary>
    public static class PersonnelStateFlush
    {
        /// <summary>Hard per-blob wire cap: keeps recLen (u16, 65535) + the 15-byte record frame +
        /// the sibling membership block safely under EncodeStateSync's u16 payload cap.</summary>
        public const int MaxBlobWireBytes = 60000;

        /// <summary>Record frame overhead: i64 id + u16 recLen + u8 flags + u32 blobLen.</summary>
        public const int RecordOverheadBytes = 15;

        public sealed class Result
        {
            public readonly List<PersonnelSoldierState> Emit = new List<PersonnelSoldierState>();
            public readonly List<long> Deferred = new List<long>();   // budget overflow — stays dirty
            public int SkippedUnchanged;
            public int Failed;
            public int Oversized;
        }

        public static ulong Hash(byte[] blob)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                if (blob != null) foreach (byte b in blob) { h ^= b; h *= 1099511628211UL; }
                return h;
            }
        }

        public static Result Run(IEnumerable<long> dirtyIds, Func<long, byte[]> serialize,
                                 IDictionary<long, ulong> lastSent, int byteBudget)
        {
            var res = new Result();
            if (dirtyIds == null) return res;
            var seen = new HashSet<long>();
            int used = 0;
            bool budgetClosed = false;   // once the budget rejects one record, defer the rest UNSERIALIZED
            foreach (var id in dirtyIds)
            {
                if (id == 0 || !seen.Add(id)) continue;   // 0 = None sentinel; dup ids coalesce
                if (budgetClosed) { res.Deferred.Add(id); continue; }
                var blob = serialize != null ? serialize(id) : null;
                if (blob == null || blob.Length == 0) { res.Failed++; continue; }
                if (blob.Length > MaxBlobWireBytes) { res.Oversized++; continue; }
                ulong h = Hash(blob);
                if (lastSent != null && lastSent.TryGetValue(id, out var prev) && prev == h)
                {
                    res.SkippedUnchanged++;
                    continue;
                }
                int recBytes = RecordOverheadBytes + blob.Length;
                // Emit-at-least-one: the first changed record always ships (its blob is ≤ the hard cap),
                // else a single blob larger than the budget would starve forever.
                if (used + recBytes > byteBudget && res.Emit.Count > 0)
                {
                    res.Deferred.Add(id);
                    budgetClosed = true;
                    continue;
                }
                res.Emit.Add(new PersonnelSoldierState(id, blob));
                if (lastSent != null) lastSent[id] = h;
                used += recBytes;
            }
            return res;
        }
    }

    /// <summary>
    /// PURE value-only membership reconcile core (PS1 §4): edit a container's roster list DIRECTLY to
    /// match a mirrored ordered id set — add-if-missing / remove-if-absent / reorder — NEVER through the
    /// native <c>AddCharacter</c>/<c>RemoveCharacter</c> (they cascade sim onto the frozen client mirror).
    /// A transferred soldier is removed from its CURRENT container BEFORE landing in the target, so the
    /// value-only reconcile can never leave one soldier in two containers (spec PS1 risk). An id that
    /// resolves to nothing on this client is reported in <see cref="Outcome.Unresolved"/> and skipped
    /// (degrade-to-notify — the caller logs; never a throw, never a partial-payload reject).
    /// Operates on non-generic <see cref="IList"/> + resolver delegates so it is directly unit-testable
    /// on fake containers (mirrors <see cref="MissionMirrorDecision"/>'s pure-branch pattern).
    /// </summary>
    public static class RosterReconcile
    {
        public sealed class Outcome
        {
            public bool Changed;                 // target list was mutated
            public int Added;                    // members not previously in target
            public int Removed;                  // previous members absent from the mirrored set
            public bool Reordered;               // same membership, different order
            public readonly List<long> Unresolved = new List<long>();   // mirrored ids with no live instance
            // The instances behind Removed: after this apply they sit in NO container (remove-from-old
            // moved same-apply transfers already) — the caller parks them in PersonnelOrphanPool so a
            // cross-CHANNEL transfer (#9 site remove now, #6 crew add ticks later) can still resolve them.
            public readonly List<object> RemovedInstances = new List<object>();
        }

        /// <param name="target">The container's live roster list (mutated in place, value-only).</param>
        /// <param name="orderedIds">The mirrored ordered GeoUnitIds (host truth for this container).</param>
        /// <param name="resolveById">GeoUnitId → live character instance, or null (unresolvable → skip).</param>
        /// <param name="currentContainerOf">Character → the roster list currently holding it, or null.
        /// Consulted for remove-from-old-before-add; a stale answer is safe (Contains-guarded).</param>
        public static Outcome Apply(IList target, IList<long> orderedIds,
                                    Func<long, object> resolveById, Func<object, IList> currentContainerOf)
        {
            var outcome = new Outcome();
            if (target == null || orderedIds == null) return outcome;

            var desired = new List<object>(orderedIds.Count);
            foreach (var id in orderedIds)
            {
                var ch = resolveById != null ? resolveById(id) : null;
                if (ch == null) { outcome.Unresolved.Add(id); continue; }
                desired.Add(ch);
            }

            // Remove-from-old BEFORE add-to-new: a transfer must never leave the soldier in two containers.
            foreach (var ch in desired)
            {
                var cur = currentContainerOf != null ? currentContainerOf(ch) : null;
                if (cur == null || ReferenceEquals(cur, target)) continue;
                if (cur.Contains(ch)) cur.Remove(ch);
            }

            // Exact match (same instances, same order) → idempotent no-op.
            bool same = target.Count == desired.Count;
            if (same)
                for (int i = 0; i < desired.Count; i++)
                    if (!ReferenceEquals(target[i], desired[i])) { same = false; break; }
            if (same) return outcome;

            var before = new List<object>(target.Count);
            foreach (var o in target) before.Add(o);
            foreach (var o in desired) if (!before.Contains(o)) outcome.Added++;
            foreach (var o in before) if (!desired.Contains(o)) { outcome.Removed++; outcome.RemovedInstances.Add(o); }
            outcome.Reordered = outcome.Added == 0 && outcome.Removed == 0;

            target.Clear();
            foreach (var o in desired) target.Add(o);
            outcome.Changed = true;
            return outcome;
        }
    }
}
