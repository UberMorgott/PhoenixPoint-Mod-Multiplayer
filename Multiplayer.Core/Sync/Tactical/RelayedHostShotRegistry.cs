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
        /// <summary>One in-flight relayed attack (shoot OR melee — the full 0x87 relayable set). Beyond the
        /// B1/B2 keys it carries the intent's TARGET netId + whether ANY effect landed on that target during
        /// the window (damage of any form — HP/paralysis/viral — via the ApplyDamage funnel, or a status via
        /// the CallApplyStatus funnel) — read at <c>End</c> by the miss-feedback broadcast (tac.shot.result):
        /// TargetNetId ≥ 0 and EffectSeen=false = an authoritative MISS the origin client's damage-neutered
        /// presentation cannot show. CosmeticStrips scopes B1/B2 to SHOOT windows only: melee (BashAbility)
        /// already animates inline via BashCrt and must never see a forced CurrentlyAiming.</summary>
        public sealed class Shot
        {
            public object Shooter;
            public int TargetNetId;
            public bool EffectSeen;
            public bool CosmeticStrips;
        }

        // ability instance -> its in-flight shot entry. Identity comparer so overridden Equals/GetHashCode on
        // Unity-derived game objects can never collapse two distinct instances (or alias a fake-null).
        private readonly Dictionary<object, Shot> _byAbility =
            new Dictionary<object, Shot>(RefIdentityComparer.Instance);

        /// <summary>Number of in-flight relayed shots (diag / tests).</summary>
        public int Count => _byAbility.Count;

        /// <summary>Register a relayed attack: the host is about to <c>Activate</c> <paramref name="ability"/>
        /// for <paramref name="shooter"/> at the intent's target actor (<paramref name="targetNetId"/>, -1 for a
        /// bare-position target — no miss feedback for those). <paramref name="cosmeticStrips"/> = true for a
        /// SHOOT window (B1/B2 apply), false for melee (verdict tracking only). No-op if ability/shooter is
        /// null. Overwrites a leaked prior entry for the same ability (self-heal).</summary>
        public void Begin(object ability, object shooter, int targetNetId = -1, bool cosmeticStrips = true)
        {
            if (ability == null || shooter == null) return;
            _byAbility[ability] = new Shot { Shooter = shooter, TargetNetId = targetNetId, CosmeticStrips = cosmeticStrips };
        }

        /// <summary>B1: is this exact ability instance an in-flight relayed shoot (→ run its action inline)?
        /// False for a melee (verdict-only) entry — melee already runs inline natively.</summary>
        public bool IsAbilityActive(object ability)
            => ability != null && _byAbility.TryGetValue(ability, out var shot) && shot.CosmeticStrips;

        /// <summary>B2: is this actor the shooter of an in-flight relayed shoot (→ skip the aim-up wait)?
        /// False for a melee (verdict-only) entry — a melee attacker must never see a forced CurrentlyAiming.</summary>
        public bool IsActorActive(object actor)
        {
            if (actor == null) return false;
            foreach (var kv in _byAbility)
                if (kv.Value.CosmeticStrips && ReferenceEquals(kv.Value.Shooter, actor)) return true;
            return false;
        }

        /// <summary>Miss feedback: the host just applied SOME effect (any damage form via the ApplyDamage
        /// funnel, or a status via CallApplyStatus) to <paramref name="targetNetId"/> — flag every in-flight
        /// attack aimed at it as a HIT (no tac.shot.result at its End). Window-scoped: only attacks currently
        /// registered can match.</summary>
        public void MarkEffect(int targetNetId)
        {
            if (targetNetId < 0) return;
            foreach (var kv in _byAbility)
                if (kv.Value.TargetNetId == targetNetId) kv.Value.EffectSeen = true;
        }

        /// <summary>Remove an ability's entry at its shot end and return it (null if absent / null) so the
        /// caller can read TargetNetId/EffectSeen for the miss-feedback broadcast.</summary>
        public Shot End(object ability)
        {
            if (ability == null) return null;
            if (!_byAbility.TryGetValue(ability, out var shot)) return null;
            _byAbility.Remove(ability);
            return shot;
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
