using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L150 — NO SEQUENCE OF CONCURRENT SKILL CONFIRMATIONS CAN DRIVE THE POINT BALANCE BELOW ZERO. THE
    /// LOSER IS REFUSED; NOBODY WAITS FOR ANYBODY.
    ///
    /// THE EXPLOIT (2026-08-06, reported verbatim): "when you assign a skill point to a soldier a
    /// confirmation window appears first. My friend and I picked DIFFERENT skills on the SAME soldier, and we
    /// only had enough points for ONE. We both reached the confirmation window and both pressed confirm — and
    /// BOTH skills were learned and the points went NEGATIVE."
    ///
    /// THE CLIENT LEG WAS NEVER THE HOLE. A client's purchase is blocked locally and ships as intent 0xAF
    /// op 2; the host answers it with `PersonnelSync.Charge`, which refuses an over-spend and says so on a
    /// rejected intent. THE HOST'S OWN PURCHASE RUNS FULLY NATIVE, and native asks about affordability
    /// EXACTLY ONCE — at the CLICK that opens the window (`UIModuleCharacterProgression
    /// .OnTrackSlotPointerClicked`:1029 → `CanAffordSkill`:1052) — and NEVER AGAIN AT THE CONFIRM. Between
    /// those two moments an arbitrary amount of other people's spending can land. And when it has,
    /// `ConsumeAbilityCost`:428-441 does not refuse: it clamps only the PERSONAL pool at zero and lets
    /// `_currentFactionPoints` run straight through, after which `CommitStatChanges`:375-378 writes that
    /// number into `GeoPhoenixFaction.Skillpoints` ABSOLUTELY. A negative shared purse, both skills learned,
    /// no exception and no log line — the silent-swallow class this repo is built against.
    ///
    /// THE FIX IS THE SAME VERDICT, ASKED AT THE COMMIT. `PersonnelSync.HostSpendGate` runs
    /// `CanAfford` — the identical predicate the client's intent is judged by, so this mod has ONE
    /// affordability rule and not two — as a prefix on both native purchase funnels the host still runs
    /// (`BuyAbility` and `ChoseSecondSpecialization`: same purse, same missing check). Unaffordable → native
    /// never runs, the funnel's own widget release runs instead, and the tree behind re-greys the slot from
    /// the model NATIVELY (`AbilityTrackContainerElement.SetAbilitySlot`:230-248 → `IsAbilityBuyable` →
    /// `SetSkillState(isBuyable:false)`).
    ///
    /// NOT A QUORUM, AND THIS IS THE PART TO READ BEFORE "IMPROVING" IT. Nothing is acked, nothing is
    /// reserved, nothing waits: the host reads its own authoritative model and answers in the same frame, and
    /// a client's loss is delivered by the reject it already had. An AFK peer cannot hold anyone's purchase.
    /// Arm (d) is what keeps a well-meaning wait from being added later.
    ///
    /// AND THE STAGE IS RE-SEEDED, WHICH IS A SECOND BUG IN THE SAME PLACE. `_currentSkillPoints` /
    /// `_currentFactionPoints` are a PER-VISIT SNAPSHOT of a SHARED purse and the commit writes them back
    /// ABSOLUTELY — so a purchase priced against a stale snapshot does not merely mis-charge, it REFUNDS the
    /// other peer's spend. `HostSpendGate` points both pools at the live model before letting native pay.
    ///
    /// THE REACTIVE HALF (postulate 1) is `UiNativeRepaint.CloseUnaffordableBuyConfirm`: the peer that lost
    /// the race should not be left facing a live-looking Confirm button for points that are gone. The window
    /// is CLOSED rather than greyed, and the reason is that closing is the only version made of native parts —
    /// `ConfirmBuyAbilityDataBind` holds the cost text, the icon, the name and the description and NO BUTTON
    /// (Confirm is wired to `UIModal.Confirm()` in the prefab) and `UIModal` exposes none either, so greying
    /// would mean hunting a button down a transform tree and guessing which one it is: a lookup that fails
    /// SILENTLY when it misses. `GeoscapeView.FinishQueriedState`:2164 is what the game itself runs when the
    /// player cancels (`UIStateGeoModal.OnCancel`:151-155), and its `ExitState`:116-119 then hands the
    /// unhandled callback `ModalResult.Close`, so `UIStateEditSoldier.ConfirmationHandler`:719-722 releases
    /// the offer properly. The player lands on the tree with that skill already grey — which is the greying
    /// they asked for, built out of nothing of ours.
    ///
    /// ARMS: (a) EXECUTES the economy over concurrent-confirmation sequences and asserts the OUTCOME (never
    /// negative, never more spent than the purse held); (b) the guard is not decorative; (c) both native
    /// purchase funnels are gated — this is the arm that goes red if the fix is removed; (d) the gate is not
    /// a blocker; (e) the reactive half is wired to the game's own dismissal.
    /// FALSIFY: make `CanAfford(int,int,int)` always true → (a); drop the `HostSpendGate` call from either
    /// Prefix → (c).
    ///
    /// STATED LIMIT: two confirms that reach the host inside the SAME frame are still ordered by the host —
    /// that is the point, one of them is simply second and is refused. What is NOT asserted is that the loser
    /// always sees the window close first; if the delta and the click race, the refusal is what holds, and
    /// the refusal is arm (a).
    /// </summary>
    internal static class L150_NoConcurrentSpendGoesNegative
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(PersonnelSync).Assembly;
            var canAfford = typeof(PersonnelSync).GetMethods(All).FirstOrDefault(m =>
                m.Name == "CanAfford" && m.GetParameters().Length == 3);
            var debit = typeof(PersonnelSync).GetMethod("Debit", All);
            var gate = typeof(PersonnelSync).GetMethods(All).FirstOrDefault(m =>
                m.Name == "HostSpendGate" && m.GetParameters().Length == 5);
            var charge = typeof(PersonnelSync).GetMethod("Charge", All);
            var closeConfirm = typeof(UiNativeRepaint).GetMethod("CloseUnaffordableBuyConfirm", All);

            if (canAfford == null || debit == null || gate == null || charge == null || closeConfirm == null)
            {
                yield return "L150 premise-changed: PersonnelSync.{CanAfford(int,int,int),Debit,HostSpendGate," +
                             "Charge} / UiNativeRepaint.CloseUnaffordableBuyConfirm no longer resolve. The " +
                             "over-spend refusal has moved and this law is asserting something about a shape the " +
                             "mod no longer has — re-read it before assuming the purse still cannot go negative.";
                yield break;
            }

            // ── (a) THE OUTCOME, EXECUTED: drive real confirmation sequences through the economy ──
            // Every case is "N peers each hold a confirmation window open on the same soldier and every one of
            // them presses confirm". The host serialises them; the law asks only what the purse looks like
            // afterwards. Costs deliberately include the reported shape (two skills, one skill's worth of
            // points) plus overflow across the personal/shared split and a free slot.
            Func<int, int, int, bool> afford = (p, s, c) => (bool)canAfford.Invoke(null, new object[] { p, s, c });
            var debitArgs = new object[3];
            Action<int, int[]> pay = (cost, purse) =>
            {
                debitArgs[0] = cost; debitArgs[1] = purse[0]; debitArgs[2] = purse[1];
                debit.Invoke(null, debitArgs);
                purse[0] = (int)debitArgs[1]; purse[1] = (int)debitArgs[2];
            };

            var scenarios = new[]
            {
                new { Name = "the reported exploit (two peers, one skill's worth)", Personal = 0, Shared = 10, Costs = new[] { 10, 10 } },
                new { Name = "two peers, personal pool only",                       Personal = 10, Shared = 0,  Costs = new[] { 10, 10 } },
                new { Name = "three peers, spend straddles the split",              Personal = 4,  Shared = 6,  Costs = new[] { 7, 5, 3 } },
                new { Name = "an empty purse, everybody confirms",                  Personal = 0,  Shared = 0,  Costs = new[] { 1, 1, 1 } },
                new { Name = "free slots never refused",                            Personal = 0,  Shared = 0,  Costs = new[] { 0, 0 } },
                new { Name = "exact change, then one too many",                     Personal = 3,  Shared = 2,  Costs = new[] { 5, 1 } },
            };

            foreach (var sc in scenarios)
            {
                var purse = new[] { sc.Personal, sc.Shared };
                int started = sc.Personal + sc.Shared, spent = 0, granted = 0;
                foreach (int cost in sc.Costs)
                {
                    if (!afford(purse[0], purse[1], cost)) continue;   // the loser is refused, and that is all
                    pay(cost, purse);
                    spent += cost;
                    granted++;
                }
                if (purse[0] < 0 || purse[1] < 0)
                    yield return "L150 balance-went-negative (" + sc.Name + "): after " + granted +
                                 " confirmation(s) out of " + sc.Costs.Length + " the purse reads personal=" +
                                 purse[0] + " shared=" + purse[1] + ". This is the reported exploit exactly: two " +
                                 "peers confirming different skills on one soldier with one skill's worth of " +
                                 "points between them, both granted, the shared pool underwater. A purse that can " +
                                 "go negative is not a slow leak — it is free skills for as long as anyone keeps " +
                                 "clicking.";
                if (spent > started)
                    yield return "L150 spent-more-than-was-held (" + sc.Name + "): " + spent +
                                 " points were charged out of a purse that started with " + started +
                                 ". Even without going negative, granting more than the purse held means two " +
                                 "peers bought one pile of points twice — the same exploit, arrived at by " +
                                 "arithmetic that clamps instead of refusing.";
                if (sc.Costs.All(c => c <= 0) && granted != sc.Costs.Length)
                    yield return "L150 free-slot-refused (" + sc.Name + "): a zero-cost purchase was turned away. " +
                                 "The native tracks express a slot that costs nothing as cost <= 0, so refusing " +
                                 "those refuses legal purchases and the fix becomes a worse bug than the exploit.";
            }

            // ── (b) one affordability rule, not two ──────────────────────────────────────────────
            // Both legs must reach a CanAfford, and the live-model overload must reach the PURE one — that
            // last hop is what makes arm (a)'s executed grid a statement about the shipped code rather than
            // about a copy of it that happens to live next door.
            var liveAfford = typeof(PersonnelSync).GetMethods(All).FirstOrDefault(m =>
                m.Name == "CanAfford" && m.GetParameters().Length == 2);
            if (!Program.Callees(charge, modAsm).Any(c => c.Name == "CanAfford"))
                yield return "L150 charge-has-its-own-verdict: PersonnelSync.Charge no longer routes through " +
                             "CanAfford. Charge answers every CLIENT's purchase and HostSpendGate answers the " +
                             "HOST's own; the moment they stop sharing one predicate, the mod has two " +
                             "affordability rules that will drift, and only one of them is the one arm (a) proves.";
            if (liveAfford == null ||
                !Program.Callees(liveAfford, modAsm).Any(c => c.MetadataToken == canAfford.MetadataToken))
                yield return "L150 verdict-is-not-the-executed-one: CanAfford(GeoCharacter,int) no longer routes " +
                             "into CanAfford(int,int,int). The pure overload is the ONLY thing a console harness " +
                             "can execute, so if the live one stops delegating to it, arm (a) proves a purse the " +
                             "game never uses and the shipped verdict goes back to being an unverified comment.";
            if (!Program.Callees(gate, modAsm).Any(c => c.Name == "CanAfford"))
                yield return "L150 gate-asks-nothing: HostSpendGate no longer calls CanAfford. Then it is a " +
                             "comment: native pays out of the stage with the only check it ever had left behind " +
                             "at the click that opened the window.";

            // ── (c) both native purchase funnels are actually gated ──────────────────────────────
            foreach (var patch in new[] { "BuyAbilityCapturePatch", "SecondSpecCapturePatch" })
            {
                var prefix = modAsm.GetType("Multiplayer.Network.Sync.PersonnelSync+" + patch)?.GetMethod("Prefix", All);
                if (prefix == null)
                {
                    yield return "L150 funnel-patch-missing (" + patch + "): its Prefix no longer resolves, so " +
                                 "nothing can be said about whether the host's own purchase through that funnel is " +
                                 "checked at all.";
                    continue;
                }
                if (!Program.Callees(prefix, modAsm).Any(c => c.Name == "HostSpendGate"))
                    yield return "L150 host-spend-ungated (" + patch + "): the host branch of this Prefix no longer " +
                                 "reaches HostSpendGate. This is the whole fix. Without it the host's own purchase " +
                                 "runs the untouched native body, which asked about affordability once at the click " +
                                 "and never at the confirm — and ConsumeAbilityCost:435-441 clamps only the personal " +
                                 "pool, so the shared one goes negative and CommitStatChanges:375-378 writes that " +
                                 "number into the faction ABSOLUTELY.";
            }

            // ── (d) the refusal must never become a wait ─────────────────────────────────────────
            var blockers = new[] { "GetRosterSlots", "AllDone", "RunUntilComplete", "WaitFor", "IsDone" };
            var waiting = Program.Callees(gate, modAsm).Select(c => c.Name).Where(n => blockers.Contains(n)).Distinct().ToArray();
            if (waiting.Length > 0)
                yield return "L150 refusal-waits-on-a-peer: HostSpendGate reaches " + string.Join(", ", waiting) +
                             ". The host is AUTHORITATIVE over this purse and answers from its own model in the " +
                             "same frame — there is nothing to collect from anybody. A confirmation that waits on " +
                             "another human is a quorum, and this mod does not have quorums: an AFK-but-connected " +
                             "peer never leaves the roster, so such a gate hangs forever.";

            // ── (e) the loser's window closes through the game's own dismissal ───────────────────
            if (!UsesModalEntry())
                yield return "L150 confirm-window-not-repainted: the UiNativeRepaint entry for UIStateGeoModal no " +
                             "longer reaches CloseUnaffordableBuyConfirm. An unpaid OFFER over a shared purse is " +
                             "the one modal that is not a frozen snapshot of something that already happened, and " +
                             "leaving it un-repainted leaves the peer that lost the race staring at a live-looking " +
                             "Confirm button for points that are gone (postulate 1).";
            var closeCallees = Program.Callees(closeConfirm, modAsm)
                .Concat(Program.Callees(closeConfirm, typeof(GeoscapeView).Assembly)).ToList();
            if (!closeCallees.Any(c => c.Name == "FinishQueriedState"))
                yield return "L150 confirm-window-closed-by-hand: CloseUnaffordableBuyConfirm no longer calls " +
                             "GeoscapeView.FinishQueriedState. That is the game's OWN modal dismissal — the very " +
                             "call UIStateGeoModal.OnCancel:151-155 makes — and taking it means ExitState:116-119 " +
                             "hands the callback ModalResult.Close so ConfirmationHandler:719-722 runs " +
                             "ClearBoughtAbility and the offer is released. Anything hand-rolled here either " +
                             "strands the offer armed or fires the callback twice.";
            if (!closeCallees.Any(c => c.Name == "CanAfford"))
                yield return "L150 confirm-window-closes-blindly: CloseUnaffordableBuyConfirm no longer asks " +
                             "CanAfford. It must close ONLY an offer that has become unaffordable — a modal torn " +
                             "off a player's screen on any other rail delta is a worse defect than the one it was " +
                             "written for.";
        }

        /// <summary>The Table is a dictionary of compiled lambdas, so its UIStateGeoModal arm has no name to
        /// look up — it is a closure method the compiler placed either on the type or on its `&lt;&gt;c` cache
        /// class. Read it the only way that survives a compiler's choice: any method ANYWHERE on
        /// UiNativeRepaint or its nested types that reaches the close.</summary>
        private static bool UsesModalEntry()
        {
            var modAsm = typeof(UiNativeRepaint).Assembly;
            var close = typeof(UiNativeRepaint).GetMethod("CloseUnaffordableBuyConfirm", All);
            if (close == null) return false;
            var owners = new List<Type> { typeof(UiNativeRepaint) };
            owners.AddRange(typeof(UiNativeRepaint).GetNestedTypes(All));
            foreach (var t in owners)
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All | BindingFlags.Static)))
                    if (Program.Callees(m, modAsm).Any(c => c.MetadataToken == close.MetadataToken)) return true;
            return false;
        }
    }
}
