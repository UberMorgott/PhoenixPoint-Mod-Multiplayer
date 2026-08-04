using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Base.Core;
using Base.Defs;
using Base.Entities.Effects;
using Base.Entities.Statuses;
using Base.Levels;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Effects.DamageTypes;
using PhoenixPoint.Tactical.Entities.Equipments;
using PhoenixPoint.Tactical.Entities.Statuses;
using PhoenixPoint.Tactical.Levels;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// "This damage is the HOST'S, already resolved, and is being re-applied verbatim." Narrower than
    /// <see cref="SyncApplyScope"/> on purpose: that one means "a delta apply is somewhere on the stack" and
    /// is true for whole mirrored animations, while this one wraps exactly the ONE native
    /// <c>ApplyDamage</c> call whose numbers must not be touched again by anybody. Everything that would
    /// RE-DERIVE a shipped number keys off this and stands down (law L66b): the foreign
    /// <c>ref DamageResult</c> mutators, the nested return-melee, the loot roll.
    /// Game code is main-thread only, so a plain depth counter suffices.
    /// </summary>
    internal static class MirrorApplyScope
    {
        private static int _depth;
        internal static bool Active => _depth > 0;
        internal static IDisposable Enter() => new Scope();
        internal static void Reset() => _depth = 0;

        private sealed class Scope : IDisposable
        {
            public Scope() { _depth++; }
            public void Dispose() { _depth--; }
        }
    }

    /// <summary>
    /// THE <c>DamageResult</c> CODEC — the same EXPLICIT DECLARED FIELD SET discipline as
    /// <see cref="TacAbilityTargetCodec"/>, for the same reason: the struct holds live references
    /// (<c>Source</c> is an <c>object</c>, <c>ImpactHit.Collider</c> is a Unity <c>Collider</c>,
    /// <c>StatusApplication.StatusSource/StatusTarget</c> are <c>object</c>s) so a reflective walk would
    /// serialize a scene graph or silently write nulls. Every public instance field of <c>DamageResult</c> is
    /// named in exactly ONE of <see cref="Rides"/> / <see cref="Dropped"/>; a field ADDED by a patch or by a
    /// game update lands in neither and turns RailCheck RED (L66-codec).
    ///
    /// THE REDUCTION, stated because it is the one place a field rides in a lossy form: <c>Source</c> is an
    /// arbitrary object (a <c>Weapon</c>, a <c>TacticalAbility</c>, a <c>Status</c>) and rides as the SOURCE
    /// ACTOR's key. That is what the post-apply readers actually need —
    /// <c>TacUtil.GetSourceTacticalActorBase</c> drives contribution, aggro and KILL CREDIT
    /// (<c>TacticalActorBase.ApplyDamageInternal</c>:863-881). What it loses is
    /// <c>DamageResult.GetDamagePayload()</c>, whose only in-apply reader is
    /// <c>TacticalReturnMeleeDamage.Activate</c>:26-27 — which never runs on a mirror at all (it is the
    /// nested re-roll leak, stood down by <see cref="MirrorApplyScope"/>), so nothing on a mirror can observe
    /// the difference.
    /// </summary>
    internal static class DamageResultCodec
    {
        internal const ushort BitDamageTypeDef = 1 << 0;
        internal const ushort BitRelatedDamageTypeDefs = 1 << 1;
        internal const ushort BitApplyStatuses = 1 << 2;
        internal const ushort BitStatModifications = 1 << 3;
        internal const ushort BitActorEffects = 1 << 4;
        internal const ushort BitHealthDamage = 1 << 5;
        internal const ushort BitArmorDamage = 1 << 6;
        internal const ushort BitArmorMitigatedDamage = 1 << 7;
        internal const ushort BitStunValue = 1 << 8;
        internal const ushort BitHealValue = 1 << 9;
        internal const ushort BitImpactForce = 1 << 10;
        internal const ushort BitImpactHit = 1 << 11;
        internal const ushort BitDamageOrigin = 1 << 12;
        internal const ushort BitSource = 1 << 13;
        internal const ushort BitForceHurt = 1 << 14;

        internal const ushort KnownBits = BitDamageTypeDef | BitRelatedDamageTypeDefs | BitApplyStatuses |
                                          BitStatModifications | BitActorEffects | BitHealthDamage |
                                          BitArmorDamage | BitArmorMitigatedDamage | BitStunValue |
                                          BitHealValue | BitImpactForce | BitImpactHit | BitDamageOrigin |
                                          BitSource | BitForceHurt;

        /// <summary>Every field rides. There is no "the peer can recompute this one": the whole point of the
        /// arc is that NOTHING downstream of the host recomputes damage (law L66a).</summary>
        internal static readonly string[] Rides =
        {
            "DamageTypeDef", "RelatedDamageTypeDefs", "ApplyStatuses", "StatModifications", "ActorEffects",
            "HealthDamage", "ArmorDamage", "ArmorMitigatedDamage", "StunValue", "HealValue", "ImpactForce",
            "ImpactHit", "DamageOrigin", "Source", "forceHurt",
        };

        /// <summary>Deliberately empty — and that is the assertion, not an oversight. If this ever gains an
        /// entry, some part of a resolved hit stopped crossing and a peer started making it up again.</summary>
        internal static readonly Dictionary<string, string> Dropped = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void Write(BinaryWriter w, DamageResult d)
        {
            ushort mask = 0;
            if (d.DamageTypeDef != null) mask |= BitDamageTypeDef;
            if (d.RelatedDamageTypeDefs != null) mask |= BitRelatedDamageTypeDefs;
            if (d.ApplyStatuses != null) mask |= BitApplyStatuses;
            if (d.StatModifications != null) mask |= BitStatModifications;
            if (d.ActorEffects != null) mask |= BitActorEffects;
            if (d.HealthDamage != 0f) mask |= BitHealthDamage;
            if (d.ArmorDamage != 0f) mask |= BitArmorDamage;
            if (d.ArmorMitigatedDamage != 0f) mask |= BitArmorMitigatedDamage;
            if (d.StunValue != 0f) mask |= BitStunValue;
            if (d.HealValue != 0f) mask |= BitHealValue;
            if (d.ImpactForce != Vector3.zero) mask |= BitImpactForce;
            if (d.ImpactHit.Point != Vector3.zero || d.ImpactHit.Normal != Vector3.zero) mask |= BitImpactHit;
            if (d.DamageOrigin != Vector3.zero) mask |= BitDamageOrigin;
            if (d.Source != null) mask |= BitSource;
            if (d.forceHurt) mask |= BitForceHurt;
            w.Write(mask);

            if ((mask & BitDamageTypeDef) != 0) w.Write(Guid(d.DamageTypeDef));
            if ((mask & BitRelatedDamageTypeDefs) != 0)
            {
                w.Write(d.RelatedDamageTypeDefs.Count);
                foreach (var def in d.RelatedDamageTypeDefs) w.Write(Guid(def));
            }
            if ((mask & BitApplyStatuses) != 0)
            {
                w.Write(d.ApplyStatuses.Count);
                foreach (var s in d.ApplyStatuses)
                {
                    w.Write(Guid(s.StatusDef));
                    w.Write(s.Value);
                    w.Write(TacticalActorKey.Of(s.StatusSource as TacticalActorBase));
                    w.Write(SlotNameOf(s.StatusTarget));
                    // THE STATUS-DURATION RE-ROLL, resolved rather than re-rolled (law L66e).
                    // CooldownStatus.OnApply:13 does DurationTurns = Random.Range(Min, Max+1) — a fresh draw
                    // per instantiation, and ApplyDamageInternal:930 instantiates every shipped status. It
                    // writes the DEF, and TacStatus.DurationInTurns:37 reads the def LIVE, so the host's
                    // number can simply be restored after the fact (CooldownDurationMirror).
                    var cd = s.StatusDef as CooldownStatusDef;
                    w.Write(cd == null ? -1 : cd.DurationTurns);
                }
            }
            if ((mask & BitStatModifications) != 0)
            {
                w.Write(d.StatModifications.Count);
                foreach (var m in d.StatModifications)
                {
                    w.Write((int)m.Modification);
                    w.Write(m.StatName ?? "");
                    w.Write(m.Value);
                    w.Write(m.GetApplicationValue());
                }
            }
            if ((mask & BitActorEffects) != 0)
            {
                w.Write(d.ActorEffects.Count);
                foreach (var e in d.ActorEffects) w.Write(Guid(e));
            }
            if ((mask & BitHealthDamage) != 0) w.Write(d.HealthDamage);
            if ((mask & BitArmorDamage) != 0) w.Write(d.ArmorDamage);
            if ((mask & BitArmorMitigatedDamage) != 0) w.Write(d.ArmorMitigatedDamage);
            if ((mask & BitStunValue) != 0) w.Write(d.StunValue);
            if ((mask & BitHealValue) != 0) w.Write(d.HealValue);
            if ((mask & BitImpactForce) != 0) WriteVec(w, d.ImpactForce);
            if ((mask & BitImpactHit) != 0) { WriteVec(w, d.ImpactHit.Point); WriteVec(w, d.ImpactHit.Normal); }
            if ((mask & BitDamageOrigin) != 0) WriteVec(w, d.DamageOrigin);
            if ((mask & BitSource) != 0) w.Write(TacticalActorKey.Of(TacUtil.GetSourceTacticalActorBase(d.Source)));
        }

        internal static DamageResult Read(BinaryReader r, TacticalLevelController tlc, List<string> notes)
        {
            ushort mask = r.ReadUInt16();
            if ((mask & ~KnownBits) != 0)
                throw new InvalidDataException("damage-result mask 0x" + mask.ToString("X4") + " declares field bits " +
                                               "this build cannot decode (known 0x" + KnownBits.ToString("X4") + ") — " +
                                               "the payload after it is misaligned and must not be guessed at");
            var d = new DamageResult();
            if ((mask & BitDamageTypeDef) != 0) d.DamageTypeDef = Def<DamageTypeBaseEffectDef>(r.ReadString(), "DamageTypeDef", notes);
            if ((mask & BitRelatedDamageTypeDefs) != 0)
            {
                int n = r.ReadInt32();
                d.RelatedDamageTypeDefs = new List<DamageTypeBaseEffectDef>(n);
                for (int i = 0; i < n; i++) d.RelatedDamageTypeDefs.Add(Def<DamageTypeBaseEffectDef>(r.ReadString(), "RelatedDamageTypeDef", notes));
            }
            if ((mask & BitApplyStatuses) != 0)
            {
                int n = r.ReadInt32();
                d.ApplyStatuses = new List<StatusApplication>(n);
                for (int i = 0; i < n; i++)
                {
                    var def = Def<StatusDef>(r.ReadString(), "StatusDef", notes);
                    float value = r.ReadSingle();
                    int srcKey = r.ReadInt32();
                    string targetSlot = r.ReadString();
                    int cooldownTurns = r.ReadInt32();
                    string ignore;
                    d.ApplyStatuses.Add(new StatusApplication
                    {
                        StatusDef = def,
                        Value = value,
                        StatusSource = srcKey == 0 ? null : TacticalActorKey.Resolve(tlc, srcKey, out ignore),
                        StatusTarget = string.IsNullOrEmpty(targetSlot) ? null : (object)targetSlot,
                    });
                    if (cooldownTurns >= 0) CooldownDurationMirror.Declare(def, cooldownTurns);
                }
            }
            if ((mask & BitStatModifications) != 0)
            {
                int n = r.ReadInt32();
                d.StatModifications = new List<StatModification>(n);
                for (int i = 0; i < n; i++)
                {
                    var type = (StatModificationType)r.ReadInt32();
                    string name = r.ReadString();
                    float value = r.ReadSingle();
                    float applied = r.ReadSingle();
                    d.StatModifications.Add(new StatModification(type, name, value, null, applied));
                }
            }
            if ((mask & BitActorEffects) != 0)
            {
                int n = r.ReadInt32();
                d.ActorEffects = new List<EffectDef>(n);
                for (int i = 0; i < n; i++) d.ActorEffects.Add(Def<EffectDef>(r.ReadString(), "ActorEffect", notes));
            }
            if ((mask & BitHealthDamage) != 0) d.HealthDamage = r.ReadSingle();
            if ((mask & BitArmorDamage) != 0) d.ArmorDamage = r.ReadSingle();
            if ((mask & BitArmorMitigatedDamage) != 0) d.ArmorMitigatedDamage = r.ReadSingle();
            if ((mask & BitStunValue) != 0) d.StunValue = r.ReadSingle();
            if ((mask & BitHealValue) != 0) d.HealValue = r.ReadSingle();
            if ((mask & BitImpactForce) != 0) d.ImpactForce = ReadVec(r);
            if ((mask & BitImpactHit) != 0)
            {
                // Collider is deliberately left null: it is a live Unity component, and every in-apply reader
                // of ImpactHit uses Point/Normal only (ApplyDamageInternal:852 logging, the hit visuals).
                var hit = default(CastHit);
                hit.Point = ReadVec(r);
                hit.Normal = ReadVec(r);
                d.ImpactHit = hit;
            }
            if ((mask & BitDamageOrigin) != 0) d.DamageOrigin = ReadVec(r);
            if ((mask & BitSource) != 0)
            {
                int key = r.ReadInt32();
                string why;
                var src = key == 0 ? null : TacticalActorKey.Resolve(tlc, key, out why);
                if (src == null && key != 0) notes.Add("damage Source actor (key " + key + ") does not resolve here — " +
                                                       "the kill will be credited to nobody on this screen");
                d.Source = src;
            }
            d.forceHurt = (mask & BitForceHurt) != 0;
            return d;
        }

        /// <summary><c>StatusApplication.StatusTarget</c> is an untyped object that the game only ever fills
        /// with a body-part NAME or the slot that owns it — <c>AddStatusDamageKeywordData</c>:66 writes
        /// <c>data.Target.GetSlotName()</c> and <c>BleedStatus</c>:192 reads
        /// <c>(target as ItemSlot)?.GetSlotName() ?? (string)target</c>, i.e. the string form is the one both
        /// sides of the game accept. Anything else is not shipped and says so once.</summary>
        private static string SlotNameOf(object statusTarget)
        {
            if (statusTarget == null) return "";
            var s = statusTarget as string;
            if (s != null) return s;
            var slot = statusTarget as ItemSlot;
            if (slot != null) return slot.GetSlotName();
            var recv = statusTarget as IDamageReceiver;
            if (recv != null) return recv.GetSlotName() ?? "";
            TacticalDamageSync.SayOnce("status-target-" + statusTarget.GetType().Name,
                "[Multiplayer][tac] a shipped status carries a StatusTarget of type " + statusTarget.GetType().Name +
                ", which is neither a slot name nor a damage receiver — it rides as 'no target' and any effect " +
                "that keyed off it will differ on the other peers.");
            return "";
        }

        private static string Guid(BaseDef def) => def == null ? "" : def.Guid;

        private static T Def<T>(string guid, string what, List<string> notes) where T : BaseDef
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var def = GameUtl.GameComponent<DefRepository>()?.GetDef(guid) as T;
            if (def == null && notes != null)
                notes.Add(what + " guid " + guid + " resolves to no " + typeof(T).Name + " on this peer (mod " +
                          "parity should have made that impossible, law 10)");
            return def;
        }

        private static void WriteVec(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVec(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    /// <summary>
    /// TACTICAL ARC A3b — ATTACKS AND THEIR OUTCOMES: <see cref="SurfaceIds.TacResult"/> (0x84).
    ///
    /// "SHIP THE RESULT, NEVER THE ROLL" IS NOT A PREFERENCE HERE, IT IS THE ONLY OPTION. A battle has no
    /// shared and no seedable generator: scatter (<c>Weapon</c>:481/487/490), the damage roll and shred
    /// (<c>DamageEffectDef</c>:52/57), the fumble (<c>TacticalAbility</c>:1128) and jet-jump all draw from the
    /// GLOBAL unseeded <c>UnityEngine.Random</c>, while loot and spawns draw from
    /// <c>SharedData.Random = new System.Random()</c> (<c>SharedData</c>:69); <c>Random.InitState</c> is used
    /// only for map generation. And a shot has NO hit/miss die at all — the outcome is a scattered trajectory
    /// plus a physics raycast (<c>Weapon.SpreadInCone</c>/<c>SpreadInRadius</c>), so two peers rolling
    /// independently would hit DIFFERENT TARGETS, not merely different numbers.
    ///
    /// THE THREE HALVES:
    ///   • CAPTURE (host) — a postfix on <c>TacticalActorBase.ApplyDamageInternal</c> and on
    ///     <c>ItemSlot.ApplyDamage</c>. The Internal one and not the public <c>ApplyDamage</c>, because the
    ///     <c>ref DamageResult</c> mutators other mods install (TFTV's acid resistance, Die Hard) sit on
    ///     Internal and mutate a struct that <c>ApplyDamage</c> passed BY VALUE — a capture on the public
    ///     method would ship a number the host itself never applied.
    ///   • NEUTER (every non-authoritative peer) — <c>DamageAccumulation.ApplyAddedDamage</c>:550 is the ONE
    ///     funnel where computed damage becomes real (everything before it only fills <c>_targetsData</c>), so
    ///     a prefix there deletes client-side damage computation wholesale, exactly as the mandate requires
    ///     and NOT at <c>ApplyDamage</c>. Two belts sit under it — <c>TacticalActorBase.ApplyDamage</c> and
    ///     <c>ItemSlot.ApplyDamage</c> — because damage-over-time statuses tick on a client's own turn edges
    ///     and reach <c>ApplyDamage</c> without ever passing through an accumulation, which would double every
    ///     poison/bleed/fire tick against the host's own shipped one.
    ///   • APPLY (mirror) — resolve the receiver, run the game's OWN <c>ApplyDamage</c> with the shipped
    ///     struct inside <see cref="MirrorApplyScope"/>, then OVERWRITE hp/ap/wp and the receiver's own
    ///     hp/armor from the host's snapshot. The overwrite is also the DETECTOR: a non-zero correction means
    ///     something re-derived a shipped number, and it is logged as such instead of quietly healing.
    ///
    /// THE GAP. Every other surface in this repo can heal a lost message by re-emitting current state; this
    /// one cannot, because it is a stream of discrete EVENTS — a lost hit is a soldier who is permanently 30
    /// HP too healthy. <see cref="SurfaceSeq"/> only dedups. So the client additionally checks CONTIGUITY and,
    /// on a hole, says so loudly and asks the host for a full resnapshot over the 0x83 intent surface
    /// (op <see cref="OpIntentResnap"/>) — no new surface, and the request rides the same dedup/reject
    /// plumbing every other intent does.
    /// </summary>
    public static class TacticalDamageSync
    {
        private const byte OpDamage = 1;
        private const byte OpResnap = 2;
        /// <summary>A4 — the actor LIFECYCLE ops. They ride 0x84 rather than a surface of their own because
        /// this is already the discrete-event stream with the contiguity check and the resnapshot recovery,
        /// and because ONE seq stream is the only thing that can stop a damage record from overtaking the
        /// spawn of the actor it names (the same argument that puts turn+end on 0x80).</summary>
        internal const byte OpSpawn = 3;
        internal const byte OpDeath = 4;
        /// <summary>A6 — the two remaining tactical facts, on the SAME stream and for A4's reason. An
        /// inventory hand-off must not be overtaken by the shot fired with the handed weapon, and a wall
        /// must not be removed after the grenade record that removed it. 0x85 stays free.</summary>
        internal const byte OpInventory = 5;
        internal const byte OpEnvDamage = 6;
        /// <summary>Op 2 on <see cref="SurfaceIds.TacCommandIntent"/> (op 1 is the command). Registered by
        /// <c>TacticalCommandSync.RegisterIntents</c> — one family, one op table.</summary>
        internal const byte OpIntentResnap = 2;

        private static readonly SurfaceSeq Seq = new SurfaceSeq();
        /// <summary>CLIENT: the last CONTIGUOUS seq applied. Separate from <see cref="SurfaceSeq"/>'s
        /// last-writer-wins mark because they answer different questions — that one asks "is this stale?",
        /// this one asks "did I miss one?", and on an event stream only the second can be answered by
        /// noticing a hole.</summary>
        private static uint _lastContiguous;
        private static bool _resnapRequested;
        private static bool _resnapPending;
        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>CLIENT: how many NATIVE stat events this peer has replayed off the rail (law L105).
        ///
        /// THE SNAPSHOT AND THE EVENT RACE, AND THE SNAPSHOT USED TO WIN. A settle is a snapshot of one
        /// actor's pos+AP+WP taken on the host at <c>ClearPlayingAction</c> — which fires BEFORE that
        /// action's own damage resolves, measured a full second before it: host log 2026-08-05,
        /// <c>settle Soldier_4 ap=2,6 wp=8 seq=231</c> @00:38:04.036 → <c>damage Ironbar hp-196 seq=104</c>
        /// @00:38:04.953 → <c>settle Soldier_4 ap=2,6 wp=10 seq=233</c> @00:38:05.545. The +2 between the
        /// two settles is the kill bonus: <c>TacticalActor.OnAnotherActorDeath</c>:1849-1865 adds the
        /// victim's <c>WillPointWorth</c> to its KILLER and subtracts it from every one of the victim's
        /// faction-mates, synchronously inside <c>Health.Set(0)</c> → <c>OnHealthChange</c>:616-622 →
        /// <c>Die</c>, on EVERY peer separately. The host therefore shows one clean +2 — it never applies
        /// its own settles. A client holds settle #231 behind its mirrored ability
        /// (<c>TacticalCommandSync.ClientTick</c>), applies the death from 0x84 in the meantime (+2), then
        /// drains the held PRE-kill snapshot (-2) and only then gets #233 (+2). That is the reported
        /// +2/-2/+2 verbatim, and it is an ORDERING defect, not a paint one.
        ///
        /// So every stat snapshot that crosses the wire is stamped with this counter when it ARRIVES, and one
        /// that drains against a higher counter is older than the board. Dropping its stat write loses
        /// nothing: this peer replayed the SAME native grant the host did, so the fresher numbers are
        /// already here.
        /// ponytail: one global counter, not per actor — a death moves an unbounded set of actors (killer
        /// plus every faction-mate) and the counter is the cheap superset. Cost is an unrelated actor's
        /// settle losing its stat write across a death; it says so out loud and the next settle re-asserts.
        /// Per-actor sets only if a live run shows AP corrections actually being starved.</summary>
        private static int _statEpoch;

        /// <summary>The stamp to put on a snapshot that arrives NOW.</summary>
        internal static int StatEpoch => _statEpoch;

        /// <summary>A host fact whose native replay rewrote stats the record itself does not name just
        /// landed. Only a DEATH qualifies — an ordinary hit's victim has its own AP/WP overwritten from the
        /// host's snapshot two lines below the apply.</summary>
        internal static void NoteNativeStatEvent() => ++_statEpoch;

        /// <summary>Was <paramref name="epoch"/> stamped before the last native stat event this peer
        /// replayed? Then that snapshot's stat values lost the race and must not be written. FALSE is the
        /// ordinary answer: this is a race guard, not a blanket refusal, and a settle that crossed no death
        /// still carries the host's authoritative AP (law L98).
        /// ponytail: the stamp is taken at ARRIVAL, not at the host's capture, so this catches an event
        /// replayed between arrival and drain — the measured case, and the only one a single ordered
        /// transport can produce, since both surfaces ride one connection in send order. A death that
        /// genuinely overtook the settle it precedes would still slip through; that needs a host-side count
        /// in the settle payload, and no wire change is worth it until one is seen.</summary>
        internal static bool StatsAreStale(int epoch) => epoch != _statEpoch;

        internal static void Reset()
        {
            Seq.Reset();
            _lastContiguous = 0;
            _statEpoch = 0;
            _resnapRequested = false;
            _resnapPending = false;
            _said.Clear();
            MirrorApplyScope.Reset();
            CooldownDurationMirror.Reset();
            TacticalActorLifecycle.Reset();   // A4: pending deaths, loot memos and the spawn-apply depth
            TacticalInventorySync.Reset();    // A6: the observed AP charge waiting for its batch
            TacticalDestruction.Reset();      // A6: the previous battle's destructible index
        }

        internal static void SayOnce(string key, string message)
        {
            if (_said.Add(key)) Debug.LogError(message);
        }

        internal static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

        internal static NetworkEngine LiveEngine()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;
            var coord = engine.SaveTransfer;
            return coord != null && coord.SessionStarted ? engine : null;
        }

        /// <summary>TRUE when this peer must not compute damage of its own: a live co-op client, outside a
        /// mirror apply. The ONE predicate every neuter arm consults, so "which peer decides damage" is a
        /// single readable answer rather than five copies of a condition that can drift apart.</summary>
        internal static bool DamageIsSomebodyElses()
        {
            if (MirrorApplyScope.Active) return false;   // this IS the host's damage being re-applied
            var engine = LiveEngine();
            return engine != null && !engine.IsHost;
        }

        // ─── HOST: capture ─────────────────────────────────────────────────

        /// <summary>Ship one resolved hit. <paramref name="receiver"/> is the thing the game just wrote to;
        /// its slot name plus its actor's key is the whole address (law L66c).</summary>
        internal static void OnDamageApplied(IDamageReceiver receiver, DamageResult result)
        {
            var engine = LiveEngine();
            if (engine == null || !engine.IsHost) return;
            if (MirrorApplyScope.Active) return;          // never re-ship what we are re-applying
            if (receiver == null) return;
            var actor = receiver.GetActor();
            int key = TacticalActorKey.Of(actor);
            if (key == 0)
            {
                SayOnce("unkeyed-" + (actor == null ? "<null>" : actor.name),
                    "[Multiplayer][tac] damage to " + (actor == null ? "an ownerless receiver" : actor.name) +
                    " is NOT relayed — it has no shared key (no GeoTacUnitId, no derived battle key and no " +
                    "host-assigned spawn key, which A4 mints for every mid-battle spawn). That actor's health " +
                    "will differ on every other screen for the rest of the mission.");
                return;
            }
            string slot = TacticalActorKey.SlotOf(receiver) ?? "";
            var stats = (actor as TacticalActor)?.CharacterStats;
            // A4: if this hit KILLED, the corpse manifest the host just pre-rolled rides with it. It has to
            // ride HERE and not behind: the mirror's own death starts the instant it applies this very
            // DamageResult, and DieAbility.DropItems asks its questions inside that same synchronous chain —
            // no later message could possibly get there first (the same trap the fumble pre-roll exists for).
            var loot = actor.IsDead ? TacticalActorLifecycle.TakePendingDeath(key) : null;
            Send(OpDamage,
                 "damage " + actor.name + (slot.Length == 0 ? "" : "/" + slot) + " hp-" + result.HealthDamage.ToString("0.##") +
                 " arm-" + result.ArmorDamage.ToString("0.##"),
                 w =>
                 {
                     w.Write(key);
                     w.Write(slot);
                     DamageResultCodec.Write(w, result);
                     w.Write(Stat(receiver.GetHealth()));
                     w.Write(Stat(receiver.GetArmor()));
                     w.Write(Stat(actor.Health));
                     w.Write(stats == null ? float.NaN : (float)stats.ActionPoints);
                     w.Write(stats == null ? float.NaN : (float)stats.WillPoints);
                     w.Write((byte)(actor.IsDead ? 1 : 0));
                     if (actor.IsDead) TacticalActorLifecycle.WriteLoot(w, loot);
                 });
        }

        internal static float Stat(BaseStat s) => s == null ? float.NaN : (float)s;

        internal static void Send(byte op, string what, Action<BinaryWriter> writeBody)
        {
            var engine = NetworkEngine.Instance;
            try
            {
                uint seq = Seq.Next(SurfaceIds.TacResult);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    w.Write(op);
                    writeBody(w);
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.TacResult, SyncKind.StateDelta, inner)));
                Debug.Log("[Multiplayer][tac] HOST " + what + " seq=" + seq);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] HOST " + what + " FAILED to reach the wire — every other peer " +
                               "now has a different battlefield, and this surface has no re-emit to heal it: " + ex);
            }
        }

        // ─── HOST: the resnapshot, the only recovery a discrete stream has ──

        /// <summary>Op 2 of the 0x83 intent family: a client noticed a hole in the 0x84 stream. Answered with
        /// every keyable actor's authoritative state — expensive and rare, which is exactly the right shape
        /// for a recovery path.</summary>
        internal static void HandleResnapRequest(NetworkEngine engine, ulong senderPeerId, uint nonce, byte op, BinaryReader r)
        {
            var tlc = Tlc();
            if (tlc == null || tlc.Map == null)
            {
                IntentRail.Reject(SurfaceIds.TacCommandIntent, senderPeerId,
                                  "resnapshot requested but the host is not in a battle");
                return;
            }
            var actors = new List<TacticalActorBase>();
            foreach (var a in tlc.Map.GetActors<TacticalActorBase>())
                if (TacticalActorKey.Of(a) != 0) actors.Add(a);
            Debug.LogWarning("[Multiplayer][tac] HOST resnapshotting " + actors.Count + " actor(s) for peer=" +
                             senderPeerId + " — that peer lost at least one resolved-attack record.");
            Send(OpResnap, "resnap " + actors.Count + " actor(s)", w =>
            {
                w.Write(actors.Count);
                foreach (var a in actors)
                {
                    var stats = (a as TacticalActor)?.CharacterStats;
                    int aKey = TacticalActorKey.Of(a);
                    w.Write(aKey);
                    w.Write(Stat(a.Health));
                    w.Write(stats == null ? float.NaN : (float)stats.ActionPoints);
                    w.Write(stats == null ? float.NaN : (float)stats.WillPoints);
                    w.Write((byte)(a.IsDead ? 1 : 0));
                    // A5 closes A4's last ceiling: a resnapshot-FORCED death used to carry no corpse manifest,
                    // so the recovered corpse kept every item the host had destroyed. The host's own pre-roll is
                    // archived for the whole battle, so the recovery path can carry the same answers the killing
                    // hit did.
                    if (a.IsDead) TacticalActorLifecycle.WriteLoot(w, TacticalActorLifecycle.ManifestFor(aKey));
                    var slots = new List<ItemSlot>();
                    var body = (a as TacticalActor)?.BodyState;
                    if (body != null) foreach (var s in body.GetHealthSlots()) slots.Add(s);
                    w.Write(slots.Count);
                    foreach (var s in slots)
                    {
                        w.Write(s.GetSlotName() ?? "");
                        w.Write(Stat(s.GetHealth()));
                        w.Write(Stat(s.GetArmor()));
                    }
                }
            });
        }

        // ─── CLIENT: apply ─────────────────────────────────────────────────

        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.TacResult) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    byte op = r.ReadByte();
                    if (!Seq.ShouldApply(SurfaceIds.TacResult, seq)) return true;   // stale re-delivery (law 7)
                    if (HasGap(seq)) RequestResnap(seq);
                    if (op == OpDamage) ApplyDamage(r);
                    else if (op == OpResnap) ApplyResnap(r);
                    else if (op == OpSpawn) TacticalActorLifecycle.ApplySpawn(r);
                    else if (op == OpDeath) TacticalActorLifecycle.ApplyDeath(r);
                    else if (op == OpInventory) TacticalInventorySync.ApplyInventory(r);
                    else if (op == OpEnvDamage) TacticalDestruction.ApplyEnvDamage(r);
                    else
                    {
                        Debug.LogError("[Multiplayer][tac] unknown host→all result op " + op + " (seq=" + seq +
                                       ") — this peer can no longer follow the shared battle.");
                        return true;
                    }
                    // ONE mark for the whole 0x84 family (law 11). TacticalUiRepaint's only other driver is
                    // the AbilityExecuted postfix, so every model change that is NOT an ability executing —
                    // a resolved hit, a spawn, a death, a resnapshot, a destroyed cover — left the model fresh
                    // and the observer's screen stale, which is indistinguishable from "it never crossed".
                    // MarkDirty only sets a flag; the flush is that class's own Update postfix.
                    TacticalUiRepaint.MarkDirty();
                    Seq.Mark(SurfaceIds.TacResult, seq);
                    _lastContiguous = seq;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] result inbound FAILED — this peer's battle has diverged from " +
                               "the host's: " + ex);
                RequestResnap(0);
            }
            return true;
        }

        /// <summary>THE GAP CHECK. On a value rail a hole heals itself on the next emit; here every message is
        /// a distinct EVENT, so a hole is a permanent difference in somebody's health. A NAMED method rather
        /// than an inline comparison, deliberately: it is then visible in the caller's IL, so deleting the
        /// only recovery trigger this surface has turns the harness RED instead of doing nothing quietly.</summary>
        private static bool HasGap(uint seq) => _lastContiguous != 0 && seq > _lastContiguous + 1;

        /// <summary>Ask once per hole, not once per message: the answer is a whole-battlefield broadcast, and
        /// a request per lost record would turn one dropped packet into a storm.
        /// It only ARMS the request; <see cref="ClientTick"/> is what emits it. Sending from the receive path
        /// itself would make an intent reachable from every inbound dispatch, which is the shape law L19 calls
        /// a result-ship — and it is genuinely the wrong shape: a recovery request belongs on the standing
        /// tick, where it is naturally rate-limited, not inside the handler for the message that revealed the
        /// hole.</summary>
        private static void RequestResnap(uint seq)
        {
            Debug.LogError("[Multiplayer][tac] GAP in the resolved-attack stream (expected seq " +
                           (_lastContiguous + 1) + ", got " + seq + ") — at least one resolved hit never " +
                           "arrived and this surface has no re-emit, so somebody's health is wrong here. " +
                           "Requesting a full actor resnapshot from the host.");
            if (_resnapRequested) return;
            _resnapRequested = true;
            _resnapPending = true;
        }

        /// <summary>The standing emitter (driven from <c>SyncEngine.Tick</c>, client-only inside): ships an
        /// armed resnapshot request. Standing rather than one-shot for the same reason A3a holds its settles —
        /// a request that could not be sent (no engine yet, mid-teardown) must not vanish.</summary>
        internal static void ClientTick(NetworkEngine engine)
        {
            if (!_resnapPending) return;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) { _resnapPending = false; return; }
            _resnapPending = false;
            IntentRail.Send(SurfaceIds.TacCommandIntent, OpIntentResnap, "resnapshot after a 0x84 gap");
        }

        private static void ApplyDamage(BinaryReader r)
        {
            var tlc = Tlc();
            int key = r.ReadInt32();
            string slot = r.ReadString();
            var notes = new List<string>();
            var result = DamageResultCodec.Read(r, tlc, notes);
            float recvHp = r.ReadSingle(), recvArmor = r.ReadSingle();
            float actorHp = r.ReadSingle(), ap = r.ReadSingle(), wp = r.ReadSingle();
            bool dead = r.ReadByte() != 0;
            var loot = dead ? TacticalActorLifecycle.ReadLoot(r) : null;

            string why;
            var actor = TacticalActorKey.Resolve(tlc, key, out why);
            if (actor == null)
            {
                Debug.LogError("[Multiplayer][tac] resolved damage for actor " + key + " CANNOT be applied — " +
                               why + ". That actor's health is now wrong on this screen for good.");
                RequestResnap(0);
                return;
            }
            var receiver = TacticalActorKey.ResolveReceiver(actor, slot, out why);
            if (receiver == null)
            {
                Debug.LogError("[Multiplayer][tac] resolved damage for " + actor.name + " body part '" + slot +
                               "' CANNOT be applied — " + why + ".");
                RequestResnap(0);
                return;
            }
            if (notes.Count > 0)
                Debug.LogWarning("[Multiplayer][tac] resolved damage for " + actor.name + " arrived with parts this " +
                                 "peer could not rebuild: " + string.Join("; ", notes.ToArray()));

            // A4: the corpse manifest is DECLARED BEFORE the hit, never after — the death this ApplyDamage is
            // about to start runs DieAbility.DropItems inside the same synchronous chain (law L67d).
            if (dead) LootMirror.Declare(key, loot);

            // VERBATIM, through the game's OWN writer — statuses, effects, notifications, contribution and
            // death all come from the native body, and none of it is re-derived here (law L66a).
            using (SyncApplyScope.Enter())
            using (MirrorApplyScope.Enter())
                receiver.ApplyDamage(result);

            Correct(receiver.GetHealth(), recvHp, actor.name + "/" + (slot.Length == 0 ? "actor" : slot) + " hp");
            Correct(receiver.GetArmor(), recvArmor, actor.name + "/" + (slot.Length == 0 ? "actor" : slot) + " armor");
            Correct(actor.Health, actorHp, actor.name + " health");
            var stats = (actor as TacticalActor)?.CharacterStats;
            if (stats != null)
            {
                Correct(stats.ActionPoints, ap, actor.name + " AP");
                Correct(stats.WillPoints, wp, actor.name + " WP");
            }
            // A4: death is a REPLICATED STRUCTURAL FACT, not a log line. The overwrite above normally does it
            // by itself (Health.Set is the game's own death trigger), so reaching this branch means the host's
            // snapshot did not kill it here — force it through the same native trigger rather than leave the
            // peers disagreeing about who is standing.
            if (dead && !actor.IsDead) TacticalActorLifecycle.ForceDeath(actor, "the host's resolved hit");
            else if (!dead && actor.IsDead)
                Debug.LogError("[Multiplayer][tac] " + actor.name + " is DEAD here but ALIVE on the host after the " +
                               "same hit — this peer killed something the host did not, which nothing can undo. " +
                               "The damage neuter (law L66a) is supposed to make this unreachable.");
            // L105: the native death this peer just replayed moved the KILLER's will points and every
            // faction-mate's. Any stat snapshot captured before this instant is now older than the board.
            if (dead) NoteNativeStatEvent();
        }

        /// <summary>Overwrite from the host's snapshot AND report it. A correction that is not zero means
        /// something on this peer re-derived a number the host had already resolved — which is precisely the
        /// double-apply that <see cref="MirrorApplyScope"/> exists to prevent — so it is never silent.</summary>
        internal static void Correct(BaseStat stat, float authoritative, string what)
        {
            if (stat == null || float.IsNaN(authoritative)) return;
            float mine = stat;
            if (Mathf.Abs(mine - authoritative) < 0.005f) return;
            stat.Set(authoritative);
            Debug.LogWarning("[Multiplayer][tac] corrected " + what + " " + mine.ToString("0.##") + " → " +
                             authoritative.ToString("0.##") + " from the host — a difference here means this peer " +
                             "recomputed something the host had already resolved.");
        }

        private static void ApplyResnap(BinaryReader r)
        {
            var tlc = Tlc();
            int n = r.ReadInt32();
            int fixedUp = 0, lost = 0;
            using (SyncApplyScope.Enter())
                for (int i = 0; i < n; i++)
                {
                    int key = r.ReadInt32();
                    float hp = r.ReadSingle(), ap = r.ReadSingle(), wp = r.ReadSingle();
                    bool dead = r.ReadByte() != 0;
                    // A5: the manifest must be DECLARED before the forced death below, for exactly A4's reason —
                    // DieAbility.DropItems asks its questions inside that same synchronous chain.
                    if (dead) LootMirror.Declare(key, TacticalActorLifecycle.ReadLoot(r));
                    int slots = r.ReadInt32();
                    string why;
                    var actor = TacticalActorKey.Resolve(tlc, key, out why);
                    if (actor == null) lost++;
                    for (int s = 0; s < slots; s++)
                    {
                        string name = r.ReadString();
                        float sh = r.ReadSingle(), sa = r.ReadSingle();
                        if (actor == null) continue;
                        var recv = TacticalActorKey.ResolveReceiver(actor, name, out why);
                        if (recv == null) continue;
                        Correct(recv.GetHealth(), sh, actor.name + "/" + name + " hp");
                        Correct(recv.GetArmor(), sa, actor.name + "/" + name + " armor");
                    }
                    if (actor == null) continue;
                    fixedUp++;
                    Correct(actor.Health, hp, actor.name + " health");
                    var stats = (actor as TacticalActor)?.CharacterStats;
                    if (stats != null)
                    {
                        Correct(stats.ActionPoints, ap, actor.name + " AP");
                        Correct(stats.WillPoints, wp, actor.name + " WP");
                    }
                    // A4: the resnapshot's dead flag is now ACTED ON. It is the recovery path's only structural
                    // repair, and it is why a lost 0x84 death record is not permanent. A5 gave it the host's
                    // archived corpse manifest too (declared above), so a recovered corpse holds what the
                    // host's does instead of everything.
                    if (dead && !actor.IsDead) TacticalActorLifecycle.ForceDeath(actor, "the host's resnapshot");
                    if (dead) NoteNativeStatEvent();   // L105, same reason as ApplyDamage's
                }
            _resnapRequested = false;
            Debug.LogWarning("[Multiplayer][tac] CLIENT applied the host's resnapshot: " + fixedUp + " actor(s) " +
                             "reconciled, " + lost + " unresolvable.");
        }
    }

    /// <summary>
    /// THE STATUS-DURATION LEAK (law L66e). <c>CooldownStatus.OnApply</c>:13 rolls
    /// <c>DurationTurns = Random.Range(Min, Max+1)</c> every time such a status is instantiated, and
    /// <c>ApplyDamageInternal</c>:930 instantiates every status a shipped <c>DamageResult</c> carries — so a
    /// mirror would give the host's status a different lifetime. The roll writes the DEF and
    /// <c>TacStatus.DurationInTurns</c>:37 reads the def LIVE, which is why restoring the host's number in a
    /// POSTFIX is enough: the status instance never cached the rolled value.
    /// </summary>
    internal static class CooldownDurationMirror
    {
        private static readonly Dictionary<string, int> _declared = new Dictionary<string, int>(StringComparer.Ordinal);

        internal static void Reset() => _declared.Clear();

        internal static void Declare(StatusDef def, int turns)
        {
            if (def != null) _declared[def.Guid] = turns;
        }

        internal static void Restore(CooldownStatus status)
        {
            var def = status == null ? null : status.CooldownStatusDef;
            if (def == null) return;
            int turns;
            if (!_declared.TryGetValue(def.Guid, out turns)) return;
            _declared.Remove(def.Guid);
            if (def.DurationTurns == turns) return;
            Debug.Log("[Multiplayer][tac] cooldown status '" + def.name + "' rolled " + def.DurationTurns +
                      " turns here; forcing the host's " + turns + ".");
            def.DurationTurns = turns;
        }
    }

    [HarmonyPatch(typeof(CooldownStatus), "OnApply")]
    internal static class CooldownDurationGate
    {
        private static void Postfix(CooldownStatus __instance)
        {
            if (MirrorApplyScope.Active) CooldownDurationMirror.Restore(__instance);
        }
    }

    /// <summary>
    /// HOST CAPTURE + CLIENT NEUTER on the actor. The capture is on <c>ApplyDamageInternal</c> and NOT on the
    /// public <c>ApplyDamage</c>: other mods install <c>ref DamageResult</c> mutators on Internal (TFTV's acid
    /// resistance at <c>TFTVAcid</c>:54 and Die Hard at <c>FactionPerks</c>:181, both prefixes), and
    /// <c>ApplyDamage</c>:950-956 passes the struct BY VALUE, so a capture one frame out sees the number the
    /// host never applied. Patching the BASE method is what makes one patch cover both actor kinds:
    /// <c>TacticalActor.ApplyDamageInternal</c>:1497-1500 calls <c>base</c> with the already-mutated struct.
    /// <c>__result</c> false means the native body refused it (god mode, the full-health-only gate) and
    /// nothing changed — so nothing is shipped.
    /// </summary>
    [HarmonyPatch]
    internal static class ActorDamageSeam
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TacticalActorBase), "ApplyDamageInternal", new[] { typeof(DamageResult) });

        private static void Postfix(TacticalActorBase __instance, ref DamageResult damageResult, bool __result)
        {
            if (__result) TacticalDamageSync.OnDamageApplied(__instance, damageResult);
        }
    }

    /// <summary>
    /// THE PUBLIC ACTOR ENTRY, client side only: a damage-over-time status ticking on this peer's own turn
    /// edge reaches <c>ApplyDamage</c> without ever passing through a <c>DamageAccumulation</c>, and the host
    /// ships its OWN tick for the same status — so without this belt every poison/bleed/fire tick lands twice
    /// on a client. Never touches the host, and never touches a mirror apply (that IS the host's damage).
    /// </summary>
    [HarmonyPatch(typeof(TacticalActorBase), nameof(TacticalActorBase.ApplyDamage))]
    internal static class ActorDamageClientGate
    {
        private static bool Prefix(TacticalActorBase __instance)
        {
            if (!TacticalDamageSync.DamageIsSomebodyElses()) return true;
            TacticalDamageSync.SayOnce("neuter-actor",
                "[Multiplayer][tac] damage computed on this client is DISCARDED (first occurrence: " +
                __instance.name + ") — every hit that counts arrives already resolved on 0x84. This line is " +
                "the arc working, not a failure.");
            return false;
        }
    }

    /// <summary>Body parts: the same two jobs on the one method. <c>ItemSlot</c> IS the receiver
    /// (<c>ItemSlot</c>:18) and <c>ItemSlot.ApplyDamage</c>:120-128 is where a limb's health and armour
    /// actually move, so the capture and the neuter both belong here rather than one level down in
    /// <c>DamageReceiverImplementation</c>, which has no identity of its own to name on the wire.</summary>
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.ApplyDamage))]
    internal static class SlotDamageSeam
    {
        private static bool Prefix(ItemSlot __instance)
        {
            if (!TacticalDamageSync.DamageIsSomebodyElses()) return true;
            TacticalDamageSync.SayOnce("neuter-slot",
                "[Multiplayer][tac] body-part damage computed on this client is DISCARDED (first occurrence: " +
                __instance.GetSlotName() + ") — the host's resolved version arrives on 0x84.");
            return false;
        }

        private static void Postfix(ItemSlot __instance, DamageResult damageResult)
            => TacticalDamageSync.OnDamageApplied(__instance, damageResult);
    }

    /// <summary>
    /// THE NEUTER THE MANDATE NAMES: <c>DamageAccumulation.ApplyAddedDamage</c>:550 is the ONE place where a
    /// computed hit becomes a real one — <c>AddTarget</c>/<c>GenerateStandardDamageTargetData</c> only fill
    /// <c>_targetsData</c>, so skipping this deletes every client-side weapon/explosion/fire application at
    /// once while leaving the projectile and its animation completely intact. It sits HERE and not at
    /// <c>ApplyDamage</c> because that is also where the foreign <c>DamageResult</c> FACTORY lives (TFTV
    /// full-replaces <c>GenerateStandardDamageTargetData</c> at <c>TFTVVanillaFixes</c>:3790 and postfixes
    /// <c>AddTarget</c> at <c>TFTVExperimental</c>:560): harmless while the host computes, fatal the moment a
    /// peer recomputes.
    /// </summary>
    [HarmonyPatch(typeof(DamageAccumulation), nameof(DamageAccumulation.ApplyAddedDamage))]
    internal static class AccumulationClientGate
    {
        private static bool Prefix(DamageAccumulation __instance)
        {
            if (!TacticalDamageSync.DamageIsSomebodyElses()) return true;
            __instance.ClearTargets();   // leave no half-built accumulation behind for the next shot to inherit
            return false;
        }
    }

    /// <summary>
    /// VANILLA RE-ROLL LEAK 1 — the NESTED return-melee. <c>TacticalActorBase.ApplyDamageInternal</c>:858
    /// calls <c>GetAbility&lt;TacticalReturnMeleeDamage&gt;()?.Activate(damageResult)</c> from INSIDE the very
    /// method a mirror re-enters, and that ability applies a SECOND <c>DamageResult</c> of its own
    /// (<c>TacticalReturnMeleeDamage</c>:54) plus a <c>Random.insideUnitSphere</c> draw for its projectile
    /// (:66). On the host it is ordinary damage and is captured and shipped like any other; on a mirror it
    /// would apply a hit the host already sent, so it stands down.
    /// (Correction to the arc's research note: the returned DAMAGE is deterministic —
    /// <c>HealthDamage * returnDamageAmountMult</c> clamped to <c>minDamage</c> — the only RNG here is the
    /// cosmetic projectile offset. The double-apply is the real hazard, not the roll.)
    /// </summary>
    [HarmonyPatch(typeof(TacticalReturnMeleeDamage), nameof(TacticalReturnMeleeDamage.Activate))]
    internal static class ReturnMeleeMirrorGate
    {
        private static bool Prefix()
        {
            if (!MirrorApplyScope.Active) return true;
            Debug.Log("[Multiplayer][tac] return-melee suppressed inside a mirror apply — the host already " +
                      "resolved it and shipped it as its own record.");
            return false;
        }
    }

    /// <summary>
    /// VANILLA RE-ROLL LEAK 3 — the death loot roll. <c>DieAbility.ShouldDestroyItem</c>:187-200 draws from
    /// <c>SharedData.Random</c>, which is a per-peer <c>new System.Random()</c> (<c>SharedData</c>:69) and is
    /// therefore not comparable between peers at all — AND it is the same stream the spawn tables use
    /// (<c>TacParticipantSpawn</c>), so a client consuming it desynchronises more than loot.
    ///
    /// A3b made it host-only and left the CONTENTS to A4; A4 is here. The memo is consulted FIRST for both
    /// roles — the host's own pre-rolled draw (so exactly one roll happens per item, as in vanilla) and the
    /// mirror's declared answer from <see cref="LootMirror"/>. Only a peer with no answer at all falls back to
    /// "keep it", and never to a draw.
    /// </summary>
    [HarmonyPatch(typeof(DieAbility), "ShouldDestroyItem")]
    internal static class LootRollHostOnly
    {
        private static bool Prefix(DieAbility __instance, TacticalItem item, ref bool __result)
        {
            if (LootMirror.TryConsume(TacticalActorKey.Of(__instance == null ? null : __instance.TacticalActorBase),
                                      item, out __result)) return false;
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || engine.IsHost) return true;   // solo or host: roll natively
            __result = false;
            TacticalDamageSync.SayOnce("loot-roll",
                "[Multiplayer][tac] a corpse's contents arrived with no host manifest — this client keeps every " +
                "droppable item instead of drawing from SharedData.Random (the same stream the spawn tables " +
                "use). The manifest rides with the killing hit; a death that reached here by any other route " +
                "has none.");
            return false;
        }
    }

    /// <summary>
    /// THE MIRROR-APPLY GUARD (law L66b) — and it is deliberately UNIVERSAL rather than a list of TFTV class
    /// names. THE HAZARD: a mirror re-applies an already-resolved <c>DamageResult</c> through the game's own
    /// <c>ApplyDamage</c>, and every foreign patch on that path runs AGAIN on a number that is already final.
    /// Measured against the real TFTV source, all five of them mutate state:
    /// <c>TFTVAcid</c>:54 rewrites <c>HealthDamage</c> by acid resistance; <c>FactionPerks</c>:181 (Die Hard)
    /// rewrites it to "health minus one" AND calls <c>UnityEngine.Random.InitState((int)Stopwatch
    /// .GetTimestamp())</c> at :171 — a WALL-CLOCK reseed of the global generator in the middle of a battle;
    /// <c>TFTVSuppression</c>:498-501 postfixes the same method; <c>TFTVDeliriumPerks</c>:162 postfixes
    /// <c>TriggerHurt</c> and subtracts a will point; <c>TFTVHarmonyTactical</c>:493 postfixes
    /// <c>ApplyDamage</c> for a containment check.
    ///
    /// WHY IT IS BUILT AS AN ENUMERATION AND NOT A LIST: it asks HARMONY ITSELF which patches sit on the four
    /// vanilla damage entries and neutralises every one whose owner is not us. A new TFTV release, a sixth
    /// patch, or an entirely different mod is covered without an edit — and there is no hard-coded type name
    /// to rot. It only ever stands a patch down while <see cref="MirrorApplyScope"/> is active, so
    /// SINGLE-PLAYER AND THE HOST ARE UNTOUCHED: the scope is entered in exactly one place, around the
    /// client's verbatim re-application.
    ///
    /// THE TWO SHAPES matter and getting them wrong would be silent: a void patch method is simply skipped,
    /// while a BOOL-returning prefix must be skipped with <c>__result = true</c> — skipping it into a default
    /// <c>false</c> would tell Harmony to skip the ORIGINAL, i.e. drop the host's damage entirely.
    ///
    /// LATE-BOUND, never <c>TypeByName</c> at ModInit: TFTV is enabled AFTER us, so an install that ran only
    /// at <c>PatchAll</c> time would find zero foreign patches and bind nothing, silently
    /// (memory: harmony-crossmod-patch-late-bind). <see cref="Install"/> is idempotent and is called both at
    /// startup and from <c>TftvLateBinder</c>'s post-TFTV bind.
    /// </summary>
    internal static class MirrorApplyGuard
    {
        private static readonly HashSet<MethodBase> _guarded = new HashSet<MethodBase>();
        private static HarmonyLib.Harmony _harmony;

        /// <summary>The four vanilla methods a foreign <c>ref DamageResult</c> mutator can sit on, resolved
        /// with EXACT parameter matching (<c>AccessTools</c> does no widening — a missed parameter yields null
        /// and the guard would scan nothing there, silently). Exposed so RailCheck asserts THIS list rather
        /// than re-deriving its own, which would let a typo here pass the harness.</summary>
        internal static List<MethodBase> Entries()
        {
            var entries = new List<MethodBase>();
            Add(entries, AccessTools.Method(typeof(TacticalActorBase), "ApplyDamageInternal", new[] { typeof(DamageResult) }));
            Add(entries, AccessTools.Method(typeof(TacticalActor), "ApplyDamageInternal", new[] { typeof(DamageResult) }));
            Add(entries, AccessTools.Method(typeof(TacticalActor), "TriggerHurt", new[] { typeof(DamageResult) }));
            Add(entries, AccessTools.Method(typeof(TacticalActorBase), nameof(TacticalActorBase.ApplyDamage)));
            return entries;
        }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            _harmony = harmony;
            var entries = Entries();
            if (entries.Count < 4)
                Debug.LogError("[Multiplayer][tac] the mirror-apply guard could not resolve all four vanilla damage " +
                               "entries (" + entries.Count + "/4) — a foreign patch on the missing one will mutate " +
                               "the host's already-resolved damage a second time on every client.");

            int newly = 0;
            foreach (var entry in entries)
            {
                var info = HarmonyLib.Harmony.GetPatchInfo(entry);
                if (info == null) continue;
                foreach (var p in Foreign(info))
                {
                    if (p.PatchMethod == null || !_guarded.Add(p.PatchMethod)) continue;
                    try
                    {
                        bool returnsBool = (p.PatchMethod as MethodInfo)?.ReturnType == typeof(bool);
                        _harmony.Patch(p.PatchMethod, prefix: new HarmonyMethod(
                            returnsBool ? AccessTools.Method(typeof(MirrorApplyGuard), nameof(SkipBool))
                                        : AccessTools.Method(typeof(MirrorApplyGuard), nameof(SkipVoid))));
                        newly++;
                        Debug.Log("[Multiplayer][tac] mirror-apply guard armed on " + p.owner + " → " +
                                  p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name +
                                  (returnsBool ? " (bool prefix)" : ""));
                    }
                    catch (Exception ex)
                    {
                        _guarded.Remove(p.PatchMethod);
                        Debug.LogError("[Multiplayer][tac] mirror-apply guard FAILED on " + p.owner + " → " +
                                       p.PatchMethod.Name + " — that patch will run again on every mirrored hit " +
                                       "and mutate damage the host already resolved: " + ex.Message);
                    }
                }
            }
            if (newly > 0)
                Debug.Log("[Multiplayer][tac] mirror-apply guard covering " + _guarded.Count + " foreign damage patch(es).");
        }

        private static void Add(List<MethodBase> list, MethodBase m) { if (m != null) list.Add(m); }

        private static IEnumerable<Patch> Foreign(HarmonyLib.Patches info)
        {
            string ours = _harmony.Id;
            foreach (var p in info.Prefixes) if (p.owner != ours) yield return p;
            foreach (var p in info.Postfixes) if (p.owner != ours) yield return p;
            foreach (var p in info.Finalizers) if (p.owner != ours) yield return p;
        }

        private static bool SkipVoid() => !MirrorApplyScope.Active;

        private static bool SkipBool(ref bool __result)
        {
            if (!MirrorApplyScope.Active) return true;
            __result = true;   // "run the original" — a default false would drop the host's damage entirely
            return false;
        }
    }
}
