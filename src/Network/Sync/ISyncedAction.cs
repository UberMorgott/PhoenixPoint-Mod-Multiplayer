using System;
using System.IO;

namespace Multipleer.Network.Sync
{
    /// <summary>
    /// A discrete, serializable, replayable shared-state mutation. Each game action we sync gets
    /// exactly one <see cref="ISyncedAction"/> implementation plus one thin Harmony interceptor.
    /// </summary>
    public interface ISyncedAction
    {
        /// <summary>Stable wire id (see <see cref="SyncedActionIds"/>).</summary>
        ushort ActionId { get; }

        /// <summary>Permission category gated by <see cref="PermissionGate"/>.</summary>
        ActionCategory Category { get; }

        /// <summary>Write the action payload only (no id/sequence/nonce header).</summary>
        void Write(BinaryWriter w);

        /// <summary>Host-side pre-apply validity check (e.g. enough funds, target exists).</summary>
        bool Validate(GeoRuntime rt, Guid actor);

        /// <summary>
        /// Execute the real mutation. Runs on the host (authoritative) and on clients during
        /// replay — always inside <see cref="SyncApplyScope"/> so interceptors pass through.
        /// </summary>
        void Apply(GeoRuntime rt);
    }

    /// <summary>
    /// Marker for actions whose <see cref="ISyncedAction.Apply"/> must run ONLY on the authoritative host
    /// and be SUPPRESSED on a client replay. The host applies the outcome exactly once; the client must not
    /// re-run reward/outcome side-effects (it would diverge from the host) — its synced consequences arrive
    /// through the dedicated echoes/channels (wallet, inventory, research) instead. Used by event-answer
    /// outcomes whose effects are not fully channelled. The host call path (OnActionRequest) never checks
    /// this; only the client replay path (OnActionApply) does.
    /// </summary>
    public interface IHostOnlyApply { }

    /// <summary>Reconstructs an action from its payload bytes.</summary>
    public delegate ISyncedAction ActionReader(BinaryReader r);
}
