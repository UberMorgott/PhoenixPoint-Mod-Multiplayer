using System;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// The engine-side seam of the unified sync pipeline, abstracted so <see cref="SurfaceRouter"/> has
    /// no <c>NetworkEngine</c> / Unity / HarmonyLib dependency (keeps the router pure + unit-testable —
    /// the same constraint every other tested sync primitive obeys: it must not touch a game-bound type
    /// at RUNTIME, or the test assembly's missing <c>UnityEngine.CoreModule</c> / <c>0Harmony</c> throw a
    /// load exception). Every outbound effect AND every game-runtime handle the router needs is routed
    /// through here. The production implementation is <c>SyncEngine</c>; tests use a capturing fake that
    /// returns a null runtime and no-op UI refresh.
    /// </summary>
    public interface ISyncSink
    {
        /// <summary>True on the authoritative host (drives the host-only pipeline branch).</summary>
        bool IsHost { get; }

        /// <summary>The live geoscape runtime handle passed to action Validate/Apply (null in tests).</summary>
        GeoRuntime Runtime { get; }

        /// <summary>Map a transport peer id to the player guid, or <see cref="Guid.Empty"/> if unmapped.</summary>
        Guid ResolveActor(ulong peerId);

        /// <summary>Host: send a rejection envelope back to the originating peer.</summary>
        void RejectTo(ulong peerId, byte surfaceId);

        /// <summary>Host: assign a sequence and broadcast an ActionApply envelope for this surface to all peers.</summary>
        void RebroadcastActionApply(byte surfaceId, ulong sequence, byte[] payload);

        /// <summary>Host: a surface mutated authoritatively; mark its state channel dirty if it has one (no-op for pure actions).</summary>
        void MarkSurfaceDirty(byte surfaceId);

        /// <summary>
        /// After a synced apply, re-drive the open "needs-kick" geoscape modules (research / manufacturing /
        /// base-layout) so a model mutation becomes visible without re-entering the screen. Mirrors the legacy
        /// <c>GeoUiRefresh.RefreshNeedsKick</c> fan-out; no-op in tests (no Unity).
        /// </summary>
        void RefreshUi();
    }
}
