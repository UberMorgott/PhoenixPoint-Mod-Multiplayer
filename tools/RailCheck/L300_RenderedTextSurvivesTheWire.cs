using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Base.UI;
using Multiplayer.Network.Sync;

namespace RailCheck
{
    /// <summary>
    /// L300 — A BIND THE HOST RENDERS AS TEXT ARRIVES AS THAT TEXT ON THE CLIENT.
    ///
    /// WHAT THIS LAW IS ANSWERING. One 3-instance session emitted, from a single warning inside
    /// <c>RailMeta.EncodeLeaf</c>: ×307944 <c>GeoSiteInstaceData.Motto</c>, ×302022
    /// <c>GeoSiteInstaceData.Name</c>, ×7896 <c>GeoPhoenixBase.InstanceData.LocationDescription</c> — each
    /// line predicting that a client's site label would be blank. The prediction was accurate and the defect
    /// was not: <c>LocalizedTextBind.Localize()</c> returns <c>LocalizationKey</c> VERBATIM whenever the key
    /// is empty (decompile Base.UI/LocalizedTextBind.cs:35-42), so those sites render blank on the HOST too,
    /// and an empty bind is the game's own spelling of "no text" (GeoPhoenixBase.cs:1120-1122 constructs one
    /// to mean exactly that; UIModuleSelectionInfoBox.cs:603-610 hides the motto row on it). The warning was
    /// asserting a PROXY — "the key is non-empty" — which is neither necessary nor sufficient for the thing
    /// anyone cares about.
    ///
    /// So this law asserts the OUTCOME, and asserts it by EXECUTING the shipped codec rather than by reading
    /// it: put a bind through <c>EncodeLeaf</c> → <c>DecodeLeaf</c> and ask the two binds what they RENDER.
    /// That is deliberately not a fact we control — it is the same question the geoscape asks when it draws
    /// the label, and it stays true or goes red no matter how the codec is spelled.
    ///
    ///   (a) <c>text-does-not-survive</c> — EXECUTED round-trip. A literal bind (<c>_doNotLocalize</c>, the
    ///       form a runtime-composed name takes) must <c>Localize()</c> to the same string after the wire.
    ///       This arm is why the codec may not drop <c>_doNotLocalize</c>: without the flag the literal comes
    ///       back as a failed key lookup, which is the blank label for real.
    ///   (b) <c>host-language-shipped</c> — the KEY rides, not the host's RESOLVED string. Asserted on the
    ///       decoded bind: same <c>LocalizationKey</c>, still localizable. A client on another language
    ///       resolves that key through ITS OWN LocalizationManager (<c>Localize</c> passes
    ///       <c>language = null</c>, :39) and reads its own translation; had the encoder shipped
    ///       <c>Localize()</c> instead, all eight shipped languages would collapse onto the host's.
    ///   (c) <c>rediagnosed-every-walk</c> — EXECUTED: encode a keyless bind 500 times and the diagnostic
    ///       must have fired ONCE. A leaf that legitimately has nothing to say is not a finding, and
    ///       re-deriving that verdict per walk is what made one benign member the loudest thing in the log.
    ///   (d) POSITIVE CONTROL, EXECUTED — <see cref="FakeSeam.DropsTheText"/> is a codec that loses the
    ///       literal; arm (a)'s comparison is run over it and MUST come back red.
    ///
    /// Falsify (each verified RED, then restored): stop writing <c>BindNoLocalize(bind)</c> in
    /// <c>EncodeLeaf</c> → (a); write <c>bind.Localize()</c> in place of the key → (b); delete the
    /// <c>_keylessBindSeen</c> gate from <c>DiagnoseKeylessBind</c> → (c); make <see cref="FakeSeam"/>
    /// lossless → (d).
    /// </summary>
    internal static class L300_RenderedTextSurvivesTheWire
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            // Read through the generic surface, never `as System.Collections.ICollection`: HashSet<T> does
            // NOT implement the non-generic one, so that cast silently yields null and the law reports
            // "premise-changed" over a premise that is perfectly intact — a false RED that reads like a
            // finding. Count and Clear come off the runtime type instead.
            var gate = typeof(RailMeta).GetField("_keylessBindSeen", All)?.GetValue(null);
            var count = gate?.GetType().GetProperty("Count", All);
            var diagnose = typeof(RailMeta).GetMethod("DiagnoseKeylessBind", All);
            if (diagnose == null || gate == null || count == null)
            {
                yield return "L300 premise-changed: RailMeta.DiagnoseKeylessBind / _keylessBindSeen no longer " +
                             "resolve. Those two ARE the damping — 610k suppressed lines in one session came " +
                             "from the per-walk warning they replaced, and arm (c) reads them to prove a " +
                             "benign leaf is diagnosed once rather than on every walk.";
                yield break;
            }

            // ── (a) EXECUTED: a literal the host renders must render the same after the wire ────────
            foreach (var red in RenderSurvives(new LocalizedTextBind("Camp Zero", doNotLocalize: true),
                                               RailCodec, "L300")) yield return red;

            // ── (d) POSITIVE CONTROL, executed: the same comparison over a codec that DOES lose it ──
            if (!RenderSurvives(new LocalizedTextBind("Camp Zero", doNotLocalize: true),
                                FakeSeam.DropsTheText, "control").Any())
                yield return "L300 control-not-red: FakeSeam.DropsTheText discards the literal and the " +
                             "round-trip comparison did not flag it. Arm (a) is decorative — it would stay " +
                             "green over a codec that ships every runtime-composed name as a blank.";

            // ── (b) the ADDRESS rides, so each peer resolves it in its own language ─────────────────
            var localizable = new LocalizedTextBind("HAVEN_NAME_042");
            LocalizedTextBind wired = null;
            string threw = null;
            // An encoder that resolved the string instead of shipping the address would REACH I2 here, and
            // from a process with no LocalizationManager that is a throw rather than a wrong answer. Same
            // finding either way — and never an abort, which would prove nothing (L193).
            try { wired = RailCodec(localizable); }
            catch (Exception ex) { threw = ex.GetType().Name; }
            if (threw != null)
                yield return "L300 host-language-shipped: encoding a localizable bind threw " + threw +
                             ". The codec now depends on RESOLVING the text at encode time; the key is an " +
                             "address and must ride untouched.";
            else if (wired == null || wired.LocalizationKey != localizable.LocalizationKey)
                yield return "L300 host-language-shipped: a localizable bind came back with LocalizationKey='" +
                             (wired?.LocalizationKey ?? "<null>") + "' instead of '" + localizable.LocalizationKey +
                             "'. The key is an ADDRESS and must ride as one: Localize() is called with " +
                             "language=null on the RECEIVING peer (LocalizedTextBind.cs:39), so shipping the " +
                             "host's resolved string instead would pin every client to the host's language and " +
                             "silently delete seven of the eight this mod ships.";
            else if (ReadNoLocalize(wired))
                yield return "L300 host-language-shipped: the decoded bind carries _doNotLocalize over a real " +
                             "LocalizationKey, so the client will print the raw key instead of translating it " +
                             "(the flag short-circuits the lookup, LocalizedTextBind.cs:37). A key that arrives " +
                             "un-localizable is the host's language winning by another route.";

            // ── (c) EXECUTED: a benign keyless leaf is diagnosed ONCE, not once per walk ────────────
            // Driven through the REAL EncodeLeaf, not the private diagnostic, so this measures the shipped
            // walk. The first call may reach UnityEngine.Debug from a console process; the gate is taken
            // BEFORE that line either way, so a throw there cannot fake the arm green — it would leave the
            // count at 1 having genuinely run once, which is the property under test.
            var clear = gate.GetType().GetMethod("Clear", All);
            var tally = typeof(RailMeta).GetField("_tally", All)?.GetValue(null);
            var tallyCount = tally?.GetType().GetProperty("Count", All);
            var tallyClear = tally?.GetType().GetMethod("Clear", All);
            if (tallyCount == null)
            {
                yield return "L300 premise-changed: RailMeta._tally no longer resolves, so the half of arm (c) " +
                             "that proves a benign leaf emits NO warn family cannot run.";
                yield break;
            }
            clear.Invoke(gate, null); tallyClear.Invoke(tally, null);
            var keyless = new LocalizedTextBind();
            for (int i = 0; i < 500; i++)
                try { RailCodec(keyless); } catch { }
            int fired = (int)count.GetValue(gate);
            int families = (int)tallyCount.GetValue(tally);
            clear.Invoke(gate, null); tallyClear.Invoke(tally, null);
            if (fired != 1)
                yield return "L300 rediagnosed-every-walk: 500 encodes of one keyless bind left " + fired +
                             " gate entrie(s) instead of 1. The gate is keyed on the RailField being walked " +
                             "precisely so naming the member (EncodingWhere → reflection + concatenation) and " +
                             "asking I2 what it renders happen ONCE. Ungated, this exact leaf produced 307944 " +
                             "lines for GeoSiteInstaceData.Motto alone in a single session — a member that has " +
                             "nothing to report must stop reporting it, without going silent about members " +
                             "that do.";
            if (families != 0)
                yield return "L300 rediagnosed-every-walk: encoding a bind the host renders as NOTHING opened " +
                             families + " miss-tally family(ies). A leaf carried faithfully is not a mirror " +
                             "gap, and routing it through WarnOnce is what put ×307944 / ×302022 / ×7896 into " +
                             "one session's digests — three families that described the rail working. The " +
                             "warn path is reserved for a bind whose text is actually being dropped.";
        }

        /// <summary>THE SHIPPED CODEC, exercised end to end — the real <c>EncodeLeaf</c> bytes read back by
        /// the real <c>DecodeLeaf</c>. <c>geo</c> is null because the TextBind case never consults the level
        /// (only <c>LeafKind.EntityRef</c> does).</summary>
        private static LocalizedTextBind RailCodec(LocalizedTextBind src)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    RailMeta.EncodeLeaf(w, typeof(LocalizedTextBind), src);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                    return RailMeta.DecodeLeaf(r, typeof(LocalizedTextBind), null) as LocalizedTextBind;
            }
        }

        /// <summary>The comparison, run over the shipped codec in arm (a) and over <see cref="FakeSeam"/> in
        /// arm (d) — same code both times, which is what makes the control a control. It asks
        /// <c>Localize()</c>, not the fields: what the player sees is the whole property.</summary>
        private static IEnumerable<string> RenderSurvives(LocalizedTextBind src,
                                                          Func<LocalizedTextBind, LocalizedTextBind> codec,
                                                          string id)
        {
            var before = src.Localize();
            string shown;
            // A LOST literal is exactly a bind that has become a key lookup, and a lookup reaches I2 from a
            // process with no loaded LocalizationManager — so the failure mode under test can throw rather
            // than return the wrong string. Both are "the text did not survive"; neither may be an abort.
            try { shown = codec(src)?.Localize(); }
            catch (Exception ex) { shown = "<threw " + ex.GetType().Name + ">"; }
            if (shown != before)
                yield return id + " text-does-not-survive: the host renders \"" + before + "\" and the client " +
                             "would render \"" + (shown ?? "<null>") + "\". A LocalizedTextBind is two members " +
                             "(LocalizationKey + _doNotLocalize, LocalizedTextBind.cs:11/:13) and BOTH decide " +
                             "the rendered string — drop the flag and a runtime-composed literal arrives as a " +
                             "failed key lookup, which is the blank site label the log spent 610k lines " +
                             "predicting for binds that never had the problem.";
        }

        private static bool ReadNoLocalize(LocalizedTextBind b) =>
            (bool)(typeof(LocalizedTextBind).GetField("_doNotLocalize", All)?.GetValue(b) ?? false);

        private static class FakeSeam
        {
            /// <summary>THE POSITIVE CONTROL: a codec on the shape of the real one that keeps the key and
            /// LOSES <c>_doNotLocalize</c> — the single most likely way to break this for real. Arm (a)'s
            /// comparison MUST flag it, or it is not comparing.</summary>
            internal static LocalizedTextBind DropsTheText(LocalizedTextBind src) =>
                new LocalizedTextBind(src.LocalizationKey, doNotLocalize: false);
        }
    }
}
