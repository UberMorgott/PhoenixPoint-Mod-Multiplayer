using System;
using System.IO;

namespace Multipleer.Util
{
    /// <summary>
    /// Pure path-decision logic for redirecting TFTV's log FILENAME to a per-instance name when we
    /// are a secondary same-machine instance (the local 2-instance co-op test rig that shares the TFTV
    /// mod folder via a junction). Two PP instances pointed at the same TFTV folder otherwise both open
    /// the single TFTV.log and clobber each other; giving the secondary instance its own TFTV-2.log
    /// keeps the shared folder/configs/saves intact while separating only the log file.
    ///
    /// This type is Unity-free (System.IO only) so it can be unit-tested directly. The actual lock
    /// probe (<see cref="ProbePrimaryLocked"/>) is the ONLY impure part and is kept out of the pure
    /// <see cref="ResolveRedirectedPath"/> decision so the latter is deterministic and testable.
    /// </summary>
    public static class TftvLogRedirect
    {
        // Mirrors MultipleerLog.MaxInstances: instance 1 = primary (unsuffixed), 2..5 = suffixed.
        public const int MaxInstances = 5;

        /// <summary>
        /// Pure decision: given the original TFTV log path, decide the per-instance path.
        /// Primary (not secondary AND index &lt;= 1) => returned unchanged (shared TFTV.log).
        /// Secondary (isSecondary OR index &gt; 1) => insert "-N" before the extension, where N is the
        /// instance index (e.g. TFTV.log => TFTV-2.log), consistent with our multipleer-N.log scheme.
        /// Any pre-existing numeric "-K" suffix on the base name is replaced (not stacked) so the
        /// decision is idempotent and tolerant of already-suffixed inputs.
        /// </summary>
        public static string ResolveRedirectedPath(string originalPath, bool isSecondary, int instanceIndex)
        {
            if (string.IsNullOrEmpty(originalPath))
                return originalPath;

            // Gate: index 1 is always primary; redirect only when flagged secondary or index advanced.
            if (instanceIndex <= 1 && !isSecondary)
                return originalPath;
            if (instanceIndex <= 1)
                return originalPath; // defensive: secondary with no advanced index => no safe suffix to use.

            var dir = Path.GetDirectoryName(originalPath);
            var name = Path.GetFileNameWithoutExtension(originalPath);
            var ext = Path.GetExtension(originalPath); // includes leading '.', "" when none

            var baseName = StripNumericSuffix(name);
            var newFile = baseName + "-" + instanceIndex + ext;

            return string.IsNullOrEmpty(dir) ? newFile : Path.Combine(dir, newFile);
        }

        // Remove a trailing "-<digits>" (our own instance suffix) so re-targeting replaces rather than
        // stacks. A non-numeric trailing "-token" (e.g. "TFTV-beta") is NOT a suffix and is preserved.
        private static string StripNumericSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var dash = name.LastIndexOf('-');
            if (dash <= 0 || dash == name.Length - 1)
                return name;

            for (var i = dash + 1; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                    return name; // trailing token is not purely digits => keep as part of the base name.
            }
            return name.Substring(0, dash);
        }

        /// <summary>
        /// Impure gate: is the canonical PRIMARY lock path already held by an earlier instance?
        /// Tries to open <paramref name="primaryLockPath"/> with exclusive write (FileShare.None).
        /// Returns true (=&gt; we are the secondary) ONLY on a sharing/IO violation; opens-and-closes
        /// cleanly =&gt; false (=&gt; we are the primary, OR a real separate-machine peer whose primary
        /// path is its OWN unlocked file). This is init-order-independent and is the same lock
        /// semantics MultipleerLog.Init already relies on, hoisted into a standalone synchronous probe.
        /// Stays INERT for a single instance and for real cross-machine co-op (no local lock contention).
        /// </summary>
        public static bool ProbePrimaryLocked(string primaryLockPath)
        {
            if (string.IsNullOrEmpty(primaryLockPath))
                return false;

            try
            {
                using (new FileStream(primaryLockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    // Opened exclusively => nobody else holds it => we are NOT a secondary instance.
                    return false;
                }
            }
            catch (IOException)
            {
                // Sharing violation => an earlier same-machine instance holds it => we are secondary.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Treat permission failures conservatively as "not secondary" — never redirect on doubt.
                return false;
            }
        }

        /// <summary>
        /// Find the lowest free instance index (2..MaxInstances) for the secondary log by probing
        /// the candidate redirected paths with the same exclusive-open lock test. Returns the first
        /// index whose target file is openable; defaults to 2 if none is free (degrade gracefully —
        /// worst case two secondaries share one fallback file, never the primary).
        /// </summary>
        public static int ResolveSecondaryIndex(string originalPath)
        {
            for (var i = 2; i <= MaxInstances; i++)
            {
                var candidate = ResolveRedirectedPath(originalPath, isSecondary: true, instanceIndex: i);
                if (string.IsNullOrEmpty(candidate))
                    return i;
                try
                {
                    using (new FileStream(candidate, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                        return i; // this suffix is free for us.
                }
                catch (IOException)
                {
                    // held by yet another instance — advance.
                }
                catch (UnauthorizedAccessException)
                {
                    return i;
                }
            }
            return 2;
        }
    }
}
