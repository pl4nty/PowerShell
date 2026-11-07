// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Management.Automation.Security;
using System.Text;
using System.Threading.Tasks;

namespace System.Management.Automation
{
    /// <summary>
    /// Parses (and optionally compiles) scripts that interactive startup is about to run, such as the profiles,
    /// on background threads, leaving them in the script block cache where running the file finds them.
    /// That moves first-time parse and compile work off the startup path,
    /// overlapping it with runspace creation and module imports.
    /// </summary>
    /// <remarks>
    /// Only the parse and compile move. Execution policy, file policy and AMSI decisions are unchanged:
    /// the policy checks still run when the script runs, and compiling runs the same AMSI scan, just earlier.
    /// The cache is keyed on the file's path and contents, so a file that changes in between isn't affected.
    /// </remarks>
    internal static class StartupScriptCache
    {
        /// <summary>
        /// Parses <paramref name="path"/> on a background thread, then compiles it the way it will run if
        /// <paramref name="optimized"/> is given (dot-sourced scripts such as profiles run unoptimized; module
        /// manifests are evaluated optimized).
        /// </summary>
        internal static void Preload(string path, bool? optimized)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    // Under WDAC or AppLocker enforcement, language mode affects parsing; leave those to the normal path.
                    if (SystemPolicy.GetSystemLockdownPolicy() != SystemEnforcementMode.None || !File.Exists(path))
                    {
                        return;
                    }

                    // Read the way ExternalScriptInfo does, so the cache key matches.
                    string contents;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    using (var reader = new StreamReader(stream, Encoding.Default))
                    {
                        contents = reader.ReadToEnd();
                    }

                    if (ScriptBlock.TryGetCachedScriptBlock(path, contents) is not null)
                    {
                        return;
                    }

                    ScriptBlockAst ast = new Parser().Parse(path, contents, tokenList: null, out ParseError[] errors, ParseMode.Default);
                    if (errors.Length != 0)
                    {
                        // Leave the errors to be reported when the script runs.
                        return;
                    }

                    var scriptBlock = new ScriptBlock(ast, isFilter: false);
                    if (optimized is bool optimize)
                    {
                        scriptBlock.Compile(optimize);
                    }

                    ScriptBlock.CacheScriptBlock(scriptBlock, path, contents);

                    // Running a script checks it against SAFER (AppLocker and software restriction policies), and the
                    // first check loads the trust providers and catalogs to check its signature. Warm that here;
                    // the result is discarded, and the check still runs when the script does.
                    if (path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = SecuritySupport.GetSaferPolicy(path, handle: null);
                    }
                }
                catch (Exception)
                {
                    // Anything that fails here fails again, and is reported, when the script runs.
                }
            });
        }
    }
}
