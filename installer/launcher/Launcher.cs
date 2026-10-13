using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// Stable per-user launcher. The installer copies it to the installation root; it reads
// current.json beside it, rolls a version back if it failed to start twice, and starts the
// selected DisplayWeave.exe. It replaces the former Launch-DisplayWeave.ps1 so the Start menu
// shortcut and the sign-in entry start a signed program instead of powershell.exe.
namespace DisplayWeave
{
    internal static class Launcher
    {
        private const string VersionPattern = @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$";
        // The previous version, used only for rollback; build metadata (+...) is not part of a folder name.
        private const string RollbackPattern = @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$";

        [STAThread]
        private static int Main(string[] args)
        {
            bool minimized = false;
            foreach (string arg in args)
            {
                if (String.Equals(arg, "--minimized", StringComparison.Ordinal)) minimized = true;
                else return 2;
            }
            try
            {
                string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                string pointerPath = Path.Combine(root, "current.json");
                string json = File.ReadAllText(pointerPath);
                string version = Field(json, "version");
                string executable = Field(json, "executable");
                string dataDirectory = Field(json, "dataDirectory");
                if (version == null || !Regex.IsMatch(version, VersionPattern) ||
                    executable != "versions/" + version + "/DisplayWeave.exe" || dataDirectory != "data")
                    throw new InvalidOperationException("DisplayWeave current.json is invalid. Run the installer again.");
                string exe = Path.Combine(root, "versions", version, "DisplayWeave.exe");
                string data = Path.Combine(root, "data");
                if (!File.Exists(exe)) throw new FileNotFoundException("The selected DisplayWeave version is missing. Run the installer again.");

                // A version that stopped twice without running a minute or exiting cleanly (the app then
                // writes health\<version>.ok) goes back to the version it replaced. Bookkeeping problems
                // never stop the launch, so every failure here is ignored.
                try
                {
                    string previous = Field(json, "previousVersion");
                    string health = Path.Combine(root, "health");
                    if (previous != null && Regex.IsMatch(previous, RollbackPattern) && previous != version &&
                        !File.Exists(Path.Combine(health, version + ".ok")))
                    {
                        string starts = Path.Combine(health, version + ".starts");
                        string previousExe = Path.Combine(root, "versions", previous, "DisplayWeave.exe");
                        if (File.Exists(starts) && File.ReadAllLines(starts).Length >= 2 && File.Exists(previousExe))
                        {
                            // Same current.json layout as Updater.GoBack, so both paths agree.
                            string text = "{\r\n    \"version\":  \"" + previous + "\",\r\n    \"executable\":  \"versions/" +
                                previous + "/DisplayWeave.exe\",\r\n    \"dataDirectory\":  \"data\"\r\n}\r\n";
                            string temporary = pointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            File.WriteAllText(temporary, text, new UTF8Encoding(false));
                            File.Replace(temporary, pointerPath, null);
                            File.WriteAllText(Path.Combine(health, version + ".failed"), "Stopped twice within a minute of starting.\r\n");
                            exe = previousExe;
                        }
                        else
                        {
                            Directory.CreateDirectory(health);
                            File.AppendAllText(starts, DateTime.UtcNow.ToString("o") + "\r\n");
                        }
                    }
                }
                catch { }

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = exe;
                info.WorkingDirectory = Path.GetDirectoryName(exe);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.Arguments = "--data-dir \"" + data + "\"" + (minimized ? " --minimized" : "");
                using (Process process = Process.Start(info))
                    if (process == null) throw new InvalidOperationException("Windows did not start DisplayWeave.");
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static string Field(string json, string name)
        {
            Match match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
