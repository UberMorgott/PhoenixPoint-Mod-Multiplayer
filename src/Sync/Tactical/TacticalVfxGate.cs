namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// Pure policy for the TS7 AoE/explosion VFX replay (<c>tac.vfx</c> 0x98): the host broadcasts a blast VFX
    /// ONLY for a REAL effect application, never for a damage-PREDICTION simulation pass (the native
    /// ExplosionEffect/VolumeEffect <c>SpawnObject</c> chokepoint is entered during simulation too but early-returns
    /// without drawing). Unity-free so it is unit-tested; the engine-side spawn + replay live in
    /// <see cref="TacticalVfxSync"/> and the host patch.
    /// </summary>
    public static class TacticalVfxGate
    {
        /// <summary>TRUE when the host should broadcast the VFX: only when this is NOT a simulation/prediction pass.</summary>
        public static bool ShouldBroadcastVfx(bool isSimulation) => !isSimulation;
    }
}
