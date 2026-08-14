using System.Collections.Generic;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L501 — A REFUSAL NOTICE IS THE PLAYER'S SENTENCE, AND IT IS SAID ONCE.
    ///
    /// L485 settled WHICH refusals reach the screen. This law settles WHAT the player reads and HOW OFTEN,
    /// which is the half that regressed the moment L485 shipped.
    ///
    /// THE MEASURED DEFECT (session of 2026-08-14, the first one to run L485). The player reported
    /// "constant TFTV errors". The log had ZERO exceptions from TFTV or anything else: what he was reading
    /// were OUR OWN notices — four "assign U#12 — TFTV refused the assignment" and three tac-cmd refusals,
    /// each an internal sentence written for a log file (root keys, op codes, a third-party mod named as the
    /// author of a failure) put on screen through the native prompt. The defect count did not rise; the
    /// session merely READ as broken. Felt quality is quality.
    ///
    ///   (a) THE SHOWN TEXT CARRIES NO INTERNAL IDENTIFIER. <c>SessionNotifier.Scrub</c> is the one seam every
    ///       notifying call site passes through (IntentRail's client branch), so a future caller cannot leak a
    ///       root key by forgetting — the guard is at the funnel, not at 40 call sites.
    ///   (b) A REPEAT IS COLLAPSED, and where the ONLY surface is the modal prompt — tactical has no
    ///       <c>NotificationController</c>, so <c>ShowToast(modalFallback: true)</c> falls through to
    ///       <c>GameUtl.GetMessageBox().ShowSimplePrompt</c> — ANY second notice inside the window is
    ///       collapsed. Four dead clicks must not stack four boxes over a battle.
    ///   (c) THE TWO NOTIFYING FAMILIES SAY IT IN THE PLAYER'S WORDS. Every arm L485 marks notify must have a
    ///       sentence, that sentence must survive Scrub unchanged (it never contained an identifier), and it
    ///       must not name a mod. The quiet arms must have none — one table answers both questions, so
    ///       "we notify this" and "we have words for this" cannot drift apart.
    ///
    /// SEMANTIC KILL (src/, compile-valid): <c>SessionNotifier.Collapses</c> → <c>false</c>
    ///   → L501 refusal-notice-is-not-rate-limited (verified RED, baseline restored GREEN).
    /// </summary>
    internal static class L501_ARefusalNoticeIsThePlayersSentenceSaidOnce
    {
        internal static IEnumerable<string> Check()
        {
            // ═══ (a) NO INTERNAL IDENTIFIER SURVIVES ONTO A SCREEN ═══
            // The exact shapes the live log produced, plus the two the other notifying families still build.
            foreach (var raw in new[]
            {
                "assign U#12 — the assignment was refused",
                "V#1@0123abcd — explore: nothing to explore",
                "equip char=U#7 — the item is gone",
                "op 3 refused nonce=41 peer=2",
                "(throw) something native said no",
            })
            {
                var shown = SessionNotifier.Scrub(raw);
                if (shown == null) continue;   // nothing sayable survived; the caller shows nothing
                foreach (var leak in new[] { "U#", "V#", "op=", "nonce=", "peer=", "(throw)" })
                    if (shown.Contains(leak))
                        yield return "L501 refusal-notice-leaks-an-internal-identifier: '" + shown + "' reaches " +
                                     "the player's screen still carrying '" + leak + "'. A root key or an op " +
                                     "code in a popup is what makes an ordinary refusal read as a crash — the " +
                                     "measured regression this law exists for";
                if (shown.StartsWith("—") || shown.StartsWith("-") || shown.StartsWith(":"))
                    yield return "L501 refusal-notice-leaks-an-internal-identifier: '" + shown + "' begins with " +
                                 "the separator its stripped identifier left behind";
            }
            if (SessionNotifier.Scrub("U#12") != null || SessionNotifier.Scrub("") != null ||
                SessionNotifier.Scrub(null) != null)
                yield return "L501 refusal-notice-is-blank: a reason that is nothing BUT an identifier does not " +
                             "scrub to null, so the player gets an empty box instead of no box";
            if (SessionNotifier.Scrub("There is nothing to explore at this location.") !=
                "There is nothing to explore at this location.")
                yield return "L501 refusal-notice-is-mangled: Scrub rewrites a sentence that carried no " +
                             "identifier at all, so it is editing the player's text rather than guarding it";

            // ═══ (b) SAID ONCE ═══
            const string a = "No valid target.";
            const string b = "This aircraft is already exploring this site.";
            float inside = SessionNotifier.RefusalWindow / 2f;
            float outside = SessionNotifier.RefusalWindow + 1f;

            if (!SessionNotifier.Collapses(a, a, inside, transientSurface: true))
                yield return "L501 refusal-notice-is-not-rate-limited: the SAME refusal shown twice inside the " +
                             "window is shown twice. One dead click repeated is one fact, and repeating the " +
                             "answer is what turned seven notices into 'constant errors'";
            if (!SessionNotifier.Collapses(b, a, inside, transientSurface: false))
                yield return "L501 refusal-modals-stack: with no transient surface (tactical), a second refusal " +
                             "inside the window still raises a second native PROMPT. Four refused clicks then " +
                             "stack four boxes the player must dismiss mid-battle, which is a heavier " +
                             "interruption than the dead click it explains";
            if (SessionNotifier.Collapses(b, a, inside, transientSurface: true))
                yield return "L501 refusal-notice-is-swallowed: two DIFFERENT refusals collapse onto a surface " +
                             "that shows them transiently. Two facts are two notices there; the collapse is for " +
                             "repeats and for stacked modals, not for silence (L485's guarantee)";
            if (SessionNotifier.Collapses(a, a, outside, transientSurface: true) ||
                SessionNotifier.Collapses(b, a, outside, transientSurface: false))
                yield return "L501 refusal-notice-is-swallowed: the window never reopens, so after the first " +
                             "refusal of a session every later one is silent — L485's guarantee lost to its " +
                             "own rate limit";

            // ═══ (c) EVERY NOTIFIED ARM HAS PLAYER WORDS, EVERY QUIET ARM HAS NONE ═══
            foreach (var pair in new[]
            {
                (surface: "vehicle", why: VehicleSync.AlreadyExploringReason,
                 shown: VehicleSync.PlayerText(VehicleSync.AlreadyExploringReason),
                 notify: VehicleSync.ShouldNotify(VehicleSync.AlreadyExploringReason)),
                (surface: "vehicle", why: VehicleSync.NotExplorableReason,
                 shown: VehicleSync.PlayerText(VehicleSync.NotExplorableReason),
                 notify: VehicleSync.ShouldNotify(VehicleSync.NotExplorableReason)),
                (surface: "command", why: TacticalCommandSync.BusyRefusal,
                 shown: TacticalCommandSync.PlayerText(TacticalCommandSync.BusyRefusal),
                 notify: TacticalCommandSync.ShouldNotify(TacticalCommandSync.BusyRefusal)),
                (surface: "command", why: TacticalCommandSync.TargetNotOfferedRefusal,
                 shown: TacticalCommandSync.PlayerText(TacticalCommandSync.TargetNotOfferedRefusal),
                 notify: TacticalCommandSync.ShouldNotify(TacticalCommandSync.TargetNotOfferedRefusal)),
            })
            {
                if (!pair.notify || string.IsNullOrEmpty(pair.shown))
                {
                    yield return "L501 notified-arm-has-no-words: the " + pair.surface + " arm '" + pair.why +
                                 "' is put on a player's screen with nothing written for him, so the " +
                                 "engineering sentence is what he reads";
                    continue;
                }
                if (SessionNotifier.Scrub(pair.shown) != pair.shown)
                    yield return "L501 notified-arm-has-no-words: the " + pair.surface + " sentence '" +
                                 pair.shown + "' does not survive Scrub unchanged, so it was still carrying " +
                                 "an internal identifier when it was written";
                if (pair.shown.Contains("TFTV") || pair.shown.Contains("Multiplayer") ||
                    pair.shown.Contains("host") || pair.shown.Contains("peer"))
                    yield return "L501 notified-arm-names-the-plumbing: the " + pair.surface + " sentence '" +
                                 pair.shown + "' names a mod or the protocol as the author of the refusal. " +
                                 "The player is told what happened in the game, never which layer said no";
            }

            // The quiet half, driven through the REAL validators: a refusal vanilla greys has no player
            // sentence at all, which is the same fact L485 states as "no box".
            var quietVehicle = VehicleSync.Validate(VehicleSync.OpExploreSite, new VehicleSync.Facts
            {
                Resolved = true, OwnedByPlayer = true, SiteExplorable = true,
                CanExploreSites = true, HasCrew = false, AlreadyExploring = false,
            });
            var quietCommand = TacticalCommandSync.Validate(true, true, true, true, true, true, false,
                                                            "NoValidTarget", true, 4f, 0f, 4f, 0f);
            if (quietVehicle == null || quietCommand == null)
                yield return "L501 control-not-red: a case built to be REFUSED was accepted, so the quiet-half " +
                             "assertion below proves nothing";
            else if (VehicleSync.PlayerText(quietVehicle) != null ||
                     TacticalCommandSync.PlayerText(quietCommand) != null)
                yield return "L501 words-for-a-greyed-refusal: a refusal the game itself expresses by disabling " +
                             "the control now has a popup sentence. That is L485's noise arm arriving through " +
                             "the text table instead of through the notify bit";
        }
    }
}
