using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
            test("The compiled-in version is a release version", delegate
            {
                Assert(System.Text.RegularExpressions.Regex.IsMatch(Program.Version, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"),
                    "The window would show an invalid version: " + Program.Version);
            });
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
            test("Administrator mode copies helpers to a fresh folder and removes only this user's earlier copies", delegate
            {
                string temporary = Path.Combine(Path.GetTempPath(), "DisplayWeave-helpers-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string source = Path.Combine(temporary, "app"), root = Path.Combine(temporary, "protected");
                    Directory.CreateDirectory(Path.Combine(source, "native-v2-x64"));
                    File.WriteAllText(Path.Combine(source, "native-v2-x64", "RegionHookHost.exe"), "x64 helper");
                    string earlier = Directory.CreateDirectory(Path.Combine(root, "S-1-5-21-7_old")).FullName;
                    string otherUser = Directory.CreateDirectory(Path.Combine(root, "S-1-5-21-70_old")).FullName;
                    string copy = ElevationRestart.CopyHelpers(source, root, "S-1-5-21-7");
                    Assert(File.ReadAllText(Path.Combine(copy, "native-v2-x64", "RegionHookHost.exe")) == "x64 helper" &&
                        !Directory.Exists(Path.Combine(copy, "native-v2-x86")), "Helpers were not copied as they are.");
                    Assert(!Directory.Exists(earlier) && Directory.Exists(otherUser), "Only this user's earlier copies may be removed.");
                    ElevationRestart.RemoveHelpers(copy);
                    Assert(!Directory.Exists(copy) && Directory.Exists(root), "The copy was not removed, or a folder still in use was.");
                }
                finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            });
            test("Updates pick the newest app release and skip prototype tags", delegate
            {
                Assert(Updater.Newest("0.5.5-preview", new string[] { "prototype-v9.0.0-preview", "v0.5.10-preview", "v0.5.9-preview", "v0.5.4-preview", "draft", "v1.0" }) == "0.5.10-preview",
                    "Did not pick the highest numeric release.");
                Assert(Updater.Newest("0.5.5-preview", new string[] { "v0.5.5-preview", "v0.5.4", " v0.5.5-preview " }) == null, "Offered the current or an older version.");
                Assert(Updater.Newest("0.6.0-preview", new string[] { "v0.6.0" }) == "0.6.0", "A final release did not replace its preview.");
                Assert(Updater.Newest("not-a-version", new string[] { "v9.9.9" }) == null, "An unparsable current version must never update.");
            });
            test("Update checks read only published tags from GitHub's release list", delegate
            {
                List<string> tags = Updater.ReleaseTags("[{\"tag_name\":\"v0.5.10-preview\",\"draft\":false,\"body\":\"\\\"tag_name\\\":\\\"v9.9.9\\\"\"}," +
                    "{\"tag_name\":\"v0.6.0\",\"draft\":true},{\"tag_name\":7,\"draft\":false},{\"tag_name\":\"v0.6.1\"},[]]");
                Assert(tags.Count == 1 && tags[0] == "v0.5.10-preview", "A draft, a malformed entry or text in a release body was read as a tag.");
                bool refused = false;
                try { Updater.ReleaseTags("{\"message\":\"API rate limit exceeded\"}"); }
                catch (InvalidDataException) { refused = true; }
                Assert(refused, "An error response was read as a release list.");
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
            test("Updates install only a setup whose checksum carries a release-key signature", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "DisplayWeave-signed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                // Ephemeral keys: nothing is stored in the Windows key store.
                using (CngKey release = CngKey.Create(CngAlgorithm.ECDsaP256))
                using (CngKey attacker = CngKey.Create(CngAlgorithm.ECDsaP256))
                {
                    string[] trusted = { Convert.ToBase64String(release.Export(CngKeyBlobFormat.EccPublicBlob)) };
                    Func<string, string, CngKey, string> publish = delegate(string version, string namedVersion, CngKey signer)
                    {
                        string setup = Path.Combine(directory, "DisplayWeave-Setup-" + version + ".exe");
                        File.WriteAllText(setup, "setup " + namedVersion);
                        string hash;
                        using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(setup))).Replace("-", "").ToLowerInvariant();
                        File.WriteAllText(setup + ".sha256", hash + "  DisplayWeave-Setup-" + namedVersion + ".exe\r\n"); // as build-setup.ps1 writes it
                        using (ECDsaCng ecdsa = new ECDsaCng(signer))
                        {
                            ecdsa.HashAlgorithm = CngAlgorithm.Sha256;
                            File.WriteAllText(setup + ".sha256.sig", Convert.ToBase64String(ecdsa.SignData(File.ReadAllBytes(setup + ".sha256"))));
                        }
                        return setup;
                    };
                    try
                    {
                        string good = publish("0.6.0", "0.6.0", release);
                        Assert(Updater.IsSignedRelease(good, "0.6.0", trusted), "A correctly signed setup was refused.");
                        Assert(!Updater.IsSignedRelease(good, "0.6.0", new string[0]), "A setup was accepted without any pinned key.");
                        Assert(!Updater.IsSignedRelease(publish("0.6.1", "0.6.1", attacker), "0.6.1", trusted), "A setup signed by another key was accepted.");
                        string swapped = publish("0.6.2", "0.6.2", release);
                        File.WriteAllText(swapped, "malicious");
                        Assert(!Updater.IsSignedRelease(swapped, "0.6.2", trusted), "A replaced setup was accepted.");
                        string forged; // the attacker can match the checksum to the new file, but cannot sign it
                        using (SHA256 sha = SHA256.Create()) forged = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(swapped))).Replace("-", "").ToLowerInvariant();
                        File.WriteAllText(swapped + ".sha256", forged + "  DisplayWeave-Setup-0.6.2.exe\r\n");
                        Assert(!Updater.IsSignedRelease(swapped, "0.6.2", trusted), "A rewritten checksum was accepted.");
                        Assert(!Updater.IsSignedRelease(publish("9.9.9", "0.5.0", release), "9.9.9", trusted), "An older signed setup was accepted under a newer tag.");
                        string unsigned = publish("0.7.0", "0.7.0", release);
                        File.Delete(unsigned + ".sha256.sig");
                        Assert(!Updater.IsSignedRelease(unsigned, "0.7.0", trusted), "A setup without a signature was accepted.");
                        File.WriteAllText(unsigned + ".sha256.sig", "not base64!");
                        Assert(!Updater.IsSignedRelease(unsigned, "0.7.0", trusted), "An unreadable signature was accepted.");
                    }
                    finally { Directory.Delete(directory, true); }
                }
            });
            test("A release key is pinned, so installed copies can verify updates", delegate
            {
                Assert(Updater.ReleaseKeys.Length > 0, "No release key is pinned. Run scripts\\sign-release.ps1 -NewKey before building a release.");
                foreach (string key in Updater.ReleaseKeys)
                    using (CngKey publicKey = CngKey.Import(Convert.FromBase64String(key), CngKeyBlobFormat.EccPublicBlob))
                        Assert(publicKey.Algorithm == CngAlgorithm.ECDsaP256, "A pinned release key is not ECDSA P-256.");
            });
            test("Diagnostic reports leave out names, paths and identifiers", delegate
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string game = @"D:\Some Games\Secret Title\SecretGame.exe";
                List<string> paths = new List<string> { game };
                string raw = "Helper failed for " + game + " (SecretGame.exe) in " + profile + @"\AppData\Local\DisplayWeave\data, user " + Environment.UserName +
                    " on " + Environment.MachineName + @", SID S-1-5-21-1111111111-2222222222-3333333333-1001, other file E:\Tools Folder\other tool.exe and E:\plain\folder.";
                string hidden = DiagnosticReport.Scrub(raw, paths, false);
                foreach (string leak in new[] { "Some Games", "Secret", profile, "S-1-5-21", "Tools Folder", "other tool", @"E:\plain" })
                    Assert(hidden.IndexOf(leak, StringComparison.OrdinalIgnoreCase) < 0, "The report kept " + leak + ": " + hidden);
                foreach (string name in new[] { Environment.UserName, Environment.MachineName })
                    if (name.Length > 2) Assert(!Regex.IsMatch(hidden, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase), "The report kept a user or computer name: " + hidden);
                Assert(hidden.Contains("App 1") && hidden.Contains("%USERPROFILE%"), "Paths were removed instead of labelled: " + hidden);
                Assert(DiagnosticReport.Scrub(raw, paths, true).Contains("SecretGame.exe"), "An app name the user chose to include was removed.");
            });
            test("Diagnostic reports are Markdown with this computer's displays", delegate
            {
                HandledApp app = new HandledApp { Target = new ApplicationIdentity { ExecutablePath = @"D:\Games\SecretGame.exe", Is32Bit = true },
                    State = HandledAppState.Error, Error = @"Could not load into D:\Games\SecretGame.exe." };
                string text = DiagnosticReport.Build("Handling active · 1 handled · 0 attaching", new LayoutSettings(), new List<HandledApp> { app },
                    new[] { "", "A repeated message", "A repeated message" }, false);
                Assert(text.StartsWith("### DisplayWeave diagnostic report") && text.Contains("| Windows | Windows ") && text.Contains("#### Displays") &&
                    text.Contains("#### Graphics drivers"), "A section is missing: " + text);
                Assert(text.Contains("| App 1 | x86 | Error | Could not load into App 1. |"), "The app row is wrong: " + text);
                Assert(text.IndexOf("SecretGame", StringComparison.OrdinalIgnoreCase) < 0, "An app name was shown without the user's choice.");
                Assert(Regex.Matches(text, "A repeated message").Count == 1, "A repeated message was listed twice.");
                foreach (DisplayMonitor monitor in DisplayMonitor.All())
                    Assert(text.Contains(monitor.DeviceName), "A display is missing: " + monitor.DeviceName);
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
