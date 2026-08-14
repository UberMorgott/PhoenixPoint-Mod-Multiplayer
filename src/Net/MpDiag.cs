using System;

namespace Multiplayer.Network
{
    /// <summary>
    /// ONE switch for every temporary investigation-diag family ([MP][inv], [MP][mfgdiag],
    /// [MP][scrap], [MP][uirepaint], [MP][sessionend]). DEFAULT ON FOR THE CURRENT TESTING PHASE: these
    /// are per-entry, per-tick traces that exist to close a specific bug, and left on they become their own perf tax — one
    /// live 3-instance run logged 23642 [MP][inv] lines on the host and ~1600 mfgdiag lines per
    /// client, inflating the log ~3x and costing real frame time in string building alone.
    ///
    /// Switch on in the mod settings or with the environment variable MULTIPLAYER_DIAG (any non-empty
    /// value) before launching the game. Deliberately ONE flag rather than one per family: these traces
    /// are read together when a sync bug is being chased, and five booleans would be five things to remember.
    ///
    /// Call sites guard with `if (MpDiag.On)` rather than routing through a log helper, so that when
    /// the flag is off the string concatenation is never evaluated either — the concatenation, not
    /// the write, is the expensive half on the hot paths.
    ///
    /// This gates INFORMATIONAL traces only. Warnings and errors are never gated: a real failure
    /// must always be visible without knowing to re-run with a flag set.
    /// </summary>
    public static class MpDiag
    {
        public static bool On =>
            MultiplayerMain.Instance?.Config?.EnableDiagnosticLogging == true ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MULTIPLAYER_DIAG"));
    }
}
