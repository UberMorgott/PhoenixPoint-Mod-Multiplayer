using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure (Unity-free) BURST-SAFE holder for the client reward-render "pending" slots, used by
    /// <c>RewardDisplayReflection</c> when <see cref="EventMirrorFixGate"/> is on. Each armed entry keys a reward
    /// snapshot to the EXACT synthetic result <c>GeoscapeEvent</c> instance it must render onto — the page carries
    /// <c>EventID==""</c> so it cannot be matched by id, only by reference identity at the deferred
    /// <c>UIModuleSiteEncounters.ShowEncounter</c> Postfix.
    ///
    /// The legacy design held ONE slot (<c>RewardDisplayReflection._pendingEvent</c>): a SECOND result page armed
    /// before the first's deferred render consumed it — a burst of events in one tick, or an empty-reward
    /// <c>ClearPending</c> following a real-reward arm — clobbered the slot and the first page's reward line was
    /// lost (log-confirmed: host <c>rewardBytes=112</c>, client drew 0 lines). This keyed map lets multiple armed
    /// rewards coexist so each lands on its own page exactly once (one-shot consume). Reference identity is still
    /// the key, but a stale never-consumed arm can no longer evict a live one; the map is FIFO-bounded so a stale
    /// arm (page build failed → never consumed) can never leak. Reset on session teardown.
    /// </summary>
    public sealed class RewardPendingSlots
    {
        /// <summary>Hard cap on coexisting armed slots; mirrors the correlator's pending-dismiss bound. Oldest is
        /// evicted past this so a never-consumed arm can't leak.</summary>
        public const int MaxPending = 16;

        // Reference-identity keyed: two distinct synthetic events are distinct keys even if a type ever overrode
        // Equals/GetHashCode (GeoscapeEvent does not, but key by identity to be safe and explicit).
        private readonly Dictionary<object, RewardDisplaySnapshot> _byEvent =
            new Dictionary<object, RewardDisplaySnapshot>(ReferenceComparer.Instance);
        private readonly Queue<object> _order = new Queue<object>();

        /// <summary>Currently-armed slot count (diagnostics/tests).</summary>
        public int Count => _byEvent.Count;

        /// <summary>
        /// Arm the reward render for a specific synthetic result event instance. A null event or null/empty reward
        /// is ignored. Re-arming the SAME instance overwrites its reward in place (keeps its FIFO position).
        /// </summary>
        public void Arm(object syntheticEvent, RewardDisplaySnapshot reward)
        {
            if (syntheticEvent == null || reward == null || reward.IsEmpty) return;
            if (!_byEvent.ContainsKey(syntheticEvent))
            {
                // Evict the oldest armed slot once at capacity so the map is hard-bounded.
                while (_order.Count >= MaxPending && _order.Count > 0)
                {
                    var oldest = _order.Dequeue();
                    _byEvent.Remove(oldest);
                }
                _order.Enqueue(syntheticEvent);
            }
            _byEvent[syntheticEvent] = reward;
        }

        /// <summary>
        /// If <paramref name="shownEvent"/> has an armed reward, remove + return it (one-shot). Returns null for
        /// any unrelated/already-consumed event — so the render only ever lands on an armed page, exactly once.
        /// </summary>
        public RewardDisplaySnapshot TryConsume(object shownEvent)
        {
            if (shownEvent == null) return null;
            if (!_byEvent.TryGetValue(shownEvent, out var reward)) return null;
            _byEvent.Remove(shownEvent);
            // _order is lazily pruned: a stale token whose entry is gone is skipped on the next eviction sweep.
            return reward;
        }

        /// <summary>Drop all armed slots (session teardown). Idempotent.</summary>
        public void Clear()
        {
            _byEvent.Clear();
            _order.Clear();
        }

        // Reference-identity comparer (net472 has no built-in ReferenceEqualityComparer).
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
