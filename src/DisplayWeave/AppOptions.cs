using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DisplayWeave
{
    public sealed class AppOptions
    {
        public string DataDirectory { get; private set; }
        public bool StartMinimized { get; private set; }
        public bool ExitForUpdate { get; private set; }
        public string AutomaticExecutableFilter { get; private set; }
        public int WaitForExitProcessId { get; private set; }

        private AppOptions() { }

        public static AppOptions Parse(string[] args)
        {
            if (args == null) args = new string[0];
            AppOptions result = new AppOptions { DataDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory) };
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (option != "--data-dir" && option != "--minimized" && option != "--exit-for-update" &&
                    option != "--auto-test-executable" && option != "--wait-for-exit")
                    throw new ArgumentException("Unknown option: " + (option ?? "<null>") + ".");
                if (!seen.Add(option)) throw new ArgumentException("Option was specified more than once: " + option + ".");
                if (option == "--minimized") result.StartMinimized = true;
                else if (option == "--exit-for-update") result.ExitForUpdate = true;
                else
                {
                    if (++index >= args.Length || String.IsNullOrWhiteSpace(args[index]) ||
                        args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException((option == "--wait-for-exit" ? "A process ID" : "A path") + " is required after " + option + ".");
                    if (option == "--wait-for-exit")
                    {
                        int processId;
                        if (!Int32.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId <= 0)
                            throw new ArgumentException("--wait-for-exit requires a positive process ID written as decimal digits.");
                        result.WaitForExitProcessId = processId;
                        continue;
                    }
                    string path;
                    try { path = Path.GetFullPath(args[index]); }
                    catch (Exception ex)
                    {
                        if (!(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is PathTooLongException)) throw;
                        throw new ArgumentException("Invalid path for " + option + ": " + ex.Message, ex);
                    }
                    if (option == "--data-dir") result.DataDirectory = path;
                    else result.AutomaticExecutableFilter = path;
                }
            }
            if (result.ExitForUpdate && seen.Count != 1)
                throw new ArgumentException("--exit-for-update cannot be combined with other options.");
            return result;
        }
    }
}
