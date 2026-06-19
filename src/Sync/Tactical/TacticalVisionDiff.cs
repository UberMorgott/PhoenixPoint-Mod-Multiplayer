using System.Collections.Generic;

namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) reconcile diff for host→client VISION replication (surface <c>tac.vision</c>). Given
    /// the CLIENT's current known-state map {netId→state} for the player/viewer faction and the host's incoming
    /// snapshot {netId→state}, compute the minimal change set the engine apply must execute:
    ///   • <see cref="VisionDiff.ToSet"/>  — netId→targetState for entries that are NEW or whose state CHANGED
    ///     (upgrade Located→Revealed or downgrade Revealed→Located). An unchanged entry is omitted (idempotent).
    ///   • <see cref="VisionDiff.ToForget"/> — netIds currently known on the client but ABSENT from the snapshot
    ///     (the enemy went out of sight / was forgotten on the host) → clear them so the mirror converges.
    ///
    /// State values match the wire/engine mapping (<see cref="StateRevealed"/> &gt; <see cref="StateLocated"/>):
    /// 2 = Revealed (RED, seen + LOS), 1 = Located (GREY, position known). Re-applying the same snapshot yields an
    /// empty diff (<see cref="VisionDiff.HasChanges"/> == false), so the engine apply is a clean no-op. Unit-tested
    /// in isolation; the engine apply (<c>TacticalVisionSync</c>) just executes the diff against KnownActors.
    /// </summary>
    public static class TacticalVisionDiff
    {
        public const int StateRevealed = 2;   // RED  — seen + line of sight
        public const int StateLocated = 1;     // GREY — position known, no LOS

        public sealed class VisionDiff
        {
            /// <summary>netId → target state, for new or changed entries (apply: set this actor to that state).</summary>
            public readonly Dictionary<int, int> ToSet = new Dictionary<int, int>();
            /// <summary>netIds present on the client but absent from the snapshot (apply: forget/clear them).</summary>
            public readonly List<int> ToForget = new List<int>();
            public bool HasChanges => ToSet.Count > 0 || ToForget.Count > 0;
        }

        /// <summary>Compute the reconcile diff from the client's <paramref name="current"/> known-state map to the
        /// host's <paramref name="incoming"/> snapshot. Both maps are {netId→state}. Null is treated as empty.</summary>
        public static VisionDiff Compute(IDictionary<int, int> current, IDictionary<int, int> incoming)
        {
            var diff = new VisionDiff();
            current = current ?? new Dictionary<int, int>();
            incoming = incoming ?? new Dictionary<int, int>();

            // SET: every incoming entry that is new or whose state differs from the client's current.
            foreach (var kv in incoming)
            {
                if (!current.TryGetValue(kv.Key, out var cur) || cur != kv.Value)
                    diff.ToSet[kv.Key] = kv.Value;
            }

            // FORGET: every currently-known netId that the snapshot no longer lists.
            foreach (var kv in current)
            {
                if (!incoming.ContainsKey(kv.Key))
                    diff.ToForget.Add(kv.Key);
            }

            return diff;
        }
    }
}
