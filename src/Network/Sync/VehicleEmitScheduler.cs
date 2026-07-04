namespace Multipleer.Network.Sync
{
    /// <summary>
    /// Pure, Unity-free scheduling helper for the HOST's geoscape vehicle-mirror poll (0xA5 position + 0xA6
    /// travel-meta), which <c>SyncEngine.Tick</c> throttles to every Nth frame. Two decisions live here so both
    /// are directly unit-testable without <c>SyncEngine</c>/Unity:
    /// <list type="bullet">
    ///   <item>WHICH applied host action collapses the poll latency to the NEXT frame — a vehicle order
    ///   (StartTravel → <c>MoveVehicleAction</c> / StartExploringCurrentSite → <c>ExploreSiteAction</c>, both
    ///   <see cref="ActionCategory.VehicleTravel"/>) just changed a vehicle's authoritative travel state, so the
    ///   route line + first placement should ship at once instead of waiting up to a full poll interval.</item>
    ///   <item>the throttle-counter math — the Tick gate comparison and the counter value that "arms" an
    ///   immediate emit.</item>
    /// </list>
    /// </summary>
    public static class VehicleEmitScheduler
    {
        // ─── CANONICAL host vehicle-mirror emit cadence (single source of truth; Unity-free so it's testable) ──
        // SyncEngine.Tick throttles the 0xA5 position + 0xA6 travel-meta polls to every EmitTickInterval-th frame,
        // and GeoVehicleMirror derives its client interp delay from these SAME numbers — so the emit rate and the
        // buffer sizing can never drift apart. 6 ticks @60fps ≈ 10 Hz (was 15 / ~4 Hz); the per-vehicle signature
        // skip keeps idle traffic at zero, so a faster poll only tightens latency for MOVING vehicles.
        public const int EmitTickInterval = 6;
        public const double NominalFps = 60.0;
        public const double EmitDelayMultiplier = 1.5;   // render 1.5 emit-intervals behind newest snapshot

        /// <summary>An applied host action of this category just changed a vehicle's authoritative travel state:
        /// force an immediate 0xA5+0xA6 emit rather than waiting for the next poll boundary. Today that is exactly
        /// the vehicle-order category (travel + explore both ride <see cref="ActionCategory.VehicleTravel"/>).</summary>
        public static bool TriggersImmediateEmit(ActionCategory category)
            => category == ActionCategory.VehicleTravel;

        /// <summary>The Tick throttle gate: SyncEngine increments the counter each frame then polls when it reaches
        /// the interval. Pure mirror of that comparison (<c>++counter &gt;= interval</c>).</summary>
        public static bool ShouldPoll(int counterAfterIncrement, int interval)
            => counterAfterIncrement >= interval;

        /// <summary>Counter value that makes the NEXT Tick fire the poll (the following <c>++counter &gt;= interval</c>
        /// is then true). Used to honor an immediate-emit request WITHOUT emitting mid-apply (which would be
        /// re-entrant / read a not-yet-moved transform); the very next frame's existing poll path ships it.</summary>
        public static int ArmImmediate(int interval) => interval;
    }
}
