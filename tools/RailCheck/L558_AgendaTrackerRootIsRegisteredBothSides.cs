using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>L558 — AGENDA TRACKER ROOT IS REGISTERED, BOTH SIDES, AND THE CLIENT PATCH READS IT.
    ///
    /// THE REPORT: the Faction Agenda Tracker HUD widget (top-right geoscape countdown list) showed
    /// different remaining-time values on host vs client — every peer independently recomputed
    /// countdowns from mirrored low-level fields on its own repaint cadence. See
    /// docs/superpowers/specs/2026-08-16-multiplayer-agenda-tracker-sync-design.md.
    ///
    /// ARMS:
    /// (a) <c>AgendaTrackerSync.Register</c> is actually called from <c>SyncEngine</c>'s constructor
    ///     (SyncEngineStub.cs) — the "M#agenda" mod root reaches both host and client the same way
    ///     every other mod root does (MistSync, DeployCountdown, DeployPrep, AssignSync).
    /// (b) The client-side apply patch (<c>AgendaTrackerSync.AgendaRowApplyPatch.Prefix</c>) actually
    ///     READS <c>AgendaState.Rows</c> — guard against a patch that compiles and short-circuits the
    ///     native computation but never consults the synced state, which would be a silent no-op
    ///     regression indistinguishable from working code until a client is actually watched.
    ///
    /// Falsify (a): delete the <c>AgendaTrackerSync.Register();</c> call from
    /// <c>SyncEngine</c>'s constructor — RED.
    /// Falsify (b): change <c>AgendaRowApplyPatch.Prefix</c> to unconditionally <c>return true;</c>
    /// (never touch <c>State.Rows</c>) — RED.
    /// </summary>
    internal static class L558_AgendaTrackerRootIsRegisteredBothSides
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var asm = typeof(AgendaTrackerSync).Assembly;
            var ctor = typeof(SyncEngine).GetConstructors(All).FirstOrDefault();
            var register = typeof(AgendaTrackerSync).GetMethod("Register", All);
            var prefix = typeof(AgendaTrackerSync.AgendaRowApplyPatch).GetMethod("Prefix", All);
            var rowsField = typeof(AgendaState).GetField("Rows", All);

            if (ctor == null || register == null || prefix == null || rowsField == null)
            {
                yield return "L558 premise-changed: the agenda-tracker root/apply seam no longer resolves " +
                             "(SyncEngine's constructor, AgendaTrackerSync.Register, " +
                             "AgendaTrackerSync.AgendaRowApplyPatch.Prefix, or AgendaState.Rows). Re-point " +
                             "this law at whatever registers \"M#agenda\" and consults it on the client now; " +
                             "do not delete it — the defect it guards is a HUD countdown that silently " +
                             "diverges between host and client.";
                yield break;
            }

            // ═══ (a) the mod root is registered from SyncEngine's constructor, like every other root ═══
            if (!Program.Callees(ctor, asm)
                    .Any(m => m.MetadataToken == register.MetadataToken && m.Module == register.Module))
                yield return "L558 root-not-registered: SyncEngine's constructor never calls " +
                             "AgendaTrackerSync.Register. \"M#agenda\" would then never reach " +
                             "IdentityResolver.RootKinds, so the client never mirrors the host's agenda " +
                             "rows at all — every peer falls back to its own native (and diverging) " +
                             "countdown computation.";

            // ═══ (b) and the client apply patch actually reads the synced state it exists to consult ═══
            if (!Program.ReadsField(prefix, rowsField))
                yield return "L558 client-apply-ignores-state: AgendaTrackerSync.AgendaRowApplyPatch.Prefix " +
                             "never reads AgendaState.Rows. A patch that short-circuits the native per-row " +
                             "computation without consulting the synced rows is a silent no-op — it compiles, " +
                             "it passes every other law, and the client keeps recomputing its own countdowns " +
                             "exactly as if this feature did not exist.";
        }
    }
}
