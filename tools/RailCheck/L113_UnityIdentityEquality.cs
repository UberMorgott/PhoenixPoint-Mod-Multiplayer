using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>
    /// L113 — NO IDENTITY QUESTION ON THE RAIL IS ASKED WITH <c>==</c>.
    ///
    /// THE BUG CLASS THIS LAW IS THE GRAVE OF. <c>UnityEngine.Object</c> overloads <c>==</c>/<c>!=</c>, and
    /// the overload does NOT answer "is this the same reference". <c>CompareBaseObjects</c> answers
    /// "is the NATIVE half still alive" (via the <c>GetCachedPtr</c> ECall) and only then falls back to
    /// <c>m_InstanceID</c>. Two consequences, and this repo has been bitten by both:
    ///   * a DESTROYED-but-still-referenced wrapper compares EQUAL TO NULL, so a guard written to mean
    ///     "do I have this object at all" silently means "is it still alive" — and the rail's addressing
    ///     code is precisely the code that must still NAME a destroyed thing (a corpse the game just
    ///     destroyed is exactly what a trailing loot or damage record addresses);
    ///   * an <c>a == b</c> between two objects is not an identity test at all.
    /// It fails QUIETLY — a wrong <c>bool</c>, never an exception — which is this repo's dominant bug
    /// shape (a mirrored record dropped with no log line). Found by hand on 2026-08-05: the marketplace's
    /// "which GeoVehicle did the peer pick", and <c>DefGuid</c>/<c>ResolveIn</c> in the tactical inventory
    /// address, where a destroyed <c>InventoryComponent</c>/def collapsed to null and the record was
    /// dropped as unaddressable.
    ///
    /// IT IS ALSO A RELEASE-BUILD LANDMINE. That <c>GetCachedPtr</c> ECall cannot be JIT-compiled outside
    /// the Unity player ("ECall methods must be packaged into a system module"). Under <c>-c Debug</c>
    /// nothing inlines, so the ECall is only reached if the comparison walks that far AT RUNTIME; under
    /// <c>-c Release</c> the JIT inlines <c>op_Equality</c> into its CALLER, and the caller then fails to
    /// compile AT ALL. That is precisely how <c>TacticalActorKey.BuildBattleKeys</c> took the Release
    /// harness down while Debug stayed green — for a reason nothing in the source shows.
    ///
    /// WHY THE LAW IS SCOPED, AND NOT "NEVER USE == ON A UNITY OBJECT". A plain <c>view == null</c> in
    /// presentation code is the CORRECT idiom: there the question really is "is this still alive", which
    /// is the question Unity's operator answers. Banning it outright would flag 356 methods and turn a
    /// sharp law into one everybody suppresses. So the law bans exactly the two shapes that are wrong:
    ///
    ///   (a) MOD-WIDE — <c>a == b</c> between two Unity objects with NO null literal. "Is this the same
    ///       object" has no correct spelling through this operator, anywhere, ever.
    ///   (b) IN THE ADDRESSING PATHS — <c>== null</c> too, inside the declared set of types whose whole job
    ///       is turning an object into a wire address and back. A destroyed object still HAS an address;
    ///       collapsing it to null there is how a record gets dropped instead of applied.
    ///
    /// The fix is always <c>ReferenceEquals</c> — never <c>(object)x == null</c> (same three tokens, and
    /// the next reader deletes the cast) and never <c>x is null</c>.
    ///
    /// WHAT THIS LAW DELIBERATELY DOES NOT TOUCH. PP types that carry real VALUE semantics —
    /// <c>GeoItem.Equals</c>:124 compares by def, and the geoscape item paths depend on that — are not
    /// <c>UnityEngine.Object</c> and never enter this sweep. Dictionary/HashSet keying on a Unity object
    /// is likewise left alone: <c>Object.GetHashCode</c> is <c>m_InstanceID</c> and the <c>Equals</c>
    /// override only collapses against NULL, which is not a key the rail ever stores — so
    /// <c>TacticalActorKey._derived</c> and friends are correct as written.
    ///
    /// FALSIFIABILITY (verified 2026-08-05). Restoring <c>if (tlc == null || tlc.Map == null) return;</c> in
    /// <c>TacticalActorKey.BuildBattleKeys</c> turns arm (b) red and names the method.
    /// </summary>
    internal static class L113_UnityIdentityEquality
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        /// <summary>Arm (b)'s scope: the METHODS that turn a live object into a wire address and back, and
        /// only those. These are the ones for which "destroyed" and "absent" must be DIFFERENT answers,
        /// because the rail's whole job at that moment is to keep naming a thing the game has already torn
        /// down. Everywhere ELSE in the mod the ordinary Unity null idiom is correct and stays, which is
        /// why this is a method list and not a type list: a blanket ban inside these types would also
        /// convert guards that legitimately mean "is this view still alive", turning a silent skip into a
        /// MissingReferenceException. Scoped deliberately, not lazily — see the report of 2026-08-05 for
        /// the addressing-adjacent sites left as ambiguous.</summary>
        private static readonly string[] AddressKeyMethods =
        {
            "Multiplayer.Tactical.TacticalActorKey.BuildBattleKeys",
            "Multiplayer.Tactical.TacticalActorKey.Resolve",
            "Multiplayer.Tactical.TacticalActorKey.ResolveReceiver",
            "Multiplayer.Tactical.TacticalInventorySync.AddressOf",
            "Multiplayer.Tactical.TacticalInventorySync.ContainerOf",
            "Multiplayer.Tactical.TacticalInventorySync.DefGuid",
            "Multiplayer.Tactical.TacticalInventorySync.OrdinalOf",
            "Multiplayer.Tactical.TacticalInventorySync.ResolveIn",
        };

        internal static IEnumerable<string> Check()
        {
            var unity = typeof(UnityEngine.Object).Assembly;
            var mod = typeof(Multiplayer.Network.Sync.DiffEngine).Assembly;

            // Non-vacuity: the sweep only means something if it can SEE the operator, and if the scope of
            // arm (b) still resolves to real types. Either gone = say so, never report a clean sweep.
            if (typeof(UnityEngine.Object).GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static) == null)
            {
                yield return "L113 unity-operator-gone: UnityEngine.Object.op_Equality does not resolve — the sweep " +
                             "below can no longer detect the bug class it exists for";
                yield break;
            }

            Type[] types;
            try { types = mod.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            int scanned = 0;
            var identity = new List<string>();
            var addressing = new List<string>();
            var seenAddressKey = new HashSet<string>();

            foreach (var t in types)
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    scanned++;
                    string full = t.FullName + "." + m.Name;
                    bool addressKey = AddressKeyMethods.Contains(full);
                    if (addressKey) seenAddressKey.Add(full);
                    foreach (var site in Program.CallSites(m, unity, directCallsOnly: true))
                    {
                        var c = site.Key;
                        if (c.DeclaringType != typeof(UnityEngine.Object) ||
                            (c.Name != "op_Equality" && c.Name != "op_Inequality")) continue;
                        string where = full + " (" + (c.Name == "op_Equality" ? "==" : "!=") + ")";
                        if (!site.Value) { identity.Add(where); break; }   // site.Value = one operand was `null`
                        if (addressKey) { addressing.Add(where); break; }
                    }
                }

            // Arm (b) is a named list, so a renamed or deleted method must be LOUD: silently checking one
            // method fewer than it claims to is how a scoped law rots into a green no-op.
            foreach (var name in AddressKeyMethods.Where(n => !seenAddressKey.Contains(n)))
                yield return "L113 address-key-scope-gone: " + name + " no longer resolves, so arm (b) is checking " +
                             "one method fewer than it claims to — re-point the list or drop the entry";

            // A sweep over nothing is the failure mode a coverage law has to rule out first.
            if (scanned < 500)
                yield return "L113 sweep-vacuous: only " + scanned + " mod methods were walked — the assembly did " +
                             "not load the way this law assumes, so a clean result proves nothing";

            foreach (var h in identity.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                yield return "L113 unity-object-identity: " + h + " compares two UnityEngine.Objects. That operator " +
                             "answers 'is the native half alive' and then compares instance ids — it is never a " +
                             "reference-identity test, and a destroyed operand collapses it to a null comparison. " +
                             "Use ReferenceEquals";

            foreach (var h in addressing.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                yield return "L113 address-path-null-collapse: " + h + " tests a UnityEngine.Object against null " +
                             "inside an ADDRESSING path. A destroyed object still has a wire address — the game " +
                             "destroys the corpse a trailing record names — but Unity's operator reports it as " +
                             "null, so the record is dropped as unaddressable with no log line. Use ReferenceEquals " +
                             "(it also keeps the method JIT-able in a Release harness: the inlined GetCachedPtr " +
                             "ECall cannot compile outside the player)";
        }
    }
}
