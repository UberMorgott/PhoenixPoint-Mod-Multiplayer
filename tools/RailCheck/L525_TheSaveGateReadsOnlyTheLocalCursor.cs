using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Base.Serialization;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L525 — THE SAVE GATE READS ONLY THE LOCAL CURSOR, AND AN AUTOSAVE ALWAYS PROCEEDS.
    ///
    /// R8: if the empty-journal gate ever consults another peer's state it is a quorum and violates the
    /// no-blockers rule outright — one player must be able to drive the whole game to completion while
    /// every other peer is AFK. And §A.2c: an autosave is never blocked, never deferred and never forces
    /// a drain; a law that could turn RED because an autosave went through with a non-empty journal is
    /// wrong by construction and must be rejected in review.
    ///
    /// ARMS:
    ///   (a) autosave-blocked — EXECUTED: MaySave(SaveType.Autosave, localJournalEmpty: false) is TRUE,
    ///       and so is the ironman substitute AutosaveGame uses in ironman mode
    ///       (PhoenixSaveManager.cs:417 -> IronmanSave(autosaveSubstitute: true), SaveType.Ironman).
    ///   (b) manual-not-gated / manual-over-gated — EXECUTED, both directions and both player-initiated
    ///       types (ManualSave, Quicksave), so the gate cannot be satisfied by a constant.
    ///   (c) gate-reads-remote-state — SIGNATURE: MaySave takes exactly (SaveType, bool). A parameter
    ///       list that cannot express a remote peer is a stronger statement than any IL walk, because
    ///       there is nothing remote in scope for it to read.
    ///   (d) autosave-patched — EXECUTED: the gate's own Harmony TargetMethods are enumerated and none
    ///       of them is AutosaveGame, and no type in the mod assembly carries a [HarmonyPatch] attribute
    ///       naming it either.
    ///
    /// ROLES SEPARATED (§C.3): the gate is a LOCAL decision by construction and (c) is what proves it —
    /// there is no host arm and no client arm because there is no remote input at all.
    ///
    /// Falsify (compile-valid src mutations, each named): make MaySave `=> localJournalEmpty;` (dropping
    /// the non-player-initiated exemption) → (a); make MaySave `=> true;` → (b); add a third parameter
    /// carrying a peer count → (c); add AutosaveGame to the guard's TargetMethods → (d).
    /// </summary>
    internal static class L525_TheSaveGateReadsOnlyTheLocalCursor
    {
        internal static IEnumerable<string> Check()
        {
            var gate = typeof(JournalSaveGate);
            var maySave = gate.GetMethod("MaySave", BindingFlags.Static | BindingFlags.Public |
                                                    BindingFlags.NonPublic);
            if (maySave == null)
            {
                yield return "L525 premise-changed: JournalSaveGate.MaySave did not resolve, so this law " +
                             "cannot execute the decision it exists to constrain.";
                yield break;
            }

            // (c) SIGNATURE: nothing remote is even in scope.
            var ps = maySave.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != typeof(SaveType) || ps[1].ParameterType != typeof(bool))
                yield return "L525 gate-reads-remote-state: MaySave's parameters are (" +
                             string.Join(", ", ps.Select(p => p.ParameterType.Name).ToArray()) +
                             "), not (SaveType, Boolean). The gate must be unable to see a remote peer at " +
                             "all — a save gate that consults another peer's state is a quorum, and one " +
                             "player must be able to finish the game while every other peer is AFK.";

            // (a) AUTOSAVE ALWAYS PROCEEDS — including the ironman substitute AutosaveGame delegates to.
            foreach (var never in new[] { SaveType.Autosave, SaveType.Ironman })
                if (!JournalSaveGate.MaySave(never, false))
                    yield return "L525 autosave-blocked: a " + never + " save was refused with a non-empty " +
                                 "journal. An autosave is never blocked, never deferred and never drains " +
                                 "the journal first; whatever is unread at that moment is LOST, exactly as " +
                                 "on any session exit, and that is intended (§A.2c) — never to be 'fixed' " +
                                 "with persistence.";

            // (b) BOTH DIRECTIONS on both player-initiated paths.
            foreach (var gated in new[] { SaveType.ManualSave, SaveType.Quicksave })
            {
                if (JournalSaveGate.MaySave(gated, false))
                    yield return "L525 manual-not-gated: a player-initiated " + gated + " proceeded with " +
                                 "unread entries. The journal is never written to a save file, so a save " +
                                 "taken over a backlog silently discards windows the player was owed.";
                if (!JournalSaveGate.MaySave(gated, true))
                    yield return "L525 manual-over-gated: a player-initiated " + gated + " was refused " +
                                 "with an EMPTY journal. The gate must add exactly one condition — the " +
                                 "LOCAL cursor — and must never become a reason a player cannot save.";
            }

            // (d) NOTHING PATCHES THE AUTOSAVE — the gate's own declared targets, then the whole assembly.
            var targets = gate.GetNestedType("PlayerInitiatedSaveGuard", BindingFlags.NonPublic |
                                                                         BindingFlags.Public);
            var targetMethods = targets == null ? null : targets.GetMethod("TargetMethods",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (targetMethods == null)
            {
                yield return "L525 premise-changed: the save guard no longer declares its Harmony targets " +
                             "through TargetMethods, so this law can no longer read what it patches.";
            }
            else
            {
                var bound = ((IEnumerable<MethodBase>)targetMethods.Invoke(null, null))
                    .Select(m => m == null ? "<unresolved>" : m.DeclaringType.Name + "." + m.Name)
                    .ToList();
                if (bound.Any(n => n.IndexOf("Autosave", StringComparison.Ordinal) >= 0) ||
                    bound.Contains("<unresolved>"))
                    yield return "L525 autosave-patched: the save guard binds [" + string.Join(", ", bound) +
                                 "]. AutosaveGame and its five GeoLevelController triggers (:701, :1236, " +
                                 ":1328, :1424, :1447) may not be patched, skipped, delayed or wrapped by " +
                                 "this work, and an unresolved target would silently gate nothing.";
            }

            var offenders = typeof(WindowJournal).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(false)
                             .Any(a => a.GetType().Name == "HarmonyPatch" && a.ToString().IndexOf(
                                      "AutosaveGame", StringComparison.Ordinal) >= 0))
                .Select(t => t.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (offenders.Count > 0)
                yield return "L525 autosave-patched: " + string.Join(", ", offenders) + " patch(es) " +
                             "AutosaveGame. None of its five GeoLevelController triggers (:701, :1236, " +
                             ":1328, :1424, :1447) may be patched, skipped, delayed or wrapped by this " +
                             "work.";
        }
    }
}
