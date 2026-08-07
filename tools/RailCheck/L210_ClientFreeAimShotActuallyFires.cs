using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Entities.Abilities;
using Multiplayer.Tactical;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>A CLIENT'S FREE-AIM SHOT ACTUALLY FIRES — the outcome, not the call.
    ///
    /// L169 removed the grid sweep from OUR target-choice gate and left the game's own
    /// <c>GetDisabledState()</c> standing. It was GREEN through this bug and could not have caught it,
    /// because arm (a) hands <see cref="TacticalCommandSync.Validate"/> a <c>null</c>
    /// <c>abilityDisabledReason</c> — it STUBS OUT the very arm that was doing the refusing. Worse, its
    /// stub is self-contradictory with the case it models: it says the ability "published a list that
    /// matched nothing", but a shoot whose sweep publishes anything has
    /// <c>ShootAbility.HasValidTargets</c>:41 TRUE and never reaches <c>NoValidTarget</c> at all. The real
    /// free-aim shot has an EMPTY sweep, which is exactly the state that trips
    /// <c>TacticalAbility.GetDisabledStateInternal</c>:464-466 — so on the live wire the host answered the
    /// intent with "Нет подходящей цели" and L169's arm never saw it. Five refusals across two peers and
    /// four soldiers, 2026-08-08 01:53:12-01:54:19; the host's own free-aim shot at 01:53:33 logged the
    /// SAME disabled state at its confirm and fired for 76 damage, because a host click takes the
    /// RelayMirror branch and never reaches Validate.
    ///
    /// The engine never asks the unfiltered question when it confirms a shoot:
    /// <c>UIStateShoot.ConfirmShoot</c>:1295 and <c>UIStateFreeCam.ConfirmShoot</c>:466 both gate on
    /// <c>IsEnabled(IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter)</c>. Arm (d) reads that
    /// out of the shipped IL rather than trusting this paragraph, so the day the engine stops waiving it
    /// the law says so instead of quietly protecting a rule nobody else follows.
    ///
    /// TO TURN IT RED: in <c>TacticalCommandSync.HostGateFilter</c> return <c>null</c> unconditionally
    /// → <c>L210 free-aim-shot-never-fires</c> + <c>L210 shoot-still-answers-to-an-empty-sweep</c>.
    /// Return <c>IgnoreNoValidTargetsFilter</c> unconditionally → <c>L210 waiver-is-not-narrowed-to-a-shoot</c>.
    /// Widen it to <c>IgnoreNoValidTargetsEquipmentNotSelectedAndNotEnoughActionPoints</c> →
    /// <c>L210 waiver-covers-more-than-the-engine-waives</c>. Revert the call site at
    /// <c>TacticalCommandSync</c>:2118 to the unfiltered <c>GetDisabledState()</c> →
    /// <c>L210 waiver-is-decorative</c>.
    /// </summary>
    internal static class L210_ClientFreeAimShotActuallyFires
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var filter = cmd.GetMethod("HostGateFilter", All);
            var validate = cmd.GetMethod("Validate", All);
            var choice = cmd.GetMethod("ChoiceIsOffered", All);
            if (filter == null || validate == null || choice == null)
            {
                yield return "L210 premise-changed: TacticalCommandSync.{HostGateFilter,Validate," +
                             "ChoiceIsOffered} no longer resolves. Nothing else stops the host refusing a " +
                             "client's free-aim shot on an enumeration the engine's own confirm is told to " +
                             "ignore — re-read this law before assuming something does.";
                yield break;
            }

            // THE ARM WE ARE WAIVING MUST STILL BE THE ARM THAT REFUSES. If ShootAbility stops deriving its
            // validity from the sweep, the waiver is protecting nothing and this law is theatre.
            var hasValid = typeof(ShootAbility).GetProperty("HasValidTargets", All);
            if (hasValid == null || hasValid.GetGetMethod(true) == null ||
                hasValid.GetGetMethod(true).DeclaringType != typeof(ShootAbility))
            {
                yield return "L210 premise-changed: ShootAbility no longer overrides HasValidTargets, so " +
                             "GetDisabledStateInternal:464-466 is deciding NoValidTarget on something other " +
                             "than the grid sweep. The waiver was written against that sweep and must be " +
                             "re-read against whatever replaced it.";
                yield break;
            }

            var waiver = TacticalCommandSync.HostGateFilter(true);

            // ── (a) THE OUTCOME: a client's free-aim shot ENDS WITH THE ABILITY EXECUTING ──
            // The intent exactly as it arrived on the wire: actor alive, player-controlled, its faction
            // playing, not busy, AP and WP paid, and the aim point a raw cursor position. The sweep is EMPTY
            // — that is what makes HasValidTargets false — so the choice gate has no list to hold it to.
            bool offered = TacticalCommandSync.ChoiceIsOffered(true, false, false);
            // NEVER STRINGIFY AN AbilityDisabledState HERE. It is a CLASS, not an enum, and its ToString()
            // resolves a localisation key through GameUtl.Game() — outside a running game that throws
            // SecurityException, which the harness reports as a CRASH rather than as this law's own red line.
            // It matters because the throw only happens on the FAILING branch: the law would be green while the
            // waiver worked and would ABORT THE WHOLE RUN the moment it regressed, so arm (a) could never be
            // seen red. Verified 2026-08-08 by falsifying it: "GameUtl HARNESS-CRASH ... ECall methods must be
            // packaged into a system module", stack at AbilityDisabledState.get_MessagesByKeyDef. The literal
            // is only a diagnostic string — Validate branches on IsNullOrEmpty, never on its contents.
            string reason = waiver != null && waiver.IsStateIgnored(AbilityDisabledState.NoValidTarget)
                            ? null : "NoValidTarget";
            var verdict = TacticalCommandSync.Validate(true, true, true, true, true, true, false, reason,
                                                       offered, 4f, 1f, 10f, 0f);
            if (verdict != null)
                yield return "L210 free-aim-shot-never-fires: a client switched to free-aim, aimed and " +
                             "clicked; the intent left the client and the host refused it with \"" + verdict +
                             "\". The host then never runs the shot, the reject forces a settle, and " +
                             "CancelActions tears the wind-up out mid-animation — the player sees a soldier " +
                             "who simply does not shoot, while the same shot from the host's own hand fires.";
            if (!offered)
                yield return "L210 outcome-is-vacuous: the choice gate is refusing an empty publication " +
                             "again, so arm (a) would stay red for a reason that has nothing to do with the " +
                             "disabled-state waiver and green-ness here would mean nothing.";

            // ── (b) the waiver is the engine's, in both directions ──
            if (waiver == null || !waiver.IsStateIgnored(AbilityDisabledState.NoValidTarget))
                yield return "L210 shoot-still-answers-to-an-empty-sweep: the host asks the unfiltered " +
                             "question for a ShootAbility. That is the reported bug verbatim — the sweep a " +
                             "free cursor point is not drawn from is refusing the order again.";
            if (TacticalCommandSync.HostGateFilter(false) != null)
                yield return "L210 waiver-is-not-narrowed-to-a-shoot: a NON-shoot ability is being handed the " +
                             "waiver too. UIStateAbilitySelected.AbilityConfirmed:613 waives nothing and " +
                             "UIStateOverwatchAbilitySelected:302 asks the plain question, so this hands every " +
                             "targetless ability — an ExitVehicle, a heal with nobody in reach — past the " +
                             "game's own gate. That is a wider hole than the one being closed.";

            // ── (c) POSITIVE CONTROL: exactly one state is waived, and it is not the expensive ones ──
            // Names carried as literals for the same reason as `reason` above: concatenating the state itself
            // calls its GameUtl-backed ToString() and turns this arm's RED into a harness crash.
            var risky = new[] { AbilityDisabledState.NotEnoughActionPoints,
                                AbilityDisabledState.NotEnoughWillPoints,
                                AbilityDisabledState.EquipmentNotSelected,
                                AbilityDisabledState.OffMap,
                                AbilityDisabledState.ActorStunned,
                                AbilityDisabledState.NoSuitableEquipment };
            var riskyNames = new[] { "NotEnoughActionPoints", "NotEnoughWillPoints", "EquipmentNotSelected",
                                     "OffMap", "ActorStunned", "NoSuitableEquipment" };
            for (int i = 0; i < risky.Length; i++)
                if (waiver != null && waiver.IsStateIgnored(risky[i]))
                    yield return "L210 waiver-covers-more-than-the-engine-waives: " + riskyNames[i] + " is being " +
                                 "waived on the host. IgnoreNoValidTargetsFilter waives ONE state; anything " +
                                 "else here lets a client fire a broken weapon, fire off-map, fire while " +
                                 "stunned or fire without paying, and the host is the only place that checks.";

            // ── (d) THE ENGINE'S OWN STATEMENT, READ OUT OF THE SHIPPED IL, not out of this comment ──
            var ignore = typeof(IgnoredAbilityDisabledStatesFilter)
                         .GetField("IgnoreNoValidTargetsFilter", BindingFlags.Public | BindingFlags.Static);
            if (ignore == null)
            {
                yield return "L210 premise-changed: IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter " +
                             "is gone from this build. The host is mirroring a waiver the game no longer has.";
                yield break;
            }
            foreach (var name in new[] { "PhoenixPoint.Tactical.View.ViewStates.UIStateShoot",
                                         "PhoenixPoint.Tactical.View.ViewStates.UIStateFreeCam" })
            {
                var t = typeof(ShootAbility).Assembly.GetType(name);
                var confirm = t == null ? null : t.GetMethod("ConfirmShoot", All | BindingFlags.DeclaredOnly,
                                                             null, Type.EmptyTypes, null);
                if (confirm == null) continue;   // UIStateFreeCam may stop overriding; the base still speaks
                if (!LoadsField(confirm, ignore))
                    yield return "L210 engine-no-longer-waives-the-sweep: " + name + ".ConfirmShoot no longer " +
                                 "reads IgnoreNoValidTargetsFilter. The whole justification for the host " +
                                 "waiving NoValidTarget is that the engine waives it at the very same confirm; " +
                                 "with that gone the waiver is our invention and must be re-derived.";
            }

            // ── (e) the waiver is actually WIRED, not merely declared ──
            if (!CallsHostGateFilter(cmd.GetMethod("HandleActivate", All)) &&
                !cmd.GetMethods(All).Any(CallsHostGateFilter))
                yield return "L210 waiver-is-decorative: nothing in TacticalCommandSync calls HostGateFilter, " +
                             "so the host is back to the unfiltered GetDisabledState() and every arm above is " +
                             "green while the shot is refused on the wire. This is the shape L169 failed in.";
        }

        /// <summary>Naive scan for a <c>ldsfld</c>/<c>ldsflda</c> token naming a given field — the same shape
        /// L100 uses. Naive is fine: a false token can only make the law MORE lenient, never red.</summary>
        private static bool LoadsField(MethodBase m, FieldInfo want)
        {
            foreach (int token in TokensAfter(m, 0x7E, 0x7F))
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(token); } catch { }
                if (f != null && f.MetadataToken == want.MetadataToken &&
                    f.DeclaringType == want.DeclaringType) return true;
            }
            return false;
        }

        private static bool CallsHostGateFilter(MethodBase m)
        {
            if (m == null) return false;
            foreach (int token in TokensAfter(m, 0x28, 0x6F))
            {
                MethodBase c = null;
                try { c = m.Module.ResolveMethod(token); } catch { }
                if (c != null && c.Name == "HostGateFilter") return true;
            }
            return false;
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m == null ? null : m.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
