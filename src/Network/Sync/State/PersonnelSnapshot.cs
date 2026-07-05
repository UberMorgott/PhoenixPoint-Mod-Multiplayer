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
    /// Decoded personnel snapshot for state channel #9 — pure data + wire codec, free of any
    /// <c>IStateChannel</c>/<c>SyncEngine</c>/Unity dependency so it is directly unit-testable (mirrors
    /// <see cref="GeoSiteSnapshot"/>). PS1 carries only the site-roster membership block; the PS2
    /// live-state block is a versioned optional record array this decoder already SKIPS record-by-record
    /// (recLen-prefixed, parse-known-then-skip — no known tail bits exist yet), so PS2 can add per-soldier
    /// payload bits without a wire break.
    ///
    /// Wire payload (inside EncodeStateSync(channelId=9, ver, payload)) — BOTH blocks always present
    /// (fixed order), each independently empty:
    ///   [u16 siteCount] { [i32 siteId] [u16 nUnits] [i64 GeoUnitId × nUnits] }*     // PS1 membership, ordered
    ///   [u16 stateCount]{ [i64 GeoUnitId] [u16 recLen] [u8 tailFlags][payloads…] }* // PS2 live-state tail
    ///   recLen = byte length of tailFlags + payloads, so an older decoder skips a record whose flags
    ///   carry unknown (future, higher) bits; a truncated record throws → whole payload rejected
    ///   (all-or-nothing, the GeoSiteSnapshot extras contract).
    /// </summary>
    public sealed class PersonnelSnapshot
    {
        public readonly List<PersonnelSiteRoster> Sites = new List<PersonnelSiteRoster>();

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
                // PS2 live-state block: ALWAYS present (fixed order); PS1 emits none.
                w.Write((ushort)0);
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
                    // PS2 live-state block: PS1 knows no tail bits → skip each recLen-prefixed record
                    // (parse-known-then-skip). A truncated record throws → whole payload rejected.
                    int nStates = r.ReadUInt16();
                    for (int i = 0; i < nStates; i++)
                    {
                        r.ReadInt64();                      // GeoUnitId (unused until PS2)
                        int recLen = r.ReadUInt16();
                        var rec = r.ReadBytes(recLen);
                        if (rec.Length != recLen)
                            throw new EndOfStreamException("PersonnelSnapshot: truncated state record (wanted " + recLen + ", got " + rec.Length + ")");
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
            foreach (var o in before) if (!desired.Contains(o)) outcome.Removed++;
            outcome.Reordered = outcome.Added == 0 && outcome.Removed == 0;

            target.Clear();
            foreach (var o in desired) target.Add(o);
            outcome.Changed = true;
            return outcome;
        }
    }
}
