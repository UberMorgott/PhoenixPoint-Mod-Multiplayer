using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Network.Sync;
using PhoenixPoint.Geoscape.Events;

namespace RailCheck
{
    /// <summary>
    /// L327 — AN EVENT VARIABLE THAT NAMES WHAT ONE PLAYER IS LOOKING AT NEVER ENTERS THE REPLICATED
    /// CAMPAIGN STATE, AND EVERY OTHER EVENT VARIABLE STILL DOES.
    ///
    /// THE REPORT (2026-08-08). A "hide helmet" toggle flipped on one soldier restyled ALL soldiers on
    /// EVERY peer — including the soldier another peer had open on its own screen — and then flip-flopped
    /// on its own. Neither half was a rail defect: the rail did exactly what it is built to do.
    ///
    ///   (1) THE TOGGLE IS CAMPAIGN-GLOBAL IN TFTV, BY DESIGN. `ShowWithoutHelmet.HelmetsOff` (:33) is one
    ///       static bool persisted through `EventSystem.SetVariable("TFTV_HelmetsOff", 0|1)` (:116) and
    ///       read back at :98. TFTV keeps NO per-`GeoCharacter` helmet state, so "sync it per soldier"
    ///       names a thing that does not exist. The only correct answer is that it is not shared at all —
    ///       which `UiEventMap`:242-250 had ALREADY declared in prose while the event-variable path
    ///       silently contradicted it and won at runtime. The mixed appearance the player eventually saw
    ///       is TFTV forcing helmets ON for mutoid/augmented-head characters (:221-236, :296-310).
    ///
    ///   (2) THE FLIP-FLOP WAS THE BLOCK ITSELF, NOT A SECOND BUG. `EventVariableCapture.Prefix`'s
    ///       equality early-out reads the LIVE dictionary, which its own `return false` never updates, so
    ///       TFTV re-read the stale value (:88-105), re-applied it (:138) and its postfix (:264) wrote
    ///       again — an unbounded loop that a shadow-less exclusion would have KEPT.
    ///
    /// WHAT IS ASSERTED IS THE END STATE OF THE DICTIONARY THE RAIL CARRIES, not that some method was
    /// called. `_customVariables` IS `CustomVariables` (docs/rail-baseline.txt:473, `EntityList`), so the
    /// question "did it reach the wire" is exactly "is the key in that dictionary", and the answer is
    /// demanded on BOTH sides: the listed name must be absent, and an ordinary campaign flag must be
    /// present with its value and readable back. A law that only checked the exclusion would be green over
    /// the far worse regression of breaking every event variable in the game.
    ///
    /// AND THE READ SIDE IS HALF THE OUTCOME: the excluded name must answer THIS process's own value, not
    /// the absent-key default, or the write is blocked and the loop above survives.
    ///
    /// FALSIFY: delete the `EventSync.TrySetLocalPreference` call from `EventVariableCapture.Prefix` — the
    /// shipped defect verbatim — → `view-setting-lands-in-replicated-state` + `local-value-not-read-back`.
    /// The other way: make `TrySetLocalPreference` claim every name → `campaign-flag-dropped`.
    ///
    /// STATED LIMIT: a console harness cannot hold a `GeoscapeEventSystem` — it is a `MonoBehaviour`, and
    /// `FormatterServices.GetUninitializedObject` refuses it — so the two prefixes are executed against a
    /// STANDALONE dictionary, with the game's own two accessor bodies performed in its place where the
    /// prefix answers "run native": `SetVariable`:253-259 (a two-branch dictionary write; :261-262 raises
    /// `VariableSet` and logs against a Unity context object) and `GetVariable`:272-277 (`TryGetValue`, else
    /// the caller's default). `__instance` is passed null, which is sound only because the decision under
    /// test is reached before any use of it — a future prefix that dereferences it first reports
    /// `capture-threw` rather than passing quietly.
    /// </summary>
    internal static class L327_AViewSettingNeverRidesTheCampaignRail
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        private const string Listed = "TFTV_HelmetsOff";
        private const string Campaign = "PP_L327_StoryFlag";

        internal static IEnumerable<string> Check()
        {
            var modAsm = typeof(EventSync).Assembly;
            var capture = typeof(EventVariableCapture).GetMethod("Prefix", All);
            var read = typeof(EventVariableLocalRead).GetMethod("Prefix", All);
            var vars = typeof(GeoscapeEventSystem).GetField("_customVariables", All);
            var getTwo = typeof(GeoscapeEventSystem).GetMethod("GetVariable", All, null,
                             new[] { typeof(string), typeof(int) }, null);
            var getOne = typeof(GeoscapeEventSystem).GetMethod("GetVariable", All, null,
                             new[] { typeof(string) }, null);
            var send = typeof(IntentRail).GetMethod("Send", All);

            if (capture == null || read == null || vars == null || getTwo == null || getOne == null ||
                send == null || vars.FieldType != typeof(Dictionary<string, int>))
            {
                yield return "L327 premise-changed: one of EventVariableCapture.Prefix, " +
                             "EventVariableLocalRead.Prefix, IntentRail.Send, GeoscapeEventSystem." +
                             "_customVariables (as Dictionary<string,int>) or its two GetVariable overloads " +
                             "no longer resolves. The seam that keeps a per-player view setting out of " +
                             "replicated campaign state has moved, and this law is asserting things about a " +
                             "shape that is not there — re-read it before assuming a helmet toggle still " +
                             "stops at the peer that clicked it.";
                yield break;
            }

            // ── (a) PREMISE, EXECUTED: the 2-arg overload really is the whole read funnel ────────
            // The patch deliberately covers ONE overload. That is only correct while the 1-arg one is the
            // pass-through the decompile shows (:265-268) — otherwise reads slip past the shadow and TFTV
            // reads the replicated value again, which is the whole reported bug wearing a different hat.
            if (!Program.Callees(getOne, typeof(GeoscapeEventSystem).Assembly)
                        .Any(m => m.MetadataToken == getTwo.MetadataToken))
                yield return "L327 read-funnel-split: GeoscapeEventSystem.GetVariable(string) no longer " +
                             "delegates to GetVariable(string,int). Patching the 2-arg overload alone was " +
                             "sound BECAUSE every read funnelled through it; with the funnel split, a caller " +
                             "on the 1-arg path reads the replicated dictionary and the excluded value is " +
                             "shared again with nothing in any log to say so.";

            // ── the listed set is the subject; an empty one makes everything below vacuous ───────
            if (!EventSync.LocalPreferenceVariables.Contains(Listed))
                yield return "L327 subject-unlisted: EventSync.LocalPreferenceVariables no longer names '" +
                             Listed + "'. That is TFTV's helmet toggle (ShowWithoutHelmet.cs:33/:116/:98) — " +
                             "the one campaign-global variable that is really a per-player view setting. " +
                             "Unlisted, one peer's click restyles every soldier on every peer's screen again.";

            var dict = new Dictionary<string, int>();

            // ── (b) OUTCOME: the view setting never enters the state the rail carries ────────────
            string threw = Write(capture, dict, Listed, 1) ?? Write(capture, dict, Campaign, 7);
            if (threw != null) { yield return threw; yield break; }
            if (dict.ContainsKey(Listed))
                yield return "L327 view-setting-lands-in-replicated-state: after toggling '" + Listed +
                             "' the key is in GeoscapeEventSystem._customVariables. That dictionary IS the " +
                             "rail-covered CustomVariables list (docs/rail-baseline.txt:473) and the apply " +
                             "writes it RAW, so a key present on ANY peer is a delta every other peer takes: " +
                             "one player's helmet click restyling every soldier on every screen, including " +
                             "the soldier somebody else has open. No per-peer filter downstream can undo it — " +
                             "the key must never be written anywhere, host and client alike.";

            // ── (c) OUTCOME, the other half: an ordinary campaign flag still becomes shared state ─
            int stored;
            if (!dict.TryGetValue(Campaign, out stored) || stored != 7)
                yield return "L327 campaign-flag-dropped: an ordinary event variable did not reach " +
                             "_customVariables (got " + (dict.ContainsKey(Campaign) ? stored.ToString() : "no key") +
                             ", expected 7). Event variables are how the game and every mod store campaign " +
                             "and story flags; excluding them wholesale is a far larger regression than the " +
                             "one this law exists for, and an exclusion-only law would have been green over it.";
            if (Read(read, dict, Campaign) != 7)
                yield return "L327 campaign-flag-unreadable: reading an ordinary event variable no longer " +
                             "answers the replicated dictionary. The local-preference read is intercepting " +
                             "names it does not own, so every campaign flag now answers this process's own " +
                             "shadow — a peer-divergent campaign with no log line anywhere.";

            // ── (d) OUTCOME: the player's own toggle sticks, which is what stops the flip-flop ───
            if (Read(read, dict, Listed) != 1)
                yield return "L327 local-value-not-read-back: '" + Listed + "' was set to 1 and reads back " +
                             Read(read, dict, Listed) + ". Blocking the write without keeping the value " +
                             "is the FLIP-FLOP verbatim: TFTV re-reads the stale value (ShowWithoutHelmet.cs:" +
                             "88-105), re-applies it (:138) and its own postfix (:264) writes again, forever. " +
                             "The player's own click must also simply stay applied.";
            threw = Write(capture, dict, Listed, 0);
            if (threw != null) { yield return threw; yield break; }
            if (Read(read, dict, Listed) != 0 || dict.ContainsKey(Listed))
                yield return "L327 local-value-not-updated: toggling '" + Listed + "' back to 0 answered " +
                             Read(read, dict, Listed) + " and " + (dict.ContainsKey(Listed) ? "did" : "did not") +
                             " leak into _customVariables. A shadow that only takes the first write is a " +
                             "toggle that can be turned on and never off.";

            // ── (e) …and the client emit the exclusion sits in front of is still there ───────────
            if (!Program.Callees(capture, modAsm).Any(m => m.MetadataToken == send.MetadataToken))
                yield return "L327 event-variable-emit-gone: EventVariableCapture.Prefix no longer reaches " +
                             "IntentRail.Send. A client that writes a campaign variable must still ship it — " +
                             "without the emit its write is silently reverted by the next mirrored diff, which " +
                             "is the exact silent-swallow this capture seam was added to kill. Making the " +
                             "helmet toggle local by deleting the emit would 'fix' this law's other arms.";
        }

        /// <summary>The patched write, end to end: consult the prefix and, where it answers "run native",
        /// perform the dictionary half of GeoscapeEventSystem.SetVariable:253-259 in its place. Returns a
        /// violation rather than throwing — a law that throws aborts the whole run.</summary>
        private static string Write(MethodInfo capture, Dictionary<string, int> dict, string name, int value)
        {
            bool native;
            try { native = (bool)capture.Invoke(null, new object[] { null, name, value }); }
            catch (System.Exception ex)
            {
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                return "L327 capture-threw: EventVariableCapture.Prefix threw for '" + name + "' — " +
                       root.GetType().Name + ": " + root.Message + ". The harness passes __instance null " +
                       "because the local-preference decision is reached before anything uses it; a prefix " +
                       "that now dereferences the system FIRST has moved that decision behind a read of the " +
                       "very dictionary the excluded value must never enter.";
            }
            if (native) dict[name] = value;
            return null;
        }

        /// <summary>The patched read, end to end: the prefix's answer, or GetVariable:272-277's own body
        /// (TryGetValue, else the caller's default) over the same dictionary.</summary>
        private static int Read(MethodInfo read, Dictionary<string, int> dict, string name)
        {
            var args = new object[] { name, 0 };
            if (!(bool)read.Invoke(null, args)) return (int)args[1];
            int v;
            return dict.TryGetValue(name, out v) ? v : 0;
        }
    }
}
