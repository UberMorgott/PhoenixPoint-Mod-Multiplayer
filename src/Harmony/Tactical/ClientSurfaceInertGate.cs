namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the TS3 ground-surface inert guards (<see cref="ClientSurfaceInertGuards"/>).
    /// Extracted from the Harmony prefixes so the "no double damage / no double status" contract is unit-testable
    /// without NetworkEngine or game types.
    ///
    /// On a co-op CLIENT inside a mirrored tactical mission the frozen client re-applies the host's ground volumes
    /// for DISPLAY + LoS, but their DAMAGE / STATUS must NOT re-run (host-authoritative — rides tac.damage 0x88 /
    /// the 0x8F status delta). Every ground volume on the client is a host mirror (client abilities are suppressed),
    /// so the decision is simply "are we a client mirror?": true → SKIP the native surface-gameplay method
    /// (goo-status apply / fire-damage-on-enter / per-turn fire+goo tick). Off-client / host / single-player →
    /// false → run native unchanged (byte-identical to vanilla).
    /// </summary>
    public static class ClientSurfaceInertGate
    {
        /// <summary>True → SUPPRESS the native surface-gameplay method (client mirror owns display only). False →
        /// run native (host / single-player / no session).</summary>
        public static bool ShouldSuppress(bool isClientMirroring) => isClientMirroring;
    }
}
