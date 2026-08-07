using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L186 — THE WEAPON IN A SOLDIER'S HANDS IS THE HOST'S ANSWER, RE-ASSERTED, NOT AN EVENT THAT CAN BE
    /// MISSED.
    ///
    /// THE HOLE, proven from BOTH peers' logs of 2026-08-07 and lost in BOTH directions in the same fifteen
    /// seconds of one mission entry:
    ///  • INBOUND, five times (client 23:14:09.582-09.885) — "the host's weapon switch for actor 1..5 cannot
    ///    be applied here — no tactical map on this peer to resolve actor key N against". The host was
    ///    relaying per-actor selections while this peer had no map at all.
    ///  • OUTBOUND, three times on EACH peer (host 23:14:02.450-02.640 for Soldier_6/7/8, client
    ///    23:14:23.937-24.076 for Rancid/Cinder/Kain) — "a weapon switch on X cannot be relayed — that actor
    ///    has no shared key". <c>EquipmentComponent</c>:56 raises the enter-play selection before that peer
    ///    has built its battle key map, so <c>TacticalActorKey.Of</c> answers 0 and the relay is dropped.
    /// Nothing retried either half. The selection rode ONLY as an event (<c>OpSelectEquipment</c>), and an
    /// event is precisely what a peer that is not ready yet loses — permanently. The outbound line even
    /// predicted the consequence ("the host will refuse any ability whose source is the new one,
    /// EquipmentNotSelected"), which is the shape that gets reported later as an ability bug.
    ///
    /// THE FIX IS THE SEAM THAT ALREADY EXISTS, for the third time: the host's selection rides the 0x82 settle
    /// beside the status set (L131) and the ability-trait set (L137). The settle closes every rider action AND
    /// sweeps every keyed live actor at every turn edge, so the answer is re-asserted routinely instead of
    /// once; a switch lost in either direction converges at the next settle, under the host's authority
    /// (L66/L67/L68) rather than by a retry queue with a lifetime of its own.
    ///
    /// WHAT THIS LAW ASSERTS. Arm (a) is the game-side PREMISE, executed against the real assemblies: the
    /// identity the settle ships (an item def guid) and the writer it is applied through must still be what
    /// this arc reads. Arm (b) is structural and is the honest limit of a headless check — the settle's codec
    /// lives inside a <c>Send</c> lambda and cannot be invoked — so it asserts the two halves that make the
    /// field cross at all: the host writes it and the apply reconciles it. Arm (c) is the reactivity mandate:
    /// a selection changed on the model with no repaint reads exactly like a switch that never crossed, which
    /// is the defect the event path already carries a <c>MarkDirty</c> for. Arm (d) is the echo guard: the
    /// reconcile runs inside the apply scope, so it cannot re-enter the relay and bounce back at the host.
    ///
    /// FALSIFIED, each against a freshly built assembly (the mod rebuilt before every harness run, so no
    /// verdict came off a stale <c>Multiplayer.dll</c>): stop <c>HostSettle</c> reading the selection →
    /// <c>L186 settle-carries-no-selection</c>; drop the <c>MarkDirty</c> from <c>ReconcileSelection</c> →
    /// <c>L186 repaired-weapon-is-invisible</c>; move <c>ReconcileSelection</c> above the
    /// <c>SyncApplyScope.Enter</c> in <c>ApplySettle</c> → <c>L186 reconcile-echoes (scope@1, reconcile@0)</c>.
    /// Also falsifiable: drop the <c>ReconcileSelection</c> call from <c>ApplySettle</c> →
    /// <c>L186 settle-applies-no-selection</c>.
    /// </summary>
    internal static class L186_WeaponSelectionRidesTheSettle
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var asm = cmd.Assembly;
            var settle = cmd.GetMethod("HostSettle", All);
            var applySettle = cmd.GetMethod("ApplySettle", All);
            var reconcile = cmd.GetMethod("ReconcileSelection", All);
            var pending = cmd.GetNestedType("PendingSettle", All);
            if (settle == null || applySettle == null || reconcile == null || pending == null ||
                pending.GetField("Selected", All) == null)
            {
                yield return "L186 premise-changed: TacticalCommandSync.{HostSettle,ApplySettle," +
                             "ReconcileSelection} or PendingSettle.Selected no longer resolves. The selection is " +
                             "the field this arc moved OFF a losable event and onto the settle — re-read this law " +
                             "before assuming a lost weapon switch still repairs itself.";
                yield break;
            }

            // ── (a) THE GAME-SIDE PREMISE, read from the real assembly ────────
            var selected = typeof(EquipmentComponent).GetProperty("SelectedEquipment", All);
            var setSelected = typeof(EquipmentComponent).GetMethod("SetSelectedEquipment", All,
                                  null, new[] { typeof(Equipment) }, null);
            MemberInfo items = typeof(EquipmentComponent).GetMember("Items", All).FirstOrDefault();
            if (selected == null || setSelected == null)
                yield return "L186 selection-premise-gone: EquipmentComponent.SelectedEquipment / " +
                             "SetSelectedEquipment(Equipment) no longer have the shapes this arc reads and writes. " +
                             "The settle would then ship a field nobody can read back, silently.";
            if (typeof(Equipment).GetProperty("ItemDef", All) == null)
                yield return "L186 identity-premise-gone: Equipment.ItemDef no longer exists, so the def guid the " +
                             "settle ships is not an address either peer can resolve. Items carry no shared " +
                             "instance identity at all — the def is the only thing both peers name the same way.";
            if (items == null)
                yield return "L186 lookup-premise-gone: EquipmentComponent no longer exposes Items, so the guid on " +
                             "the wire cannot be resolved back to this peer's own equipment instance.";

            // ── (b) IT MUST CROSS, AND IT MUST LAND ──────────────────────────
            // HostSettle hands its BYTES to Send as a lambda, but it reads the selection in its own body (it
            // has to — the closure runs synchronously but the value is captured), so this arm can ask the
            // named method directly rather than scanning the type.
            var gameAsm = typeof(EquipmentComponent).Assembly;
            if (!Program.Callees(settle, gameAsm).Any(c => c.Name == "get_SelectedEquipment" &&
                                                          c.DeclaringType == typeof(EquipmentComponent)))
                yield return "L186 settle-carries-no-selection: HostSettle no longer reads SelectedEquipment on " +
                             "the way out. The selection is then back to riding only OpSelectEquipment — an event " +
                             "— and every peer that was mid-load, keyless or mapless when it passed keeps the " +
                             "weapon it was already holding for the rest of the battle.";
            if (!Program.Callees(applySettle, asm).Any(c => c.Name == "ReconcileSelection"))
                yield return "L186 settle-applies-no-selection: ApplySettle no longer reconciles the selection, so " +
                             "the host ships an answer nothing acts on. The turn-edge sweep then re-asserts " +
                             "everything about an actor except which weapon he is holding.";

            // ── (c) AND THE REPAIR MUST BE VISIBLE (reactivity mandate) ───────
            var reconcileCalls = Program.Callees(reconcile, asm).ToList();
            if (!reconcileCalls.Any(c => c.Name == "MarkDirty"))
                yield return "L186 repaired-weapon-is-invisible: ReconcileSelection changes the model and never " +
                             "marks the tactical UI dirty. A weapon switch is not an ability, so the " +
                             "AbilityExecuted postfix that drives the repaint never fires for it — the panel keeps " +
                             "the old weapon, which reads exactly like the switch never crossed.";
            if (!Program.Callees(reconcile, gameAsm).Any(c => c.Name == "get_SelectedEquipment"))
                yield return "L186 reconcile-churns: ReconcileSelection does not compare what this peer is already " +
                             "holding before writing. The turn-edge sweep settles every keyed live actor, so an " +
                             "unconditional write re-selects every soldier's weapon several times a turn — and the " +
                             "overwhelmingly common case, agreement, would narrate itself in the log.";

            // ── (d) THE ECHO GUARD: it runs inside the apply scope ────────────
            {
                var seq = Program.Callees(applySettle, asm).ToList();
                int iScope = seq.FindIndex(c => c.Name == "Enter" && c.DeclaringType != null &&
                                                c.DeclaringType.Name == "SyncApplyScope");
                int iSel = seq.FindIndex(c => c.Name == "ReconcileSelection");
                if (iScope < 0 || iSel < 0 || iScope > iSel)
                    yield return "L186 reconcile-echoes: the selection is reconciled outside SyncApplyScope " +
                                 "(scope@" + iScope + ", reconcile@" + iSel + "). OnEquipmentSelected stands down " +
                                 "only inside that scope (law 8), so the repair would be relayed straight back as " +
                                 "this peer's own switch — and on a client that is an intent the host replays, " +
                                 "which arrives here as another settle.";
            }
        }
    }
}
