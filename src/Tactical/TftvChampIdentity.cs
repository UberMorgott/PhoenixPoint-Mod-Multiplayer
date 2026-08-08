using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using PhoenixPoint.Common.Entities.GameTags;
using PhoenixPoint.Common.View.ViewControllers;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE OTHER HALF OF <see cref="TftvClientChampGuard"/>: the host's rolled human-enemy IDENTITY, carried.
    ///
    /// The guard stopped every peer minting its own elite. It could not make the peers AGREE — the host's
    /// roll stayed on the host — so a gated client showed the enemy under its engine name with no rank. This
    /// is what crosses, and it is exactly two things, both read out of TFTV's real source:
    ///
    ///   • THE TAGS. <c>TFTVHumanEnemies.GiveRankAndNameToHumaoidEnemy</c>:1648-1650 adds three
    ///     <c>GameTagDef</c>s — the tier (<c>HumanEnemyTier_2/3/4_GameTagDef</c>), <c>HumanEnemy_GameTagDef</c>
    ///     and <c>HumanEnemyFaction_&lt;ShortName&gt;_GameTagDef</c>. Def REFERENCES, and TFTV mints all of them
    ///     through <c>Helper.CreateDefFromClone</c> with a literal name (:218 <c>tagName = "HumanEnemy"</c>,
    ///     :225-270), so the name is the same literal on every peer — the same argument L130 makes for status
    ///     def names, and the reason nothing here ships a guid.
    ///   • THE NAME. :1652 <c>actor.name</c>, a plain string off TFTV's own generator.
    ///
    /// AND IT IS VISIBLE THROUGH ONE READER: TFTV postfixes <c>TacticalActorBase.get_DisplayName</c>
    /// (TFTVHumanEnemies.cs:851-871) with <c>__result = __instance.name + GetRankName(tags)</c> for any actor
    /// carrying a <c>HumanEnemyFaction</c> tag. Name and tier tag together ARE the label; either one missing
    /// is a different soldier on that screen.
    ///
    /// WHAT ALREADY CROSSES, so that this carries only what does not:
    ///   • Tags cross for BATTLE-START actors and only for them. <c>TacticalActorBase</c>:495 writes
    ///     <c>GameTags</c> into <c>TacActorBaseInstanceData.AdditionalGameTags</c> and :391-393 merges them
    ///     back, so the entry save transfer carries them — the capture is at <c>HasAnyTurnStarted</c>, i.e.
    ///     after the host's roll. A mid-battle spawn gets none: <c>ApplySpawn</c> passes a null instance-data
    ///     template on purpose.
    ///   • The NAME crosses nowhere at all. It is not instance data in any form — <c>ActorSpawner</c>:17
    ///     regenerates it per peer as <c>&lt;prefab&gt;_&lt;NextSpawnedActorID&gt;</c> from a local counter.
    ///     That is the whole visible defect on a battle-start champ: right rank, wrong soldier.
    ///
    /// THE CLIENT NEVER RE-ROLLS. It resolves the host's tag names and writes the host's string; TFTV's
    /// <c>GiveRankAndNameToHumaoidEnemy</c> stays suppressed on this peer exactly as before, which is what
    /// keeps the <c>Stopwatch</c>-seeded generator out of the answer.
    /// </summary>
    internal static class TftvChampIdentity
    {
        /// <summary>TFTV's own predicate, not ours: <c>GetFactionTierAndClassTags</c>:880-900 recognises its
        /// tags by <c>tag.name.Contains("HumanEnemyFaction")</c> / <c>Contains("HumanEnemyTier")</c>, and every
        /// tag the roll adds is built from the <c>"HumanEnemy"</c> literal at :218. The class tag is
        /// deliberately NOT in this set — it comes off the actor's def and is therefore already identical on
        /// every peer.</summary>
        private const string TagPrefix = "HumanEnemy";

        /// <summary>The one tag whose presence makes TFTV paint the rolled label at all
        /// (<c>get_DisplayName</c> postfix :858 tests <c>[0] != null</c>, i.e. the faction tag).</summary>
        private const string FactionTagPrefix = "HumanEnemyFaction";

        internal sealed class Identity
        {
            internal string Name;
            internal List<string> Tags;
        }

        // ─── host ──────────────────────────────────────────────────────────

        /// <summary>The host's rolled identity for this actor, or null when it has none — which is every
        /// actor in the game except a TFTV human enemy, and every human enemy in a session with no TFTV.
        /// Null is the silent, zero-cost case and it is the normal one.</summary>
        internal static Identity Collect(TacticalActorBase actor)
        {
            var tags = actor == null ? null : actor.GameTags;
            if (tags == null) return null;
            List<string> names = null;
            bool hasFaction = false;
            foreach (var t in tags)
            {
                if (t == null || t.name == null || !t.name.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
                if (t.name.StartsWith(FactionTagPrefix, StringComparison.Ordinal)) hasFaction = true;
                if (names == null) names = new List<string>();
                names.Add(t.name);
            }
            if (!hasFaction) return null;
            names.Sort(StringComparer.Ordinal);   // so two peers' sets compare as sets, not as enumeration order
            var id = new Identity { Name = actor.name, Tags = names };
            Announce(actor, id);
            return id;
        }

        private static readonly HashSet<string> _announced = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>SAY WHAT THE HOST IS SENDING, once. Without this the rider is unfalsifiable from a log: the
        /// host's <see cref="Collect"/> was silent and the client's <see cref="Apply"/> speaks only when it
        /// CHANGES something, so a session where no line appeared could equally mean "no human enemy was in
        /// that mission" or "the identity is not being collected at all" — and the 2026-08-08 sweep could not
        /// tell them apart. With this line the two are distinguishable: a host line and no client line is a
        /// carry that is failing, no host line at all is a mission with nothing to carry.
        ///
        /// ON CHANGE ONLY, never per settle — <see cref="Collect"/> runs on every settle of every actor, and
        /// the identity is rolled once. The key is the identity itself, so a re-roll speaks again and a repeat
        /// says nothing.</summary>
        /// <summary>The dedup key, pure so law L325 can execute it: it is the IDENTITY, not the actor, so a
        /// re-roll of the same key speaks again and a repeat of the same roll says nothing. Keyed on the actor
        /// alone, a second roll would be swallowed and the log could no longer tell "nothing to say" from
        /// "already said something else".</summary>
        internal static string AnnounceKey(int key, Identity id) =>
            key + "|" + (id == null ? "" : id.Name) + "|" +
            (id == null || id.Tags == null ? "" : string.Join(", ", id.Tags.ToArray()));

        private static void Announce(TacticalActorBase actor, Identity id)
        {
            int key = TacticalActorKey.Of(actor);
            string tags = string.Join(", ", id.Tags.ToArray());
            if (!_announced.Add(AnnounceKey(key, id))) return;
            Debug.Log("[Multiplayer][tac] HOST champ identity for key " + key + " — " + id.Name + " (" + tags +
                      "). TFTV rolled this off a Stopwatch-seeded generator, so it rides every spawn record and " +
                      "every settle naming that key and the clients adopt it instead of rolling their own.");
        }

        // ─── the codec ─────────────────────────────────────────────────────

        /// <summary>Always writes, null included — an actor with no identity is an empty name and a zero
        /// count, because the stream is shared with every other actor in the same message and a reader that
        /// skips bytes conditionally on something it has not read yet cannot exist.</summary>
        internal static void Write(BinaryWriter w, Identity id)
        {
            w.Write(id == null || id.Name == null ? "" : id.Name);
            var tags = id == null ? null : id.Tags;
            w.Write(tags == null ? 0 : tags.Count);
            if (tags == null) return;
            foreach (var t in tags) w.Write(t ?? "");
        }

        internal static Identity Read(BinaryReader r)
        {
            string name = r.ReadString();
            int n = r.ReadInt32();
            var tags = new List<string>(n < 0 ? 0 : n);
            for (int i = 0; i < n; i++) tags.Add(r.ReadString());
            return string.IsNullOrEmpty(name) && tags.Count == 0 ? null : new Identity { Name = name, Tags = tags };
        }

        // ─── client ────────────────────────────────────────────────────────

        /// <summary>Make this actor BE the host's champ: the host's tag set, then the host's name. Silent and
        /// free when the two already agree, which is every call after the first for a given actor — so this is
        /// safe to re-assert on every settle. Returns true when something actually changed.</summary>
        internal static bool Apply(TacticalActorBase actor, Identity id, string when)
        {
            if (actor == null || id == null) return false;
            try
            {
                bool changed = ApplyTags(actor, id);
                string was = actor.name;
                if (!string.IsNullOrEmpty(id.Name) && !string.Equals(was, id.Name, StringComparison.Ordinal))
                {
                    actor.name = id.Name;
                    changed = true;
                }
                if (!changed) return false;
                Repaint(actor);
                Debug.Log("[Multiplayer][tac] " + was + " is the host's " + id.Name + " (" +
                          string.Join(", ", (id.Tags ?? new List<string>()).ToArray()) + "), applied at " + when +
                          ". TFTV rolls a human enemy's rank and name off a Stopwatch-seeded generator, so this " +
                          "is the host's answer being adopted rather than a second roll.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] could not adopt the host's champ identity for " +
                               TacticalActorLifecycle.SafeName(actor) + " — this enemy keeps a different name " +
                               "and rank on this screen than it has on the host's: " + ex);
                return false;
            }
        }

        /// <summary>The host's <c>HumanEnemy*</c> set, exactly — a stale tier tag left BESIDE the host's is a
        /// second tier tag, and <c>GetFactionTierAndClassTags</c>:880-900 keeps whichever the enumeration
        /// reaches last, so the label would then depend on list order. Put back through
        /// <c>MergeRange(…, ReplaceExistingExclusive)</c>, which is the game's OWN recipe for restoring this
        /// very field (<c>TacticalActorBase.ProcessInstanceData</c>:391-393) — <c>Add</c> is not usable here at
        /// all: <c>GameTagsList.AddImpl</c>:106-108 THROWS on a tag that is already present.</summary>
        private static bool ApplyTags(TacticalActorBase actor, Identity id)
        {
            var tags = actor.GameTags;
            if (tags == null) return false;
            var want = new HashSet<string>(StringComparer.Ordinal);
            if (id.Tags != null) foreach (var n in id.Tags) want.Add(n);

            bool changed = false;
            List<GameTagDef> drop = null;
            foreach (var t in tags)
            {
                if (t == null || t.name == null) continue;
                if (!t.name.StartsWith(TagPrefix, StringComparison.Ordinal) || want.Contains(t.name)) continue;
                if (drop == null) drop = new List<GameTagDef>();
                drop.Add(t);
            }
            if (drop != null)
                foreach (var t in drop) changed |= tags.Remove(t);

            List<GameTagDef> add = null;
            foreach (var n in want)
            {
                var def = ResolveTag(n);
                if (def == null)
                {
                    Debug.LogError("[Multiplayer][tac] the host's human enemy carries game tag '" + n + "' and no " +
                                   "def of that name exists on this peer — mod parity should have made that " +
                                   "impossible (law 10). This enemy's rank stays different here.");
                    continue;
                }
                if (tags.Contains(def)) continue;
                if (add == null) add = new List<GameTagDef>();
                add.Add(def);
            }
            if (add == null) return changed;
            tags.MergeRange(add, GameTagAddMode.ReplaceExistingExclusive);
            return true;
        }

        // ─── the repaint ───────────────────────────────────────────────────

        /// <summary>REACTIVITY (postulate 1), and the rank half of it needs a call because NOTHING listens.
        ///
        /// THE NAME HAS EXACTLY ONE PERSISTENT LABEL AND NOTHING REPAINTS IT, said plainly rather than assumed.
        /// The enemy's name is baked into the aim HUD at <c>UIStateShoot</c>:253
        /// (<c>_shootTargetHealthBarModule.TargetNameText.text = _abilityTargetActor.DisplayName.ToUpper()</c>),
        /// written ONLY from <c>SetShootTarget</c>:219/:224 — whose callers are all target-CHANGE edges
        /// (:485-486 init, :677 pick, :1207 ground-target update, :1271 clear), never an Update. So a client
        /// already holding aim on the very enemy whose identity lands keeps the old label until it re-targets,
        /// and <see cref="TacticalUiRepaint"/> cannot help: it EXCLUDES <c>UIStateShoot</c> on purpose, because
        /// that state's <c>EnterState</c> moves the state stack and an Exit+Enter there double-exits it (L63).
        /// A stale label in that one window is the price this repo already decided to pay there; every OTHER
        /// surface resolves <c>DisplayName</c> at paint time and is right on its next read
        /// (<c>FreeAimTargetHealthbarUIActorElement</c>:66, <c>UIModuleDamagePrediction</c>:57), which
        /// <see cref="TacticalUiRepaint.MarkDirty"/> forces for the open ability-bar state.
        ///
        /// The RANK ICON does not: TFTV sets the healthbar's class icon ONCE, from its postfix on
        /// <c>HealthbarUIActorElement.InitHealthbar</c> (TFTVUI/Tactical/TargetIcons.cs:815-823) and from the
        /// tail of the roll itself (TFTVHumanEnemies.cs:1657-1658). Neither fires again when the tags arrive
        /// afterwards, and nothing in the game watches <c>GameTagsList.Changed</c> on the tactical side (its
        /// only subscribers in the whole assembly are <c>AddonsManager</c>:103 and geoscape sites). So the
        /// repaint is TFTV's OWN setter, called directly — the native widget, never a hand-rolled overlay, and
        /// never TFTV's roll. Reflected because we hold no reference to TFTV; absent TFTV it is simply null and
        /// this does nothing.</summary>
        private static void Repaint(TacticalActorBase actor)
        {
            TacticalUiRepaint.MarkDirty();
            var icon = ClassIconOf(actor);
            if (icon == null) return;                       // no healthbar built yet: InitHealthbar's own postfix will
            var setter = IconSetter();                      // read the tags we just wrote
            if (setter == null) return;
            try { setter.Invoke(null, new object[] { icon, actor }); }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][tac] TFTV's healthbar icon setter threw for " +
                                 TacticalActorLifecycle.SafeName(actor) + "; the name is right and the rank ICON " +
                                 "stays whatever it was until that healthbar is rebuilt: " + ex.Message);
            }
        }

        private static ActorClassIconElement ClassIconOf(TacticalActorBase actor)
        {
            var view = actor.TacticalActorViewBase;
            if (view == null) return null;
            var element = view.UIActorElement;
            if (element == null) return null;
            var bar = element.GetComponent<HealthbarUIActorElement>();
            return bar == null ? null : bar.ActorClassIconElement;
        }

        private static MethodInfo _iconSetter;
        private static bool _iconLookedUp;

        private static MethodInfo IconSetter()
        {
            if (_iconLookedUp) return _iconSetter;
            _iconLookedUp = true;
            var type = AccessTools.TypeByName("TFTV.TFTVUI.Tactical.TargetIcons");
            if (type == null) return null;                   // no TFTV in this session
            // EXACT parameter match, because AccessTools does no widening and a null here would mean the rank
            // icon silently stops being repainted with no line anywhere.
            _iconSetter = AccessTools.Method(type, "ChangeHealthBarIcon",
                                             new[] { typeof(ActorClassIconElement), typeof(TacticalActorBase) });
            if (_iconSetter == null)
                Debug.LogWarning("[Multiplayer][tac] TFTV is loaded but TargetIcons.ChangeHealthBarIcon" +
                                 "(ActorClassIconElement, TacticalActorBase) did not resolve — the host's rank " +
                                 "still crosses, but its healthbar icon is only repainted when that healthbar is " +
                                 "next rebuilt.");
            return _iconSetter;
        }

        // ─── the def lookup ────────────────────────────────────────────────

        private static readonly Dictionary<string, GameTagDef> _byName =
            new Dictionary<string, GameTagDef>(StringComparer.Ordinal);

        /// <summary>Rebuilt on a miss, so a def TFTV mints during load resolves on its first use instead of
        /// being permanently unknown — the same rule <see cref="TacticalStatusSet"/> uses.</summary>
        private static GameTagDef ResolveTag(string name)
        {
            GameTagDef def;
            if (_byName.TryGetValue(name, out def) && def != null) return def;
            var repo = GameUtl.GameComponent<DefRepository>();
            if (repo == null) return null;
            _byName.Clear();
            foreach (var d in repo.GetAllDefs<GameTagDef>())
                if (d != null && !string.IsNullOrEmpty(d.name)) _byName[d.name] = d;
            _byName.TryGetValue(name, out def);
            return def;
        }
    }
}
