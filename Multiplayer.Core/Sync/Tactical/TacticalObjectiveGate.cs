using System;
using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) decisions for the LIVE mission-objective mirror (surface <c>tac.objective</c> 0x99).
    /// Isolated from the reflection glue in <see cref="TacticalObjectiveSync"/> so the seams are unit-testable:
    ///   • <see cref="BuildRecords"/> — HOST diff: which records go on the wire (seed-all, per-index state/progress
    ///     change, tail appends → ADD records; a list SHRINK reseeds full state, degrade-not-desync).
    ///   • <see cref="ResolveStateApplies"/> — CLIENT: map STATE records to in-range, class-matching client
    ///     indices; anything else lands in <paramref name="skipped"/> (unknown-subclass / index-drift degrade).
    ///   • <see cref="ResolveAddMatch"/> — CLIENT: resolve an ADD record to a local not-yet-added candidate from
    ///     the shared NextOnSuccess/NextOnFail def graph by class name (+ description key when both carry one).
    /// </summary>
    public static class TacticalObjectiveGate
    {
        /// <summary>One objective's host-side snapshot (the diff unit).</summary>
        public sealed class ObjSnap
        {
            public string ClassName;
            public string DescKey;
            public byte State;
            public int[] Progress;

            public ObjSnap() { ClassName = ""; DescKey = ""; Progress = new int[0]; }
            public ObjSnap(string className, string descKey, byte state, int[] progress)
            {
                ClassName = className ?? "";
                DescKey = descKey ?? "";
                State = state;
                Progress = progress ?? new int[0];
            }
        }

        /// <summary>One ADD-resolution candidate: a child sitting in some local objective's
        /// NextOnSuccess/NextOnFail array (shared def graph → present on both sides).</summary>
        public sealed class AddCandidate
        {
            public string ClassName;
            public string DescKey;
            public bool AlreadyPresent;   // already in the local objective list → never re-added
            public AddCandidate(string className, string descKey, bool alreadyPresent)
            {
                ClassName = className ?? "";
                DescKey = descKey ?? "";
                AlreadyPresent = alreadyPresent;
            }
        }

        /// <summary>HOST: diff <paramref name="current"/> against <paramref name="lastSent"/> → wire records.
        /// <paramref name="seedAll"/> (or an empty/shrunken cache — index alignment can no longer be trusted)
        /// emits a STATE record for EVERY index (the client owns the same def/save-built baseline list, so a
        /// full value-stamp reconciles it). Otherwise: STATE records only for indices whose class, state or
        /// progress changed; indices past the cache tail (mid-mission scripted adds) become ADD records.</summary>
        public static List<TacticalObjectiveCodec.ObjectiveRec> BuildRecords(
            IReadOnlyList<ObjSnap> current, IReadOnlyList<ObjSnap> lastSent, bool seedAll)
        {
            var records = new List<TacticalObjectiveCodec.ObjectiveRec>();
            if (current == null) return records;
            int cached = lastSent != null ? lastSent.Count : 0;
            bool reseed = seedAll || cached == 0 || current.Count < cached;

            for (int i = 0; i < current.Count; i++)
            {
                var now = current[i];
                if (now == null) continue;
                if (reseed)
                {
                    records.Add(ToRec(TacticalObjectiveCodec.KindState, i, now));
                    continue;
                }
                if (i >= cached)
                {
                    records.Add(ToRec(TacticalObjectiveCodec.KindAdd, i, now));
                    continue;
                }
                var prev = lastSent[i];
                if (prev == null ||
                    !string.Equals(prev.ClassName, now.ClassName, StringComparison.Ordinal) ||
                    prev.State != now.State || !ProgressEquals(prev.Progress, now.Progress))
                {
                    records.Add(ToRec(TacticalObjectiveCodec.KindState, i, now));
                }
            }
            return records;
        }

        /// <summary>CLIENT: map the STATE records → (client index, record) applies. A record applies only when
        /// its index is in range AND the client objective at that index has the SAME concrete class name
        /// (ordinal); everything else (index drift, unknown TFTV subclass, ADD records) is skipped — ADD records
        /// are simply ignored here (they ride <see cref="ResolveAddMatch"/>), mismatches land in
        /// <paramref name="skipped"/> for the caller's log-once degrade notice.</summary>
        public static List<KeyValuePair<int, TacticalObjectiveCodec.ObjectiveRec>> ResolveStateApplies(
            IReadOnlyList<TacticalObjectiveCodec.ObjectiveRec> records,
            IReadOnlyList<string> clientClassNames,
            List<TacticalObjectiveCodec.ObjectiveRec> skipped)
        {
            var applies = new List<KeyValuePair<int, TacticalObjectiveCodec.ObjectiveRec>>();
            if (records == null || clientClassNames == null) return applies;
            foreach (var r in records)
            {
                if (r == null || r.Kind != TacticalObjectiveCodec.KindState) continue;
                if (r.Index >= 0 && r.Index < clientClassNames.Count &&
                    string.Equals(clientClassNames[r.Index] ?? "", r.ClassName ?? "", StringComparison.Ordinal))
                {
                    applies.Add(new KeyValuePair<int, TacticalObjectiveCodec.ObjectiveRec>(r.Index, r));
                }
                else
                {
                    skipped?.Add(r);
                }
            }
            return applies;
        }

        /// <summary>CLIENT: resolve an ADD record against the local candidates (children of the shared
        /// NextOnSuccess/NextOnFail def graph). First not-yet-present candidate whose class name matches — and
        /// whose description key matches when BOTH sides carry one — wins. Returns the candidate index, or -1
        /// (unresolvable scripted add → caller degrades with a log-once notice).</summary>
        public static int ResolveAddMatch(
            TacticalObjectiveCodec.ObjectiveRec add, IReadOnlyList<AddCandidate> candidates)
        {
            if (add == null || candidates == null) return -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.AlreadyPresent) continue;
                if (!string.Equals(c.ClassName ?? "", add.ClassName ?? "", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(add.DescKey) && !string.IsNullOrEmpty(c.DescKey) &&
                    !string.Equals(c.DescKey, add.DescKey, StringComparison.Ordinal)) continue;
                return i;
            }
            return -1;
        }

        /// <summary>HOST: build ZONE_UNLOCK records (audit D20) from the unlocked
        /// <c>TacticalLevelLockTagDef</c> guids — one record per DISTINCT non-empty guid, order preserved.
        /// The guid rides <c>DescKey</c> (see <see cref="TacticalObjectiveCodec.KindZoneUnlock"/>); the
        /// other record fields are unused. An empty/null input yields no records (caller skips the flush).</summary>
        public static List<TacticalObjectiveCodec.ObjectiveRec> BuildZoneUnlockRecords(IEnumerable<string> tagGuids)
        {
            var records = new List<TacticalObjectiveCodec.ObjectiveRec>();
            if (tagGuids == null) return records;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in tagGuids)
            {
                if (string.IsNullOrEmpty(g) || !seen.Add(g)) continue;
                records.Add(new TacticalObjectiveCodec.ObjectiveRec(
                    TacticalObjectiveCodec.KindZoneUnlock, 0, 0, "", g, null));
            }
            return records;
        }

        /// <summary>CLIENT: collect the DISTINCT unlocked-tag guids from a batch's ZONE_UNLOCK records
        /// (other kinds and empty guids are ignored). Order preserved.</summary>
        public static List<string> CollectZoneUnlockGuids(IReadOnlyList<TacticalObjectiveCodec.ObjectiveRec> records)
        {
            var guids = new List<string>();
            if (records == null) return guids;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (r == null || r.Kind != TacticalObjectiveCodec.KindZoneUnlock) continue;
                if (string.IsNullOrEmpty(r.DescKey) || !seen.Add(r.DescKey)) continue;
                guids.Add(r.DescKey);
            }
            return guids;
        }

        private static TacticalObjectiveCodec.ObjectiveRec ToRec(byte kind, int index, ObjSnap s)
            => new TacticalObjectiveCodec.ObjectiveRec(kind, index, s.State, s.ClassName, s.DescKey, s.Progress);

        private static bool ProgressEquals(int[] a, int[] b)
        {
            a = a ?? new int[0];
            b = b ?? new int[0];
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
