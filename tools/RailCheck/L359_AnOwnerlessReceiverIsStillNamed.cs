using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L359 — A REFUSAL THIS RAIL CANNOT FIX MUST STILL NAME WHAT IT REFUSED.
    ///
    /// <c>OnDamageApplied</c> refuses damage whose receiver has no actor, and that refusal is CORRECT and
    /// permanent: <c>Item.Actor</c>:24-42 and <c>ItemSlot.GetActor</c>:142-145 both answer null when there is
    /// no <c>InventoryComponent</c> and no <c>ActorComponent</c> on the rig root, and
    /// <c>DamageReceiverImplementation.OnActorExitPlay</c>:97-101 nulls <c>_actorBase</c> outright — so damage
    /// landing after an actor leaves play arrives here by design. Giving it a full address would need an
    /// identity scheme for non-actor items, which is a redesign and is deliberately NOT attempted.
    ///
    /// WHAT WAS WRONG WAS THE DIAGNOSTIC. Both the message and the <c>SayOnce</c> key collapsed to the literal
    /// <c>&lt;null&gt;</c>: one dedup key for every object in the game. So a recurrence could not be told from a
    /// repetition, the type and the def were both thrown away, and the only evidence that would say whether
    /// this is one stray explosion or a systematic hole was destroyed at the moment it was produced. A refusal
    /// that cannot be diagnosed is a silent swallow with a log line attached.
    ///
    /// Falsify (each verified RED, then restored): key the SayOnce on the literal "&lt;null&gt;" again → (c)
    /// key-collapses; make <c>Describe</c> return a constant → (a) shapes-indistinguishable; drop the
    /// <c>GetType()</c> from it → (b).
    /// </summary>
    internal static class L359_AnOwnerlessReceiverIsStillNamed
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(TacticalDamageSync);
            var asm = sync.Assembly;
            var describe = sync.GetMethod("Describe", All);
            var shipped = sync.GetMethod("OnDamageApplied", All);
            if (describe == null || describe.ReturnType != typeof(string) || shipped == null)
            {
                yield return "L359 premise-changed: TacticalDamageSync.Describe(IDamageReceiver,string)->string or " +
                             "OnDamageApplied no longer resolves. Describe is the whole of this law's subject — the " +
                             "one place an unaddressable receiver is turned into something a reader can tell apart " +
                             "from the next one.";
                yield break;
            }

            // ── (a) EXECUTED: TWO DIFFERENT OBJECTS ARE TWO DIFFERENT STRINGS ────────
            // Deliberately built WITHOUT defs: a def's `name` is a native ECall and would throw here. The
            // question this arm asks is the one that broke — does the description vary with the object at all.
            string built = null, forItem = null, forSlot = null, forNull = null;
            try
            {
                var item = (TacticalItem)FormatterServices.GetUninitializedObject(typeof(TacticalItem));
                var slot = (ItemSlot)FormatterServices.GetUninitializedObject(typeof(ItemSlot));
                forItem = (string)describe.Invoke(null, new object[] { item, "" });
                forSlot = (string)describe.Invoke(null, new object[] { slot, "" });
                forNull = (string)describe.Invoke(null, new object[] { null, "" });
            }
            catch (Exception ex) { built = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            if (built != null)
            {
                yield return "L359 describe-throws: naming an unaddressable receiver threw (" + built + "). This runs " +
                             "inside the host's damage capture, so a throw here does not lose a diagnostic — it loses " +
                             "the hit.";
                yield break;
            }
            if (string.IsNullOrEmpty(forItem) || string.IsNullOrEmpty(forSlot) || string.IsNullOrEmpty(forNull))
                yield return "L359 describe-empty: an unaddressable receiver was described as nothing at all, which " +
                             "is the empty dedup key by another route.";
            else if (forItem == forSlot)
                yield return "L359 shapes-indistinguishable: a TacticalItem and an ItemSlot are described " +
                             "identically ('" + forItem + "'). One SayOnce key then swallows every distinct object " +
                             "that ever reaches this refusal — which is the reported defect verbatim, and it is why " +
                             "nobody could say whether the four lines in the capture were four events or four defs.";
            if (forItem != null && forItem.IndexOf("TacticalItem", StringComparison.Ordinal) < 0)
                yield return "L359 type-not-named: the description of a TacticalItem does not contain its type. The " +
                             "type is the one fact always available on an object with no address at all.";

            // ── (b) IT IS DERIVED, NOT HARDCODED ─────────────────────────────────────
            if (!Program.CalleeSequence(describe).Any(c => c.Name == "GetType"))
                yield return "L359 type-hardcoded: Describe never asks the receiver its type. A fixed list of shapes " +
                             "is exactly what fails on the shape nobody predicted — which is the only shape that " +
                             "ever reaches an unaddressable-receiver refusal.";

            // ── (c) THE REFUSAL USES IT, AND THE OLD KEY IS GONE ─────────────────────
            if (!Program.Callees(shipped, asm).Any(c => c.MetadataToken == describe.MetadataToken &&
                                                        c.Module == describe.Module))
                yield return "L359 refusal-unnamed: OnDamageApplied does not call Describe, so whatever it does name " +
                             "the object by, it is not the one rule this law can check.";
            if (Program.StringRefs(shipped).Any(s => s.IndexOf("<null>", StringComparison.Ordinal) >= 0))
                yield return "L359 key-collapses: OnDamageApplied still carries the literal \"<null>\". That literal " +
                             "WAS the bug: it was both the message and the SayOnce key, so every ownerless receiver " +
                             "in the mission deduped onto one line and the recurrence became undiagnosable.";
        }
    }
}
