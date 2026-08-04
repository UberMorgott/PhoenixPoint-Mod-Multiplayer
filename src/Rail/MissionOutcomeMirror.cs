using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Base.Core;
using Base.Defs;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Geoscape.Core;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Entities.Sites;
using PhoenixPoint.Geoscape.Levels;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE POST-MISSION SCREENS, ON EVERY PEER (user report 2026-08-01 items 3a+3b: "the panel showing what
    /// was gained — materials, technology, reputation — appeared on the HOST only, and so did the end-of-
    /// mission resupply panel").
    ///
    /// ROOT CAUSE, and it is ONE gate for BOTH panels — not two bugs. Everything a peer sees on arriving back
    /// from a battle hangs off a single branch, <c>UIStateInitial.EnterState</c>:101:
    /// <code>_params.LastMission != null &amp;&amp; (LastMission.IsCompleted || GetMissionOutcomeState() != Playing)</code>
    /// and inside it, in this order: the outcome modal (:105-112 <c>GetMissionOutcomeModal</c> →
    /// <c>OpenModalPersistent</c>), the Pandoran reveal (:114-123) and the RESUPPLY screen (:124-127
    /// <c>QueueReplenishState</c>). On a client that branch was ALWAYS FALSE, because
    /// <see cref="Multiplayer.Tactical.ClientMissionResultGate"/> blocks <c>GeoMission.Complete</c> whole —
    /// and <c>Complete</c>:267-276 is what sets BOTH of the things the branch tests (<c>Result</c> on its
    /// first line, <c>IsCompleted</c> at :275). Correct block, wrong blast radius: the campaign WRITE had to
    /// go, the two bookkeeping flags did not, and taking them with it silently removed every post-mission
    /// screen a client would ever see. The window-coverage table recorded the opposite premise — "arrival UI
    /// that fires natively on every peer that played the mission" — for both entries; it was never true.
    ///
    /// HALF ONE, THE GATE, IS THE GAME'S OWN METHOD: <c>GeoMission.CompleteSilently</c>:284-287, whose entire
    /// body is <c>IsCompleted = true</c>. It exists in the shipped game for exactly this case — record the
    /// completion, apply nothing — so the client takes it instead of the blocked <c>Complete</c>. That one
    /// call restores the resupply screen COMPLETELY (item 3b): <c>QueueReplenishState</c> is gated on this
    /// peer's own <c>GeoPhoenixFaction.GetMissingItems()</c> over its own aircraft and its own storage, all
    /// of it already mirrored, so nothing about it needs the wire. <c>Result</c> is deliberately NOT stamped:
    /// it is the host's authoritative mission result (law 3) and the branch's <c>||</c> does not need it.
    ///
    /// HALF TWO, THE CONTENT, IS THIS SURFACE. With the gate open the outcome modal now OPENS on every peer,
    /// but it renders exactly one thing — every one of the eleven outcome data binds is the same six lines
    /// ending in <c>RewardsController.SetReward(mission.Reward)</c> (ScavengeOutcomeDataBind:42,
    /// AmbushOutcomeDataBind:42, …), and <c>SetReward</c>:97-117 reads <c>reward.ApplyResult</c>, the object
    /// <c>GeoFactionReward.Apply</c>:110-112 mints. That is host-computed and NOTHING else on the rail
    /// carries it: the resources and items themselves arrive as ordinary wallet/storage state, but "what this
    /// battle gave you" is a DELTA that exists only in that one object, and the mission it hangs off leaves
    /// the graph moments later. So 0xBB ships the two lists the panel actually draws and each peer's own
    /// native modal renders them — the same posture as 0xBA cutscenes and the elite-intro rebuild: replay
    /// natively per peer, never ship rendered content.
    ///
    /// SCOPE (2026-08-05: no longer resources+items). The declared remainder was "each row kind needs its own
    /// identity resolution" — which is true of the ROWS and false of the MECHANISM: every row either native
    /// page draws is a KIND, up to two live references and an int, so the wire gained ONE generic row
    /// (<see cref="Row"/>) and one table (<see cref="RowKinds"/>) covering all seventeen at once. The
    /// references ride the rail's own stable addresses (<see cref="Ref"/> → law 2) and are re-resolved
    /// against the RECEIVING peer's model, never shipped as objects. STILL OUT: <c>CapturedAliens</c>
    /// (<c>GeoUnitDescriptor</c> is a generated recruit descriptor with no id in any registry — there is
    /// nothing to address) and <c>DestroySites</c>/<c>NewObjectives</c>/<c>PhoenixpediaEntries</c>/
    /// <c>StartMission</c>/<c>DestroyedVehicles</c>, which neither page reads.
    ///
    /// NOTHING HERE IS APPLIED, and that is the law-3 line: the values themselves already reach every peer as
    /// ordinary replicated state — resources/items on the value rail, diplomacy as
    /// <c>PartyDiplomacy+Relation._diplomacy</c>, a COVERED leaf under both <c>FactionDiplomacy</c> and the
    /// haven leader (docs/rail-baseline.txt:137-140, :492-495). This surface ships only the DISPLAY of what
    /// changed. RailCheck L101 keeps that honest.
    /// </summary>
    internal static class MissionOutcomeMirror
    {
        private static readonly SurfaceSeq Seq = new SurfaceSeq();

        /// <summary>The last outcome the host announced, waiting for this peer's own <c>Complete</c> to be
        /// gated. ONE slot, not a queue keyed by mission: <c>GeoLevelController</c>:694-711 completes exactly
        /// one <c>_missionToComplete</c> per geoscape load and stamps the screen five lines later, so a
        /// second outcome cannot be in flight — and a mission REF would be the wrong key anyway, since the
        /// client's <c>GeoMission</c> is a structural mirror the host's ref does not name.</summary>
        private static RewardWire _incoming;

        internal static void Reset() { Seq.Reset(); _incoming = null; }

        // ─── HOST ───────────────────────────────────────────────────────────

        /// <summary>Broadcast what the mission actually granted. Captured at
        /// <c>GeoFaction.OnMissionRewardApplied</c> (GeoFaction.cs:910), whose ONE caller is the
        /// <c>OnMissionCompleted</c> delegate <c>GeoSite</c>:798-805 installs on every mission — it runs one
        /// line after <c>reward.Apply(...)</c>:801, so the <c>ApplyResult</c> is fully built and is the
        /// GRANTED amount, not the requested one. Mission-specific by construction, which is why the seam is
        /// here and not on <c>GeoFactionReward.Apply</c> (that one also fires for every encounter reward).</summary>
        internal static void HostBroadcast(GeoFactionRewardApplyResult result)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession || !engine.IsHost) return;
            var coord = engine.SaveTransfer;
            if (coord == null || !coord.SessionStarted) return;
            if (result == null) return;

            try
            {
                uint seq = Seq.Next(SurfaceIds.GeoMissionOutcome);
                byte[] inner;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms, Encoding.UTF8))
                {
                    w.Write(seq);
                    Encode(w, result);
                    inner = ms.ToArray();
                }
                engine.BroadcastToAll(new NetworkMessage(PacketType.SyncEnvelope,
                    SyncProtocol.EncodeEnvelope(SurfaceIds.GeoMissionOutcome, SyncKind.StateDelta, inner)));
                Debug.Log("[MP][outcome] HOST mission reward seq=" + seq + " res=" + Count(result.Resources) +
                          " items=" + Count(result.Items));
            }
            catch (Exception ex)
            {
                // A dropped payload is a post-mission panel that opens EMPTY on every other peer — the exact
                // "model fresh, view stale" shape this repo treats as its dominant bug. Never silent.
                Debug.LogError("[MP][outcome] HOST mission reward broadcast FAILED — every other peer's " +
                               "outcome panel will show nothing gained: " + ex);
            }
        }

        // ─── WIRE (pure both ways, so RailCheck L83/L101 round-trip it headless) ──

        /// <summary>ONE reward row, as the wire carries it: a KIND tag, up to two stable ADDRESSES and one
        /// number. Every row either native reward page draws fits that shape and nothing else does —
        /// <c>UIModuleSiteEncounters.ShowReward</c>:361-512 is seventeen blocks whose payload is always a
        /// live reference, an int, or both (diplomacy = two parties + a delta; damaged aircraft = a vehicle
        /// + a number; skill points = a number alone), and <c>RewardsController.SetReward</c>:97-117 adds
        /// three more of the same shape. So the codec has ONE row and one table, not one branch per kind:
        /// a new row kind is a table entry, never a new wire concept.</summary>
        internal struct Row { public byte Kind; public string A, B; public int Value; }

        /// <summary>What the wire says, before any of it is resolved against a peer's model. PURE — no
        /// <c>GeoLevelController</c> is touched — because 0xBB arrives while the receiving peer is still in
        /// TACTICAL (there is no geoscape to resolve against until <see cref="StampMirroredOutcome"/> runs,
        /// GeoLevelController:703) and 0xBD is stashed per event id until its page is built. Decode is
        /// therefore the wire, <see cref="Build"/> is the resolution, and only the second one needs a
        /// level.</summary>
        internal sealed class RewardWire
        {
            public ResourcePack Resources = new ResourcePack();
            public ItemStorage Items = new ItemStorage();
            public readonly List<Row> Rows = new List<Row>();
        }

        // internal, not private: it appears in RowKinds' type, and a field may not be more accessible than
        // the types in its signature (CS0053).
        internal delegate void EmitRow(byte kind, string a, string b, int value);

        /// <summary>The one table: per row kind, how the HOST reads it off the granted
        /// <c>GeoFactionRewardApplyResult</c> and how a mirroring peer writes it back into its own. The two
        /// halves sit on ONE line each so they cannot drift apart — the failure mode a hand-written
        /// encoder/decoder pair always eventually reaches. <c>Read</c> returns false when an address did not
        /// resolve on this peer; the caller counts those and says so once (a null in any of these lists
        /// NREs inside the native renderer's own foreach, and a null Dictionary key throws outright).
        ///
        /// COMPANION LISTS ARE FILLED IN THE SAME LINE, deliberately: <c>ShowReward</c> GATES on the
        /// ApplyResult but ITERATES <c>geoEvent.ChoiceReward</c> for resources (:411/:413), units
        /// (:403/:405) and revealed sites (:419/:421), so filling one object opens the branch and then
        /// renders nothing. Both objects are written where the kind is declared, not in a second pass.
        ///
        /// <c>Member</c> is the <c>GeoFactionRewardApplyResult</c> field the row IS, declared rather than
        /// implied: RailCheck L101 resolves it against the real type and reads the <c>Write</c> lambda's IL
        /// to prove the row ships THAT field — a copy-pasted row that quietly ships its neighbour's list is
        /// otherwise invisible, and would show a player the wrong page with no error anywhere.</summary>
        internal static readonly (byte Id, string Member, Action<GeoFactionRewardApplyResult, EmitRow> Write,
                                  Func<GeoLevelController, GeoFactionReward, Row, bool> Read)[] RowKinds =
        {
            // ── the encounter outcome page (UIModuleSiteEncounters.ShowReward:361-512) ──
            (1, "Diplomacy", (r, e) => { foreach (var d in r.Diplomacy) e(1, Ref(d.Party), Ref(d.Target), d.Value); },
                (geo, w, row) =>
                {
                    // Party decides WHICH sentence is drawn (:383-391); an unresolved one would hand
                    // AddRewardText a null string. Target may legitimately be null — the game's own
                    // one-arg ctor builds such rows (RewardDiplomacyChange.cs:20-24).
                    if (!(Deref(geo, row.A) is IDiplomaticParty party)) return false;
                    w.ApplyResult.Diplomacy.Add(new RewardDiplomacyChange(party, Deref(geo, row.B) as IDiplomaticParty, row.Value));
                    return true;
                }),
            (2, "Units", (r, e) => { foreach (var u in r.Units) e(2, Ref(u.Unit), "", 0); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoCharacter c)) return false;
                    w.ApplyResult.Units.Add(new RewardNewUnit(c, null));   // Container: renderer never reads it
                    w.Units.Add(c);                                        // the list :405 iterates
                    return true;
                }),
            (3, "RevealedSites", (r, e) => { foreach (var s in r.RevealedSites) e(3, Ref(s), "", 0); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoSite s)) return false;
                    w.ApplyResult.RevealedSites.Add(s);
                    w.RevealedSites.Add(s);                                // the list :421 iterates (map markers)
                    return true;
                }),
            (4, "DamagedAircrafts", (r, e) => { foreach (var kv in r.DamagedAircrafts) e(4, Ref(kv.Key), "", kv.Value); },
                (geo, w, row) => Deref(geo, row.A) is GeoVehicle v && Put(w.ApplyResult.DamagedAircrafts, v, row.Value)),
            (5, "DamagedSoldiers", (r, e) => { foreach (var kv in r.DamagedSoldiers) e(5, Ref(kv.Key), "", kv.Value); },
                (geo, w, row) => Deref(geo, row.A) is GeoCharacter c && Put(w.ApplyResult.DamagedSoldiers, c, row.Value)),
            (6, "TiredSoldiers", (r, e) => { foreach (var kv in r.TiredSoldiers) e(6, Ref(kv.Key), "", kv.Value); },
                (geo, w, row) => Deref(geo, row.A) is GeoCharacter c && Put(w.ApplyResult.TiredSoldiers, c, row.Value)),
            (7, "DamageZones", (r, e) => { foreach (var kv in r.DamageZones) e(7, Ref(kv.Key), "", kv.Value); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoHavenZone z)) return false;
                    w.ApplyResult.DamageZones.Add(new KeyValuePair<GeoHavenZone, int>(z, row.Value));
                    return true;
                }),
            (8, "ChangeHavenPopulation", (r, e) => { foreach (var kv in r.ChangeHavenPopulation) e(8, Ref(kv.Key), "", kv.Value); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoHaven h)) return false;
                    w.ApplyResult.ChangeHavenPopulation.Add(new KeyValuePair<GeoHaven, int>(h, row.Value));
                    return true;
                }),
            (9, "ChangeMaxDiplomacyState", (r, e) => { foreach (var c in r.ChangeMaxDiplomacyState) e(9, Ref(c.Faction), "", (int)c.State); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoFaction f)) return false;
                    w.ApplyResult.ChangeMaxDiplomacyState.Add(new RewardMaxDiplomacyStateChange
                    { Faction = f, State = (PartyDiplomacyState)row.Value });
                    return true;
                }),
            (10, "FactionDiplomacyObjectiveChanged", (r, e) => { foreach (var f in r.FactionDiplomacyObjectiveChanged) e(10, Ref(f), "", 0); },
                (geo, w, row) => Deref(geo, row.A) is GeoFaction f && Add(w.ApplyResult.FactionDiplomacyObjectiveChanged, f)),
            (11, "SpawnedHavenDefensesAt", (r, e) => { foreach (var s in r.SpawnedHavenDefensesAt) e(11, Ref(s), "", 0); },
                (geo, w, row) => Deref(geo, row.A) is GeoSite s && Add(w.ApplyResult.SpawnedHavenDefensesAt, s)),
            (12, "NewPhoenixBase", (r, e) => { if (r.NewPhoenixBase != null) e(12, Ref(r.NewPhoenixBase), "", 0); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is GeoPhoenixBase b)) return false;
                    w.ApplyResult.NewPhoenixBase = b;
                    return true;
                }),
            // Refless rows — the same shape with both addresses empty, so scalars need no second mechanism.
            (13, "FactionSkillPoints", (r, e) => { if (r.FactionSkillPoints != 0) e(13, "", "", r.FactionSkillPoints); },
                (geo, w, row) => { w.ApplyResult.FactionSkillPoints = row.Value; return true; }),
            (14, "AllSoldiersDamage", (r, e) => { if (r.AllSoldiersDamage != 0) e(14, "", "", r.AllSoldiersDamage); },
                (geo, w, row) => { w.ApplyResult.AllSoldiersDamage = row.Value; return true; }),
            (15, "AllSoldiersTiredness", (r, e) => { if (r.AllSoldiersTiredness != 0) e(15, "", "", r.AllSoldiersTiredness); },
                (geo, w, row) => { w.ApplyResult.AllSoldiersTiredness = row.Value; return true; }),

            // ── the post-mission modal (RewardsController.SetReward:97-117) ──
            (16, "Vehicles", (r, e) => { foreach (var v in r.Vehicles) e(16, Ref(v), "", 0); },
                (geo, w, row) => Deref(geo, row.A) is GeoVehicle v && Add(w.ApplyResult.Vehicles, v)),
            (17, "Research", (r, e) => { foreach (var re in r.Research) e(17, Ref(re.ReserachDef), "", re.Progress); },
                (geo, w, row) =>
                {
                    if (!(Deref(geo, row.A) is ResearchDef d)) return false;
                    w.ApplyResult.Research.Add(new RewardResearchElement { ReserachDef = d, Progress = row.Value });
                    return true;
                }),
        };

        private static bool Put<T>(Dictionary<T, int> d, T key, int v) { d[key] = v; return true; }
        private static bool Add<T>(List<T> l, T v) { l.Add(v); return true; }

        // ─── Addressing (law 2): one Ref, one inverse, no per-row-kind identity scheme ───

        /// <summary>The stable address of anything a reward row can point at. Root entities (faction, site,
        /// vehicle, character) and haven zones are the rail's OWN refs, verbatim — no second naming scheme.
        /// The three kinds the root table does not name are all SITE-OWNED components reached by the site's
        /// own <c>GetComponent</c> (the same dispatch <c>GeoSite.RecordInstanceData</c> uses, GeoSite.cs
        /// :1501-1525), so they ride the site id plus a kind tag; a haven leader has no site back-ref at
        /// all, so it rides its own serialized <c>GeoUnitId</c>. "" = not addressable — the row still ships
        /// and the receiver drops it out loud, rather than the host silently shipping half a page.</summary>
        internal static string Ref(object o)
        {
            switch (o)
            {
                case null: return "";
                case GeoHavenLeader l: return "HL#" + (int)l.Id;
                case GeoHaven h: return h.Site == null || h.Site.SiteId < 0 ? "" : "HV#" + h.Site.SiteId;
                case GeoPhoenixBase b: return b.Site == null || b.Site.SiteId < 0 ? "" : "PB#" + b.Site.SiteId;
                case BaseDef d: return "D#" + d.Guid;
                default: return IdentityResolver.RootRef(o) ?? "";
            }
        }

        /// <summary>The inverse, against THIS peer's own live model. Null when the address does not resolve
        /// here — never a fabricated stand-in, because every consumer of one is a native renderer.</summary>
        internal static object Deref(GeoLevelController geo, string r)
        {
            if (geo == null || string.IsNullOrEmpty(r)) return null;
            int h = r.IndexOf('#');
            if (h < 0) return null;
            string kind = r.Substring(0, h), id = r.Substring(h + 1);
            switch (kind)
            {
                case "HL": return LeaderById(geo, id);
                case "HV": return SiteComponent<GeoHaven>(geo, id);
                case "PB": return SiteComponent<GeoPhoenixBase>(geo, id);
                case "D": return GameUtl.GameComponent<DefRepository>()?.GetDef(id);
                default: return IdentityResolver.Resolve(geo, r, null);
            }
        }

        private static T SiteComponent<T>(GeoLevelController geo, string siteId) where T : Component =>
            IdentityResolver.Resolve(geo, "S#" + siteId, null) is GeoSite s ? s.GetComponent<T>() : null;

        /// <summary>A leader is owned by its haven and carries no back-ref (GeoHavenLeader.cs:11-23), so it
        /// is found the way the game finds one: through the site that holds the haven. Linear over sites,
        /// which is fine — a reward names at most a handful of leaders, once, when a page is built.</summary>
        private static GeoHavenLeader LeaderById(GeoLevelController geo, string id)
        {
            if (!int.TryParse(id, out int want) || geo.Map == null) return null;
            foreach (var site in geo.Map.AllSites)
            {
                var leader = site == null ? null : site.GetComponent<GeoHaven>()?.Leader;
                if (leader != null && (int)leader.Id == want) return leader;
            }
            return null;
        }

        // ─── Encode / Decode ───────────────────────────────────────────────

        /// <summary>[resCount:u16]([resourceType:i32][value:f32])* [itemCount:u16]([ItemRec])*
        /// [rowCount:u16]([kind:u8][refA][refB][value:i32])*. Resources ride as the game's own
        /// <c>ResourceType</c> enum (a def-order-free stable id) and items reuse <see cref="GeoItemCodec"/>'s
        /// record — def guid + count + charges + malfunction, law 2 addressing, no instance identity,
        /// because the panel draws a LIST and not the peer's storage.</summary>
        internal static void Encode(BinaryWriter w, GeoFactionRewardApplyResult result)
        {
            WriteHead(w, result?.Resources, result?.Items);
            var rows = new List<Row>();
            if (result != null)
                foreach (var k in RowKinds)
                    k.Write(result, (kind, a, b, v) => rows.Add(new Row { Kind = kind, A = a, B = b, Value = v }));
            w.Write((ushort)rows.Count);
            foreach (var row in rows)
            { w.Write(row.Kind); w.Write(row.A ?? ""); w.Write(row.B ?? ""); w.Write(row.Value); }
        }

        /// <summary>Resources+items only, no rows — the shape a caller that has nothing else to say writes.
        /// Same wire either way (an empty row block), so the two overloads are one format.</summary>
        internal static void Encode(BinaryWriter w, ResourcePack resources, ItemStorage items)
        {
            WriteHead(w, resources, items);
            w.Write((ushort)0);
        }

        private static void WriteHead(BinaryWriter w, ResourcePack resources, ItemStorage items)
        {
            var res = resources == null ? new List<ResourceUnit>() : resources.Values;
            w.Write((ushort)res.Count);
            foreach (var u in res) { w.Write((int)u.Type); w.Write(u.Value); }

            var list = items == null ? new List<GeoItem>() : items.ToList();
            w.Write((ushort)list.Count);
            foreach (var it in list) GeoItemCodec.WriteRec(w, GeoItemCodec.RecOf(it));
        }

        /// <summary>The inverse, PURE. Items whose def does not resolve are DROPPED by
        /// <c>GeoItemCodec.FromRec</c>, loudly — mod parity (law 10) says that cannot happen, and a null in
        /// the list would NRE inside the native renderer's own <c>foreach</c>.</summary>
        internal static RewardWire DecodeRaw(BinaryReader r)
        {
            var wire = new RewardWire();
            int n = r.ReadUInt16();
            for (int i = 0; i < n; i++)
            {
                var type = (ResourceType)r.ReadInt32();
                float value = r.ReadSingle();
                wire.Resources.Add(new ResourceUnit(type, value));
            }
            int m = r.ReadUInt16();
            for (int i = 0; i < m; i++)
            {
                var item = GeoItemCodec.FromRec(GeoItemCodec.ReadRec(r)) as GeoItem;
                if (item != null) wire.Items.AddItem(item);
            }
            int k = r.ReadUInt16();
            for (int i = 0; i < k; i++)
                wire.Rows.Add(new Row { Kind = r.ReadByte(), A = r.ReadString(), B = r.ReadString(), Value = r.ReadInt32() });
            return wire;
        }

        /// <summary>Back-compat entry for callers that want only the two lists (RailCheck L83). Reads the
        /// SAME wire, so a payload carrying rows still round-trips to the byte.</summary>
        internal static void Decode(BinaryReader r, out ResourcePack resources, out ItemStorage items)
        {
            var wire = DecodeRaw(r);
            resources = wire.Resources;
            items = wire.Items;
        }

        /// <summary>Turn a decoded wire into the pair of objects the two native pages read, resolved against
        /// THIS peer's model. NOTHING IS APPLIED: every value here already reached this peer as ordinary
        /// replicated state (the wallet and storage on the value rail, and diplomacy as
        /// <c>PartyDiplomacy+Relation._diplomacy</c>, a covered leaf on both the faction and the haven
        /// leader — see docs/rail-baseline.txt:140). What does not cross as state is "what THIS choice gave
        /// you", the delta, and that is the only thing this builds.</summary>
        internal static GeoFactionReward Build(GeoLevelController geo, RewardWire wire, string reason)
        {
            var applyResult = new GeoFactionRewardApplyResult { Resources = wire?.Resources ?? new ResourcePack(),
                                                                Items = wire?.Items ?? new ItemStorage() };
            // BOTH halves, and they are read by DIFFERENT methods: HasRewards():71 gates on the ApplyResult's
            // own lists while the encounter page ITERATES the reward's (:413/:405/:421). Filling one leaves
            // either a panel that refuses to draw or a panel that draws nothing.
            var reward = new GeoFactionReward
            {
                Reason = reason,
                Resources = applyResult.Resources,
                Items = applyResult.Items,
                ApplyResult = applyResult,
            };
            if (wire == null || wire.Rows.Count == 0) return reward;

            int dropped = 0;
            foreach (var row in wire.Rows)
            {
                bool handled = false;
                foreach (var k in RowKinds)
                    if (k.Id == row.Kind) { handled = k.Read(geo, reward, row); break; }
                if (!handled) dropped++;
            }
            if (dropped > 0)
                Debug.LogWarning("[MP][outcome] " + dropped + " of " + wire.Rows.Count + " reward rows did not " +
                                 "resolve on this peer and were dropped — the page will list less than the host's. " +
                                 "An address that resolves nowhere is either a mod-parity break (law 10) or state " +
                                 "that has not landed yet; a fabricated stand-in would NRE inside the native renderer.");
            return reward;
        }

        // ─── CLIENT ─────────────────────────────────────────────────────────

        /// <summary>Returns true when the surface was consumed. Client-only: the host already drew its own.</summary>
        internal static bool HandleInbound(NetworkEngine engine, ulong senderPeerId, byte surfaceId, byte[] payload)
        {
            if (surfaceId != SurfaceIds.GeoMissionOutcome) return false;
            if (engine == null || engine.IsHost) return true;
            try
            {
                using (var ms = new MemoryStream(payload ?? new byte[0]))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    uint seq = r.ReadUInt32();
                    if (!Seq.ShouldApply(SurfaceIds.GeoMissionOutcome, seq)) return true; // stale (law 7)
                    // Wire only — this peer is still in TACTICAL, so there is no geoscape to resolve the row
                    // addresses against yet. Build() does that at StampMirroredOutcome, where there is one.
                    _incoming = DecodeRaw(r);
                    Seq.Mark(SurfaceIds.GeoMissionOutcome, seq);
                    Debug.Log("[MP][outcome] CLIENT mission reward seq=" + seq + " res=" + Count(_incoming.Resources) +
                              " items=" + Count(_incoming.Items) + " rows=" + _incoming.Rows.Count +
                              " — held for this peer's own mission completion");
                }
            }
            catch (Exception ex) { Debug.LogError("[MP][outcome] inbound failed: " + ex); }
            return true;
        }

        // GeoMission.Reward has a private setter (GeoMission.cs:145) — the one reason reflection appears here.
        private static readonly System.Reflection.MethodInfo RewardSetter =
            AccessTools.PropertySetter(typeof(GeoMission), "Reward");

        /// <summary>The client's stand-in for the blocked <c>Complete</c>: the game's own bookkeeping flag,
        /// plus the host's reward so the native panel has something to draw. Called from
        /// <see cref="Multiplayer.Tactical.ClientMissionResultGate"/>, i.e. from
        /// <c>GeoLevelController</c>:703 — FIVE LINES before :708 hands the same mission to
        /// <c>UIStateInitial</c>, in the same coroutine step, which is what makes a stash safe here and why
        /// no repaint seam is needed: nothing has read the mission yet.</summary>
        internal static void StampMirroredOutcome(GeoMission mission)
        {
            if (mission == null) return;
            mission.CompleteSilently();   // the game's own "record it, apply nothing" (GeoMission.cs:284)

            var wire = _incoming;
            _incoming = null;
            var reward = wire == null ? null : Build(GeoLevel(), wire, "Mission");
            if (reward == null)
            {
                // The panel still OPENS (the gate above is what decides that) and reads "nothing gained".
                // Loud, because the alternative reading — "this mission really granted nothing" — is one a
                // player cannot tell apart from a lost message.
                Debug.LogWarning("[MP][outcome] no host reward payload had arrived when this peer completed its " +
                                 "mission — the outcome panel will open EMPTY. The 0xBB raise is sent as the host " +
                                 "applies the reward, so this means the host had not got there yet.");
                reward = new GeoFactionReward { Reason = "Mission" };
            }
            if (RewardSetter == null)
            {
                Debug.LogError("[MP][outcome] GeoMission.Reward setter did not resolve — the outcome panel " +
                               "renders whatever this peer's mission already held, which is nothing.");
                return;
            }
            RewardSetter.Invoke(mission, new object[] { reward });
            Debug.Log("[MP][outcome] CLIENT stamped mission outcome — CompleteSilently + the host's reward " +
                      "(res=" + Count(reward.Resources) + " items=" + Count(reward.Items) + "); the native " +
                      "UIStateInitial:101 branch now runs the outcome modal and the resupply screen.");
        }

        private static int Count(ResourcePack p) => p?.Values?.Count ?? 0;
        private static int Count(ItemStorage s) => s == null ? 0 : s.ToList().Count;

        private static GeoLevelController GeoLevel()
        {
            try { return GameUtl.CurrentLevel()?.GetComponent<GeoLevelController>(); }
            catch { return null; }
        }
    }

    /// <summary>The host-side capture. POSTFIX so the reward is already applied and the result is the GRANTED
    /// amount; host-gated inside <see cref="MissionOutcomeMirror.HostBroadcast"/>, so a client reaching this
    /// (it cannot — its <c>Complete</c> is gated one level up) would still send nothing.</summary>
    [HarmonyPatch(typeof(GeoFaction), nameof(GeoFaction.OnMissionRewardApplied))]
    internal static class MissionRewardBroadcast
    {
        private static void Postfix(GeoFactionRewardApplyResult result) =>
            MissionOutcomeMirror.HostBroadcast(result);
    }
}
