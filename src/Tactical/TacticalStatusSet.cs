using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using Base.Entities.Statuses;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Statuses;
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

        /// <summary>The stable identity of one applied status: its def NAME, the actor it names (0 = it names
        /// none) and its SOURCE def name ("" = its source is not a def, or it has none). Two statuses with the
        /// same triple are interchangeable, which is what makes the plan a multiset comparison rather than an
        /// instance one.
        ///
        /// WHY THE SOURCE IS PART OF THE IDENTITY AND NOT A LOOSE FIELD. A status is applied by the game as
        /// <c>ApplyStatus(def, source)</c> — <c>OverwatchAbility</c>:89 passes
        /// <c>OverwatchWeapon.ItemDef</c> — and its own lifecycle READS that source back:
        /// <c>OverwatchStatus.GetWeapon</c>:59-68 casts <c>Source</c> to a <c>WeaponDef</c> and looks the live
        /// weapon up by it. A rebuilt status with a null source is not "the same status minus a detail", it is
        /// a status whose <c>OnApply</c> AND <c>OnUnapply</c> both throw at <c>GetWeapon().Detached</c> — which
        /// is the 2026-08-06 client log verbatim, three times. Carrying it in the key means the plan can never
        /// call two differently-sourced statuses interchangeable, and both peers compute the name the same way
        /// from the same def. NAME, never guid: TFTV mints defs at runtime with <c>Guid.NewGuid()</c> (L130).
        /// A non-def source (an actor, a live equipment instance) is "" on EVERY peer, so it still matches
        /// itself and nothing regresses — it is simply not rebuildable, exactly as before.</summary>
        /// AND ITS TARGET, for the same reason and from the same evidence. <c>Status.Target</c> is a base-class
        /// field the game ALWAYS supplies when it applies a status —
        /// <c>TacticalActorBase.ApplyDamageInternal</c>:930-934 is the engine's own three-line rebuild recipe
        /// (<c>Source</c>, then <c>Target</c>, then <c>SetValue</c>, then <c>ApplyStatus</c>) and
        /// <c>StatusComponent.ApplyStatus</c>:180-184 is the same two assignments — and this rail was writing
        /// one of the three. A status whose lifecycle READS its target is then not "the same status minus a
        /// detail": <c>BleedStatus.OnApply</c>:133-135 is
        /// <c>GetSlot(GetTargetSlotName(Target))</c> -&gt; <c>GetSlotBleedValue(slot)</c>, so a null target is a
        /// null slot name, a null slot, and an NRE at <c>GetSlotBleedValue</c>'s first dereference. That is the
        /// 2026-08-07 client log verbatim, five times in eight minutes on <c>Bleed_StatusDef</c>, each one
        /// leaving the actor's bleed permanently different from the host's — and it is generic, not about
        /// bleeding: <c>TacStatus.GetTargetSlotsNames</c>:75-79 reads it too.
        ///
        /// SHAPED, NOT GUESSED. The game gives a target exactly two nameable shapes: a raw slot-name STRING
        /// (<c>AddStatusDamageKeywordData</c>:66 for bleed) and an <c>IDamageReceiver</c>
        /// (<c>ApplyStatusAbility.GetStatusTarget</c>:44-55 hands the item's <c>ParentSlot</c>; anything else
        /// is <c>data.Target</c>, the receiver). They are NOT interchangeable on arrival —
        /// <c>GetTargetSlotsNames</c> does <c>Target.ToString()</c>, which on an <c>ItemSlot</c> is a Unity
        /// object name and not a slot name — so the shape rides with the name: <c>s:</c> = string,
        /// <c>r:</c> = receiver, rebuilt through law L66c's own round trip
        /// (<c>TacticalActorKey.ResolveReceiver</c>, i.e. <c>CharacterBodyState.GetSlot</c>). A third shape is
        /// said out loud rather than shipped as null.
        ///
        /// WHAT STILL DOES NOT RIDE, deliberately: a status's own accumulated INTERNALS. <c>BleedStatus</c>
        /// merges every later application into the first instance (<c>OnApply</c>:146-154 —
        /// <c>AddBleedDamage</c> + <c>_slotNames.Add</c>), and only the FIRST slot is its <c>Target</c>. A
        /// bleed rebuilt from this rail therefore re-derives its magnitude from the one limb it names rather
        /// than the host's running total. That is a bounded number, not a null dereference, and closing it
        /// needs a per-status value channel this rail has no generic answer for.
        internal static string Key(string defName, int refKey, string sourceDefName, string target = "") =>
            refKey.ToString(CultureInfo.InvariantCulture) + "@" + (defName ?? "") + "|" + (sourceDefName ?? "") +
            "|" + (target ?? "");

        private static bool Split(string key, out string defName, out int refKey, out string sourceDefName,
                                  out string target)
        {
            defName = null; refKey = 0; sourceDefName = ""; target = "";
            if (string.IsNullOrEmpty(key)) return false;
            int at = key.IndexOf('@');
            if (at <= 0) return false;
            if (!int.TryParse(key.Substring(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out refKey))
                return false;
            string rest = key.Substring(at + 1);
            // Parsed from the RIGHT, so a def name that itself contains a bar still reads correctly.
            int bar = rest.LastIndexOf('|');
            if (bar < 0) { defName = rest; return defName.Length != 0; }   // pre-source key, still readable
            target = rest.Substring(bar + 1);
            rest = rest.Substring(0, bar);
            bar = rest.LastIndexOf('|');
            if (bar < 0) { defName = rest; sourceDefName = target; target = ""; return defName.Length != 0; }
            defName = rest.Substring(0, bar);
            sourceDefName = rest.Substring(bar + 1);
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
                string name; int refKey; string source; string target;
                Split(k, out name, out refKey, out source, out target);
                w.Write(name ?? "");
                w.Write(refKey);
                w.Write(source ?? "");
                w.Write(target ?? "");
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
                string source = r.ReadString();
                string target = r.ReadString();
                keys.Add(Key(name, refKey, source, target));
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
            string defName; int refKey; string sourceName; string targetTag;
            if (!Split(key, out defName, out refKey, out sourceName, out targetTag)) return;
            try
            {
                var def = Resolve(defName);
                if (def == null)
                {
                    Debug.LogError("[Multiplayer][tac] the host has status '" + defName + "' on " + actor.name +
                                   " and no def of that NAME resolves on this peer. A status def can be minted at " +
                                   "runtime (L130 is why this rides as a name and not a guid), so this is as likely " +
                                   "to be a def another mod made only on the host as it is a def-set difference — " +
                                   "nothing here can tell those apart. That actor's state stays different here.");
                    return;
                }
                var repo = GameUtl.GameComponent<DefRepository>();
                if (repo == null) return;
                var status = repo.Instantiate<Status>(def);
                if (status == null) return;
                if (refKey != 0 && !SetActorRef(status, tlc, refKey, actor, defName)) return;
                // THE SOURCE, BEFORE ApplyStatus AND NOT AFTER — StatusComponent.ApplyStatus runs OnApply
                // synchronously, and OnApply is the first thing that reads it. Missing, OverwatchStatus
                // .OnApply:38 NREs at GetWeapon().Detached (client log, 22:31:53 / 22:32:45 / 22:32:48), and
                // the SAME null then kills OnUnapply:53 one line before SetCone(null) — so the red cone can
                // never be torn down on the peer that received the status through this rail. Generic: any
                // status whose lifecycle reads its own source is repaired by this, none of them named here.
                if (!string.IsNullOrEmpty(sourceName))
                {
                    var source = ResolveAnyDef(sourceName);
                    if (source == null)
                        Debug.LogWarning("[Multiplayer][tac] the host's status '" + defName + "' on " + actor.name +
                                         " was applied from def '" + sourceName + "', which does not exist on this " +
                                         "peer. It is applied without one — a status that reads its source " +
                                         "(OverwatchStatus.GetWeapon) will throw and leave its visuals behind.");
                    else status.Source = source;
                }
                // THE TARGET, BEFORE ApplyStatus AND FOR THE SAME REASON AS THE SOURCE — OnApply runs
                // synchronously inside it and BleedStatus.OnApply:133-135 dereferences the slot this names on
                // its very first statement. A target the host had and this peer cannot rebuild is a REFUSAL,
                // not a null: applying the status anyway is precisely the NRE this line exists to stop.
                if (!SetTarget(status, actor, targetTag, defName)) return;
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
            var source = s.Source as BaseDef;
            return Key(def.name, RefKeyOf(s), source == null ? "" : source.name, TargetTag(s));
        }

        /// <summary>The host's <c>Status.Target</c> as something the other peer can rebuild: its SHAPE and its
        /// slot name. Both shapes name a slot the same way — a string IS the name
        /// (<c>AddStatusDamageKeywordData</c>:66) and <c>IDamageReceiver.GetSlotName</c> is law L66c's own
        /// identity string — so the two peers derive it identically from a def field. Anything else is left
        /// out LOUDLY rather than shipped as an empty target that would rebuild into the null this whole
        /// field exists to stop.</summary>
        private static string TargetTag(Status s)
        {
            var t = s == null ? null : s.Target;
            if (t == null) return "";
            // AN EffectTarget IS A WRAPPER, NOT A SHAPE OF ITS OWN. Base.Entities.Effects.EffectTarget:5-13 is
            // `object Object` plus a position, and the thing inside it is usually one of the two shapes below —
            // so refusing the wrapper threw away an address that was right there. GameObject is the third form
            // it holds (the property is literally `Object as GameObject`), and the actor on it is an
            // IDamageReceiver like any other.
            var wrapper = t as Base.Entities.Effects.EffectTarget;
            if (wrapper != null)
            {
                t = wrapper.Object;
                var go = t as GameObject;
                if (go != null) t = go.GetComponent<TacticalActorBase>();
                if (t == null) return "";
            }
            var text = t as string;
            if (text != null) return "s:" + text;
            var receiver = t as IDamageReceiver;
            // SlotOf, not GetSlotName(): TacticalItem.GetSlotName:634-637 is hardcoded "", so the naive call
            // shipped a body-part status as "r:" and the far side rebuilt it onto the WHOLE ACTOR.
            if (receiver != null) return "r:" + (TacticalActorKey.SlotOf(receiver) ?? "");
            string type = t.GetType().Name;
            if (_said.Add("target-" + type))
                Debug.LogWarning("[Multiplayer][tac] a status is targeted at a " + type + ", which is neither a slot " +
                                 "name nor an IDamageReceiver and so has no shared address. It rides with NO target, " +
                                 "and any status whose OnApply reads one will behave differently on every other peer.");
            return "";
        }

        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Put the host's target back on a rebuilt status, or refuse the rebuild. The receiver shape
        /// goes through <c>TacticalActorKey.ResolveReceiver</c> — the SAME round trip the damage rail already
        /// uses for a body part (law L66c), so "" is the actor itself and anything else must resolve to a real
        /// slot or it is a named failure, never a fallback to null.</summary>
        private static bool SetTarget(Status status, TacticalActorBase actor, string tag, string defName)
        {
            if (string.IsNullOrEmpty(tag)) return true;
            string name = tag.Length > 2 ? tag.Substring(2) : "";
            if (tag[0] == 's') { status.Target = name; return true; }
            string why;
            var receiver = TacticalActorKey.ResolveReceiver(actor, name, out why);
            if (receiver == null)
            {
                Debug.LogError("[Multiplayer][tac] the host's status '" + defName + "' on " +
                               TacticalActorLifecycle.SafeName(actor) + " is targeted at body part '" + name +
                               "' and " + why + " — it is NOT applied here, because a status rebuilt without the " +
                               "target its own OnApply dereferences throws instead of applying. That actor's state " +
                               "stays different on this peer.");
                return false;
            }
            status.Target = receiver;
            return true;
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

        /// <summary>Any def by NAME — the source a status was applied from is not a <c>StatusDef</c> (it is a
        /// <c>WeaponDef</c> for overwatch, and whatever a mod chose for a modded status), so this walks the
        /// whole repository rather than one def family. Cached with the same rebuild-on-miss rule as
        /// <see cref="Resolve"/>, so a runtime-minted def resolves on its first use.</summary>
        private static BaseDef ResolveAnyDef(string name)
        {
            BaseDef def;
            if (_anyByName.TryGetValue(name, out def) && def != null) return def;
            var repo = GameUtl.GameComponent<DefRepository>();
            if (repo == null) return null;
            _anyByName.Clear();
            foreach (var d in repo.GetAllDefs<BaseDef>())
                if (d != null && !string.IsNullOrEmpty(d.name)) _anyByName[d.name] = d;
            _anyByName.TryGetValue(name, out def);
            return def;
        }

        private static readonly Dictionary<string, BaseDef> _anyByName =
            new Dictionary<string, BaseDef>(StringComparer.Ordinal);

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

    /// <summary>
    /// A STATUS CHANGE IS A STATE CHANGE, SO IT SETTLES LIKE ONE (postulate 1).
    ///
    /// THE HOLE, and it is generic rather than about any one visual. The status set rides only on the 0x82
    /// settle, and <c>TacticalCommandSync.OnAbilityActionEnded</c> emits one only for a declared RIDER —
    /// i.e. for an ORDER. Everything else that changes a status changed nothing on any other peer until the
    /// next turn-edge sweep: a REACTION (the game's overwatch shot is
    /// <c>TacticalLevelController.ExecuteOverwatch</c>:1353-1391, driven by <c>TriggerOverwatch</c> off the
    /// victim's navigation and never an order), an effect, an expiry, a status a mod removes.
    ///
    /// AND THE RECEIVING PEER CANNOT RUN THAT TEARDOWN ITSELF, by design: law L83 blocks a client's own
    /// reaction ("a local Overwatch reaction was BLOCKED here", client log 22:31:49) because raising it would
    /// be a second shot from one actor. So the client never reaches :1381 <c>SetConeVisualsMode(false,false)</c>
    /// nor :1387 <c>UnapplyStatus(overwatch)</c> — the two lines that destroy the red cone
    /// (<c>OverwatchStatus.OnUnapply</c>:53 → <c>SetCone(null)</c> → <c>Destroy(_coneGameObject)</c>:96-102).
    /// The shot arrived, the damage arrived, and the cone hung in the air for the rest of the battle. That is
    /// the whole report, and it is not an overwatch bug: it is every visual owned by a status the host removed
    /// outside an order.
    ///
    /// ONE SEAM, ON THE GAME'S OWN WRITERS — and specifically on <c>CallApplyStatus</c>:219 /
    /// <c>CallUnapplyStatus</c>:227, not on the public <c>ApplyStatus</c>/<c>UnapplyStatus</c> above them.
    /// Those two private calls are where <c>OnApply</c>/<c>OnUnapply</c> actually run, and they are the ONLY
    /// funnel: the public <c>UnapplyStatus</c>:192 may merely queue into <c>_statusesToRemove</c> and let
    /// <c>ApplyStatus</c>:249 do the removal later, and a TIMED status expires straight out of
    /// <c>UpdateStatuses</c>:256-262 without touching either public method. Patching the pair catches every
    /// route, and the mod's own <c>Reconcile</c> is scoped out on the receiving side, so no per-status code
    /// and no ability is named anywhere in the seam (postulate 3). The receiving peer's repaint is then the
    /// game's own: <see cref="TacticalStatusSet.Reconcile"/> calls <c>comp.UnapplyStatus</c>, whose
    /// <c>OnUnapply</c> destroys the visual in that same frame.
    ///
    /// BOUNDED: host-only, never inside an apply scope, and only while a turn is actually playing
    /// (<c>TacticalLevelController.TurnIsPlaying</c>:251 — <c>_nextTurnUpdateable != null</c>), which keeps
    /// mission setup's hundreds of initial status applications off the wire. <c>HostSettle</c> itself drops
    /// anything unkeyed or not a <c>TacticalActor</c>.
    /// </summary>
    [HarmonyPatch]
    internal static class StatusChangeSettlesTheActor
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(StatusComponent), "CallApplyStatus", new[] { typeof(Status) });
            yield return AccessTools.Method(typeof(StatusComponent), "CallUnapplyStatus", new[] { typeof(Status) });
        }

        private static void Postfix(StatusComponent __instance)
        {
            try
            {
                if (SyncApplyScope.Active) return;               // law 8: this change IS a mirror being applied
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
                var tac = __instance as TacStatusComponent;
                var actor = tac == null ? null : tac.TacticalActorBase;
                if (actor == null) return;
                var level = GameUtl.CurrentLevel();
                var tlc = level == null ? null : level.GetComponent<TacticalLevelController>();
                if (ReferenceEquals(tlc, null) || !tlc.TurnIsPlaying) return;   // L113
                TacticalCommandSync.HostSettle(actor);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][tac] a status change could not be settled: " + ex.Message +
                                 ". Every other peer keeps the status set it already had until the next " +
                                 "turn-edge sweep — a visual the host has just torn down stays on their screen.");
            }
        }
    }
}
