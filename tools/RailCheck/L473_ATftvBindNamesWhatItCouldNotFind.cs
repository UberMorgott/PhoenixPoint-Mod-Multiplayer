using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

#pragma warning disable 169, 649 // the stub surface below exists to be REFLECTED over, never called.

namespace RailCheck
{
    /// <summary>
    /// L473 — the TFTV personnel bind says WHICH member it could not find, and an OPTIONAL member never
    /// kills the whole surface.
    ///
    /// The lived failure: a client on the Workshop TFTV build (2872311902) reported the same version string
    /// "1.1.4.5" as the host's newer local build, so parity saw no difference; the one field the Workshop
    /// build lacks — <c>RecruitTrainingSession.StartLevel</c> — failed the 23-member all-or-nothing gate and
    /// the ASSIGNMENTS replication died for the session, with an error that named nothing.
    ///
    /// Driven against a SYNTHETIC surface, not against whatever TFTV happens to be installed next to the
    /// harness: the three shapes below are a complete surface, one missing only the optional member, and one
    /// missing a load-bearing member.
    /// </summary>
    internal static class L473_ATftvBindNamesWhatItCouldNotFind
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            AssignSync.Tftv full, older, broken;
            List<string> fullMissing, olderMissing, brokenMissing;

            if (!AssignSync.TryResolve(typeof(Personnel), typeof(Info), typeof(TrainingFull), typeof(Workers),
                                       typeof(Role), out full, out fullMissing))
                yield return "L473 POSITIVE CONTROL: a COMPLETE TFTV surface does not bind — missing: " +
                             string.Join(", ", fullMissing.ToArray());

            if (!AssignSync.TryResolve(typeof(Personnel), typeof(Info), typeof(TrainingNoStartLevel),
                                       typeof(Workers), typeof(Role), out older, out olderMissing))
                yield return "L473 optional-member-kills-the-surface: a TFTV build without the OPTIONAL " +
                             "RecruitTrainingSession.StartLevel fails the whole ASSIGNMENTS bind — missing: " +
                             string.Join(", ", olderMissing.ToArray());
            else if (older.SVirtual == null)
                yield return "L473 optional-member-has-no-fallback: StartLevel is optional but " +
                             "VirtualLevelAchieved, the value it falls back to, did not resolve.";

            if (AssignSync.TryResolve(typeof(Personnel), typeof(Info), typeof(TrainingNoTargetLevel),
                                      typeof(Workers), typeof(Role), out broken, out brokenMissing))
                yield return "L473 required-member-is-not-required: a surface without " +
                             "RecruitTrainingSession.TargetLevel still binds, so the mirror writes an " +
                             "invented target level onto every peer.";
            else if (!brokenMissing.Contains("RecruitTrainingSession.TargetLevel"))
                yield return "L473 failure-does-not-name-the-member: the bind refused a surface without " +
                             "TargetLevel but reported [" + string.Join(", ", brokenMissing.ToArray()) + "].";

            var bind = typeof(AssignSync).GetMethod("Bind", All);
            var resolve = typeof(AssignSync).GetMethod("TryResolve", All);
            if (bind == null || resolve == null)
            {
                yield return "L473 premise-changed: AssignSync.Bind / TryResolve moved.";
                yield break;
            }
            if (!Program.Callees(bind, typeof(AssignSync).Assembly)
                        .Any(m => m.MetadataToken == resolve.MetadataToken))
                yield return "L473 probe-bypassed: Bind resolves the TFTV members without the one probe the " +
                             "missing-member report is built from.";
            if (!Program.StringRefs(bind).Any(s => s.IndexOf("Missing member(s)", StringComparison.Ordinal) >= 0))
                yield return "L473 silent-failure: the TFTV bind failure log does not name the members that " +
                             "did not resolve — the same dead end that cost one wrong diagnosis.";
        }

        // ─── The synthetic TFTV surface (shape only — nothing here is ever invoked) ─────────────

        private enum Role { Unassigned, Research, Manufacturing, Training }

        private static class Personnel
        {
            internal static Dictionary<int, object> Assignments { get { return null; } }
            internal static void ResyncWorkSlots(object faction) { }
        }

        private static class Workers
        {
            internal enum FacilitySlotType { Research, Manufacturing }
            internal static void RefreshInfoBar(object faction) { }
        }

        private sealed class Info
        {
            public int Id;
            public object Character;
            public Role Assignment;
            public object TrainingSpec;
        }

        private static class TrainingFull
        {
            private static readonly IList RecruitSessions = new List<object>();
            private static readonly Dictionary<int, int> _appliedStatLevels = new Dictionary<int, int>();
            private static readonly Dictionary<int, int> _pendingPostRecruitStatApply = new Dictionary<int, int>();

            internal sealed class RecruitTrainingSession
            {
                public int PersonnelId;
                public object Character;
                public int GeoUnitId;
                public object TargetSpecialization;
                public double StartHour;
                public double DurationHours;
                public int TargetLevel;
                public bool Completed;
                public int StartLevel;
                public int VirtualLevelAchieved;
                public int SpPaid;
                public bool WasDismissed;
            }
        }

        /// <summary>The Workshop TFTV build: identical but for the one recent field.</summary>
        private static class TrainingNoStartLevel
        {
            private static readonly IList RecruitSessions = new List<object>();
            private static readonly Dictionary<int, int> _appliedStatLevels = new Dictionary<int, int>();
            private static readonly Dictionary<int, int> _pendingPostRecruitStatApply = new Dictionary<int, int>();

            internal sealed class RecruitTrainingSession
            {
                public int PersonnelId;
                public object Character;
                public int GeoUnitId;
                public object TargetSpecialization;
                public double StartHour;
                public double DurationHours;
                public int TargetLevel;
                public bool Completed;
                public int VirtualLevelAchieved;
                public int SpPaid;
                public bool WasDismissed;
            }
        }

        /// <summary>A genuine upstream rename: the member has no honest fallback, so the bind must refuse.</summary>
        private static class TrainingNoTargetLevel
        {
            private static readonly IList RecruitSessions = new List<object>();
            private static readonly Dictionary<int, int> _appliedStatLevels = new Dictionary<int, int>();
            private static readonly Dictionary<int, int> _pendingPostRecruitStatApply = new Dictionary<int, int>();

            internal sealed class RecruitTrainingSession
            {
                public int PersonnelId;
                public object Character;
                public int GeoUnitId;
                public object TargetSpecialization;
                public double StartHour;
                public double DurationHours;
                public bool Completed;
                public int StartLevel;
                public int VirtualLevelAchieved;
                public int SpPaid;
                public bool WasDismissed;
            }
        }
    }
}
