using System;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PS2 whole-<c>GeoCharacter</c> ⇄ bytes bridge for the personnel channel (#9). Thin wrapper over the
    /// tac-sync serializer round-trip (<see cref="TacticalDeploySync.SerializeGraph"/> /
    /// <see cref="TacticalDeploySync.DeserializeGraph"/>) — the ONE in-game-proven path through the
    /// game's configured Serializer (<c>GameUtl.GameComponent&lt;SerializationComponent&gt;().Serializer</c>,
    /// NEVER <c>new Serializer(null)</c> — null Context NREs in BaseDef.ResolveOrCreateBaseDef → silently
    /// empty graph; memory <c>pp-serializer-context-and-pump</c>). The serializer coroutine is pumped by
    /// <c>Timing.RunUntilComplete</c>, which spins its OWN <c>new Timing()</c> + <c>TimingScheduler</c>
    /// (Timing.cs:285-296) — fully independent of the frozen client's paused level Timing, so Read works
    /// on the pure-mirror geoscape without any external <c>IUpdateable</c> driver (spec risk R2).
    /// quiet=true: per-soldier round-trips skip the tac probes + host self-roundtrip (hourly-flush spam).
    /// All failures return null — the caller degrades to notify (keep old state), never throws.
    /// </summary>
    public static class PersonnelBlob
    {
        private static Type _geoCharacterType;
        private static Type _geoUnitDescriptorType;

        /// <summary>HOST: one [SerializeType] root (GeoCharacter for #9, GeoUnitDescriptor for #10) →
        /// game-Serializer graph bytes, or null (caller skips/logs). Type-agnostic by construction.</summary>
        public static byte[] Write(object geoCharacter)
        {
            if (geoCharacter == null) return null;
            try
            {
                return TacticalDeploySync.SerializeGraph(new object[] { geoCharacter }, quiet: true);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PersonnelBlob.Write failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>CLIENT: blob bytes → a transient fully-deserialized <c>GeoCharacter</c> (its PostRead
        /// <c>InitAfterDeserialiaztion</c> already ran inside the pump), or null on any failure.</summary>
        public static object Read(byte[] blob)
        {
            if (blob == null || blob.Length == 0) return null;
            try
            {
                if (_geoCharacterType == null)
                    _geoCharacterType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoCharacter");
                return TacticalDeploySync.DeserializeGraph(blob, _geoCharacterType, quiet: true);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PersonnelBlob.Read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>CLIENT (#10): blob bytes → a transient <c>GeoUnitDescriptor</c> (the [SerializeType]
        /// recruit/capture snapshot — save-proven inside ExtendedInstanceData), or null on any failure.</summary>
        public static object ReadDescriptor(byte[] blob)
        {
            if (blob == null || blob.Length == 0) return null;
            try
            {
                if (_geoUnitDescriptorType == null)
                    _geoUnitDescriptorType = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.GeoUnitDescriptor");
                return TacticalDeploySync.DeserializeGraph(blob, _geoUnitDescriptorType, quiet: true);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] PersonnelBlob.ReadDescriptor failed: " + ex.Message);
                return null;
            }
        }
    }
}
