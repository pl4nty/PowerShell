// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.PowerShell.Commands.Internal.Format;

namespace System.Management.Automation.Runspaces
{
    /// <summary>
    /// Builds the type table and format database for the default session state's built-in type and format
    /// files on background threads, as soon as the session state exists, so that opening the first runspace
    /// can take them instead of building them itself (the longest part of binding it).
    /// </summary>
    /// <remarks>
    /// Only built-in files qualify: their data is compiled into PowerShell, so loading them reads no file and
    /// checks no signature, and runspace binding already loads them on thread pool threads. The prebuilt data
    /// is taken once, by the first runspace whose list of files is exactly the list it was built from, and only
    /// if building it reported no errors; anything else loads the usual way. The type table and format database
    /// are ordinary per-runspace instances, so Update-TypeData and Update-FormatData work on them as usual.
    /// </remarks>
    internal static class StartupTypeAndFormatData
    {
        private static string[] s_typeFiles;
        private static Task<TypeTable> s_typeTable;
        private static string[] s_formatFiles;
        private static Task<TypeInfoDataBaseManager> s_formatDatabase;

        /// <summary>
        /// Starts building the type table and format database for <paramref name="iss"/>'s type and format files.
        /// </summary>
        internal static void Start(InitialSessionState iss)
        {
            // Only the file lists are read here; checking they're built in (which runs TypeTable's type initializer)
            // happens with the building, off this thread.
            string[] typeFiles = GetFiles(iss.Types, static e => e.TypeData is null && e.TypeTable is null ? e.FileName : null);
            if (typeFiles is not null)
            {
                s_typeFiles = typeFiles;
                s_typeTable = Task.Factory.StartNew(
                    static state => BuildTypeTable((string[])state),
                    typeFiles,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            string[] formatFiles = GetFiles(iss.Formats, static e => e.FormatData is null && e.Formattable is null ? e.FileName : null);
            if (formatFiles is not null)
            {
                s_formatFiles = formatFiles;
                s_formatDatabase = Task.Factory.StartNew(
                    static state => BuildFormatDatabase((string[])state),
                    formatFiles,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// The prebuilt type table, if it was built from exactly <paramref name="types"/>' files.
        /// </summary>
        internal static TypeTable TryTakeTypeTable(InitialSessionStateEntryCollection<SessionStateTypeEntry> types)
        {
            Task<TypeTable> task = Interlocked.Exchange(ref s_typeTable, null);
            if (task is null || !MatchesFiles(types, static e => e.TypeData is null && e.TypeTable is null ? e.FileName : null, s_typeFiles))
            {
                return null;
            }

            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The prebuilt format database, if it was built from exactly the files in <paramref name="entries"/>.
        /// </summary>
        internal static TypeInfoDataBaseManager TryTakeFormatDatabase(Collection<PSSnapInTypeAndFormatErrors> entries)
        {
            Task<TypeInfoDataBaseManager> task = Interlocked.Exchange(ref s_formatDatabase, null);
            if (task is null
                || entries.Count != s_formatFiles.Length
                || !entries.Select(static e => e.FormatData is null && e.FormatTable is null && e.TypeData is null ? e.FullPath : null)
                    .SequenceEqual(s_formatFiles, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        private static string[] GetFiles<T>(InitialSessionStateEntryCollection<T> entries, Func<T, string> fileName)
            where T : InitialSessionStateEntry
        {
            if (entries.Count == 0)
            {
                return null;
            }

            var files = new string[entries.Count];
            for (int i = 0; i < files.Length; i++)
            {
                if (fileName(entries[i]) is not string file)
                {
                    return null;
                }

                files[i] = file;
            }

            return files;
        }

        private static bool MatchesFiles<T>(InitialSessionStateEntryCollection<T> entries, Func<T, string> fileName, string[] files)
            where T : InitialSessionStateEntry
        {
            if (entries.Count != files.Length)
            {
                return false;
            }

            for (int i = 0; i < files.Length; i++)
            {
                if (!string.Equals(fileName(entries[i]), files[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static TypeTable BuildTypeTable(string[] files)
        {
            if (!files.All(TypeTable.IsBuiltInTypesFile))
            {
                return null;
            }

            var typeTable = new TypeTable();
            var errors = new ConcurrentBag<string>();
            foreach (string file in files)
            {
                typeTable.Update(string.Empty, file, errors, authorizationManager: null, host: null, out _);
            }

            typeTable.ClearConsolidatedMembers();
            return errors.IsEmpty ? typeTable : null;
        }

        private static TypeInfoDataBaseManager BuildFormatDatabase(string[] files)
        {
            if (!files.All(TypeInfoDataBaseManager.IsBuiltinFormatFile))
            {
                return null;
            }

            var entries = new Collection<PSSnapInTypeAndFormatErrors>();
            foreach (string file in files)
            {
                entries.Add(new PSSnapInTypeAndFormatErrors(file, file));
            }

            var database = new TypeInfoDataBaseManager();
            database.UpdateDataBase(entries, authorizationManager: null, host: null, preValidated: true);
            return entries.All(static e => e.Errors.IsEmpty) ? database : null;
        }
    }
}
