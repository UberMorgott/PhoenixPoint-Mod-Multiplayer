using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L516 — A STRIP THAT IS NOT ON SCREEN NEVER VOUCHES FOR THE ROWS IT DID NOT DRAW.
    ///
    /// THE FAILURE (owner, live 3-peer session 2026-08-15, multiplayer{,-2,-3}.log). The top-right activity
    /// label stopped following RESEARCH on clients while it kept following MANUFACTURING. Same strip, same
    /// signature, same rebuild — so the rebuild was not the thing that broke. What broke is WHEN the strip
    /// was asked.
    ///
    /// <c>OpenUiRepaint.RepaintNeeded</c> is a memory: every key it is handed is recorded as "this strip is
    /// now showing that". <c>RefreshAgendaTracker</c> handed it a key on EVERY rail flush, including the
    /// flushes where <c>UIModuleFactionAgendaTracker</c>'s GameObject was switched off by the game
    /// (<c>UIModuleBehavior.SetStateID</c>:34 deactivates a module for a state it is not part of). The paint
    /// could not land, but the key was remembered. So a research head that changed while the player sat in
    /// <c>UIStateResearch</c> — or behind the research-complete modal, which since dc0a148 is queued from
    /// inside the very apply that changes <c>Research.Current</c> — recorded its signature against an OFF
    /// strip; the first flush after the strip came back compared EQUAL and skipped the rebuild it owed; and
    /// the row sat stale until something ELSE in the signature moved. Manufacturing looked healthy for
    /// exactly that reason: any later change rebuilds the whole strip, and manufacture heads move often.
    ///
    /// This module is the one that cannot be rescued by a re-Init. It SPANS view states — that is the entire
    /// premise of <c>OpenUiRepaint.RefreshPersistentHud</c> — so nothing re-runs <c>Init</c>/<c>InitialSetup</c>
    /// when the state the player left comes back.
    ///
    /// THE RULE, AND IT IS THE ORDER OF TWO OPERATIONS. The visibility question is asked BEFORE the memory
    /// is touched, never after. An off strip must return the same answer twice in a row and must leave the
    /// memory exactly as it found it.
    ///
    /// THE ARMS — every one of them EXECUTES the shipped decision
    /// (<c>OpenUiRepaint.AgendaNeedsRebuild</c>), none of them merely asserts a shape:
    ///   (a) <c>off-strip-rebuilds</c> — an OFF strip must not claim a rebuild. There is nothing on screen
    ///       to rebuild, and claiming one would run <c>InitialSetup</c> over a hidden module every flush.
    ///   (b) <c>off-strip-burns-the-key</c> — THE REPORTED DEFECT, reproduced end to end: ask the OFF strip
    ///       about signature X, then ask the ON strip about the SAME X. The second call MUST rebuild. It
    ///       fails the moment the visibility gate moves after <c>RepaintNeeded</c> instead of before it.
    ///   (c) <c>off-strip-burns-the-key-repeatedly</c> — the same, with the strip asked off many times
    ///       first: no number of hidden flushes may consume the pending rebuild.
    ///   (d) <c>on-strip-changed-skipped</c> / (e) <c>on-strip-unchanged-rebuilds</c> — the gate must not
    ///       have cost L492's two directions: an ON strip still rebuilds on change and still refuses to
    ///       rebuild on no change. Without these the law would be satisfiable by returning a constant.
    ///   (f) <c>gate-unwired</c> — IL, and the ONLY structural arm here, stated as such: the production
    ///       <c>RefreshAgendaTracker</c> must actually read <c>GameObject.activeInHierarchy</c>. The arms
    ///       above prove the DECISION is right; this one proves the production caller feeds it the real
    ///       visibility rather than a hard-coded <c>true</c>, which is the one thing no pure execution of
    ///       <c>AgendaNeedsRebuild</c> can see.
    ///
    /// Falsify (compile-valid src mutations, each named): swap the two lines in
    /// <c>RefreshAgendaTracker</c> so the signature is recorded before the visibility check → (b), (c);
    /// change <c>AgendaNeedsRebuild</c>'s <c>&amp;&amp;</c> to <c>&amp;</c> so <c>RepaintNeeded</c> is
    /// evaluated for an off strip → (b), (c); pass <c>true</c> instead of
    /// <c>tracker.gameObject.activeInHierarchy</c> → (f); return <c>stripOnScreen</c> alone → (e).
    /// </summary>
    internal static class L516_AnOffScreenStripNeverVouchesForItsRows
    {
        internal static IEnumerable<string> Check()
        {
            OpenUiRepaint.Reset();

            if (OpenUiRepaint.AgendaNeedsRebuild(false, "research=A|"))
                yield return "L516 off-strip-rebuilds: a strip the game has switched off claimed it owed a " +
                             "full rebuild. InitialSetup disposes and re-creates every row, so this is the " +
                             "L492 flicker paid on a module nobody can see, ~10 times a second.";

            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=A|"))
                yield return "L516 off-strip-burns-the-key: the strip was asked about a signature while it " +
                             "was OFF SCREEN, and that question was REMEMBERED. The first flush after the " +
                             "strip came back compared EQUAL and skipped the rebuild it owed — the live " +
                             "2026-08-15 report verbatim: the top-right label kept following manufacturing " +
                             "and stopped following research, because the research head moved while the " +
                             "player was inside the research screen. The visibility gate must be asked " +
                             "BEFORE OpenUiRepaint.RepaintNeeded, never after it.";

            OpenUiRepaint.Reset();
            for (int i = 0; i < 25; i++) OpenUiRepaint.AgendaNeedsRebuild(false, "research=B|");
            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=B|"))
                yield return "L516 off-strip-burns-the-key-repeatedly: 25 flushes against an off strip " +
                             "consumed the pending rebuild. A player sitting in a screen produces exactly " +
                             "that — a burst of hidden flushes — and this is the shape in which the defect " +
                             "actually reached the session.";

            if (!OpenUiRepaint.AgendaNeedsRebuild(true, "research=C|"))
                yield return "L516 on-strip-changed-skipped: a VISIBLE strip did not rebuild on a CHANGED " +
                             "signature. The visibility gate has swallowed the reactivity half — a stale " +
                             "strip is a defect in this repo, not a cosmetic issue.";

            if (OpenUiRepaint.AgendaNeedsRebuild(true, "research=C|"))
                yield return "L516 on-strip-unchanged-rebuilds: a VISIBLE strip rebuilt on an UNCHANGED " +
                             "signature. The gate must add a condition, never remove one (L492's flicker).";

            OpenUiRepaint.Reset(); // leave no signature of this law's own behind

            const BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var refresh = typeof(OpenUiRepaint).GetMethod("RefreshAgendaTracker", Any);
            var active = typeof(UnityEngine.GameObject).GetProperty("activeInHierarchy");
            var activeGetter = active == null ? null : active.GetGetMethod(nonPublic: true);
            if (refresh == null || activeGetter == null)
            {
                yield return "L516 premise-changed: OpenUiRepaint.RefreshAgendaTracker or " +
                             "UnityEngine.GameObject.activeInHierarchy did not resolve, so the structural arm " +
                             "cannot see what feeds the gate. Re-point it before believing the verdict.";
                yield break;
            }
            if (!References(refresh, activeGetter))
                yield return "L516 gate-unwired: RefreshAgendaTracker never reads " +
                             "GameObject.activeInHierarchy, so whatever it passes as 'is this strip on " +
                             "screen' is not the strip's real visibility — the executed arms above stay " +
                             "green while the production caller hands the gate a constant. STRUCTURAL, and " +
                             "the only structural arm in this law.";
        }

        /// <summary>Does <paramref name="m"/>'s IL mention <paramref name="callee"/>? Same cross-assembly
        /// token resolve L492 uses: the callee here lives in UnityEngine, so a raw token compare inside the
        /// mod assembly could never match it.</summary>
        private static bool References(MethodBase m, MethodBase callee)
        {
            byte[] il = null;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { }
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
    }
}
