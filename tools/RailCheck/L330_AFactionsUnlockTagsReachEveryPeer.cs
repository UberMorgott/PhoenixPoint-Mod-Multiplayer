using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.GameTags;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View.ViewModules;

namespace RailCheck
{
    /// <summary>
    /// L330 — A FACTION'S GAME TAGS CROSS, AND THE FLAG DERIVED FROM THEM ACTUALLY FLIPS.
    ///
    /// THE DEFECT (2026-08-08 co-op session): on a client the roster's RECRUITS tab was permanently dead
    /// and whole factions were missing from the diplomacy screen, long after the research that grants them
    /// had completed on the host.
    ///
    /// NEITHER IS A REPAINT DEFECT — repainting would have redrawn the same disabled tab. Both are DERIVED
    /// from faction GAME TAGS. <c>GeoPhoenixFaction.RecruitmentFunctionalityUnlocked</c> (:124) is written
    /// ONLY by <c>CheckForUiUnlocks()</c>, from <c>GameTags.Contains(FactionDef.RecruitFunctionalityTag)</c>
    /// (:1647-1652), and read by <c>UIModuleGeoRosterTabs.CheckAvailableTabs</c>:83-84.
    /// <c>IsFactionDiscovered</c>:1200-1212 reads the DiscoveredTag the same way, and
    /// <c>UIModuleDiplomacySection</c>:74-79 hides an entire faction section when it is false. Natively the
    /// tags arrive through <c>UnlockFunctionalityResearchReward.GiveReward</c> → <c>GeoFaction.AddTag</c> →
    /// <c>_factionTags.AddNotContained</c> (GeoFaction.cs:1634-1637) — a reward chain the client never
    /// runs. And <c>GameTags</c> was EXCLUDED from the rail on all three faction types
    /// ("bridge-unresolved"), so the flag was FALSE ON THE CLIENT FOREVER.
    ///
    /// THIS LAW ASSERTS THE OUTCOME, NOT A CALL. Arm (b) does not check that anything was invoked: it
    /// computes the EXACT expression <c>CheckForUiUnlocks</c> assigns from — <c>GameTags.Contains(tag)</c>
    /// over the game's own <c>GameTagsProviderList</c> — before and after driving the rail's own generic
    /// apply, and requires it to go false → true. That is the tab's enabled state, one dereference early.
    ///
    /// ARMS
    ///   (a) <c>unlock-tag-never-crosses</c> — <c>GameTags</c> is a SHIPPING field resolved onto
    ///       <c>_factionTags</c> on GeoFaction, GeoPhoenixFaction and GeoAlienFaction alike. Three types
    ///       and not one, because the alias is keyed on the base and its reach through the subclasses is
    ///       the claim.
    ///   (b) <c>unlock-flag-stays-false</c> — THE OUTCOME, executed: the generic apply lands a tag in a
    ///       real <c>GameTagsList</c> registered as a provider of a real <c>GameTagsProviderList</c>, and
    ///       the value the flag is assigned from flips.
    ///   (c) <c>unlock-never-recomputed</c> — the provider's <c>Changed</c> event fires. It is subscribed
    ///       in <c>GeoPhoenixFaction.OnLevelStart</c>:306 and is the ONLY thing that re-runs
    ///       <c>CheckForUiUnlocks</c> (:1741). Tags that land silently leave the flag at its old value
    ///       until something else happens to recompute it, which on an open screen is nothing.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • delete the <c>GeoFaction.GameTags → _factionTags</c> twin-alias row → <c>unlock-tag-never-crosses</c>
    ///   • make ApplyListCore skip its Clear+Add for an ICollection&lt;T&gt; container → <c>unlock-flag-stays-false</c>
    ///   • rename the flag / the tab reader / the storage field → <c>premise-changed</c>
    /// </summary>
    internal static class L330_AFactionsUnlockTagsReachEveryPeer
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The one probe owner: a live <c>GameTagsList</c> behind a plain field, which is the
        /// shape the rail field resolves to (<c>GeoFaction._factionTags</c>).</summary>
        private sealed class TagOwner { public GameTagsList Tags = new GameTagsList(); }

        internal static IEnumerable<string> Check()
        {
            var store = typeof(GeoFaction).GetField("_factionTags", All);
            var view = typeof(GeoFaction).GetProperty("GameTags", All);
            var flag = typeof(GeoPhoenixFaction).GetField("RecruitmentFunctionalityUnlocked", All);
            var tabs = typeof(UIModuleGeoRosterTabs).GetMethod("CheckAvailableTabs", All);
            var recompute = typeof(GeoPhoenixFaction).GetMethod("CheckForUiUnlocks", All);
            var dto = RailMeta.FindBridge(typeof(GeoPhoenixFaction));

            if (store == null || store.FieldType != typeof(GameTagsList) ||
                view == null || view.PropertyType != typeof(GameTagsProviderList) ||
                flag == null || flag.FieldType != typeof(bool) ||
                tabs == null || recompute == null || dto == null ||
                dto.GetField("GameTags", All) == null)
            {
                yield return "L330 premise-changed: one of GeoFaction._factionTags (GameTagsList), " +
                             "GeoFaction.GameTags (GameTagsProviderList), " +
                             "GeoPhoenixFaction.RecruitmentFunctionalityUnlocked, " +
                             "GeoPhoenixFaction.CheckForUiUnlocks, UIModuleGeoRosterTabs.CheckAvailableTabs " +
                             "or the DTO's GameTags member no longer resolves. The whole chain this law " +
                             "walks — tag store -> provider view -> derived flag -> tab — has moved, and " +
                             "every arm below would pass while saying nothing.";
                yield break;
            }

            // ── (a) the field SHIPS, on all three faction types ──────────────────────────────────────
            foreach (var owner in new[] { typeof(GeoFaction), typeof(GeoPhoenixFaction), typeof(GeoAlienFaction) })
            {
                var f = RailType.Get(owner)?.FieldByName("GameTags");
                if (f == null || f.Class != FieldClass.LeafList || f.LiveAlias != "_factionTags")
                    yield return "L330 unlock-tag-never-crosses: " + owner.Name + ".GameTags is " +
                                 (f == null ? "absent from the rail table"
                                            : f.Class + (f.Class == FieldClass.Excluded ? " (" + f.Exclude + ")" : "") +
                                              " -> live '" + (f.LiveAlias ?? "<none>") + "'") +
                                 ", not a LeafList resolved onto _factionTags. Every research-granted unlock " +
                                 "and every 'faction discovered' flag is DERIVED from this one member, so " +
                                 "while it does not ride, the client's recruit tab is dead and its diplomacy " +
                                 "sections are missing — with nothing in any log to say so.";
            }

            // ── (b)+(c) THE OUTCOME, over the game's own tag classes ─────────────────────────────────
            // A def cannot be constructed in a console host, but the DefRef codec and the tag list both
            // only ever read the reference itself — same move as L192's uninitialised PPFactionDef. ONE
            // tag, deliberately: Unity treats an uninitialised Object as null-equal, so two probe tags
            // would compare equal to each other and the arm would test nothing.
            var tag = (GameTagDef)FormatterServices.GetUninitializedObject(typeof(GameTagDef));
            var owner2 = new TagOwner();
            var provider = new GameTagsProviderList();
            provider.AddProvider(owner2.Tags);          // exactly GeoFaction.Init:346
            int changed = 0;
            provider.Changed += _ => changed++;          // exactly GeoPhoenixFaction.OnLevelStart:306

            // This IS the expression CheckForUiUnlocks:1647 assigns the flag from.
            bool before = provider.Contains(tag);

            var field = new RailField
            {
                Name = "GameTags", Class = FieldClass.LeafList,
                ValueType = typeof(GameTagsList), ElemType = typeof(GameTagDef),
                Fi = typeof(TagOwner).GetField("Tags", All),
            };
            Exception threw = null;
            try { RailMeta.ApplyList(owner2, field, new List<object> { tag }); }
            catch (Exception ex) { threw = ex; }

            bool after = threw == null && provider.Contains(tag);

            if (before || !after)
                yield return "L330 unlock-flag-stays-false: after the rail's own generic apply landed the " +
                             "tag, GameTags.Contains(tag) is " + after + " (was " + before + ")" +
                             (threw == null ? "" : "; the apply threw " + threw.GetType().Name + ": " + threw.Message) +
                             ". That expression is verbatim what CheckForUiUnlocks:1647 assigns " +
                             "RecruitmentFunctionalityUnlocked from and what CheckAvailableTabs:83-84 turns " +
                             "into the Recruits tab's interactable state — so this is the dead tab itself, " +
                             "not a proxy for it.";

            if (threw == null && changed == 0)
                yield return "L330 unlock-never-recomputed: the tag landed but GameTagsProviderList.Changed " +
                             "never fired. That event is subscribed in GeoPhoenixFaction.OnLevelStart:306 and " +
                             "is the ONLY thing that re-runs CheckForUiUnlocks (:1741), so a silent apply " +
                             "leaves every unlock flag at its previous value — the tab would stay dead on an " +
                             "open screen even though the tag itself had arrived.";
        }
    }
}
