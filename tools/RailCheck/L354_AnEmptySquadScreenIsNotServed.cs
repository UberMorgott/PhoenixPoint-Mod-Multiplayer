using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L354 — A SQUAD SCREEN WITH NOTHING TO DEPLOY IS NOT SERVED, AND AN OPEN ONE TRACKS ITS OWN SET.
    ///
    /// FOUR MEASURED FAILURES, and all four are WORSE than "the window stayed open" — the screen opened
    /// EMPTY. Host 22:04:10 <c>SKIP re-enter of queued window UIStateRosterDeployment</c>; host 22:16:38
    /// opened with 0 containers / 0 soldiers, the aircraft already at another site; client 00:19:08 and
    /// 00:21:03 opened with 1 container / 0 soldiers, the aircraft in transit. An empty screen is a DEAD
    /// screen: <c>CheckForDeployment</c>:371 gates START MISSION on <c>squad.Any()</c>, so there is nothing to
    /// tick and no gesture that can make the button live. In co-op the queue can hold this screen for minutes
    /// (<c>WindowOrder.HoldsForOpenScreen</c>), so the aircraft leaving in between is ordinary, not a race.
    ///
    /// THE SET IS THE OTHER HALF, and it is why "close it" alone is the wrong law. The screen is backed by a
    /// SET — <c>_characterContainers</c> (UIStateRosterDeployment.cs:26) filled from
    /// <c>GeoMission.GetDeploymentSources</c> = priority container + <c>Site.Vehicles.Where(Owner ==
    /// faction)</c> + the Site (GeoMission.cs:1477-1499); two containers were observed live. So ONE of two
    /// aircraft leaving must take its own soldiers off the open screen and leave it standing for the rest,
    /// and only the LAST departure may close it.
    ///
    /// ARMS
    ///   (a) <c>empty-screen-served</c> — <see cref="DeploymentRosterRefresh.IsServable"/> EXECUTED over the
    ///       three shapes the log actually produced (0/0, 1/0, 0/1) plus the ordinary one.
    ///   (b) <c>entry-does-not-ask</c> — the <c>EnterState</c> postfix must consult both the count and the
    ///       verdict, and must close through <c>DeploymentWindowClose</c>.
    ///   (c) <c>open-screen-keeps-its-snapshot</c> — the repaint must re-ask <c>TryCount</c> (i.e.
    ///       <c>GetDeploymentSources</c> NOW) and re-run the game's own tail: <c>UIModuleGeneralPersonelRoster
    ///       .Init</c> (:90) and <c>SetUpInitialDeployment</c> (:121 → <c>CheckForDeployment</c>:369-379).
    ///       Without this the departed aircraft's soldiers stay on the screen and in the landing.
    ///   (d) <c>no-table-entry</c> — <c>UiNativeRepaint.Table</c> must hold <c>UIStateRosterDeployment</c>, and
    ///       that entry must reach the repaint. It is the ONLY repaint this screen can have: it occupies the
    ///       current queued slot (GeoscapeView.cs:592-600, priority int.MaxValue), which makes
    ///       <c>PauseHold.IsCurrentQueuedWindow</c> true and the Exit+Enter fallback SKIP it
    ///       (OpenUiRepaint:586-593) — the table runs first, at :554.
    ///   (e) premise — the private native members the repaint drives must still resolve.
    ///   (f) POSITIVE CONTROL, EXECUTED — the same shapes over <see cref="FakeSeam.AlwaysServable"/> MUST come
    ///       back red on (a).
    ///
    /// Falsify: <c>IsServable => true</c> → (a); delete the postfix's <c>IsServable</c> call → (b); drop the
    /// <c>SetUpInitialDeployment</c> invoke → (c); remove the Table key → (d).
    /// </summary>
    internal static class L354_AnEmptySquadScreenIsNotServed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var refresh = typeof(DeploymentRosterRefresh);
            var close = typeof(DeploymentWindowClose);
            var mod = refresh.Assembly;
            var servable = refresh.GetMethod("IsServable", All);
            var postfix = refresh.GetMethod("Postfix", All);
            var repaint = close.GetMethod("RepaintDeploymentScreen", All);
            if (servable == null || postfix == null || repaint == null)
            {
                yield return "L354 premise-changed: DeploymentRosterRefresh.IsServable / .Postfix or " +
                             "DeploymentWindowClose.RepaintDeploymentScreen no longer resolve whole. Those three " +
                             "are the whole of \"an empty squad screen is not served\" — re-point this law at " +
                             "whatever carries them now; do not delete it, four measured sessions parked a player " +
                             "in a screen with a permanently dead START MISSION button.";
                yield break;
            }

            foreach (var red in Table(DeploymentRosterRefresh.IsServable, "L354")) yield return red;

            // ── (b) the entry gate asks and closes ───────────────────────────────────────────────────
            var entryCalls = Program.Callees(postfix, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "TryCount", "IsServable", "CloseCurrent" })
                if (!entryCalls.Contains(needed))
                    yield return "L354 entry-does-not-ask: the UIStateRosterDeployment EnterState postfix no " +
                                 "longer calls " + needed + ", so a screen whose aircraft left while it sat in " +
                                 "the queue opens EMPTY again — 0 containers / 0 soldiers, START MISSION dead by " +
                                 "construction (CheckForDeployment:371) and no click able to revive it.";

            // ── (c) the OPEN screen re-asks the set and re-runs the game's own tail ──────────────────
            var repaintCalls = Program.Callees(repaint, mod).Where(c => c != null).Select(c => c.Name).ToList();
            foreach (var needed in new[] { "TryCount", "IsServable" })
                if (!repaintCalls.Contains(needed))
                    yield return "L354 open-screen-keeps-its-snapshot: the repaint no longer calls " + needed +
                                 ", so an OPEN squad screen keeps the container set it was built with. With two " +
                                 "aircraft on a site (measured live) one flying off must take its soldiers off " +
                                 "this screen and out of the landing, and the LAST one leaving must close it.";
            var repaintIl = CalleeNames(repaint);
            foreach (var needed in new[] { "Init", "Invoke" })
                if (!repaintIl.Contains(needed))
                    yield return "L354 native-tail-skipped: the repaint no longer reaches the game's own " +
                                 "rebuild (" + needed + "). Refilling _characterContainers without " +
                                 "UIModuleGeneralPersonelRoster.Init:90 + SetUpInitialDeployment:121 leaves the " +
                                 "widgets showing soldiers that are not in the model any more — the exact split " +
                                 "between \"the mirror is right\" and \"the screen lies\" that L21 exists for.";

            // ── (d) the table entry, and it reaches the repaint ─────────────────────────────────────
            if (!UiNativeRepaint.Table.TryGetValue(typeof(UIStateRosterDeployment), out var entry) || entry == null)
                yield return "L354 no-table-entry: UiNativeRepaint.Table has no UIStateRosterDeployment arm. That " +
                             "entry is the ONLY repaint this screen can ever have — it holds the current queued " +
                             "slot, so PauseHold.IsCurrentQueuedWindow makes OpenUiRepaint:586 SKIP the fallback " +
                             "Exit+Enter, and without a table arm at :554 nothing repaints it at all.";
            else if (!Program.Callees(entry.Method, mod).Any(c => c != null && c.Name == "RepaintDeploymentScreen"))
                yield return "L354 table-entry-is-decorative: the UIStateRosterDeployment Table arm no longer " +
                             "reaches RepaintDeploymentScreen, so arms (c) above are proved about code the " +
                             "repaint path does not run.";

            // ── (e) premise: the native members the repaint drives ──────────────────────────────────
            foreach (var member in new[] { "_deploymentItems", "_characterContainers", "_selectedDeployment" })
                if (AccessTools.Field(typeof(UIStateRosterDeployment), member) == null)
                    yield return "L354 premise-changed: UIStateRosterDeployment." + member + " no longer resolves, " +
                                 "so the repaint silently does nothing and the open screen keeps a stale roster.";
            if (AccessTools.Method(typeof(UIStateRosterDeployment), "SetUpInitialDeployment") == null)
                yield return "L354 premise-changed: UIStateRosterDeployment.SetUpInitialDeployment no longer " +
                             "resolves — the repaint cannot re-run the game's own enrol pass, so tick boxes and " +
                             "the START MISSION gate keep the values they had before the aircraft left.";

            // ── (f) POSITIVE CONTROL ────────────────────────────────────────────────────────────────
            if (!Table(FakeSeam.AlwaysServable, "control").Any())
                yield return "L354 control-not-red: FakeSeam.AlwaysServable calls every screen servable — the " +
                             "pre-fix behaviour — and the shapes above did not flag it. Arm (a) is decorative.";
        }

        /// <summary>The three shapes the live log produced, plus the ordinary one — run over production in the
        /// arms and over <see cref="FakeSeam"/> in the control.</summary>
        private static IEnumerable<string> Table(Func<int, int, bool> servable, string id)
        {
            foreach (var probe in new[]
            {
                new { C = 0, S = 0, What = "0 containers / 0 soldiers (host 22:16:38, the aircraft already at another site)" },
                new { C = 1, S = 0, What = "1 container / 0 soldiers (client 00:19:08 and 00:21:03, the aircraft in transit)" },
                new { C = 0, S = 1, What = "0 containers but a soldier counted — an impossible pair that must still be refused" },
            })
                if (servable(probe.C, probe.S))
                    yield return id + " empty-screen-served: " + probe.What + " is treated as a servable squad " +
                                 "screen. CheckForDeployment:371 gates START MISSION on squad.Any(), so this " +
                                 "screen has a dead button over an empty roster and NOTHING the player can do " +
                                 "makes it live — the only exit is Back, and Back is the button whose " +
                                 "_mission.Cancel:258 the co-op gates had to take away.";

            if (id != "control" && !servable(2, 5))
                yield return id + " every-screen-refused: a screen with 2 containers and 5 soldiers is refused as " +
                             "well. Closing every deployment screen passes the arms above by removing the " +
                             "feature — no peer could ever deploy anything again.";
        }

        private static HashSet<string> CalleeNames(MethodBase caller)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return names;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                try { names.Add(caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)).Name); } catch { }
            }
            return names;
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the verdict as it stood before this gate — every screen serves.</summary>
            internal static bool AlwaysServable(int containers, int soldiers) => true;
        }
    }
}
