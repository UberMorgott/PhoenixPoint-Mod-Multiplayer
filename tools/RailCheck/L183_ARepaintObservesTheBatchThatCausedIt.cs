using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L183 — A REPAINT OBSERVES THE BATCH THAT LANDED THE STATE, AND SAYS WHICH OF THE THREE THINGS IT SAW.
    ///
    /// THE REPORT (2026-08-07 co-op session, both logs). A site's mission was created on the client and the
    /// client's own repaint diagnostic said, five times across the session, that the site had no mission:
    ///   23:09:52.626  structural create 'S#104.SerializationData.ActiveMission' applied (GeoCustomMission)
    ///   23:09:52.626  [MP][site] repaint S#104 state=Functioning activeMission=none
    /// Read straight, that says the mirror never got the mission. It is not what happened, and the two ways
    /// it lied are the two halves of this law.
    ///
    /// (1) THE LINE COULD NOT TELL ABSENT FROM UNFINISHED. It rendered
    /// <c>s.ActiveMission?.MissionDef?.name ?? "none"</c>, and a descend create ships the mission's TYPE
    /// and nothing else — every member of it rides a LATER packet by design
    /// (<c>GenericApplier.ApplyDescendCreate</c>). So a mission that exists, is constructed, and has been
    /// handed to <c>GeoSite.RegisterMission</c>:1629 prints the same single word as a site with no mission
    /// at all. A diagnostic that collapses two states manufactures its own root cause; this one did, and an
    /// RCA was written on it. <see cref="GenericApplier.MissionLabel"/> now has THREE outcomes and arm (a)
    /// drives all three.
    ///
    /// (2) THE SITE WAS NEVER MARKED AGAIN WHEN THE VALUES ARRIVED. <c>MarkOrderChange</c> decided the
    /// marker repaint from the TYPE of the entity it had just written, and the batch that fills a mission
    /// writes a <c>GeoMission</c> — so it marked nothing, and the create-time repaint stayed the client's
    /// last word on that site. The host log closes it: the host answered the client's own backfill request
    /// (<c>DiffEngine tick … changed=6</c> at 23:09:45.461 host clock, 362 ms after the create it sent),
    /// the client applied it, and NOTHING said so. <c>S#293</c> looked healthy only because an unrelated
    /// site-level leaf happened to ride the same batch 9 ms later.
    ///
    /// DEPTH IS NOT A GATE, for the reason <c>SiteWriteConsequences</c> already gives about the field NAME:
    /// the marker repaint is one bool on a MonoBehaviour (<c>GeoSiteVisualsController.Refresh</c>:202 sets
    /// <c>_refresh</c>, consumed by <c>Update</c>:701-714 into <c>RefreshSiteVisuals</c> +
    /// <c>RefreshMissionVisuals</c>), while the parked-vehicle RE-SEED is a derivation and must keep its
    /// named authoritative leaf. So this law asserts the repaint widened and the re-seed did NOT.
    ///
    /// Falsify: make <see cref="GenericApplier.MissionLabel"/> return "none" for a mission whose def has
    /// not landed → <c>L183 unfinished-reads-as-absent</c>; make it claim a mission when there is none →
    /// <c>L183 absent-reads-as-present</c>; make <see cref="GenericApplier.SiteRootKeyOf"/> return null for
    /// a descend path → <c>L183 descendant-write-repaints-nothing</c>; make it answer for a vehicle or a
    /// character path → <c>L183 foreign-root-claimed-as-a-site</c>; stop calling it from MarkOrderChange →
    /// <c>L183 widening-is-decorative</c>; drop the Refresh() call out of the flush →
    /// <c>L183 premise-changed</c>.
    /// </summary>
    internal static class L183_ARepaintObservesTheBatchThatCausedIt
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var ga = typeof(GenericApplier);
            var label = ga.GetMethod("MissionLabel", All, null,
                            new[] { typeof(bool), typeof(string), typeof(string) }, null);
            var rootOf = ga.GetMethod("SiteRootKeyOf", All, null, new[] { typeof(string) }, null);
            var mark = ga.GetMethod("MarkOrderChange", All);
            var flush = ga.GetMethod("FlushOrderReseed", All);
            var refresh = typeof(PhoenixPoint.Geoscape.View.GeoSiteVisualsController).GetMethod("Refresh", All);
            if (label == null || rootOf == null || mark == null || flush == null || refresh == null)
            {
                yield return "L183 premise-changed: GenericApplier.MissionLabel / SiteRootKeyOf / " +
                             "MarkOrderChange / FlushOrderReseed or GeoSiteVisualsController.Refresh no " +
                             "longer resolves. Nothing else makes a site repaint after the packet that " +
                             "actually fills its mission — the create's own repaint necessarily runs " +
                             "against an empty one, because a descend create carries the TYPE only.";
                yield break;
            }

            // ── (a) THE LINE: three outcomes, never two ──────────────────────
            if (GenericApplier.MissionLabel(false, null, null) != "none")
                yield return "L183 absent-reads-as-present: a site with NO active mission no longer reads " +
                             "'none'. That word is the one the log is grepped for and every prior session's " +
                             "lines are compared against.";
            if (GenericApplier.MissionLabel(true, "GeoCustomMission", null) == "none")
                yield return "L183 unfinished-reads-as-absent: a site that HAS a mission whose def has not " +
                             "landed yet prints the same word as a site with no mission at all. That is the " +
                             "2026-08-07 defect verbatim: S#104 printed 'activeMission=none' five times " +
                             "while its mission existed, was registered natively via GeoSite.RegisterMission " +
                             "and had had the host's answer to the client's own backfill request applied to " +
                             "it. The RCA that followed said the client never received the mission.";
            if (GenericApplier.MissionLabel(true, "GeoAmbushMission", "AmbushBandits_CustomMissionTypeDef")
                != "AmbushBandits_CustomMissionTypeDef")
                yield return "L183 finished-mission-unnamed: a fully landed mission no longer prints its def " +
                             "name. That name is the only thing joining this line to the host's " +
                             "'structural create … sent' line for the same root.";

            // ── (b) THE MARKING: a write DEEPER than the site still repaints it ──
            if (GenericApplier.SiteRootKeyOf("S#104.SerializationData.ActiveMission.MissionDef") != "S#104")
                yield return "L183 descendant-write-repaints-nothing: the batch that fills a site's mission " +
                             "no longer names that site. The create-time repaint then stays the client's " +
                             "last word on it, and the create-time repaint is the one that by construction " +
                             "cannot see any of the mission's values — they ride the NEXT packet.";
            if (GenericApplier.SiteRootKeyOf("S#293") != null)
                yield return "L183 site-itself-taken-twice: a write ON the site now also goes through the " +
                             "descendant path. That branch skips SiteWriteConsequences, so the " +
                             "authoritative-leaf re-seed of every vehicle parked there is silently lost.";
            if (GenericApplier.SiteRootKeyOf("U#4.Identity.HairColorDef") != null ||
                GenericApplier.SiteRootKeyOf("V#1@8be7e872.CurrentSite") != null ||
                GenericApplier.SiteRootKeyOf("GL.Timing.Now") != null)
                yield return "L183 foreign-root-claimed-as-a-site: a character, vehicle or global path is " +
                             "being read as a site root. Every such write would then resolve a root that is " +
                             "not a GeoSite, on the hot per-entry path, for nothing.";
            if (GenericApplier.SiteRootKeyOf(null) != null || GenericApplier.SiteRootKeyOf("") != null ||
                GenericApplier.SiteRootKeyOf("S#") != null)
                yield return "L183 degenerate-path-accepted: an empty or truncated path answers with a root. " +
                             "This runs on every applied entry — it must never throw and never invent a key.";

            // ── (c) the seam: the widening is actually consulted ─────────────
            if (!Program.Callees(mark, ga.Assembly).Any(c => c.MetadataToken == rootOf.MetadataToken))
                yield return "L183 widening-is-decorative: MarkOrderChange no longer consults " +
                             "SiteRootKeyOf, so arm (b) is proved about a decision the live seam does not " +
                             "make. This is the exact shape the repo keeps paying for (L132, L137, L179).";
            if (!Program.Callees(flush, ga.Assembly).Any(c => c.MetadataToken == label.MetadataToken))
                yield return "L183 line-is-decorative: the repaint flush no longer renders through " +
                             "MissionLabel, so arm (a) proves nothing about what the log actually says.";

            // ── (d) the GAME premise the whole repaint stands on ─────────────
            if (!Program.Callees(flush, typeof(PhoenixPoint.Geoscape.View.GeoSiteVisualsController).Assembly)
                        .Any(c => c.MetadataToken == refresh.MetadataToken))
                yield return "L183 premise-changed: the repaint flush no longer calls the game's own " +
                             "GeoSiteVisualsController.Refresh. Law 11's repaint always comes from the " +
                             "decompile — a hand-rolled material swap has broken this three times.";
        }
    }
}
