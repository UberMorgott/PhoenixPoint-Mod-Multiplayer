using System;
using System.Collections.Generic;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L369 — A BASE RENAME REACHES THE MIRROR.
    ///
    /// <c>GeoPhoenixBase.RenameBase</c>:991-993 writes <c>Site.SiteName</c>, and the site's DTO carries that
    /// value under the member name <c>Name</c> (<c>GeoSite.RecordInstanceData</c>:1486 /
    /// <c>ProcessInstanceData</c>:1557). The generic resolver could not bridge the two by itself: the
    /// same-named live member <c>GeoSite.Name</c> (GeoSite.cs:213) is a get-only <c>=> LocalizedSiteName</c>
    /// STRING — neither the store nor even the DTO's type — so the field resolved to something unwritable
    /// and never shipped. A player renaming a base was the only peer who ever saw the new name.
    ///
    /// The fix is one alias row, the same shape <c>GeoVehicle.Name -> _vehicleName</c> has had all along,
    /// which is what makes the gap an oversight rather than a decision. This law asserts the OUTCOME of that
    /// row — the bridged field resolves and is CARRIED — not the presence of a table entry, because a row
    /// pointing at a member the next build renames is exactly as dead as no row.
    ///
    /// REACTIVITY: <c>SiteName</c> is a plain auto-property with no change event, so the universal repaint
    /// carries it — <c>UIStatePhoenixBaseLayout.EnterState</c>:53 → <c>SelectBase</c>:93 →
    /// <c>UIModuleBaseLayout.Init</c>:298 → <c>SetLeftSideInfo</c>:605 → <c>BaseName.text =
    /// PxBase.Site.Name</c>:607. Confirmed in the decompile, not assumed.
    ///
    /// ARMS
    ///   (a) <c>rename-unresolved</c> — the bridged <c>Name</c> field must exist and must not be Excluded.
    ///   (b) <c>rename-uncarried</c> — it must be readable, i.e. an actual live member behind it.
    ///
    /// Falsify: delete the <c>GeoSite.Name -> SiteName</c> row from <c>RailMeta._twinAliases</c> → (a) red
    /// with "dto-twin unresolved"; point the row at a nonexistent member → (a) red as well.
    /// </summary>
    internal static class L369_ABaseRenameReachesTheMirror
    {
        internal static IEnumerable<string> Check()
        {
            var live = typeof(PhoenixPoint.Geoscape.Entities.GeoSite);
            var dto = live.Assembly.GetType("PhoenixPoint.Geoscape.Entities.GeoSiteInstaceData", false);
            var rt = dto == null ? null : RailType.GetBridged(live, dto);
            var f = rt?.FieldByName("Name");
            if (rt == null || f == null)
            {
                yield return "L369 premise-changed: the GeoSite <= GeoSiteInstaceData bridge, or its 'Name' " +
                             "member, no longer resolves (dto=" + (dto == null ? "missing" : dto.Name) +
                             ", bridge=" + (rt == null ? "null" : rt.Fields.Count + " fields") + "). That pair IS " +
                             "the carrier of every site's name — re-point this law at whatever carries it now; do " +
                             "not delete it. Without the row, GeoPhoenixBase.RenameBase:991 is a purely local " +
                             "write and the renaming player is the only peer who ever sees the new name.";
                yield break;
            }

            if (f.Class == FieldClass.Excluded)
                yield return "L369 rename-unresolved: GeoSite's bridged 'Name' is Excluded (\"" +
                             (f.Exclude ?? "?") + "\"), so a base rename never leaves the machine it was typed " +
                             "on. The live target is SiteName (GeoSite.cs:215, LocalizedTextBind get/set) — the " +
                             "same-named GeoSite.Name (:213) is a get-only string view and can never be it.";
            else if (!f.CanRead)
                yield return "L369 rename-uncarried: GeoSite's bridged 'Name' resolved but cannot be READ, so " +
                             "the walk ships nothing for it and the rename still reaches nobody.";
        }
    }
}
