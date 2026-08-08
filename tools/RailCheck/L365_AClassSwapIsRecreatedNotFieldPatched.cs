using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L365 — A RAIL-OWNED DESCEND FIELD THAT CHANGES CLASS IS RE-CREATED, NOT FIELD-PATCHED.
    ///
    /// The fourth structural shape, and the one the set-diff was blind to: the PATH is unchanged, so
    /// <c>_prevRoots</c> sees nothing, and <c>DiffEngine.SnapKey</c> carries no kind — field 5 of the old
    /// class and field 5 of the new one share a snapshot key. Untreated, every delta under that path lands
    /// on the wrong-class instance FOREVER, with the only trace a "dto-twin gap" line about a member the
    /// live object does not have.
    ///
    /// Measured, live: the host's <c>S#88.SerializationData.ActiveMission</c> became a
    /// <c>GeoStealResourcesFromHavenMission</c> while the client still held the <c>GeoCustomMission</c>
    /// created earlier — and that client never saw the haven-attack window at all, because
    /// <c>GeoModalMirror.NeedsPark</c> parks only on an UNRESOLVED ref and a stale wrong-class hit resolves
    /// fine.
    ///
    /// Driven as the REAL function over the REAL statics, not as an IL shape: this law fills
    /// <c>DiffEngine._walkRoots</c> / <c>_prevRootTypes</c> exactly as a walk would and asks
    /// <c>ClassSwappedPaths</c> what it found. Both are restored afterwards, so no later law inherits them.
    ///
    /// ARMS
    ///   (a) <c>sibling-class-not-recreated</c> — a Descend path whose object is replaced by a SIBLING
    ///       subclass must come back as swapped (⇒ EmitStructural destroys and re-creates it).
    ///   (b) <c>same-class-recreated</c> — NEGATIVE CONTROL: the identical class must NOT be swapped, or
    ///       every walk would destroy and rebuild every mission on every peer.
    ///   (c) <c>actor-root-recreated</c> — NEGATIVE CONTROL: a class change on a NON-Descend key must not
    ///       produce a destroy+create. Re-creating a MonoBehaviour-bound actor is forbidden (law 3), and a
    ///       blob root that changed class is a keying gap this cannot repair — it gets a log line instead.
    ///   (d) <c>unseen-path-recreated</c> — NEGATIVE CONTROL: a path with no recorded previous type (its
    ///       first walk) is a CREATE, already handled by the set-diff, and must not also be swapped.
    ///
    /// Falsify: make <c>ClassSwappedPaths</c> return null → (a) red; make it ignore the PayloadFor gate
    /// → (c) red; make it compare nothing and swap unconditionally → (b) and (d) red.
    /// </summary>
    internal static class L365_AClassSwapIsRecreatedNotFieldPatched
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        // A Descend path (a '.' with no '#' after it) and a plain root key, in the exact shapes
        // DiffEngine.IsDescendPath sorts on.
        private const string DescendPath = "S#88.SerializationData.ActiveMission";
        private const string RootKey = "U#4242";

        internal static IEnumerable<string> Check()
        {
            var swapper = typeof(DiffEngine).GetMethod("ClassSwappedPaths", All);
            var walkRoots = typeof(DiffEngine).GetField("_walkRoots", All)?.GetValue(null) as IDictionary<string, object>;
            var prevTypes = typeof(DiffEngine).GetField("_prevRootTypes", All)?.GetValue(null) as IDictionary<string, string>;
            var missionA = typeof(PhoenixPoint.Geoscape.Entities.GeoMission).Assembly
                             .GetType("PhoenixPoint.Geoscape.Entities.Missions.GeoCustomMission", false)
                           ?? SomeMissionSubclass(0);
            var missionB = SomeMissionSubclass(1, missionA);
            if (swapper == null || walkRoots == null || prevTypes == null || missionA == null || missionB == null)
            {
                yield return "L365 premise-changed: DiffEngine.ClassSwappedPaths / _walkRoots / _prevRootTypes no " +
                             "longer resolve whole, or the game no longer ships two concrete GeoMission subclasses " +
                             "to swap between. Those three ARE the detector — re-point this law at whatever carries " +
                             "the per-path runtime type now; do not delete it. Without the detector a mission that " +
                             "changes class is field-patched onto the wrong-class instance forever, and the only " +
                             "symptom is a haven-attack window one peer never sees.";
                yield break;
            }

            var savedWalk = new Dictionary<string, object>(walkRoots);
            var savedTypes = new Dictionary<string, string>(prevTypes);
            List<string> sibling, same, actor, unseen;
            try
            {
                sibling = Run(swapper, walkRoots, prevTypes, DescendPath, missionA.FullName, missionB);
                same = Run(swapper, walkRoots, prevTypes, DescendPath, missionB.FullName, missionB);
                actor = Run(swapper, walkRoots, prevTypes, RootKey, missionA.FullName, missionB);
                unseen = Run(swapper, walkRoots, prevTypes, DescendPath, null, missionB);
            }
            finally
            {
                walkRoots.Clear(); foreach (var kv in savedWalk) walkRoots[kv.Key] = kv.Value;
                prevTypes.Clear(); foreach (var kv in savedTypes) prevTypes[kv.Key] = kv.Value;
            }

            if (!sibling.Contains(DescendPath))
                yield return "L365 sibling-class-not-recreated: '" + DescendPath + "' held a " + missionA.Name +
                             " and now holds a " + missionB.Name + ", and ClassSwappedPaths reported nothing. No " +
                             "destroy+create is emitted, so the client keeps the OLD class and every field delta " +
                             "under that path lands on it — SnapKey carries no kind, so field 5 of one class " +
                             "overwrites field 5 of the other. That is the haven-attack window a client never saw.";

            if (same.Contains(DescendPath))
                yield return "L365 same-class-recreated: an UNCHANGED class at '" + DescendPath + "' was reported " +
                             "as a class swap. Every walk would then destroy and re-create the mission on every " +
                             "peer — a structural packet per cycle, and the client's object replaced under any " +
                             "open window that holds it.";

            if (actor.Contains(RootKey))
                yield return "L365 actor-root-recreated: a class change on the non-Descend key '" + RootKey +
                             "' was reported as a re-createable swap. Destroy+create is licensed for Descend " +
                             "FIELDS only: re-creating a MonoBehaviour-bound actor is forbidden (law 3), and a " +
                             "blob root whose id changed class is a KEYING gap a create cannot repair. It must " +
                             "get the loud line instead.";

            if (unseen.Contains(DescendPath))
                yield return "L365 unseen-path-recreated: a path with no recorded previous type was reported as a " +
                             "swap. Its first sighting is an ordinary CREATE the set-diff already emits; calling " +
                             "it a swap emits a destroy for an object no peer has.";
        }

        /// <summary>Stage one walk's worth of state and ask the real detector. <paramref name="was"/> null =
        /// the path has never been walked before.</summary>
        private static List<string> Run(MethodInfo swapper, IDictionary<string, object> walkRoots,
                                        IDictionary<string, string> prevTypes, string key, string was, Type now)
        {
            walkRoots.Clear();
            prevTypes.Clear();
            walkRoots[key] = FormatterServices.GetUninitializedObject(now); // no ctor — only GetType() is read
            if (was != null) prevTypes[key] = was;
            return (swapper.Invoke(null, null) as List<string>) ?? new List<string>();
        }

        /// <summary>A concrete <c>GeoMission</c> subclass, picked off the game assembly rather than named:
        /// the family has 12 concretions and which ones exist is a DLC question.</summary>
        private static Type SomeMissionSubclass(int skip, Type notThis = null)
        {
            var b = typeof(PhoenixPoint.Geoscape.Entities.GeoMission);
            return b.Assembly.GetTypes()
                    .Where(t => t != null && !t.IsAbstract && b.IsAssignableFrom(t) && t != b && t != notThis)
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .Skip(skip).FirstOrDefault();
        }
    }
}
