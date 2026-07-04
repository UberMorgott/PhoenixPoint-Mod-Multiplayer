using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) registry of CLIENT-ORIGIN relayed shots the HOST is currently executing
    /// authoritatively. It scopes two host-side COSMETIC-delay strips to relayed shots ONLY, so the host's
    /// OWN shots keep their full native cinematic (no network lag → no reason to strip):
    ///   • B1 (inline): while a shoot ability is registered, the host reroutes its long-range
    ///     <c>EnqueueAction(soloAfterCurrent)</c> + camera-blend defer to an immediate <c>PlayAction</c>
    ///     (the same inline branch overwatch / return-fire / point-blank shots already take).
    ///   • B2 (aim-skip): while the SHOOTER is registered, the host reports <c>CurrentlyAiming = true</c> so
    ///     <c>FireWeaponAtTargetCrt</c> SKIPS the standing aim-up wait (the same native path an already-aiming
    ///     overwatch reaction shot takes).
    /// The NATIVE damage roll (<c>ShootAndWaitRF</c> → <c>ApplyDamage</c>) and burst/grenade/overwatch logic
    /// are UNTOUCHED — only the host's pre-damage cosmetic scheduling is removed for relayed shots.
    ///
    /// Keyed by OBJECT IDENTITY (ability instance → its shooter actor instance). Registered synchronously in
    /// <c>TacticalCombatSync.HostOnAbilityIntent</c> BEFORE <c>Activate</c> (so the synchronous B1 branch sees
    /// it) and removed at the shot's <c>OnPlayingActionEnd</c> (which fires on hit / miss / fumble). Entries
    /// self-overwrite on re-register so a leaked clear self-heals on that actor's next relayed shot.
    /// </summary>
    public sealed class RelayedHostShotRegistry
    {
        // ability instance -> shooter actor instance. Identity comparer so overridden Equals/GetHashCode on
        // Unity-derived game objects can never collapse two distinct instances (or alias a fake-null).
        private readonly Dictionary<object, object> _byAbility =
            new Dictionary<object, object>(RefIdentityComparer.Instance);

        /// <summary>Number of in-flight relayed shots (diag / tests).</summary>
        public int Count => _byAbility.Count;

        /// <summary>Register a relayed shoot: the host is about to <c>Activate</c> <paramref name="ability"/>
        /// for <paramref name="shooter"/>. No-op if either is null. Overwrites a leaked prior entry for the
        /// same ability (self-heal).</summary>
        public void Begin(object ability, object shooter)
        {
            if (ability == null || shooter == null) return;
            _byAbility[ability] = shooter;
        }

        /// <summary>B1: is this exact ability instance an in-flight relayed shoot (→ run its action inline)?</summary>
        public bool IsAbilityActive(object ability)
            => ability != null && _byAbility.ContainsKey(ability);

        /// <summary>B2: is this actor the shooter of an in-flight relayed shoot (→ skip the aim-up wait)?</summary>
        public bool IsActorActive(object actor)
        {
            if (actor == null) return false;
            foreach (var kv in _byAbility)
                if (ReferenceEquals(kv.Value, actor)) return true;
            return false;
        }

        /// <summary>Remove an ability's entry at its shot end. No-op if absent / null.</summary>
        public void End(object ability)
        {
            if (ability != null) _byAbility.Remove(ability);
        }

        /// <summary>Drop all entries (session teardown / tests).</summary>
        public void Reset() => _byAbility.Clear();

        // Reference-identity comparer (net472 has no System...ReferenceEqualityComparer).
        private sealed class RefIdentityComparer : IEqualityComparer<object>
        {
            public static readonly RefIdentityComparer Instance = new RefIdentityComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
