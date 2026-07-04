using System;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// The engine-side seam passed to <see cref="SurfaceRouter.OnInbound"/>, abstracted so the router has
    /// no <c>NetworkEngine</c> / Unity / HarmonyLib dependency (keeps the router pure + unit-testable —
    /// the same constraint every other tested sync primitive obeys: it must not touch a game-bound type
    /// at RUNTIME, or the test assembly's missing <c>UnityEngine.CoreModule</c> / <c>0Harmony</c> throw a
    /// load exception). The production implementation is <c>SyncEngine</c>; tests use a capturing fake that
    /// returns a null runtime and no-op UI refresh. (The dead 0x67 action-relay members — RejectTo /
    /// RebroadcastActionApply / MarkSurfaceDirty — were removed with the never-wired action router.)
    /// </summary>
    public interface ISyncSink
    {
        /// <summary>True on the authoritative host (drives the host-only pipeline branch).</summary>
        bool IsHost { get; }

        /// <summary>The live geoscape runtime handle passed to action Validate/Apply (null in tests).</summary>
        GeoRuntime Runtime { get; }

        /// <summary>Map a transport peer id to the player guid, or <see cref="Guid.Empty"/> if unmapped.</summary>
        Guid ResolveActor(ulong peerId);

        /// <summary>
        /// After a synced apply, re-drive the open "needs-kick" geoscape modules (research / manufacturing /
        /// base-layout) so a model mutation becomes visible without re-entering the screen. Mirrors the legacy
        /// <c>GeoUiRefresh.RefreshNeedsKick</c> fan-out; no-op in tests (no Unity).
        /// </summary>
        void RefreshUi();
    }
}
