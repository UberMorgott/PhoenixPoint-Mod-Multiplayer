using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.GameTagsTypes;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Sites;

namespace RailCheck
{
    /// <summary>
    /// L273 — THE HAVEN ZONE CROSSES THE WIRE AS AN INDEX, AND THE HOST NEVER RE-DERIVES IT BY TAG.
    ///
    /// WHY IT CANNOT BE A KEY. <c>GeoHavenZone</c> has no identity of its own and never will: it declares no
    /// id member (its whole public surface is <c>Def</c>, <c>ZoneCount</c>, <c>Health</c>, <c>Haven</c>,
    /// <c>State</c>), it is CREATED by its haven (<c>GeoHaven.BuildZone</c> → <c>_zones.Insert</c>,
    /// GeoHaven.cs:410), and every other mention of one in the save graph is an ALIAS into that list —
    /// <c>IdentityResolver</c>'s own <c>HavenZoneRef</c> is a path, not a root. So the only thing both peers
    /// can name it by is its position in <c>haven.Zones</c>.
    ///
    /// WHY A TAG LOOKUP IS NOT AN EQUIVALENT. <c>GeoHavenZoneDef.AvailableMissionsTags</c> is a LIST and
    /// several zones of one haven can carry the same <c>MissionTypeTagDef</c> — the game's own derivation
    /// (<c>zone ?? Zones.FirstOrDefault(z =&gt; z.Def.AvailableMissionsTags.Contains(tag))</c>,
    /// GeoHaven.cs:1061) is a FALLBACK for callers that passed nothing, not a way to recover a zone somebody
    /// already chose. Re-running it host-side can pick a different zone than the one the player's brief modal
    /// was built from, and the zone is not decoration: the mission is constructed against it
    /// (<c>new GeoStealAircraftMission(def, Site, zone, vehicle)</c>) and the population, the defenders and
    /// the stolen aircraft all hang off it. The mission would be minted somewhere the player never looked.
    ///
    ///   (a) <c>zone-not-an-index</c> — the client seam resolves the chosen zone to a POSITION in
    ///       <c>haven.Zones</c> before it sends.
    ///   (b) <c>index-not-read</c> — the host reads that position back off the wire.
    ///   (c) <c>host-re-derives-by-tag</c> — the host handler reaches NO <c>FirstOrDefault</c>-shaped search
    ///       and never touches <c>AvailableMissionsTags</c>. Index -1 is passed on as a NULL zone so the GAME
    ///       does its own fallback exactly as in solo; that is not a re-derivation of ours.
    ///   (d) <c>zone-grew-an-identity</c> — the game-side premise. If <c>GeoHavenZone</c> ever gets a real id
    ///       member, the index becomes the wrong answer and this whole law should be re-opened rather than
    ///       silently kept.
    ///   (e) POSITIVE CONTROL, EXECUTED — arm (c)'s scan is run over <see cref="FakeSeam.DerivesByTag"/>,
    ///       which does exactly the forbidden lookup, and MUST come back red.
    ///
    /// Falsify (each verified RED, then restored): replace the <c>IndexOf</c> with a tag lookup in the prefix →
    /// (a) and, once the handler follows, (c); drop the <c>ReadInt16</c> → (b); empty
    /// <see cref="FakeSeam.DerivesByTag"/> → (e).
    /// </summary>
    internal static class L273_TheHavenZoneRidesAsAnIndex
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var prefix = typeof(HavenMissionGate).GetMethod("Prefix", All);
            var handler = typeof(MissionSync).GetMethod("HandlePrepareHaven", All);
            var zones = typeof(GeoHaven).GetProperty("Zones", All);
            if (prefix == null || handler == null || zones == null)
            {
                yield return "L273 premise-changed: HavenMissionGate.Prefix / MissionSync.HandlePrepareHaven / " +
                             "GeoHaven.Zones no longer resolve whole — the two ends of the 0xB8 prepare-haven " +
                             "wire and the list the index points into. Every arm below reads that trio.";
                yield break;
            }

            // ── (a) the client sends a POSITION ────────────────────────────────────────────────────
            if (!Program.CalleeSequence(prefix).Any(c => c != null && c.Name == "IndexOf"))
                yield return "L273 zone-not-an-index: HavenMissionGate.Prefix no longer resolves the chosen zone " +
                             "to its position in haven.Zones. A GeoHavenZone has no rail identity to send " +
                             "instead — it declares no id member and every reference to one in the save graph is " +
                             "an alias into that same list — so whatever replaced the index is either a key that " +
                             "does not exist or a tag the host must guess from.";

            // ── (b) the host reads it back ─────────────────────────────────────────────────────────
            if (!Program.CalleeSequence(handler).Any(c => c != null && c.Name == "ReadInt16"))
                yield return "L273 index-not-read: MissionSync.HandlePrepareHaven no longer reads the i16 zone " +
                             "index off the wire, so either the body shape drifted (and the reads after it are " +
                             "misaligned) or the host has gone back to working the zone out for itself.";

            // ── (c) and it does not work it out for itself ─────────────────────────────────────────
            foreach (var red in TagLookup(handler, "L273")) yield return red;

            // ── (d) the game-side premise ──────────────────────────────────────────────────────────
            var id = typeof(GeoHavenZone).GetMember("Id", All).Concat(typeof(GeoHavenZone).GetMember("ZoneId", All))
                                         .FirstOrDefault();
            if (id != null)
                yield return "L273 zone-grew-an-identity: GeoHavenZone now declares '" + id.Name + "'. The index " +
                             "was chosen because the zone had NOTHING stable to name it by; a real id is a better " +
                             "answer and the wire should carry it instead — re-open this law rather than keeping " +
                             "an index that is now merely the second-best key.";

            // ── (e) POSITIVE CONTROL, executed ─────────────────────────────────────────────────────
            if (!TagLookup(typeof(FakeSeam).GetMethod("DerivesByTag", All), "control").Any())
                yield return "L273 control-not-red: FakeSeam.DerivesByTag searches haven.Zones by mission tag and " +
                             "the scan did not flag it. Arm (c) is decorative — it would stay green over a host " +
                             "that picked the zone itself.";
        }

        /// <summary>The scan, run over the real handler in arm (c) and over <see cref="FakeSeam"/> in (e).</summary>
        private static IEnumerable<string> TagLookup(MethodBase m, string id)
        {
            if (m == null) yield break;
            foreach (var c in Program.CalleeSequence(m))
            {
                if (c == null) continue;
                bool search = c.Name == "FirstOrDefault" || c.Name == "First" ||
                              c.Name == "SingleOrDefault" || c.Name == "Single";
                bool tags = c.Name.IndexOf("AvailableMissionsTags", StringComparison.Ordinal) >= 0;
                if (!search && !tags) continue;
                yield return id + " host-re-derives-by-tag: the host reaches " + c.Name + " while resolving the " +
                            "haven zone. GeoHavenZoneDef.AvailableMissionsTags is a LIST and several zones of one " +
                            "haven can carry the same MissionTypeTagDef, so a tag search can return a DIFFERENT " +
                            "zone than the one the player's infiltration brief was built from — and the zone is " +
                            "what the mission is constructed against (population, defenders, the aircraft being " +
                            "stolen). The wire carries a position for exactly this reason; -1 means the caller " +
                            "passed no zone at all, which the host reproduces by passing NULL so the game runs " +
                            "its own fallback (GeoHaven.cs:1061), identical to solo.";
                yield break;
            }
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the forbidden host-side derivation, written out. Never executed;
            /// only its IL is read. The scan MUST flag it.</summary>
            internal static GeoHavenZone DerivesByTag(GeoHaven haven, MissionTypeTagDef tag) =>
                haven.Zones.FirstOrDefault(z => z.Def.AvailableMissionsTags.Contains(tag));
        }
    }
}
