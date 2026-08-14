using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Entities.Statuses;
using Multiplayer.Network.Sync;
using PhoenixPoint.Common.Entities.Characters;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.View.ViewControllers;

namespace RailCheck
{
    /// <summary>
    /// L497 — A MIRRORED STAT RAISES THE GAME'S OWN STAT EVENT, SO THE CREW BARS REPAINT THEMSELVES.
    ///
    /// THE FAILURE (client, reported 2026-08-15). The strip under a flying aircraft draws one HEALTH and
    /// one STAMINA slider per soldier aboard. On the peer that was not driving, both sat frozen at the
    /// values they carried when the strip was built, for the whole flight.
    ///
    /// IT WAS NOT A DATA GAP. <c>GeoCharacter._health</c> and <c>_fatigue._stamina</c> are covered members
    /// of the rail (docs/rail-baseline.txt), and the client's own log carries "UiEventMap: StatusStat
    /// rides the universal open-screen repaint" — that is the stat traffic arriving. The numbers were
    /// correct in the model and only the paint was old.
    ///
    /// IT WAS NOT A MISSING REPAINT ARM EITHER, and that is why the fix is not one. The strip is already
    /// fully self-refreshing: <c>AircraftCrewController.SetCrew</c>:90-95 subscribes to the crew's
    /// <c>Health</c> / <c>Corruption</c> / <c>Fatigue.Stamina</c>, the handler :214-217 sets
    /// <c>CrewBarsNeedRefresh</c>, and its own <c>Update</c>:235-242 re-runs <c>RefreshCrewBars</c>:172-203.
    /// It never fired because <c>BaseStat.Value</c> is a plain FIELD (BaseStat.cs:21): the rail's write
    /// lands it directly and skips <c>BaseStat.Set</c>:95 → <c>OnStatChange</c>:111 → the event at :50.
    /// So the rule this law fixes in place is GENERIC and one line deep — a mirrored stat write must
    /// look, to the game, exactly like a native one:
    ///
    ///     <c>RailField.SetValue</c> echoes <c>StatChangeType.Value</c> whenever it moves a BaseStat.
    ///
    /// That is the geoscape twin of <c>TacticalUiRepaint.SquadBarStatPatch</c> ("THE SEAM IS THE STAT
    /// ITSELF"), and it covers every other widget wired the same way — the corruption report
    /// (<c>UIModuleCorruptionReport</c>:181), the roster (<c>UIStateGeoRoster</c>:343-344), edit-soldier
    /// (<c>UIStateEditSoldier</c>:340), the geoscape log (<c>GeoscapeLog</c>:612-615) — with no per-panel
    /// repaint and no screen re-enter, which a stat stream must never trigger (law L63).
    ///
    /// THE ARMS:
    ///   (a) <c>raiser-unresolved</c> — the open delegate over <c>BaseStat.OnStatChange</c> must exist. A
    ///       null one is a SILENT no-echo: every arm below still describes the fix, and nothing repaints.
    ///   (b) <c>mirrored-write-silent</c> — the shipped <c>RailField.SetValue</c> for
    ///       <c>StatusStat.Value</c>, driven on a real stat, must raise the event with the true previous
    ///       and new numbers. This is the bug, reproduced through the production seam.
    ///   (c) <c>unchanged-write-echoes</c> — the SAME value written again must raise nothing. The rail
    ///       re-lands values at batch rate; an unconditional echo would spam every subscriber in the game
    ///       (and the geoscape log would re-announce an exhausted soldier on every tick).
    ///   (d) <c>bar-source-unmirrored-*</c> — the three members the bars are computed from must still ride:
    ///       <c>GeoCharacter._health</c>, <c>GeoCharacter._fatigue</c>, <c>CharacterFatigue._stamina</c>,
    ///       and <c>StatusStat.Value</c> as a LEAF (the leaf class is what routes it through SetValue at
    ///       all — a Descend would land the numbers by another door and this whole echo would be dead).
    ///   (e) <c>strip-not-event-driven</c> / <c>strip-reads-*</c> — IL, the premise: <c>SetCrew</c> must
    ///       still subscribe to <c>BaseStat.StatChangeEvent</c>, and <c>RefreshCrewBars</c> must still
    ///       read <c>StatusStat.Ratio</c> (health) and <c>CharacterFatigue.Ratio</c> (stamina). If the
    ///       game ever repaints that strip some other way, arms (a)-(c) would stay green while guarding
    ///       nothing.
    ///
    /// Falsify: delete the <c>EchoStatChange</c> call in <c>RailField.SetValue</c> → (b); drop its
    /// equality guard → (c); exclude <c>_fatigue</c> from the rail → (d).
    /// </summary>
    internal static class L497_TheCrewBarsRepaintOnAMirroredStat
    {
        internal static IEnumerable<string> Check()
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance |
                                     BindingFlags.NonPublic | BindingFlags.Public;

            // ── (a) the raiser ───────────────────────────────────────────────────────────────────────
            var raiser = typeof(RailField).GetField("_raiseStatChange", Any);
            if (raiser == null)
            {
                yield return "L497 premise-changed: RailField._raiseStatChange is gone, so the mirrored-write " +
                             "echo this law guards cannot be found. Re-point the law before believing any " +
                             "verdict below.";
                yield break;
            }
            if (raiser.GetValue(null) == null)
                yield return "L497 raiser-unresolved: RailField could not bind BaseStat.OnStatChange, so a " +
                             "mirrored stat write raises nothing at all. Every stat-driven widget in the game " +
                             "— starting with the aircraft crew strip's health and stamina bars — is frozen on " +
                             "a non-authoritative peer, and NOTHING says so at runtime.";

            // ── (d) the bars' source members still ride ──────────────────────────────────────────────
            foreach (var source in new[]
            {
                Src(typeof(GeoCharacter), "_health", FieldClass.Descend, "the HEALTH bar"),
                Src(typeof(GeoCharacter), "_fatigue", FieldClass.Descend, "the STAMINA bar"),
                Src(typeof(CharacterFatigue), "_stamina", FieldClass.Descend, "the STAMINA bar"),
                Src(typeof(StatusStat), "Value", FieldClass.Leaf, "both bars"),
            })
            {
                var f = RailType.Get(source.Owner)?.FieldByName(source.Member);
                if (f == null || f.Class != source.Want)
                    yield return "L497 bar-source-unmirrored-" + source.Owner.Name + "." + source.Member + ": " +
                                 (f == null ? "absent from the rail table"
                                            : f.Class + (f.Class == FieldClass.Excluded ? " (" + f.Exclude + ")" : "")) +
                                 ", not " + source.Want + ". " + source.What + " under a flying aircraft then has " +
                                 "no number to paint on a non-authoritative peer, and no echo can invent one.";
            }

            // ── (b)+(c) the shipped seam, driven ─────────────────────────────────────────────────────
            var valueField = RailType.Get(typeof(StatusStat))?.FieldByName("Value");
            if (valueField == null)
            {
                yield return "L497 premise-changed: StatusStat.Value has no rail field, so the write seam cannot " +
                             "be driven here.";
                yield break;
            }

            var stat = new StatusStat { Name = "Health" };
            stat.SetMin(0f, triggerStatChangeEvent: false);
            stat.SetMax(100f, triggerStatChangeEvent: false);
            stat.Set(50f, triggerStatChangeEvent: false);

            int fired = 0;
            float sawPrev = float.NaN, sawNow = float.NaN;
            BaseStat.StatChangeHandler watch = (s, change, prev, unclamped) =>
            {
                if (change != StatChangeType.Value) return;
                fired++;
                sawPrev = prev;
                sawNow = unclamped;
            };
            stat.StatChangeEvent += watch;
            try
            {
                valueField.SetValue(stat, new ModifiableValue { BaseValue = 20f });
                if (fired == 0)
                    yield return "L497 mirrored-write-silent: the rail wrote StatusStat.Value and the game's own " +
                                 "StatChangeEvent never fired. That is the reported defect verbatim — the crew " +
                                 "strip's health and stamina sliders repaint ONLY from that event " +
                                 "(AircraftCrewController:90-95 -> :216 -> Update:235-242), so on the peer that " +
                                 "is not driving the aircraft they stay frozen at the values the strip was built " +
                                 "with, for the whole flight, with correct numbers sitting in the model.";
                else if (fired != 1 || sawPrev != 50f || sawNow != 20f)
                    yield return "L497 mirrored-write-mislabelled: the echo fired " + fired + " time(s) carrying " +
                                 "prev=" + sawPrev + " new=" + sawNow + ", expected exactly one (50 -> 20). " +
                                 "Subscribers branch on those two numbers (GeoscapeLog:619-645 announces an " +
                                 "exhausted or rested soldier from them), so a wrong pair is a wrong log entry " +
                                 "and a wrong bar.";

                fired = 0;
                valueField.SetValue(stat, new ModifiableValue { BaseValue = 20f });
                if (fired != 0)
                    yield return "L497 unchanged-write-echoes: re-landing the SAME value raised the event again. " +
                                 "The rail re-writes members at batch rate, so this puts every StatChangeEvent " +
                                 "subscriber in the game on that same rate — the geoscape log would re-announce " +
                                 "an exhausted soldier every tick, and the crew strip would rebuild its bars " +
                                 "every frame for nothing.";
            }
            finally { stat.StatChangeEvent -= watch; }

            // ── (e) the strip is still event-driven, and still reads those stats ─────────────────────
            var setCrew = typeof(AircraftCrewController).GetMethod("SetCrew", Any);
            var refresh = typeof(AircraftCrewController).GetMethod("RefreshCrewBars", Any);
            var subscribe = typeof(BaseStat).GetEvent("StatChangeEvent")?.GetAddMethod(nonPublic: true);
            var healthRatio = typeof(StatusStat).GetProperty("Ratio")?.GetGetMethod(nonPublic: true);
            var staminaRatio = typeof(CharacterFatigue).GetProperty("Ratio")?.GetGetMethod(nonPublic: true);
            if (setCrew == null || refresh == null || subscribe == null ||
                healthRatio == null || staminaRatio == null)
            {
                yield return "L497 premise-changed: AircraftCrewController.SetCrew / .RefreshCrewBars, " +
                             "BaseStat.StatChangeEvent or one of the two Ratio getters did not resolve. The crew " +
                             "strip this law guards is not the one the game ships any more.";
                yield break;
            }
            if (!References(setCrew, subscribe))
                yield return "L497 strip-not-event-driven: AircraftCrewController.SetCrew no longer subscribes to " +
                             "BaseStat.StatChangeEvent, so the echo above repaints nothing and the arms of this " +
                             "law guard a path the game abandoned. Find what repaints the strip now and re-point " +
                             "the reactivity claim at it.";
            if (!References(refresh, healthRatio))
                yield return "L497 strip-reads-health-elsewhere: RefreshCrewBars no longer reads StatusStat.Ratio, " +
                             "so the HEALTH bar is fed by something this law never checks reaches the client.";
            if (!References(refresh, staminaRatio))
                yield return "L497 strip-reads-stamina-elsewhere: RefreshCrewBars no longer reads " +
                             "CharacterFatigue.Ratio, so the STAMINA bar is fed by something this law never " +
                             "checks reaches the client.";
        }

        private struct BarSource
        {
            internal Type Owner;
            internal string Member;
            internal FieldClass Want;
            internal string What;
        }

        private static BarSource Src(Type owner, string member, FieldClass want, string what) =>
            new BarSource { Owner = owner, Member = member, Want = want, What = what };

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Same cross-assembly
        /// token resolve L492 uses — every callee here lives in the GAME assembly, so a raw token compare
        /// alone would never match.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                int token = BitConverter.ToInt32(il, i);
                if (token == callee.MetadataToken && m.Module == callee.Module) return true;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(token); } catch { }
                if (resolved != null && resolved.MetadataToken == callee.MetadataToken &&
                    resolved.Module == callee.Module) return true;
            }
            return false;
        }
    }
}
