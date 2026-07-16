namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Client-only latch spanning the geoscape→tactical launch window. While set, our sync layer must NOT
    /// apply host wallet echoes or force the persistent resource bars to repaint: during that window the
    /// geoscape UI is being torn down, yet <c>GeoRuntime.Wallet()</c>/<c>GeoLevel()</c> are still non-null,
    /// so each late <c>Wallet.Apply</c> fires <c>ResourcesChanged</c> → <c>UIModuleInfoBar.UpdateResourceInfo</c>
    /// → <c>RefreshResourceText</c>, which TFTV postfixes with no level-alive guard → NRE popup storm on the
    /// client. Set at deploy-driven launch begin (<see cref="Multiplayer.Harmony.Tactical.LaunchTacticalGameGatePatch"/>),
    /// cleared at BOTH tactical level-ready and geoscape (re)load (double clear = never stuck if a launch aborts).
    /// Wallet syncs skipped here are recovered by the drift-poll / full-wallet re-broadcast on geoscape re-entry.
    /// </summary>
    public static class GeoTransitionGate
    {
        public static bool InTransition;
    }
}
