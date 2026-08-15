using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Serialization;
using HarmonyLib;
using PhoenixPoint.Common.Saves;
using PhoenixPoint.Common.View.ViewModules;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// A PLAYER MAY NOT SAVE UNTIL THEIR OWN JOURNAL IS EMPTY — PLAYER-INITIATED SAVES ONLY.
    ///
    /// Every entry read is deleted (§A.2), so "journal empty" is reachable by the local player simply
    /// looking at their own windows. THE GATE READS ONLY THE LOCAL PEER'S CURSOR: no roster, no peer list,
    /// no message, no acknowledgement, no network round-trip. IT IS THEREFORE NOT A QUORUM AND MUST NEVER
    /// BECOME ONE (§7.6) — an AFK peer blocks only their OWN save, and every other peer saves freely.
    ///
    /// AN AUTOSAVE ALWAYS PROCEEDS (§A.2c). It is never blocked, never deferred, and never drains the
    /// journal first. Unread entries at that moment are LOST, exactly as they are lost on any ordinary
    /// session exit. That is intended behaviour and must never be "fixed" by adding persistence — the
    /// journal is session-scoped and no journal state is ever written to or read from a save file.
    /// <see cref="PhoenixSaveManager.AutosaveGame"/> (decompiled PhoenixSaveManager.cs:414) and its five
    /// GeoLevelController triggers (:701, :1236, :1328, :1424, :1447) are NOT patched here.
    /// </summary>
    internal static class JournalSaveGate
    {
        /// <summary>THE DECISION, pure and named so a law executes the real one with no game. TRUE = the
        /// save proceeds. Reads exactly two things: the save's own type, and this peer's own cursor.
        ///
        /// Only the two PLAYER-INITIATED types are gated. Everything else — <see cref="SaveType.Autosave"/>
        /// (Base.Serialization/SaveType.cs:8) and <see cref="SaveType.Ironman"/> (:10), which is how
        /// AutosaveGame saves in ironman mode (PhoenixSaveManager.cs:417) — proceeds unconditionally, so
        /// no autosave path can ever be refused by this gate.</summary>
        internal static bool MaySave(SaveType type, bool localJournalEmpty) =>
            localJournalEmpty || (type != SaveType.ManualSave && type != SaveType.Quicksave);

        /// <summary>The four PLAYER-INITIATED save entry points, gated at the entry point rather than at
        /// the shared <c>PhoenixSaveManager.SaveGame</c> (PhoenixSaveManager.cs:191) on purpose: refusing
        /// there leaves <c>written.Value</c> false, which makes QuickSave throw (PhoenixSaveManager.cs:539)
        /// and would make an overwrite delete the old save it never replaced.
        ///
        /// All four are ITERATOR methods, so a skipped call must return an EMPTY routine — a null
        /// <c>__result</c> reaches Timing.Start/CallSafe and NREs in the click handler (same trap as
        /// src/Lobby/SaveLoadInterceptPatch.cs:368).</summary>
        [HarmonyPatch]
        internal static class PlayerInitiatedSaveGuard
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                // PhoenixSaveManager.cs:502 (F5 / GeoscapeView.cs:1149) and :549 (console "save_game").
                yield return AccessTools.Method(typeof(PhoenixSaveManager), "QuickSave");
                yield return AccessTools.Method(typeof(PhoenixSaveManager), "SaveWithName");
                // The save screen: UIModuleSaveGame.cs:190 (new slot) and :210 (overwrite), both ManualSave.
                yield return AccessTools.Method(typeof(UIModuleSaveGame), "NewSaveGame");
                yield return AccessTools.Method(typeof(UIModuleSaveGame), "OverwriteGame");
            }

            private static IEnumerator<NextUpdate> Skipped() { yield break; }

            private static bool Prefix(MethodBase __originalMethod, ref IEnumerator<NextUpdate> __result)
            {
                var type = __originalMethod.Name == "QuickSave" ? SaveType.Quicksave : SaveType.ManualSave;
                if (MaySave(type, WindowJournal.LocalJournalEmpty)) return true;

                __result = Skipped();
                MpLog.LogWarning("[Multiplayer][windows] save refused: this peer still has " +
                                 WindowJournal.UnreadCount + " unread window(s). Read them and the save " +
                                 "proceeds — nothing here waits on another player, and every other peer " +
                                 "can save right now");
                return false;
            }
        }
    }
}
