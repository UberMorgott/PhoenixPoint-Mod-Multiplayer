using System;
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
