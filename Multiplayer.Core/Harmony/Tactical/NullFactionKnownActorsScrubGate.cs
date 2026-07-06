namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind <see cref="NullFactionKnownActorsScrubPatch"/> (host deploy-snapshot
    /// null-faction guard). Extracted from the Harmony prefix so the cases are unit-testable.
    ///
    /// WHY (grounded by the HOST stack trace 2026-06-18): <c>TacticalFactionVision.RecordInstanceData</c>
    /// (TacticalFactionVision.cs:968-978) iterates <c>KnownActors</c> and THROWS <c>ArgumentException</c> for
    /// any known actor whose <c>TacticalFaction == null</c> (:973-976). A deploy intruder
    /// (<c>Deploy_Intruder_1x1_Grunt_Elite_and_Tiny</c>) is in <c>KnownActors</c> with a null faction, so the
    /// host's snapshot capture throws — chain: <c>HostOnLevelReady</c> → <c>TacticalLevelController.RecordInstanceData</c>
    /// → <c>Factions.Select(f =&gt; f.RecordInstanceData())</c> → <c>TacticalFaction.RecordInstanceData</c> →
    /// <c>Vision.RecordInstanceData</c> → throw. This aborts <c>HostOnLevelReady</c> at its FIRST step, BEFORE
    /// the <c>tac.deploy</c> broadcast → the client never hydrates → never enters mirror mode → its moves run
    /// locally and no <c>tac.intent.move</c> is ever sent (the observed "Bug #1" is a downstream symptom).
    ///
    /// FIX: in an active synced session, scrub null-faction entries out of <c>KnownActors</c> BEFORE the native
    /// <c>RecordInstanceData</c> copies them (:970), so the snapshot is throw-free AND carries no phantom
    /// null-faction known-actor (which would also break the client's <c>ProcessInstanceData</c>). Narrow,
    /// defensive (mirrors <c>theturned-tftv-compat-required</c>); outside a session the native runs unchanged.
    ///
    ///   * true  -> scrub null-faction KnownActors entries (active synced session).
    ///   * false -> leave KnownActors untouched (single-player / no session → native byte-identical).
    /// </summary>
    public static class NullFactionKnownActorsScrubGate
    {
        public static bool ShouldScrub(bool inActiveSession) => inActiveSession;
    }
}
