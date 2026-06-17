using System;
using System.Reflection;

namespace Multipleer.Network.Sync.State
{
    /// <summary>
    /// Tiny, Unity-free reflection helper for disambiguating overloaded methods that
    /// <see cref="Type.GetMethod(string, Type[])"/> (and Harmony's <c>AccessTools.Method</c>) cannot tell apart.
    ///
    /// Motivating case: <c>Base.Defs.NamedListDef</c> declares BOTH <c>GetDef(string)</c> and
    /// <c>GetDef&lt;T&gt;(string)</c>. Looking up by parameter types alone matches both single-<c>string</c>
    /// overloads and throws <see cref="AmbiguousMatchException"/>. This resolver enumerates the candidates and
    /// selects by name + arity (generic vs not) + a single <c>string</c> parameter, so it never throws.
    /// </summary>
    internal static class MethodOverloadResolver
    {
        /// <summary>
        /// Returns the GENERIC method definition named <paramref name="name"/> on <paramref name="type"/> that
        /// takes exactly one <see cref="string"/> parameter (the open <c>GetDef&lt;T&gt;(string)</c> form), or
        /// <c>null</c> if no such overload exists. Never throws on ambiguity.
        /// </summary>
        public static MethodInfo FindGenericSingleStringMethod(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != name || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return m;
            }
            return null;
        }
    }
}
