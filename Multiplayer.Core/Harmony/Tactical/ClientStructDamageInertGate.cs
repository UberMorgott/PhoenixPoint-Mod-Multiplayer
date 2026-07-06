namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the TS6 struct-damage apply seam (<see cref="Multiplayer.Sync.Tactical.TacticalStructDamageSync"/>).
    /// Extracted so the "no double actor damage" contract is unit-testable without NetworkEngine or game types.
    ///
    /// When a co-op CLIENT re-applies the host's structural damage, a <c>Breakable</c> prop that carries an
    /// <c>ExplodeEffectDef</c> (an explosive barrel / fuel tank) would, inside its native <c>Explode()</c>, run that
    /// chain effect and deal AoE damage to nearby ACTORS and chain-destroy nearby props. But the host already ran and
    /// BROADCAST all of those consequences — actor damage rides tac.damage (0x88) / the 0x8F delta, and each chained
    /// destructible rides its OWN 0x96 hit. Re-running the chain on the client would DOUBLE-apply. So on a client
    /// mirror the explosion chain is neutered (the client keeps only the visual break + native geometry/nav update).
    /// Off-client / host / single-player → false → run native unchanged (byte-identical to vanilla).
    /// </summary>
    public static class ClientStructDamageInertGate
    {
        /// <summary>True → NEUTER the explosive-Breakable chain effect during a client struct-damage replay (host
        /// owns collapse/chain actor damage). False → run native (host / single-player / no session).</summary>
        public static bool ShouldNeuterExplosionChain(bool isClientMirroring) => isClientMirroring;
    }
}
