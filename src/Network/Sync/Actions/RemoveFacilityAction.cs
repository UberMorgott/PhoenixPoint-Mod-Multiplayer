using System;
using System.IO;

namespace Multiplayer.Network.Sync.Actions
{
    /// <summary>
    /// Demolishes (scraps) a facility in a base. Native chokepoint:
    /// <c>GeoPhoenixBase.RemoveFacility(GeoPhoenixFacility, bool scrap = false)</c> (GeoPhoenixBase.cs:277)
    /// — wallet refund (if scrap) → <c>facility.DestroyFacility()</c> → <c>Layout.RemoveFacility</c> →
    /// <c>UninitFacility</c> → <c>UpdateStats</c>. The SAME native call is also CANCEL-CONSTRUCTION
    /// (scrapping an under-construction facility; <c>GetRefundForFacilityScrap</c> handles
    /// <c>FacilityState.UnderContstruction</c>, :310) and the internal site-destroyed facility drain
    /// (<c>Site_StateChanged</c>, :762) — so this ONE action id covers the whole demolish family.
    /// There is no facility-upgrade mechanic in PP (no such symbol in the decompile) — no gap there.
    ///
    /// Wire payload: <c>string baseId, string facilityId, i32 gridX, i32 gridY, bool scrap</c>.
    /// Same identity scheme as <see cref="RepairFacilityAction"/>: FacilityId primary, grid fallback.
    ///
    /// NOT IHostOnlyApply — the client MUST replay the structural removal: there is no facility state
    /// channel to converge the layout otherwise, so suppressing the replay leaves a ghost facility on the
    /// client (the exact in-game bug this action fixes). The WALLET side is NOT replayed though: the 0xA0
    /// wallet snapshot is the one balance writer (sync canon), and the scrap refund depends on host-side
    /// Health/Construction percentages a frozen client cannot reproduce — so a client replay forces
    /// scrap:false (<see cref="ReplayScrap"/>) and the refund converges via the wallet echo. Idempotent:
    /// a facility already absent on the replayer resolves to null → no-op.
    /// </summary>
    public sealed class RemoveFacilityAction : ISyncedAction
    {
        private readonly string _baseId;
        private readonly string _facilityId;
        private readonly int _gridX;
        private readonly int _gridY;
        private readonly bool _scrap;

        public RemoveFacilityAction(string baseId, string facilityId, int gridX, int gridY, bool scrap)
        {
            _baseId = baseId;
            _facilityId = facilityId;
            _gridX = gridX;
            _gridY = gridY;
            _scrap = scrap;
        }

        /// <summary>Host-side scrap flag as sent (refund on demolish; false for the site-destroyed drain).</summary>
        public bool Scrap => _scrap;

        public ushort ActionId => SyncedActionIds.RemoveFacility;
        public ActionCategory Category => ActionCategory.BaseConstruction;

        public void Write(BinaryWriter w)
        {
            w.Write(_baseId ?? "");
            w.Write(_facilityId ?? "");
            w.Write(_gridX);
            w.Write(_gridY);
            w.Write(_scrap);
        }

        public static ISyncedAction Read(BinaryReader r)
            => new RemoveFacilityAction(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean());

        public bool Validate(GeoRuntime rt, Guid actor)
            => !string.IsNullOrEmpty(_baseId) && rt != null && rt.IsGeoscapeActive;

        /// <summary>
        /// Wallet one-writer decision: only the authoritative host executes the scrap refund
        /// (client-relayed demolish applied in <c>OnActionRequest</c>); a client replay is
        /// structural-only — its refund converges via the 0xA0 wallet snapshot.
        /// </summary>
        public static bool ReplayScrap(bool isAuthoritativeHost, bool wireScrap)
            => isAuthoritativeHost && wireScrap;

        /// <summary>
        /// Host-authority provider, wired by the SyncEngine ctor to the live engine's IsHost (this file
        /// stays NetworkEngine-free so the pure wire/decision tests can link it — the project's
        /// pure-core/game-glue split). Defaults to false = the safe structural-only replay.
        /// </summary>
        public static Func<bool> IsAuthoritativeHost = () => false;

        public void Apply(GeoRuntime rt)
            => BaseReflection.Remove(rt, _baseId, _facilityId, _gridX, _gridY,
                ReplayScrap(IsAuthoritativeHost(), _scrap));
    }
}
