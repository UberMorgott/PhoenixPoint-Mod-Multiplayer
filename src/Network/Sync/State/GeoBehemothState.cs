using System.Globalization;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure conventions for the BEHEMOTH mirror (WA-1, gap 5a — Festering Skies). <c>GeoBehemothActor</c> is a
    /// <c>GeoActor</c> that is NOT in <c>GeoMap.Vehicles</c> (GeoMap.cs:241), so the existing vehicle walks
    /// miss it entirely. Instead of a new rail it rides the EXISTING surfaces under one RESERVED composite key:
    ///   • position/heading updates ride the ~4 Hz 0xA5 walk (host appends one extra record —
    ///     <c>GeoLevel.AlienFaction.Behemoth</c>, read in <c>GeoBehemothReflection</c>);
    ///   • presence / <c>BehemothStatus</c> / tombstone ride the vehicle-creation channel #6 as a SENTINEL
    ///     <see cref="GeoVehicleIdentity"/>: <c>OwnerFactionDefGuid == "__behemoth"</c> marks the branch and
    ///     <c>VehicleSetDefGuid</c> carries the status digit — NO real guids are needed on the wire because the
    ///     client resolves the spawn template locally (<c>FesteringSkiesSettings.BehemothDef</c>, the exact def
    ///     the native <c>GeoAlienFaction.SpawnBehemoth</c> instantiates — GeoAlienFaction.cs:1333) — so the
    ///     existing codec/tracker/tombstone machinery is reused wholesale, wire-format unchanged.
    /// The reserved owner key is the FNV-1a hash of "__behemoth" — no faction def asset can collide with a
    /// double-underscore name, and the hash is deterministic on both ends (same derivation as real owners).
    /// Pure + Unity-free (unit-tested); the game-bound glue lives in <c>GeoBehemothReflection</c>.
    /// </summary>
    public static class GeoBehemothState
    {
        /// <summary>Sentinel carried in <see cref="GeoVehicleIdentity.OwnerFactionDefGuid"/> (never a real guid).</summary>
        public const string OwnerSentinel = "__behemoth";

        /// <summary>Reserved owner half of the composite key (FNV-1a of the sentinel, like real owners).</summary>
        public static readonly int OwnerId = GeoVehiclePos.StableOwnerKey(OwnerSentinel);

        /// <summary>There is at most ONE behemoth per campaign (GeoAlienFaction.Behemoth) — fixed id.</summary>
        public const int VehicleId = 1;

        /// <summary>The behemoth's reserved composite key on the 0xA5/#6 surfaces.</summary>
        public static long Key => GeoVehiclePos.MakeKey(OwnerId, VehicleId);

        // GeoBehemothActor.BehemothStatus (decompile GeoBehemothActor.cs:31): None/Idle/Moving/Dormant/Dead.
        public const byte StatusNone = 0;
        public const byte StatusIdle = 1;
        public const byte StatusMoving = 2;
        public const byte StatusDormant = 3;
        public const byte StatusDead = 4;

        public static bool IsBehemothKey(long key) => key == Key;

        /// <summary>True when a channel-#6 identity is the behemoth sentinel entry (branch before any
        /// GeoVehicle spawn/despawn logic runs).</summary>
        public static bool IsBehemothIdentity(GeoVehicleIdentity id) => id.OwnerFactionDefGuid == OwnerSentinel;

        /// <summary>Build the sentinel identity: reserved key + status digit + current placement (so a client
        /// spawning the mirror places it correctly before the next 0xA5 record lands).</summary>
        public static GeoVehicleIdentity MakeIdentity(byte status, GeoVehiclePos placement)
            => new GeoVehicleIdentity(OwnerId, VehicleId, OwnerSentinel,
                                      status.ToString(CultureInfo.InvariantCulture),
                                      placement.QX, placement.QY, placement.QZ, placement.QW,
                                      placement.X, placement.Y, placement.Z);

        /// <summary>Parse the status digit back out of a sentinel identity. False for non-behemoth identities
        /// or a malformed digit (fail closed — the presence apply is skipped, healed by re-emission).</summary>
        public static bool TryParseStatus(GeoVehicleIdentity id, out byte status)
        {
            status = 0;
            if (!IsBehemothIdentity(id)) return false;
            return byte.TryParse(id.VehicleSetDefGuid, NumberStyles.None, CultureInfo.InvariantCulture, out status);
        }

        /// <summary>Native visuals contract: Dormant (submerged — <c>SetSubmergeVisuals</c> hides VisualsRoot)
        /// and Dead hide the model; every surface status shows it.</summary>
        public static bool VisualsVisible(byte status) => status != StatusDormant && status != StatusDead;

        /// <summary>Native animator contract (GeoBehemothActor: IdleState=0, MovingState=1).</summary>
        public static int AnimatorState(byte status) => status == StatusMoving ? 1 : 0;
    }
}
