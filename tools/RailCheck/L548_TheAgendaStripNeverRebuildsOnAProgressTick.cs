using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L548 — A PROGRESS-ONLY DELTA NEVER REBUILDS THE TOP-RIGHT AGENDA STRIP.
    ///
    /// THE FAILURE (client, reported 2026-08-15, regression of the 2026-08-14 flicker L492 fixed).
    /// The top-right strip blinked at ~1 Hz on CLIENTS and was steady on the host. Live pair in
    /// <c>multiplayer-2.log</c>, period ≈1.0 s: <c>ManufactureSync CLIENT applied queue count=1</c>
    /// followed by <c>[MP][uirepaint] native rebuild</c>. A manufacture COUNTDOWN ticking is a value-only
    /// delta — the SET OF ROWS is unchanged — yet the strip tore every row down and re-created it.
    ///
    /// THE ROOT CAUSE, and why L492 stayed green through it. §B.8 (249e963) replaced the strip's row
    /// identity key with <c>OpenUiRepaint.ScopeKey("UIModuleFactionAgendaTracker")</c>, a GENERATION that
    /// advances whenever any path under the strip's declared prefixes ("F#", "S#", "V#") was touched.
    /// Manufacture progress lands under "F#" about once a second on a client, so the key moved every
    /// second and <c>AgendaNeedsRebuild</c> — which is exactly right about its own argument — answered
    /// "rebuild" every second. L492 executes that gate with hand-written strings, so it cannot see WHICH
    /// key the runtime hands it; the defect lives entirely at the CALL SITE.
    ///
    /// THE RULE, and the distinction this law exists to pin. SCOPE decides WHETHER the strip is looked at.
    /// IDENTITY decides whether it is TORN DOWN. A generation is a legitimate scope answer and an
    /// illegitimate teardown answer, because it moves on values as well as on rows. The per-row time text
    /// is not this law's business: <c>UIModuleFactionAgendaTracker.UpdateData()</c>:191-198 repaints every
    /// live element on EVERY call, rebuild or not, so refusing the rebuild costs no freshness at all —
    /// while <c>InitialSetup</c>:144-177 disposes and re-creates every row (:148
    /// <c>EnsureActiveComponentsInContainer(..., 0)</c> empties the container first), which is the blink.
    ///
    /// ARMS:
    ///   (a) <c>rebuild-keyed-on-a-generation</c> — IL: <c>RefreshAgendaTracker</c> must NOT reach
    ///       <c>ScopeKey</c>. This is the reported defect, stated where it lives.
    ///   (b) <c>identity-builder-unwired</c> — IL: it MUST reach a row-IDENTITY builder
    ///       (<c>AgendaSignature</c>), so arm (a) cannot be satisfied by keying on a constant.
    ///   (c) <c>progress-tick-rebuilds</c> — EXECUTED end to end on the real gate and the real generation
    ///       machinery: the same row identity, twice, with a real <c>BumpScopeGenerations</c> over a
    ///       manufacture path in between. The second call must NOT rebuild. This is the property
    ///       nobody asserted: a moved generation must not, by itself, imply a teardown.
    ///   (d) <c>row-change-skipped</c> — EXECUTED, the reactivity direction: a CHANGED row identity must
    ///       rebuild. Without it the whole law is satisfied by never rebuilding at all, which trades the
    ///       flicker for a stale strip — a defect, not a cosmetic issue, in this repo.
    ///
    /// L543 KEEPS ITS SUBJECT: the four repaint KEYS it deleted stay deleted. The agenda strip's identity
    /// builder is not a repaint key — it is the teardown gate this law requires — and L543 now names it as
    /// its own positive control, so the two laws cannot both be satisfied by deleting the mechanism.
    ///
    /// Falsify (compile-valid src mutations): key the rebuild on <c>ScopeKey</c> again → (a) and (b);
    /// <c>AgendaNeedsRebuild</c> => <c>stripOnScreen</c> → (c); => <c>false</c> → (d).
    /// </summary>
    internal static class L548_TheAgendaStripNeverRebuildsOnAProgressTick
    {
        internal static IEnumerable<string> Check()
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var repaint = typeof(OpenUiRepaint);
            var refresh = repaint.GetMethod("RefreshAgendaTracker", Any);
            var scopeKey = repaint.GetMethod("ScopeKey", Any);
            var identity = repaint.GetMethod("AgendaSignature", Any);
            if (refresh == null || scopeKey == null)
            {
                yield return "L548 premise-changed: OpenUiRepaint.RefreshAgendaTracker / .ScopeKey did not " +
                             "resolve, so this law cannot see the rebuild decision it guards. Re-point it " +
                             "before believing any verdict below.";
                yield break;
            }

            // (a) THE REPORTED DEFECT: the teardown decision keyed on a touched-path generation.
            if (References(refresh, scopeKey))
                yield return "L548 rebuild-keyed-on-a-generation: RefreshAgendaTracker computes its rebuild " +
                             "key from OpenUiRepaint.ScopeKey. That generation advances on ANY touched path " +
                             "under the strip's declared prefixes — a manufacture countdown ticking under " +
                             "\"F#\" moves it about once a second on a client — so the strip takes " +
                             "InitialSetup's full row teardown on a value-only delta. That is the 1 Hz " +
                             "client flicker of 2026-08-15, and it is the 2026-08-14 flicker L492 already " +
                             "fixed once. Scope decides WHETHER the strip is looked at; only a change in the " +
                             "SET OF ROWS may tear it down.";

            // (b) AND IT IS AN IDENTITY BUILDER THAT REPLACED IT, not a constant.
            if (identity == null || !References(refresh, identity))
                yield return "L548 identity-builder-unwired: RefreshAgendaTracker does not reach a row-IDENTITY " +
                             "builder (OpenUiRepaint.AgendaSignature). The rebuild is owed to a change in the " +
                             "SET OF ROWS InitialSetup:150-176 builds — the manufacture head, the research " +
                             "head, the vehicles with a current action and the facilities building or " +
                             "repairing — so something must compute that set. Without it arm (a) is satisfied " +
                             "by a key that never moves, i.e. a strip that never rebuilds.";

            // (c) EXECUTED: the real gate, driven with the real generation machinery moving underneath it.
            OpenUiRepaint.Reset();
            const string rows = "manufacture=Gun|research=RES_A|vehicles=|facilities=";
            if (!OpenUiRepaint.AgendaNeedsRebuild(true, rows))
                yield return "L548 first-paint-skipped: the FIRST row identity of a session did not rebuild " +
                             "the strip, so a client joining mid-campaign paints an inherited row set.";
            OpenUiRepaint.BumpScopeGenerations(
                new List<string> { "F#7a3.SerializationData.Manufacture.Current.Progress" });
            if (OpenUiRepaint.AgendaNeedsRebuild(true, rows))
                yield return "L548 progress-tick-rebuilds: the strip rebuilt although its ROW IDENTITY was " +
                             "unchanged and only a manufacture progress path had been touched. InitialSetup " +
                             "disposes and re-creates every row (:148 EnsureActiveComponentsInContainer with " +
                             "count 0 empties the container first, and the disposed rows drain a tick later " +
                             "at :181-185) — that is the visible blink, at the rate the value ticks. The " +
                             "countdown text needs no rebuild: UpdateData() repaints every live element on " +
                             "every call.";

            // (d) THE REACTIVITY DIRECTION: a real row change still tears down and re-creates.
            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "manufacture=Gun|research=RES_B|vehicles=|facilities="))
                yield return "L548 row-change-skipped: a CHANGED row identity did not rebuild the strip. A " +
                             "research starting, a manufacture finishing or a vehicle departing on any peer " +
                             "must appear on every other peer's ALREADY-OPEN strip immediately — trading the " +
                             "flicker for a stale strip is not a fix, it is the defect this repaint layer " +
                             "exists to kill.";
            OpenUiRepaint.Reset(); // leave no key of this law's own behind
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Same-assembly scan:
        /// both methods live on <c>OpenUiRepaint</c>, so a raw token comparison is exact.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
            if (il == null || callee == null) return false;
            for (int i = 0; i + 4 <= il.Length; i++)
                if (BitConverter.ToInt32(il, i) == callee.MetadataToken && m.Module == callee.Module)
                    return true;
            return false;
        }
    }
}
