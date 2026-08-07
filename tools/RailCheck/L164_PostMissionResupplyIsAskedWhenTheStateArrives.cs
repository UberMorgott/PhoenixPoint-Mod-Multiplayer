using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Levels.Factions;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L164 — THE POST-MISSION RESUPPLY GATE IS ASKED AGAIN ONCE THE RETURNING PEER'S OWN STATE HAS ARRIVED.
    ///
    /// THE REPORT (2026-08-06, owner): after the tactical mission the CLIENT had no "replenish ammo" button.
    /// Reported as an OLD bug that was fixed once and came back.
    ///
    /// WHAT THE ORIGINAL FIX DID, AND WHY IT WAS NOT UNDONE. 76980f2 (2026-08-01) found that
    /// <c>ClientMissionResultGate</c> blocked <c>GeoMission.Complete</c> WHOLE, leaving
    /// <c>UIStateInitial.EnterState</c>:102 — the one branch that raises the outcome modal AND calls
    /// <c>QueueReplenishState</c>:127 — permanently false on every client, and replaced it with the game's
    /// own <c>CompleteSilently</c>:284. That fix is still in the tree and still RUNS: the 2026-08-06 client
    /// log carries "CLIENT stamped mission outcome — CompleteSilently …" at 21:22:39.119 and the same
    /// branch's custom-mission arm fires one line later. NOTHING UNDID IT.
    ///
    /// WHAT UNDID ITS EFFECT is the gate one line further down, and it is a RACE the commit asserted away:
    /// "gated on this peer's own GetMissingItems() over its own aircraft and its own storage, all already
    /// mirrored, so nothing about it needs the wire". Mirrored, yes. ARRIVED, no. A client's post-battle
    /// geoscape is rebuilt from the HOST'S MID-TACTICAL save, so at <c>EnterState</c> every returning
    /// soldier still carries the full pre-battle loadout — nothing missing, nothing un-full, nothing damaged
    /// — <c>GetMissingItems()</c> is empty and the screen is never queued. The host's own
    /// <c>PostMissionReplenish</c> / <c>SetItems</c> writes land on the ordinary 0xAC value rail TWO FRAMES
    /// LATER (client log: frame 52446 enters the state, the post-mission batch applies at 52448). The whole
    /// session contains not one <c>Queuerd state switch … UIStateReplenish</c> line.
    ///
    /// SO IT IS A RACE, WHICH IS WHY IT "COMES BACK": on a fast local link the batch sometimes wins, the
    /// screen appears, and the bug looks fixed. Nothing asserted the outcome — <c>GeoWindowCoverage</c>'s
    /// <c>UIStateReplenish</c> rule is <c>LocalOnly</c> (a verdict about WHO raises it) and L48 asserts a
    /// window has a reviewed ANSWER, not that the returning peer ever gets the window.
    ///
    /// THE ARMS. The re-ask itself is a per-frame Unity path, so what is asserted is the SHAPE that can only
    /// hold if the outcome does — every one of these is a way the fix has silently died before:
    ///   (a) <c>recheck-undriven</c> — <c>SyncEngine.Tick</c> calls
    ///       <c>ReplenishSync.ClientArrivalTick</c>. A re-ask nothing drives is the exact failure of the
    ///       original: code that is right and never runs.
    ///   (b) <c>gate-not-the-game's</c> — the re-ask reads <c>GeoPhoenixFaction.GetMissingItems</c> and
    ///       raises through <c>GeoscapeView.QueueReplenishState</c>. Both halves matter: a hand-rolled
    ///       "is anything short" test would drift from the screen's own contents, and minting a
    ///       <c>UIStateReplenish</c> directly would bypass the rank in <c>QueueRankPatch</c> (L93 arm G) and
    ///       the ordinal in <c>WindowOrder</c>, i.e. re-open the window-order bug next to this one.
    ///   (c) <c>arming-premise-gone</c> — the arming postfix targets <c>UIStateInitial.EnterState</c> and
    ///       <c>UIStateInitial._params</c> still resolves. That private field is how this peer tells a
    ///       post-mission arrival from any other; without it the re-ask would either never arm or arm on
    ///       every geoscape entry and offer a resupply screen on a fresh join.
    ///   (d) <c>unbounded-recheck</c>, EXECUTED — <c>ReplenishSync.RecheckFrames</c> is finite and positive.
    ///       A named ceiling is L91's own standing requirement for anything that waits, and an infinite
    ///       count-down would put the screen up minutes after the player moved on.
    ///   (e) <c>recheck-survives-teardown</c> — <c>SyncEngine.DetachAllChannels</c> calls
    ///       <c>ReplenishSync.Reset</c>, so a live count-down cannot leak into the next session.
    ///
    /// Falsify (each verified to go RED, then restored): delete the <c>ClientArrivalTick</c> line from
    /// <c>SyncEngine.Tick</c> → (a); replace <c>GetMissingItems</c> with a local item scan, or
    /// <c>QueueReplenishState</c> with a direct <c>QueryStateSwitch</c> → (b); rename <c>_params</c>'s
    /// reader off <c>UIStateInitial</c> → (c); set <c>RecheckFrames</c> to 0 or negative → (d); drop the
    /// <c>ReplenishSync.Reset</c> line from <c>DetachAllChannels</c> → (e).
    /// </summary>
    internal static class L164_PostMissionResupplyIsAskedWhenTheStateArrives
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var replenish = typeof(ReplenishSync);
            var tick = replenish.GetMethod("ClientArrivalTick", AllMembers);
            var reset = replenish.GetMethod("Reset", AllMembers);
            var frames = replenish.GetField("RecheckFrames", AllMembers);
            var arming = replenish.GetNestedType("ArmArrivalRecheckPatch", AllMembers);

            var engineTick = typeof(SyncEngine).GetMethod("Tick", AllMembers);
            var detach = typeof(SyncEngine).GetMethod("DetachAllChannels", AllMembers);

            var getMissing = typeof(GeoPhoenixFaction).GetMethod("GetMissingItems", AllMembers);
            var queueReplenish = typeof(GeoscapeView).GetMethod("QueueReplenishState", AllMembers);
            var paramsField = typeof(UIStateInitial).GetField("_params", AllMembers);

            if (tick == null || reset == null || frames == null || arming == null || engineTick == null ||
                detach == null || getMissing == null || queueReplenish == null || paramsField == null)
            {
                yield return "L164 premise-changed: ReplenishSync.{ClientArrivalTick,Reset,RecheckFrames," +
                             "ArmArrivalRecheckPatch} / SyncEngine.{Tick,DetachAllChannels} / " +
                             "GeoPhoenixFaction.GetMissingItems / GeoscapeView.QueueReplenishState / " +
                             "UIStateInitial._params did not all resolve. The post-mission resupply path has " +
                             "moved and every arm below would pass vacuously — which is how this exact defect " +
                             "spent a week 'already fixed' with a green harness.";
                yield break;
            }

            // ── (a) something actually drives the re-ask ────────────────────────────────────────────
            if (!CallsMethod(engineTick, tick))
                yield return "L164 recheck-undriven: SyncEngine.Tick does not call " +
                             "ReplenishSync.ClientArrivalTick. The re-ask then never runs and the client is " +
                             "back to the single EnterState question it always loses — the returning squad is " +
                             "still the host's PRE-BATTLE save at that instant, so GetMissingItems() is empty " +
                             "and QueueReplenishState is never called. Silent: there is no line to miss.";

            // ── (b) it asks the GAME's question and hands the raise back to the GAME ────────────────
            if (!CallsMethod(tick, getMissing))
                yield return "L164 gate-not-the-game's: ClientArrivalTick does not call " +
                             "GeoPhoenixFaction.GetMissingItems. The whole point of re-asking is that the " +
                             "GAME decides what 'short' means — the same call UIStateInitial:125 makes and the " +
                             "same list UIModuleReplenish draws. Any local re-implementation drifts from the " +
                             "screen it is deciding to open, and drifts silently.";
            if (!CallsMethod(tick, queueReplenish))
                yield return "L164 gate-not-the-game's: ClientArrivalTick does not call " +
                             "GeoscapeView.QueueReplenishState. Minting a UIStateReplenish request directly " +
                             "would skip ReplenishSync.QueueRankPatch (the rank L93 arm G asserts) and " +
                             "WindowOrder.Stamp (the cross-surface ordinal L124 asserts), i.e. fix the missing " +
                             "screen by re-opening the window-order bug that lives one file away.";

            // ── (c) the arming premise: the game's own post-mission params ──────────────────────────
            var target = arming.GetCustomAttribute<HarmonyPatch>();
            if (target == null || target.info == null || target.info.declaringType != typeof(UIStateInitial) ||
                target.info.methodName != "EnterState")
                yield return "L164 arming-premise-gone: ReplenishSync.ArmArrivalRecheckPatch no longer patches " +
                             "UIStateInitial.EnterState. That method IS the post-mission arrival — the branch " +
                             "at :102 whose last statement is QueueReplenishState — and arming anywhere else " +
                             "either misses the arrival or fires on every geoscape entry, which would offer a " +
                             "resupply screen to a peer that just joined.";

            // ── (d) the ceiling is named, finite and positive ───────────────────────────────────────
            var ceiling = Convert.ToInt32(frames.GetRawConstantValue());
            if (ceiling <= 0)
                yield return "L164 unbounded-recheck: ReplenishSync.RecheckFrames = " + ceiling + ". A " +
                             "non-positive ceiling disarms the re-ask outright; there is no infinite value " +
                             "here to test for, so the only failure this can take is the one that makes the " +
                             "fix a no-op while the file still reads as if it works.";
            if (ceiling > 60 * 60)
                yield return "L164 unbounded-recheck: ReplenishSync.RecheckFrames = " + ceiling + " frames, " +
                             "over a minute at 60 fps. Past a few seconds the player has moved on and the " +
                             "screen arrives as an interruption rather than as the arrival UI it is — the very " +
                             "shape L163 exists to stop.";

            // ── (e) it cannot leak into the next session ────────────────────────────────────────────
            if (!CallsMethod(detach, reset))
                yield return "L164 recheck-survives-teardown: SyncEngine.DetachAllChannels does not call " +
                             "ReplenishSync.Reset. A count-down armed in the session that just ended keeps " +
                             "counting in the next one and can put a resupply screen in front of a player who " +
                             "has not been in a battle — state surviving a teardown, the family L26/L91 " +
                             "already record twice.";
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
