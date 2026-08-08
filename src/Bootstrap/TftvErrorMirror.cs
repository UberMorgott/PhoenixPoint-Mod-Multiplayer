using System;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// INSTRUMENTATION ONLY. Copies every exception TFTV reports into OUR log, verbatim, at the moment it
    /// happens — and it is the reason the next TFTV popup will be diagnosable at all.
    ///
    /// THE PROBLEM IT SOLVES. <c>TFTVLogger.Error(Exception)</c>
    /// (<c>refs/TFTV-src/TFTV/TFTVLogger.cs</c>:59-73) is TFTV's ONLY exception sink: it appends the message
    /// and stack to <c>TFTVMain.LogPath</c> and shows the "An error has occurred in the Terror from the Void
    /// mod!" prompt. Nothing goes to Unity, so nothing reaches <c>Player.log</c> or <c>multiplayer.log</c>.
    /// Then <c>Cleanup()</c> (:48-56) opens that same file with <c>append: false</c> at EVERY game launch, so
    /// the exception survives exactly until the next start of the game. A report that arrives two sessions
    /// later — which is how the co-op restart popup reached us — is unreconstructable by then. Worse on the
    /// local two-instance rig, where both instances share the TFTV folder through a junction and therefore
    /// the same <c>TFTV.log</c>, so each peer erases the other's evidence live.
    ///
    /// A PREFIX, NOT A POSTFIX, AND IT NEVER BLOCKS. <c>Error</c> only writes anything when TFTV's own
    /// <c>_awake &amp;&amp; _debugLevel &gt;= 1</c> holds (:61) — with TFTV debug off it shows the popup path
    /// not at all and logs nothing — so mirroring BEFORE the body is what makes our copy independent of
    /// TFTV's own settings. It returns nothing and swallows everything: an instrumentation seam that can
    /// throw inside another mod's error handler would turn one bug into two.
    ///
    /// TFTV-GATED, therefore LATE-BOUND: registered in <c>TftvLateBinder._patchClasses</c>, because TFTV's
    /// assembly loads AFTER this mod and a <c>[HarmonyPatch]</c> whose target type is unresolvable at
    /// <c>PatchAll</c> time silently binds nothing.
    /// </summary>
    [HarmonyPatch]
    internal static class TftvErrorMirror
    {
        internal const string Tag = "[MP][tftv]";

        private static bool Prepare() => AccessTools.TypeByName("TFTV.TFTVLogger") != null;

        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("TFTV.TFTVLogger"), "Error", new[] { typeof(Exception) });

        private static void Prefix(Exception ex)
        {
            try
            {
                Debug.LogError(Tag + " TFTV REPORTED AN EXCEPTION (mirrored here because TFTV's own log is " +
                               "truncated at every game launch and is shared between same-machine instances): " +
                               (ex == null ? "<null>" : ex.ToString()));
                // If a mission restart is in flight, this lands in that trace too, so one grep shows both.
                RestartTrace.Note("a TFTV exception was reported DURING this restart — see the " + Tag +
                                  " line immediately above for its message and stack.");
            }
            catch { /* never throw out of another mod's error handler */ }
        }
    }
}
