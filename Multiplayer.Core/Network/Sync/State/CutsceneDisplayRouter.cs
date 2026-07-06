using System;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE routing seam between <c>PlayCutsceneAction.Apply</c> and the SyncEngine-owned
    /// <see cref="UnifiedDisplayQueue"/> (Batch-3 P4). The action file is linked into the Unity-free test
    /// project, so it must not reference <c>NetworkEngine</c>/<c>SyncEngine</c> directly; the live SyncEngine
    /// installs its enqueue delegate here at construction (and clears it on teardown). Unset (tests /
    /// no active session) → <see cref="TryEnqueue"/> returns false → the action plays directly, exactly the
    /// pre-Batch-3 behavior.
    /// </summary>
    public static class CutsceneDisplayRouter
    {
        /// <summary>(displaySeq, nativePriority, cutsceneGuid) → true iff queued on the unified display queue.</summary>
        public static Func<uint, int, string, bool> Enqueue;

        public static bool TryEnqueue(uint displaySeq, int nativePriority, string cutsceneGuid)
        {
            var hook = Enqueue;
            if (hook == null) return false;
            try { return hook(displaySeq, nativePriority, cutsceneGuid); }
            catch { return false; }   // fail-open: a routing failure must never eat the cutscene
        }
    }
}
