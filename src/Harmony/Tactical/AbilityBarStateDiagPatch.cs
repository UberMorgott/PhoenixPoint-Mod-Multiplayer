using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multipleer.Network;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
{
    /// <summary>
    /// HOST-only DIAGNOSTIC (no behaviour change). The host shows the SAME stale ability-bar symptom as the client
    /// for CLIENT-controlled soldiers: the host executes a relayed client action programmatically, so its own
    /// tactical view never re-cycles <c>UIStateCharacterSelected</c> → <c>UIModuleAbilities.SetAbilities</c> never
    /// re-runs → <c>AbilityButtonController.SetButton</c> never re-stamps <c>Button.IsEnabled</c> against the spent
    /// AP. The exact host hook that should re-cycle is not yet located, so this postfix instruments the ground truth:
    /// EVERY time the host's ability bar IS rebuilt (<c>SetAbilities(TacticalActor, InputController)</c>), it logs the
    /// actor NetId, its current AP, and per-ability enabled state. Comparing when this fires (and with what AP) vs.
    /// when the host UI looks stale pinpoints the missing host-side re-cycle for a follow-up fix.
    ///
    /// Gated to a live co-op HOST (<c>NetworkEngine.Instance.IsActive &amp;&amp; IsHost</c>) so single-player and the
    /// client are never spammed. Fully fail-open. Signature ground in the fresh decompile
    /// (UIModuleAbilities.cs:112 — <c>public void SetAbilities(TacticalActor actor, InputController input)</c>);
    /// AccessTools 2-param exact match avoids an overload miss.
    /// </summary>
    [HarmonyPatch]
    public static class AbilityBarStateDiagPatch
    {
        private static MethodBase _target;
        private static MethodInfo _getAbilities;   // TacticalActor.GetAbilities<TacticalAbility>() (closed)

        public static bool Prepare()
        {
            var module = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewModules.UIModuleAbilities");
            var actor = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.TacticalActor");
            var input = AccessTools.TypeByName("Base.Input.InputController");
            if (module == null || actor == null || input == null) return false;

            // public void SetAbilities(TacticalActor actor, InputController input) — 2-param EXACT match.
            _target = AccessTools.Method(module, "SetAbilities", new[] { actor, input });
            if (_target == null) return false;

            // Closed GetAbilities<TacticalAbility>() (zero-arg generic on ActorComponent) for per-ability logging.
            var tacAbility = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            if (tacAbility != null)
            {
                foreach (var m in actor.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "GetAbilities" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    {
                        try { _getAbilities = m.MakeGenericMethod(tacAbility); } catch { _getAbilities = null; }
                        break;
                    }
                }
            }
            return true;
        }

        public static MethodBase TargetMethod() => _target;

        // Signature: void SetAbilities(TacticalActor actor, InputController input)
        public static void Postfix(object __0 /*actor*/)
        {
            try
            {
                var e = NetworkEngine.Instance;
                if (e == null || !e.IsActive || !e.IsHost) return;   // co-op host ONLY (SP / client never spammed)
                if (__0 == null) return;

                int netId = TacticalDeploySync.NetIdForLiveActor(__0);
                float ap = ReadActionPoints(__0);

                var sb = new StringBuilder();
                sb.Append("[Multipleer][tac] HOST ability-bar rebuilt net=").Append(netId)
                  .Append(" ap=").Append(ap.ToString("0.##")).Append(" [");
                if (_getAbilities != null)
                {
                    if (_getAbilities.Invoke(__0, null) is IEnumerable abilities)
                    {
                        bool first = true;
                        foreach (var ab in abilities)
                        {
                            if (ab == null) continue;
                            bool enabled = AbilityIsEnabled(ab);
                            if (!first) sb.Append(", ");
                            sb.Append(ab.GetType().Name).Append('=').Append(enabled ? "on" : "off");
                            first = false;
                        }
                    }
                }
                sb.Append(']');
                Debug.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] AbilityBarStateDiagPatch.Postfix failed: " + ex);
            }
        }

        /// <summary>actor.CharacterStats.ActionPoints read via the same op_Implicit(float) path the state sync uses.</summary>
        private static float ReadActionPoints(object actor)
        {
            object stats = Prop(actor, "CharacterStats");
            object apStat = Field(stats, "ActionPoints");
            if (apStat == null) return 0f;
            try
            {
                var op = AccessTools.Method(apStat.GetType(), "op_Implicit", new[] { apStat.GetType() });
                if (op != null) return Convert.ToSingle(op.Invoke(null, new[] { apStat }));
            }
            catch { }
            try { return Convert.ToSingle(Prop(apStat, "IntValue") ?? 0); } catch { return 0f; }
        }

        /// <summary>Ability.IsEnabled(IAbilityDisabledStatesFilter filter = null) — invoked with a null filter.</summary>
        private static bool AbilityIsEnabled(object ability)
        {
            try
            {
                var m = AccessTools.Method(ability.GetType(), "IsEnabled");
                if (m != null) return Convert.ToBoolean(m.Invoke(ability, new object[] { null }));
            }
            catch { }
            return false;
        }

        private static object Prop(object obj, string name)
        {
            if (obj == null) return null;
            var p = AccessTools.Property(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            var f = AccessTools.Field(obj.GetType(), name);
            return f?.GetValue(obj);
        }

        private static object Field(object obj, string name)
        {
            if (obj == null) return null;
            var f = AccessTools.Field(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(obj.GetType(), name);
            return p != null ? p.GetValue(obj, null) : null;
        }
    }
}
