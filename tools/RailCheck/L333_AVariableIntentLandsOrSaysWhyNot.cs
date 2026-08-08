using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using UnityEngine;

namespace RailCheck
{
    /// <summary>
    /// L333 — AN EVENT-VARIABLE INTENT THAT REACHES THE HOST EITHER LANDS ON ITS STATE OR SAYS WHY NOT.
    /// IT NEVER JUST ENDS.
    ///
    /// WHERE THIS CAME FROM, AND WHAT IT IS NOT (2026-08-08). A cross-peer sweep reported six of eight
    /// `TFTV_HelmetsOff` intents (nonces 275, 280, 298, 324, 325, 359) as vanished with no trace on either
    /// side. THEY HAD ALL APPLIED — every one has its `[MP][events] HOST variable …` line on the host at
    /// the matching wall clock (host 18:37:39.310 / 18:37:45.673 / 18:37:59.788 / 18:38:44.462 /
    /// 18:38:45.437 / 18:43:42.261, client + 02:02:31.7). So this law does not encode a lost intent.
    ///
    /// It encodes what made "did it apply?" cost a whole log sweep to answer: `HandleSetVariable` ended in
    /// a BARE `return` when the incoming value equalled the host's, so a legitimately-ignored write and one
    /// that never arrived were the same observation — nothing. That is the silent-swallow class wearing its
    /// most respectable costume, correct behaviour with no evidence, and it sits on the path that carries
    /// real campaign and story flags, not just one mod's view toggle.
    ///
    /// WHAT IS ASSERTED IS THE DECISION AND ITS VOICE. `NoOpReason` is EXECUTED on both corners, including
    /// the one that is a real drop if the sentinel is ever "simplified" to 0: a FIRST write of 0 to a
    /// variable nobody has set changes state and must land, because `GetVariable`:270-277 answers the
    /// caller's default for an absent key and `int.MinValue` is what tells "absent" from "zero". A blank
    /// reason is refused — a swallow with a return value is still a swallow. And the handler must reach
    /// TWO distinct `Debug.Log` sites: one for the apply, one for the refusal. One site means the refusal
    /// went quiet again, which is the exact shape this law exists to keep out.
    ///
    /// FALSIFY: put the bare `if (…) return;` back in place of the report → `noop-is-silent` (one log site)
    /// + `decision-unreached`. The other way: make `NoOpReason` always answer null → `noop-not-reported`;
    /// or have it claim the absent-key case → `first-write-dropped`.
    ///
    /// STATED LIMIT: `HandleSetVariable` needs a live `GeoLevelController` and is not callable here, so the
    /// EXECUTED half is its extracted decision and the wiring half is IL. The other silent return on this
    /// path is not in this file's reach: `IntentRail.HandleInbound`:183 drops a re-delivered nonce with no
    /// line at all (correct — reliable double-send — but equally invisible), and it covers EVERY surface.
    /// </summary>
    internal static class L333_AVariableIntentLandsOrSaysWhyNot
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var handler = typeof(EventSync).GetMethod("HandleSetVariable", All);
            var decision = typeof(EventSync).GetMethod("NoOpReason", All);

            if (handler == null || decision == null || decision.ReturnType != typeof(string))
            {
                yield return "L333 premise-changed: EventSync.HandleSetVariable or EventSync.NoOpReason " +
                             "(returning string) no longer resolves. The seam where a relayed variable write " +
                             "is either applied or explained has moved, and this law is asserting things " +
                             "about a shape that is not there — re-read it before assuming an event-variable " +
                             "intent still leaves any evidence that it arrived.";
                yield break;
            }

            // ── (a) EXECUTED: what must land, lands ──────────────────────────────────────────────
            // The third corner is the one a "tidy-up" breaks: absent key, first write of 0.
            foreach (var corner in new[] { new[] { 0, 1 }, new[] { 1, 0 }, new[] { int.MinValue, 0 } })
            {
                var reason = (string)decision.Invoke(null, new object[] { corner[0], corner[1] });
                if (reason != null)
                    yield return "L333 " + (corner[0] == int.MinValue ? "first-write-dropped" : "real-write-dropped") +
                                 ": a write of " + corner[1] + " over " +
                                 (corner[0] == int.MinValue ? "an ABSENT key" : corner[0].ToString()) +
                                 " was called a no-op (\"" + reason + "\"). " +
                                 (corner[0] == int.MinValue
                                    ? "GetVariable:270-277 answers the CALLER'S default for a key that is not " +
                                      "there, so int.MinValue is the only thing separating 'absent' from " +
                                      "'zero'; compare against 0 instead and every first 'clear this flag' on a " +
                                      "fresh campaign is dropped, silently, forever."
                                    : "That write changes host state and must reach GeoscapeEventSystem." +
                                      "SetVariable — the client has already blocked its own copy of it (law 3), " +
                                      "so a host that declines is a gesture that happened on no peer at all.");
            }

            // ── (b) EXECUTED: what does not land, is explained ───────────────────────────────────
            foreach (var same in new[] { 0, 1, 7 })
            {
                var reason = (string)decision.Invoke(null, new object[] { same, same });
                if (string.IsNullOrWhiteSpace(reason))
                    yield return "L333 noop-not-reported: writing " + same + " over " + same + " answered " +
                                 (reason == null ? "null (apply it)" : "a blank reason") + ". A relayed write " +
                                 "the host declines must carry a human reason it can print: without one the " +
                                 "handler is back to a bare return, and an intent that arrived and was ignored " +
                                 "looks exactly like an intent that was lost in transit — which is precisely " +
                                 "the ambiguity that sent a whole cross-peer log sweep after six intents that " +
                                 "had all applied.";
            }

            // ── (c) …and the handler consults it and SPEAKS on both outcomes ─────────────────────
            var callees = Program.Callees(handler, typeof(EventSync).Assembly).ToList();
            if (!callees.Any(m => m.MetadataToken == decision.MetadataToken))
                yield return "L333 decision-unreached: EventSync.HandleSetVariable no longer calls NoOpReason. " +
                             "The decision is extracted precisely so it can be executed above; a handler that " +
                             "inlines it again is one where the corners this law checks are checked on nothing.";

            int logs = Program.Callees(handler, typeof(Debug).Assembly)
                              .Count(m => m.DeclaringType == typeof(Debug) && m.Name == "Log");
            if (logs < 2)
                yield return "L333 noop-is-silent: EventSync.HandleSetVariable reaches " + logs + " Debug.Log " +
                             "site(s); it needs one for the apply and one for the refusal. A no-op that returns " +
                             "without a line is this repo's dominant bug class in its most respectable costume " +
                             "— correct behaviour with no evidence — on the path that carries every campaign " +
                             "and story flag a client ever writes, not just one mod's view toggle.";
        }
    }
}
