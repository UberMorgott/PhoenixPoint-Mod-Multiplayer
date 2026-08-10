using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Core;

namespace RailCheck
{
    /// <summary>
    /// L409 — A RETRIED SHARED ANSWER CHARGES THE SHARED WALLET EXACTLY ONCE.
    ///
    /// THE FAILURE, and it is SILENT — which is the only reason this is a law and not just a fix.
    /// <c>EventSync.TryDurableOrdinaryAnswer</c> hands <c>DurableSharedChoiceEngine.TryAnswer</c> a native
    /// lambda that does two things in a row: <c>Wallet.Take</c> for the choice's cost, then native
    /// <c>CompleteEvent</c>. They are NOT one transaction and deliberately cannot be — native CompleteEvent
    /// is not undoable and the engine says so in its own summary (DurableInboxEngine.cs:966-969). So when
    /// CompleteEvent throws: the payment is gone, the native record is still <c>Triggered</c>, and the
    /// engine's catch (:1018-1027) rolls back only the LEDGER's pending decision. The occurrence is
    /// answerable again, the lambda's own re-entry guard (<c>record.State != Triggered</c>) still passes,
    /// and the retry takes the cost out of the SHARED wallet a SECOND time. Nobody sees a stack trace,
    /// nobody sees a message box — everyone's resources are simply lower than they should be.
    ///
    /// THE FIX THIS PINS: a claim, taken by occurrence+choice, that survives the rollback. First attempt
    /// pays; every retry of the same answer completes the event without paying again. A refund-on-failure
    /// was the other candidate and is WRONG here, because <c>Wallet.Take</c> CLAMPS to what the wallet
    /// actually holds (Wallet.cs:55) and reports nothing about it, so handing the full pack back can mint
    /// resources the faction never had.
    ///
    /// THE ARMS:
    ///   (a) <c>retry-recharges</c> — EXECUTES the claim. A key claims once and refuses every repeat; a
    ///       different key is unaffected; the session clear makes the first key claimable again (trigger
    ///       ids restart with a new store, so a stale claim must not suppress a legitimate charge in the
    ///       next campaign). Repeated-retry idempotence is asserted directly, three claims deep.
    ///   (b) <c>session-clear-unwired</c> — the clear has exactly one production caller, the store swap in
    ///       <c>DurableInboxSession.ActiveStore</c>'s setter, and it is asserted by IL: without it arm (a)
    ///       is a unit test of a HashSet and the claims leak across campaigns.
    ///   (c) POSITIVE CONTROL / anti-vacuity — every method on a NESTED type of <c>EventSync</c> (i.e. the
    ///       compiler-generated closure the native lambda lives in) that calls <c>Wallet.Take</c> must also
    ///       call the claim, AND at least one such method must exist. Without the second half this law
    ///       stays green the day the charge moves somewhere the claim does not guard, and without the first
    ///       half it stays green the day the claim is called but the take is left unconditional. The
    ///       top-level <c>EventSync</c> charge at :265 is deliberately out of scope — that is the
    ///       non-durable host path, which has no rollback-and-retry to double up on.
    ///
    /// Falsify: make the lambda call <c>Wallet.Take</c> unconditionally again → (c); delete the claim's
    /// <c>Add</c> return check so it always claims → (a); drop <c>ClearWalletChargeClaims</c> from the
    /// ActiveStore setter → (b).
    /// </summary>
    internal static class L409_ASharedAnswerChargesTheWalletOnce
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(EventSync).Assembly;
            var game = typeof(Wallet).Assembly;
            var claim = typeof(EventSync).GetMethod("ClaimWalletCharge", All);
            var clear = typeof(EventSync).GetMethod("ClearWalletChargeClaims", All);
            var take = typeof(Wallet).GetMethod("Take", All, null,
                new[] { typeof(ResourcePack), typeof(OperationReason) }, null);
            var session = mod.GetType("Multiplayer.Network.Sync.DurableInboxSession");
            var setStore = session?.GetProperty("ActiveStore", All)?.GetSetMethod(true);

            if (claim == null || clear == null || take == null || session == null || setStore == null)
            {
                yield return "L409 premise-changed: EventSync.{ClaimWalletCharge,ClearWalletChargeClaims}, " +
                             "Wallet.Take(ResourcePack, OperationReason) or DurableInboxSession.ActiveStore's " +
                             "setter no longer resolves, so nothing here checks that a retried shared answer " +
                             "pays once. Re-point this law at whatever the charge-once seam is called now — " +
                             "do not delete it: the failure it guards is a silent second charge against the " +
                             "SHARED wallet, which no player can see happening.";
                yield break;
            }

            // ── arm (a): the claim itself, including repeated retries.
            EventSync.ClearWalletChargeClaims();
            const string key = "L409|trigger:1|choice:pay";
            const string other = "L409|trigger:1|choice:refuse";
            if (!EventSync.ClaimWalletCharge(key))
                yield return "L409 retry-recharges: the FIRST attempt at an occurrence+choice did not get " +
                             "the charge. Nobody pays and the winner's choice is free — the mirror image of " +
                             "the bug, and just as silent.";
            if (EventSync.ClaimWalletCharge(key) || EventSync.ClaimWalletCharge(key))
                yield return "L409 retry-recharges: a repeat claim on the SAME occurrence+choice succeeded, " +
                             "so a shared answer whose native CompleteEvent threw charges the shared wallet " +
                             "again on every retry. The engine rolls back the ledger only (" +
                             "DurableInboxEngine.cs:1018-1027) and the lambda's Triggered guard still passes, " +
                             "so this is the whole of the defect.";
            if (!EventSync.ClaimWalletCharge(other))
                yield return "L409 claim-too-broad: a DIFFERENT choice for the same occurrence was refused " +
                             "its charge. The window stays answerable after a failed answer, so the player " +
                             "may pick another option — and that one must pay for itself.";
            EventSync.ClearWalletChargeClaims();
            if (!EventSync.ClaimWalletCharge(key))
                yield return "L409 claims-outlive-the-session: the claim survived the session clear. " +
                             "Occurrence trigger ids restart with a new store, so a leftover key silently " +
                             "hands somebody a free choice in the NEXT campaign.";
            EventSync.ClearWalletChargeClaims();

            // ── arm (b): the clear is actually wired to the session boundary.
            if (!Program.Callees(setStore, mod).Any(c => c.MetadataToken == clear.MetadataToken &&
                                                          c.Module == clear.Module))
                yield return "L409 session-clear-unwired: DurableInboxSession.ActiveStore's setter no longer " +
                             "calls EventSync.ClearWalletChargeClaims, so the claim set is never emptied. " +
                             "Arm (a) above then proves a HashSet works and nothing else, while a claim from " +
                             "a finished campaign suppresses a real charge in the next one.";

            // ── arm (c): POSITIVE CONTROL — the charge site is the guarded one.
            int chargeSites = 0;
            foreach (var nested in typeof(EventSync).GetNestedTypes(All))
                foreach (var m in nested.GetMethods(All).Cast<MethodBase>()
                                        .Concat(nested.GetConstructors(All)))
                {
                    List<MethodBase> gameCalls;
                    try { gameCalls = Program.Callees(m, game).ToList(); } catch { continue; }
                    if (!gameCalls.Any(c => c.MetadataToken == take.MetadataToken &&
                                            c.Module == take.Module)) continue;
                    chargeSites++;
                    if (!Program.Callees(m, mod).Any(c => c.MetadataToken == claim.MetadataToken &&
                                                          c.Module == claim.Module))
                        yield return "L409 charge-unclaimed: " + nested.Name + "." + m.Name + " calls " +
                                     "Wallet.Take out of the SHARED wallet without taking the charge claim " +
                                     "first. That is the durable shared-answer lambda, the one path whose " +
                                     "native call can throw AFTER the payment and be retried with the " +
                                     "record still Triggered — an unclaimed take there pays twice.";
                }

            if (chargeSites == 0)
                yield return "L409 sweep-is-vacuous: no closure of EventSync calls Wallet.Take at all, so " +
                             "arm (c) asserted nothing. Either the durable answer's charge moved out of the " +
                             "lambda (re-point this arm at wherever it lives now — the claim has to move " +
                             "with it) or the shared cost stopped being charged, which is its own bug.";
        }
    }
}
