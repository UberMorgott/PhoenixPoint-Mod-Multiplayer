namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE (Unity-free, unit-testable) guard deciding whether a <c>GeoscapeView.ToCutsceneState</c> call is
    /// broadcast to clients (<c>CutsceneMirrorPatch.Postfix</c>).
    ///
    /// The <c>SyncApplyScope.IsApplying</c> skip applies ONLY on the NON-HOST: it exists so the client's own
    /// mirror-driven <c>ToCutsceneState</c> replay (PlayCutsceneAction.Apply runs inside the scope) can never
    /// re-broadcast. On the HOST a cutscene can fire SYNCHRONOUSLY INSIDE a client-relayed action apply —
    /// SyncEngine wraps the authoritative <c>action.Apply</c> in <c>SyncApplyScope.Enter()</c> (SyncEngine.cs:208),
    /// and a relayed explore of an ExplorationTime==0 story site runs StartExploringCurrentSite → SiteExplored
    /// inline (GeoVehicle.cs:417-419) → GeoFactionReward.Apply → ToCutsceneState — and MUST still broadcast, or
    /// the ordering client never sees the cutscene. IsApplying is never a host re-broadcast echo: the host never
    /// applies PlayCutsceneAction (OnActionApply is host-gated, SyncEngine.cs:248).
    /// </summary>
    public static class CutsceneBroadcastDecision
    {
        /// <summary>Broadcast ⇔ host authority with an active session; the IsApplying suppression is scoped to the
        /// non-host (belt-and-braces vs the host gate — a non-host never broadcasts either way). PURE.</summary>
        public static bool ShouldBroadcast(bool isHost, bool isActiveSession, bool isApplying)
        {
            if (isApplying && !isHost) return false;   // client mirror-driven replay → never re-broadcast
            return isHost && isActiveSession;          // host authority only (host IsApplying = relayed apply, still broadcasts)
        }
    }
}
