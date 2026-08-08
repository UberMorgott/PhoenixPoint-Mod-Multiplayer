using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L348 — A BATCH THIS PEER COULD NOT APPLY IS ASKED FOR AGAIN, BY THE PEER ITSELF.
    ///
    /// THE OUTCOME, two halves and neither is a call count. (a) A host batch dropped because this peer had
    /// no geoscape level leaves a RECORD behind — the drop is knowable afterwards. (b) The level going live
    /// turns that record into a <c>MsgResyncRequest</c> WITHOUT an inbound message being needed to notice.
    ///
    /// THE DEFECT. <c>GenericApplier.ApplyDelta</c> and <c>ApplyStructural</c> both opened with
    /// <c>var geo = GeoLevel(); if (geo == null) return;</c> — a silent swallow with no line, no counter and
    /// no flag. It is worse than a normal silent drop because <c>_lastSeq</c> is marked only AFTER that
    /// guard, so the loss moved nothing at all and stayed invisible until an unrelated later batch tripped
    /// the gap check and named a range nobody could account for. MEASURED, identically on both mission
    /// returns of the 2026-08-08 session: the host was back on the geoscape 50-77 s before the client
    /// finished loading, and every batch of that window died there — <c>seq gap (200→249)</c> = 49 batches,
    /// then <c>seq gap (579→629)</c> = 50. From the host's side the same event reads as 13 CRC DIVERGED for
    /// that client, all clustered right after the first tac→geo return, healed only by re-emit.
    ///
    /// WHY IT NEVER HEALED ITSELF. Every other <c>RequestResync</c> call site is REACTIVE — a seq gap, an
    /// unknown kind def, a torn batch, a CRC report — and each of them needs an inbound message to notice
    /// anything. The peer's whole problem here is that it received nothing it was able to keep. The level
    /// becoming live is the one edge in this system that is NOT a message, which is why arm (b) is about a
    /// TICK and not about a handler: a resync trigger that lives in <c>HandleInbound</c> satisfies nothing.
    ///
    /// ARMS
    ///   (a) <c>discard-leaves-no-record</c> — THE ONE THAT MATTERS. Both <c>ApplyDelta</c> and
    ///       <c>ApplyStructural</c> must reach a write of <c>_missedNoLevel</c>, directly or through one
    ///       call in the same class. Written as a FIELD write, not as a call to a named helper, so
    ///       inlining the flag or renaming the helper does not turn the law red for nothing.
    ///   (b) <c>nothing-asks-for-it-back</c> — <c>ClientMissedBatchTick</c> must READ the flag and reach
    ///       <c>RequestResync</c>, and <c>SyncEngine.Tick</c> must reach the tick. The second half is what
    ///       makes the trigger non-reactive; without it the record is a log line and nothing more.
    ///   (c) <c>control-not-red</c> — POSITIVE CONTROL. A seam that returns on a null level and records
    ///       nothing must raise (a). A must-reach arm whose IL walk resolves no call edges reports every
    ///       gate as present, which is how the ping laws stayed green through a real leak.
    ///
    /// Falsify (RUN 2026-08-09, verified RED against a real build and restored):
    ///   • VERIFIED RED — delete the <c>MissedNoLevel()</c> call at <c>GenericApplier.ApplyDelta</c>, i.e.
    ///     restore the bare <c>if (geo == null) return;</c> → <c>discard-leaves-no-record</c>
    ///   • not run — drop <c>GenericApplier.ClientMissedBatchTick</c> from <c>SyncEngine.Tick</c> →
    ///     <c>nothing-asks-for-it-back</c>
    ///   • not run — rename the flag, the tick or either applier → <c>premise-changed</c>
    /// </summary>
    internal static class L348_ADiscardedBatchIsAskedForBack
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;
        private const string Flag = "_missedNoLevel";

        internal static IEnumerable<string> Check()
        {
            var seam = typeof(GenericApplier);
            var delta = seam.GetMethod("ApplyDelta", AllMembers);
            var structural = seam.GetMethod("ApplyStructural", AllMembers);
            var tick = seam.GetMethod("ClientMissedBatchTick", AllMembers);
            var engineTick = typeof(SyncEngine).GetMethod("Tick", AllMembers);

            if (delta == null || structural == null || tick == null || engineTick == null ||
                seam.GetField(Flag, AllMembers) == null ||
                seam.GetMethod("RequestResync", AllMembers) == null)
            {
                yield return "L348 premise-changed: one of GenericApplier.ApplyDelta / ApplyStructural (the " +
                             "two mid-load discards), GenericApplier." + Flag + " (the record they leave), " +
                             "ClientMissedBatchTick, RequestResync or SyncEngine.Tick no longer resolves. " +
                             "Both arms below are asserting about a shape the build no longer has.";
                yield break;
            }

            // ── arm (a), over the real seam.
            foreach (var v in Scan(delta, "GenericApplier.ApplyDelta")) yield return v;
            foreach (var v in Scan(structural, "GenericApplier.ApplyStructural")) yield return v;

            // ── arm (b): the record must become a request, off a tick and not off a message.
            bool reads = ReadsFlag(tick);
            bool asks = Reaches(tick, "RequestResync");
            bool driven = Reaches(engineTick, "ClientMissedBatchTick");
            if (!reads || !asks || !driven)
                yield return "L348 nothing-asks-for-it-back: the record has to turn back into a " +
                             "MsgResyncRequest on its own. ClientMissedBatchTick reads " + Flag + "=" + reads +
                             ", reaches RequestResync=" + asks + ", and SyncEngine.Tick reaches it=" + driven +
                             ". Every other resync trigger in this class is reactive — it needs an inbound " +
                             "message to notice — and a peer that dropped 50 batches while it had no level " +
                             "received nothing it could keep. The level going live is the only non-message " +
                             "edge available, so a trigger reachable only from HandleInbound is no trigger.";

            // ── arm (c): the must-reach walk above must be able to say NO.
            var control = Scan(typeof(FakeSeam).GetMethod("ApplyDelta", AllMembers), "FakeSeam.ApplyDelta").ToList();
            if (!control.Any(c => c.Contains("discard-leaves-no-record")))
                yield return "L348 control-not-red: FakeSeam.ApplyDelta returns on a null level and records " +
                             "nothing at all — the shipped shape before 2026-08-09 — and the scan did not " +
                             "flag it. The arm cannot tell a discard that leaves a trace from one that does " +
                             "not, so its green above means nothing.";
        }

        private static IEnumerable<string> Scan(MethodBase m, string label)
        {
            if (m == null || !Records(m, 1))
                yield return "L348 discard-leaves-no-record: " + label + " never writes " + Flag +
                             " (directly or through one call of its own class), so a batch it throws away " +
                             "because this peer has no geoscape level leaves NO trace. _lastSeq is marked " +
                             "only after that guard, so the drop moves nothing either — the 49 and 50 batch " +
                             "losses of 2026-08-08 were first noticed by an unrelated later seq gap, minutes " +
                             "of post-battle state after the fact.";
        }

        // ─── IL helpers (same primitives as L153/L158/L182/L250/L253/L344; Program.cs is not partial) ───

        /// <summary>Does this method leave the record, itself or through one call inside the same class?</summary>
        private static bool Records(MethodBase m, int depth)
        {
            if (TouchesFlag(m, 0x80)) return true;              // stsfld
            if (depth <= 0) return false;
            foreach (var c in CalleesOf(m))
                if (c.DeclaringType == typeof(GenericApplier) && Records(c, depth - 1)) return true;
            return false;
        }

        private static bool ReadsFlag(MethodBase m) => TouchesFlag(m, 0x7E);   // ldsfld

        private static bool TouchesFlag(MethodBase m, byte opcode)
        {
            foreach (var tok in TokensAfter(m, opcode))
            {
                FieldInfo f = null;
                try { f = m.Module.ResolveField(tok); } catch { }
                if (f != null && f.Name == Flag) return true;
            }
            return false;
        }

        private static bool Reaches(MethodBase caller, string calleeName)
            => CalleesOf(caller).Any(c => c.Name == calleeName);

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            foreach (var tok in TokensAfter(caller, 0x28, 0x6F))   // call / callvirt
            {
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(tok); } catch { }
                if (c != null) yield return c;
            }
        }

        private static IEnumerable<int> TokensAfter(MethodBase m, params byte[] opcodes)
        {
            byte[] il;
            try { il = m?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = 0; i + 4 < il.Length; i++)
                if (Array.IndexOf(opcodes, il[i]) >= 0)
                    yield return BitConverter.ToInt32(il, i + 1);
        }

        /// <summary>ARM (c). Never called — it exists only to be walked. This is the shipped shape: the
        /// level is missing, the batch is gone, and nothing anywhere knows it happened.</summary>
        private sealed class FakeSeam
        {
            internal static void ApplyDelta(object geo, byte[] payload)
            {
                if (geo == null) return;
                Console.WriteLine(payload.Length);
            }
        }
    }
}
