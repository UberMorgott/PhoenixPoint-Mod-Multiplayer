using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.MessageLayer;

namespace RailCheck
{
    /// <summary>
    /// L414 — NO WIRE STRING ALLOCATES ON THE SENDER'S WORD.
    ///
    /// THE FAILURE (4549234). <c>BinaryReader.ReadString</c> pre-sizes a StringBuilder from the
    /// WIRE-DECLARED byte length before it reads a single byte of payload (it only reads 128 bytes at a
    /// time), so a ~20-byte JOIN declaring 2^28 forced a ~512 MB allocation on the host — PRE-AUTH, and
    /// long before the roster's nickname cap ever saw the string. aa32a1a had already bounded the
    /// <c>ReadBytes</c> and count sites in this file; all 17 <c>ReadString</c> sites were still bare, which
    /// is exactly how this class of hole survives a fix: the guarded reads look guarded.
    ///
    /// THE RULE. Every string this serializer decodes goes through
    /// <see cref="MessageSerializer"/>.<c>ReadBoundedString</c>, which reads the 7-bit length prefix itself
    /// and rejects a length the stream cannot back before it allocates anything. It decodes with
    /// <c>UTF8Encoding(false, true)</c> — byte-identical to the default reader, so the wire format is
    /// unchanged and this is a pure read-side bound.
    ///
    /// WHY IT NEEDS A LAW AND NOT A CODE REVIEW. A new message added later with one bare
    /// <c>br.ReadString()</c> re-opens the whole hole silently: it parses correctly against a friendly
    /// peer forever, and the only sender that notices the difference is the hostile one.
    ///
    /// THE ARMS:
    ///   (a) <c>bare-readstring</c> — no method of <see cref="MessageSerializer"/> (or any type nested in
    ///       it, where lambdas and iterators live) may call <c>BinaryReader.ReadString</c>.
    ///   (b) POSITIVE CONTROL, EXECUTED — arm (a) is an ABSENCE claim, and an absence claim that cannot
    ///       SEE its subject passes forever the moment the walker or the type stops resolving. So the same
    ///       scan is run against <see cref="Sentinel"/> in this file, which calls <c>ReadString</c> by
    ///       construction, and must find it. The bounded helper must resolve and must actually be reached
    ///       from the serializer too — a file that decodes no strings at all would satisfy arm (a) by
    ///       having nothing to check.
    ///
    /// Falsify: put one <c>br.ReadString()</c> back at any site → (a); rename or delete
    /// <c>ReadBoundedString</c>, or break <see cref="Program.CalleeSequence"/> → (b).
    /// </summary>
    internal static class L414_NoWireStringAllocatesOnTheSendersWord
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        internal static IEnumerable<string> Check()
        {
            var ser = typeof(MessageSerializer);
            var bounded = ser.GetMethod("ReadBoundedString", All);
            var bodies = AllTypes(ser).SelectMany(AllMethodsOf).ToList();

            // ── arm (b), FIRST: nothing below means anything if the scan is blind.
            var control = typeof(L414_NoWireStringAllocatesOnTheSendersWord).GetMethod("Sentinel", All);
            if (bounded == null || control == null || !CallsBareReadString(control))
            {
                yield return "L414 premise-changed: POSITIVE CONTROL failed — MessageSerializer." +
                             "ReadBoundedString did not resolve, or the IL scan cannot see the " +
                             "BinaryReader.ReadString call inside this law's own Sentinel, which is there by " +
                             "construction. This law's whole claim is an ABSENCE, so a scan that finds nothing " +
                             "anywhere would report the serializer as bounded while every read on it is bare. " +
                             "Fix the reader (or re-point this law) before believing arm (a).";
                yield break;
            }
            if (!bodies.Any(m => Program.CalleeSequence(m)
                    .Any(c => c != null && c.MetadataToken == bounded.MetadataToken && c.Module == bounded.Module)))
            {
                yield return "L414 premise-changed: no method of MessageSerializer reaches ReadBoundedString. " +
                             "Either the decode paths moved out of this type or the helper is dead — and a " +
                             "serializer that reads no strings satisfies arm (a) by having no strings to bound. " +
                             "Re-point this law at wherever the wire strings are decoded now.";
                yield break;
            }

            // ── arm (a): the bound has no exceptions.
            foreach (var m in bodies)
            {
                if (!CallsBareReadString(m)) continue;
                yield return "L414 bare-readstring: " + m.DeclaringType?.Name + "." + m.Name + " calls " +
                             "BinaryReader.ReadString. That reader pre-sizes a StringBuilder from the length " +
                             "the SENDER declared, before any payload is read, so a 20-byte packet claiming " +
                             "2^28 makes the host allocate ~512 MB — pre-auth, and the nickname cap never " +
                             "sees the string. Read it through MessageSerializer.ReadBoundedString, which " +
                             "rejects a length the stream cannot back and decodes byte-identically.";
            }
        }

        /// <summary>ARM (b). The shape arm (a) must be able to see. Never called at runtime — it exists only
        /// to be scanned, and it is deliberately NOT expression-bodied over a field so the call survives.</summary>
        private static string Sentinel(BinaryReader br)
        {
            return br.ReadString();
        }

        private static bool CallsBareReadString(MethodBase m)
            => Program.CalleeSequence(m)
                .Any(c => c != null && c.Name == "ReadString" && c.DeclaringType == typeof(BinaryReader));

        /// <summary>A type plus its compiler-generated nests — an iterator or lambda's IL is not in the
        /// declaring type at all, and a decode loop is exactly where a bare read would hide.</summary>
        private static IEnumerable<Type> AllTypes(Type root)
        {
            yield return root;
            foreach (var n in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var d in AllTypes(n)) yield return d;
        }

        private static IEnumerable<MethodBase> AllMethodsOf(Type t)
            => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All));
    }
}
