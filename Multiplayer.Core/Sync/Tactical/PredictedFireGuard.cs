using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free, unit-tested) de-dup ledger for CLIENT-PREDICTED fire animations (combat concurrency fix).
    ///
    /// A relayed long-range (sniper) host shot is DEFERRED — <c>ShootAbility.Activate</c> enqueues it
    /// (<c>EnqueueAction(soloAfterCurrent)</c> + camera-blend), so the host's shot coroutine — and thus the
    /// host's <c>tac.fire.start</c> broadcast (sent from <c>FireWeaponAtTargetCrt</c>'s host prefix) — only begins
    /// AFTER that defer. A no-defer client replay driven off that echoed instant therefore ALWAYS animates late.
    /// The fix: the originating client plays a PREDICTED local fire animation immediately on its own press
    /// (<c>TacticalFireAnimSync.ClientPredictFireStart</c>, reusing the same damage-less / camera-silent replay) and
    /// records the shooter here. The host still runs the authoritative shot and BROADCASTS <c>tac.fire.start</c> back
    /// to ALL peers — INCLUDING the originator. <c>TacticalFireAnimSync.ClientOnFireStart</c> consults this ledger:
    ///   • a live predicted entry for the incoming shooter ⇒ this is the client's OWN echo → CONSUME + SKIP the
    ///     replay (the predicted anim already played) — the shooter animates EXACTLY ONCE;
    ///   • no entry ⇒ another viewer's / host-origin shot (e.g. overwatch reaction) → REPLAY normally.
    ///
    /// Entries SELF-EXPIRE on a TTL so a never-arriving echo (the host rejected/redirected the shot — a cosmetic
    /// mispredict, damage is 100% host-authoritative via tac.damage) never leaks an entry or blocks a later legit
    /// replay of the same shooter. Keyed by shooter netId; multiple in-flight predicts for one shooter are FIFO-queued
    /// so rapid re-fires de-dup 1:1. Deterministic given (shooterNetId, now) — the runtime passes a monotonic clock.
    /// </summary>
    public sealed class PredictedFireGuard
    {
        // shooterNetId -> FIFO queue of expiry DEADLINES (one per outstanding predicted anim). A deadline is
        // (recordTime + ttl); an entry whose deadline is < now is expired and purged (never matched). Because the
        // runtime clock is monotonic, deadlines are enqueued in non-decreasing order → expired ones sit at the front.
        private readonly Dictionary<int, Queue<float>> _pending = new Dictionary<int, Queue<float>>();
        private readonly float _ttlSeconds;

        /// <summary>Default echo-wait window (seconds). A host shot's intent→run→broadcast round-trip stays well
        /// under this even for a deferred (camera-blend) long-range shot; a never-arriving echo purges after it.</summary>
        public const float DefaultTtlSeconds = 8f;

        public PredictedFireGuard(float ttlSeconds = DefaultTtlSeconds) { _ttlSeconds = ttlSeconds; }

        /// <summary>Record one predicted local fire anim for <paramref name="shooterNetId"/> at time
        /// <paramref name="now"/>; its host echo is expected within the TTL. Negative netIds are ignored.</summary>
        public void RecordPredicted(int shooterNetId, float now)
        {
            if (shooterNetId < 0) return;
            if (!_pending.TryGetValue(shooterNetId, out var q))
            {
                q = new Queue<float>();
                _pending[shooterNetId] = q;
            }
            q.Enqueue(now + _ttlSeconds);
        }

        /// <summary>Consult on an incoming fire-start at time <paramref name="now"/>. Returns TRUE when a live
        /// (non-expired) predicted entry for <paramref name="shooterNetId"/> matched and was CONSUMED → the caller
        /// SKIPS the replay (this is the client's own echoed shot). Returns FALSE when there is no live entry → the
        /// caller REPLAYS. Expired entries at the front of the queue are purged first and never matched.</summary>
        public bool ConsumeIfPredicted(int shooterNetId, float now)
        {
            if (!_pending.TryGetValue(shooterNetId, out var q)) return false;
            while (q.Count > 0 && q.Peek() < now) q.Dequeue();   // purge expired (front-loaded) deadlines
            if (q.Count == 0)
            {
                _pending.Remove(shooterNetId);
                return false;
            }
            q.Dequeue();                                          // consume the matched predicted entry
            if (q.Count == 0) _pending.Remove(shooterNetId);
            return true;
        }

        /// <summary>Drop all pending entries (mission exit). Idempotent.</summary>
        public void Reset() => _pending.Clear();

        /// <summary>Count of outstanding (not yet consumed/purged) predicted entries — for tests/diagnostics.</summary>
        public int PendingCount
        {
            get
            {
                int n = 0;
                foreach (var q in _pending.Values) n += q.Count;
                return n;
            }
        }
    }
}
