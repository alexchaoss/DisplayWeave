using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace DisplayWeave
{
    // Elevation applies to this launch only. These helpers never change startup,
    // create scheduled tasks/services, or launch a process during validation.
    public static class ElevationRestart
    {
        public const int PreviousProcessTimeoutMilliseconds = 30000;

        public static bool IsElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static ProcessStartInfo CreateStartInfo(string executable, string dataDirectory,
            string automaticExecutableFilter, int parentPid)
        {
            if (parentPid <= 0) throw new ArgumentOutOfRangeException("parentPid", "A positive previous process ID is required.");
            string fullExecutable = FullPath(executable, "executable");
            string fullData = FullPath(dataDirectory, "dataDirectory");
            string arguments = "--data-dir " + StartupRegistration.QuoteArgument(fullData) +
                " --wait-for-exit " + parentPid.ToString(CultureInfo.InvariantCulture);
            if (!String.IsNullOrWhiteSpace(automaticExecutableFilter))
                arguments += " --auto-test-executable " + StartupRegistration.QuoteArgument(FullPath(automaticExecutableFilter, "automaticExecutableFilter"));
            return new ProcessStartInfo
            {
                FileName = fullExecutable,
                WorkingDirectory = Path.GetDirectoryName(fullExecutable),
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };
        }

        public static void WaitForPreviousProcess(int processId)
        {
            if (processId == 0) return;
            using (Process current = Process.GetCurrentProcess())
                ValidatePreviousProcessId(processId, current.Id);
            Process previous;
            try { previous = Process.GetProcessById(processId); }
            catch (ArgumentException) { return; } // It already exited before startup.
            using (previous)
            {
                if (!previous.WaitForExit(PreviousProcessTimeoutMilliseconds))
                    throw new TimeoutException("The previous DisplayWeave instance did not exit within 30 seconds. Close it, then try again.");
            }
        }

        internal static void ValidatePreviousProcessId(int processId, int currentProcessId)
        {
            if (processId < 0) throw new ArgumentOutOfRangeException("processId", "A process ID cannot be negative.");
            if (processId != 0 && processId == currentProcessId)
                throw new ArgumentException("DisplayWeave cannot wait for its own process to exit.", "processId");
        }

        private static string FullPath(string path, string parameter)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", parameter);
            return Path.GetFullPath(path);
        }
    }
}
