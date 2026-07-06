using System;
using System.Collections.Generic;

namespace Multiplayer.Sync.Geoscape
{
    /// <summary>
    /// PURE (engine-free) policy for the generic GEOSCAPE ability-INTENT relay (the geoscape analogue of the
    /// tactical 0x8E generic relay — <c>Multiplayer.Sync.Tactical.TacticalAbilityRelay</c>).
    ///
    /// The seven sim-MUTATING geoscape abilities below all share ONE entry chokepoint —
    /// <c>GeoAbility.Activate(GeoAbilityTarget)</c> (GeoAbility.cs:130) — and differ only in the native sink
    /// their <c>ActivateInternal</c> calls. A mirroring client's sim is FROZEN, so a locally-run activation
    /// neither advances (its scheduled effect never ticks) nor reaches the host — the order silently dies.
    /// So the client SUPPRESSES the local <c>Activate</c> and relays a single
    /// <see cref="Multiplayer.Network.Sync.Actions.GeoAbilityActivateAction"/> (participant + ability-def-guid +
    /// target-by-id/pos); the HOST re-resolves the live actor + its GeoAbility by def guid and <c>Activate</c>s it
    /// authoritatively. Each ability's OUTCOME already rides an ALREADY-SHIPPED geoscape state channel (site
    /// state #, faction/wallet, mid-session actor spawn), so every peer converges with no new outcome path —
    /// the host's native apply fires the very change-events those channels subscribe to.
    ///
    /// This class is the ALLOWLIST that decides which <c>GeoAbility</c> subclasses ride the relay, matched by
    /// simple runtime type NAME so it stays Unity-free and unit-testable. ONE Harmony prefix binds the base
    /// <c>GeoAbility.Activate</c> (all subclasses inherit it — none of the seven override it), and this allowlist
    /// scopes it: an off-list ability (e.g. <c>MoveVehicleAbility</c>, whose travel is relayed by its OWN
    /// <c>StartTravel</c> patch) passes straight through, so no ability is double-handled.
    /// </summary>
    public static class GeoAbilityRelay
    {
        /// <summary>Runtime type NAMES (Type.Name, not full namespace) of the sim-mutating <c>GeoAbility</c>
        /// subclasses relayed client→host. Adding a name here is the ONLY change needed to relay another
        /// target-taking geoscape ability that goes through <c>GeoAbility.Activate</c>.
        /// <list type="bullet">
        ///   <item><c>HarvestFromSiteAbility</c> — vehicle collects from its current AncientHarvest site
        ///     (<c>GeoVehicle.StartCollectingFromCurrentSite</c>); resources mirror via the wallet channel.</item>
        ///   <item><c>ExcavateAbility</c> — vehicle starts excavating an archeology site
        ///     (<c>GeoPhoenixFaction.StartExcavatingSite</c>); the site state channel mirrors ExcavatingSites.</item>
        ///   <item><c>EmergencyRepairAbility</c> — schedules a repair on a downed vehicle
        ///     (<c>GeoVehicle.ScheduleRepair</c>); the vehicle mirror carries the result.</item>
        ///   <item><c>ScanAbility</c> — creates a scanner over a site (<c>GeoFaction.CreateScanner</c>);
        ///     the faction/site channels mirror the scanner.</item>
        ///   <item><c>AncientSiteProbeAbility</c> — launches a probe at a POSITION
        ///     (<c>GeoFaction.CreateAncientSiteProbe</c>); the mid-session actor-spawn channel mirrors it.</item>
        ///   <item><c>ActivateBaseAbility</c> — activates an abandoned Phoenix base from exploration
        ///     (<c>GeoPhoenixFaction.ActivateBaseFromExploration</c>); the site state channel mirrors the flip.</item>
        ///   <item><c>AncientGuardianGuardAbility</c> — tags a site with its guardian
        ///     (<c>GeoSite.GameTags.Add</c>); the site state channel mirrors the tag.</item>
        /// </list></summary>
        public static readonly string[] RelayableAbilityTypeNames =
        {
            "HarvestFromSiteAbility",
            "ExcavateAbility",
            "EmergencyRepairAbility",
            "ScanAbility",
            "AncientSiteProbeAbility",
            "ActivateBaseAbility",
            "AncientGuardianGuardAbility",
        };

        private static readonly HashSet<string> _relayable =
            new HashSet<string>(RelayableAbilityTypeNames, StringComparer.Ordinal);

        /// <summary>True when a <c>GeoAbility</c> whose runtime type name is <paramref name="abilityTypeName"/>
        /// should ride the generic client→host relay (client suppress + send intent → host authoritative
        /// Activate). Unknown / off-list types return false — their native <c>Activate</c> runs unchanged (host /
        /// single-player) or their own dedicated sync path handles them.</summary>
        public static bool IsRelayable(string abilityTypeName)
            => !string.IsNullOrEmpty(abilityTypeName) && _relayable.Contains(abilityTypeName);
    }
}
