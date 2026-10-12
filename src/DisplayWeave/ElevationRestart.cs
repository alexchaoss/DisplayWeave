using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

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

        // Folder under Program Files for administrator mode; only administrators can change it.
        public static string HelperRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DisplayWeave"); } }

        public static string UserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.User.Value;
        }

        // An elevated process must not start helpers from the user-writable install folder, so administrator
        // mode copies them to a fresh folder under root. Only the files this build lists are copied, and each copy is
        // checked against its hash there, where only administrators can change it: a helper swapped in the user's
        // folder never runs elevated. This user's earlier copies are removed first unless an app still holds their
        // hook DLL; SIDs never contain '_', so another user's folders never match.
        public static string CopyHelpers(string source, string root, string userSid, IDictionary<string, string> helpers)
        {
            if (Directory.Exists(root))
                foreach (string old in Directory.GetDirectories(root, userSid + "_*"))
                    RemoveFolder(old);
            string target = Path.Combine(root, userSid + "_" + Guid.NewGuid().ToString("N"));
            foreach (KeyValuePair<string, string> helper in helpers)
            {
                string from = Path.Combine(source, helper.Key);
                if (!File.Exists(from)) continue; // a missing helper is reported when an app needs it
                string to = Path.Combine(target, helper.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to);
                if (Sha256(to) == helper.Value) continue;
                RemoveFolder(target);
                throw new IOException(helper.Key + " is not the file this DisplayWeave build was made with. Reinstall DisplayWeave, then restart it as administrator.");
            }
            return target;
        }

        // The helpers scripts\build.ps1 compiled into this build: "<sha256>  native-v2-<architecture>/<file>" lines.
        internal static Dictionary<string, string> EmbeddedHelpers()
        {
            using (Stream stream = typeof(ElevationRestart).Assembly.GetManifestResourceStream("DisplayWeave.helpers.sha256"))
                return ReadHelperList(stream == null ? null : new StreamReader(stream).ReadToEnd());
        }

        internal static Dictionary<string, string> ReadHelperList(string text)
        {
            Dictionary<string, string> helpers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (text ?? String.Empty).Split('\n'))
            {
                Match entry = Regex.Match(line.TrimEnd('\r'), @"^([0-9a-f]{64})  (native-v2-(?:x64|x86)/[A-Za-z0-9._-]+)$");
                if (entry.Success) helpers[entry.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar)] = entry.Groups[1].Value;
            }
            return helpers;
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Best effort at exit; a copy still in use is removed by a later administrator start.
        public static void RemoveHelpers(string target)
        {
            RemoveFolder(target);
            try { Directory.Delete(Path.GetDirectoryName(target)); } // only when empty
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void RemoveFolder(string folder)
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string FullPath(string path, string parameter)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", parameter);
            return Path.GetFullPath(path);
        }
    }
}
