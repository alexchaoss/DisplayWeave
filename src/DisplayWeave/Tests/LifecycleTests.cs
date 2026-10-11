using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    public static class LifecycleTests
    {
        public static int Run(string reportPath)
        {
            int passed = 0, failed = 0;
            StringBuilder report = new StringBuilder();
            Action<string, Action> test = delegate(string name, Action body)
            {
                try { body(); passed++; report.AppendLine("PASS " + name); }
                catch (Exception ex) { failed++; report.AppendLine("FAIL " + name + ": " + ex.Message); }
            };
            test("Defaults use the executable directory without changing startup state", delegate
            {
                AppOptions options = AppOptions.Parse(new string[0]);
                Assert(options.DataDirectory == Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), "Default directory changed.");
                Assert(!options.StartMinimized && !options.ExitForUpdate && options.AutomaticExecutableFilter == null &&
                    options.WaitForExitProcessId == 0, "Unexpected default option.");
            });
            test("Data and executable filter paths become absolute", delegate
            {
                AppOptions options = AppOptions.Parse(new string[] { "--minimized", "--data-dir", "data folder", "--auto-test-executable", "probe.exe" });
                Assert(options.StartMinimized && options.DataDirectory == Path.GetFullPath("data folder"), "Data option did not normalize.");
                Assert(options.AutomaticExecutableFilter == Path.GetFullPath("probe.exe"), "Filter did not normalize.");
            });
            test("Exit for update is accepted only by itself", delegate
            {
                Assert(AppOptions.Parse(new string[] { "--exit-for-update" }).ExitForUpdate, "Exit option missing.");
                Reject(delegate { AppOptions.Parse(new string[] { "--exit-for-update", "--minimized" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir", "data", "--exit-for-update" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--wait-for-exit", "123", "--exit-for-update" }); });
            });
            test("Unknown options and positional arguments are rejected", delegate
            {
                Reject(delegate { AppOptions.Parse(new string[] { "--minimised" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "file.txt" }); });
                Reject(delegate { AppOptions.Parse(new string[] { null }); });
            });
            test("All duplicate options are rejected", delegate
            {
                Reject(delegate { AppOptions.Parse(new string[] { "--minimized", "--minimized" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir", "one", "--data-dir", "two" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--auto-test-executable", "one.exe", "--auto-test-executable", "two.exe" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--exit-for-update", "--exit-for-update" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--wait-for-exit", "123", "--wait-for-exit", "456" }); });
            });
            test("Missing values cannot consume another option", delegate
            {
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--auto-test-executable" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir", "--minimized" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir", " " }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--wait-for-exit" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--wait-for-exit", "--minimized" }); });
            });
            test("Invalid filter and data paths are rejected", delegate
            {
                Reject(delegate { AppOptions.Parse(new string[] { "--auto-test-executable", "bad\0path" }); });
                Reject(delegate { AppOptions.Parse(new string[] { "--data-dir", "bad\0path" }); });
            });
            test("Portable startup quotes both paths and preserves minimized option", delegate
            {
                string command = StartupRegistration.BuildCommand(@"C:\My Apps\DisplayWeave.exe", @"C:\My Data", delegate { return false; });
                Assert(command == "\"C:\\My Apps\\DisplayWeave.exe\" --minimized --data-dir \"C:\\My Data\"", "Portable command changed: " + command);
            });
            test("Installed startup uses the stable launcher instead of versioned EXE", delegate
            {
                string inspected = null;
                string command = StartupRegistration.BuildCommand(@"C:\Apps\DisplayWeave\versions\0.4\DisplayWeave.exe", @"C:\Apps\DisplayWeave\data\",
                    delegate(string candidate) { inspected = candidate; return true; });
                Assert(inspected == @"C:\Apps\DisplayWeave\Launch-DisplayWeave.ps1", "Wrong launcher checked.");
                Assert(command == PowerShellPrefix() + " -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"C:\\Apps\\DisplayWeave\\Launch-DisplayWeave.ps1\" -Minimized", "Unstable installed command.");
            });
            test("Missing launcher falls back to the current portable command", delegate
            {
                string command = StartupRegistration.BuildCommand(@"C:\App\versions\v1\DisplayWeave.exe", @"C:\App\data", delegate { return false; });
                Assert(command.StartsWith("\"C:\\App\\versions\\v1\\DisplayWeave.exe\" --minimized", StringComparison.Ordinal), "Missing launcher was selected.");
            });
            test("Sibling version names and another data directory cannot select installed launcher", delegate
            {
                string outside = StartupRegistration.BuildCommand(@"C:\App\versions-other\v1\DisplayWeave.exe", @"C:\App\data", delegate { return true; });
                string custom = StartupRegistration.BuildCommand(@"C:\App\versions\v1\DisplayWeave.exe", @"C:\Other\data", delegate { return true; });
                Assert(outside.StartsWith("\"C:\\App\\versions-other\\v1\\DisplayWeave.exe\" --minimized", StringComparison.Ordinal) &&
                    custom.StartsWith("\"C:\\App\\versions\\v1\\DisplayWeave.exe\" --minimized", StringComparison.Ordinal), "Unrelated installation was selected.");
            });
            test("Installed path comparison follows Windows casing and normalizes parent segments", delegate
            {
                string command = StartupRegistration.BuildCommand(@"c:\APP\versions\temp\..\v2\DisplayWeave.exe", @"C:\App\DATA", delegate { return true; });
                Assert(command.StartsWith(PowerShellPrefix() + " -NoProfile ", StringComparison.Ordinal), "Path normalization lost installation membership.");
            });
            test("Windows argument quoting handles spaces embedded quotes and trailing slashes", delegate
            {
                Assert(StartupRegistration.QuoteArgument("") == "\"\"", "Empty argument changed.");
                Assert(StartupRegistration.QuoteArgument("a b") == "\"a b\"", "Spaces lost quoting.");
                Assert(StartupRegistration.QuoteArgument("say \"yes\"") == "\"say \\\"yes\\\"\"", "Quote escaping failed.");
                Assert(StartupRegistration.QuoteArgument(@"C:\") == "\"C:\\\\\"", "Trailing slash escaped closing quote.");
                Assert(StartupRegistration.QuoteArgument("a\\\"b") == "\"a\\\\\\\"b\"", "Slash before quote was lost.");
            });
            test("Root data directory keeps its separator safely quoted", delegate
            {
                string command = StartupRegistration.BuildCommand(@"C:\App\DisplayWeave.exe", @"D:\", delegate { return false; });
                Assert(command.EndsWith("--data-dir \"D:\\\\\"", StringComparison.Ordinal), "Root path changed.");
            });
            test("Startup command limit rejects long paths before registry work", delegate
            {
                Assert(StartupRegistration.IsUsableCommand(new string('x', 260)), "260-character command rejected.");
                Assert(!StartupRegistration.IsUsableCommand(new string('x', 261)), "Oversized command accepted.");
                Reject(delegate { StartupRegistration.BuildCommand(@"C:\" + new string('a', 170) + @"\DisplayWeave.exe", @"C:\" + new string('b', 100), delegate { return false; }); });
            });
            test("Empty and control-character startup entries are not treated as enabled", delegate
            {
                Assert(!StartupRegistration.IsUsableCommand(null) && !StartupRegistration.IsUsableCommand(" ") &&
                    !StartupRegistration.IsUsableCommand("app\r\nother") && !StartupRegistration.IsUsableCommand("app\0"), "Invalid startup entry accepted.");
            });
            test("Elevation handoff accepts a positive process ID alongside preserved options", delegate
            {
                AppOptions options = AppOptions.Parse(new string[] { "--wait-for-exit", "12345", "--data-dir", "data folder", "--auto-test-executable", "probe.exe" });
                Assert(options.WaitForExitProcessId == 12345 && options.DataDirectory == Path.GetFullPath("data folder") &&
                    options.AutomaticExecutableFilter == Path.GetFullPath("probe.exe"), "Handoff dropped an option.");
                Assert(AppOptions.Parse(new string[] { "--wait-for-exit", Int32.MaxValue.ToString(CultureInfo.InvariantCulture) }).WaitForExitProcessId == Int32.MaxValue,
                    "Valid maximum process ID was rejected.");
            });
            test("Elevation handoff rejects zero negative overflowing and formatted process IDs", delegate
            {
                foreach (string invalid in new string[] { "0", "-1", "+1", " 1", "1 ", "1,000", "1.0", "0x10", "2147483648", "not-a-pid" })
                    Reject(delegate { AppOptions.Parse(new string[] { "--wait-for-exit", invalid }); });
            });
            test("Elevation restart requests runas and preserves exact executable and data paths", delegate
            {
                var start = ElevationRestart.CreateStartInfo(@"C:\My Apps\DisplayWeave.exe", @"C:\My Data", null, 12345);
                Assert(start.FileName == @"C:\My Apps\DisplayWeave.exe" && start.WorkingDirectory == @"C:\My Apps", "Wrong executable or working directory.");
                Assert(start.UseShellExecute && start.Verb == "runas", "Restart does not request the Windows elevation prompt.");
                Assert(start.Arguments == "--data-dir \"C:\\My Data\" --wait-for-exit 12345", "Data path or handoff arguments changed.");
                Assert(!start.RedirectStandardInput && !start.RedirectStandardOutput && !start.RedirectStandardError, "Shell launch cannot redirect streams.");
            });
            test("Elevation restart preserves the exact test filter and quotes trailing separators", delegate
            {
                var start = ElevationRestart.CreateStartInfo(@"C:\App\DisplayWeave.exe", @"D:\", @"C:\Test Apps\Probe.exe", 7);
                Assert(start.Arguments == "--data-dir \"D:\\\\\" --wait-for-exit 7 --auto-test-executable \"C:\\Test Apps\\Probe.exe\"", "Root directory quoting or test filter changed.");
            });
            test("Elevation restart normalizes relative paths without choosing a different executable", delegate
            {
                var start = ElevationRestart.CreateStartInfo(@"relative folder\DisplayWeave.exe", "data folder", "probe.exe", 19);
                Assert(start.FileName == Path.GetFullPath(@"relative folder\DisplayWeave.exe"), "Executable did not become absolute.");
                Assert(start.Arguments == "--data-dir " + StartupRegistration.QuoteArgument(Path.GetFullPath("data folder")) +
                    " --wait-for-exit 19 --auto-test-executable " + StartupRegistration.QuoteArgument(Path.GetFullPath("probe.exe")), "Paths did not normalize together.");
            });
            test("Elevation restart rejects unusable paths and missing previous process identity", delegate
            {
                Reject(delegate { ElevationRestart.CreateStartInfo(" ", @"C:\Data", null, 1); });
                Reject(delegate { ElevationRestart.CreateStartInfo(@"C:\App.exe", " ", null, 1); });
                Reject(delegate { ElevationRestart.CreateStartInfo(@"C:\App.exe", @"C:\Data", "bad\0path", 1); });
                Reject(delegate { ElevationRestart.CreateStartInfo(@"C:\App.exe", @"C:\Data", null, 0); });
                Reject(delegate { ElevationRestart.CreateStartInfo(@"C:\App.exe", @"C:\Data", null, -1); });
            });
            test("Elevation restart process IDs remain invariant under another numeric culture", delegate
            {
                CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                    Assert(AppOptions.Parse(new string[] { "--wait-for-exit", "12345" }).WaitForExitProcessId == 12345, "Parsing used the current culture.");
                    Assert(ElevationRestart.CreateStartInfo(@"C:\App.exe", @"C:\Data", null, 12345).Arguments.EndsWith("--wait-for-exit 12345", StringComparison.Ordinal),
                        "Restart formatted the process ID with the current culture.");
                }
                finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
            });
            test("Previous process waiting is bounded and cannot wait on itself", delegate
            {
                Assert(ElevationRestart.PreviousProcessTimeoutMilliseconds == 30000, "Wait is no longer bounded to 30 seconds.");
                ElevationRestart.ValidatePreviousProcessId(0, 123);
                ElevationRestart.ValidatePreviousProcessId(456, 123);
                Reject(delegate { ElevationRestart.ValidatePreviousProcessId(-1, 123); });
                Reject(delegate { ElevationRestart.ValidatePreviousProcessId(123, 123); });
            });
            test("Updates pick the newest app release and skip prototype tags", delegate
            {
                Assert(Updater.Newest("0.5.5-preview", new string[] { "prototype-v9.0.0-preview", "v0.5.10-preview", "v0.5.9-preview", "v0.5.4-preview", "draft", "v1.0" }) == "0.5.10-preview",
                    "Did not pick the highest numeric release.");
                Assert(Updater.Newest("0.5.5-preview", new string[] { "v0.5.5-preview", "v0.5.4", " v0.5.5-preview " }) == null, "Offered the current or an older version.");
                Assert(Updater.Newest("0.6.0-preview", new string[] { "v0.6.0" }) == "0.6.0", "A final release did not replace its preview.");
                Assert(Updater.Newest("not-a-version", new string[] { "v9.9.9" }) == null, "An unparsable current version must never update.");
            });
            test("Only installed copies look for updates", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "DisplayWeave-updater-" + Guid.NewGuid().ToString("N"));
                string executable = Path.Combine(root, @"versions\0.5.5-preview\DisplayWeave.exe");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(executable));
                    Assert(Updater.InstallRoot(executable) == null, "A folder without current.json was treated as an installation.");
                    File.WriteAllText(Path.Combine(root, "current.json"), "{}");
                    Assert(Updater.InstallRoot(executable) == root, "The installation root was not found.");
                    Assert(Updater.InstallRoot(Path.Combine(root, "DisplayWeave.exe")) == null, "A development build was treated as an installation.");
                }
                finally { Directory.Delete(root, true); }
            });
            test("Downloaded setups must match their release checksum", delegate
            {
                string setup = Path.Combine(Path.GetTempPath(), "DisplayWeave-Setup-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    File.WriteAllText(setup, "abc");
                    File.WriteAllText(setup + ".sha256", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  " + Path.GetFileName(setup) + "\r\n");
                    Assert(Updater.MatchesChecksum(setup), "A matching setup was rejected.");
                    File.WriteAllText(setup, "abd");
                    Assert(!Updater.MatchesChecksum(setup), "A changed setup was accepted.");
                    File.WriteAllText(setup + ".sha256", "");
                    Assert(!Updater.MatchesChecksum(setup), "An empty checksum file was accepted.");
                }
                finally { File.Delete(setup); File.Delete(setup + ".sha256"); }
            });
            report.AppendLine(passed + " passed; " + failed + " failed.");
            string fullReport = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullReport));
            File.WriteAllText(fullReport, report.ToString(), new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string PowerShellPrefix()
        {
            return "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe") + "\"";
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Invalid input was accepted.");
        }
    }
}
