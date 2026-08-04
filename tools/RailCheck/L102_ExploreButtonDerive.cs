using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L102 — A CONTEXTUAL OFFER THAT WAS TAKEN IS CLOSED, NOT RE-DERIVED. Sibling of L92, not a
    /// duplicate of it: L92 asserts the site menu is REACHED by the repaint at all (module handle, no
    /// scene scan, never opened). It was GREEN through the whole 2026-08-04/05 session while the bug it
    /// names was live, because reaching the menu and re-running SetMenuItems changes NOTHING for this
    /// failure — and L92's arm (f) actively asserts the opposite ("the Explore button's disappearance
    /// stops being DERIVED…"), which is true of most abilities and false of this one.
    ///
    /// THE MEASURED FACT (3-instance session 2026-08-05 00:17, DLL 875520 B). A client explores a POI;
    /// on every OTHER peer the Explore button stays lit over the exploration spinner. The state mirror
    /// is NOT the gap — both watchers logged
    /// "[Multiplayer][rail] exploration re-seed V#1@8be7e872… → started", i.e. GenericApplier's
    /// ReseedExploration ran and the spinner is theirs. The DERIVATION is the gap:
    ///   • ExploreSiteAbility.GetDisabledStateInternal:18-29 reads crew only;
    ///     GetTargetDisabledStateInternal:31-55 reads GetVisible / ExplorationTime / GetInspected /
    ///     CurrentSite. Neither reads GeoVehicle.IsExploringSite:236 — whose ONLY readers in the whole
    ///     assembly are the ability's own no-op guard (ExploreSiteAbility:12) and
    ///     VehicleActionsViewService:179/:192. So the ability derives visible AND activatable for the
    ///     entire exploration, on every peer, exactly as it does in vanilla.
    ///   • and even a CanActivate that went false would not hide it: SetMenuItems:94 GREYS a
    ///     non-activatable ability (SetInteractable(false)), it never drops it. Only
    ///     VisibleInContextMenu drops, and that reads the same blind disabled states.
    /// What actually removes the button in vanilla is the CLICK PATH:
    /// UIStateVehicleSelected.OnContextualItemSelected:431 calls HideContextualMenu()
    /// UNCONDITIONALLY. A mirror never walks it, so the offer outlives the order it offered.
    ///
    /// WHAT THIS LAW ASSERTS IS THE OUTCOME, NOT THE CALL. Arms (a)-(c) hold the premise ("re-deriving
    /// cannot answer this") so the day the game makes the derivation exploration-aware, this law goes
    /// red and the close becomes removable dead weight. Arm (d) proves the close is reached. Arm (e)
    /// EXECUTES the edge decider case by case — the close must fire exactly once, on the batch where
    /// the peer learns exploration started, and must NOT fire again while it runs, or a player who
    /// deliberately opens that site's menu has it slammed shut by the next rail batch and the menu
    /// becomes unusable at any exploring site (vanilla allows it). Arm (f) keeps the close from ever
    /// becoming a view-state transition — the popped-state hazard this repo has paid for twice.
    ///
    /// Falsify: delete the HideContextualMenu call → close-unreached; make the edge state-based
    /// (hide whenever the site is exploring) → edge-not-once; drop the memo re-base on a null-site call
    /// → edge-stale-open; let the game start reading IsExploringSite in the ability → premise-derivable.
    /// </summary>
    internal static class L102_ExploreButtonDerive
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(OpenUiRepaint);
            var refresh = repaint.GetMethod("RefreshPersistentHud", All);
            var edge = repaint.GetMethod("ExplorationJustStarted", All);
            if (refresh == null || edge == null)
            {
                yield return "L102 seam-gone: OpenUiRepaint." +
                             (refresh == null ? "RefreshPersistentHud" : "ExplorationJustStarted") +
                             " does not resolve — the offer-consumed close has no entry point and every arm " +
                             "below is unaskable";
                yield break;
            }

            // ─── (a) PREMISE: the ability's derivation is BLIND to an exploration in progress ───
            var exploring = typeof(GeoVehicle).GetProperty("IsExploringSite", All);
            var explorationHandle = typeof(GeoVehicle).GetField("_explorationUpdateable", All);
            if (exploring == null || explorationHandle == null)
            {
                yield return "L102 premise-changed: GeoVehicle.IsExploringSite / _explorationUpdateable no longer " +
                             "resolve — the model predicate this whole seam is built on is gone, and the edge it " +
                             "samples now means something else";
            }
            else
            {
                var derivations = new[]
                {
                    typeof(ExploreSiteAbility).GetMethod("GetDisabledStateInternal", All),
                    typeof(ExploreSiteAbility).GetMethod("GetTargetDisabledStateInternal", All)
                };
                if (derivations.Any(d => d == null))
                    yield return "L102 premise-changed: ExploreSiteAbility.GetDisabledStateInternal / " +
                                 "GetTargetDisabledStateInternal no longer resolve — whether the Explore button can " +
                                 "derive itself away is now unknown, and this seam must not be trusted meanwhile";
                // Depth 2, not 3: this walk expands through the GAME assembly, and the claim being made is
                // about the DERIVATION — its own body and what it directly asks. A deeper walk buys nothing
                // here and fans out far enough to turn some unrelated deep helper into a false red.
                else if (derivations.Any(d => ReadsMember(d, game, exploring.GetMethod, explorationHandle, 2)))
                    yield return "L102 premise-derivable: ExploreSiteAbility's disabled-state derivation now READS " +
                                 "IsExploringSite. The button hides itself the moment the mirrored exploration state " +
                                 "lands, so re-deriving (L92) is sufficient and OpenUiRepaint's offer-consumed close " +
                                 "is dead weight that also closes a menu the player may legitimately keep open";
            }

            // ─── (b) PREMISE: a non-activatable ability is GREYED, never dropped ───
            var setMenuItems = typeof(UIModuleSiteContextualMenu).GetMethod("SetMenuItems", All);
            var setInteractable = typeof(PhoenixGeneralButton).GetMethod("SetInteractable", All);
            if (setMenuItems == null)
                yield return "L102 premise-changed: UIModuleSiteContextualMenu.SetMenuItems is gone — the one native " +
                             "rebuild this module has cannot be inspected";
            else if (setInteractable == null)
                yield return "L102 premise-changed: PhoenixGeneralButton.SetInteractable no longer resolves — whether " +
                             "SetMenuItems greys or drops a non-activatable ability can no longer be told apart, and " +
                             "the difference is exactly why re-deriving does not remove this button";
            else if (!Calls(setMenuItems, setInteractable))
                yield return "L102 premise-changed: SetMenuItems no longer calls SetInteractable — it may now DROP a " +
                             "non-activatable ability instead of greying it, which would make CanActivate enough to " +
                             "remove the button and this seam's close unnecessary. Re-derive the premise";

            // ─── (c) PREMISE: the game's own reaction to taking the offer IS closing the menu ───
            var hide = typeof(UIModuleSiteContextualMenu).GetMethod("HideContextualMenu", All);
            var onSelected = typeof(UIStateVehicleSelected).GetMethod("OnContextualItemSelected", All);
            if (hide == null || onSelected == null)
                yield return "L102 premise-changed: UIModuleSiteContextualMenu.HideContextualMenu / " +
                             "UIStateVehicleSelected.OnContextualItemSelected no longer resolve — the native reaction " +
                             "this seam reproduces on a mirror cannot be located, so the close is now a guess";
            else if (!Calls(onSelected, hide))
                yield return "L102 premise-changed: OnContextualItemSelected no longer closes the menu on activation. " +
                             "The mirror is then reproducing a reaction the acting peer no longer has — peers would " +
                             "diverge in the opposite direction, the watcher losing a menu the actor keeps";

            // ─── (d) THE CLOSE IS REACHED from the one universal repaint ───
            if (hide != null && !Reaches(refresh, game, 3).Any(c => c.MetadataToken == hide.MetadataToken))
                yield return "L102 close-unreached: RefreshPersistentHud never reaches " +
                             "UIModuleSiteContextualMenu.HideContextualMenu — nothing on any peer retires an offer " +
                             "another peer already took, and the Explore button keeps covering the exploration " +
                             "spinner until the player clicks elsewhere";

            // ─── (e) THE OUTCOME — the edge decider EXECUTED, not merely present ───
            foreach (var v in EdgeCases(edge)) yield return v;

            // ─── (f) THE CLOSE MAY NOT BE A VIEW-STATE TRANSITION ───
            if (hide != null)
            {
                var transition = Callees(hide, game)
                    .FirstOrDefault(c => c.Name == "SwitchToState" || c.Name == "SwitchToPreviousState" ||
                                         c.Name == "EnterState" || c.Name == "ExitState");
                if (transition != null)
                    yield return "L102 close-transitions: HideContextualMenu now reaches " + transition.Name +
                                 ". Closing an offer must stay a pure widget SetActive(false) — a repaint that moves " +
                                 "the view stack is what resurrected a zombie ability and replayed a cutscene 7x";
            }
        }

        /// <summary>Arm (e), the one that bites on the REAL failure. The decider is pure, so it is driven
        /// here on a scratch memo instead of being asserted to exist.</summary>
        private static IEnumerable<string> EdgeCases(MethodInfo edge)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var none = new HashSet<string>(StringComparer.Ordinal);
            var s372 = new HashSet<string>(StringComparer.Ordinal) { "S#372" };
            var both = new HashSet<string>(StringComparer.Ordinal) { "S#372", "S#41" };

            bool Ask(HashSet<string> now, string site)
            {
                try { return (bool)edge.Invoke(null, new object[] { seen, now, site }); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            // Nothing is being explored — an open menu is never touched.
            if (Ask(none, "S#372"))
                yield return "L102 edge-spurious: the decider reports an exploration start for a site nobody is " +
                             "exploring — every rail batch would close the open site menu";

            // The batch on which this peer learns the exploration started: the offer is taken, close once.
            if (!Ask(s372, "S#372"))
                yield return "L102 edge-missed: the decider does not fire on the batch where the site FIRST appears " +
                             "as being explored. That batch is the mirror's only equivalent of the acting peer's " +
                             "click, so the Explore button survives the exploration — the reported bug, unfixed";

            // …and NOT again while it runs, or the player can never open that site's menu.
            if (Ask(s372, "S#372"))
                yield return "L102 edge-not-once: the decider fires again while the SAME exploration is still " +
                             "running. The close is then effectively state-based: a player who deliberately opens " +
                             "that site's menu has it slammed shut by the next rail batch, which vanilla allows and " +
                             "this seam must not take away";

            // A second site starting is its own edge; the first one staying is not.
            if (!Ask(both, "S#41"))
                yield return "L102 edge-missed: a SECOND site starting exploration in the same batch is not " +
                             "reported — the memo is being treated as a single slot, so only one aircraft's order " +
                             "ever retires an offer";
            if (Ask(both, "S#372"))
                yield return "L102 edge-not-once: the still-running first exploration re-fires once a second one " +
                             "starts — the memo is not being re-based, only added to";

            // Exploration ends, then a NEW one starts on the same site: a fresh offer, a fresh edge.
            if (Ask(none, "S#372"))
                yield return "L102 edge-spurious: the decider fires when the site STOPS being explored";
            if (!Ask(s372, "S#372"))
                yield return "L102 edge-missed: a site explored again after an earlier exploration ended no longer " +
                             "produces an edge — the memo kept a dead entry, so the second order retires nothing";

            // THE ONE THAT MADE THE STATE-BASED VERSION UNSHIPPABLE: the exploration started while the
            // menu was CLOSED (the peer asks about no site at all), and the player opens that site later.
            // The memo must have been re-based by those menu-less calls, so opening is not an edge.
            seen.Clear();
            Ask(s372, null);            // batches arriving with no menu open
            Ask(s372, null);
            if (Ask(s372, "S#372"))
                yield return "L102 edge-stale-open: an exploration that started while the menu was closed reads as a " +
                             "fresh edge the moment the player opens that site — the menu closes under their cursor. " +
                             "The memo must be re-based on EVERY call, including the ones that ask about no site";
        }

        // ─── IL helpers — VERBATIM from L92 (same operand walk: under-reports, never invents an edge) ───

        /// <summary>Every method in <paramref name="asm"/> reachable from <paramref name="root"/> within
        /// <paramref name="depth"/> hops, expanding only through our OWN assembly.</summary>
        private static IEnumerable<MethodBase> Reaches(MethodBase root, Assembly asm, int depth)
        {
            var ours = root.DeclaringType.Assembly;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<MethodBase> { root };
            var result = new List<MethodBase>();
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                {
                    result.AddRange(Callees(m, asm));
                    foreach (var c in Callees(m, ours))
                        if (seen.Add(Key(c))) next.Add(c);
                }
                frontier = next;
            }
            return result;
        }

        /// <summary>Is <paramref name="getter"/> called, or <paramref name="field"/> read, within
        /// <paramref name="depth"/> hops of <paramref name="root"/> through the GAME assembly? Both, because
        /// a derivation could read the backing handle directly instead of the property.</summary>
        private static bool ReadsMember(MethodBase root, Assembly game, MethodBase getter, FieldInfo field, int depth)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<MethodBase> { root };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                {
                    foreach (var op in Ops(m))
                    {
                        if (op.Method != null && getter != null &&
                            op.Method.MetadataToken == getter.MetadataToken &&
                            op.Method.Module == getter.Module) return true;
                        if (op.Field != null && field != null &&
                            op.Field.MetadataToken == field.MetadataToken &&
                            op.Field.Module == field.Module) return true;
                    }
                    foreach (var c in Callees(m, game))
                        if (seen.Add(Key(c))) next.Add(c);
                }
                frontier = next;
            }
            return false;
        }

        /// <summary>Identity for the visited set: ResolveMethod hands back a FRESH MethodBase per call site,
        /// so reference/hash identity would never dedup and the walk would fan out exponentially.</summary>
        private static string Key(MethodBase m) => m.Module.FullyQualifiedName + ":" + m.MetadataToken;

        private static bool Calls(MethodBase m, MethodBase callee) =>
            callee != null && Callees(m, callee.Module.Assembly)
                .Any(c => c.MetadataToken == callee.MetadataToken && c.Module == callee.Module);

        private static IEnumerable<MethodBase> Callees(MethodBase m, Assembly asm)
        {
            foreach (var op in Ops(m))
                if (op.Method != null && op.Method.Module.Assembly == asm) yield return op.Method;
        }

        private struct Op { public int Offset; public MethodBase Method; public FieldInfo Field; }

        private static readonly Dictionary<short, OpCode> OpCodeByValue =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                           .Where(f => f.FieldType == typeof(OpCode))
                           .Select(f => (OpCode)f.GetValue(null))
                           .GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

        private static IEnumerable<Op> Ops(MethodBase m)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                int at = i;
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null) yield return new Op { Offset = at, Method = callee };
                }
                else if (op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (f != null) yield return new Op { Offset = at, Field = f };
                }
                i += size;
            }
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return -1;
            }
        }
    }
}
