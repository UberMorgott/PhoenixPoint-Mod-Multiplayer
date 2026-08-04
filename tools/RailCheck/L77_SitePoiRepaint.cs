using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Levels;
using PhoenixPoint.Geoscape.View;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L77 — THE SITE/POI SEAM ON A CLIENT: ONE CLOCK AND ONE NOTIFICATION. Both halves of the
    /// 2026-08-04 report ("the Explore button is missing until the POI is clicked a second time" and
    /// "the exploration progress bar renders only on the host") are the client failing to replay a
    /// derivation the host performed, and each half failed SILENTLY in its own way.
    ///
    /// THE CLOCK. Every actor owns a private clock — <c>ActorComponent.Timing = new Timing()</c>:85,
    /// parented to the level's :89 — and <c>Timing.Now</c> is <c>StartTime + OwnNow</c>
    /// (Base.Core/Timing.cs:55), i.e. a PER-ACTOR epoch. <c>GeoVehicle.StartedExplorationAt</c> is written
    /// from it (:423) and read back by <c>ExploreCurrentSite</c>:449 and <c>SetProgression</c>:456. The
    /// re-seed asked <c>GeoLevel.Timing</c> instead, so its `now &lt; end` test compared ~36 days of actor
    /// life against the campaign's absolute datetime and was false for EVERY vehicle, forever — the client
    /// log's own words: `mirrored start 36.08:16:30 … the clock (747322.13:10:33) is already past`. Nothing
    /// crashed, nothing was unmirrored; the guard simply never opened. THAT is why this arm is by NAME:
    /// two properties spelled `Timing` on two types, and only one of them is the actor's.
    ///
    /// THE NOTIFICATION. <c>VehicleArrivalGate</c> skips <c>GeoVehicle.OnArrived</c> WHOLE on a client — it
    /// must, the body is the authoritative arrival — but its last two lines are the PRESENTATION half
    /// (:347/:348), and their chain ends at <c>UIStateVehicleSelected.OnVehicleArrived</c>:1180-1192, which
    /// is what OPENS the site menu carrying "Explore (Xh)". Lose it and the client player is left on the
    /// native click path, which needs TWO clicks by construction (<c>ShowBaseInfoCrt</c> `yield break`s on
    /// the first click at a site that was not already hovered). So the re-seed raises
    /// <c>GeoscapeView.FactionVehicleArrived</c> itself — one link BELOW <c>GeoFaction.OnVehicleArrived</c>,
    /// whose body mints authoritative state (SetInspected / UpdateVehicleSite / Refill /
    /// EngageEnemyAircraftOnSite) a projector may not write (law 3). This law pins that boundary in both
    /// directions: the notification must fire, and it must never be "fixed" by calling the gameplay entry.
    ///
    /// Falsify: put <c>v.GeoLevel.Timing</c> back → <c>clock-is-the-levels</c> (+ <c>actor-clock-unused</c>);
    /// delete the <c>RaiseArrivedForUi</c> call → <c>arrival-unannounced</c>; raise
    /// <c>GeoFaction.OnVehicleArrived</c> / <c>GeoSite.VehicleArrived</c> instead →
    /// <c>arrival-mints-outcome</c>; drop the arrival gate → <c>arrival-double-raised</c>.
    /// </summary>
    internal static class L77_SitePoiRepaint
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var applier = typeof(GenericApplier);
            var mod = applier.Assembly;

            var actorClock = typeof(Base.Entities.ActorComponent).GetProperty("Timing", All)?.GetGetMethod(true);
            var levelClock = typeof(GeoLevelController).GetProperty("Timing", All)?.GetGetMethod(true);
            var reseedExpl = applier.GetMethod("ReseedExploration", All);
            var reseedNav = applier.GetMethod("ReseedNavigation", All);
            var raise = applier.GetMethod("RaiseArrivedForUi", All);

            // ─── (a) POSITIVE CONTROL — the two clocks must be tellable apart, or every arm below is noise ───
            if (actorClock == null || levelClock == null)
            {
                yield return "L77 clock-premise-gone: ActorComponent.Timing / GeoLevelController.Timing no longer " +
                             "resolve as properties, so 'which clock the exploration re-seed reads' cannot be " +
                             "checked at all — and reading the wrong one is INVISIBLE at runtime (a guard that " +
                             "never opens, no exception, no log line)";
                yield break;
            }
            if (actorClock.MetadataToken == levelClock.MetadataToken)
                yield return "L77 clock-premise-collapsed: the actor clock and the level clock resolve to the SAME " +
                             "getter — the distinction this whole law is about no longer exists in the game, so the " +
                             "arms below pass vacuously and the epoch question needs re-deriving from scratch";

            // ─── (b) THE GAME still stamps the exploration start from the ACTOR clock ───
            var startExploring = typeof(GeoVehicle).GetMethod("StartExploringCurrentSite", All);
            var exploreCurrent = typeof(GeoVehicle).GetMethod("ExploreCurrentSite", All, null,
                                                              new[] { typeof(Base.Core.TimeUnit), typeof(Base.Core.TimeUnit) }, null);
            if (startExploring == null || exploreCurrent == null)
                yield return "L77 premise-changed: GeoVehicle.StartExploringCurrentSite / " +
                             "ExploreCurrentSite(TimeUnit,TimeUnit) no longer resolve — the re-seed drives them by " +
                             "reflection and the epoch of the value it mirrors is now unknown";
            else
            {
                if (!Calls(startExploring, actorClock))
                    yield return "L77 premise-changed: StartExploringCurrentSite no longer stamps StartedExplorationAt " +
                                 "from the ACTOR's own Timing. The mirrored StartExplorationTime leaf changed epoch, " +
                                 "so the re-seed now compares it against the wrong clock again — silently, exactly " +
                                 "as before";
                if (Calls(exploreCurrent, levelClock))
                    yield return "L77 premise-changed: ExploreCurrentSite now reads the LEVEL clock. Its `end - now` " +
                                 "schedule and the re-seed's `now < end` guard must ask the same clock or the timer " +
                                 "is armed for an absurd interval";
            }

            // ─── (c) THE RE-SEED asks the actor's clock, and never the level's ───
            if (reseedExpl == null)
                yield return "L77 reseed-gone: GenericApplier.ReseedExploration no longer exists — nothing replays " +
                             "the host's exploration order on a client and the progress bar is host-only again";
            else
            {
                if (Calls(reseedExpl, levelClock))
                    yield return "L77 clock-is-the-levels: ReseedExploration reads GeoLevelController.Timing. That is " +
                                 "the campaign's absolute datetime; the mirrored StartExplorationTime is the ACTOR's " +
                                 "own elapsed time, so `timing.Now < end` is false for every vehicle forever and NO " +
                                 "client ever starts the game's own exploration timer — the bar simply never appears";
                if (!Calls(reseedExpl, actorClock))
                    yield return "L77 actor-clock-unused: ReseedExploration no longer reads ActorComponent.Timing — " +
                                 "whatever clock it now uses is not the one StartedExplorationAt was written from";
            }

            // ─── (d) THE ARRIVAL NOTIFICATION is wired, and it is reached ───
            var evt = typeof(GeoscapeView).GetField("FactionVehicleArrived", All);
            if (evt == null || evt.FieldType != typeof(Action<GeoVehicle, bool>))
                yield return "L77 arrival-event-gone: GeoscapeView.FactionVehicleArrived is not a field-like event of " +
                             "type Action<GeoVehicle,bool> (" + (evt == null ? "no such field" : evt.FieldType.Name) +
                             "). The client's arrival notification is fetched by that exact name and would resolve to " +
                             "null — a silent no-op, and the site menu stops opening with no log line";
            if (raise == null)
                yield return "L77 arrival-unannounced: GenericApplier.RaiseArrivedForUi does not exist. A client's " +
                             "arrival is gated (VehicleArrivalGate skips GeoVehicle.OnArrived whole), so NOTHING " +
                             "tells the open geoscape screen an aircraft landed: no UpdateVehicleActions, no " +
                             "UpdateReachableSitesMarkers, and no auto-opened site menu — the Explore button appears " +
                             "only after the player clicks the POI TWICE (ShowBaseInfoCrt yield-breaks on click one)";
            else if (reseedNav == null || !Calls(reseedNav, raise))
                yield return "L77 arrival-unannounced: ReseedNavigation no longer calls RaiseArrivedForUi, so the " +
                             "notification is dead code — the arrival edge is the ONLY moment a client can announce " +
                             "one, since its own OnArrived is refused by VehicleArrivalGate";

            // ─── (e) IT IS THE PRESENTATION BOUNDARY, not the gameplay one (law 3) ───
            var factionArrived = typeof(GeoFaction).GetMethod("OnVehicleArrived", All);
            var siteArrived = typeof(GeoSite).GetMethod("VehicleArrived", All);
            var setInspected = typeof(GeoSite).GetMethod("SetInspected", All);
            if (factionArrived == null || setInspected == null)
                yield return "L77 premise-changed: GeoFaction.OnVehicleArrived / GeoSite.SetInspected no longer " +
                             "resolve — what makes the gameplay entry unsafe for a projector is now unproven, so the " +
                             "ban below guards a guess";
            else if (!Calls(factionArrived, setInspected))
                yield return "L77 premise-changed: GeoFaction.OnVehicleArrived no longer reaches SetInspected. The " +
                             "reason the client enters one link LOWER (at GeoscapeView) was that this method mints " +
                             "authoritative state; if it no longer does, re-derive the boundary rather than keeping " +
                             "a rule whose reason has evaporated";
            foreach (var banned in new[] { factionArrived, siteArrived })
            {
                if (banned == null) continue;
                foreach (var m in new[] { raise, reseedNav, reseedExpl })
                    if (m != null && Calls(m, banned))
                        yield return "L77 arrival-mints-outcome: " + m.Name + " calls " +
                                     banned.DeclaringType.Name + "." + banned.Name + ". That is the GAMEPLAY arrival " +
                                     "— SetInspected / UpdateVehicleSite / Refill / EngageEnemyAircraftOnSite — and a " +
                                     "projector writing it produces authoritative state the diff can never correct " +
                                     "(the diff is host-now vs host-before). Raise GeoscapeView.FactionVehicleArrived " +
                                     "instead: UI and audio only";

            }

            // ─── (f) THE RAISER we chose really is presentation-only ───
            var viewRaiser = typeof(GeoscapeView).GetMethod("OnFactionVehicleArrived", All);
            if (viewRaiser == null)
                yield return "L77 premise-changed: GeoscapeView.OnFactionVehicleArrived is gone — the event we raise " +
                             "may no longer be the game's own presentation boundary";
            else if (Callees(viewRaiser, game).Any())
                yield return "L77 raiser-not-inert: GeoscapeView.OnFactionVehicleArrived now calls into the game " +
                             "instead of only invoking its event. Raising the event directly would then SKIP whatever " +
                             "it added, so the client's arrival is no longer equivalent to the host's";

            // ─── (g) THE NATIVE ARRIVAL is still gated — otherwise we announce it TWICE ───
            var gate = mod.GetType("Multiplayer.Network.Sync.VehicleArrivalGate");
            var targetMethod = gate?.GetMethod("TargetMethod", All);
            object gated = null;
            try { gated = targetMethod?.Invoke(null, null); } catch { }
            var onArrived = typeof(GeoVehicle).GetMethod("OnArrived", All, null,
                                                         new[] { typeof(UnityEngine.Vector3), typeof(bool) }, null);
            if (gate == null || targetMethod == null || onArrived == null ||
                !(gated is MethodBase mb) || mb.MetadataToken != onArrived.MetadataToken)
                yield return "L77 arrival-double-raised: VehicleArrivalGate no longer resolves GeoVehicle.OnArrived" +
                             "(Vector3,bool) as its target (" + (gate == null ? "gate gone"
                                 : targetMethod == null ? "TargetMethod gone"
                                 : gated == null ? "resolved NULL" : "resolved " + gated) +
                             "). The client's own arrival would then run natively AND the re-seed would announce it, " +
                             "so the site menu opens twice and GeoFaction.OnVehicleArrived mints outcome on a projector";

            // ─── (h) THE HANDLER we are aiming at still opens the menu ───
            var uiArrived = typeof(UIStateVehicleSelected).GetMethod("OnVehicleArrived", All, null,
                                                                     new[] { typeof(GeoVehicle), typeof(bool) }, null);
            var showMenu = typeof(UIStateVehicleSelected).GetMethod("ShowContextualMenu", All);
            if (uiArrived == null || showMenu == null)
                yield return "L77 premise-changed: UIStateVehicleSelected.OnVehicleArrived(GeoVehicle,bool) / " +
                             "ShowContextualMenu no longer resolve — the screen behaviour this seam exists to trigger " +
                             "is now unknown";
            else if (!Calls(uiArrived, showMenu))
                yield return "L77 premise-changed: UIStateVehicleSelected.OnVehicleArrived no longer opens the site " +
                             "contextual menu. Raising FactionVehicleArrived then does NOT put the Explore button on " +
                             "a client's screen, and the reported symptom needs a different seam";
        }

        /// <summary>Does <paramref name="m"/> reference <paramref name="callee"/> in its IL? Same operand-size
        /// walk Program.Callees uses; anything unparseable ABANDONS the method rather than guessing, so this
        /// under-reports (a missed edge) and never invents one (a false red).</summary>
        private static bool Calls(MethodBase m, MethodBase callee) =>
            callee != null && Callees(m, callee.Module.Assembly)
                .Any(c => c.MetadataToken == callee.MetadataToken && c.Module == callee.Module);

        private static readonly Dictionary<short, OpCode> OpCodeByValue =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                           .Where(f => f.FieldType == typeof(OpCode))
                           .Select(f => (OpCode)f.GetValue(null))
                           .GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

        private static IEnumerable<MethodBase> Callees(MethodBase m, Assembly asm)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null) yield break;
            var typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType
                ? m.DeclaringType.GetGenericArguments() : null;
            var methodArgs = m.IsGenericMethodDefinition ? m.GetGenericArguments() : null;
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i++];
                if (code == 0xFE)
                {
                    if (i >= il.Length) yield break;
                    code = (short)(0xFE00 | il[i++]);
                }
                if (!OpCodeByValue.TryGetValue(code, out var op)) yield break;
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0 || i + size > il.Length) yield break;
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase callee = null;
                    try { callee = m.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (callee != null && callee.Module.Assembly == asm) yield return callee;
                }
                i += size;
            }
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return -1;
            }
        }
    }
}
