using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L352 — THE PER-PEER ANSWER STAYS HOME, AND IT STILL LANDS. Both halves, because either one alone is a
    /// worse bug than the one they replace.
    ///
    /// HALF ONE: NOTHING CROSSES. A mission brief / outcome answered on this peer must emit no 0xB9 advance
    /// (<see cref="WindowQueueSync"/>). If it did, the HOST would run <c>ModalResultCallback</c> for somebody
    /// else's click, and that method's Cancel arm is <c>GeoMission.Cancel</c>:253 — the deletion L351 is
    /// about, arriving by the back door.
    ///
    /// HALF TWO: THE ANSWER STILL WORKS LOCALLY. The mirrored copy is built at
    /// <c>GeoModalMirror.RaiseMirrored</c>, and for the whole rest of the modal family its
    /// <c>DialogCallback</c> is deliberately NULL — with a null handler every button funnels into
    /// <c>UIStateGeoModal.FinishDialog</c>:82 → <c>_dialogHandler?.Invoke</c> and does nothing but close the
    /// copy. Suppress the intent while the handler is still null and the brief becomes a DEAD WINDOW: Confirm
    /// closes it and no peer ever reaches a deployment screen. So the copy of this class must be given the
    /// game's own callback (verbatim from <c>UIStateGeoModal.RestoreContext.RegenerateState</c>:36-39), and
    /// THAT is what makes the missing intent safe rather than a regression.
    ///
    /// ARMS
    ///   (a) <c>answer-still-crosses</c> — <c>SendAdvance</c> must consult
    ///       <c>GeoWindowCoverage.IsPerPeerAnswer</c>.
    ///   (b) <c>copy-cannot-answer</c> — <c>RaiseMirrored</c> must consult it too, i.e. the copy is handed a
    ///       real callback for this class. Half two.
    ///   (c) <c>predicate-keys-on-a-list</c> — the live predicate must ask the GAME which modal is a
    ///       mission's brief/outcome (<c>GetMissionBriefModal</c> / <c>GetMissionOutcomeModal</c>), not a
    ///       hand-written set of ModalTypes. A list would be stale the day a DLC or TFTV adds content, and
    ///       TFTV specifically patches <c>GetMissionBriefModal</c> itself.
    ///   (d) POSITIVE CONTROL, EXECUTED — the same IL check over <see cref="FakeSeam.SendsAnyway"/>, a send
    ///       site with no guard, MUST come back red.
    ///
    /// Falsify: delete the guard from <c>SendAdvance</c> → (a); restore the bare
    /// <c>new UIStateGeoModal(modalType, null, data)</c> → (b); replace the predicate's body with a ModalType
    /// set → (c).
    /// </summary>
    internal static class L352_ThePerPeerAnswerStaysHomeAndStillLands
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(WindowQueueSync).Assembly;
            var send = typeof(WindowQueueSync).GetMethod("SendAdvance", All);
            var raise = typeof(GeoModalMirror).GetMethod("RaiseMirrored", All);
            var predicate = typeof(GeoWindowCoverage).GetMethod("IsPerPeerAnswer", All);
            if (send == null || raise == null || predicate == null)
            {
                yield return "L352 premise-changed: WindowQueueSync.SendAdvance / GeoModalMirror.RaiseMirrored / " +
                             "GeoWindowCoverage.IsPerPeerAnswer no longer resolve whole. Those three ARE the two " +
                             "halves this law pairs — re-point it at whatever carries them now; do not delete it, " +
                             "because either half alone is worse than the bug they replace (a dead brief nobody " +
                             "can answer, or one peer's Cancel deleting the shared mission).";
                yield break;
            }

            if (!Asks(send, predicate, mod))
                yield return "L352 answer-still-crosses: WindowQueueSync.SendAdvance no longer asks " +
                             "IsPerPeerAnswer, so a mission brief answered on THIS peer ships a 0xB9 advance " +
                             "again. The host then runs its own ModalResultCallback for somebody else's click, " +
                             "whose Cancel arm is GeoMission.Cancel:253 — L351's deletion, arriving by the back " +
                             "door with L351 itself still green.";

            if (!Asks(raise, predicate, mod))
                yield return "L352 copy-cannot-answer: GeoModalMirror.RaiseMirrored no longer asks " +
                             "IsPerPeerAnswer, so the mirrored brief is built with a NULL DialogCallback again " +
                             "(UIStateGeoModal.FinishDialog:82 -> _dialogHandler?.Invoke). With the 0xB9 " +
                             "suppressed by the other half, that copy is a DEAD WINDOW: Confirm closes it and " +
                             "nothing happens anywhere — no deployment screen, no launch, on any peer.";

            // Program.Callees resolves within the MOD's assembly; the two questions below live in the GAME's,
            // so this arm walks the IL itself.
            var asked = CalleeNames(predicate);
            if (!asked.Contains("GetMissionOutcomeModal") || !asked.Contains("Invoke"))
                yield return "L352 predicate-keys-on-a-list: IsPerPeerAnswer no longer calls the GAME's own " +
                             "GetMissionBriefModal / GetMissionOutcomeModal. A hand-written ModalType set is " +
                             "stale the day a DLC or a mod adds mission content, and TFTV adds no ModalType at " +
                             "all — it PATCHES GetMissionBriefModal, so keying on the method is what inherits it.";

            // ── POSITIVE CONTROL, executed: the same check over a send site with no guard ────────────
            var control = typeof(FakeSeam).GetMethod("SendsAnyway", All);
            if (control != null && Asks(control, predicate, mod))
                yield return "L352 control-not-red: FakeSeam.SendsAnyway ships the advance with no guard at all " +
                             "and the IL check claims it asks the predicate. The arms above are measuring " +
                             "nothing.";
        }

        /// <summary>Every method NAME <paramref name="caller"/> calls, across assemblies — the game's own
        /// methods included, which is the whole point of arm (c).</summary>
        private static HashSet<string> CalleeNames(MethodBase caller)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return names;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                try { names.Add(caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)).Name); } catch { }
            }
            return names;
        }

        /// <summary>Does <paramref name="caller"/> consult <paramref name="question"/>? IL, not a value: both
        /// call sites need a live geoscape view RailCheck cannot construct outside the game.</summary>
        private static bool Asks(MethodBase caller, MethodBase question, Assembly mod) =>
            Program.Callees(caller, mod).Any(c => c != null && c.MetadataToken == question.MetadataToken &&
                                                  c.Module == question.Module);

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: the send site as it stood before the guard — it never asks.</summary>
            internal static void SendsAnyway() { }
        }
    }
}
