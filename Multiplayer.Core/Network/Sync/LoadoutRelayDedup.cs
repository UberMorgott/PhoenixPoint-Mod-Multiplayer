using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// PURE per-unit content dedup for the client SetItems equip relay (BCL-only → unit-testable).
    /// While a co-op CLIENT has the EditSoldier screen open, native
    /// <c>UIStateEditSoldier.UpdateState</c> re-flushes the open soldier's WHOLE loadout through
    /// <c>GeoCharacter.SetItems</c> every frame (its <c>_uiRefreshNeeded</c> stays armed) — so the
    /// <c>SetItemsEditRelayPatch</c> prefix, which suppresses the local write and re-emits it as an
    /// <see cref="Actions.EquipSoldierAction"/> intent, would fire an identical intent ~60×/second (the
    /// measured FPS-collapse storm, 2-instance session 2026-07-08).
    ///
    /// This dedup keys the last-RELAYED loadout by <c>GeoUnitId</c> → an ordered
    /// <c>(armour | equipment | inventory)</c> def-guid signature: a genuine edit changes the signature
    /// and relays immediately; an unchanged per-frame re-flush is suppressed with NO send. Order-sensitive
    /// (a slot reorder is a real edit). Keyed per unit so soldier A's cache never masks soldier B; a stale
    /// entry after a session boundary is self-healing (its worst case suppresses a redundant no-op SetItems
    /// that already matches the model) and <see cref="Reset"/> drops all entries on a new session.
    ///
    /// GRANULARITY INVARIANT (review 2026-07-08): the signature is DEFINITION-level (per-slot ordered
    /// <c>BaseDef.Guid</c>s) — exactly the information the relayed <c>EquipSoldierAction</c> wire carries
    /// (i64 unitId + three guid lists, nothing else; EquipSoldierAction.cs "Item fidelity is def-level").
    /// The host apply rebuilds fresh items from those guids (<c>new GeoItem(def)</c>, freeReload:true), so
    /// EQUAL SIGNATURE ⇒ BYTE-IDENTICAL INTENT ⇒ IDENTICAL HOST RESULT: suppression can never lose
    /// per-instance state (ammo/charges) because the intent never carries it — that state converges via
    /// the authoritative #9 blob by design. Swapping two same-def item instances therefore dedups as a
    /// no-op (correct: the re-relayed action would be the same bytes).
    /// </summary>
    public sealed class LoadoutRelayDedup
    {
        private readonly Dictionary<long, string> _lastByUnit = new Dictionary<long, string>();

        /// <summary>Ordered per-slot def-guid signature; null and empty slot lists are both honest
        /// (a null arg the caller pre-filled from the current list yields the same bytes as that list).</summary>
        public static string Signature(string[] armour, string[] equipment, string[] inventory)
        {
            var sb = new StringBuilder();
            Append(sb, armour);
            sb.Append('|');
            Append(sb, equipment);
            sb.Append('|');
            Append(sb, inventory);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string[] guids)
        {
            if (guids == null) return;
            for (int i = 0; i < guids.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(guids[i]);
            }
        }

        /// <summary>True → relay this loadout (first sight of the unit, or changed since its last relay) and
        /// remember it; false → byte-identical to the last relayed loadout for this unit, so suppress the
        /// redundant per-frame re-emit.</summary>
        public bool ShouldRelay(long unitId, string signature)
        {
            if (_lastByUnit.TryGetValue(unitId, out var prev) && prev == signature) return false;
            _lastByUnit[unitId] = signature;
            return true;
        }

        /// <summary>Drop every remembered signature (new session / teardown).</summary>
        public void Reset() => _lastByUnit.Clear();
    }
}
