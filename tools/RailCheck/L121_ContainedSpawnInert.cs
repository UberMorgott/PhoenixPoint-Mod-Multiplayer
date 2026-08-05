using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Entities;
using Base.Levels;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L121 — A CONTAINED SPAWN IS INERT TO THE GAME'S OWN ENUMERATOR, NOT JUST TO OURS.
    ///
    /// The failure (live 3-instance test, 2026-08-05): the peer that pressed FINISH on the post-mission
    /// results screen hung in the loading screen forever; the other two left normally. In the same frame it
    /// threw <c>NullReferenceException at TacticalActor.OnExitPlay() at TacticalLevelController.OnLevelEnd()
    /// at Base.Levels.Level.SetState … Broken coroutine call chain: Level+&lt;SetCurrentCrt&gt;d__50</c>. The
    /// one thing unique to that peer was a line no other peer logged: <c>a spawn this client made ITSELF
    /// (Oilfish_4) is CONTAINED</c>.
    ///
    /// THE ASSUMPTION THAT BROKE. Containment is a <c>false</c> prefix on <c>ActorComponent.DoEnterPlay</c>,
    /// and its whole safety argument was that <c>OnEnterPlay</c>:130-138 is the only caller of
    /// <c>BaseMap.RegisterActor</c> — so a contained actor is not in <c>_actors</c> and therefore invisible.
    /// It is not. <c>BaseMap.FindAllActors</c>:75-80 never reads <c>_actors</c>: it is
    /// <c>FindGameObjectsWithTag(ActorsTag).Where(go =&gt; go.activeInHierarchy)</c>, over the Unity scene. A
    /// half-built actor is still tagged and still active, so <c>OnLevelEnd</c>:748 called <c>OnExitPlay</c> on
    /// an actor whose <c>CharacterStats</c> <c>PrepareEnterPlay</c> had never built, and the NRE took the
    /// level-switch coroutine with it. The invariant was never enforced against the filter that mattered.
    ///
    /// THE ARMS:
    ///   (a) PREMISE — the containment seam, the native enumerator and the exit edge all still resolve.
    ///   (b) THE HAZARD IS REAL AND STILL WIRED: <c>TacticalLevelController.OnLevelEnd</c> still enumerates
    ///       through <c>BaseMap.FindAllActors</c> and still calls <c>ActorComponent.OnExitPlay</c> on what it
    ///       finds. If it ever stops, this law has become archaeology and should be re-read, not deleted
    ///       silently.
    ///   (c) THE PREDICATE IS STILL TAG + activeInHierarchy. This is the arm the fix RIDES: deactivating the
    ///       GameObject only contains anything for as long as the game filters on <c>activeInHierarchy</c>.
    ///       A game patch that reroutes <c>FindAllActors</c> through <c>_actors</c> — or drops the
    ///       active filter — makes <c>SetActive(false)</c> a no-op for containment, silently, and the hang
    ///       comes straight back.
    ///   (d) THE CONTAINMENT ACTUALLY DEACTIVATES. The one arm our own code can break.
    ///   (e) AND STILL DOES NOT DESTROY. Containment keeps the spawner's reference valid; the v1 shape
    ///       (destroy the object) is what this seam exists instead of, and an NRE in the spawner is the same
    ///       class of hang one layer up.
    ///   (f) THE SEAM IS STILL THE SEAM: <c>ActorEnterPlaySeam.Prefix</c> is still the containment decision,
    ///       so (d) is not being asserted about a method nothing calls.
    ///
    /// Falsify (verified to go RED, then restored):
    ///   • drop <c>component.gameObject.SetActive(false)</c> from OnActorEnteringPlay → contained-spawn-stays-enumerable
    /// </summary>
    internal static class L121_ContainedSpawnInert
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var mod = typeof(TacticalActorLifecycle).Assembly;
            var unity = typeof(GameObject).Assembly;

            var contain = typeof(TacticalActorLifecycle).GetMethod("OnActorEnteringPlay", All);
            var seam = mod.GetType("Multiplayer.Tactical.ActorEnterPlaySeam")?.GetMethod("Prefix", All);
            var findAll = typeof(BaseMap).GetMethod("FindAllActors", All);
            var actorsField = typeof(BaseMap).GetField("_actors", All);
            var onLevelEnd = typeof(TacticalLevelController).GetMethod("OnLevelEnd", All);
            var onExitPlay = typeof(ActorComponent).GetMethod("OnExitPlay", All);
            var setActive = typeof(GameObject).GetMethod("SetActive", All, null, new[] { typeof(bool) }, null);
            var activeInHierarchy = typeof(GameObject).GetMethod("get_activeInHierarchy", All);
            var findByTag = typeof(GameObject).GetMethods(All)
                                              .FirstOrDefault(m => m.Name == "FindGameObjectsWithTag");

            if (contain == null || seam == null || findAll == null || actorsField == null ||
                onLevelEnd == null || onExitPlay == null || setActive == null ||
                activeInHierarchy == null || findByTag == null)
            {
                yield return "L121 premise-changed: the containment family no longer resolves " +
                             "(TacticalActorLifecycle.OnActorEnteringPlay, ActorEnterPlaySeam.Prefix, " +
                             "BaseMap.FindAllActors/_actors, TacticalLevelController.OnLevelEnd, " +
                             "ActorComponent.OnExitPlay, GameObject.SetActive/activeInHierarchy/" +
                             "FindGameObjectsWithTag). Every arm below would pass vacuously, so 'a contained " +
                             "spawn is inert' is UNCHECKED rather than satisfied";
                yield break;
            }

            // ═══ (b) THE HAZARD IS REAL AND STILL WIRED ═══
            if (!Reaches(onLevelEnd, findAll, game) || !Reaches(onLevelEnd, onExitPlay, game))
                yield return "L121 premise-changed: TacticalLevelController.OnLevelEnd no longer enumerates " +
                             "through BaseMap.FindAllActors and calls ActorComponent.OnExitPlay on the result. " +
                             "That pair IS the hang this law exists for — a contained, half-built actor handed " +
                             "to OnExitPlay, which dereferences the CharacterStats PrepareEnterPlay never built " +
                             "and kills Level+<SetCurrentCrt>d__50. If the game changed this edge, re-derive " +
                             "the containment invariant against whatever replaced it; do not assume it is safe";

            // ═══ (c) THE PREDICATE IS STILL TAG + activeInHierarchy ═══
            // FindAllActors is a LINQ query, so its filter lives in a compiler-generated lambda on a nested
            // type — the walk has to include those or the arm passes on an empty search.
            var filterClosure = Closure(findAll).ToList();
            if (!filterClosure.Any(m => Reaches(m, findByTag, unity)) ||
                !filterClosure.Any(m => Reaches(m, activeInHierarchy, unity)))
                yield return "L121 enumerator-predicate-changed: BaseMap.FindAllActors no longer selects by " +
                             "FindGameObjectsWithTag + activeInHierarchy. The containment fix is to DEACTIVATE " +
                             "the contained GameObject, which contains it only for as long as that is the " +
                             "predicate the game filters on. Whatever FindAllActors reads now, the contained " +
                             "actor is back inside it, and OnLevelEnd will hand it to OnExitPlay again — the " +
                             "peer that presses FINISH hangs in loading, exactly as before";
            if (filterClosure.Any(m => Program.ReadsField(m, actorsField)))
                yield return "L121 enumerator-predicate-changed: BaseMap.FindAllActors now reads _actors. A " +
                             "contained spawn is deliberately never registered there, so this would make the " +
                             "law's fix redundant — but it would ALSO mean the enumerator is no longer the " +
                             "scene-wide one this containment was proven against. Re-derive before trusting it";

            // ═══ (d) THE CONTAINMENT ACTUALLY DEACTIVATES ═══
            if (!Reaches(contain, setActive, unity))
                yield return "L121 contained-spawn-stays-enumerable: TacticalActorLifecycle." +
                             "OnActorEnteringPlay refuses enter-play without deactivating the GameObject. " +
                             "Refusing DoEnterPlay only keeps the actor out of BaseMap._actors; " +
                             "BaseMap.FindAllActors:75-80 does not read _actors — it sweeps the scene by TAG " +
                             "and activeInHierarchy, and a half-built actor is still both. " +
                             "TacticalLevelController.OnLevelEnd:748 then calls OnExitPlay on it, " +
                             "TacticalActor.OnExitPlay:804 dereferences CharacterStats that PrepareEnterPlay " +
                             "never built, and the NRE kills the level-switch coroutine: the peer that " +
                             "pressed FINISH never leaves the loading screen";

            // ═══ (e) AND STILL DOES NOT DESTROY ═══
            foreach (var name in new[] { "Destroy", "DestroyImmediate" })
                foreach (var d in typeof(UnityEngine.Object).GetMethods(All).Where(m => m.Name == name))
                    if (Reaches(contain, d, unity))
                        yield return "L121 contained-spawn-destroyed: OnActorEnteringPlay calls " +
                                     "UnityEngine.Object." + name + ". Containment is inertness, not removal — " +
                                     "the spawner (ActorSpawner.SpawnActor returns the actor to its caller, and " +
                                     "deploy zones, spawn effects and death belchers all keep using it) still " +
                                     "holds the reference, so destroying it moves the NRE from level teardown " +
                                     "into the spawn path. That is the v1 shape this seam replaced";

            // ═══ (f) THE SEAM IS STILL THE SEAM ═══
            if (!Reaches(seam, contain, mod))
                yield return "L121 premise-changed: ActorEnterPlaySeam.Prefix no longer routes to " +
                             "TacticalActorLifecycle.OnActorEnteringPlay, so arm (d) is asserting a property " +
                             "of a method the DoEnterPlay patch does not call";
        }

        /// <summary>A method plus ITS OWN compiler-generated methods — LINQ lambdas and iterator bodies.
        /// Without them, walking a query method finds the Enumerable.Where call and none of the predicate
        /// that call carries, so a filter law would pass on an empty search. Narrowed by the C# naming
        /// convention (<c>&lt;FindAllActors&gt;b__33_0</c>, <c>&lt;FindAllActors&gt;d__12</c>) rather than
        /// taking every nested type: BaseMap also nests <c>GetActors</c>'s iterator, which DOES read
        /// <c>_actors</c>, and sweeping it in made the "still filters the scene, not _actors" arm answer
        /// about the wrong method.</summary>
        private static IEnumerable<MethodBase> Closure(MethodInfo m)
        {
            yield return m;
            var owner = m.DeclaringType;
            if (owner == null) yield break;
            string tag = "<" + m.Name + ">";
            foreach (var nested in owner.GetNestedTypes(All))
            {
                bool ownType = nested.Name.IndexOf(tag, StringComparison.Ordinal) >= 0;
                foreach (var inner in nested.GetMethods(All))
                    if (ownType || inner.Name.IndexOf(tag, StringComparison.Ordinal) >= 0)
                        yield return inner;
            }
        }

        private static bool Reaches(MethodBase from, MethodBase target, Assembly asm) =>
            from != null && target != null &&
            Program.Callees(from, asm).Any(c => c.MetadataToken == target.MetadataToken &&
                                                c.Module == target.Module);
    }
}
