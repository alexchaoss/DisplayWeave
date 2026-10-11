using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DisplayWeave
{
    // Finds and downloads a newer DisplayWeave setup from the private GitHub releases
    // through the user's signed-in GitHub CLI. gh keeps the credentials; none are read here.
    internal static class Updater
    {
        private const string Repository = "alexchaoss/DisplayWeave";
        private static readonly Regex ReleaseVersion = new Regex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(-[0-9A-Za-z.-]+)?$");

        // Installed copies run from <root>\versions\<version>\; development builds never update.
        internal static string InstallRoot(string executablePath)
        {
            string versions = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
            if (versions == null || !String.Equals(Path.GetFileName(versions), "versions", StringComparison.OrdinalIgnoreCase)) return null;
            string root = Path.GetDirectoryName(versions);
            return root != null && File.Exists(Path.Combine(root, "current.json")) ? root : null;
        }

        // Numeric order; a final release sorts after its own previews (0.6.0 > 0.6.0-preview).
        internal static int Compare(string left, string right)
        {
            Match a = ReleaseVersion.Match(left ?? ""), b = ReleaseVersion.Match(right ?? "");
            if (!a.Success || !b.Success) return 0;
            for (int i = 1; i <= 3; i++)
            {
                int difference = Int32.Parse(a.Groups[i].Value).CompareTo(Int32.Parse(b.Groups[i].Value));
                if (difference != 0) return difference;
            }
            return b.Groups[4].Success.CompareTo(a.Groups[4].Success);
        }

        // Returns the newest release version from gh's tag list when it is newer than current.
        internal static string Newest(string current, IEnumerable<string> tags)
        {
            string best = null;
            foreach (string tag in tags)
            {
                string version = tag.Trim();
                if (!version.StartsWith("v", StringComparison.Ordinal)) continue; // prototype-v* tags are another app
                version = version.Substring(1);
                if (ReleaseVersion.IsMatch(version) && Compare(version, current) > 0 && (best == null || Compare(version, best) > 0)) best = version;
            }
            return best;
        }

        // Returns the checksum-verified setup of a newer release, or null when this copy is current.
        internal static string DownloadNewer(string current, out string version)
        {
            string gh = FindGitHubCli();
            if (gh == null) throw new InvalidOperationException("install GitHub CLI and run gh auth login to receive updates.");
            version = Newest(current, Run(gh, "release list --repo " + Repository + " --exclude-drafts --limit 50 --json tagName --jq \".[].tagName\"")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            if (version == null) return null;
            string directory = Path.Combine(Path.GetTempPath(), "DisplayWeaveUpdate");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.CreateDirectory(directory);
            string setup = Path.Combine(directory, "DisplayWeave-Setup-" + version + ".exe");
            Run(gh, "release download v" + version + " --repo " + Repository + " --dir \"" + directory + "\" --pattern \"" +
                Path.GetFileName(setup) + "\" --pattern \"" + Path.GetFileName(setup) + ".sha256\"");
            if (!MatchesChecksum(setup)) throw new InvalidDataException("the downloaded " + version + " setup does not match its checksum, so it was not installed.");
            return setup;
        }

        // The release's <file>.sha256 holds "<hex>  <name>", as written by build-setup.ps1.
        internal static bool MatchesChecksum(string file)
        {
            string expected = File.ReadAllText(file + ".sha256").Trim().Split(' ')[0].ToLowerInvariant();
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == expected;
        }

        private static string FindGitHubCli()
        {
            List<string> folders = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"));
            foreach (string folder in folders)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim('"', ' '), "gh.exe");
                    if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate; // relative PATH entries are never searched
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private static string Run(string gh, string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo(gh, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
            start.EnvironmentVariables["GH_NO_UPDATE_NOTIFIER"] = "1";
            using (Process process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    throw new TimeoutException("GitHub CLI did not answer within two minutes.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("GitHub CLI could not read the releases (" + error.Result.Trim() + "). Run gh auth login if you are signed out.");
                return output.Result;
            }
        }
    }
}
