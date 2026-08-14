using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RailCheck
{
    /// <summary>
    /// L346 — THE ONE SEAM THAT RUNS INSIDE A NATIVE COROUTINE CONTAINS ITS OWN THROW.
    ///
    /// THE DEFECT (2026-08-08, the shipped DLL): an NRE in the environment-damage capture (L345) did not lose a
    /// wall — it lost the SESSION. <c>DestructableDamageReceiver.ApplyDamage</c> is called from inside a native
    /// effect coroutine (<c>ExplosionEffect.ApplicationComplete</c> ← <c>MultiTargetEffect.OnApply</c> ←
    /// <c>ApplyDamageEffectAbility+&lt;ApplyDamageEffectCrt&gt;</c>), so a throw in our postfix propagates out of
    /// the iterator and Unity logs <c>Broken coroutine call chain</c>. <c>PlayingAction+&lt;CompleteAction&gt;</c>
    /// never runs: the acting soldier stays mid-action forever, the HUD is never re-shown, and the host's last UI
    /// line is <c>Exiting UI state UIStateCharacterSelected. Stack contents: StateStack:</c> — an empty stack, no
    /// input, nothing to click. Unrecoverable.
    ///
    /// WHY ONLY HERE, and this narrowness is the point. Every other seam we own (TacticalEntry, ClientSimGate,
    /// TacticalActorDrive, TacticalInventorySync, ~180 more) runs on the game's ordinary call stack, where our
    /// throw costs the one operation and is VISIBLE. Blanket-wrapping those would convert this project's loudest
    /// failures into its dominant bug class — the silent swallow. This one is wrapped because a throw here costs
    /// the game loop, not the operation.
    ///
    /// STRUCTURAL, and stated as such: making the inner capture actually throw needs a live
    /// <c>DestructableDamageReceiver</c> and a running engine, neither of which RailCheck can construct outside
    /// the game. So the outcome — "the postfix returns normally when the capture throws" — is asserted as the IL
    /// that produces it: the call sits inside a try whose handler catches everything and SAYS SO.
    ///
    /// ARMS
    ///   (a) <c>throw-escapes-the-coroutine</c> — the call to the capture is inside a try region with a catch
    ///       clause of type <c>System.Exception</c> (or a filterless catch-all).
    ///   (b) <c>containment-is-silent</c>, THE NARROWNESS — the handler LOGS. A bare <c>catch { }</c> keeps the
    ///       coroutine alive and hides a capture that has stopped relaying anything, which is how a broken arc
    ///       survives a whole session unnoticed.
    ///
    /// Falsify: delete the try/catch (restore the expression-bodied postfix) → <c>throw-escapes-the-coroutine</c>;
    /// empty the catch body → <c>containment-is-silent</c>. Both verified RED, then restored.
    /// </summary>
    internal static class L346_TheCoroutineResidentSeamContainsItsOwnThrow
    {
        internal static IEnumerable<string> Check()
        {
            var seam = typeof(Multiplayer.Tactical.TacticalDestruction).Assembly
                .GetType("Multiplayer.Tactical.EnvironmentDamageSeam", false);
            var post = seam?.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            var capture = typeof(Multiplayer.Tactical.TacticalDestruction)
                .GetMethod("OnEnvironmentDamage", BindingFlags.NonPublic | BindingFlags.Static);
            MethodBody body = null;
            try { body = post?.GetMethodBody(); } catch { }
            if (seam == null || post == null || capture == null || body == null)
            {
                yield return "L346 premise-changed: " +
                             (seam == null ? "EnvironmentDamageSeam" : post == null ? "its Postfix" :
                              capture == null ? "TacticalDestruction.OnEnvironmentDamage" : "the postfix's IL body") +
                             " no longer resolves. The capture on DestructableDamageReceiver.ApplyDamage is the one " +
                             "seam we own that runs inside a native effect coroutine; if it moved, move this law " +
                             "with it — an unguarded throw there costs the host the whole mission.";
                yield break;
            }

            int callAt = OffsetOfCall(post, capture);
            if (callAt < 0)
            {
                yield return "L346 premise-changed: the postfix no longer calls OnEnvironmentDamage, so there is " +
                             "nothing here to contain and both arms below would pass vacuously. Whatever the seam " +
                             "runs inside that coroutine now is what needs the containment.";
                yield break;
            }

            var clauses = body.ExceptionHandlingClauses ?? (IList<ExceptionHandlingClause>)new ExceptionHandlingClause[0];
            var catcher = clauses.FirstOrDefault(c =>
                c.Flags == ExceptionHandlingClauseOptions.Clause &&
                callAt >= c.TryOffset && callAt < c.TryOffset + c.TryLength &&
                (c.CatchType == null || c.CatchType == typeof(Exception)));

            if (catcher == null)
            {
                yield return "L346 throw-escapes-the-coroutine: the call to OnEnvironmentDamage at IL_" +
                             callAt.ToString("X4") + " is not inside a try with a catch-all (" + clauses.Count +
                             " handling clause(s) on the postfix). A throw there breaks the native effect " +
                             "coroutine's chain, so PlayingAction+<CompleteAction> never runs: the actor stays " +
                             "mid-action forever, the HUD never returns and the host's UI state stack is left " +
                             "empty. Losing the relay of one wall is a defect; losing the game loop is the end of " +
                             "the session.";
                yield break;
            }

            // ── (b) IT MUST SAY SO ───────────────────────────────────────────────────────────────────
            bool speaks = CalleesBetween(post, catcher.HandlerOffset, catcher.HandlerOffset + catcher.HandlerLength)
                .Any(m => m.DeclaringType == typeof(Multiplayer.MpLog) &&
                          (m.Name == "LogError" || m.Name == "LogWarning" || m.Name == "LogException" || m.Name == "Log"));
            if (!speaks)
                yield return "L346 containment-is-silent: the postfix's catch swallows without logging. The " +
                             "coroutine survives, which is what this containment is for — but a capture that has " +
                             "started throwing on every hit then relays nothing while the mission looks healthy, " +
                             "and every wall the host breaks stays solid on every other peer with no line saying " +
                             "why. Silence is this project's dominant failure mode; the catch has to name itself.";
        }

        private static int OffsetOfCall(MethodBase caller, MethodBase target)
        {
            byte[] il;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return -1;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return i;
            }
            return -1;
        }

        private static IEnumerable<MethodBase> CalleesBetween(MethodBase caller, int from, int to)
        {
            byte[] il;
            try { il = caller.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) yield break;
            for (int i = from; i + 4 < il.Length && i < to; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null) yield return c;
            }
        }
    }
}
