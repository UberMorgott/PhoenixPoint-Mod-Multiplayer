using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Code.PhoenixPoint.Geoscape.Entities.Sites.TheMarketplace;
using HarmonyLib;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L189 — A ONE-SHOT PUSH THE RECEIVING PEER CANNOT TAKE IS HELD, NOT LOST.
    ///
    /// THE DEFECT (2026-08-07 co-op session, BOTH logs). The marketplace assortment crossed exactly once in
    /// sixteen minutes, and the one peer it was for was mid-load at that instant:
    ///   HOST   23:09:06.209  [MP][market] HOST shipped 14 offer(s) seq=1 (reroll)
    ///   CLIENT 23:09:13.725  [MP][market] offer list DROPPED — this peer has no live marketplace …
    ///                        the next push converges it
    /// `seq=1` is the ONLY market push in the whole session, so the drop line's own guarantee is FALSIFIED BY
    /// THE LOG. And it was not a race that could have gone the other way: host+7.5 s = 23:09:13.7 against the
    /// client's own `SendPrepared` for that same first campaign load at 23:09:15.484 — the client was
    /// GUARANTEED to be mid-load at the only moment offers are ever sent. Its shop stayed 14 offers behind
    /// for the rest of the session, and after that one line neither peer's log mentioned it again.
    ///
    /// THE SHAPE, not the field. A push happens on a reroll, a purchase or a reject reconverge — all HOST
    /// events — while the condition that made the peer drop it (in a battle, mid-load, no shop yet) is
    /// RECEIVER state that the host cannot see and does not poll. "The next push converges it" is therefore
    /// not a mechanism, it is a hope: with no retry, a one-shot push is a race the receiver always loses.
    ///
    /// THE ANSWER IS THE ONE THIS CODEBASE ALREADY USES. <c>EventPopup</c> holds a raise its peer cannot
    /// carry (<c>_held</c> + <c>DrainHeldRaises</c>) and replays it when the geoscape can take a window — all
    /// three HELD raises of that same session landed. The offer list now holds the same way, and the drain is
    /// <c>GeoMarketplace.OnLevelStart</c>, which is where <c>MarketplaceChoices</c> is CONSTRUCTED AT ALL
    /// (decompile GeoMarketplace.cs:41-60): the first campaign load and the return from every battle, i.e.
    /// the exact frame after which a dropped list can land, whatever the reason it was dropped. Nothing is
    /// asked of the host, so a peer that was away converges without costing every other peer a re-broadcast.
    ///
    /// STILL MISSING THE HOLD, named here so the next one is not rediscovered from a log:
    /// <c>GeoModalMirror</c>:591 and <c>CutsceneMirror</c>:201 both drop on "this peer has no live …" and
    /// nothing re-sends.
    ///
    /// Falsify: drop the list instead of holding it → <c>L189 dropped-push-is-lost</c>; keep holding a list
    /// that landed → <c>L189 stale-hold-outlives-its-list</c>; hold across a session teardown →
    /// <c>L189 hold-outlives-its-seq-stream</c>; unwire the hold from the drop path →
    /// <c>L189 drop-path-holds-nothing</c>; unwire the clear from the apply path →
    /// <c>L189 landed-list-stays-held</c>; make the drain replay nothing → <c>L189 hold-never-drains</c>;
    /// let the drain mark the seq without applying → <c>L189 unapplied-list-marked-applied</c>; point the
    /// drain at a method that is not where the shop is built → <c>L189 drain-is-not-the-moment</c>.
    /// </summary>
    internal static class L189_AOneShotPushAPeerCannotTakeIsHeld
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var ms = typeof(MarketplaceSync);
            var hold = ms.GetMethod("HoldOffers", All, null,
                           new[] { typeof(uint), typeof(List<KeyValuePair<string, float>>) }, null);
            var clear = ms.GetMethod("ClearHeldOffers", All, null, Type.EmptyTypes, null);
            var has = ms.GetProperty("HasHeldOffers", All);
            var apply = ms.GetMethod("ApplyOffers", All);
            var inbound = ms.GetMethod("HandleInbound", All);
            var reset = ms.GetMethod("Reset", All, null, Type.EmptyTypes, null);
            var retry = ms.GetNestedType("HeldOfferRetryPatch", All);
            var drain = retry?.GetMethod("Postfix", All);
            var seqType = typeof(SurfaceSeq);
            var shouldApply = seqType.GetMethod("ShouldApply", All);
            var mark = seqType.GetMethod("Mark", All);
            var onLevelStart = AccessTools.Method(typeof(GeoMarketplace), "OnLevelStart");
            var choices = AccessTools.Property(typeof(GeoMarketplace), "MarketplaceChoices");

            if (hold == null || clear == null || has == null || apply == null || inbound == null ||
                reset == null || drain == null || shouldApply == null || mark == null ||
                onLevelStart == null || choices == null)
            {
                yield return "L189 premise-changed: MarketplaceSync.HoldOffers / ClearHeldOffers / " +
                             "HasHeldOffers / ApplyOffers / HandleInbound / Reset / HeldOfferRetryPatch.Postfix, " +
                             "SurfaceSeq.ShouldApply|Mark, or GeoMarketplace.OnLevelStart|MarketplaceChoices no " +
                             "longer resolves. The offer list rides a one-shot push because it CANNOT ride the " +
                             "value rail (GeoMarketplaceInstanceData names members the live class does not " +
                             "have) — so the hold is the only thing between a mid-load peer and a shop that " +
                             "is wrong for the rest of the session.";
                yield break;
            }

            // ── (a) THE OUTCOME, over the real hold ──
            MarketplaceSync.ClearHeldOffers();
            if (MarketplaceSync.HasHeldOffers)
                yield return "L189 hold-starts-armed: a peer that has dropped nothing reports a held list, so " +
                             "the drain will replay an assortment nobody sent over a shop that is already right.";

            var rows = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("probe-offer-a", 100f),
                new KeyValuePair<string, float>("probe-offer-b", 250f),
            };
            MarketplaceSync.HoldOffers(7u, rows);
            if (!MarketplaceSync.HasHeldOffers)
                yield return "L189 dropped-push-is-lost: a list this peer could not place is not kept, which " +
                             "is the 2026-08-07 defect verbatim — one push, dropped mid-load, no retry, and a " +
                             "client shop fourteen offers behind for the remaining sixteen minutes.";

            // A REPLAY THAT FAILS IS STILL HELD, asserted where it is decidable without a game: in the drain's
            // own IL order. Every ClearHeldOffers in it must sit BEFORE the ApplyOffers call (those are the
            // early returns — not a client, or a seq already superseded); one AFTER it would throw the list
            // away on a replay that did not land, which is the original drop wearing a retry.
            var order = Program.CalleeSequence(drain);
            int applyAt = order.FindIndex(m => m.MetadataToken == apply.MetadataToken);
            if (applyAt >= 0 && order.Skip(applyAt).Any(m => m.MetadataToken == clear.MetadataToken))
                yield return "L189 failed-replay-drops-the-hold: the drain clears the hold AFTER attempting " +
                             "the replay, so a peer whose marketplace is still not live at that moment is left " +
                             "with neither the list nor a reason to ask again — the 2026-08-07 drop, one layer " +
                             "down, with the retry that was supposed to fix it doing the losing.";

            // ── (b) POSITIVE CONTROL: the hold really retires, both ways ──
            MarketplaceSync.ClearHeldOffers();
            if (MarketplaceSync.HasHeldOffers)
                yield return "L189 stale-hold-outlives-its-list: ClearHeldOffers does not clear, so a list " +
                             "that has already landed is replayed over a newer one at the next level start " +
                             "and the shop goes BACKWARDS.";
            MarketplaceSync.HoldOffers(9u, rows);
            MarketplaceSync.Reset();
            if (MarketplaceSync.HasHeldOffers)
                yield return "L189 hold-outlives-its-seq-stream: session teardown leaves a list held. Its seq " +
                             "belongs to the dead session's stream, so the next session's first genuine push " +
                             "can be judged older than a corpse and refused.";

            // ── (c) the seams: held on the drop, retired on the landing, replayed at the shop's birth ──
            if (!Program.Callees(inbound, ms.Assembly).Any(m => m.MetadataToken == hold.MetadataToken))
                yield return "L189 drop-path-holds-nothing: the inbound path no longer holds a list it failed " +
                             "to apply, so arm (a) is proved about a hold nothing ever fills.";
            if (!Program.Callees(apply, ms.Assembly).Any(m => m.MetadataToken == clear.MetadataToken))
                yield return "L189 landed-list-stays-held: a list that DID apply no longer retires the hold. " +
                             "The next level start then replays an older assortment over the current one.";
            var drainCalls = Program.Callees(drain, ms.Assembly).ToList();
            if (!drainCalls.Any(m => m.MetadataToken == apply.MetadataToken))
                yield return "L189 hold-never-drains: the retry no longer re-applies the held list. Holding " +
                             "without draining is the same silence with an extra field in it.";
            if (!drainCalls.Any(m => m.MetadataToken == mark.MetadataToken))
                yield return "L189 unapplied-list-marked-applied: the retry does not mark the seq on success, " +
                             "so the held list is replayed forever, or an out-of-order older push is allowed " +
                             "to win later — the guard SurfaceSeq exists to make.";
            if (!drainCalls.Any(m => m.MetadataToken == shouldApply.MetadataToken))
                yield return "L189 replay-skips-the-seq-guard: the retry replays without asking whether its " +
                             "held seq is still the newest. A list held across a battle can then land ON TOP " +
                             "of a newer one the peer took while it was held.";

            // ── (d) the drain is the moment the shop is BUILT, not some nearby method ──
            var patched = retry.GetCustomAttributes(typeof(HarmonyPatch), true).OfType<HarmonyPatch>()
                               .Select(a => a.info).ToList();
            if (!patched.Any(i => i.declaringType == typeof(GeoMarketplace) && i.methodName == "OnLevelStart"))
                yield return "L189 drain-is-not-the-moment: HeldOfferRetryPatch no longer targets " +
                             "GeoMarketplace.OnLevelStart. That method is where MarketplaceChoices is " +
                             "constructed at all — the first campaign load and the return from every battle — " +
                             "so it is the one frame after which a dropped list can actually be placed. A " +
                             "drain anywhere else either runs too early (no list to fill) or not at all.";
            if (!Program.Callees(onLevelStart, typeof(GeoMarketplace).Assembly)
                        .Any(m => m.Name == "set_MarketplaceChoices" || m.Name == "UpdateOptions"))
                yield return "L189 drain-target-no-longer-builds-the-shop: GeoMarketplace.OnLevelStart neither " +
                             "assigns MarketplaceChoices nor rolls it, so the premise that this is the moment " +
                             "a peer GAINS a live marketplace has stopped holding and the drain fires at a " +
                             "point where it will fail exactly as the original push did.";
        }
    }
}
