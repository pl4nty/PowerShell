// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.ComponentModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Tracing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell
{
    /// <summary>
    /// Defines an entry point from unmanaged code to PowerShell.
    /// </summary>
    public sealed class UnmanagedPSEntry
    {
        /// <summary>
        /// Starts PowerShell.
        /// </summary>
        /// <param name="consoleFilePath">
        /// Deprecated: Console file used to create a runspace configuration to start PowerShell
        /// </param>
        /// <param name="args">
        /// Command line arguments to the PowerShell
        /// </param>
        /// <param name="argc">
        /// Length of the passed in argument array.
        /// </param>
        [Obsolete("Callers should now use UnmanagedPSEntry.Start(string[], int)", error: true)]
        public static int Start(string consoleFilePath, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] args, int argc)
        {
            return Start(args, argc);
        }

        private const long StartupNoGCRegionSize = 64 * 1024 * 1024;

        /// <summary>
        /// Whether the arguments might include -SettingsFile, matching the parser's prefixes and switch characters loosely.
        /// </summary>
        private static bool MayUseSettingsFile(string[] args)
        {
            foreach (string arg in args)
            {
                ReadOnlySpan<char> name = arg.AsSpan().TrimStart("-/–—―");
                if (name.StartsWith("se", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether startup is running in a no-GC region, which ConsoleHost ends once the first command line is read.
        /// </summary>
        internal static bool StartupNoGCRegion;

        /// <summary>
        /// Starts PowerShell.
        /// </summary>
        /// <param name="args">
        /// Command line arguments to PowerShell
        /// </param>
        /// <param name="argc">
        /// Length of the passed in argument array.
        /// </param>
        public static int Start([MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] args, int argc)
        {
            ArgumentNullException.ThrowIfNull(args);

            // Startup allocates enough for the default gen0 budget to collect several times, and those pauses
            // add up. Suspend collection until the first command line is read (ConsoleHost ends the region);
            // past this size the region ends by itself and collection carries on as normal.
            // (Kept out of ConsoleHost, so its type initializer doesn't run before the culture is set below.)
            StartupNoGCRegion = GC.TryStartNoGCRegion(StartupNoGCRegionSize);

#if DEBUG
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]) && args[0]!.Equals("-isswait", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Attach the debugger to continue...");
                while (!System.Diagnostics.Debugger.IsAttached)
                {
                    Thread.Sleep(100);
                }

                System.Diagnostics.Debugger.Break();
            }
#endif
            // Warm up some components concurrently on background threads.
            EarlyStartup.Init();

            // Windows Vista and later support non-traditional UI fallback ie., a
            // user on an Arabic machine can choose either French or English(US) as
            // UI fallback language.
            // CLR does not support this (non-traditional) fallback mechanism.
            // The currentUICulture returned NativeCultureResolver supports this non
            // traditional fallback on Vista. So it is important to set currentUICulture
            // in the beginning before we do anything.
            Thread.CurrentThread.CurrentUICulture = NativeCultureResolver.UICulture;
            Thread.CurrentThread.CurrentCulture = NativeCultureResolver.Culture;

            // Create the default session state on another thread while the command line is parsed.
            // Not with -SettingsFile, which changes the configuration that creating it reads. On its own thread,
            // since the main thread soon waits for it and the pool is busy with warm-ups.
            Task<InitialSessionState>? defaultSessionState = MayUseSettingsFile(args) ? null : Task.Factory.StartNew(
                CreateDefaultSessionState,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            static InitialSessionState CreateDefaultSessionState()
            {
                Thread.CurrentThread.CurrentUICulture = NativeCultureResolver.UICulture;
                Thread.CurrentThread.CurrentCulture = NativeCultureResolver.Culture;
                InitialSessionState iss = InitialSessionState.CreateDefault2();
                StartupTypeAndFormatData.Start(iss);
                return iss;
            }

            ConsoleHost.ParseCommandLine(args);

            // NOTE: On Unix, logging depends on a command line parsing
            // and must be just after ConsoleHost.ParseCommandLine(args)
            // to allow overriding logging options.
            PSEtwLog.LogConsoleStartup();

            int exitCode = 0;
            try
            {
                string banner = string.Format(
                    CultureInfo.InvariantCulture,
                    ManagedEntranceStrings.ShellBannerPowerShell,
                    PSVersionInfo.GitCommitId);

                ConsoleHost.DefaultInitialSessionState = defaultSessionState?.GetAwaiter().GetResult() ?? InitialSessionState.CreateDefault2();

                exitCode = ConsoleHost.Start(
                    bannerText: banner,
                    helpText: ManagedEntranceStrings.UsageHelp,
                    issProvidedExternally: false);
            }
            catch (HostException e)
            {
                if (e.InnerException is Win32Exception win32e)
                {
                    // These exceptions are caused by killing conhost.exe
                    // 1236, network connection aborted by local system
                    // 0x6, invalid console handle
                    if (win32e.NativeErrorCode == 0x6 || win32e.NativeErrorCode == 1236)
                    {
                        return exitCode;
                    }
                }

                System.Environment.FailFast(e.Message, e);
            }
            catch (Exception e)
            {
                System.Environment.FailFast(e.Message, e);
            }

            return exitCode;
        }
    }
}
