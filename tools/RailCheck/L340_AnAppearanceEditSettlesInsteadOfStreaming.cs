using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L340 — A BURST OF APPEARANCE EDITS REACHES THE OTHER PEERS AS THE SETTLED VALUE, AND AS A BOUNDED
    /// NUMBER OF INTENTS.
    ///
    /// THE OUTCOME, not the call. BOTH halves are asserted on purpose and neither is sufficient alone: a
    /// law that only bounded the count would be greenest of all if the edits were dropped entirely, and a
    /// law that only checked the final value is exactly what was green while the flood was shipping.
    ///
    /// THE DEFECT (client multiplayer.log, 2026-08-08). <c>UIStateUnitCustomization</c> wires
    /// <c>OnCustomizationChanged -> RefreshUnitDisplay</c> (decompile :60), so the mod's capture postfix on
    /// that method runs once per CONTROL TICK — and shipped a full 15-leaf identity intent each time. 191
    /// <c>[MP][intent] CLIENT personnel customize</c> in one session; nonces 197-223 inside 15 s
    /// (:3357-:3386, 20:39:20.284 -> 20:39:35.499), gaps of 0.16-1.04 s, all for ONE soldier. Every one of
    /// them made the host replay the write, <c>FlushNow</c>, and <c>MarkDirty</c> every peer's open screen,
    /// so the author's own customization screen was Exit+Enter'd out from under the control he was still
    /// holding: <c>cost/10s ... tickMax=103,8ms worst=repaint</c> (:3360) then <c>tickMax=122,5ms</c>
    /// (:3380) against a ~15 ms baseline, and the "stuck gesture flag" backstop fired in the same window
    /// (:3349). The send site was correct; the RATE was the bug.
    ///
    /// ARMS
    ///   (a) <c>flood</c> — a real 40-tick burst through the real <see cref="CustomizeCoalescer"/> yields a
    ///       BOUNDED number of sends. This is the half the fix is for.
    ///   (b) <c>settled-value-lost</c> — that same burst still hands back the LAST body captured. A
    ///       debounce that can swallow the final edit is a worse bug than the flood it replaced.
    ///   (c) <c>attributed-to-the-wrong-soldier</c> — cycling to another soldier mid-edit ships the
    ///       previous one's held edit under the PREVIOUS id, and leaves the new soldier holding only their
    ///       own. The hold is one slot, so this is the case that silently repaints the wrong face.
    ///   (d) <c>capture-bypassed</c> — the live send site actually goes through the hold. Arms (a)-(c) are
    ///       proved about the coalescer; this is what stops them being proved about code the game does not
    ///       run.
    ///   (e) <c>no-release</c> — something drives <c>PersonnelSync.CustomizeTick</c> every frame. A
    ///       trailing-edge debounce has no successor event of its own: without a pump the held edit is
    ///       never sent at all, which is arm (b)'s failure with no way to observe it.
    ///
    /// Falsify (each verified RED, then restored):
    ///   • make <c>Capture</c> return its own hold every tick (the per-tick flood) → <c>flood</c>
    ///   • make <c>Settled</c> clear the hold without returning it → <c>settled-value-lost</c>
    ///   • drop the character-change eviction from <c>Capture</c> → <c>attributed-to-the-wrong-soldier</c>
    ///   • send from <c>ShipCustomize</c> instead of holding → <c>capture-bypassed</c>
    ///   • remove the <c>CustomizeTick</c> call from <c>SyncEngineStub.Tick</c> → <c>no-release</c>
    ///   • rename the coalescer or its members → <c>premise-changed</c>
    /// </summary>
    internal static class L340_AnAppearanceEditSettlesInsteadOfStreaming
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        /// <summary>What one gesture may cost the wire. A burst is ONE decision by the player; the eviction
        /// slot is the only other thing that can fire without a character change, so anything above this is
        /// the stream coming back.</summary>
        private const int SendBudget = 2;

        internal static IEnumerable<string> Check()
        {
            var coalescer = typeof(CustomizeCoalescer);
            var capture = coalescer.GetMethod("Capture", All);
            var settled = coalescer.GetMethod("Settled", All);
            var ship = typeof(PersonnelSync).GetMethod("ShipCustomize", All);
            var tick = typeof(PersonnelSync).GetMethod("CustomizeTick", All);
            var pump = typeof(SyncEngine).GetMethod("Tick", All);
            if (capture == null || settled == null || ship == null || tick == null || pump == null)
            {
                yield return "L340 premise-changed: CustomizeCoalescer.Capture/Settled, " +
                             "PersonnelSync.ShipCustomize/CustomizeTick or SyncEngine.Tick no longer " +
                             "resolves. The customization screen calls its capture seam once per control " +
                             "tick, so without the hold every arm below passes while a single 15-second " +
                             "gesture puts 27 full-identity intents on the wire and re-enters every peer's " +
                             "open screen once per intent.";
                yield break;
            }

            // ── (a)+(b) ONE gesture, driven for real: 40 control ticks, each a distinct appearance ──────
            var box = new CustomizeCoalescer();
            var sent = new List<CustomizeCoalescer.Held>();
            float now = 100f;
            byte[] last = null;
            for (int i = 0; i < 40; i++)
            {
                last = new byte[] { 1, (byte)i };
                var evicted = box.Capture(9, last, "soldier", now);
                if (evicted != null) sent.Add(evicted);
                // The measured cadence of the reported burst, well inside the settle window.
                now += 0.4f;
            }
            var tail = box.Settled(now + CustomizeCoalescer.SettleSec);
            if (tail != null) sent.Add(tail);

            if (sent.Count > SendBudget)
                yield return "L340 flood: one 40-tick appearance gesture put " + sent.Count + " intents on " +
                             "the wire (budget " + SendBudget + "). That is the 20:39:20-20:39:35 measurement " +
                             "coming back: the host replays each one and marks every peer's open screen " +
                             "dirty, so the author's own customization screen is Exit+Enter'd under the " +
                             "control he is still holding and the frame cost goes from ~15 ms to 122,5 ms " +
                             "with worst=repaint.";
            if (sent.Count == 0 || !sent[sent.Count - 1].Body.SequenceEqual(last))
                yield return "L340 settled-value-lost: after the gesture ended, the value handed to the wire " +
                             "was " + (sent.Count == 0 ? "NOTHING AT ALL" : "an intermediate frame, not the " +
                             "last one captured") + ". The other peers keep an appearance the player moved " +
                             "off, forever — a debounce that can lose the final edit is a worse defect than " +
                             "the flood, because the flood at least converged.";

            // ── (c) the player cycles to the next soldier while an edit is still held ───────────────────
            var cycle = new CustomizeCoalescer();
            cycle.Capture(9, new byte[] { 9, 9 }, "soldier", 200f);
            var handed = cycle.Capture(11, new byte[] { 11, 11 }, "soldier", 200.1f);
            if (handed == null || handed.Id != 9 || !handed.Body.SequenceEqual(new byte[] { 9, 9 }))
                yield return "L340 attributed-to-the-wrong-soldier: an edit held for U#9 was not handed back " +
                             "under its own id when the player cycled to U#11 — it was " +
                             (handed == null ? "silently overwritten" : "handed back as U#" + handed.Id) +
                             ". The hold is a single slot, so the two outcomes are the same one bug: U#9's " +
                             "appearance is either lost or painted onto U#11 on every other peer's screen.";
            var after = cycle.Settled(200.1f + CustomizeCoalescer.SettleSec);
            if (after == null || after.Id != 11 || !after.Body.SequenceEqual(new byte[] { 11, 11 }))
                yield return "L340 attributed-to-the-wrong-soldier: after the character change the hold does " +
                             "not settle as U#11's own edit (" + (after == null ? "nothing settled" :
                             "settled as U#" + after.Id) + "), so the soldier now on screen either never " +
                             "ships or ships somebody else's face.";

            // ── (d)+(e) the live seams, so the arms above are about code the game runs ──────────────────
            var asm = typeof(PersonnelSync).Assembly;
            if (!Program.Callees(ship, asm).Any(c => c.MetadataToken == capture.MetadataToken))
                yield return "L340 capture-bypassed: ShipCustomize no longer routes the control tick through " +
                             "the hold, so it is back to putting an intent on the wire per RefreshUnitDisplay " +
                             "— the exact 191-intent shape this law was written from — while the coalescer " +
                             "sits beside it passing its own tests.";
            if (!Program.Callees(pump, asm).Any(c => c.MetadataToken == tick.MetadataToken))
                yield return "L340 no-release: nothing drives PersonnelSync.CustomizeTick from the frame " +
                             "pump. A trailing-edge debounce is released by a clock, not by the next control " +
                             "tick that will never come — so with no pump the last edit of every gesture is " +
                             "held forever and reaches no peer, and the screen it was made on shows the " +
                             "author a value nobody else has.";
        }
    }
}
