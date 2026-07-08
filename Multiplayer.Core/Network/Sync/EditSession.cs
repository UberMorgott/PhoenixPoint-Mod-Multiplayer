namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE co-op edit-session state machine (BCL-only → unit-testable) — the ONE primitive behind the
    /// soldier-equip co-op sync rebuild (design: docs/superpowers/specs/2026-07-08-coop-edit-session-engine-design.md).
    /// Screens are thin adapters; this holds the "is a repaint safe to fire right now?" decision so the
    /// impure Harmony/UI layer never re-derives it (the guard-vs-guard whack-a-mole the 3 reverted RCA rounds
    /// produced).
    ///
    /// MODEL (single active session — only one equip screen is open at a time; the task API confirms it:
    /// <see cref="ShouldDeferRepaint"/>/<see cref="DrainRepaint"/> carry no unit id):
    ///   • <see cref="Target"/>       — the open screen's unit (null = closed).
    ///   • <see cref="GestureInFlight"/> — a user drag/gesture is physically in hand (between
    ///     <see cref="GestureBegin"/> and <see cref="GestureEnd"/>).
    ///   • <see cref="PendingRepaint"/> — an authoritative remote apply stamped the model and a UI repaint is
    ///     owed but was deferred past an in-flight gesture.
    ///
    /// RULE (spec §Principle-2): the mirror ALWAYS stamps the model; the UI repaint of the session target
    /// defers ONLY while a gesture is in flight, drains the instant it ends, AND has a HARD TIME CAP — after
    /// <c>capTicks</c> from the gesture start the deferral is broken and the repaint is forced even mid-gesture
    /// (a stale view beats a frozen one — the b9144e5 "deferred forever because _uiRefreshNeeded was armed the
    /// whole time the screen was open" lesson). NO clock is read here: the caller passes monotonic
    /// <c>nowTicks</c> (same unit as <c>capTicks</c>) so every decision is pure + deterministic.
    ///
    /// TERMINATION INVARIANTS (pinned by EditSessionTests — the exact loop shapes that burned us):
    ///   • <see cref="DrainRepaint"/> fires AT MOST ONCE per <see cref="RemoteApplied"/> (clear-before-return).
    ///   • It never re-arms itself: a repeated Tick-drain while a gesture is still in flight is a fast no-op
    ///     (no re-arm, no churn) — the 60 Hz re-arm+log loop that collapsed fps cannot recur.
    ///   • The cap guarantees a pending repaint always fires within <c>capTicks</c> — no infinite defer.
    /// </summary>
    public sealed class EditSession
    {
        private readonly long _capTicks;
        private long? _target;
        private bool _gestureInFlight;
        private bool _pendingRepaint;
        private long _gestureStartTicks;

        /// <param name="capTicks">Hard defer cap in the caller's tick unit (~2s of ms). &lt;=0 → never defer
        /// (defensive: an apply always repaints immediately).</param>
        public EditSession(long capTicks) => _capTicks = capTicks > 0 ? capTicks : 0;

        public long? Target => _target;
        public bool IsOpen => _target.HasValue;
        public bool GestureInFlight => _gestureInFlight;
        public bool PendingRepaint => _pendingRepaint;

        /// <summary>The edit screen opened on <paramref name="unitId"/>. Clears any stale gesture/pending from a
        /// prior screen so a drag-skip armed in a dying screen can never fire a spurious first repaint here.</summary>
        public void Open(long unitId)
        {
            _target = unitId;
            _gestureInFlight = false;
            _pendingRepaint = false;
        }

        /// <summary>The edit screen closed. No pending state survives the screen (spec §4). Ignores a close for a
        /// unit other than the one currently open (a stale ExitState of a screen already replaced).</summary>
        public void Close(long unitId)
        {
            if (_target.HasValue && _target.Value != unitId) return;
            _target = null;
            _gestureInFlight = false;
            _pendingRepaint = false;
        }

        /// <summary>A user gesture (drag / click-pick) started on <paramref name="unitId"/>. Anchors the cap at
        /// <paramref name="nowTicks"/>. A gesture implies the screen is open on this unit (self-opens if a raw
        /// begin-drag beat the open hook).</summary>
        public void GestureBegin(long unitId, long nowTicks)
        {
            if (!_target.HasValue || _target.Value != unitId)
            {
                _target = unitId;
                _pendingRepaint = false;
            }
            _gestureInFlight = true;
            _gestureStartTicks = nowTicks;
        }

        /// <summary>The gesture completed / was cancelled. A pending repaint (if one landed mid-gesture) stays
        /// armed and drains on the very next <see cref="DrainRepaint"/> — it no longer defers.</summary>
        public void GestureEnd(long unitId)
        {
            if (_target.HasValue && _target.Value == unitId) _gestureInFlight = false;
        }

        /// <summary>An authoritative remote apply stamped the model → a UI repaint is owed. Coalescing: many
        /// applies while a gesture is in flight collapse into ONE pending repaint (one drain).</summary>
        public void RemoteApplied() => _pendingRepaint = true;

        /// <summary>TRUE ⇒ hold the repaint: a gesture is physically in flight AND still within the hard cap from
        /// its start. The instant the gesture ends OR the cap elapses this goes false and the repaint is released.</summary>
        public bool ShouldDeferRepaint(long nowTicks)
            => _gestureInFlight && (nowTicks - _gestureStartTicks) < _capTicks;

        /// <summary>Consume the pending repaint iff one is owed and it is no longer being deferred. Clears the
        /// flag BEFORE returning true (clear-before-fire) so a re-entrant apply during the repaint can't
        /// double-fire and a repeated drain can't re-arm without a new <see cref="RemoteApplied"/>.</summary>
        public bool DrainRepaint(long nowTicks)
        {
            if (!_pendingRepaint) return false;
            if (ShouldDeferRepaint(nowTicks)) return false;
            _pendingRepaint = false;
            return true;
        }
    }
}
