using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// HOST dirty-trigger for the DiplomacyChannel (#4): an INSTANT re-mark on any faction-reputation change, so a
    /// pure <c>ModifyDiplomacy</c> delta (e.g. the steal-aircraft reputation penalty) mirrors to clients within a
    /// frame instead of lagging up to the hourly #4 heartbeat. Models the <see cref="ResearchDirtyGate"/> pattern:
    /// one reflective HOST postfix on the game's single diplomacy-mutation choke, re-marking ch#4 dirty; the
    /// existing snapshot engine coalesces the flag into ONE send. NO new message, NO new wire format — the value
    /// mirror (<see cref="Multiplayer.Network.Sync.State.DiplomacyChannel"/>) is unchanged.
    ///
    /// THE SINGLE FUNNEL. Every reputation write in the game — <c>PartyDiplomacy.ModifyDiplomacy</c> (both
    /// overloads), <c>SetDiplomacy</c>, and the initial <c>StartRelations</c> seed — routes through the nested
    /// <c>PartyDiplomacy.Relation.Diplomacy</c> setter (PartyDiplomacy.cs:43-49; it clamps then fires
    /// <c>OnDiplomacyChanged</c>). Patching that ONE setter catches them all (converge, don't multiply). It is the
    /// int-delta trigger the channel's existing <c>OnFactionDiplomacyStateChanged</c> subscription does NOT cover:
    /// that event fires only on a forced STATE flip (war/alliance threshold), never on a sub-threshold delta.
    ///
    /// Host-only + active session + not-applying (<see cref="ResearchDirtyGate"/>-equivalent gate): a client never
    /// marks (its diplomacy is a pure value mirror), and an engine replay never re-marks. Reputation writes are
    /// event-driven and rare (rewards / missions / daily drift), and the flag coalesces per flush, so marking on
    /// every relation write is cheap. Reflective target (Prepare false → PatchAll skips) so an engine rename only
    /// costs this instant trigger — the hourly heartbeat stays a belt.
    /// </summary>
    [HarmonyPatch]
    public static class DiplomacyRelationDirtyPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            // Nested type: PartyDiplomacy.Relation (the per-relation reputation holder).
            var relationType = AccessTools.TypeByName("PhoenixPoint.Common.Core.PartyDiplomacy+Relation");
            if (relationType == null) return false;
            _target = AccessTools.PropertySetter(relationType, "Diplomacy");   // internal set — AccessTools finds it
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static void Postfix()
        {
            try
            {
                if (SyncApplyScope.IsApplying) return;                 // engine replay → never re-mark
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                engine.Sync?.MarkChannelDirty(4);                      // DiplomacyChannel.ChannelId
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer] DiplomacyRelationDirtyPatch failed: " + ex.Message); }
        }
    }
}
