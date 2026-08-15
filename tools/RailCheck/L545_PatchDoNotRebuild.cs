using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L545 — A SURFACE WITH MODEL OR ANIMATION STATE IS PATCHED, NEVER REBUILT.
    ///
    /// SCOPING ALONE DOES NOT FIX THIS, which is why it is a separate law and a separate work item. A
    /// repaint that legitimately fires must still not destroy the model: UIStateEditSoldier.DisplaySoldier
    /// (UIStateEditSoldier.cs:580-585) reaches UIModuleActorCycle.DisplaySoldier(c, resetAnimation: true) →
    /// CommonCharacterUtils.ResetCharacterAnimation = Animator.Play(0, -1, 0f)
    /// (CommonCharacterUtils.cs:66-73, called at UIModuleActorCycle.cs:640), or the rebuild branch with its
    /// loading indicator (UIModuleActorCycle.cs:653-654). UIStateEditVehicle.DisplaySoldier does the same
    /// via DisplayVehicle(c, resetAnimation: true) (UIStateEditVehicle.cs:344-348 → UIModuleActorCycle
    /// .cs:695/:704).
    ///
    /// THE KNOWN-GOOD PATTERN ALREADY EXISTS IN THIS MOD: UiEventMap.RepaintAugmentScreen reaches
    /// UIModuleActorCycle.DisplaySoldier(c, resetAnimation: false, …) DIRECTLY instead of going through the
    /// state's private method. UiNativeRepaint.PaintSoldierDoll / PaintVehicleDoll now have that shape.
    ///
    /// ARMS:
    ///   (a) surface-has-no-table-entry — every name in UiNativeRepaint.ModelAnimationSurfaces has an entry
    ///       in UiNativeRepaint.Table, so TryRepaint reaches a real patch and the Exit+Enter fallback is
    ///       never consulted for it.
    ///   (b) destructive-doll-path-bound — no static reflection binding held by UiNativeRepaint resolves to
    ///       a method DECLARED ON one of those surfaces that itself calls UIModuleActorCycle.DisplaySoldier
    ///       or DisplayVehicle. Those are the state's own doll paths and every one of them passes
    ///       resetAnimation: true; binding one is how the repaint reached the animation reset for months.
    ///       IL-checked against the real game assembly, so a rename cannot make it pass by accident.
    ///   (c) fallback-reachable — OpenUiRepaint.Repaint's IL loads UiNativeRepaint.ModelAnimationSurfaces,
    ///       i.e. the Exit+Enter fallback actually asks the question before rebuilding a screen.
    ///   (d) POSITIVE CONTROL — the set is non-empty and contains both edit screens. Every arm above is
    ///       quantified over the set, so an empty set makes all of them vacuous — and the empty-set version
    ///       is exactly the state the code was in when the defect was reported.
    ///
    /// ROLES SEPARATED (§C.3): a repaint is a LOCAL act on whichever peer received the change, so these arms
    /// are role-free — and that is precisely the point: the reported reset happened on the peer that did
    /// NOTHING, driven by another peer's manufacturing tick.
    ///
    /// Falsify (compile-valid src mutations, each named): empty ModelAnimationSurfaces → (d); remove the
    /// UIStateEditSoldier entry from UiNativeRepaint.Table → (a); bind the state's private DisplaySoldier
    /// again → (b); delete the fallback refusal in OpenUiRepaint.Repaint → (c).
    /// </summary>
    internal static class L545_PatchDoNotRebuild
    {
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
                                        BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerable<string> Check()
        {
            // ── (d) POSITIVE CONTROL ─────────────────────────────────────────────────────────────
            var surfaces = UiNativeRepaint.ModelAnimationSurfaces;
            if (surfaces == null || surfaces.Count == 0)
            {
                yield return "L545 positive-control: UiNativeRepaint.ModelAnimationSurfaces is empty, so " +
                             "every arm of this law is quantified over nothing. That empty state is exactly " +
                             "the state the code was in when the soldier-model reset was reported.";
                yield break;
            }
            foreach (var required in new[] { "UIStateEditSoldier", "UIStateEditVehicle" })
                if (!surfaces.Contains(required))
                    yield return "L545 positive-control: " + required + " is not in ModelAnimationSurfaces. " +
                                 "It is one of the two screens whose native repaint reaches " +
                                 "resetAnimation: true, and it is the screen the reported defect was " +
                                 "observed on.";

            var table = UiNativeRepaint.Table;
            if (table == null)
            {
                yield return "L545 premise-changed: UiNativeRepaint.Table did not resolve, so arms (a) and " +
                             "(b) cannot see what a repaint of these surfaces reaches.";
                yield break;
            }

            var cycle = typeof(PhoenixPoint.Common.View.ViewModules.UIModuleActorCycle);
            var dollCalls = cycle.GetMethods(All)
                .Where(m => m.Name == "DisplaySoldier" || m.Name == "DisplayVehicle")
                .Cast<MethodBase>().ToList();
            if (dollCalls.Count == 0)
            {
                yield return "L545 premise-changed: UIModuleActorCycle has no DisplaySoldier/DisplayVehicle " +
                             "overload, so arm (b) cannot recognise a destructive doll path at all.";
                yield break;
            }

            // Every method the surfaces themselves own that repaints the doll. In the shipped game each of
            // them hands resetAnimation: true (UIStateEditSoldier.cs:584, UIStateEditVehicle.cs:348) or the
            // state's own _uiCharacterAnimationResetNeeded flag — none of them is a safe repaint target.
            var destructive = new Dictionary<MethodBase, string>();
            foreach (var name in surfaces.OrderBy(x => x, StringComparer.Ordinal))
            {
                var surfaceType = table.Keys.FirstOrDefault(k => k.Name == name);
                if (surfaceType == null)
                {
                    // ── (a) ──────────────────────────────────────────────────────────────────────
                    yield return "L545 surface-has-no-table-entry: " + name + " has no entry in " +
                                 "UiNativeRepaint.Table, so TryRepaint finds nothing and the repaint falls " +
                                 "through to Exit+Enter — the fallback that destroys and rebuilds every " +
                                 "widget on the screen and is documented to have restarted a cutscene seven " +
                                 "times. For a screen carrying a character doll that fallback IS the " +
                                 "animation reset this law exists to stop.";
                    continue;
                }
                if (table[surfaceType] == null)
                    yield return "L545 surface-has-no-table-entry: the UiNativeRepaint.Table entry for " +
                                 name + " is null, which is the same thing as having none — TryRepaint " +
                                 "cannot run it and the Exit+Enter fallback is what actually paints.";
                foreach (var m in surfaceType.GetMethods(All).Cast<MethodBase>())
                    if (dollCalls.Any(d => Il.References(m, d)))
                        destructive[m] = name;
            }
            if (destructive.Count == 0)
            {
                yield return "L545 premise-changed: not one method on any surface in " +
                             "ModelAnimationSurfaces calls UIModuleActorCycle.DisplaySoldier/DisplayVehicle. " +
                             "The doll paths arm (b) forbids binding could not be located, so arm (b) is " +
                             "checking nothing.";
                yield break;
            }

            // ── (b) NO STATIC BINDING HOLDS ONE OF THEM ──────────────────────────────────────────
            // Only UiNativeRepaint is scanned: that is where every native binding in the repaint path
            // lives (EsGetData, EsRefreshStorage, EsSelectProgression, EvVehicleEquipment …), and touching
            // arbitrary types would run their type initialisers in a console host.
            foreach (var fi in typeof(UiNativeRepaint).GetFields(BindingFlags.Static | BindingFlags.Public |
                                                                 BindingFlags.NonPublic))
            {
                if (!typeof(MethodBase).IsAssignableFrom(fi.FieldType)) continue;
                var bound = fi.GetValue(null) as MethodBase;
                if (bound == null || !destructive.TryGetValue(bound, out var owner)) continue;
                yield return "L545 destructive-doll-path-bound: UiNativeRepaint." + fi.Name + " binds " +
                             owner + "." + bound.Name + ", a method that repaints the character doll " +
                             "through UIModuleActorCycle.DisplaySoldier/DisplayVehicle with " +
                             "resetAnimation: true (UIStateEditSoldier.cs:584, UIStateEditVehicle.cs:348). " +
                             "That call reaches CommonCharacterUtils.ResetCharacterAnimation = " +
                             "Animator.Play(0, -1, 0f) (CommonCharacterUtils.cs:66-73), so ANOTHER peer's " +
                             "unrelated delta restarts the soldier's animation on a screen this player is " +
                             "only looking at. Reach UIModuleActorCycle directly with " +
                             "resetAnimation: false instead — the shape PaintSoldierDoll, PaintVehicleDoll " +
                             "and RepaintAugmentScreen already use.";
            }

            // ── (c) THE FALLBACK REFUSES THESE SURFACES ──────────────────────────────────────────
            var repaintMethod = typeof(OpenUiRepaint).GetMethod("Repaint",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var setField = typeof(UiNativeRepaint).GetField("ModelAnimationSurfaces",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (repaintMethod == null || setField == null)
                yield return "L545 premise-changed: OpenUiRepaint.Repaint or " +
                             "UiNativeRepaint.ModelAnimationSurfaces did not resolve, so arm (c) cannot see " +
                             "whether the fallback refuses a model/animation surface.";
            else if (!LoadsField(repaintMethod, setField))
                yield return "L545 fallback-reachable: OpenUiRepaint.Repaint never reads " +
                             "ModelAnimationSurfaces, so the Exit+Enter fallback is still reachable for a " +
                             "screen carrying model and animation state. For those screens the fallback IS " +
                             "the animation reset — it tears the state down and its EnterState repaints the " +
                             "doll through the very resetAnimation: true path arm (b) forbids binding.";
        }

        /// <summary>Does this method's IL resolve a token to that exact field? Same shape as
        /// <see cref="Il.References(MethodBase, MethodBase)"/>, for fields (ldsfld = 0x7E).</summary>
        private static bool LoadsField(MethodBase m, FieldInfo field)
        {
            var il = Il.Body(m);
            if (il == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                FieldInfo resolved = null;
                try { resolved = m.Module.ResolveField(BitConverter.ToInt32(il, i)); } catch { }
                if (resolved != null && resolved.MetadataToken == field.MetadataToken &&
                    resolved.Module == field.Module) return true;
            }
            return false;
        }
    }
}
