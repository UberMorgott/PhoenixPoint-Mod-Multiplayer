using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L345 — A DESTRUCTIBLE THE HOST CANNOT NAME IS REFUSED, NOT THROWN ON.
    ///
    /// THE DEFECT (shipped in the deployed DLL, 2026-08-08): commit 552a331 removed the capture's
    /// <c>if (string.IsNullOrEmpty(guid)) { ...; return; }</c> and left the length test that followed it.
    /// <c>TacticalDestruction.GuidOf</c> answers NULL for a destructible whose <c>GuidInScene</c> is invalid or
    /// throws — 59 of 2768 objects on the map that session — and the collided-guid line short-circuits on null,
    /// so <c>guid.Length</c> dereferenced it. Every explosion that touched one of those 59 raised an NRE.
    ///
    /// WHY THAT IS UNRECOVERABLE, and why this law is about the KEY rather than about the exception: the throw
    /// happens inside a native effect coroutine, so it does not merely lose the capture — it breaks the chain
    /// (<c>ApplyDamageEffectCrt</c> / <c>WaitingForCameraBlendingAction</c> / <c>CompleteAction</c>),
    /// <c>CompleteAction</c> never runs, the acting soldier stays mid-action forever and the HUD is never
    /// re-shown. The host's last UI line is an empty state stack. L346 contains the throw; THIS law is what
    /// stops it being raised, which is the half that keeps the hit's refusal NAMED instead of silent.
    ///
    /// WHAT IT ASSERTS IS THE OUTCOME the capture needs: the key an unnameable destructible produces is a
    /// measurable empty string, so the refusal branch (<c>guid.Length == 0 &amp;&amp; posTag.Length == 0</c> →
    /// the named <c>envkey-</c> line) is REACHED and the capture returns normally.
    ///
    /// ARMS
    ///   (a) <c>unnameable-key-is-null</c> — null / empty / not-in-index raw guids all yield "", and measuring
    ///       the result throws nothing. This is the defect itself.
    ///   (b) <c>every-key-blanked</c>, THE NARROWNESS — a readable guid that the index KNOWS passes through
    ///       unchanged. A fix that blanked every key would satisfy (a) and silently stop relaying all
    ///       environment damage, which is the mission-wide failure the whole arc exists to prevent.
    ///   (c) <c>capture-bypasses-the-key</c>, structural — <c>OnEnvironmentDamage</c> still routes its guid
    ///       through <c>EnvGuidKey</c>. Without this, recomputing the key inline at the call site would restore
    ///       the crash with both arms above still green.
    ///
    /// Falsify: make <c>EnvGuidKey</c> return <c>rawGuid</c> for the unreadable case (the pre-fix behaviour) →
    /// <c>unnameable-key-is-null</c>; delete the call from <c>OnEnvironmentDamage</c> →
    /// <c>capture-bypasses-the-key</c>.
    /// </summary>
    internal static class L345_AnUnnameableDestructibleIsRefusedNotThrownOn
    {
        internal static IEnumerable<string> Check()
        {
            var t = typeof(TacticalDestruction);
            var key = t.GetMethod("EnvGuidKey", BindingFlags.NonPublic | BindingFlags.Static);
            var capture = t.GetMethod("OnEnvironmentDamage", BindingFlags.NonPublic | BindingFlags.Static);
            if (key == null || capture == null)
            {
                yield return "L345 premise-changed: TacticalDestruction." +
                             (key == null ? "EnvGuidKey" : "OnEnvironmentDamage") + " no longer resolves, so the " +
                             "arms below would execute nothing. The capture's key is what decides between a named " +
                             "refusal and an NRE inside a native effect coroutine — re-point this law at whatever " +
                             "computes it now; do not delete it.";
                yield break;
            }

            // ── (a) THE DEFECT: an unnameable destructible must produce a MEASURABLE key ─────────────
            foreach (var probe in new[]
            {
                new { Raw = (string)null, Known = false, What = "GuidInScene unreadable (GuidOf returned null)" },
                new { Raw = (string)null, Known = true,  What = "null guid that the index somehow claims to know" },
                new { Raw = "",           Known = false, What = "empty GuidInScene" },
                new { Raw = "abc",        Known = false, What = "readable guid the index dropped as collided" },
            })
            {
                string got;
                Exception threw = null;
                try { got = TacticalDestruction.EnvGuidKey(probe.Raw, probe.Known); }
                catch (Exception ex) { got = null; threw = ex; }

                // The capture's own next move, executed here rather than described: it MEASURES the key.
                bool measurable = false;
                if (threw == null) { try { measurable = got.Length == 0; } catch (Exception ex) { threw = ex; } }

                if (threw != null || !measurable)
                    yield return "L345 unnameable-key-is-null: with " + probe.What + " the capture's key is " +
                                 (threw != null ? threw.GetType().Name + " when measured" : "\"" + got + "\"") +
                                 " instead of a measurable empty string. The capture reads guid.Length on the very " +
                                 "next line, so this is an NRE raised inside a native effect coroutine: the chain " +
                                 "breaks, CompleteAction never runs, the acting soldier never finishes its action " +
                                 "and the host's HUD never comes back. That hit had a name for its refusal — " +
                                 "'envkey-<object>' — and this turns it into a dead session instead.";
            }

            // ── (b) NARROWNESS: a key the index KNOWS is not blanked ─────────────────────────────────
            string kept = null;
            try { kept = TacticalDestruction.EnvGuidKey("wall-7", true); } catch { }
            if (kept != "wall-7")
                yield return "L345 every-key-blanked: a readable guid the index knows came back as \"" +
                             (kept ?? "<threw>") + "\" instead of itself. Refusing to name a destructible that CAN " +
                             "be named drops its damage on the wire, and every wall the host breaks stays solid on " +
                             "every other peer for the rest of the mission — the v1 failure (fc661b7) that L69 and " +
                             "this whole arc exist to keep impossible.";

            // ── (c) STRUCTURAL: the capture still goes through it ────────────────────────────────────
            // Executed as IL rather than as a value because the capture needs a live DestructableDamageReceiver
            // and a running engine, neither of which RailCheck can construct outside the game.
            if (!Calls(capture, key))
                yield return "L345 capture-bypasses-the-key: OnEnvironmentDamage no longer calls EnvGuidKey, so " +
                             "the two arms above now measure a helper nothing uses. The crash comes back the moment " +
                             "the call site derives the guid itself again — that is exactly how it arrived (552a331 " +
                             "deleted the guard and left the length test that needed it).";
        }

        private static bool Calls(MethodBase caller, MethodBase target)
        {
            byte[] il;
            try { il = caller?.GetMethodBody()?.GetILAsByteArray(); } catch { il = null; }
            if (il == null) return false;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;      // call / callvirt
                MethodBase c = null;
                try { c = caller.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); } catch { }
                if (c != null && c.MetadataToken == target.MetadataToken && c.Module == target.Module) return true;
            }
            return false;
        }
    }
}
