using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L500 — A STRUCTURAL CREATE WIRES ONLY AFTER ITS VALUES LAND.
    ///
    /// THE FAILURE (2026-08-14 session, 6x on the client: S#80, S#85, S#84, …). A descend create
    /// (<c>S#&lt;id&gt;.SerializationData.ActiveMission</c>) constructs the mission and handed it STRAIGHT to
    /// the owner's native load wiring in the same packet — while every classified member of that mission
    /// still rode the NEXT batch. <c>GeoSite.RegisterMission</c> raises <c>SiteMissionStarted</c>
    /// synchronously, <c>GeoscapeFactionObjectiveSystem.OnSiteMissionStarted:293</c> builds the haven-defence
    /// objective, and its <c>mission.MissionDef.Description</c> (:188) NREs because <c>MissionDef</c> is an
    /// ordinary <c>[SerializeMember]</c> leaf (GeoMission.cs:204) that had not landed. The throw unwound out
    /// of <c>ApplyStructural</c>, which — correctly — leaves the seq UNMARKED, so one missing objective
    /// became a seq gap (<c>98→100</c>), a FULL resend, and the CRC-divergence/resend storm behind it. The
    /// game's own load path cannot hit this: it fills the DTO first and registers after
    /// (<c>GeoSite.ProcessInstanceData:1621-1631</c>). The mirror was doing it in the opposite order.
    ///
    /// THE RULE. A structural create may ASSIGN in its own packet, but any native wiring it replays is
    /// PARKED and run from the value-batch rung — retried while it still throws, dropped with a line when
    /// its field no longer holds what the create made. A create must never poison the subtree: the seq path
    /// stays clean whatever a native subscriber does.
    ///
    /// THE ARMS:
    ///   (a) <c>decision</c> — <c>GenericApplier.WireDecision</c> is DRIVEN, both ways: Wait before the
    ///       values land, Wire after, Drop when the field was replaced or the retries are spent.
    ///   (b) <c>premise-changed</c> (executable guard, anti-vacuity) — the decision function, the retry
    ///       bound and the park/flush pair must exist, and <c>ApplyDescendCreate</c> must actually reach
    ///       <c>ParkNativeWiring</c>; otherwise the arms below prove nothing about the real path.
    ///   (c) <c>inline-invoke</c> — <c>ApplyDescendCreate</c>'s own IL must NOT call
    ///       <c>MethodBase.Invoke</c>. That reflected call IS the regression: it is how the native wiring
    ///       ran inside the create packet, and it is the only frame between the create and the NRE.
    ///   (d) <c>flush-rung</c> — <c>FlushPendingWire</c> must be reached from <c>ApplyDelta</c> (the batch
    ///       that carries the values) and must NOT be reached from <c>ApplyStructural</c> (which would run
    ///       it in the create's own packet, i.e. the bug with extra steps).
    ///
    /// Falsify: put <c>SiteRegisterMission.Invoke(...)</c> back inline in the create → (c); move the flush
    /// call into <c>ApplyStructural</c> → (d); make <c>WireDecision</c> return Wire at parked==applied → (a).
    /// </summary>
    internal static class L500_AStructuralCreateWiresOnlyAfterItsValuesLand
    {
        internal static IEnumerable<string> Check()
        {
            const BindingFlags Priv = BindingFlags.Static | BindingFlags.NonPublic;
            var applier = typeof(GenericApplier);
            var create = applier.GetMethod("ApplyDescendCreate", Priv);
            var park = applier.GetMethod("ParkNativeWiring", Priv);
            var flush = applier.GetMethod("FlushPendingWire", Priv);
            var delta = applier.GetMethod("ApplyDelta", Priv);
            var structural = applier.GetMethod("ApplyStructural", Priv);

            // ── arm (b): the executable guard.
            if (create == null || park == null || flush == null || delta == null || structural == null)
            {
                yield return "L500 premise-changed: GenericApplier no longer has the create/park/flush/apply " +
                             "quintet (ApplyDescendCreate=" + (create != null) + " ParkNativeWiring=" + (park != null) +
                             " FlushPendingWire=" + (flush != null) + " ApplyDelta=" + (delta != null) +
                             " ApplyStructural=" + (structural != null) + "). The deferral was renamed or " +
                             "removed; re-point this law before believing a create still waits for its values.";
                yield break;
            }
            if (GenericApplier.WireMaxTries < 1)
            {
                yield return "L500 premise-changed: WireMaxTries is " + GenericApplier.WireMaxTries +
                             " — a parked wire can never run, so every deferred native registration is lost.";
                yield break;
            }
            if (!References(create, park))
            {
                yield return "L500 premise-changed: ApplyDescendCreate does not reach ParkNativeWiring at all. " +
                             "Nothing is deferred any more, so arms (a)/(c)/(d) describe a path the applier " +
                             "no longer takes.";
                yield break;
            }

            // ── arm (a): the decision, driven both ways against the real function.
            foreach (var bad in Drive()) yield return bad;

            // ── arm (c): the create packet must not invoke native wiring itself.
            var invoke = typeof(MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) });
            if (invoke != null && References(create, invoke))
                yield return "L500 inline-invoke: ApplyDescendCreate calls MethodBase.Invoke in its own IL. " +
                             "That is the native wiring running inside the create packet, on an object whose " +
                             "leaves ride the NEXT batch — the exact frame that NRE'd in " +
                             "GeoscapeFactionObjectiveSystem.CreateHavenDefenseMissionObjective and left the " +
                             "seq unmarked. Park the call instead.";

            // ── arm (d): the flush hangs off the value batch, and only off it.
            if (!References(delta, flush))
                yield return "L500 flush-rung: ApplyDelta does not reach FlushPendingWire. Parked wiring then " +
                             "never runs on the batch that filled the created object — the mission is assigned " +
                             "but its owner's native registration (and the objective it raises) never happens.";
            if (References(structural, flush))
                yield return "L500 flush-rung: ApplyStructural reaches FlushPendingWire. A structural packet is " +
                             "the create's OWN packet: flushing there runs the native wiring against an object " +
                             "whose values have not landed, which is the failure this law exists for.";
        }

        /// <summary>Every outcome of the real decision function, including the exact create-time state the old
        /// inline invoke used to call into (parked == applied).</summary>
        private static IEnumerable<string> Drive()
        {
            foreach (var c in new[]
            {
                (Name: "create-time", Assigned: true, Parked: 42u, Applied: 42u, Tries: 0,
                 Want: GenericApplier.WireVerdict.Wait,
                 Why: "the values ride a LATER batch; wiring here dereferences leaves that have not landed"),
                (Name: "values-landed", Assigned: true, Parked: 42u, Applied: 43u, Tries: 0,
                 Want: GenericApplier.WireVerdict.Wire,
                 Why: "a batch past the create has applied, so the object is filled and the native load wiring can run"),
                (Name: "field-replaced", Assigned: false, Parked: 42u, Applied: 43u, Tries: 0,
                 Want: GenericApplier.WireVerdict.Drop,
                 Why: "the field no longer holds what the create made (destroy or replace) — there is nothing to wire onto"),
                (Name: "retries-spent", Assigned: true, Parked: 42u, Applied: 99u, Tries: GenericApplier.WireMaxTries,
                 Want: GenericApplier.WireVerdict.Drop,
                 Why: "a wire that keeps throwing must be reported and released, never retried forever"),
            })
            {
                var got = GenericApplier.WireDecision(c.Assigned, c.Parked, c.Applied, c.Tries);
                if (got != c.Want)
                    yield return "L500 decision/" + c.Name + ": WireDecision(assigned=" + c.Assigned +
                                 ", parked=" + c.Parked + ", applied=" + c.Applied + ", tries=" + c.Tries +
                                 ") = " + got + ", expected " + c.Want + " — " + c.Why + ".";
            }
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Same-assembly callees
        /// match on the raw token; a cross-assembly one (MethodBase.Invoke) is a MemberRef, so each candidate
        /// word is also resolved through the calling module — the shape L492.References uses.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { }
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
