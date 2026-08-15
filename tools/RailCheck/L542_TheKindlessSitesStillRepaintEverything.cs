using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L542 — THE KINDLESS MARK SITES STILL REPAINT EVERYTHING.
    ///
    /// ~63 call sites across 11 syncer files call OpenUiRepaint.MarkDirty() with NO kind and NO path —
    /// AssignSync 9, PersonnelSync 15, MissionSync 12, VehicleSync 6, GenericApplier 8 structural,
    /// IntentRail 3, DeployPrep 2, DeploymentWindow 2, EquipSync 2, TradeSync 1. Intent rejects,
    /// structural create/destroy, reseeds. They carry no path BY NATURE — a create has no leaf that
    /// changed — so they MUST keep an unconditional repaint-everything arm and must not be "improved"
    /// into the scoped path (§B.4).
    ///
    /// L38's own scan covers only UiEventMap.Fire, so these sites are outside its reach by construction;
    /// this law is what closes that. It does NOT weaken or replace L38 — L38 still owns the claim that no
    /// arm of Fire falls back to the parameterless mark.
    ///
    /// ARMS:
    ///   (a) kindless-mark-gone — the parameterless MarkDirty() overload still exists and is still public.
    ///   (b) kindless-sites-vanished — a substantial number of methods across the mod assembly still call
    ///       it. The count is asserted as a FLOOR, not an exact number: a site legitimately disappearing
    ///       with the code that raised it must not turn this red, but the whole family disappearing must.
    ///   (c) kindless-mark-is-scoped — the parameterless overload's IL does NOT reach SurfaceRepaints or
    ///       touch the path set. It sets the global bool and nothing else; anything more would make an
    ///       intent reject skippable, and an intent reject is exactly the moment a screen must repaint.
    ///   (d) global-bool-gone — POSITIVE CONTROL: the field the kindless arm sets still exists. §B.4 says
    ///       the global bool STAYS; a law that only checks callers would pass against an assembly where
    ///       the bool had been deleted and the mark had become a no-op.
    ///
    /// ROLES SEPARATED (§C.3): all four arms are statements about the shipped assembly, identical from
    /// either role.
    ///
    /// Falsify (compile-valid src mutations, each named): route MarkDirty() through the scoped path →
    /// (c); delete _dirty and set only _scopedDirty → (d); convert most kindless call sites to
    /// MarkDirty(type, geo, path) → (b).
    /// </summary>
    internal static class L542_TheKindlessSitesStillRepaintEverything
    {
        /// <summary>The floor, measured not guessed: 52 distinct methods across 11 files raise the kindless
        /// mark today (RailCheck probe, 2026-08-15). A fifth of them could legitimately go with the
        /// features that raise them before this number is wrong; the whole family cannot.</summary>
        private const int MinimumKindlessCallSites = 40;

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var kindless = repaint.GetMethod("MarkDirty", BindingFlags.Static | BindingFlags.Public |
                                                          BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (kindless == null)
            {
                yield return "L542 kindless-mark-gone: OpenUiRepaint.MarkDirty() (no arguments) does not " +
                             "exist. ~63 sites carry no path BY NATURE — a structural create has no leaf " +
                             "that changed, an intent reject has no leaf at all — and they must keep an " +
                             "unconditional repaint-everything arm (§B.4).";
                yield break;
            }

            Type[] types;
            try { types = repaint.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            var callers = types
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance |
                                              BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly).Cast<MethodBase>())
                .Where(m => m.DeclaringType != repaint && Il.References(m, kindless))
                .Select(m => m.DeclaringType.Name + "." + m.Name)
                .Distinct().ToList();

            if (callers.Count < MinimumKindlessCallSites)
                yield return "L542 kindless-sites-vanished: only " + callers.Count + " method(s) still " +
                             "call the kindless MarkDirty(), below the floor of " +
                             MinimumKindlessCallSites + ". The kindless sites were converted to the " +
                             "scoped path, which §B.4 forbids: they have no path to declare, so a scoped " +
                             "mark from one of them would be a mark nothing matches — i.e. a silent " +
                             "refusal to repaint after an intent reject or a structural destroy.";

            var scoped = repaint.GetMethod("SurfaceRepaints", BindingFlags.Static | BindingFlags.NonPublic |
                                                              BindingFlags.Public);
            if (scoped != null && Il.References(kindless, scoped))
                yield return "L542 kindless-mark-is-scoped: the parameterless MarkDirty() reaches " +
                             "SurfaceRepaints. It must set the global bool and nothing else — a kindless " +
                             "mark is the one that cannot be proven irrelevant to anything.";

            var dirty = repaint.GetField("_dirty", BindingFlags.Static | BindingFlags.NonPublic);
            if (dirty == null || dirty.FieldType != typeof(bool))
                yield return "L542 positive-control: OpenUiRepaint._dirty (bool) is gone. §B.4 says the " +
                             "global bool STAYS; without it the kindless arm this law counts callers of " +
                             "would be a no-op, and every arm above would pass while nothing repainted.";
        }
    }
}
