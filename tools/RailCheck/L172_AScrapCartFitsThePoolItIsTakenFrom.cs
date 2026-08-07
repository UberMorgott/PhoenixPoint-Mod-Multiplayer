using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Items;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Tactical.Entities.Equipments;

namespace RailCheck
{
    /// <summary>
    /// L172 — A SCRAP CART FITS THE POOL IT IS TAKEN FROM.
    ///
    /// THE REPORT (2026-08-07, client Player.log around :20522 — 31 engine errors in twelve seconds while
    /// the peer clicked "add to scrap cart"):
    /// <c>Trying to remove 5 items with 36 charges but only 4 items with 32 charges available in
    /// PX_SniperRifle_AmmoClip_ItemDef</c> (x15), the same for <c>PX_Pistol_AmmoClip</c> 5/35-vs-4/32 (x11)
    /// and <c>PX_AssaultRifle_AmmoClip</c> 17/600-vs-16/576 (x2). Every one is off by EXACTLY ONE MAGAZINE,
    /// and the missing charges are exactly one partial clip's worth.
    ///
    /// IT WAS NEVER A MIRRORING FAULT. The client's storage matched the host's — <c>ItemStorage</c> is
    /// covered 2/2 and <c>GeoItem</c> 1/1 on the rail baseline, and <c>CommonItemData._charges</c> rides as a
    /// covered leaf, so per-item charge state does survive the wire. The two numbers that disagreed both
    /// live on the SAME machine: the faction stack (5 clips, 36 charges) and the scrap screen's own pool.
    /// <c>UIModuleManufacturing.Init</c>:363-371 builds that pool as Clear + AddItems(faction storage) +
    /// <c>StripPartialMagsFromScrapStorage</c>, and the strip (:1075-1092) WITHHOLDS one partial magazine
    /// per def — leaving 4 clips / 32 charges. The pool, not the stack, is what a staged item is finally
    /// subtracted from.
    ///
    /// SO A CLIENT COULD STAGE A MAGAZINE THAT DID NOT EXIST FOR IT. A host cannot: its click runs native
    /// <c>AddToScrapQueue</c>:1109-1116, which moves the item OUT of the pool and throws once the pool is
    /// empty. A client's click is BLOCKED and sent as an intent, and the host validated it with
    /// <c>CartLiveCount</c> reading <c>CommonItemData.Count</c> off the raw stack — 5, one more than the
    /// screen ever offered. <c>ResyncScrapSnapshot</c> then clamped and charge-copied against that same raw
    /// stack, so the cart also absorbed the PARTIAL clip's charges, and its closing
    /// <c>snapshot.RemoveItems(cart)</c> underflowed. <c>ItemStorage.RemoveItem</c>:87-89 drops an entry
    /// whose count reaches zero, so the ammo then vanished off the scrap screen entirely.
    ///
    /// ARM (a) IS THE OUTCOME, EXECUTED, BOTH WAYS. <c>ManufactureSync.ScrappableCount</c> is run on three
    /// real <c>GeoItem</c>s: a five-stack with a partial clip must answer 4 (the withheld magazine), a
    /// five-stack of full clips must answer 5 (withholding from a full stack would silently forbid scrapping
    /// the last one of everything), and a five-stack of an AMMO-COMPATIBLE def whose charges are down must
    /// answer 5 — the strip's own second condition, <c>!CompatibleAmmunition.Any()</c>, exempts weapons.
    /// Return <c>Count</c> flat and case one fails; return <c>Count - 1</c> flat and cases two and three do.
    ///
    /// ARM (b) IS THE HOST DENOMINATOR. <c>CartLiveCount</c> — the ceiling every client cart-add intent is
    /// checked against — and <c>PruneCartsToLiveStorage</c> must both go through that one helper. Reading
    /// <c>Count</c> off the stack again is the original bug verbatim.
    ///
    /// ARM (c) IS THE ORDER ON THE CLIENT SCREEN. <c>ResyncScrapSnapshot</c> must REBUILD the pool
    /// (<c>ItemStorage.Clear</c>) before it touches the cart at all — before the first
    /// <c>ItemStorage.RemoveItem</c> (the clamp) and the first <c>ItemStorage.AddItem</c> (the reconcile).
    /// Rebuild last, as it did, and both of those measure the cart against the raw stack no matter what the
    /// helper says.
    ///
    /// ARM (d) IS THE SILENCE. The overdraw was reported only by the GAME, as a bare <c>Debug.LogError</c>
    /// naming a def and no peer, no cart and no screen — the swallow class this repo keeps paying for.
    /// <c>ResyncScrapSnapshot</c> must say it itself, before the subtract that underflows.
    ///
    /// ARM (e) IS THE POSITIVE CONTROL, taken from the GAME. <c>Init</c> must still call the strip, and the
    /// strip must still decide by <c>ItemDef.ChargesMax</c> + <c>ItemDef.CompatibleAmmunition</c> and still
    /// remove from the pool. The instant the game stops withholding partial magazines, arm (a)'s -1 is
    /// wrong and this whole law is enforcing a rule the game abandoned.
    ///
    /// Falsify: <c>ScrappableCount</c> returns <c>Count</c> → <c>partial-not-withheld</c>; returns
    /// <c>Count - 1</c> → <c>full-stack-shorted</c> / <c>weapon-shorted</c>; point <c>CartLiveCount</c> back
    /// at the raw stack → <c>raw-stack-denominator</c>; move the pool rebuild back below the cart clamp →
    /// <c>cart-measured-before-rebuild</c>; drop the shortfall log → <c>shortfall-silent</c>; change what the
    /// game's strip does → <c>POSITIVE CONTROL</c>; rename any member → <c>premise-changed</c>.
    /// </summary>
    internal static class L172_AScrapCartFitsThePoolItIsTakenFrom
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var sync = typeof(ManufactureSync);
            var game = typeof(UIModuleManufacturing).Assembly;

            var scrappable = sync.GetMethod("ScrappableCount", All);
            var cartLive = sync.GetMethod("CartLiveCount", All);
            var resync = sync.GetMethod("ResyncScrapSnapshot", All);
            var prune = sync.GetNestedType("ScrapCapturePatch", All)?.GetMethod("PruneCartsToLiveStorage", All);
            var init = typeof(UIModuleManufacturing).GetMethod("Init", All);
            var strip = typeof(UIModuleManufacturing).GetMethod("StripPartialMagsFromScrapStorage", All);
            var chargesMax = typeof(ItemDef).GetField("ChargesMax", All);
            var compatible = typeof(ItemDef).GetField("CompatibleAmmunition", All);
            var storageRemove = typeof(ItemStorage).GetMethod("RemoveItem", All);
            var storageAdd = typeof(ItemStorage).GetMethod("AddItem", All);
            var storageClear = typeof(ItemStorage).GetMethod("Clear", All);

            if (scrappable == null || cartLive == null || resync == null || prune == null ||
                init == null || strip == null || chargesMax == null || compatible == null ||
                storageRemove == null || storageAdd == null || storageClear == null)
            {
                yield return "L172 premise-changed: one of ManufactureSync.{ScrappableCount,CartLiveCount," +
                             "ResyncScrapSnapshot}, ScrapCapturePatch.PruneCartsToLiveStorage, " +
                             "UIModuleManufacturing.{Init,StripPartialMagsFromScrapStorage}, " +
                             "ItemDef.{ChargesMax,CompatibleAmmunition} or ItemStorage.{AddItem,RemoveItem," +
                             "Clear} no longer resolves. The scrap screen's pool is built out of exactly " +
                             "those pieces — re-derive how many of a def that screen offers before trusting " +
                             "any cart staged against it.";
                yield break;
            }

            // ── (a) THE OUTCOME, EXECUTED ──
            var plain = MakeDef(chargesMax, compatible, ammoCompatible: false);
            var weapon = MakeDef(chargesMax, compatible, ammoCompatible: true);
            if (ReferenceEquals(plain, null) || ReferenceEquals(weapon, null))
            {
                yield return "L172 premise-changed: cannot materialise an ItemDef to stand in for an ammo " +
                             "clip, so the executed arm cannot run. Without it this law would only ever " +
                             "check where the number is READ and never what it IS, and a ScrappableCount " +
                             "that returned the raw count would pass every remaining arm.";
                yield break;
            }

            foreach (var probe in new[]
                     {
                         // count, charges, def, expected, tag, why
                         Probe(5, 4, plain, 4, "partial-not-withheld",
                               "a five-stack whose last clip holds 4 of 8 charges. The screen offers FOUR: " +
                               "StripPartialMagsFromScrapStorage:1082 pulls the partial magazine out of the " +
                               "pool. Answering 5 is the reported bug verbatim — the host accepts a fifth " +
                               "cart-add, the subtract in ResyncScrapSnapshot underflows, the engine says " +
                               "'only 4 items with 32 charges available', and RemoveItem:87-89 then drops " +
                               "the def off the screen for everyone looking at it."),
                         Probe(5, 8, plain, 5, "full-stack-shorted",
                               "a five-stack of FULL clips. Nothing is withheld from it (:1082 tests " +
                               "CurrentCharges != ChargesMax), so all five are scrappable. Shaving one off " +
                               "unconditionally would leave one magazine of every stack in the game " +
                               "permanently unscrappable, with no message saying why."),
                         Probe(5, 4, weapon, 5, "weapon-shorted",
                               "a five-stack of a def WITH CompatibleAmmunition and its charges down. The " +
                               "strip's second condition (:1081-1082) exempts anything that takes ammo — its " +
                               "charge count is a loaded magazine, not a partial stack item. Withholding one " +
                               "would make weapons unscrappable one at a time."),
                     })
            {
                int got = Answer(scrappable, probe.Item3, probe.Item1, probe.Item2);
                if (got != probe.Item4)
                    yield return "L172 " + probe.Item5 + ": ScrappableCount answered " + got + " instead of " +
                                 probe.Item4 + " for " + probe.Item6;
            }

            // ── (b) THE HOST DENOMINATOR ──
            foreach (var caller in new[] { cartLive, prune })
                if (!Program.Callees(caller, sync.Assembly).Any(c => Same(c, scrappable)))
                    yield return "L172 raw-stack-denominator: ManufactureSync." + caller.Name + " does not go " +
                                 "through ScrappableCount. It is measuring a scrap cart against the RAW " +
                                 "faction stack again — the exact shape that let a client stage a fifth " +
                                 "magazine of a four-magazine pool. A host clicking natively cannot make " +
                                 "this mistake (AddToScrapQueue moves items out of the pool and throws when " +
                                 "it is empty); the client's blocked click is validated HERE instead, so " +
                                 "this is the only place the ceiling exists for it.";

            // ── (c) THE ORDER ON THE CLIENT SCREEN ──
            var seq = Program.Callees(resync, game).ToList();
            int clearAt = seq.FindIndex(c => Same(c, storageClear));
            int removeAt = seq.FindIndex(c => Same(c, storageRemove));
            int addAt = seq.FindIndex(c => Same(c, storageAdd));
            if (clearAt < 0)
                yield return "L172 cart-measured-before-rebuild: ResyncScrapSnapshot never calls " +
                             "ItemStorage.Clear, so it is not rebuilding the scrap pool at all. Rebuilding " +
                             "it is the whole point of the method — the native pool is snapshotted once on " +
                             "mode entry and goes stale the instant a remote delta lands.";
            else
                foreach (var later in new[]
                         {
                             new KeyValuePair<int, string>(removeAt, "clamps the cart down (ItemStorage.RemoveItem)"),
                             new KeyValuePair<int, string>(addAt, "stages into the cart (ItemStorage.AddItem)"),
                         })
                    if (later.Key >= 0 && later.Key < clearAt)
                        yield return "L172 cart-measured-before-rebuild: ResyncScrapSnapshot " + later.Value +
                                     " BEFORE it rebuilds the pool with ItemStorage.Clear. Whatever it " +
                                     "compares the cart to at that point is the raw faction stack, partial " +
                                     "magazine included — and the counts AND the per-item charges it copies " +
                                     "from there are both a magazine off. Order is the fix here, not a " +
                                     "tidy-up: with the pool rebuilt first, the entry it reads is " +
                                     "full-charged precisely because the partial was withheld, so each " +
                                     "staged add moves the cart's count by one instead of being merged away " +
                                     "by CommonItemData.AddItem:205-213.";

            // ── (d) THE SILENCE ──
            if (!Program.Callees(resync, typeof(UnityEngine.Debug).Assembly).Any(c => c.Name == "LogError"))
                yield return "L172 shortfall-silent: ResyncScrapSnapshot does not report a cart it cannot " +
                             "pay for. The engine's own complaint is a bare Debug.LogError from " +
                             "CommonItemData.Subtract:224 naming a def and nothing else — not the peer, not " +
                             "the cart, not the screen, not the shared store behind it. Thirty-one of them " +
                             "went by in the owner's log looking like a game bug. A shortfall this code can " +
                             "see one line before it happens has to be said out loud by this code.";

            // ── (e) POSITIVE CONTROL: the game still withholds the partial magazine ──
            if (!Program.Callees(init, game).Any(c => Same(c, strip)))
                yield return "L172 POSITIVE CONTROL: UIModuleManufacturing.Init no longer calls " +
                             "StripPartialMagsFromScrapStorage. The whole -1 in ScrappableCount exists to " +
                             "mirror that call — if the scrap pool is now built some other way, arm (a) is " +
                             "asserting a rule the game dropped and every cart ceiling in this file needs " +
                             "re-deriving from whatever replaced it.";
            if (!Program.ReadsField(strip, chargesMax) || !Program.ReadsField(strip, compatible) ||
                !Program.Callees(strip, game).Any(c => Same(c, storageRemove)))
                yield return "L172 POSITIVE CONTROL: StripPartialMagsFromScrapStorage no longer decides by " +
                             "ItemDef.ChargesMax and ItemDef.CompatibleAmmunition, or no longer removes from " +
                             "the pool. ScrappableCount reproduces that predicate line for line " +
                             "(:1081-1082); a strip that now withholds by some other test, or withholds " +
                             "nothing, makes our copy of it wrong in a way arm (a) would happily keep " +
                             "proving against itself.";
        }

        /// <summary>An ItemDef standing in for an ammo clip: 8 charges to a magazine, stacks by combining
        /// charges (the shape every PX_*_AmmoClip_ItemDef in the report has). <paramref name="ammoCompatible"/>
        /// makes it a thing that TAKES ammo instead — the strip's exemption.</summary>
        private static ItemDef MakeDef(FieldInfo chargesMax, FieldInfo compatible, bool ammoCompatible)
        {
            try
            {
                // ReferenceEquals, NOT ==: UnityEngine.Object's operator== reports an object with a zero
                // native pointer AS NULL, and every def built here has one. `== null` silently discarded
                // a perfectly good stand-in and turned this arm into premise-changed.
                var def = FormatterServices.GetUninitializedObject(typeof(ItemDef)) as ItemDef;
                if (ReferenceEquals(def, null)) return null;
                Enliven(def);
                chargesMax.SetValue(def, 8);
                var ammoDef = FormatterServices.GetUninitializedObject(typeof(TacticalItemDef)) as TacticalItemDef;
                Enliven(ammoDef);
                compatible.SetValue(def, ammoCompatible ? new[] { ammoDef } : new TacticalItemDef[0]);
                typeof(ItemDef).GetField("CombineWhenStacking", All)?.SetValue(def, true);
                return def;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Give a hand-built def a NON-ZERO native pointer. Unity's <c>Object.operator ==</c> calls
        /// an object with a zero <c>m_CachedPtr</c> null — the "fake null" that reports a destroyed asset —
        /// and production code is RIGHT to ask <c>def == null</c> that way. Without this the stand-in reads
        /// as null inside <c>ScrappableCount</c>, which then answers 0 for every probe and the executed arm
        /// tests nothing. The pointer is never dereferenced: no arm here calls into Unity.</summary>
        private static void Enliven(object obj)
        {
            if (obj == null) return;
            typeof(UnityEngine.Object).GetField("m_CachedPtr", All)?.SetValue(obj, new IntPtr(1));
            typeof(UnityEngine.Object).GetField("m_InstanceID", All)?.SetValue(obj, 1);
        }

        private static Tuple<int, int, ItemDef, int, string, string> Probe(
            int count, int charges, ItemDef def, int expected, string tag, string why) =>
            Tuple.Create(count, charges, def, expected, tag, why);

        /// <summary>-1 = ScrappableCount threw or a GeoItem could not be built; either way the arm reports a
        /// mismatch rather than passing on an answer nobody got.</summary>
        private static int Answer(MethodInfo scrappable, ItemDef def, int count, int charges)
        {
            try { return (int)scrappable.Invoke(null, new object[] { new GeoItem(def, count, charges) }); }
            catch (Exception) { return -1; }
        }

        private static bool Same(MethodBase a, MethodBase b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
