namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure, Unity-free decision (Batch 2): should the HOST arm the synchronized-reveal hold at tactical
    /// LAUNCH, so it stays behind its own loading screen until every client reports load-complete (instead
    /// of revealing the battle the instant its own level is built)? True iff ALL hold:
    ///   • flagOn        — the entry-via-save feature flag (TacticalDeploySync.UseSaveTransferEntry) is on;
    ///   • isHost        — only the co-op host holds a reveal barrier (a client rides its own transfer hold);
    ///   • sessionActive — a co-op session is live (nobody to wait for otherwise);
    ///   • sessionStarted— the host is already IN a started co-op level (SaveTransferCoordinator.SessionStarted,
    ///                     i.e. _begun). The curtain HOLD (SaveTransferMath.HoldCurtain) requires SessionStarted,
    ///                     so arming when it is false would set _revealed=false yet never actually hold — pointless.
    /// Arming is ordering-critical (plan Risk #3): it must run at launch, BEFORE the host reaches tactical
    /// Playing, so _revealed=false is set before CurtainShowPatch.Prefix decides whether to suppress the
    /// native auto-lift. Branch-per-case style mirrors <see cref="TacticalEntryTransferGate"/>.
    /// </summary>
    public static class TacticalEntryBarrierGate
    {
        public static bool ShouldArmHostReveal(
            bool isHost, bool sessionActive, bool sessionStarted, bool flagOn)
            => flagOn && isHost && sessionActive && sessionStarted;
    }
}
