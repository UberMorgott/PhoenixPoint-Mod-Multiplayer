namespace Multipleer.UI
{
    /// <summary>
    /// Pure, Unity-free easing math for the co-op load bars. Kept separate from the MonoBehaviour
    /// (LoadOverlayController) so the monotonic-up + clamp behavior is unit-testable without Unity.
    ///
    /// The native LoadingProgress source is coarse/step-quantized (pct jumps 0→40→69→80→done), so the
    /// raw fill snaps and plateaus. EaseFill animates the DISPLAYED fill toward the latest target a
    /// bounded step per frame, turning the few coarse values into a continuously climbing bar.
    /// </summary>
    public static class FillEase
    {
        /// <summary>
        /// Move <paramref name="current"/> toward <paramref name="target"/> by at most
        /// <paramref name="maxStep"/>, but NEVER backward (progress only ever climbs), and clamp the
        /// result to [0, 1]. <paramref name="maxStep"/> is the per-frame budget (e.g. rate * deltaTime).
        /// </summary>
        public static float EaseFill(float current, float target, float maxStep)
        {
            if (current < 0f) current = 0f;
            if (current > 1f) current = 1f;
            if (target > 1f) target = 1f;

            // Monotonic: never recede. If the target is at/below the displayed value, hold.
            if (target <= current) return current;
            if (maxStep <= 0f) return current;

            var next = current + maxStep;
            if (next > target) next = target; // don't overshoot the real target
            if (next > 1f) next = 1f;
            return next;
        }
    }
}
