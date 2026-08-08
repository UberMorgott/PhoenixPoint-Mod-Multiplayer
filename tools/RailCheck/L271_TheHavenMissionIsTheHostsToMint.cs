using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L271 — ON A CLIENT, THE INFILTRATE CONFIRM MINTS NOTHING. The site's <c>ActiveMission</c> stays null
    /// until a rail apply lands the host's own.
    ///
    /// THE OUTCOME, NOT THE CALL. This repo has been burned repeatedly by laws that assert a seam EXISTS
    /// (L81, L96, L132, L186 — all green over live breakage), so the arms below EXECUTE the gate's decision
    /// over its whole truth table instead of checking that a patch attribute is present. The decision is
    /// <see cref="HavenMissionGate.MintsLocally"/>: true = run the game's own minting code, false = block and
    /// ask the host.
    ///
    ///   (a) <c>client-mints-locally</c> — a CLIENT confirming a wired mission it does not yet hold must get
    ///       FALSE. This is the whole law. On 2026-08-08 it was effectively true (there was no seam at all)
    ///       and the peer wrote <c>StealAircraftAN_CustomMissionTypeDef</c> onto its own S#79 while the host
    ///       had nothing, then asked the host to launch it: <c>launch: that site has no runnable mission any
    ///       more</c>, <c>CRC backstop: root 'S#79' DIVERGED</c>.
    ///   (b) <c>brief-modal-emptied</c> — <c>wireMission == false</c> must stay NATIVE on a client. That arm is
    ///       <c>PrepareDummyMissions</c>:1096, which builds the infiltration brief the player picks FROM and
    ///       writes nothing to the site (the GeoMission ctor fills only the instance, GeoMission.cs:40-46).
    ///       Blocking it would kill the click before it could ever become a mission — a "fix" that makes the
    ///       symptom vanish by removing the feature. This arm is here so nobody simplifies the gate into
    ///       "client → never".
    ///   (c) <c>authority-blocked</c> — host / solo / inside an apply must stay NATIVE. A gate that blocks the
    ///       peer that OWNS the state is not a mirror, it is a deadlock.
    ///   (d) <c>arrival-mints-a-second-time</c> — once the host's mission is here, the call must run native so
    ///       the game's own early-out (GeoHaven.cs:1056) hands back THE HOST'S instance. Answer false there and
    ///       the second pass blocks forever and the squad screen never opens.
    ///   (e) <c>gate-is-decorative</c> — <c>HavenMissionGate.Prefix</c> must actually CONSULT the decision.
    ///       Two laws in this repo (L132, L168's own subject) were proved about a decision the live seam had
    ///       stopped making.
    ///   (f) POSITIVE CONTROL, EXECUTED — the same truth table is run over <see cref="FakeSeam.AlwaysMints"/>
    ///       and MUST come back red on (a).
    ///
    /// Falsify (each verified RED, then restored): make <c>MintsLocally</c> return true → (a); return
    /// <c>runNative || missionAlreadyHere</c> → (b); <c>!runNative &amp;&amp; …</c> → (c); drop the
    /// <c>missionAlreadyHere</c> term → (d); inline the decision into the prefix → (e); make
    /// <see cref="FakeSeam.AlwaysMints"/> return the real answer → (f).
    /// </summary>
    internal static class L271_TheHavenMissionIsTheHostsToMint
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var gate = typeof(HavenMissionGate);
            var mod = gate.Assembly;
            var decision = gate.GetMethod("MintsLocally", All);
            var prefix = gate.GetMethod("Prefix", All);
            if (decision == null || prefix == null)
            {
                yield return "L271 premise-changed: HavenMissionGate.MintsLocally / Prefix no longer resolve " +
                             "whole. Every arm below reads that pair; losing one silently un-asserts the only " +
                             "thing standing between a client's infiltrate click and a mission minted on its own " +
                             "graph (2026-08-08, S#79 DIVERGED).";
                yield break;
            }

            foreach (var red in Table(HavenMissionGate.MintsLocally, "L271")) yield return red;

            // ── (e) the live seam still asks the question the arms above answered ───────────────────
            if (!Program.Callees(prefix, mod).Any(c => c != null && c.Name == "MintsLocally"))
                yield return "L271 gate-is-decorative: HavenMissionGate.Prefix no longer calls MintsLocally, so " +
                             "every arm above is proved about a decision the live seam does not make. That is the " +
                             "shape this repo keeps paying for — L132 was four days green while the gate it named " +
                             "refused every client overwatch.";

            // ── (f) POSITIVE CONTROL, executed: the same table over a decision that really does mint ──
            if (!Table(FakeSeam.AlwaysMints, "control").Any())
                yield return "L271 control-not-red: FakeSeam.AlwaysMints lets a client mint the mission itself and " +
                             "the truth table did not flag it. The arms above are decorative — they would stay " +
                             "green over a gate that had been deleted.";
        }

        /// <summary>The whole truth table, run over production in the arms and over <see cref="FakeSeam"/> in
        /// the control — same code both times, which is what makes the control a control.</summary>
        private static IEnumerable<string> Table(Func<bool, bool, bool, bool> mints, string id)
        {
            // (a) client, a wired mission, nothing here yet -> BLOCK
            if (mints(false, true, false))
                yield return id + " client-mints-locally: a CLIENT confirming a haven infiltration is allowed to " +
                             "run GeoHaven.PrepareHavenMission itself. That call ends in Site.SetActiveMission " +
                             "(GeoHaven.cs:1091) — a structural create on this peer's OWN graph that the " +
                             "host-now-vs-host-before diff can never mention again. The peer then asks the host " +
                             "to launch a mission the host has never had, is refused with 'that site has no " +
                             "runnable mission any more', and the CRC backstop reports the site diverged.";

            if (id != "control")
            {
                // (b) client, the BRIEF list -> NATIVE
                if (!mints(false, false, false))
                    yield return id + " brief-modal-emptied: wireMission:false is blocked on a client. That is " +
                                 "PrepareDummyMissions:1096, which builds the infiltration brief the player picks " +
                                 "FROM and writes nothing to the site; blocking it removes the feature instead of " +
                                 "fixing the bug, and leaves the player a modal with no missions in it.";
                // (c) the authority -> NATIVE
                if (!mints(true, true, false))
                    yield return id + " authority-blocked: the host / solo / an in-flight apply is blocked from " +
                                 "minting. Nothing else mints it, so the mission would never exist for anybody.";
                // (d) the mission is already here -> NATIVE (the game's own early-out)
                if (!mints(false, true, true))
                    yield return id + " arrival-mints-a-second-time: with the host's mission already on this " +
                                 "peer's site the call is STILL blocked. Native's own early-out (GeoHaven.cs:1056) " +
                                 "returns that mission and writes nothing, so this arm is what lets the second " +
                                 "pass hand back the HOST's instance; refuse it and the squad screen never opens.";
            }
        }

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the decision as it effectively stood before the gate existed —
            /// everybody mints. The table MUST flag it.</summary>
            internal static bool AlwaysMints(bool runNative, bool wireMission, bool missionAlreadyHere) => true;
        }
    }
}
