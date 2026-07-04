using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Inc Vision — CLIENT targeting/selection gate (invis-targeting desync fix). Postfix on
    /// <c>TacticalAbility.TargetFilterPredicate(TacticalTargetData, TacticalActorBase source, Vector3,
    /// TacticalActorBase target, Vector3)</c> (TacticalAbility.cs:803) — the single chokepoint every actor-target
    /// enumeration funnels through (<c>GetTargetActors</c> TacticalAbility.cs:594, which also drives auto-target,
    /// the RED/GREY markers and <c>HasValidTargets</c>; <c>ShootAbility</c> uses the base impl, the
    /// ApplyEffect/ApplyStatus overrides call <c>base</c>). The native gate consults faction vision only when the
    /// weapon's <c>FactionVisibility != Ignore</c> and otherwise relies on a GEOMETRIC LOS raycast against the
    /// LOCAL map — where a host-forgotten / invisible enemy is still physically present. So on a mirroring client
    /// an enemy the host has lost vision of (already dropped from the mirrored <c>KnownActors</c> by
    /// <see cref="TacticalVisionSync"/>.ForgetActor → <c>IsRevealed</c>/<c>IsLocated</c> == false) stays
    /// selectable/targetable/shootable → desync / a bad-shot window.
    ///
    /// This postfix forces the targetable set to track the mirrored host vision: when
    /// <see cref="TacticalDeploySync.IsClientMirroring"/>, drop an ENEMY target of a player-faction source that the
    /// player faction's mirrored <c>Vision</c> no longer knows (neither Located nor Revealed). The pure decision
    /// lives in <see cref="ClientVisionTargetGate.ShouldBlockTarget"/>. Host / single-player are no-op (gated by
    /// <c>IsClientMirroring</c>, byte-identical native behaviour); friendly / neutral / self targets and
    /// still-known enemies are untouched. Auto-registers via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class TargetVisionGatePatch
    {
        private static MethodBase _target;
        private static MethodInfo _relationTo;   // TacticalActorBase.RelationTo(TacticalActorBase) — exact overload

        public static bool Prepare()
        {
            var ability = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            if (ability == null) return false;
            // protected virtual bool TargetFilterPredicate(TacticalTargetData, TacticalActorBase, Vector3, TacticalActorBase, Vector3)
            _target = AccessTools.Method(ability, "TargetFilterPredicate");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __1 = sourceActor, __3 = targetActor (positional; kept object-typed so this patch never binds game types).
        public static void Postfix(ref bool __result, object __1, object __3)
        {
            try
            {
                if (!__result) return;                              // native already rejected this candidate
                if (!TacticalDeploySync.IsClientMirroring) return;  // host / single-player → never gate
                if (__1 == null || __3 == null) return;

                object faction = GetProp(__1, "TacticalFaction");
                if (faction == null) return;
                bool sourceIsPlayerFaction = ToBool(GetProp(faction, "IsControlledByPlayer"));
                bool targetIsEnemy = RelationIsEnemy(__1, __3);
                object vision = GetProp(faction, "Vision");
                bool hostKnowsTarget = vision != null &&
                    (InvokeBool(vision, "IsRevealed", __3) || InvokeBool(vision, "IsLocated", __3));

                if (ClientVisionTargetGate.ShouldBlockTarget(
                        TacticalDeploySync.IsClientMirroring, sourceIsPlayerFaction, targetIsEnemy, hostKnowsTarget))
                    __result = false;
            }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] TargetVisionGatePatch.Postfix failed: " + ex); }
        }

        // ─── reflection helpers (mirror TacticalVisionSync's style) ───
        private static bool RelationIsEnemy(object sourceActor, object targetActor)
        {
            if (_relationTo == null)
            {
                var tacBase = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActorBase");
                // Two overloads exist (TacticalFaction / TacticalActorBase) — bind the actor one explicitly.
                _relationTo = tacBase != null ? AccessTools.Method(tacBase, "RelationTo", new[] { tacBase }) : null;
            }
            if (_relationTo == null) return false;
            object rel = _relationTo.Invoke(sourceActor, new[] { targetActor });
            return rel != null && rel.ToString() == "Enemy";   // FactionRelation.Enemy
        }

        private static bool InvokeBool(object obj, string method, object arg)
        {
            var m = AccessTools.Method(obj.GetType(), method);
            if (m == null) return false;
            object r = m.Invoke(obj, new[] { arg });
            return r is bool b && b;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static bool ToBool(object o) => o is bool b && b;
    }
}
