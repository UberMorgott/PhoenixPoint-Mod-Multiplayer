using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.MessageLayer;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L420 — NOTHING DECODES THE WIRE ON THE SENDER'S WORD, ANYWHERE IN THE ASSEMBLY.
    ///
    /// L414 closed FA-0012 on <see cref="MessageSerializer"/> and stopped there. THAT WAS THE BUG (8b1f23b):
    /// 104 bare <c>BinaryReader.ReadString</c> sites survived in 29 other files, every one of them reached
    /// from an attacker-controllable payload through <c>IntentRail.HandleInbound</c>,
    /// <c>SurfaceRouter.OnInbound</c> or <c>NetworkEngine</c>'s PingMarker seam. <c>ReadString</c> pre-sizes a
    /// StringBuilder from the length the SENDER declared before it reads a byte of payload, so a ~20-byte
    /// packet claiming 2^28 forces a ~512 MB allocation on the receiver. daaadf4 had already found the same
    /// hole on the value rail after L414 was written and believed.
    ///
    /// SO THE SCOPE IS THE ASSEMBLY, NOT A TYPE. A law that names the types it trusts has to be re-derived
    /// every time a decoder moves, and each re-derivation is a chance to leave one out — which is the whole
    /// history above. The mod writes no file formats and parses no user documents; every
    /// <c>BinaryReader</c> in it is a wire reader, so "no bare ReadString anywhere" needs no allowlist and
    /// cannot go stale. L414 keeps its narrower arm because it also asserts the HELPER's home; this one is the
    /// quantifier.
    ///
    /// THE COUNT IS THE OTHER HALF (df843ce). A string ceiling says nothing about the array count in front of
    /// it: 17 decoders read <c>int n = r.ReadInt32()</c> and looped on it, five of them pre-sizing a
    /// <c>List</c>/<c>Dictionary</c> to a multi-GB backing array on the sender's word. That is now
    /// <c>MessageSerializer.ReadBoundedCount</c>, and arm (c) EXECUTES the bound rather than asserting it is
    /// called: the guard's whole content is a comparison against the bytes remaining, and "is it called"
    /// cannot tell a working comparison from an inverted one.
    ///
    /// AND THE FRAGMENT HEADER IS THE THIRD (RailMeta). <c>TryDecodeFragment</c> validated a slice with
    /// <c>offset + n &gt; total</c>, which OVERFLOWS: <c>total=int.MaxValue</c>, <c>offset=0x7FFFFF00</c>,
    /// <c>n=0x200</c> wraps negative, passes, and the caller then indexes a 2 GB array it was just told to
    /// allocate. Arm (d) is that exact frame.
    ///
    /// THE ARMS:
    ///   (a) POSITIVE CONTROL, EXECUTED AND FIRST — arm (b) is an ABSENCE claim over the whole assembly, and
    ///       an absence claim whose scanner has gone blind reads exactly like a clean assembly. A sentinel in
    ///       this file calls <c>ReadString</c> by construction and must be found; <c>ReadBoundedString</c> and
    ///       <c>ReadBoundedCount</c> must resolve and must be reached from real decoders.
    ///   (b) <c>bare-readstring</c> — no method of the mod assembly, closures included, calls
    ///       <c>BinaryReader.ReadString</c>.
    ///   (c) EXECUTED — <c>ReadBoundedCount</c> accepts a count the stream can back, refuses one it cannot,
    ///       refuses a negative one, and honours <c>minBytesPerEntry</c>.
    ///   (d) EXECUTED — <c>RailMeta.TryDecodeFragment</c> refuses the overflow frame and still accepts a
    ///       legitimate slice. Both halves, or the arm passes on a method that returns false unconditionally.
    ///
    /// Falsify: put one <c>br.ReadString()</c> back at any of the 104 sites → (b); make
    /// <c>ReadBoundedCount</c> return <c>br.ReadInt32()</c> raw → (c); restore <c>offset + n &gt; total</c>
    /// in <c>TryDecodeFragment</c> → (d).
    /// </summary>
    internal static class L420_NothingDecodesTheWireOnTheSendersWord
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var mod = typeof(MessageSerializer).Assembly;
            var bounded = typeof(MessageSerializer).GetMethod("ReadBoundedString", All);
            var count = typeof(MessageSerializer).GetMethod("ReadBoundedCount", All);
            var control = typeof(L420_NothingDecodesTheWireOnTheSendersWord).GetMethod("Sentinel", All);

            // ── (a) NOTHING BELOW MEANS ANYTHING IF THE SCAN IS BLIND ───────────────
            if (bounded == null || count == null || control == null || !CallsBareReadString(control))
            {
                yield return "L420 premise-changed: POSITIVE CONTROL failed — MessageSerializer." +
                             "ReadBoundedString/ReadBoundedCount did not resolve, or the IL scan cannot see the " +
                             "BinaryReader.ReadString call inside this law's own Sentinel, which is there by " +
                             "construction. Arm (b) is an ABSENCE over the whole assembly, so a scan that finds " +
                             "nothing would report 29 files of bare readers as bounded.";
                yield break;
            }
            var bodies = mod.GetTypes().SelectMany(Methods).ToList();
            if (!bodies.Any(m => Reaches(m, bounded)) || !bodies.Any(m => Reaches(m, count)))
            {
                yield return "L420 premise-changed: nothing in the mod assembly reaches " +
                             (bodies.Any(m => Reaches(m, bounded)) ? "ReadBoundedCount" : "ReadBoundedString") +
                             ". An assembly that decodes no wire strings, or no wire counts, satisfies arm (b) by " +
                             "having nothing to bound — re-point this law at wherever the decoders went.";
                yield break;
            }

            // ── (b) THE BOUND HAS NO EXCEPTIONS, IN ANY FILE ────────────────────────
            foreach (var m in bodies)
            {
                if (!CallsBareReadString(m)) continue;
                yield return "L420 bare-readstring: " + Name(m) + " calls BinaryReader.ReadString. That reader " +
                             "pre-sizes a StringBuilder from the length the SENDER declared, before any payload " +
                             "is read, so a 20-byte packet claiming 2^28 makes the receiver allocate ~512 MB. " +
                             "Every BinaryReader in this assembly is over a network payload — there are no file " +
                             "formats here — so there is no site where the bare reader is the right one. Read it " +
                             "through MessageSerializer.ReadBoundedString (or one of WireString's named " +
                             "ceilings), which refuses a length the stream cannot back and decodes " +
                             "byte-identically.";
            }

            // ── (c) EXECUTED: THE COUNT BOUND IS A BOUND ────────────────────────────
            foreach (var v in CountArm(count)) yield return v;

            // ── (d) EXECUTED: THE FRAGMENT HEADER CANNOT BE WRAPPED ─────────────────
            int total = 0, offset = 0;
            byte[] chunk = null;

            var attack = new byte[RailMeta.FragmentHeaderBytes + 0x200];
            attack[0] = RailMeta.FragmentMarker;
            Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue), 0, attack, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(0x7FFFFF00), 0, attack, 5, 4);
            bool taken = false;
            string threw = null;
            try { taken = RailMeta.TryDecodeFragment(attack, out total, out offset, out chunk); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null)
            {
                yield return "L420 fragment-header-unbounded: TryDecodeFragment THREW " + threw +
                             " on the overflow frame instead of refusing it. The caller treats false as 'an " +
                             "ordinary value' and fails loudly downstream; a throw out of the frame test is a " +
                             "different, unhandled path.";
                yield break;
            }
            if (taken)
                yield return "L420 fragment-header-unbounded: TryDecodeFragment ACCEPTED total=int.MaxValue, " +
                             "offset=0x7FFFFF00, n=0x200 (it answered total=" + total + " offset=" + offset +
                             "). Written as `offset + n > total` that sum WRAPS NEGATIVE and passes, and " +
                             "GenericApplier.Reassemble then does `new byte[total]` — 2 GB on the sender's word — " +
                             "and indexes into it at the wrapped offset. The form that cannot wrap is " +
                             "`offset > total - n`, with total capped by MaxReassembledBytes first.";

            var value = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var slice = RailMeta.EncodeFragment(value, 4, 4);
            bool honest = false;
            threw = null;
            try { honest = RailMeta.TryDecodeFragment(slice, out total, out offset, out chunk); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null)
            {
                yield return "L420 fragment-guard-refuses-everything: TryDecodeFragment threw " + threw +
                             " on a slice its own EncodeFragment produced.";
                yield break;
            }
            if (!honest || total != value.Length || offset != 4 || chunk == null || chunk.Length != 4 ||
                chunk[0] != 5 || chunk[3] != 8)
                yield return "L420 fragment-guard-refuses-everything: a legitimate slice (EncodeFragment of an " +
                             "8-byte value at offset 4, length 4) came back as " +
                             (honest ? "total=" + total + " offset=" + offset + " chunk=" +
                                       (chunk == null ? "<null>" : chunk.Length + " bytes") : "REFUSED") +
                             ". Without this half the overflow arm above passes on a guard that rejects every " +
                             "frame, which is not a bound — it is oversized values silently never reassembling.";
        }

        private static IEnumerable<string> CountArm(MethodInfo count)
        {
            // 4 bytes of count, then 10 bytes of payload. Anything the stream cannot back must be refused.
            Func<int, int, object> read = (declared, minPer) =>
            {
                var buf = new byte[14];
                Buffer.BlockCopy(BitConverter.GetBytes(declared), 0, buf, 0, 4);
                using (var ms = new MemoryStream(buf))
                using (var br = new BinaryReader(ms))
                {
                    try { return count.Invoke(null, new object[] { br, minPer }); }
                    catch (TargetInvocationException ex) { return ex.InnerException; }
                    catch (Exception ex) { return ex; }
                }
            };

            if (!(read(10, 1) is int ok) || ok != 10)
                yield return "L420 count-bound-refuses-everything: ReadBoundedCount refused 10 entries over 10 " +
                             "remaining bytes at 1 byte each — a count the stream CAN back. A guard that refuses " +
                             "legitimate payloads is not a bound; it is every multi-entry message silently " +
                             "failing to decode, and it would make the refusal arms below pass on nothing.";
            if (read(11, 1) is int)
                yield return "L420 count-unbounded: ReadBoundedCount ACCEPTED 11 entries over 10 remaining " +
                             "bytes. Every entry costs at least one byte, so the count is impossible — and a " +
                             "decoder that believes it loops allocate-and-throw until the stream is exhausted, " +
                             "with any `new List<T>(n)` in between pre-sizing a multi-GB array first.";
            if (read(int.MaxValue, 1) is int)
                yield return "L420 count-unbounded: ReadBoundedCount ACCEPTED 2^31-1 entries over 10 bytes. This " +
                             "is the shape df843ce found at 17 sites — the whole point of the helper.";
            if (read(-1, 1) is int)
                yield return "L420 count-unbounded: ReadBoundedCount ACCEPTED a NEGATIVE count. The `n < 0 ? 0 : n` " +
                             "idiom five decoders carried protected only the capacity ARGUMENT from throwing; the " +
                             "loop still ran on the raw value.";
            if (read(3, 5) is int)
                yield return "L420 count-unbounded: ReadBoundedCount ignored minBytesPerEntry — 3 entries of 5 " +
                             "bytes each needs 15 and the stream has 10. The tightened floors (25 B for a " +
                             "resnapshot actor, 14 B for a status application, …) are the only thing separating " +
                             "'a plausible count' from 'as many entries as there are bytes'.";
            if (!(read(2, 5) is int fits) || fits != 2)
                yield return "L420 count-bound-refuses-everything: ReadBoundedCount refused 2 entries of 5 bytes " +
                             "over exactly 10 remaining bytes — a payload that fits exactly. An off-by-one here " +
                             "drops the last legitimate element of every full batch.";
        }

        /// <summary>ARM (a). The shape arm (b) must be able to see. Never called at runtime.</summary>
        private static string Sentinel(BinaryReader br)
        {
            return br.ReadString();
        }

        private static bool CallsBareReadString(MethodBase m)
            => Program.CalleeSequence(m)
                .Any(c => c != null && c.Name == "ReadString" && c.DeclaringType == typeof(BinaryReader));

        private static bool Reaches(MethodBase m, MethodInfo target)
            => Program.CalleeSequence(m)
                .Any(c => c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module);

        private static string Name(MethodBase m) =>
            (m.DeclaringType == null ? "?" : m.DeclaringType.FullName.Replace('+', '/')) + "." + m.Name;

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            try { return t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)).ToList(); }
            catch { return Enumerable.Empty<MethodBase>(); }
        }
    }
}
