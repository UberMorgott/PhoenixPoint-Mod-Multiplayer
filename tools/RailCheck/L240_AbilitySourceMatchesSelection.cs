using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Entities;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L240 — ONE DEF GUID IS NOT ONE ABILITY: THE INSTANCE THE RAIL RESOLVES IS THE SELECTED WEAPON'S.
    ///
    /// THE HOLE (2026-08-08 RCA). Both resolution sites asked
    /// <c>GetAbilityFiltered(a =&gt; a.AbilityDef.Guid == guid)</c>, and
    /// <c>ActorComponent.GetAbilityFiltered</c>:211-221 returns the FIRST match. But
    /// <c>Overwatch_AbilityDef</c> and <c>Reload_AbilityDef</c> mint ONE INSTANCE PER WEAPON
    /// (<c>OverwatchAbility.OverwatchWeapon =&gt; GetSource&lt;Weapon&gt;()</c>; <c>ReloadAbility</c>'s source is
    /// the <c>TacticalItem</c> it reloads), so the host always answered with the PRIMARY's instance whatever
    /// the peer was holding. Measured, client 3: <c>02:45:42.816 select → PX_Pistol</c> ACCEPTED (seq=259) →
    /// <c>02:45:43.965 command Reload_AbilityDef</c> → <c>.970 CLIENT weapon switch applied → PX_SniperRifle</c>
    /// → <c>.970 reject … Недостаточно свободных рук</c>, five times on a soldier whose broken arms could not
    /// hold that rifle. The sixth reload was accepted only because an inventory move had removed the rifle and
    /// first-match finally landed on the pistol.
    ///
    /// THE DISAMBIGUATOR IS THE GAME'S OWN AND IT IS THE SELECTION, not the def:
    /// <c>TacticalAbility.Activate</c>:1087-1090 selects the source equipment of the instance the player
    /// activated, and <c>GetDisabledStateInternal</c>:414-417/435 judges THAT instance's own equipment. So this
    /// law relaxes nothing — a weapon a broken arm cannot hold stays refused; it only stops the host answering
    /// a question about the pistol with the rifle's answer.
    ///
    /// BOTH SITES, and the second one is predicted rather than observed: a HOST-initiated pistol reload carries
    /// the same non-unique guid, so a mirror that still resolved by first match would replay it as the rifle's
    /// on every client — and <c>PreSelectSourceEquipment</c> would then publish that rifle over the acting
    /// peer's own selection, which is the visible "he swapped to the primary".
    ///
    /// WHAT THIS LAW ASSERTS.
    ///   (a) EXECUTED — <c>CandidateMatchesSelection</c>, the shipped pure disambiguator, run over two distinct
    ///       sources in all four polarities. Self-controlling: an implementation that always answers TRUE and
    ///       one that always answers FALSE both go red here.
    ///   (b) <c>ability-source-matches-selection</c> — the HOST-VALIDATE site resolves through
    ///       <c>ResolveAbility</c> and no longer reaches <c>GetAbilityFiltered</c> itself.
    ///   (c) <c>mirror-source-matches-selection</c> — the MIRROR-REPLAY site, the same.
    ///   (d) the helper really consults the selection and really uses the matcher (a resolver that reads
    ///       neither is first-match wearing a new name).
    ///   (e) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam"/> below inverts the rule and resolves both
    ///       sites by first match; every arm above must go red on it.
    ///
    /// Falsify: make <c>CandidateMatchesSelection</c> ignore <c>selected</c> → (a); revert either site to
    /// <c>actor.GetAbilityFiltered&lt;TacticalAbility&gt;(a =&gt; a.AbilityDef.Guid == guid)</c> → (b) / (c);
    /// stop <c>ResolveAbility</c> reading <c>SelectedEquipment</c> → (d); empty <see cref="FakeSeam"/> → (e).
    /// </summary>
    internal static class L240_AbilitySourceMatchesSelection
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalCommandSync);
            var resolve = sync.GetMethod("ResolveAbility", AllMembers);
            var hostSite = sync.GetMethod("HandleActivate", AllMembers);
            var mirrorSite = sync.GetMethod("ApplyActivate", AllMembers);
            var filtered = typeof(ActorComponent).GetMethod("GetAbilityFiltered",
                               BindingFlags.Public | BindingFlags.Instance);
            var selection = typeof(EquipmentComponent).GetProperty("SelectedEquipment",
                                BindingFlags.Public | BindingFlags.Instance);
            var source = typeof(TacticalAbility).GetProperty("EquipmentSource",
                             BindingFlags.Public | BindingFlags.Instance);
            var over = typeof(TacticalAbility).GetField("OverrideEquipment",
                           BindingFlags.Public | BindingFlags.Instance);

            if (resolve == null || hostSite == null || mirrorSite == null || filtered == null ||
                selection == null || source == null || over == null)
            {
                yield return "L240 premise-changed: one of TacticalCommandSync.{ResolveAbility, HandleActivate, " +
                             "ApplyActivate}, ActorComponent.GetAbilityFiltered, " +
                             "EquipmentComponent.SelectedEquipment or TacticalAbility.{EquipmentSource, " +
                             "OverrideEquipment} no longer resolves. Those five are the whole arc: the lookup " +
                             "that returns FIRST match, the selection that disambiguates it, and the pair the " +
                             "game's own gate reads to decide WHICH weapon it is judging.";
                yield break;
            }

            // ── (a) THE DISAMBIGUATOR, EXECUTED ──────────────────────────────
            foreach (var v in ScanRule(TacticalCommandSync.CandidateMatchesSelection, "CandidateMatchesSelection"))
                yield return v;

            // ── (b) + (c) BOTH RESOLUTION SITES ──────────────────────────────
            foreach (var v in ScanSite(hostSite, "ability-source-matches-selection", "HandleActivate",
                                       "the host would then GATE a peer's pistol reload on his unusable rifle " +
                                       "and PreSelectSourceEquipment would publish that rifle on the 0x82 " +
                                       "settle, over the selection the acting peer actually chose"))
                yield return v;
            foreach (var v in ScanSite(mirrorSite, "mirror-source-matches-selection", "ApplyActivate",
                                       "a HOST-initiated pistol reload would then replay as the RIFLE's on " +
                                       "every client — the same defect pointed the other way, and the half no " +
                                       "captured log has yet shown because the host never reloaded in them"))
                yield return v;

            // ── (d) THE HELPER IS NOT FIRST-MATCH WEARING A NEW NAME ─────────
            foreach (var v in ScanResolver(resolve, sync, "ResolveAbility")) yield return v;

            // ── (e) POSITIVE CONTROL, EXECUTED ───────────────────────────────
            var fake = typeof(FakeSeam);
            var control = ScanRule(FakeSeam.Rule, "FakeSeam.Rule")
                .Concat(ScanSite(fake.GetMethod("HostValidate", AllMembers), "ability-source-matches-selection",
                                 "FakeSeam.HostValidate", "control"))
                .Concat(ScanSite(fake.GetMethod("Mirror", AllMembers), "mirror-source-matches-selection",
                                 "FakeSeam.Mirror", "control"))
                .Concat(ScanResolver(fake.GetMethod("Resolve", AllMembers), fake, "FakeSeam.Resolve"))
                .ToList();
            foreach (var want in new[] { "selection-is-not-the-disambiguator", "ability-source-matches-selection",
                                         "mirror-source-matches-selection", "resolver-ignores-the-selection" })
                if (!control.Any(c => c.Contains(want)))
                    yield return "L240 control-not-red: FakeSeam commits " + want + " and the scan did not flag " +
                                 "it. That arm cannot tell the fixed shape from the broken one, so its green " +
                                 "above proves nothing — exactly how L186 stayed green while the rail propagated " +
                                 "a selection the acting peer never made.";
        }

        /// <summary>Arm (a) — run the shipped rule to exhaustion over two distinct sources. Plain
        /// <c>object</c>s stand in for the two weapons: the rule is reference identity and nothing else, which
        /// is the point — an <c>Equipment</c> cannot be built headless and does not need to be.</summary>
        private static IEnumerable<string> ScanRule(Func<bool, object, object, bool> rule, string label)
        {
            object rifle = new object(), pistol = new object();
            if (!rule(true, pistol, pistol))
                yield return "L240 selection-is-not-the-disambiguator: " + label + " rejects the instance " +
                             "sourced from the weapon the peer HAS selected. Every equipment-sourced ability " +
                             "would then fall through to first-match-by-guid, which is the defect verbatim.";
            if (rule(true, rifle, pistol))
                yield return "L240 selection-is-not-the-disambiguator: " + label + " accepts the instance " +
                             "sourced from a weapon that is NOT selected. That is first-match by another name: " +
                             "the host answers about the primary rifle a broken-armed soldier cannot hold while " +
                             "the peer is holding the pistol.";
            if (rule(false, pistol, pistol))
                yield return "L240 selection-is-not-the-disambiguator: " + label + " accepts a candidate whose " +
                             "DEF does not match. Selection would then outrank identity and any ability sourced " +
                             "from the held weapon would answer for any guid at all.";
            if (rule(true, null, null))
                yield return "L240 selection-is-not-the-disambiguator: " + label + " lets a SOURCELESS candidate " +
                             "win by selection (null == null). An actor-owned ability has no weapon to agree or " +
                             "disagree with; it must fall through to the plain def match, not tie with whatever " +
                             "happens to be in his hands.";
        }

        /// <summary>Arms (b)/(c) — the site resolves through the helper and never asks the raw lookup.</summary>
        private static IEnumerable<string> ScanSite(MethodBase site, string tag, string label, string consequence)
        {
            if (site == null)
            {
                yield return "L240 " + tag + ": " + label + " does not resolve at all, so nothing was checked.";
                yield break;
            }
            var mod = typeof(TacticalCommandSync).Assembly;
            var game = typeof(ActorComponent).Assembly;
            if (!Program.Callees(site, mod).Any(c => c.Name == "ResolveAbility"))
                yield return "L240 " + tag + ": " + label + " does not resolve the ability through " +
                             "ResolveAbility, so several instances sharing one def guid are indistinguishable " +
                             "to it — " + consequence + ".";
            if (Program.Callees(site, game).Any(c => c.Name == "GetAbilityFiltered"))
                yield return "L240 " + tag + ": " + label + " still calls GetAbilityFiltered itself. That method " +
                             "returns the FIRST match (ActorComponent:211-221) and Overwatch/Reload mint one " +
                             "instance per weapon, so " + consequence + ".";
        }

        /// <summary>Arm (d) — the resolver reads the selection and uses the pure matcher. The matcher is called
        /// from a lambda, so the scan walks the compiler-generated closures too.</summary>
        private static IEnumerable<string> ScanResolver(MethodBase resolve, Type root, string label)
        {
            if (resolve == null)
            {
                yield return "L240 resolver-ignores-the-selection: " + label + " does not resolve.";
                yield break;
            }
            if (!Program.Callees(resolve, typeof(EquipmentComponent).Assembly)
                        .Any(c => c.Name == "get_SelectedEquipment"))
                yield return "L240 resolver-ignores-the-selection: " + label + " never reads " +
                             "EquipmentComponent.SelectedEquipment, so it cannot be preferring the instance the " +
                             "acting peer meant. Whatever it returns, it is not resolved by selection.";
            var mod = typeof(TacticalCommandSync).Assembly;
            if (!MethodsUnder(root).Any(m => Program.Callees(m, mod)
                                                    .Any(c => c.Name == "CandidateMatchesSelection")))
                yield return "L240 resolver-ignores-the-selection: nothing under " + root.Name + " calls " +
                             "CandidateMatchesSelection, so arm (a) is executing a rule the shipped resolver " +
                             "does not use — a green decoration over live first-match behaviour.";
        }

        /// <summary>Every method of a type AND of its nested types, closures included.</summary>
        private static IEnumerable<MethodBase> MethodsUnder(Type t)
        {
            foreach (var m in t.GetMethods(AllMembers)) yield return m;
            foreach (var c in t.GetConstructors(AllMembers)) yield return c;
            foreach (var n in t.GetNestedTypes(AllMembers))
                foreach (var m in MethodsUnder(n)) yield return m;
        }

        /// <summary>THE BROKEN SHAPE, COMPILED. Never executed against a live actor — only its IL is read, and
        /// its <see cref="Rule"/> is run — but every arm above must be able to SEE it.</summary>
        private sealed class FakeSeam
        {
            internal static bool Rule(bool defMatches, object source, object selected) => defMatches;

            internal static TacticalAbility Resolve(TacticalActor actor, string guid) =>
                actor.GetAbilityFiltered<TacticalAbility>(a => a.AbilityDef != null && a.AbilityDef.Guid == guid);

            internal static TacticalAbility HostValidate(TacticalActor actor, string guid) => Resolve(actor, guid);

            internal static TacticalAbility Mirror(TacticalActor actor, string guid) => Resolve(actor, guid);
        }
    }
}
