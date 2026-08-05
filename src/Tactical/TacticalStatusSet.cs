using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Entities.Statuses;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE MISSING FIELD CLASS ON THE ACTOR RAIL: an actor's STATUS SET.
    ///
    /// Every other thing an actor is made of already rides — hp, ap, wp, dead, body-part hp/armor — and the
    /// statuses, which are the single largest carrier of an actor's STRUCTURAL state, rode nothing at all.
    /// They existed on each peer only as a side effect of replaying the ability that applied them
    /// (<c>TacticalCommandSync.ApplyActivate</c>), so any order that was dropped, queued behind a stuck
    /// mirror, refused by the host or torn mid-coroutine left a status the host had and this peer did not —
    /// permanently, because nothing reconciled them. The vehicle passenger roster is the loudest instance
    /// (<c>VehicleComponent.Passengers</c> is written ONLY from <c>MountedStatus.OnApply/OnUnapply</c>:41/:95,
    /// so a lost Enter order desynchronised who is inside a vehicle for the rest of the battle) but it is an
    /// INSTANCE, not the problem: the same hole covers stances, buffs, panic, the vehicle's own
    /// empty/occupied status, and every status a mod adds.
    ///
    /// SO IT RIDES WHERE THE OTHER FIELDS ALREADY RIDE, on both host→all state ops that carry an actor's
    /// state: the 0x82 settle (per action end, plus the turn-edge sweep over every keyed live actor, so the
    /// set is re-asserted routinely) and the 0x84 resnapshot (the recovery path). No new surface, no new
    /// subsystem, no per-ability code.
    ///
    /// KEYED BY DEF NAME, NEVER BY GUID. TFTV mints status defs at runtime with <c>Guid.NewGuid()</c>, so a
    /// guid names a DIFFERENT def on every peer while <c>name</c> is the literal in the mod's own source and
    /// is identical everywhere — the same reason the 0x8A hint surface ships names (law L130).
    ///
    /// AND ONE ACTOR REFERENCE, STRUCTURALLY. A status may NAME another actor — <c>MountedStatus</c> carries
    /// the vehicle in <c>VehicleActorBase</c> and its whole <c>OnApply</c> dereferences it — and a status
    /// rebuilt without it would throw instead of mounting. That is read and written GENERICALLY: the first
    /// field on the status type assignable to <c>TacticalActorBase</c>, shipped as this rail's own actor key.
    /// Nothing here knows what a vehicle is.
    ///
    /// THE APPLY IS THE GAME'S OWN. <c>DefRepository.Instantiate</c> + <c>StatusComponent.ApplyStatus</c> /
    /// <c>UnapplyStatus</c> — exactly what <c>VehicleComponent.ApplyMountedStatus</c>:147-153 does — so every
    /// side effect a status owns (the passenger roster, the nav carve, the stat modifications, the vehicle's
    /// empty/occupied tag) is produced by the engine rather than re-implemented here.
    /// </summary>
    internal static class TacticalStatusSet
    {
        /// <summary>Def-name → def, rebuilt on a miss so runtime-minted defs (TFTV) resolve on their first
        /// use instead of being permanently unknown.</summary>
        private static readonly Dictionary<string, StatusDef> _byName =
            new Dictionary<string, StatusDef>(StringComparer.Ordinal);

        private static readonly Dictionary<Type, FieldInfo> _actorRef = new Dictionary<Type, FieldInfo>();

        // ─── the key ───────────────────────────────────────────────────────

        /// <summary>The stable identity of one applied status: its def NAME plus the actor it names (0 = it
        /// names none). Two statuses with the same pair are interchangeable, which is what makes the plan a
        /// multiset comparison rather than an instance one.</summary>
        internal static string Key(string defName, int refKey) =>
            refKey.ToString(CultureInfo.InvariantCulture) + "@" + (defName ?? "");

        private static bool Split(string key, out string defName, out int refKey)
        {
            defName = null; refKey = 0;
            if (string.IsNullOrEmpty(key)) return false;
            int at = key.IndexOf('@');
            if (at <= 0) return false;
            if (!int.TryParse(key.Substring(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out refKey))
                return false;
            defName = key.Substring(at + 1);
            return defName.Length != 0;
        }

        // ─── the plan, PURE so a law can execute the OUTCOME ───────────────

        /// <summary>What must happen to <paramref name="local"/> for it to BE <paramref name="host"/>, as
        /// multisets of <see cref="Key"/>. Pure: no game types, no statics — RailCheck L131 runs it and then
        /// asserts the resulting set EQUALS the host's, which is the outcome the rail owes, rather than
        /// asserting that some call happens.</summary>
        internal static void Plan(IList<string> local, IList<string> host,
                                  List<string> apply, List<string> unapply)
        {
            var want = new Dictionary<string, int>(StringComparer.Ordinal);
            if (host != null)
                foreach (var k in host)
                {
                    int n;
                    want.TryGetValue(k, out n);
                    want[k] = n + 1;
                }
            if (local != null)
                foreach (var k in local)
                {
                    int n;
                    if (want.TryGetValue(k, out n) && n > 0) want[k] = n - 1;
                    else unapply.Add(k);
                }
            foreach (var kv in want)
                for (int i = 0; i < kv.Value; i++) apply.Add(kv.Key);
            apply.Sort(StringComparer.Ordinal);
            unapply.Sort(StringComparer.Ordinal);
        }

        // ─── the codec ─────────────────────────────────────────────────────

        /// <summary>Host side: this actor's set, as keys. Statuses whose def cannot be NAMED are left out
        /// entirely — no peer could resolve one, and a key nobody can name must not make another peer drop
        /// its own copy.</summary>
        internal static List<string> Collect(TacticalActorBase actor)
        {
            var keys = new List<string>();
            var comp = actor == null ? null : actor.Status;
            if (comp == null) return keys;
            foreach (var s in comp.Statuses)
            {
                string k = KeyOf(s);
                if (k != null) keys.Add(k);
            }
            return keys;
        }

        internal static void Write(BinaryWriter w, List<string> keys)
        {
            w.Write(keys == null ? 0 : keys.Count);
            if (keys == null) return;
            foreach (var k in keys)
            {
                string name; int refKey;
                Split(k, out name, out refKey);
                w.Write(name ?? "");
                w.Write(refKey);
            }
        }

        /// <summary>Always consumes its bytes, whether or not the actor resolves — the stream is shared with
        /// every other actor in the same message.</summary>
        internal static List<string> Read(BinaryReader r)
        {
            int n = r.ReadInt32();
            var keys = new List<string>(n < 0 ? 0 : n);
            for (int i = 0; i < n; i++)
            {
                string name = r.ReadString();
                int refKey = r.ReadInt32();
                keys.Add(Key(name, refKey));
            }
            return keys;
        }

        // ─── the apply ─────────────────────────────────────────────────────

        /// <summary>Client side: make this actor's status set BE the host's. Every add and every removal goes
        /// through the game's own writers, so the roster, the stat modifications and the tags they own follow
        /// for free. One line only when something actually changed.</summary>
        internal static void Reconcile(TacticalActorBase actor, TacticalLevelController tlc,
                                       List<string> host, string when)
        {
            if (actor == null || host == null) return;
            var comp = actor.Status;
            if (comp == null) return;

            var live = new List<Status>();
            var localKeys = new List<string>();
            foreach (var s in comp.Statuses)
            {
                string k = KeyOf(s);
                if (k == null) continue;
                live.Add(s);
                localKeys.Add(k);
            }

            var apply = new List<string>();
            var unapply = new List<string>();
            Plan(localKeys, host, apply, unapply);
            if (apply.Count == 0 && unapply.Count == 0) return;

            var toRemove = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in unapply)
            {
                int n;
                toRemove.TryGetValue(k, out n);
                toRemove[k] = n + 1;
            }
            for (int i = 0; i < live.Count; i++)
            {
                int n;
                if (!toRemove.TryGetValue(localKeys[i], out n) || n <= 0) continue;
                toRemove[localKeys[i]] = n - 1;
                try { comp.UnapplyStatus(live[i]); }
                catch (Exception ex)
                {
                    Debug.LogError("[Multiplayer][tac] could not remove status " + localKeys[i] + " from " +
                                   actor.name + ", which the host does not have — that status stays on this " +
                                   "peer only: " + ex);
                }
            }
            foreach (var k in apply) ApplyOne(actor, comp, tlc, k);

            Debug.LogWarning("[Multiplayer][tac] status set of " + actor.name + " reconciled at " + when +
                             " — applied [" + string.Join(", ", apply.ToArray()) + "] removed [" +
                             string.Join(", ", unapply.ToArray()) + "]. Anything in those lists is a status " +
                             "this peer disagreed with the host about; a MountedStatus in them is a passenger " +
                             "roster that had diverged.");
        }

        private static void ApplyOne(TacticalActorBase actor, StatusComponent comp,
                                     TacticalLevelController tlc, string key)
        {
            string defName; int refKey;
            if (!Split(key, out defName, out refKey)) return;
            try
            {
                var def = Resolve(defName);
                if (def == null)
                {
                    Debug.LogError("[Multiplayer][tac] the host has status '" + defName + "' on " + actor.name +
                                   " and no def of that name exists on this peer — mod parity should have made " +
                                   "that impossible (law 10). That actor's state stays different here.");
                    return;
                }
                var repo = GameUtl.GameComponent<DefRepository>();
                if (repo == null) return;
                var status = repo.Instantiate<Status>(def);
                if (status == null) return;
                if (refKey != 0 && !SetActorRef(status, tlc, refKey, actor, defName)) return;
                comp.ApplyStatus(status);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] applying the host's status '" + defName + "' to " +
                               actor.name + " threw — that actor's state stays different on this peer: " + ex);
            }
        }

        // ─── the one actor a status may name ───────────────────────────────

        private static string KeyOf(Status s)
        {
            var def = s == null ? null : s.BaseDef;
            if (def == null || string.IsNullOrEmpty(def.name)) return null;
            return Key(def.name, RefKeyOf(s));
        }

        private static int RefKeyOf(Status s)
        {
            var f = ActorRefField(s.GetType());
            if (f == null) return 0;
            return TacticalActorKey.Of(f.GetValue(s) as TacticalActorBase);
        }

        private static bool SetActorRef(Status status, TacticalLevelController tlc, int refKey,
                                        TacticalActorBase owner, string defName)
        {
            var f = ActorRefField(status.GetType());
            if (f == null) return true;   // the host named an actor for a status that has nowhere to put one
            string why;
            var named = TacticalActorKey.Resolve(tlc, refKey, out why);
            if (named == null)
            {
                Debug.LogError("[Multiplayer][tac] the host's status '" + defName + "' on " + owner.name +
                               " names actor " + refKey + " and " + why + " — it cannot be rebuilt here.");
                return false;
            }
            f.SetValue(status, named);
            return true;
        }

        /// <summary>The first field on the status type that holds an actor. Structural, not a list of known
        /// statuses: any status — shipped or modded — that names an actor is carried by the same rule.</summary>
        private static FieldInfo ActorRefField(Type t)
        {
            FieldInfo f;
            if (_actorRef.TryGetValue(t, out f)) return f;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var candidate in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (typeof(TacticalActorBase).IsAssignableFrom(candidate.FieldType)) { f = candidate; break; }
                if (f != null) break;
            }
            _actorRef[t] = f;
            return f;
        }

        private static StatusDef Resolve(string name)
        {
            StatusDef def;
            if (_byName.TryGetValue(name, out def) && def != null) return def;
            var repo = GameUtl.GameComponent<DefRepository>();
            if (repo == null) return null;
            _byName.Clear();
            foreach (var d in repo.GetAllDefs<StatusDef>())
                if (d != null && !string.IsNullOrEmpty(d.name)) _byName[d.name] = d;
            _byName.TryGetValue(name, out def);
            return def;
        }
    }
}
