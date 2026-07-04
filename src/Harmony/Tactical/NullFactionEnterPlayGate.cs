namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind <see cref="NullFactionEnterPlayPatch"/> (deploy null-faction guard).
    /// Extracted from the Harmony prefix so the cases are unit-testable without NetworkEngine or game types.
    ///
    /// WHY (grounded, decompile file:line): a deploy actor (observed: <c>Deploy_Intruder_1x1_Grunt_Elite_and_Tiny</c>)
    /// can enter play with an UNINITIALIZED faction — <c>TacticalActorBase.OnEnterPlay</c>
    /// (TacticalActorBase.cs:523-543) resolves <c>GetTacticalFaction(def.FactionDef)</c>, and when that faction
    /// is not initialized in this mission it logs + calls <c>SetFaction(null)</c>. Then
    /// <c>FinalizeEnterPlay</c> (:546) → <c>ActorEnteredPlay</c> → <c>TacticalFactionVision.OnActorEnteredPlay</c>
    /// (TacticalFactionVision.cs:345-348) THROWS <c>ArgumentException</c> on the null faction. In a synced
    /// session this throw propagates into the host's deploy-capture (<c>TacticalDeploySync.HostOnLevelReady</c>)
    /// and aborts part of host setup ("HostOnLevelReady failed: …OnActorEnteredPlay()… null faction").
    ///
    /// The mod neither spawns nor re-enters these actors — it is a native/TFTV deploy-ordering quirk. The narrow
    /// defensive fix (mirrors the <c>theturned-tftv-compat-required</c> pattern: guard a third-party-caused crash
    /// on our side) is: in an ACTIVE synced session, SKIP the vision handler for a null-faction actor (it would
    /// only add the actor to vision and then throw — skipping is correct, a faction-less actor has no meaningful
    /// vision relation). Outside a synced session the native handler runs unchanged (single-player byte-identical).
    ///
    ///   * true  -> run the native OnActorEnteredPlay (no session, OR the actor has a real faction).
    ///   * false -> SUPPRESS: active synced session AND the entering actor's faction is null (would throw).
    /// Soldiers always carry a faction → factionIsNull=false → run native → the verified soldier-load is never
    /// touched. Only the faction-less intruder is skipped.
    /// </summary>
    public static class NullFactionEnterPlayGate
    {
        public static bool ShouldRunNativeEnterPlay(bool inActiveSession, bool factionIsNull)
        {
            if (!inActiveSession) return true;   // single-player / no session → native runs unchanged
            if (!factionIsNull) return true;     // real faction (every soldier) → native runs (no throw)
            return false;                        // active session + null faction → suppress the native throw
        }
    }
}
