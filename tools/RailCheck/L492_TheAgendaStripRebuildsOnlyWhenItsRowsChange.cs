using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities.Research;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.Levels.Factions;

namespace RailCheck
{
    /// <summary>
    /// L492 — THE TOP-RIGHT AGENDA STRIP REBUILDS ONLY WHEN ITS ROWS CHANGE, AND ALWAYS WHEN THEY DO.
    ///
    /// THE FAILURE (client, reported 2026-08-14). The report strip flickered: rows appeared and were
    /// replaced, continuously. <c>OpenUiRepaint.RefreshAgendaTracker</c> (b9fbc52) set
    /// <c>_needsRefresh</c> and called <c>UpdateData()</c> on EVERY flush, and <c>UpdateData</c>:186-190
    /// takes that flag straight into <c>InitialSetup</c>:144, which DISPOSES every row and re-creates it.
    /// Measured rate: the throttled "native rebuild" line carried <c>marks=10</c> (client 23:38:49.652,
    /// UIStateVehicleSelected) — about ten rail batches a second, ten teardowns a second.
    ///
    /// THE RULE. The rebuild is owed to a CHANGE IN THE SET OF ROWS, nothing else. Skipping it when
    /// nothing changed is the fix; skipping it when something DID change would be a stale strip, which
    /// this repo treats as a defect, not a cosmetic issue (REACTIVITY mandate). So both directions are
    /// asserted here, and the cheap per-row refresh (<c>UpdateData(element)</c>, the time text) keeps
    /// running either way — the skip must never reach it.
    ///
    /// THE ARMS (all executed against the shipped decision, <c>OpenUiRepaint.AgendaNeedsRebuild</c>):
    ///   (a) <c>first-paint-skipped</c> — the first signature of a session must rebuild.
    ///   (b) <c>change-skipped</c> — a different signature must rebuild. This is the reactivity half: a
    ///       research finishing while the strip is open must repaint the strip.
    ///   (c) <c>unchanged-rebuilds</c> — the same signature twice must NOT rebuild: the flicker, reproduced.
    ///   (d) <c>unreadable-model-trusted</c> — a null signature (model unreadable) must rebuild, every time,
    ///       and must not be remembered as "seen".
    ///   (e) <c>reset-vouches-for-dead-session</c> — <c>OpenUiRepaint.Reset</c> must forget the signature, or
    ///       the next session's first paint is skipped against a dead session's rows.
    ///   (f) <c>gate-unwired</c> — IL: <c>RefreshAgendaTracker</c> must reach the decision through
    ///       <c>AgendaNeedsRebuild</c>, not compare something of its own.
    ///   (g) <c>declaration-blind-to-*</c> — the strip's DECLARED PREFIXES must cover all FOUR row sources
    ///       <c>InitialSetup</c>:150-174 builds rows from — <c>ItemManufacturing.Current</c> and
    ///       <c>Research.Current</c> (both descend from <c>GeoFaction</c> → "F#"),
    ///       <c>GeoFaction.Vehicles</c> ("V#") and <c>GeoPhoenixFaction.Bases</c> (a <c>GeoPhoenixBase</c>
    ///       IS a <c>GeoSite</c> → "S#"). Until §B.8/L543 this was an IL read-set check on the hand-rolled
    ///       <c>AgendaSignature</c>; the builder is gone and the same claim is now made where the read-set
    ///       is DECLARED. A declaration blind to one source is a strip permanently stale in that row kind,
    ///       which is worse than the flicker it replaced and would be invisible to arms (a)-(e).
    ///
    /// Falsify: drop the signature and rebuild unconditionally → (c); return a constant → (b); drop "V#"
    /// from the tracker's row in <c>UiNativeRepaint.DeclaredPrefixes</c> → (g); delete the reset line → (e).
    /// </summary>
    internal static class L492_TheAgendaStripRebuildsOnlyWhenItsRowsChange
    {
        internal static IEnumerable<string> Check()
        {
            OpenUiRepaint.Reset();
            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=A|"))
                yield return "L492 first-paint-skipped: the FIRST agenda signature of a session did not rebuild " +
                             "the strip, so a client joining mid-campaign paints an empty or inherited row set " +
                             "and only a screen re-enter fixes it.";

            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=B|"))
                yield return "L492 change-skipped: a CHANGED row set did not rebuild the strip. That is the " +
                             "reactivity mandate broken outright — a research or manufacture finishing on the host " +
                             "would leave the client's open strip showing the previous row until it re-enters a " +
                             "screen, which is exactly the defect class the repaint layer exists to kill.";

            if (OpenUiRepaint.AgendaNeedsRebuild(true, "research=B|"))
                yield return "L492 unchanged-rebuilds: an UNCHANGED row set still rebuilt the strip. InitialSetup " +
                             "disposes and re-creates every row, and this runs on every rail batch (marks=10 " +
                             "measured, 2026-08-14) — the reported flicker, back verbatim.";

            if (!OpenUiRepaint.AgendaNeedsRebuild(true, null) || !OpenUiRepaint.AgendaNeedsRebuild(true, null))
                yield return "L492 unreadable-model-trusted: an UNREADABLE model (null signature) was treated as " +
                             "'nothing changed'. The safe direction is the other one: an unreadable model may cost " +
                             "a flicker, it may never cost a stale strip.";

            OpenUiRepaint.AgendaNeedsRebuild(true, "research=C|");
            OpenUiRepaint.Reset();
            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=C|"))
                yield return "L492 reset-vouches-for-dead-session: OpenUiRepaint.Reset kept the last session's " +
                             "agenda signature, so the next session's first paint is skipped against rows that " +
                             "belong to a campaign that is gone.";
            OpenUiRepaint.Reset(); // leave no signature of this law's own behind

            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var refresh = typeof(OpenUiRepaint).GetMethod("RefreshAgendaTracker", Any);
            var gate = typeof(OpenUiRepaint).GetMethod("AgendaNeedsRebuild", Any);
            if (refresh == null || gate == null)
            {
                yield return "L492 premise-changed: OpenUiRepaint.RefreshAgendaTracker / .AgendaNeedsRebuild " +
                             "did not resolve, so this law cannot see the repaint it guards. " +
                             "Re-point it before believing any verdict above.";
                yield break;
            }
            string[] declared;
            if (!UiNativeRepaint.DeclaredPrefixes.TryGetValue("UIModuleFactionAgendaTracker", out declared) ||
                declared == null || declared.Length == 0)
            {
                yield return "L492 premise-changed: UIModuleFactionAgendaTracker has no row in " +
                             "UiNativeRepaint.DeclaredPrefixes, so arm (g) cannot check the strip's read-set. " +
                             "An undeclared surface repaints on everything, which is safe but is the L492 " +
                             "flicker back at flush rate — declare it rather than leaving it out.";
                yield break;
            }
            if (!References(refresh, gate))
                yield return "L492 gate-unwired: RefreshAgendaTracker does not call AgendaNeedsRebuild, so the " +
                             "rebuild decision is being made somewhere the arms above cannot see — and the arms " +
                             "stay green while the strip flickers or goes stale.";

            foreach (var source in new[]
            {
                Row("ItemManufacturing.Current", typeof(ItemManufacturing), "F#", "the manufacture row"),
                Row("Research.Current", typeof(Research), "F#", "the research row"),
                Row("GeoFaction.Vehicles", typeof(GeoFaction), "V#",
                    "every aircraft travel/exploration row"),
                Row("GeoPhoenixFaction.Bases", typeof(GeoPhoenixFaction), "S#",
                    "every facility build/repair row"),
            })
            {
                if (source.Owner == null)
                {
                    yield return "L492 premise-changed: " + source.Name + "'s owning game type did not resolve, " +
                                 "so the declaration's coverage of " + source.What + " cannot be checked.";
                    continue;
                }
                if (Array.IndexOf(declared, source.Prefix) >= 0) continue;
                yield return "L492 declaration-blind-to-" + source.Name + ": the agenda strip does not declare " +
                             "\"" + source.Prefix + "\", the rail root " + source.Name + " lands under, and " +
                             "UIModuleFactionAgendaTracker.InitialSetup:150-174 builds " + source.What + " from " +
                             "it. Every change there would leave the strip's ScopeKey EQUAL, the rebuild would " +
                             "be skipped, and that row kind would sit stale on an open screen for as long as " +
                             "nothing else in the strip moved.";
            }
        }

        private struct RowSource
        {
            internal string Name;
            internal Type Owner;
            internal string Prefix;
            internal string What;
        }

        private static RowSource Row(string name, Type owner, string prefix, string what) =>
            new RowSource { Name = name, Owner = owner, Prefix = prefix, What = what };

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? The raw token scan
        /// L107/L475 use only works INSIDE one assembly: a call into the game assembly is a MemberRef with a
        /// token of its own, which never equals the callee's def token. So each candidate 4-byte word is also
        /// RESOLVED through the calling module — that is what makes the four game-side row sources checkable
        /// at all, and it is why this helper is not shared with L475's same-assembly one.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                int token = BitConverter.ToInt32(il, i);
                if (token == callee.MetadataToken && m.Module == callee.Module) return true;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(token); } catch { }
                if (resolved != null && resolved.MetadataToken == callee.MetadataToken &&
                    resolved.Module == callee.Module) return true;
            }
            return false;
        }
    }
}
