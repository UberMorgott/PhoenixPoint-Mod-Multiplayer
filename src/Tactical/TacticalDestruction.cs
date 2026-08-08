using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Base;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels.Destruction;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// TACTICAL ARC A6, PART 2 — THE DESTRUCTIBLE ENVIRONMENT. Walls, floors, windows and props break on the
    /// host; without this arc they stay solid on every other peer, so cover, line of fire and navigation are
    /// computed against a building one player already knocked down.
    ///
    /// NO NEW SURFACE. It is op <see cref="TacticalDamageSync.OpEnvDamage"/> on 0x84, for the reason A4 gave
    /// for spawn and death: 0x84 is the discrete-event stream with the contiguity check and the resnapshot,
    /// and ORDER against the actor-damage records on the same stream is load-bearing — the wall a grenade
    /// removes and the soldier the same grenade wounds must not swap places on the wire.
    ///
    /// THE GAP IT CLOSES. A3b's receiver address is <c>(actorKey, IDamageReceiver.GetSlotName())</c>, and a
    /// destructible answers <c>GetActor()</c> with NULL and <c>GetSlotName()</c> with the constant
    /// "DestructableObject" (<c>DestructableDamageReceiver</c>:38-71) — every wall in the mission shares one
    /// address and none of them has an actor. So the existing 0x84 path could never have carried environment
    /// damage; it needed its own address, which is what this op is. The client-side neuter, meanwhile, was
    /// ALREADY total: <c>AccumulationClientGate</c> skips <c>DamageAccumulation.ApplyAddedDamage</c>, which is
    /// what every shot, burst and explosion reaches the destructible receivers through.
    ///
    /// THE IDENTITY, AND WHY v1's WAS DEAD MISSION-WIDE. v1 (`fc661b7`) keyed the right thing —
    /// <c>DestructableBase.GuidInScene</c>, the game's OWN save key (<c>FindDestructableObject</c>:312-321 is
    /// a <c>[SerializeCustomCreate]</c> that resolves exactly it) — and then RESOLVED it the one way that
    /// cannot be relied on: <c>SceneObjectIdsComponent.GetObjectById(id, scene)</c>, whose
    /// <c>GetForScene</c>:44-46 is <c>GameObject.FindGameObjectsWithTag("SceneObjectIds")
    /// .FirstOrDefault(h =&gt; h.scene == scene)</c>. That needs an ACTIVE tagged GameObject sitting in
    /// EXACTLY the scene asked for, and map generation moves it: <c>MapPlot</c>:230-243 reparents each
    /// parcel's ids component (a reparent changes a GameObject's scene), merges it into the PLOT's component
    /// and DESTROYS it. So the surviving registry lives in the plot's scene, while v1 asked in the level's —
    /// and the game's own reader asks in <c>SceneManager.GetActiveScene()</c>, a third answer again. Every
    /// batch missed on both clients, and v1's answer was a full-scan fallback over
    /// <c>Resources.FindObjectsOfTypeAll</c> with the root cause left undug.
    ///
    /// SO THIS ARC NEVER ASKS A SCENE ANYTHING. It builds its own index the way the GAME'S OWN SAVEGAME
    /// enumerates destructibles — <c>TacLevelSavegame</c>:49
    /// <c>Map.NavigableRoot.transform.GetComponentsInChildrenStable&lt;DestructableBase&gt;()</c> — and keys
    /// it by each object's own <c>GuidInScene</c>. Both peers walk the same hierarchy of the same generated
    /// map and read the same baked guids, so the index is symmetric by construction and there is no scene
    /// lookup left to point at the wrong registry. It is also the honest test of the identity: if a guid
    /// cannot be read or is not unique, <see cref="Index"/> says so out loud at battle start rather than
    /// failing silently on the first grenade.
    ///
    /// THE RECEIVER ADDRESS IS THE AIM POINT, not the impact point. One explosion damages MANY tiles through
    /// <c>GetDamageReceiversInSphere</c> and they all share a single <c>ImpactHit.Point</c>, so the impact
    /// point names a tile only for single-ray hits. The tile's aim-point transform sits at
    /// <c>GridToWorld</c>:140-148 = tile CENTRE (+0.5, +0.5*tileHeight), and
    /// <c>Destructable.GetDamageReceiverForHit</c>:826-831 is <c>FloorToInt</c> of exactly that mapping's
    /// inverse — so feeding the aim point back through the game's own lookup returns the same tile, half a
    /// tile clear of any boundary, with no grid arithmetic of our own to drift. <c>Breakable</c>:573-577
    /// ignores the position entirely and returns its single receiver, so one address covers both kinds.
    ///
    /// FALLS ARE DERIVED, NEVER REPLICATED. <c>TacticalLevelController.CheckForFallAbilitiesToActivate</c>
    /// :1916-1935 runs from <c>OnMapUpdate</c>:1913 on EVERY peer and activates <c>FallNoSupportAbility</c> on
    /// every actor of every faction that has lost its support. That is A5's autonomy rule exactly: each peer
    /// raises its own off the same replicated board, so relaying one would drop the actor twice.
    /// <c>FallNoSupportAbility</c> is already a declared local in
    /// <see cref="TacticalCommandSync.LocalAbilities"/> and law L69 keeps it there. The fall's DAMAGE is not
    /// derived — it is ordinary actor damage, neutered on non-authoritative peers and shipped as an ordinary
    /// 0x84 record like every other hit.
    ///
    /// WHAT IS DELIBERATELY NOT HERE: a window smashed by a body walking through it. That reaches
    /// <c>Breakable.ApplyDamage(origin, direction, force)</c> straight from
    /// <c>TacticalNavigationComponent</c>:609-613 during traversal, never through a
    /// <c>DamageAccumulation</c> — and A3a already mirrors the MOVE, so every peer runs the same real
    /// traversal and breaks the same glass locally. v1 needed a second capture for it only because v1's
    /// client never ran the move at all.
    /// </summary>
    public static class TacticalDestruction
    {
        /// <summary>The battle's destructibles by their own <c>GuidInScene</c>. Rebuilt per battle, never
        /// per message: the set is fixed at map generation (nothing adds a destructible mid-mission) and a
        /// wall that has been reduced to rubble is still the same object with the same guid.</summary>
        private static readonly Dictionary<string, DestructableBase> _byGuid =
            new Dictionary<string, DestructableBase>(StringComparer.Ordinal);

        /// <summary>THE SECOND ADDRESS, and it exists because the first one is not always the SAME on both
        /// peers. <c>SceneObjectIdsComponent.MergeWith</c>:29-34 mints <c>SceneObjectId.CreateNew()</c> — a
        /// FRESH RANDOM id — for every combined id that collides in the component it is merging into, and map
        /// generation merges a registry per parcel (<c>MapPlot</c>:230-243). A random id is different on every
        /// peer by construction, so a guid that collided is not an identity at all: the 2026-08-07 client log
        /// counted THIRTEEN of them in one map, and every one of those objects would take the host's damage on
        /// the wrong wall or on none.
        ///
        /// A COLLIDED GUID IS THEREFORE DROPPED, on both peers, and the object is addressed by something both
        /// peers DERIVE rather than are told: where it stands, and — because that alone does not tell two
        /// objects apart — WHICH ONE it is among everything standing there.
        ///
        /// WHY THE CELL ALONE IS NOT AN ADDRESS, and why a finer rounding cannot make it one. The 2026-08-08
        /// battle indexed 1269 destructibles and reported 358 of them sharing a cell with another — 28% of the
        /// map. That is not jitter to be rounded away, because a destructible's <c>transform</c> is its PREFAB
        /// PIVOT and the game never locates it by that: <c>Destructable.Init</c>:647 takes
        /// <c>MeshRenderer.bounds</c>, :721 keeps <c>_gridOrigin = bounds.min</c>, and :712-716 parents one
        /// receiver GameObject per tile positioned by <c>GridToWorld(bounds.min…)</c>. A kit-built wall/floor
        /// set exported with one shared pivot therefore puts every panel of a building at the SAME world point
        /// — bit-identical, so no number of decimal places separates them. Nor can the bounds be used as the
        /// address instead: <c>Destructable.CheckAndDisableSelf</c>:254-256 DESTROYS the MeshRenderer and the
        /// Collider of a fully broken object, so a bounds-derived address stops existing exactly when the
        /// object is still there and still being talked about.
        ///
        /// SO THE DISCRIMINATOR IS THE WALK, WHICH IS ALREADY THIS ARC'S PREMISE. The address is
        /// <c>cell#i/n</c>: the rounded cell, this object's index among the n destructibles found in that cell,
        /// and n itself. <c>GetComponentsInChildrenStable</c> (<c>UnityUtil</c>:69-74) is a strict depth-first
        /// walk — own components, then children in sibling order — so i and n are fixed by the hierarchy, which
        /// both peers build from the same generated map. Nothing shortens that hierarchy mid-mission either:
        /// <c>CheckAndDisableSelf</c>:261 only <c>SetActive(false)</c> and <c>Breakable.DisableSelf</c>:374 only
        /// clears <c>enabled</c> — the GameObject, the transform and the component all survive, and an inactive
        /// child is still walked. So the tags a peer assigns at battle start stay true for the whole mission.
        ///
        /// AND IT IS STILL SELF-CHECKING, which is the property a bare ordinal never had: <c>n</c> RIDES IN THE
        /// KEY. A peer whose cell holds a different number of destructibles produces tags that cannot match any
        /// tag the host produced — it fails to resolve and says so, instead of confidently naming the wrong
        /// wall. Rounded to a tenth of a unit, unchanged: the positions are baked rather than simulated, the
        /// grid is one unit, and a coarser cell is the more forgiving half of the pair now that the index
        /// carries the discrimination.</summary>
        private static readonly Dictionary<string, DestructableBase> _byPos =
            new Dictionary<string, DestructableBase>(StringComparer.Ordinal);
        /// <summary>The address the index assigned, by instance id, so the CAPTURE side ships the very tag the
        /// index minted rather than deriving a second one that could disagree with it.</summary>
        private static readonly Dictionary<int, string> _tagOf = new Dictionary<int, string>();
        private static bool _indexed;
        private static readonly HashSet<string> _said = new HashSet<string>(StringComparer.Ordinal);

        internal static void Reset()
        {
            _byGuid.Clear();
            _byPos.Clear();
            _tagOf.Clear();
            _indexed = false;
            _said.Clear();
        }

        /// <summary>Which cell this object stands in, to a tenth of a unit — the peer-independent, but NOT
        /// discriminating, half of its address. Invariant culture, because a comma decimal separator on one
        /// peer and a dot on the other would be exactly the silent mismatch this replaces.</summary>
        internal static string PosCell(DestructableBase d)
        {
            var t = d == null ? null : d.transform;
            if (t == null) return null;
            var p = t.position;
            return Mathf.Round(p.x * 10f).ToString(CultureInfo.InvariantCulture) + "," +
                   Mathf.Round(p.y * 10f).ToString(CultureInfo.InvariantCulture) + "," +
                   Mathf.Round(p.z * 10f).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The addresses of the <paramref name="occupants"/> destructibles found in one cell, in walk
        /// order. PURE, and the only place an address is ever spelled: index i tells two co-located objects
        /// apart, and the occupant COUNT rides along so a peer that found a different number there resolves
        /// nothing rather than resolving the wrong one.</summary>
        internal static string[] AddressTags(string cell, int occupants)
        {
            var tags = new string[occupants < 0 ? 0 : occupants];
            for (int i = 0; i < tags.Length; i++)
                tags[i] = cell + "#" + i.ToString(CultureInfo.InvariantCulture) + "/" +
                          occupants.ToString(CultureInfo.InvariantCulture);
            return tags;
        }

        /// <summary>The address the index gave this object, or null when it has none.</summary>
        private static string TagOf(DestructableBase d)
        {
            string tag;
            return d != null && _tagOf.TryGetValue(d.GetInstanceID(), out tag) ? tag : null;
        }

        private static void SayOnce(string key, string message)
        {
            if (_said.Add(key)) Debug.LogError(message);
        }

        // ─── IDENTITY ──────────────────────────────────────────────────────

        /// <summary>Read one destructible's own save key. <c>GuidInScene</c> is a property that reaches
        /// <c>SceneObjectIdsComponent.GetForScene(go.scene, canBeMissing: false)</c> and THROWS
        /// <c>InvalidOperationException</c> when that scene holds no tagged registry, so it is never called
        /// bare — a throwing wall must be a counted, named failure and not an exception that takes the whole
        /// index (or the capture postfix) with it.</summary>
        private static string GuidOf(DestructableBase d)
        {
            try
            {
                var id = d.GuidInScene;
                return id.IsValid ? id.GuidString : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Build the index the way the game's own savegame enumerates destructibles
        /// (<c>TacLevelSavegame</c>:49), so both peers index the same objects in the same way. Returns false
        /// when there is no map to walk yet.</summary>
        private static bool Index()
        {
            if (_indexed) return true;
            var tlc = TacticalDamageSync.Tlc();
            var map = tlc == null ? null : tlc.Map;
            var root = map == null ? null : map.NavigableRoot;
            if (root == null) return false;

            int idless = 0, total = 0, homeless = 0, sharedCells = 0, dupHits = 0;
            var byGuid = new Dictionary<string, DestructableBase>(StringComparer.Ordinal);
            var collided = new HashSet<string>(StringComparer.Ordinal);
            var cells = new Dictionary<string, List<DestructableBase>>(StringComparer.Ordinal);
            foreach (var d in root.transform.GetComponentsInChildrenStable<DestructableBase>())
            {
                total++;
                string cell = PosCell(d);
                if (cell == null) homeless++;
                else
                {
                    List<DestructableBase> here;
                    if (!cells.TryGetValue(cell, out here)) cells[cell] = here = new List<DestructableBase>();
                    here.Add(d);          // walk order, and the walk is the same on both peers
                }
                string guid = GuidOf(d);
                if (string.IsNullOrEmpty(guid)) { idless++; continue; }
                DestructableBase other;
                if (byGuid.TryGetValue(guid, out other) && !ReferenceEquals(other, d)) { collided.Add(guid); dupHits++; }
                else byGuid[guid] = d;
            }
            // EVERY OCCUPANT OF A CELL GETS AN ADDRESS, not just the first one the walk reached. The old index
            // let the first arrival keep the cell and left every later one unreachable — 358 of 1269 on the
            // 2026-08-08 map — so a host's damage to any of them landed nowhere here.
            foreach (var kv in cells)
            {
                var here = kv.Value;
                if (here.Count > 1) sharedCells++;
                var tags = AddressTags(kv.Key, here.Count);
                for (int i = 0; i < here.Count; i++) { _byPos[tags[i]] = here[i]; _tagOf[here[i].GetInstanceID()] = tags[i]; }
            }
            // A GUID THAT NAMES TWO OBJECTS NAMES NONE, and it is dropped on BOTH peers rather than
            // last-write-wins: the collision set itself can differ between peers (MergeWith's replacement id is
            // random), so keeping a colliding guid anywhere means one peer resolves it and the other resolves
            // it to something else. The position address above still names every one of them.
            foreach (var g in collided) byGuid.Remove(g);
            foreach (var kv in byGuid) _byGuid[kv.Key] = kv.Value;
            _indexed = true;
            Debug.Log("[Multiplayer][tac] indexed " + total + " destructible(s) under the navigable root: " +
                      _byGuid.Count + " carry a usable GuidInScene (the game's own save key) and " + _tagOf.Count +
                      " carry a position address, of which " + sharedCells + " cell(s) hold more than one object " +
                      "and are told apart by their index in the same stable walk both peers make.");
            // Both of these are the v1 failure class caught at battle start instead of at the first grenade.
            if (idless > 0)
                Debug.LogWarning("[Multiplayer][tac] " + idless + " of " + total + " destructible(s) have NO readable " +
                                 "GuidInScene — their scene holds no SceneObjectIds registry, the shape " +
                                 "MapPlot:230-243 leaves behind when a parcel's registry was merged and destroyed. " +
                                 "They are still addressable by position, so this is no longer a lost object.");
            if (collided.Count > 0)
                Debug.LogWarning("[Multiplayer][tac] " + collided.Count + " destructible guid collision(s) covering " +
                                 (collided.Count + dupHits) + " object(s) — SceneObjectIdsComponent.MergeWith:29-34 " +
                                 "mints a FRESH RANDOM guid when a combined one collides, and a random guid is " +
                                 "different on every peer. Those guids are DROPPED on every peer, which is the WHOLE " +
                                 "of the gap between " + total + " walked and " + _byGuid.Count + " guid-indexed — " +
                                 "nothing else is missing from that index. Every one of them is addressed by " +
                                 "position instead.");
            if (homeless > 0)
                Debug.LogError("[Multiplayer][tac] " + homeless + " destructible(s) have no transform to stand on, so " +
                               "they have NO position address. If a guid collision also cost one of them its guid it " +
                               "cannot be addressed at all, and the host's damage to it is refused here with a line " +
                               "of its own rather than landing on some other wall.");
            return true;
        }

        /// <summary>The destructible the host named, or null with a reason. Never a "closest match". The guid
        /// is tried first — it is the game's own save key and it is unique for all but the merged handful — and
        /// the position second, which is the address a merged one has left.</summary>
        private static DestructableBase Resolve(string guid, string posTag, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(posTag))
            {
                why = "the record carries neither a guid nor a position";
                return null;
            }
            if (!Index()) { why = "this peer has no tactical map to look in"; return null; }
            DestructableBase d;
            if (!string.IsNullOrEmpty(guid) && _byGuid.TryGetValue(guid, out d) && d != null) return d;
            if (!string.IsNullOrEmpty(posTag) && _byPos.TryGetValue(posTag, out d) && d != null) return d;
            why = "no destructible under this peer's navigable root carries GuidInScene '" + guid + "' (the index " +
                  "holds " + _byGuid.Count + ") and none answers to the address " + posTag + " (" + _byPos.Count +
                  " addresses). This peer found " + Occupants(posTag) + " destructible(s) in that cell, so if that " +
                  "count differs from the one the address carries the two maps really are not the same map — which " +
                  "is what the count is in the address for";
            return null;
        }

        /// <summary>How many destructibles THIS peer put in the cell the host's address names — the number that
        /// makes a failed resolve legible. Walks the keys because it only ever runs on the failure path.</summary>
        private static int Occupants(string posTag)
        {
            if (string.IsNullOrEmpty(posTag)) return 0;
            int hash = posTag.IndexOf('#');
            string cell = hash < 0 ? posTag : posTag.Substring(0, hash);
            int n = 0;
            foreach (var key in _byPos.Keys)
                if (key.Length > cell.Length && key[cell.Length] == '#' &&
                    string.CompareOrdinal(key, 0, cell, 0, cell.Length) == 0) n++;
            return n;
        }

        // ─── HOST: capture ─────────────────────────────────────────────────

        /// <summary>The host's <c>DestructableDamageReceiver.ApplyDamage</c> postfix. Ships the destructible's
        /// own guid, the receiver's aim point (which tile) and the host's already-resolved
        /// <c>DamageResult</c> through the codec A3b already round-trip-tests.</summary>
        internal static void OnEnvironmentDamage(DestructableDamageReceiver receiver, DamageResult result)
        {
            var engine = TacticalDamageSync.LiveEngine();
            if (engine == null || !engine.IsHost) return;
            if (MirrorApplyScope.Active) return;          // never re-ship what we are re-applying
            if (receiver == null) return;
            if (result.HealthDamage <= 0f) return;        // a no-op hit changes nothing to mirror

            var destructable = receiver.Destructable();
            if (destructable == null) return;
            Index();                                       // the collided guids must be dropped before one is read
            string guid = GuidOf(destructable);
            if (!string.IsNullOrEmpty(guid) && !_byGuid.ContainsKey(guid)) guid = "";   // collided: it names nothing
            // THE TAG THE INDEX MINTED, never a second one derived here: the index is what knows how many
            // objects share this cell, and an address computed without that count could not be checked.
            string posTag = TagOf(destructable) ?? "";
            if (guid.Length == 0 && posTag.Length == 0)
            {
                SayOnce("envkey-" + TacticalActorLifecycle.SafeName(destructable),
                    "[Multiplayer][tac] damage to '" + TacticalActorLifecycle.SafeName(destructable) + "' is NOT " +
                    "relayed — it has neither a usable GuidInScene nor a position, so no other peer can be told " +
                    "which object broke. That piece of cover stays solid on every other screen for the rest of " +
                    "the mission.");
                return;
            }
            var aim = receiver.GetAimPoint();
            if (aim == null)
            {
                SayOnce("envaim-" + guid,
                    "[Multiplayer][tac] damage to destructible " + guid + " is NOT relayed — its receiver has no " +
                    "aim point, so no other peer can be told WHICH tile of it broke.");
                return;
            }
            var p = aim.position;
            TacticalDamageSync.Send(TacticalDamageSync.OpEnvDamage,
                "env " + TacticalActorLifecycle.SafeName(destructable) + " hp-" + result.HealthDamage.ToString("0.##"),
                w =>
                {
                    w.Write(guid);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    DamageResultCodec.Write(w, result);
                    w.Write(posTag);
                });
        }

        // ─── PEERS: apply ──────────────────────────────────────────────────

        /// <summary>Re-apply the host's hit to the same tile of the same object, through the game's OWN
        /// receiver, so the whole native cascade — the gibs, the doomed-tile sweep, the map update and the
        /// fall abilities it raises — runs here exactly as it did there.</summary>
        internal static void ApplyEnvDamage(BinaryReader r)
        {
            string guid = r.ReadString();
            var aim = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var notes = new List<string>();
            var result = DamageResultCodec.Read(r, TacticalDamageSync.Tlc(), notes);
            string posTag = r.ReadString();

            string why;
            var destructable = Resolve(guid, posTag, out why);
            if (destructable == null)
            {
                SayOnce("envlost-" + guid + "@" + posTag,
                    "[Multiplayer][tac] the host broke a destructible this peer CANNOT find — " + why + ". That " +
                    "cover stays solid here while it is rubble on the host, so line of fire and pathing now " +
                    "disagree. This is the v1 mission-wide failure (fc661b7) and law L69 exists to make it " +
                    "impossible; if this line appears, the index at battle start reported why.");
                return;
            }
            // The game's own lookup, fed the address it derived the receiver from. Direction is unused by both
            // implementations (Destructable:826-831 floors the position into its grid, Breakable:573-577
            // returns its single receiver) and is passed as zero rather than invented.
            var receiver = destructable.GetDamageReceiverForHit(aim, Vector3.zero);
            if (receiver == null)
            {
                SayOnce("envtile-" + guid,
                    "[Multiplayer][tac] destructible " + guid + " has no damage receiver at " + aim + " on this " +
                    "peer — that tile is already gone here, or the two peers' grids disagree. The host's hit is " +
                    "dropped.");
                return;
            }
            if (notes.Count > 0)
                Debug.LogWarning("[Multiplayer][tac] environment damage for " + guid + " arrived with parts this " +
                                 "peer could not rebuild: " + string.Join("; ", notes.ToArray()));

            using (SyncApplyScope.Enter())
            using (MirrorApplyScope.Enter())
                receiver.ApplyDamage(result);
        }
    }

    /// <summary>Reach the receiver's private destructible without a second reflection site in the arc.</summary>
    internal static class DestructableReceiverAccess
    {
        private static System.Reflection.FieldInfo _field;

        internal static DestructableBase Destructable(this DestructableDamageReceiver receiver)
        {
            if (receiver == null) return null;
            if (_field == null)
            {
                _field = AccessTools.Field(typeof(DestructableDamageReceiver), "_destructable");
                if (_field == null)
                {
                    Debug.LogError("[Multiplayer][tac] DestructableDamageReceiver._destructable did not resolve — " +
                                   "no environment damage can be named on the wire and every wall the host breaks " +
                                   "stays solid on every other peer.");
                    return null;
                }
            }
            return _field.GetValue(receiver) as DestructableBase;
        }
    }

    /// <summary>
    /// HOST CAPTURE on the one environment funnel. <c>DestructableDamageReceiver.ApplyDamage</c> is where a
    /// destructible's health actually moves and where its destroy handler fires (:78-93), and every combat
    /// route — single shot, burst, explosion, fire — reaches it through
    /// <c>DamageAccumulation.ApplyAddedDamage</c>, which is already the client neuter. No prefix is needed
    /// here for that reason: the neuter one level up means a non-authoritative peer never arrives.
    /// </summary>
    [HarmonyPatch(typeof(DestructableDamageReceiver), nameof(DestructableDamageReceiver.ApplyDamage))]
    internal static class EnvironmentDamageSeam
    {
        private static void Postfix(DestructableDamageReceiver __instance, DamageResult damageResult)
            => TacticalDestruction.OnEnvironmentDamage(__instance, damageResult);
    }
}
