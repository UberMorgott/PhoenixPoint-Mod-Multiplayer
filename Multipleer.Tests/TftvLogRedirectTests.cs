using Multipleer.Util;
using Xunit;

namespace Multipleer.Tests
{
    // Pure path-decision tests for the TFTV per-instance log redirect.
    // The decision is filename-only: primary => unchanged; secondary => "-N" before the
    // extension (consistent with our own multipleer-N.log scheme). No filesystem touched.
    public class TftvLogRedirectTests
    {
        [Fact]
        public void Primary_PathUnchanged()
        {
            // instance 1 (or not secondary) must never be redirected => shared TFTV.log untouched.
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV.log", isSecondary: false, instanceIndex: 1);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV.log", r);
        }

        [Fact]
        public void Primary_EvenWithIndex1_IsUnchanged()
        {
            // Defensive: index 1 is primary even if isSecondary were mis-set.
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV.log", isSecondary: true, instanceIndex: 1);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV.log", r);
        }

        [Fact]
        public void Secondary_Index2_GetsDash2Suffix()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV.log", isSecondary: true, instanceIndex: 2);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-2.log", r);
        }

        [Fact]
        public void Secondary_Index5_GetsDash5Suffix()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV.log", isSecondary: true, instanceIndex: 5);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-5.log", r);
        }

        [Fact]
        public void Secondary_NotSecondaryButIndexHigh_StillRedirects()
        {
            // The gate is "secondary OR index>1"; an index>1 alone means a same-machine extra instance.
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV.log", isSecondary: false, instanceIndex: 3);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-3.log", r);
        }

        [Fact]
        public void Secondary_PreservesDirectoryAndExtension()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(@"C:\a b\sub\Output.txt", isSecondary: true, instanceIndex: 4);
            Assert.Equal(@"C:\a b\sub\Output-4.txt", r);
        }

        [Fact]
        public void Secondary_AlreadySuffixed_IsIdempotentToTargetIndex()
        {
            // Edge: feeding an already "-2" path with index 2 must not become "TFTV-2-2.log".
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV-2.log", isSecondary: true, instanceIndex: 2);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-2.log", r);
        }

        [Fact]
        public void Secondary_ReSuffix_ReplacesPriorSuffixNotStacks()
        {
            // Edge: existing "-2" re-targeted to index 3 => "TFTV-3.log", not "TFTV-2-3.log".
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV-2.log", isSecondary: true, instanceIndex: 3);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-3.log", r);
        }

        [Fact]
        public void NoExtension_StillSuffixes()
        {
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV", isSecondary: true, instanceIndex: 2);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-2", r);
        }

        [Fact]
        public void NullOrEmpty_ReturnedAsIs()
        {
            Assert.Null(TftvLogRedirect.ResolveRedirectedPath(null, isSecondary: true, instanceIndex: 2));
            Assert.Equal("", TftvLogRedirect.ResolveRedirectedPath("", isSecondary: true, instanceIndex: 2));
        }

        [Fact]
        public void HyphenInBaseNameNotDigits_NotMistakenForSuffix()
        {
            // "TFTV-beta.log": the "-beta" part is NOT a numeric instance suffix, so keep it and append.
            var r = TftvLogRedirect.ResolveRedirectedPath(@"D:\PP\Mods\TFTV\TFTV-beta.log", isSecondary: true, instanceIndex: 2);
            Assert.Equal(@"D:\PP\Mods\TFTV\TFTV-beta-2.log", r);
        }
    }
}
