using System;
using System.Collections.Generic;
using System.Reflection;

namespace RailCheck
{
    /// <summary>The three IL questions the window-journal laws ask, in one place so seven laws do not each
    /// carry a copy. Same cross-assembly token resolve L492/L516 use: a callee in UnityEngine or
    /// Assembly-CSharp can never match a raw token compare inside the mod assembly.</summary>
    internal static class Il
    {
        internal static byte[] Body(MethodBase m)
        {
            try { return m?.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
        }

        internal static bool References(MethodBase m, MethodBase callee)
        {
            var il = Body(m);
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

        /// <summary>Every method this one MENTIONS — called or, for a registration table, handed over as a
        /// function pointer (<c>ldftn</c>). Deliberately over-approximate (it resolves every 4-byte window,
        /// as <see cref="References"/> does): a law asks it "which handlers does this registration name",
        /// then filters by signature, so a spurious resolve is dropped rather than believed.</summary>
        internal static IEnumerable<MethodBase> ReferencedMethods(MethodBase m)
        {
            var seen = new Dictionary<int, MethodBase>();
            var il = Body(m);
            if (il == null) return seen.Values;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(BitConverter.ToInt32(il, i)); } catch { }
                if (resolved != null && !seen.ContainsKey(resolved.MetadataToken))
                    seen[resolved.MetadataToken] = resolved;
            }
            return seen.Values;
        }

        /// <summary>Does the method load this exact byte constant? ldc.i4.s = 0x1F, ldc.i4 = 0x20.</summary>
        internal static bool MentionsByte(MethodBase m, byte value)
        {
            var il = Body(m);
            if (il == null) return false;
            for (int i = 0; i + 1 < il.Length; i++)
                if (il[i] == 0x1F && il[i + 1] == value) return true;
            for (int i = 0; i + 4 < il.Length; i++)
                if (il[i] == 0x20 && BitConverter.ToInt32(il, i + 1) == value) return true;
            return false;
        }

        /// <summary>Every method this one CALLS, read at the CALL OPCODE rather than by sliding a 4-byte
        /// window over the whole body. <see cref="ReferencedMethods"/> is deliberately over-approximate
        /// because its callers filter the result by signature; a TRANSITIVE walk cannot afford that — a
        /// spurious resolve at depth 1 becomes a whole spurious subtree at depth 2. Anchored opcodes:
        /// call (0x28), callvirt (0x6F), newobj (0x73), ldftn/ldvirtftn (0xFE 0x06 / 0xFE 0x07).
        /// Still not a real IL decoder: an operand byte can alias an opcode byte, so this over-approximates
        /// too — just far less. Every consumer therefore treats the result as a SUPERSET.</summary>
        internal static IEnumerable<MethodBase> CalledMethods(MethodBase m)
        {
            var seen = new Dictionary<int, MethodBase>();
            var il = Body(m);
            if (il == null) return seen.Values;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                int at;
                if (il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73) at = i + 1;
                else if (il[i] == 0xFE && i + 5 < il.Length && (il[i + 1] == 0x06 || il[i + 1] == 0x07)) at = i + 2;
                else continue;
                if (at + 4 > il.Length) continue;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(BitConverter.ToInt32(il, at)); } catch { }
                if (resolved != null && !seen.ContainsKey(resolved.MetadataToken))
                    seen[resolved.MetadataToken] = resolved;
            }
            return seen.Values;
        }

        /// <summary>The transitive call closure of <paramref name="root"/>, bounded by
        /// <paramref name="depth"/> and confined to ONE assembly. The bound is not a nicety: the geoscape
        /// tick reaches most of Assembly-CSharp within four hops, and an unbounded walk over an
        /// over-approximate callee set is a walk over the whole game. Confinement to the declaring
        /// assembly keeps Unity/mscorlib leaves out for the same reason.</summary>
        internal static HashSet<MethodBase> Closure(MethodBase root, int depth, Assembly within)
        {
            var seen = new HashSet<MethodBase>();
            if (root == null) return seen;
            var frontier = new List<MethodBase> { root };
            seen.Add(root);
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                    foreach (var callee in CalledMethods(m))
                    {
                        if (within != null && callee.DeclaringType?.Assembly != within) continue;
                        if (seen.Add(callee)) next.Add(callee);
                    }
                frontier = next;
            }
            return seen;
        }

        /// <summary>Methods this one hands over as a FUNCTION POINTER (ldftn 0xFE06 / ldvirtftn 0xFE07) —
        /// i.e. the delegate targets it builds. An event subscription <c>x.E += H</c> compiles to exactly
        /// this: ldftn H, newobj the delegate, callvirt add_E. Separated from <see cref="CalledMethods"/>
        /// so a law can ask "which HANDLER did this register" without also getting every callee.</summary>
        internal static IEnumerable<MethodBase> LoadedFunctions(MethodBase m)
        {
            var seen = new Dictionary<int, MethodBase>();
            var il = Body(m);
            if (il == null) return seen.Values;
            for (int i = 0; i + 5 < il.Length; i++)
            {
                if (il[i] != 0xFE || (il[i + 1] != 0x06 && il[i + 1] != 0x07)) continue;
                MethodBase resolved = null;
                try { resolved = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 2)); } catch { }
                if (resolved != null && !seen.ContainsKey(resolved.MetadataToken))
                    seen[resolved.MetadataToken] = resolved;
            }
            return seen.Values;
        }

        /// <summary>Does this method subscribe to ANY event (a call to an <c>add_*</c> accessor)?</summary>
        internal static bool Subscribes(MethodBase m)
        {
            foreach (var callee in CalledMethods(m))
                if (callee.Name.StartsWith("add_", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Fields this method WRITES (stfld 0x7D, stsfld 0x80), opcode-anchored like
        /// <see cref="CalledMethods"/> and a superset for the same reason.</summary>
        internal static IEnumerable<FieldInfo> WrittenFields(MethodBase m)
        {
            var seen = new Dictionary<int, FieldInfo>();
            var il = Body(m);
            if (il == null) return seen.Values;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x7D && il[i] != 0x80) continue;
                FieldInfo f = null;
                try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (f != null && !seen.ContainsKey(f.MetadataToken)) seen[f.MetadataToken] = f;
            }
            return seen.Values;
        }

        internal static bool MentionsAnyString(MethodBase m, string[] needles)
        {
            var il = Body(m);
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x72) continue; // ldstr
                string s = null;
                try { s = m.Module.ResolveString(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (s == null) continue;
                foreach (var n in needles) if (s == n) return true;
            }
            return false;
        }
    }
}
