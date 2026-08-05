using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network;
using Multiplayer.UI;

namespace RailCheck
{
    /// <summary>
    /// L118 — A CLIENT'S LOADING BAR SHOWS THE HOST'S REAL PROGRESS, AND EVERY PHASE EDGE RESETS IT.
    ///
    /// THE REPORT (2026-08-05): "when we start a tactical mission, on the clients there is an EMPTY
    /// loading bar." Three loads happen back to back on a geo→tactical entry and the client only ever had
    /// a bar for the last one:
    ///   P1  the HOST builds the mission (~13 s, law L71's measurement) — client had NOTHING,
    ///   P2  the client downloads the host's mid-tactical save,
    ///   P3  the client's own native world load.
    ///
    /// WHY P1 WAS EMPTY, and it is TWO bugs meeting, which is why this law has two halves:
    ///   • THE HOST WAS RADIO-SILENT THROUGH ITS OWN LOAD. <c>OpenTacticalEntryBarrier</c> does not reset
    ///     <c>_loadCompleteSent</c> (only the self-load arm does), so <c>InPhase2</c> — <c>_begun &amp;&amp;
    ///     !_loadCompleteSent</c> — is FALSE on the host for that whole window: the phase-2 pump never
    ///     sampled, and the snapshot broadcast gate (<c>_barrierOpen || _loadPhaseActive</c>, both false
    ///     until deploy-ready) returned before sending. Nothing about the host's load left the host.
    ///   • THE CLIENT NEVER DROVE THE BAR. With <c>IsDownloading</c> and <c>_rxStarted</c> both false the
    ///     curtain branch wrote a LABEL and no fill, so the bar sat on <c>BeginDownloadBar</c>'s
    ///     <c>UpdateProgress(0f)</c>.
    ///
    /// NOTHING NEW WAS BUILT. The numeric progress was already captured (<c>SetLoadingLevel</c> stores the
    /// loading <c>Level</c> + the live native <c>ProgressBarController</c> and threw both away on the
    /// host), the transport row is already <c>(slot, phase, percent)</c> at ≤20 Hz, and the widget is the
    /// game's own bar. What was added is one publish, two widened gates and one <c>ResetBarFill</c>.
    ///
    /// THE ARMS:
    ///   (a) <c>host-radio-silent</c> — the host-entry-load condition must gate BOTH the pump and the
    ///       broadcast. Sampling without broadcasting is still silence, so ONE widened gate is not a fix.
    ///   (b) <c>host-phase-overtaken</c> — the host's own load publishes under a phase the terminal
    ///       <c>(1, 100)</c> from <c>HostTacticalEntryTransferCrt</c> can still overtake. EXECUTED against
    ///       the real monotone <c>Merge</c>: at phase 2 that terminal is REJECTED and the host row freezes
    ///       mid-load on every client, permanently.
    ///   (c) <c>stale-terminal-pins-the-bar</c> — the previous load's terminal row must be cleared at the
    ///       P1 edge on BOTH peers. Merge is monotone, so a surviving <c>(1,100)</c> rejects every phase-0
    ///       sample and the client renders the host row FULL before the host has loaded a thing — the
    ///       reported bug inverted, which is not an improvement.
    ///   (d) <c>client-bar-dead-before-bytes</c> — the pre-byte branch must DRIVE the bar from the host's
    ///       mirrored row, not merely relabel it.
    ///   (e) <c>phase-edge-never-resets</c> — the native controller only ever RAISES fillAmount
    ///       (<c>ProgressBarController.Update</c>), so the P1→P2 edge needs an explicit write and lowering
    ///       the SOURCE cannot do it. The P2→P3 edge is free ONLY because the game's own
    ///       <c>SetLoadingLevel</c> writes fillAmount; if it stops, we owe a third reset.
    ///
    /// Falsify: narrow either gate back to <c>InPhase2</c> → <c>host-radio-silent</c>; set
    /// <c>HostEntryPhase</c> to 2 → <c>host-phase-overtaken</c>; drop either <c>_tracker.Reset()</c> at the
    /// entry edge → <c>stale-terminal-pins-the-bar</c>; put the label back without the fill →
    /// <c>client-bar-dead-before-bytes</c>; drop the <c>ResetBarFill</c> call → <c>phase-edge-never-resets</c>.
    /// </summary>
    internal static class L118_MirroredHostLoad
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var coord = typeof(SaveTransferCoordinator);
            var update = coord.GetMethod("Update", All);
            var hostEntryLoad = coord.GetProperty("HostEntryLoad", All)?.GetGetMethod(true);
            var publish = coord.GetMethod("PublishHostEntryLoad", All);
            var openEntry = coord.GetMethod("OpenTacticalEntryBarrier", All);
            var onBegin = coord.GetMethod("OnEntryTransferBegin", All);
            var onChunk = coord.GetMethod("OnSaveChunk", All);
            var loadPhaseStarted = coord.GetProperty("LoadPhaseStarted", All)?.GetGetMethod(true);
            var entryHold = coord.GetField("_hostEntryHold", All);
            var phaseField = coord.GetField("HostEntryPhase", All);

            var trackerT = typeof(RosterProgressTracker);
            var merge = trackerT.GetMethod("Merge", All);
            var get = trackerT.GetMethod("Get", All);
            var reset = trackerT.GetMethod("Reset", All);

            var widgets = typeof(NativeWidgetFactory);
            var resetFill = widgets.GetMethod("ResetBarFill", All);
            var setBar = widgets.GetMethod("SetDownloadBar", All);
            var getFill = widgets.GetMethod("GetProgressFill", All);

            if (update == null || hostEntryLoad == null || publish == null || openEntry == null ||
                onBegin == null || onChunk == null || loadPhaseStarted == null || entryHold == null ||
                phaseField == null || merge == null || get == null || reset == null ||
                resetFill == null || setBar == null || getFill == null)
            {
                yield return "L118 premise-changed: SaveTransferCoordinator.{Update,HostEntryLoad," +
                             "PublishHostEntryLoad,OpenTacticalEntryBarrier,OnEntryTransferBegin,OnSaveChunk," +
                             "LoadPhaseStarted,_hostEntryHold,HostEntryPhase}, RosterProgressTracker." +
                             "{Merge,Get,Reset} or NativeWidgetFactory.{ResetBarFill,SetDownloadBar," +
                             "GetProgressFill} no longer resolves. The mirrored-load path has moved and this " +
                             "law is asserting something about a shape it no longer has.";
                yield break;
            }

            var mod = coord.Assembly;
            var inUpdate = Program.CallSites(update, mod).Select(kv => kv.Key).ToList();

            // ── (a) BOTH gates, or the host is still silent ────────────────
            var gateSites = inUpdate.Count(c => c.MetadataToken == hostEntryLoad.MetadataToken &&
                                                c.Module == hostEntryLoad.Module);
            if (gateSites < 2)
                yield return "L118 host-radio-silent: SaveTransferCoordinator.Update consults HostEntryLoad at " +
                             gateSites + " site(s); it needs BOTH — the phase-2 progress pump and the " +
                             "RosterProgress broadcast gate. InPhase2 is false on the host for its whole " +
                             "geo→tactical entry load (OpenTacticalEntryBarrier never resets _loadCompleteSent), " +
                             "so a pump that samples into a gate that will not send is exactly as silent as " +
                             "before, and every client keeps the empty bar that was reported.";
            if (!inUpdate.Any(c => c.MetadataToken == publish.MetadataToken && c.Module == publish.Module))
                yield return "L118 host-radio-silent: Update never calls PublishHostEntryLoad. The pump's other " +
                             "publisher, ReportLoadProgress, is hard-wired to PHASE 1 — routing the host's entry " +
                             "load through it mislabels the row and (arm b) is no longer being asserted about " +
                             "the code that runs.";

            var inGate = Program.Callees(hostEntryLoad, mod).ToList();
            if (!Program.ReadsField(hostEntryLoad, entryHold) ||
                !inGate.Any(c => c.MetadataToken == loadPhaseStarted.MetadataToken && c.Module == loadPhaseStarted.Module))
                yield return "L118 host-radio-silent: HostEntryLoad no longer reads BOTH _hostEntryHold (this is a " +
                             "tac-ENTRY hold, not any load) and LoadPhaseStarted (the curtain actually entered " +
                             "Loading, so a Level and a live bar exist to sample). Dropping either widens the " +
                             "broadcast gate into windows with nothing to publish, or narrows it back to none.";

            // ── (b) the phase the terminal (1,100) can still overtake ──────
            var phase = (byte)phaseField.GetValue(null);
            var t = new RosterProgressTracker();
            if (!t.Merge(0, phase, 40))
                yield return "L118 host-phase-overtaken: a fresh tracker REJECTS the host's first entry-load " +
                             "sample at phase " + phase + ". The client has nothing to mirror and the bar stays " +
                             "empty — the reported bug.";
            if (!t.Merge(0, 1, 100))
                yield return "L118 host-phase-overtaken: with HostEntryPhase=" + phase + " the terminal (1,100) " +
                             "that HostTacticalEntryTransferCrt writes at deploy-ready is REJECTED by the monotone " +
                             "Merge. The host row would freeze at its last mid-load percent on every client for " +
                             "the rest of the session, and the overlay would never show the host as loaded. " +
                             "Phase 0 is the only number the terminal can overtake.";
            else if (t.Get(0) != ((byte)1, (byte)100))
                yield return "L118 host-phase-overtaken: after the terminal write the host row reads " +
                             t.Get(0) + " instead of (1,100).";
            if (!Program.ReadsField(publish, phaseField))
                yield return "L118 host-phase-overtaken: PublishHostEntryLoad does not read HostEntryPhase, so the " +
                             "phase this law just proved safe is not the phase the code publishes. (The field is " +
                             "deliberately static readonly and not a const — a const is inlined and unreadable " +
                             "from IL.)";

            // ── (c) the previous load's terminal row must not survive ──────
            var stale = new RosterProgressTracker();
            stale.Merge(0, 1, 100);                       // the last load ended here
            if (stale.Merge(0, phase, 40) || stale.Get(0).percent != 100)
                yield return "L118 stale-terminal-pins-the-bar: RosterProgressTracker.Merge is no longer monotone " +
                             "against a carried-over terminal row. This law's reset requirement was derived from " +
                             "that monotonicity — re-derive it before trusting the two Reset() calls below.";
            foreach (var pair in new[] { new { M = openEntry, Who = "OpenTacticalEntryBarrier (host)" },
                                         new { M = onBegin,  Who = "OnEntryTransferBegin (client)" } })
                if (!Program.Callees(pair.M, mod).Any(c => c.MetadataToken == reset.MetadataToken &&
                                                           c.Module == reset.Module))
                    yield return "L118 stale-terminal-pins-the-bar: " + pair.Who + " does not reset the roster " +
                                 "tracker at the entry edge. The previous load's (1,100) for slot 0 survives, " +
                                 "monotone Merge rejects every phase-" + phase + " sample against it, and the " +
                                 "client renders the host's bar FULL before the host has started loading. Both " +
                                 "peers need it: the host's own aggregate feeds the snapshot, the client's " +
                                 "tracker feeds the bar.";

            // ── (d) the pre-byte branch DRIVES the bar, not just the label ──
            if (!inUpdate.Any(c => c.MetadataToken == get.MetadataToken && c.Module == get.Module))
                yield return "L118 client-bar-dead-before-bytes: Update never reads the tracker's host row " +
                             "(RosterProgressTracker.Get), so whatever it writes to the bar before the first save " +
                             "chunk is not the host's progress. That branch used to set a curtain LABEL and " +
                             "nothing else, leaving the bar on BeginDownloadBar's UpdateProgress(0f) for ~13 s — " +
                             "the empty bar, verbatim.";
            if (!inUpdate.Any(c => c.MetadataToken == setBar.MetadataToken && c.Module == setBar.Module))
                yield return "L118 client-bar-dead-before-bytes: Update never calls SetDownloadBar, so nothing " +
                             "drives the native bottom bar during the curtain phases at all.";

            // ── (e) the monotone bar only comes down when something writes it ──
            if (!Program.Callees(onChunk, mod).Any(c => c.MetadataToken == resetFill.MetadataToken &&
                                                        c.Module == resetFill.Module))
                yield return "L118 phase-edge-never-resets: OnSaveChunk does not call ResetBarFill at the " +
                             "first-chunk edge. The mirrored host load hands over to our own download at 0%, but " +
                             "ProgressBarController.Update only ever RAISES fillAmount — the bar would keep the " +
                             "host's last fill and stand still until the download passed it. Lowering the SOURCE " +
                             "(BeginDownloadBar/SetDownloadBar) cannot do this; only writing the Image can.";

            var ui = getFill.ReturnType.Assembly;           // UnityEngine.UI, without referencing it
            var setFillAmount = getFill.ReturnType.GetProperty("fillAmount", All)?.GetSetMethod(true);
            if (setFillAmount == null)
                yield return "L118 phase-edge-never-resets: Image.fillAmount has no setter to resolve — the one " +
                             "write that can lower a monotone bar cannot be proven to exist.";
            else
            {
                if (!Program.Callees(resetFill, ui).Any(c => c.MetadataToken == setFillAmount.MetadataToken &&
                                                             c.Module == setFillAmount.Module))
                    yield return "L118 phase-edge-never-resets: ResetBarFill does not write Image.fillAmount. It " +
                                 "is the only thing it exists to do; anything else it might set is read as a " +
                                 "reset by every caller and performs none.";

                var pbc = game.GetType("Base.Utils.ProgressBarController");
                var setLoadingLevel = pbc?.GetMethod("SetLoadingLevel", All);
                if (setLoadingLevel == null)
                    yield return "L118 phase-edge-never-resets: Base.Utils.ProgressBarController.SetLoadingLevel " +
                                 "no longer resolves, so the claim that the download→level-load edge resets " +
                                 "ITSELF is unproven and we may owe a third ResetBarFill there.";
                else if (!Program.Callees(setLoadingLevel, ui).Any(c => c.MetadataToken == setFillAmount.MetadataToken &&
                                                                        c.Module == setFillAmount.Module))
                    yield return "L118 phase-edge-never-resets: the game's own ProgressBarController." +
                                 "SetLoadingLevel:52 no longer writes ProgressFill.fillAmount. That write is the " +
                                 "ONLY reason the download→level-load edge needs nothing from us — without it the " +
                                 "client's own world load inherits the full download bar and never moves.";
            }
        }
    }
}
