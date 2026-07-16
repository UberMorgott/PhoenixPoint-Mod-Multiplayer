namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free once-latch for the post-reload full re-seed (rca-4). Armed by the host EXACTLY
    /// when an F2 mid-session reload transfer actually launches (never by the lobby FIRST start — there
    /// the save itself is the seed — and never on a client/joiner), then consumed AT MOST ONCE at the
    /// reveal moment (RevealAll broadcast), so a double reveal-release can never double-reseed. Extracted
    /// from the game-bound <c>SaveTransferCoordinator</c> so the arm/consume-once contract is directly
    /// unit-testable without any game DLL; the coordinator forwards to this.
    /// </summary>
    public sealed class ReseedOnceGate
    {
        private bool _pending;

        /// <summary>True while a re-seed is armed and not yet consumed (diagnostic/test view).</summary>
        public bool Pending => _pending;

        /// <summary>Arm the latch (mid-session reload transfer launched). Re-arming is idempotent.</summary>
        public void Arm() => _pending = true;

        /// <summary>Consume the latch: true exactly once per <see cref="Arm"/>; false when not armed
        /// (lobby first start, joiner path) and on every repeat call (double-release safety).</summary>
        public bool TryConsume()
        {
            if (!_pending) return false;
            _pending = false;
            return true;
        }
    }
}
