using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L549 — THE ACTING PEER'S OWN GESTURE REPAINTS ITS OWN PERSISTENT HUD. A REPAINT PATH THAT IS
    /// REACHABLE ONLY FROM AN APPLY IS NOT REACTIVITY, IT IS AN ECHO.
    ///
    /// THE REPORT (host, 2026-08-15): the top-right agenda strip grew no RESEARCH row when the HOST
    /// started the research itself. The row appeared only after clicking around the geoscape, i.e. after
    /// <c>UIStateNothingSelected.EnterState</c>:104 re-ran <c>UIModuleFactionAgendaTracker.Init</c>:93-104.
    ///
    /// WHY VANILLA CANNOT COVER IT. <c>UIModuleFactionAgendaTracker.Awake</c>:82-91 subscribes to NO
    /// research event; the research row is created in <c>InitialSetup</c>:158-161 alone, and a full rebuild
    /// happens only on a fresh <c>Init(context)</c> or when a row COMPLETES (<c>Dispose</c>:351 sets
    /// <c>_needsRefresh</c>, drained by <c>UpdateData</c>:186-190). The strip spans view states, so nothing
    /// re-Inits it when a state returns — which is exactly why
    /// <see cref="OpenUiRepaint.RefreshPersistentHud"/> exists.
    ///
    /// THE ROOT CAUSE, AND ITS SHAPE. <c>RefreshPersistentHud</c> is reached from
    /// <c>OpenUiRepaint.FlushIfDirty</c>, which needs a MARK — and EVERY mark site was an APPLY path
    /// (<c>GenericApplier</c>, <c>IntentRail.HandleInbound</c>). So a peer repainted its strip for
    /// everybody's gesture but its OWN. The host-local half of the very same seam was already known and
    /// already half-built: <c>IntentRail.HandleInbound</c> pairs <c>DiffEngine.FlushNow()</c> with
    /// <c>OpenUiRepaint.MarkDirty()</c> under the comment "The HOST'S OWN open screen (law 11, missing
    /// half)", while <c>DiffEngine.FlushOnHostGesture</c> — N3's third arm, the ONE seam every capture
    /// family reaches for a HOST-LOCAL gesture (L154 arm (a)) — shipped the state to the clients and
    /// marked nothing at home. One line missing, at the one place that serves research, manufacture,
    /// facilities and vehicles alike.
    ///
    /// WHY THE HUD MARK AND NOT <c>MarkDirty()</c>. <c>ShouldRunNative</c> is entered by every capture
    /// seam on every invocation — <c>VehicleSync.CaptureTravel</c> sees EVERY faction's departure on the
    /// planet before any owner test (<c>DiffEngine.FlushNow</c>'s own note) — so a pathless
    /// <c>MarkDirty()</c> here would bump every declared HUD scope and re-enter the open view state at
    /// NPC-traffic rate. That is the L492/L548 teardown class, bought back. The persistent-HUD mark is the
    /// cheap half: the agenda strip is gated on its ROW IDENTITY (<c>AgendaSignature</c>) and the other
    /// strips on their declared scope, so an unmoved model costs one signature build.
    ///
    /// ARMS:
    ///   (a) <c>own-gesture-repaints-nothing</c> — IL: <c>DiffEngine.FlushOnHostGesture</c> must reach
    ///       <c>OpenUiRepaint.MarkLocalGesture</c>. This is the reported defect, stated where it lives.
    ///   (b) <c>research-gesture-offline</c> — IL: <c>ResearchSync.CaptureIntent</c> must reach
    ///       <c>IntentRail.ShouldRunNative</c>, which must reach <c>DiffEngine.FlushOnHostGesture</c>. The
    ///       named family from the report must actually RIDE the seam arm (a) fixes; a family that
    ///       re-implements the host test for itself would be correct and silently unmarked, which is the
    ///       exact shape of the previous regressions here.
    ///   (c) <c>owed-refresh-is-a-dead-end</c> — IL: <c>OpenUiRepaint.FlushIfDirty</c> must reach
    ///       <c>RefreshPersistentHud</c> DIRECTLY, so the flag arm (a) raises has somewhere to land that
    ///       is not the full-screen repaint.
    ///   (d) POSITIVE CONTROL, EXECUTED on the real state machine: after <c>OpenUiRepaint.Reset()</c>
    ///       nothing is owed (so the seam is not a constant true), and after
    ///       <c>MarkLocalGesture()</c> the persistent HUD IS owed. Without it every arm above is satisfied
    ///       by a mark method whose body was emptied.
    ///
    /// NOT A QUORUM AND NOT A POLL: the mark is raised by the acting peer, for the acting peer, and is
    /// drained by the frame tick that already runs. The OTHER peers keep repainting through the apply path
    /// this law leaves entirely alone.
    ///
    /// Falsify (compile-valid src mutations): drop the <c>MarkLocalGesture()</c> line from
    /// <c>FlushOnHostGesture</c> → (a); inline a bespoke host test in <c>ResearchSync.CaptureIntent</c>
    /// → (b); empty <c>MarkLocalGesture</c>'s body → (d).
    /// </summary>
    internal static class L549_TheActingPeersOwnGestureRepaintsItsOwnHud
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var repaint = typeof(OpenUiRepaint);
            var flushOnGesture = typeof(DiffEngine).GetMethod("FlushOnHostGesture", AllMembers);
            var shouldRunNative = typeof(IntentRail).GetMethod("ShouldRunNative", AllMembers);
            var captureIntent = typeof(ResearchSync).GetMethod("CaptureIntent", AllMembers);
            var flushIfDirty = repaint.GetMethod("FlushIfDirty", AllMembers);
            var refreshHud = repaint.GetMethod("RefreshPersistentHud", AllMembers);
            var reset = repaint.GetMethod("Reset", AllMembers);

            if (flushOnGesture == null || shouldRunNative == null || captureIntent == null ||
                flushIfDirty == null || refreshHud == null || reset == null)
            {
                yield return "L549 premise-changed: DiffEngine.FlushOnHostGesture / IntentRail" +
                             ".ShouldRunNative / ResearchSync.CaptureIntent / OpenUiRepaint.{FlushIfDirty," +
                             "RefreshPersistentHud,Reset} did not all resolve. The host-local gesture seam or " +
                             "the persistent-HUD refresh has moved, so this law is asserting about a shape the " +
                             "mod no longer has — re-point it before believing any verdict below.";
                yield break;
            }

            // The two seams the FIX introduces, resolved by name on purpose: their ABSENCE is the reported
            // defect and must read as the defect, not as a premise change that yields nothing.
            var markLocal = repaint.GetMethod("MarkLocalGesture", AllMembers);
            var hudOwed = repaint.GetProperty("HudRepaintOwed", AllMembers);

            // ── arm (a): THE REPORTED DEFECT.
            if (markLocal == null || !CallsMethod(flushOnGesture, markLocal))
                yield return "L549 own-gesture-repaints-nothing: DiffEngine.FlushOnHostGesture does not reach " +
                             "OpenUiRepaint.MarkLocalGesture. That method is N3's third arm — the ONE seam a " +
                             "HOST-LOCAL gesture passes through, inherited by every capture family from " +
                             "IntentRail.ShouldRunNative — and it ships the change to the CLIENTS while marking " +
                             "nothing at home. Every other mark site in the repo is an APPLY path " +
                             "(GenericApplier, IntentRail.HandleInbound), so the acting peer repaints its " +
                             "persistent HUD for everybody's gesture but its own: the host starts a research and " +
                             "the top-right agenda strip grows no research row until something re-Inits the " +
                             "module (UIStateNothingSelected.EnterState:104). The module subscribes to no " +
                             "research event of its own (Awake:82-91) — a repaint reachable only from an apply " +
                             "is not reactivity, and postulate 1 is not satisfied by an echo.";

            // ── arm (b): the reported FAMILY actually rides that seam.
            if (!CallsMethod(captureIntent, shouldRunNative))
                yield return "L549 research-gesture-offline: ResearchSync.CaptureIntent does not call " +
                             "IntentRail.ShouldRunNative, so the host's OWN research gesture no longer passes " +
                             "the one host-local seam. A family that re-implements the host/client test for " +
                             "itself stays perfectly CORRECT about authority and silently stops marking its own " +
                             "UI — which is the exact shape of every regression this repaint layer has had.";
            if (!CallsMethod(shouldRunNative, flushOnGesture))
                yield return "L549 research-gesture-offline: IntentRail.ShouldRunNative does not call " +
                             "DiffEngine.FlushOnHostGesture. Every capture family in the repo inherits the " +
                             "host-local gesture arm from that single line, so losing it un-marks — and, with " +
                             "L154, un-flushes — every self-initiated change at once.";

            // ── arm (c): the flag has somewhere to land that is not a full-screen teardown.
            if (!CallsMethod(flushIfDirty, refreshHud))
                yield return "L549 owed-refresh-is-a-dead-end: OpenUiRepaint.FlushIfDirty does not call " +
                             "RefreshPersistentHud directly. The persistent-HUD mark is deliberately the CHEAP " +
                             "half — it must not have to go through RepaintOpenGeoscapeScreen, whose Exit+Enter " +
                             "at capture-seam rate is the L492/L548 teardown class — so the HUD-only branch of " +
                             "the flush is the whole point of raising it.";

            // ── arm (d): POSITIVE CONTROL, EXECUTED on the real mark/flush state machine.
            foreach (var v in PositiveControl(markLocal, hudOwed, reset)) yield return v;
        }

        /// <summary>ARM (d), the POSITIVE CONTROL. Arms (a)-(c) are wiring; they are all satisfied by a
        /// mark method with an empty body. Run the real one.</summary>
        private static IEnumerable<string> PositiveControl(MethodInfo markLocal, PropertyInfo hudOwed,
                                                           MethodInfo reset)
        {
            if (markLocal == null || hudOwed == null)
            {
                yield return "L549 control-unreachable: OpenUiRepaint.MarkLocalGesture / .HudRepaintOwed do " +
                             "not both resolve, so the acting peer's own gesture has no mark to raise and no " +
                             "way to prove it raised one. The persistent HUD is then repainted by applies " +
                             "alone and the peer that ACTED is the one peer left stale.";
                yield break;
            }

            bool owedBefore = false, owedAfter = false;
            string failed = null;   // C# forbids yield inside catch
            try
            {
                reset.Invoke(null, null);
                owedBefore = (bool)hudOwed.GetValue(null);
                markLocal.Invoke(null, null);
                owedAfter = (bool)hudOwed.GetValue(null);
                reset.Invoke(null, null);   // leave no state of this law's own behind
            }
            catch (Exception e) { failed = (e.InnerException ?? e).Message; }

            if (failed != null)
            {
                yield return "L549 control-unreachable: the mark state machine could not be executed (" +
                             failed + "), so arm (d) — the only executed evidence this law has — asserts " +
                             "nothing.";
                yield break;
            }

            if (owedBefore)
                yield return "L549 control-is-vacuous: OpenUiRepaint.HudRepaintOwed is true immediately after " +
                             "Reset(), so it cannot distinguish a raised mark from no mark at all and arm (d) " +
                             "proves nothing about MarkLocalGesture.";

            if (!owedAfter)
                yield return "L549 local-gesture-marks-nothing: OpenUiRepaint.MarkLocalGesture() left the " +
                             "persistent HUD un-owed. The wiring arms above are then decoration: the acting " +
                             "peer's own research start, manufacture queue or aircraft dispatch reaches the " +
                             "gesture seam, ships to every other peer, and never repaints the strip on the " +
                             "screen of the player who pressed the button.";
        }

        private static bool CallsMethod(MethodBase caller, MethodBase target)
        {
            if (caller == null || target == null) return false;
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }
    }
}
