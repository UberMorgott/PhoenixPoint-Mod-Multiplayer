using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;

namespace RailCheck
{
    /// <summary>
    /// L347 — A FOREIGN FACTION'S AIRCRAFT IS AS VISIBLE ON THIS PEER AS ON THE ONE THAT FOUND IT.
    ///
    /// THE DEFECT (2026-08-08 co-op session): aircraft belonging to other factions never appeared on the client,
    /// at sites the client had already inspected. NOTHING was missing from the rail. <c>GeoVehicle.IsVisible</c>
    /// is not state at all — it is <c>VisualsRoot.activeInHierarchy</c> (GeoVehicle.cs:174-186), a Unity object
    /// graph — so no leaf can ever carry it and no amount of widening the rail can fix it. Its ONLY writer is
    /// <c>GeoVehicle.RefreshVisibility</c> (:606-612), and the native trigger for one parked at a site is
    /// <c>GeoSite.SetInspected</c>:403-406 → GeoMap.cs:571 → GeoFaction.cs:394 →
    /// <c>GeoPhoenixFaction.OnSiteInspectedChanged</c>:1187, whose loop :1191-1196 calls it per vehicle. The rail
    /// writes <c>FactionsData</c> by DIRECT FIELD WRITE, so <c>SetInspected</c> never runs and that event never
    /// fires. Before this law, <c>src/</c> contained zero calls to <c>RefreshVisibility</c>.
    ///
    /// WHY NO EXISTING LAW COULD HAVE CAUGHT IT. L183 asserts the flush still calls
    /// <c>GeoSiteVisualsController.Refresh</c> — the SITE's marker; L115 is about a finished state beating a
    /// local animation. Neither mentions <c>RefreshVisibility</c>, <c>IsVisible</c> or <c>VisualsRoot</c>, which
    /// is why every site-side widening (`4fbd250`, `55f4c6e`, `8210aec`) stayed green while the vehicle arm was
    /// simply absent. A missing consequence is invisible to laws written about the consequences that exist.
    ///
    /// ARMS
    ///   (a) <c>visibility-rule-drifted</c> — the rule this fix depends on, transcribed from GeoVehicle.cs:608-611
    ///       and driven case by case, INCLUDING the case the defect is about (a foreign-owned aircraft parked at
    ///       an inspected site is visible). Tied to the game so the transcription cannot rot: the set of members
    ///       <c>RefreshVisibility</c> actually reads must still be exactly the set the transcription names. A new
    ///       input there means one flush-time call may no longer be the whole answer.
    ///   (b) <c>flush-never-re-derives-visibility</c> / <c>parked-vehicles-not-fed</c>, STRUCTURAL — and stated as
    ///       structural on purpose: <c>IsVisible</c> reads <c>activeInHierarchy</c> on a live Unity object,
    ///       which RailCheck cannot construct, so the outcome is asserted as the two halves that produce it —
    ///       <c>FlushOrderReseed</c> reaches <c>GeoVehicle.RefreshVisibility</c>, and <c>MarkSiteAuthority</c>
    ///       feeds <c>site.Vehicles</c> into the same <c>_reseed</c> set that flush consumes. Deleting either
    ///       leaves the other green and the aircraft invisible again.
    ///
    /// Falsify: delete the <c>RefreshVisibility</c> call from the flush → <c>flush-never-re-derives-visibility</c>;
    /// delete the <c>_reseed</c> feed from <c>MarkSiteAuthority</c> → <c>parked-vehicles-not-fed</c>. Both
    /// verified RED, then restored.
    /// </summary>
    internal static class L347_AForeignAircraftIsAsVisibleHereAsOnItsOwner
    {
        /// <summary>GeoVehicle.cs:608-611, transcribed. Kept here rather than called, because the production side
        /// calls the GAME's own method — this is the recorded rule that says WHY one flush-time call is enough.</summary>
        private static bool Visible(bool alwaysShow, bool ownedByViewer, bool ownerIsAlien, bool hasSite, bool siteInspected)
        {
            bool v = alwaysShow || ownedByViewer || ownerIsAlien;   // :608
            if (!v && hasSite) v = siteInspected;                   // :609-611
            return v;
        }

        // The members RefreshVisibility reads. If the game grows another input, the transcription above is stale
        // and the trigger set that feeds the flush may be too.
        private static readonly string[] ExpectedInputs =
            { "_alwaysShow", "IsOwnedByViewer", "Owner", "CurrentSite", "GeoLevel", "ViewerFaction", "GetInspected", "IsVisible",
              "op_Inequality" };   // `CurrentSite != null` — Unity's own null comparison, not an input of its own

        internal static IEnumerable<string> Check()
        {
            var refresh = typeof(GeoVehicle).GetMethod("RefreshVisibility", BindingFlags.Public | BindingFlags.Instance);
            var flush = typeof(GenericApplier).GetMethod("FlushOrderReseed", BindingFlags.NonPublic | BindingFlags.Static);
            var mark = typeof(GenericApplier).GetMethod("MarkSiteAuthority", BindingFlags.NonPublic | BindingFlags.Static);
            var reseed = typeof(GenericApplier).GetField("_reseed", BindingFlags.NonPublic | BindingFlags.Static);
            if (refresh == null || flush == null || mark == null || reseed == null)
            {
                yield return "L347 premise-changed: " +
                             (refresh == null ? "GeoVehicle.RefreshVisibility" :
                              flush == null ? "GenericApplier.FlushOrderReseed" :
                              mark == null ? "GenericApplier.MarkSiteAuthority" : "GenericApplier._reseed") +
                             " no longer resolves. A foreign faction's aircraft is visible only because something " +
                             "re-derives it after a direct field write; if that machinery moved, move this law with " +
                             "it — the rail can never carry IsVisible, so there is no other way for it to be right.";
                yield break;
            }

            // ── (a) THE RULE, case by case, with the defect's own case first ─────────────────────────
            foreach (var c in new[]
            {
                new { A = false, O = false, X = false, S = true,  I = true,  Want = true,
                      What = "a FOREIGN faction's aircraft parked at a site this viewer HAS inspected (the defect)" },
                new { A = false, O = false, X = false, S = true,  I = false, Want = false,
                      What = "a foreign aircraft parked at a site this viewer has NOT inspected" },
                new { A = false, O = false, X = false, S = false, I = true,  Want = false,
                      What = "a foreign aircraft in flight, at no site" },
                new { A = false, O = true,  X = false, S = false, I = false, Want = true,
                      What = "this viewer's OWN aircraft, at no site" },
                new { A = false, O = false, X = true,  S = false, I = false, Want = true,
                      What = "an alien-owned aircraft" },
                new { A = true,  O = false, X = false, S = true,  I = false, Want = true,
                      What = "an always-shown aircraft at an un-inspected site" },
            })
            {
                if (Visible(c.A, c.O, c.X, c.S, c.I) != c.Want)
                    yield return "L347 visibility-rule-drifted: the recorded rule says " + c.What + " is " +
                                 (c.Want ? "VISIBLE" : "hidden") + " and the transcription of GeoVehicle.cs:608-611 " +
                                 "now answers the opposite. Everything the flush does about aircraft visibility " +
                                 "rests on this rule being what the game runs.";
            }

            var read = MembersRead(refresh);
            var missing = ExpectedInputs.Where(m => !read.Contains(m)).ToArray();
            var extra = read.Where(m => !ExpectedInputs.Contains(m)).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
                yield return "L347 visibility-rule-drifted: GeoVehicle.RefreshVisibility no longer reads exactly " +
                             "the inputs the transcription above names" +
                             (missing.Length > 0 ? " (gone: " + string.Join(", ", missing) + ")" : "") +
                             (extra.Length > 0 ? " (new: " + string.Join(", ", extra) + ")" : "") +
                             ". A new input is the dangerous direction: it can move without a site being " +
                             "inspected, and then re-deriving once per site write is no longer the whole answer.";

            // ── (b) STRUCTURAL: the flush re-derives, and parked vehicles reach the flush ────────────
            if (!Calls(flush, refresh))
                yield return "L347 flush-never-re-derives-visibility: FlushOrderReseed no longer calls " +
                             "GeoVehicle.RefreshVisibility. IsVisible is VisualsRoot.activeInHierarchy, not rail " +
                             "state — with nothing calling the game's own writer after a direct field write, every " +
                             "other faction's aircraft stays hidden on this peer for the whole campaign, and no " +
                             "amount of widening the rail can reach it.";

            if (!ReadsStatic(mark, reseed) || !Calls(mark, typeof(GeoSite).GetMethod("get_Vehicles")))
                yield return "L347 parked-vehicles-not-fed: MarkSiteAuthority no longer puts the site's own " +
                             "vehicles into the _reseed set the flush consumes, so the site repaints and the " +
                             "aircraft parked on it are never re-derived. This mirrors the game's own reaction to " +
                             "the same write (GeoPhoenixFaction.cs:1191-1196 walks the vehicles when a site's " +
                             "inspected flag changes); without the feed, the call in the flush runs over an empty " +
                             "set and arm (b)'s other half stays green over the bug.";

            if (!ReadsStatic(flush, reseed))
                yield return "L347 flush-never-re-derives-visibility: FlushOrderReseed no longer reads _reseed, so " +
                             "whatever MarkSiteAuthority feeds is consumed by something else and the two halves of " +
                             "this arm no longer meet.";
        }

        private static HashSet<string> MembersRead(MethodBase m)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            byte[] il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return names;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                int tok = BitConverter.ToInt32(il, i + 1);
                try
                {
                    if (il[i] == 0x7B || il[i] == 0x7E)                       // ldfld / ldsfld
                        names.Add(m.Module.ResolveField(tok).Name);
                    else if (il[i] == 0x28 || il[i] == 0x6F)                  // call / callvirt
                    {
                        var n = m.Module.ResolveMethod(tok).Name;
                        if (n.StartsWith("get_", StringComparison.Ordinal) ||
                            n.StartsWith("set_", StringComparison.Ordinal)) n = n.Substring(4);
                        names.Add(n);
                    }
                }
                catch { }
            }
            return names;
        }

        private static bool Calls(MethodBase caller, MethodBase target)
        {
            if (caller == null || target == null) return false;
            byte[] il;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static bool ReadsStatic(MethodBase caller, FieldInfo target)
        {
            byte[] il;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7E) continue;                       // ldsfld
                FieldInfo f = null;
                try { f = caller.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f != null && f.MetadataToken == target.MetadataToken && f.Module == target.Module) return true;
            }
            return false;
        }
    }
}
