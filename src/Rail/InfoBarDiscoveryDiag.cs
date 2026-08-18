using System;
using HarmonyLib;
using PhoenixPoint.Geoscape.View.ViewModules;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// THE MEASUREMENT for "the top bar shows only Anu — where are New Jericho and Synedrion?", so the
    /// answer stops being argued from source reading. TFTV builds the AnuIcon/NJIcon/SynIcon and
    /// *Percentage objects hidden in a <c>UIModuleInfoBar.Init</c> prefix and reveals each row in its
    /// <c>UpdatePopulation</c> POSTFIX (refs/TFTV-src/TFTV/TFTVUI/Geoscape/TopInforBar.cs:127, reveal at
    /// :202/:212/:220) — gated on <c>PhoenixFaction.GameTags</c> holding that faction's
    /// <c>*_Discovered_DiplomacyStateTagDef</c>, which the game grants ONLY on inspecting a haven of the
    /// faction (GeoPhoenixFaction.cs:1185-1192). The reveal therefore re-runs on every UpdatePopulation
    /// — native or driven by our repaint (OpenUiRepaint.RefreshInfoBar) — so a discovered faction can
    /// never stay hidden by ordering; the ONLY open question is whether the tag is present on this
    /// peer's PhoenixFaction. This postfix prints exactly that, plus each TFTV object's live state:
    ///   • <c>tag=False</c> on BOTH peers → game-correct: the faction is genuinely undiscovered, go
    ///     inspect one of its havens;
    ///   • <c>tag=True</c> on the host but <c>False</c> on a client → the faction GameTags rail row
    ///     (RailMeta.cs:591) failed to carry it — OUR bug;
    ///   • <c>tag=True</c> with <c>icon=off</c> → TFTV's reveal is not running — a patch-ordering bug.
    /// Asked through the game's own <c>IsFactionDiscovered</c> (GeoPhoenixFaction.cs:1206-1215), the
    /// same FactionsDiplomacySettings mapping the grant uses, so no def name is duplicated here.
    ///
    /// Runs on BOTH peers (it patches the module, not the rail), logs only when the composite state
    /// CHANGES (first paint included), reads everything and writes nothing. Priority.Last so TFTV's own
    /// postfix — the reveal being measured — has already run when this reads the objects.
    /// </summary>
    [HarmonyPatch(typeof(UIModuleInfoBar), "UpdatePopulation")]
    [HarmonyPriority(Priority.Last)]
    internal static class InfoBarDiscoveryDiag
    {
        private static string _lastLine;

        public static void Postfix(UIModuleInfoBar __instance)
        {
            if (!Multiplayer.Network.MpDiag.On) return;
            try
            {
                var geo = GenericApplier.GeoLevel();
                var px = geo == null ? null : geo.PhoenixFaction;
                if (px == null || __instance == null || __instance.PopulationBarRoot == null) return;
                var meter = __instance.PopulationBarRoot.transform.parent == null
                    ? null
                    : __instance.PopulationBarRoot.transform.parent.Find("PopulationDoom_Meter");

                string line = "[Multiplayer][infobar] discovery:" +
                              Row(meter, "AN", px.IsFactionDiscovered(geo.AnuFaction == null ? null : geo.AnuFaction.Def), "AnuIcon", "AnuPercentage") +
                              Row(meter, "NJ", px.IsFactionDiscovered(geo.NewJerichoFaction == null ? null : geo.NewJerichoFaction.Def), "NJIcon", "NjPercentage") +
                              Row(meter, "SY", px.IsFactionDiscovered(geo.SynedrionFaction == null ? null : geo.SynedrionFaction.Def), "SynIcon", "SynPercentage") +
                              " (tag=False on every peer = game-correct, inspect a haven of that faction;" +
                              " host True + client False = rail GameTags row lost it;" +
                              " tag=True + icon=off = TFTV reveal not running)";
                if (string.Equals(line, _lastLine, StringComparison.Ordinal)) return;
                _lastLine = line;
                MpLog.Log(line);
            }
            catch (Exception)
            {
                // A diagnostic may never take down the paint chain it observes; TFTV's own postfix
                // already rethrows its failures loudly.
            }
        }

        private static string Row(Transform meter, string name, bool tag, string icon, string pct)
        {
            return " " + name + " tag=" + tag +
                   " icon=" + ObjState(meter, icon) +
                   " pct=" + ObjState(meter, pct) + " |";
        }

        private static string ObjState(Transform meter, string child)
        {
            var tr = meter == null ? null : meter.Find(child);
            return tr == null ? "missing" : (tr.gameObject.activeSelf ? "on" : "off");
        }
    }
}
