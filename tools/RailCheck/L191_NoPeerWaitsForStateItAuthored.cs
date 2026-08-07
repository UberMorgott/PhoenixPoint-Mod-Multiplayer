using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L191 — A PEER IS NEVER TOLD TO WAIT FOR, NOR MADE TO DISCARD, STATE IT AUTHORED ITSELF.
    ///
    /// Two seams in the window family made the same mistake from opposite ends, and both were found by
    /// reading the HOST's log against the client's on 2026-08-07.
    ///
    ///   (a) <c>host-waits-for-itself</c>. The host printed, ABOUT ITSELF: "deployment for 'PROG_NJ0_MISS' at
    ///       S#104 NOT opened — the host's mission has not arrived on this peer yet (ActiveMission missing);
    ///       reach it from the aircraft's Launch button once it lands" (host multiplayer.log:2105,
    ///       23:10:31.254). One clock, same log: 23:10:12.981 `HOST answered 'PROG_NJ0_MISS' … peer=1`,
    ///       23:10:13.052 `structural create 'S#104…ActiveMission' sent`, 23:10:26.624 `structural DESTROY`,
    ///       23:10:30.105 the stale picker `REPLAYED`. The mission had arrived — the host MINTED it 17 s
    ///       earlier — and was destroyed 4.6 s before the click. Nothing would ever land. The BEHAVIOUR was
    ///       right (no mission, no screen); the diagnosis was impossible, and it cost a session's reading by
    ///       pointing at an arrival race that cannot exist on the peer that printed it.
    ///
    ///   (b) <c>producer-drops-its-own</c>. Same boundary, same save: the host restored `3 entries … 3 kept`
    ///       (host :1382) and the client `3 entries … 0 kept — 3 dropped (Mirrored kind, produced by another
    ///       peer)` (client :367). That reads as a divergence and IS NOT ONE. The host raised
    ///       IntroBetterGeo_0/1/2 itself at 23:09:06.43 and its autosave carried them, so the restored copy
    ///       is its ONLY one; the client already held the same three as 0xB6 raises (23:09:13.96) and
    ///       replayed them (23:09:28.6-29.1), so for it the restored copy is a SECOND one — the duplicate
    ///       <c>0616e26</c> measured as six teardowns against three raises. BOTH PEERS ENDED WITH THREE.
    ///       The invariant is ONE LIVE COPY PER PEER, never equal restore counts, and "make the counts agree"
    ///       is the regression in either direction: make the host drop and it loses windows only it holds,
    ///       make the client keep and the duplicate is back.
    ///
    /// WHY A LAW AND NOT A COMMENT, for (b) especially. L135 already asserts the two halves EXIST and share
    /// one producer signal (the restore filter and <c>ReplenishSync.CarryUnreadWindowsPatch</c> read the same
    /// gate; the deferral re-carry is reached). What it cannot see is the ROLE SPLIT ITSELF: the drop read its
    /// two conditions inline at the call site, so deleting the producer test while keeping the call for the
    /// log line would have left every L135 arm green while the host started discarding its own history. Both
    /// decisions are now pure functions and this law executes them. Neither is baselined and neither gets a
    /// second law beside it — L135 keeps its subject, this one takes the role.
    ///
    /// Falsify (each verified RED, then restored): return the client wording unconditionally from
    /// <c>NoDeploymentReason</c> → <c>host-waits-for-itself</c>; make <c>DropsRestoredWindow</c> ignore
    /// <c>foreign</c> → <c>producer-drops-its-own</c>; make it ignore the kind → <c>drop-is-unkeyed</c>;
    /// rename either member → <c>premise-changed</c>.
    /// </summary>
    internal static class L191_NoPeerWaitsForStateItAuthored
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(MissionSync).Assembly;
            var nav = mod.GetType("Multiplayer.Network.Sync.MissionEncounterNav");
            var restore = mod.GetType("Multiplayer.Network.Sync.RestoreDropsResolvedSubjects");
            var reason = nav?.GetMethod("NoDeploymentReason", All);
            var drops = restore?.GetMethod("DropsRestoredWindow", All);
            if (reason == null || drops == null)
            {
                yield return "L191 premise-changed: MissionEncounterNav.NoDeploymentReason or " +
                             "RestoreDropsResolvedSubjects.DropsRestoredWindow no longer resolves. Both were extracted from " +
                             "inline call sites precisely so the ROLE SPLIT could be executed rather than read, " +
                             "and folding either back inline puts it out of reach of every arm below.";
                yield break;
            }

            // ── (a) the host is never told to wait for a mission it mints ──────────────────────────
            foreach (bool missing in new[] { true, false })
            {
                string host = (string)reason.Invoke(null, new object[] { true, missing });
                string client = (string)reason.Invoke(null, new object[] { false, missing });

                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(client))
                    yield return "L191 host-waits-for-itself: NoDeploymentReason answered blank for " +
                                 (missing ? "a missing" : "an un-runnable") + " mission. A silently eaten " +
                                 "deployment is the exact failure the whole 0xB8 family exists to kill.";
                if (host == client)
                    yield return "L191 host-waits-for-itself: the guard gives the HOST and a CLIENT the same " +
                                 "reason for having no squad screen. They are not the same fact: on a client " +
                                 "the mission is another peer's structural create and may still be in flight; " +
                                 "on the host it is the host's OWN, so a missing one can only mean GONE. The " +
                                 "host printed the client's wording about itself and sent a whole session " +
                                 "hunting an arrival race that cannot exist there.";
                if (host.IndexOf("has not arrived", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    host.IndexOf("once it lands", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return "L191 host-waits-for-itself: the HOST's reason still says it is waiting for " +
                                 "state to arrive (\"" + host + "\"). Measured: the host minted that mission 17 s " +
                                 "before and destroyed it 4.6 s before the click — there was nothing to wait " +
                                 "for and the aircraft's Launch button it points at would never work either.";
                if (client.IndexOf("has not arrived", StringComparison.OrdinalIgnoreCase) < 0)
                    yield return "L191 host-waits-for-itself: the CLIENT's reason no longer names the arrival " +
                                 "race. That half is REAL — a client's mission is the host's structural create " +
                                 "— and dropping it would trade one wrong diagnosis for the opposite one.";
            }

            // ── (b) the producer keeps what only it holds ──────────────────────────────────────────
            if ((bool)drops.Invoke(null, new object[] { false, true }))
                yield return "L191 producer-drops-its-own: a peer that AUTHORED the save drops a Mirrored " +
                             "restored window. Its restored copy is the only one it has — it raised those " +
                             "windows itself and no 0xB6 replay is coming for them — so this is the host " +
                             "discarding its own window history, and the peers really would diverge. The " +
                             "3-kept / 0-kept asymmetry in the logs is CORRECT: one copy each, from two " +
                             "different sources.";
            if (!(bool)drops.Invoke(null, new object[] { true, true }))
                yield return "L191 producer-drops-its-own: a peer restoring ANOTHER peer's blob keeps a " +
                             "Mirrored restored window. It already has that window as its own live raise, so " +
                             "keeping this one is the duplicate 0616e26 measured as six teardowns against " +
                             "three raises — the intro popups answered on the host and shown again on every " +
                             "client.";
            foreach (bool foreign in new[] { true, false })
                if ((bool)drops.Invoke(null, new object[] { foreign, false }))
                    yield return "L191 drop-is-unkeyed: a NON-Mirrored restored window is dropped (foreign=" +
                                 foreign + "). Only a window the host RAISED to this peer can have a second " +
                                 "source; a LocalOnly or Gap-declared one exists exactly once, in the save, " +
                                 "and dropping it deletes the player's own window history with nothing to " +
                                 "replace it.";
        }
    }
}
