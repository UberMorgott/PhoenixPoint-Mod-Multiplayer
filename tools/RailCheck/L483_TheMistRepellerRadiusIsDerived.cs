using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Defs;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L483 — THE MIST-REPELLER RADIUS IS DERIVED, NOT MIRRORED, AND ITS INPUTS ARE.
    ///
    /// THE FAILURE (live 2026-08-14, client log). <c>HavenData.MistRepellerRange</c> was named as a
    /// <c>CRC divergence candidate</c> four times in ONE session. It is not a divergence: the radius is a
    /// per-frame ACCUMULATION each peer runs for itself —
    /// <c>Range.Range += RangeIncrease * time.Delta.TotalDays</c>, clamped to <c>MaxRange</c>
    /// (decompile <c>MistRepeller.ExpansMistRepeller</c>, MistRepeller.cs:104-122) on the PER-ACTOR clock,
    /// which accrues from local realtime on every peer (the <c>ActorInstanceData.TimingData</c> opt-out).
    /// Two peers sampling the same ramp a rail-hop apart can never hold the same float while it expands, so
    /// mirroring the RESULT produced a permanent false alarm — the shape that teaches people to ignore the
    /// CRC log, i.e. it costs the diagnostic that names REAL divergences.
    ///
    /// THE RULE. The radius is an explicit opt-out (<c>RailMeta._optOutMembers</c>) on both carriers, and it
    /// is only sound because its INPUTS ride: <c>RangeIncrease</c> is written from mirrored state
    /// (GeoHaven.cs:1171 off the mirrored <c>Zones</c>; GeoPhoenixBase.cs:401 off the mirrored
    /// <c>Layout</c>) and <c>MaxRange</c> is DEF-FIXED (GeoHavenDef/GeoPhoenixBaseDef
    /// <c>MaxMistRepellerRange</c>). The seed lands with the transferred save, which the game reads back
    /// itself (GeoHaven.cs:1369 / GeoPhoenixBase.cs:969). Same class as the derived pose (GeoVehicle
    /// SurfacePos/SurfaceRot/Rot). Nothing branches on byte equality: the one gameplay consumer,
    /// <c>GeoSite.IsInMistRepeller</c> -> GeoHavenDefenseMission.cs:103, runs where the mission is
    /// constructed (the host); every other reader is the mist RENDER, redrawn per frame from live values.
    ///
    /// THE ARMS:
    ///   (0) <c>premise-changed</c> — MistRepeller still exposes Range/RangeIncrease/MaxRange and the
    ///       accumulating <c>ExpansMistRepeller</c>, and both defs still carry <c>MaxMistRepellerRange</c>.
    ///   (a) <c>not-excluded</c> — both carriers classify <c>MistRepellerRange</c> Excluded with EXACTLY the
    ///       declared opt-out reason (an accidental exclusion for some other reason is a different fact).
    ///   (b) <c>input-not-mirrored</c> — the carriers of <c>RangeIncrease</c> DO ride: GeoHaven.Zones and
    ///       GeoPhoenixBase.Layout are not Excluded. Without them the client would accumulate at the wrong
    ///       RATE and this opt-out would hide a real divergence instead of a false alarm.
    ///   (c) <c>clamp-not-def-fixed</c> — <c>MaxMistRepellerRange</c> is a field on a <c>BaseDef</c>, i.e.
    ///       identical on every peer by construction, not campaign state that would have to mirror.
    ///   (d) <c>derivation-gone</c> — IL: <c>ExpansMistRepeller</c> still reads BOTH inputs. If the game
    ///       stopped re-deriving the radius, the client would freeze at its seed and the value would have to
    ///       ride again.
    ///
    /// Falsify: delete either opt-out row → (a); opt out GeoHaven.Zones / GeoPhoenixBase.Layout → (b);
    /// point the clamp at a non-def member → (c); rename the accumulator → (0)/(d).
    /// </summary>
    internal static class L483_TheMistRepellerRadiusIsDerived
    {
        internal static IEnumerable<string> Check()
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;

            var repeller = typeof(PhoenixPoint.Geoscape.Entities.MistRepeller);
            var expand = repeller.GetMethod("ExpansMistRepeller", Any);
            var rangeIncrease = repeller.GetProperty("RangeIncrease", Any);
            var maxRange = repeller.GetProperty("MaxRange", Any);
            var range = repeller.GetProperty("Range", Any);
            var havenMax = typeof(PhoenixPoint.Geoscape.Entities.GeoHavenDef).GetField("MaxMistRepellerRange", Any);
            var baseMax = typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBaseDef).GetField("MaxMistRepellerRange", Any);

            if (expand == null || rangeIncrease == null || maxRange == null || range == null ||
                havenMax == null || baseMax == null)
            {
                yield return "L483 premise-changed: MistRepeller no longer exposes Range/RangeIncrease/MaxRange/" +
                             "ExpansMistRepeller, or a def dropped MaxMistRepellerRange — the derivation this " +
                             "opt-out rests on cannot be read, so excluding the radius asserts nothing.";
                yield break;
            }

            // (a) + (b), both carriers.
            var rows = new (Type Live, Type Dto, string Input)[]
            {
                (typeof(PhoenixPoint.Geoscape.Entities.GeoHaven),
                 typeof(PhoenixPoint.Geoscape.Entities.GeoHaven.InstanceData), "Zones"),
                (typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase),
                 typeof(PhoenixPoint.Geoscape.Entities.Sites.GeoPhoenixBase.InstanceData), "Layout"),
            };

            int checkedRows = 0;
            foreach (var r in rows)
            {
                var table = RailType.GetBridged(r.Live, r.Dto);
                var f = table?.FieldByName("MistRepellerRange");
                if (f == null)
                {
                    yield return "L483 twin-row-gone: " + r.Live.Name + ".MistRepellerRange is not in the twin " +
                                 "table at all, so neither the exclusion nor its reason can be asserted.";
                    continue;
                }
                checkedRows++;

                var declared = RailMeta.OptOutReason(r.Live, "MistRepellerRange");
                if (declared == null || f.Class != FieldClass.Excluded || f.Exclude != declared)
                    yield return "L483 not-excluded: " + r.Live.Name + ".MistRepellerRange is '" + f.Class +
                                 "' with reason '" + (f.Exclude ?? "<none>") + "' — it must be the DECLARED " +
                                 "RailMeta._optOutMembers opt-out (" + (declared ?? "none declared") + "). " +
                                 "Mirroring a per-frame accumulation on the per-actor clock cannot converge " +
                                 "while it expands: that is the 2026-08-14 permanent CRC false alarm.";

                var input = table.FieldByName(r.Input);
                if (input == null || input.Class == FieldClass.Excluded)
                    yield return "L483 input-not-mirrored: " + r.Live.Name + "." + r.Input + " is " +
                                 (input == null ? "absent" : "Excluded (" + input.Exclude + ")") + ", but it is " +
                                 "what writes MistRepeller.RangeIncrease (GeoHaven.cs:1171 / GeoPhoenixBase.cs:401). " +
                                 "With the rate itself unmirrored the client accumulates a DIFFERENT radius and " +
                                 "the opt-out hides a real divergence instead of a false alarm.";
            }

            // (c) the clamp is def-fixed on both sides.
            foreach (var fi in new[] { havenMax, baseMax })
                if (!typeof(BaseDef).IsAssignableFrom(fi.DeclaringType))
                    yield return "L483 clamp-not-def-fixed: MaxMistRepellerRange now lives on " +
                                 fi.DeclaringType.Name + ", which is not a BaseDef. The upper bound would be " +
                                 "campaign state and would have to mirror for the two peers to converge.";

            // (d) the derivation still runs.
            if (!ReadsMember(expand, rangeIncrease.GetGetMethod(true)) ||
                !ReadsMember(expand, maxRange.GetGetMethod(true)))
                yield return "L483 derivation-gone: ExpansMistRepeller no longer reads BOTH RangeIncrease and " +
                             "MaxRange, so the client does not re-derive the radius from the mirrored rate and " +
                             "the def-fixed clamp. Excluding the value then freezes the client at its seed.";

            // Positive control: two carriers or this law examined nothing.
            if (checkedRows != rows.Length)
                yield return "L483 vacuous: only " + checkedRows + "/" + rows.Length + " mist-repeller carriers " +
                             "were classified — the arms above read an empty table.";
        }

        private static bool ReadsMember(MethodBase caller, MethodBase target)
        {
            if (caller == null || target == null) return false;
            byte[] il;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }
    }
}
