using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Base.UI;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Entities;
using PhoenixPoint.Geoscape.Entities.Abilities;
using PhoenixPoint.Geoscape.View.ViewModules;
using PhoenixPoint.Geoscape.View.ViewStates;

namespace RailCheck
{
    /// <summary>
    /// L92 — THE GEOSCAPE MODULES ARE THE REPAINT'S OTHER HALF, AND THEY ARE FOUND BY HANDLE, NOT BY
    /// SCENE SCAN. <c>UiNativeRepaint.Table</c> is keyed by VIEW-STATE TYPE. A geoscape MODULE belongs to
    /// no single state — the top-right activity strip and the site contextual menu are each driven by both
    /// <c>UIStateNothingSelected</c> and <c>UIStateVehicleSelected</c> — so NO number of table rows can
    /// ever reach one. That is a single structural gap, and both halves of the 2026-08-04 report are it:
    /// a peer's "Explore" button outliving the exploration another peer had already started, and the
    /// activity strip missing on one client until it walked into the Research screen and back.
    ///
    /// THE SILENT SWALLOW THIS LAW EXISTS FOR. The strip's refresh used to resolve its module with
    /// <c>UnityEngine.Object.FindObjectOfType</c>, which returns ONLY components on active GameObjects —
    /// and the game switches a module's GameObject off itself, <c>UIModuleBehavior.SetStateID</c>:34
    /// (<c>gameObject.SetActive(false)</c> for the off state). So on any peer whose screen had the module
    /// off when the delta landed, the lookup answered null and the entire refresh returned with NO
    /// exception and NO log line; only a state that re-<c>Init</c>s the module
    /// (<c>UIStateNothingSelected.EnterState</c>:104) brought it back — the "enter Research and come back
    /// and it appears" in the report. <c>GeoscapeModulesData</c> holds every module by reference whether
    /// it is active or not, so the handle cannot go quiet. The scene scan is BANNED here, mechanically.
    ///
    /// AND THE MENU IS RE-DERIVED, NEVER OPENED. Everything on it is derived —
    /// <c>SetMenuItems</c>:74-77 asks each ability <c>View.VisibleInContextMenu</c> /
    /// <c>View.CanActivate</c> and hides the ones that answer no, which is precisely why nothing about a
    /// button ever needs syncing — but re-running it on a peer that has no menu up would OPEN one nobody
    /// clicked, the same hazard the arc's popup rule names. The guard is the module's own
    /// <c>IsContextualMenuVisible</c>, and this law asserts it is read BEFORE the call, not merely
    /// somewhere in the method.
    ///
    /// Falsify: put <c>FindObjectOfType</c> back → <c>hud-scene-scan</c>; delete either module refresh →
    /// <c>strip-unreached</c> / <c>menu-unreached</c>; drop the visibility guard → <c>menu-unguarded</c>;
    /// hand-roll the ability list instead of calling the open state's own derivation → <c>menu-unreached</c>
    /// plus the premise arm below naming the method it stopped using.
    /// </summary>
    internal static class L92_DerivedGeoWidgets
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check(Assembly game)
        {
            var repaint = typeof(OpenUiRepaint);
            var ours = repaint.Assembly;
            var refresh = repaint.GetMethod("RefreshPersistentHud", All);

            if (refresh == null)
            {
                yield return "L92 seam-gone: OpenUiRepaint.RefreshPersistentHud does not resolve — the module half " +
                             "of the repaint has no entry point and every arm below is unaskable";
                yield break;
            }

            // ─── (a) POSITIVE CONTROL — the IL walk must actually read something out of our assembly ───
            var reached = Reaches(refresh, ours, 3).ToList();
            if (reached.Count == 0)
            {
                yield return "L92 il-unreadable: RefreshPersistentHud yields no callees in our own assembly — the " +
                             "scan is asleep and every arm below would pass green on an empty method";
                yield break;
            }

            // ─── (b) THE HANDLES the module half is resolved through ───
            var trackerField = typeof(GeoscapeModulesData).GetField("FactionDataTracker", All);
            var menuField = typeof(GeoscapeModulesData).GetField("SiteContextualMenuModule", All);
            if (trackerField == null || trackerField.FieldType != typeof(UIModuleFactionAgendaTracker) ||
                menuField == null || menuField.FieldType != typeof(UIModuleSiteContextualMenu))
                yield return "L92 module-handles-gone: GeoscapeModulesData.FactionDataTracker / " +
                             "SiteContextualMenuModule are not the module fields this seam reads (" +
                             (trackerField == null ? "tracker missing" : trackerField.FieldType.Name) + " / " +
                             (menuField == null ? "menu missing" : menuField.FieldType.Name) + "). Without them the " +
                             "refresh has no way to reach a module that is switched OFF, and it is exactly the " +
                             "switched-off case that fails";

            // ─── (c) THE SCENE SCAN IS BANNED — it is the silent swallow, not a style preference ───
            foreach (var c in Reaches(refresh, typeof(UnityEngine.Object).Assembly, 3))
                if (c.DeclaringType == typeof(UnityEngine.Object) && c.Name.StartsWith("FindObjectOfType", StringComparison.Ordinal))
                    yield return "L92 hud-scene-scan: RefreshPersistentHud reaches UnityEngine.Object." + c.Name +
                                 ". That skips every INACTIVE GameObject, and the game deactivates a module it is " +
                                 "not showing (UIModuleBehavior.SetStateID) — so the refresh returns null-and-quiet " +
                                 "on the peers that need it most, with no exception and no log line. Read the module " +
                                 "off GeoscapeModulesData instead";

            // The reason for that ban must still be true in the game, or it is a rule guarding a dead premise.
            var setStateId = typeof(UIModuleBehavior).GetMethod("SetStateID", All);
            var setActive = typeof(UnityEngine.GameObject).GetMethod("SetActive", All, null, new[] { typeof(bool) }, null);
            if (setStateId == null || setActive == null || !Calls(setStateId, setActive))
                yield return "L92 premise-changed: UIModuleBehavior.SetStateID no longer calls GameObject.SetActive — " +
                             "geoscape modules may no longer be deactivated when off-screen, so the FindObjectOfType " +
                             "ban above now guards a premise that has evaporated. Re-derive it before trusting it";

            // ─── (d) BOTH MODULE REFRESHES ARE REACHED from the one entry point ───
            var updateData = HarmonyLib.AccessTools.Method(typeof(UIModuleFactionAgendaTracker), "UpdateData", Type.EmptyTypes);
            var setMenuItems = typeof(UIModuleSiteContextualMenu).GetMethod("SetMenuItems", All);
            if (updateData == null || setMenuItems == null)
            {
                yield return "L92 native-refresh-gone: UIModuleFactionAgendaTracker.UpdateData() / " +
                             "UIModuleSiteContextualMenu.SetMenuItems no longer resolve — the two native rebuilds " +
                             "this seam drives cannot be checked, and a guessed replacement is what has broken this " +
                             "repaint before";
                yield break;
            }
            // The tracker's rebuild is REFLECTION-invoked (UpdateData is private and has a same-named
            // per-ROW overload), so an IL callee walk can never see it — L54 owns that handle's identity.
            // What L92 asserts is the half L54 cannot: that the refresh reaches the module BY HANDLE.
            var reachedGame = Reaches(refresh, game, 3).ToList();
            if (trackerField != null && !ReadsField(refresh, trackerField, 3))
                yield return "L92 strip-unreached: RefreshPersistentHud never reads " +
                             "GeoscapeModulesData.FactionDataTracker — nothing repaints the top-right activity strip " +
                             "on a peer that did not perform the gesture, and its own poll is a GAME-CLOCK coroutine, " +
                             "so a paused geoscape never catches up either";
            if (menuField != null && !ReadsField(refresh, menuField, 3))
                yield return "L92 menu-unreached: RefreshPersistentHud never reads " +
                             "GeoscapeModulesData.SiteContextualMenuModule — whatever it repaints, it is not the " +
                             "module the open site menu lives on";
            if (!reachedGame.Any(c => c.MetadataToken == setMenuItems.MetadataToken))
                yield return "L92 menu-unreached: RefreshPersistentHud never reaches " +
                             "UIModuleSiteContextualMenu.SetMenuItems — the open site menu is re-derived by NOTHING, " +
                             "so a peer keeps clicking an Explore button for an exploration that is already running";
            // My own new early-return: a null _context handle makes the strip refresh return forever, and
            // silently — the exact failure mode this law was written about.
            var ctxHandle = repaint.GetField("TrackerContext", All);
            object ctxBound = null;
            try { ctxBound = ctxHandle?.GetValue(null); } catch { }
            if (ctxHandle == null || ctxBound == null)
                yield return "L92 strip-guard-dead: OpenUiRepaint.TrackerContext is " +
                             (ctxHandle == null ? "gone" : "NULL") + " — the guard that keeps InitialSetup from " +
                             "NRE-ing on an un-Init'd module now refuses EVERY refresh instead, and refuses it " +
                             "without a word";

            // ─── (e) THE MENU RE-DERIVE MAY NOT OPEN A MENU ───
            var visible = typeof(UIModuleSiteContextualMenu).GetField("IsContextualMenuVisible", All);
            var caller = OursReaching(refresh, ours, setMenuItems, game, 3);
            if (visible == null)
                yield return "L92 premise-changed: UIModuleSiteContextualMenu.IsContextualMenuVisible is gone — the " +
                             "flag the re-derive is gated on no longer exists, so 'never open a menu nobody clicked' " +
                             "is now unenforceable";
            else if (caller == null)
                yield return "L92 menu-unguarded: no method of ours calls SetMenuItems directly, so the " +
                             "IsContextualMenuVisible gate cannot be located. A re-derive that is not visibly gated " +
                             "is one rail batch away from opening a site menu on every peer";
            else if (!ReadsBefore(caller, visible, setMenuItems))
                yield return "L92 menu-unguarded: " + caller.Name + " calls SetMenuItems without reading " +
                             "IsContextualMenuVisible first. SetMenuItems SETS that flag and activates the container " +
                             "(:59-60), so an ungated re-derive OPENS the site menu on every peer on every rail " +
                             "batch — a popup nobody clicked, which is the one thing a repaint may never do";

            // ─── (f) THE BUTTON REALLY IS DERIVED — otherwise re-deriving fixes nothing ───
            var visibleInMenu = typeof(GeoAbilityView).GetMethod("VisibleInContextMenu", All);
            var canActivate = typeof(GeoAbilityView).GetMethod("CanActivate", All);
            if (visibleInMenu == null || canActivate == null)
                yield return "L92 premise-changed: GeoAbilityView.VisibleInContextMenu / CanActivate no longer " +
                             "resolve — whether a menu button hides itself from the model is now unknown";
            // Asked of the MODULE TYPE, not of SetMenuItems' own IL: its visibility filter is a lambda
            // (`.Where(a => a.View.VisibleInContextMenu(_abilitiesTarget))`, :74), which the compiler lifts
            // into a separate method — a body-only check would be red today, against working code.
            else if (!TypeCalls(typeof(UIModuleSiteContextualMenu), visibleInMenu) ||
                     !TypeCalls(typeof(UIModuleSiteContextualMenu), canActivate))
                yield return "L92 premise-changed: UIModuleSiteContextualMenu no longer asks VisibleInContextMenu + " +
                             "CanActivate. The Explore button's disappearance stops being DERIVED from the mirrored " +
                             "site/vehicle state, so re-running SetMenuItems would repaint the same stale button — " +
                             "this seam would need a different one, and must not be trusted meanwhile";

            // ─── (h) THE QUEUED WINDOW THAT IS NOT A ONE-SHOT ───
            // Same shape as the module gap, one layer over: UIStateReplenish IS in the table's reach, but
            // the FALLBACK is not — Repaint's queued-window guard skips it, correctly (its EnterState:29
            // RequestGamePause()s and rebinds OkButton). So for a queued window a Table entry is not an
            // optimisation, it is the only repaint that can exist at all. The cutscene beside it is
            // deliberately absent (a one-shot presentation has nothing to re-read); this one is not, because
            // its list IS model-derived and its own handler never re-reads it.
            var replenishInit = typeof(UIModuleReplenish).GetMethods(All).FirstOrDefault(m => m.Name == "Init");
            var missingItems = typeof(PhoenixPoint.Geoscape.Levels.Factions.GeoPhoenixFaction)
                               .GetMethods(All).FirstOrDefault(m => m.Name == "GetMissingItems");
            var replenishEnter = typeof(UIStateReplenish).GetMethod("EnterState", All);
            if (!UiNativeRepaint.Table.ContainsKey(typeof(UIStateReplenish)))
                yield return "L92 replenish-unrepaintable: UIStateReplenish has no UiNativeRepaint.Table entry. " +
                             "It is a QUEUED window (GeoWindowCoverage.cs:174), so OpenUiRepaint.Repaint's " +
                             "IsCurrentQueuedWindow guard skips the Exit+Enter fallback — with no table entry the " +
                             "resupply screen is repainted by NOTHING, and its missing-items list keeps naming gear a " +
                             "peer already replenished";
            else if (replenishInit == null || missingItems == null)
                yield return "L92 premise-changed: UIModuleReplenish.Init / GeoPhoenixFaction.GetMissingItems no " +
                             "longer resolve — the table entry's read-direction reseed drives methods that are gone";
            else if (replenishEnter != null && !Calls(replenishEnter, missingItems))
                yield return "L92 premise-changed: UIStateReplenish.EnterState no longer snapshots GetMissingItems(). " +
                             "The staleness the table entry exists to fix was that snapshot; if the screen now reads " +
                             "the list live, the entry is dead weight — and if it snapshots something ELSE, the entry " +
                             "reseeds the wrong thing";

            // ─── (g) THE DERIVATION WE BORROW is still spelled the way the lookup spells it ───
            foreach (var state in new[] { typeof(UIStateNothingSelected), typeof(UIStateVehicleSelected) })
            {
                var derive = state.GetMethod("GetContextualAbilities", All, null, new[] { typeof(GeoSite) }, null);
                if (derive == null || !typeof(IEnumerable<GeoAbility>).IsAssignableFrom(derive.ReturnType))
                    yield return "L92 derivation-renamed: " + state.Name + " no longer declares " +
                                 "GetContextualAbilities(GeoSite) returning ability list. The re-derive resolves that " +
                                 "method BY NAME on whatever state is open and declines silently when it misses, so " +
                                 "this rename turns the site menu stale again with nothing red at runtime";
            }
        }

        // ─── IL helpers (same operand-size walk as L77: under-reports, never invents an edge) ───

        /// <summary>Every method in <paramref name="asm"/> reachable from <paramref name="root"/> within
        /// <paramref name="depth"/> hops, expanding only through our OWN assembly — the seam delegates to
        /// two private helpers, so a one-level walk would answer about the wrong method.</summary>
        private static IEnumerable<MethodBase> Reaches(MethodBase root, Assembly asm, int depth)
        {
            var ours = root.DeclaringType.Assembly;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<MethodBase> { root };
            var result = new List<MethodBase>();
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                {
                    result.AddRange(Callees(m, asm));
                    foreach (var c in Callees(m, ours))
                        if (seen.Add(Key(c))) next.Add(c);
                }
                frontier = next;
            }
            return result;
        }

        /// <summary>The method OF OURS, at most <paramref name="depth"/> hops from the root, that calls
        /// <paramref name="target"/> directly.</summary>
        private static MethodBase OursReaching(MethodBase root, Assembly ours, MethodBase target, Assembly game, int depth)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<MethodBase> { root };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                {
                    if (Callees(m, game).Any(c => c.MetadataToken == target.MetadataToken)) return m;
                    foreach (var c in Callees(m, ours))
                        if (seen.Add(Key(c))) next.Add(c);
                }
                frontier = next;
            }
            return null;
        }

        /// <summary>Is <paramref name="field"/> read anywhere within <paramref name="depth"/> hops of
        /// <paramref name="root"/> through our own assembly?</summary>
        internal static bool ReadsField(MethodBase root, FieldInfo field, int depth)
        {
            var ours = root.DeclaringType.Assembly;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new List<MethodBase> { root };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<MethodBase>();
                foreach (var m in frontier)
                {
                    foreach (var op in Ops(m))
                        if (op.Field != null && op.Field.MetadataToken == field.MetadataToken &&
                            op.Field.Module == field.Module) return true;
                    foreach (var c in Callees(m, ours))
                        if (seen.Add(Key(c))) next.Add(c);
                }
                frontier = next;
            }
            return false;
        }

        /// <summary>Identity for the visited set: ResolveMethod hands back a FRESH MethodBase per call site,
        /// so reference/hash identity would never dedup and the walk would fan out exponentially.</summary>
        private static string Key(MethodBase m) => m.Module.FullyQualifiedName + ":" + m.MetadataToken;

        /// <summary>Does <paramref name="m"/> read <paramref name="field"/> at an IL offset BEFORE its first
        /// call to <paramref name="callee"/>? Order, not presence: a guard read after the call guards
        /// nothing.</summary>
        private static bool ReadsBefore(MethodBase m, FieldInfo field, MethodBase callee)
        {
            int fieldAt = -1;
            foreach (var op in Ops(m))
            {
                if (op.Field != null && fieldAt < 0 &&
                    op.Field.MetadataToken == field.MetadataToken && op.Field.Module == field.Module)
                    fieldAt = op.Offset;
                if (op.Method != null &&
                    op.Method.MetadataToken == callee.MetadataToken && op.Method.Module == callee.Module)
                    return fieldAt >= 0 && fieldAt < op.Offset;
            }
            return false;
        }

        /// <summary>Does <paramref name="t"/> — any of its own methods, or any method of a nested type the
        /// compiler generated for its lambdas — call <paramref name="callee"/>?</summary>
        private static bool TypeCalls(Type t, MethodBase callee)
        {
            foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                if (Calls(m, callee)) return true;
            foreach (var nested in t.GetNestedTypes(All))
                foreach (var m in nested.GetMethods(All).Cast<MethodBase>())
                    if (Calls(m, callee)) return true;
            return false;
        }

        private static bool Calls(MethodBase m, MethodBase callee) =>
            callee != null && Callees(m, callee.Module.Assembly)
                .Any(c => c.MetadataToken == callee.MetadataToken && c.Module == callee.Module);

        private static IEnumerable<MethodBase> Callees(MethodBase m, Assembly asm)
        {
            foreach (var op in Ops(m))
                if (op.Method != null && op.Method.Module.Assembly == asm) yield return op.Method;
        }

        private struct Op { public int Offset; public MethodBase Method; public FieldInfo Field; }

        private static readonly Dictionary<short, OpCode> OpCodeByValue =
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                           .Where(f => f.FieldType == typeof(OpCode))
                           .Select(f => (OpCode)f.GetValue(null))
                           .GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First());

        private static IEnumerable<Op> Ops(MethodBase m)
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
                int at = i;
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
                    if (callee != null) yield return new Op { Offset = at, Method = callee };
                }
                else if (op.OperandType == OperandType.InlineField)
                {
                    FieldInfo f = null;
                    try { f = m.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, methodArgs); } catch { }
                    if (f != null) yield return new Op { Offset = at, Field = f };
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
