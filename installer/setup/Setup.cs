using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DisplayWeave.Setup
{
    // Per-user setup for DisplayWeave. The embedded install.ps1 does all file work
    // (checksum, versioned install, graceful exit, rollback, uninstall); this program
    // adds the window, the Settings > Apps entry and the uninstaller copy.
    internal static class Program
    {
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DisplayWeave";
        internal const string UninstallerName = "Uninstall DisplayWeave.exe";
        internal static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayWeave");

        internal sealed class Options
        {
            internal bool Uninstall, Quiet, NoLaunch, RemoveSettings;
            internal string Root = DefaultRoot, Startup, Log;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Options options = new Options();
            if (Path.GetFileName(Application.ExecutablePath).Equals(UninstallerName, StringComparison.OrdinalIgnoreCase)) options.Uninstall = true;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--uninstall") options.Uninstall = true;
                    else if (arg == "--quiet") options.Quiet = true;
                    else if (arg == "--no-launch") options.NoLaunch = true;
                    else if (arg == "--remove-settings") options.RemoveSettings = true;
                    else if (arg == "--install-root" && i + 1 < args.Length) options.Root = Path.GetFullPath(args[++i]).TrimEnd('\\');
                    else if (arg == "--startup" && i + 1 < args.Length && Regex.IsMatch(args[i + 1], "^(Preserve|Enable|Disable)$")) options.Startup = args[++i];
                    else if (arg == "--log" && i + 1 < args.Length) options.Log = Path.GetFullPath(args[++i]);
                    else throw new ArgumentException("Unknown or incomplete option: " + arg);
                }
            }
            catch (ArgumentException ex)
            {
                if (!options.Quiet) MessageBox.Show(ex.Message, "DisplayWeave Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            if (options.Quiet)
            {
                string output;
                int code = options.Uninstall ? Setup.Uninstall(options, out output) : Setup.Install(options, out output);
                if (options.Log != null) File.WriteAllText(options.Log, output, new UTF8Encoding(false));
                return code;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(options));
            return 0;
        }
    }

    internal static class Setup
    {
        internal static string PackageName
        {
            get
            {
                foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                    if (Regex.IsMatch(name, @"^DisplayWeave-[0-9A-Za-z.+_-]+\.zip$")) return name;
                throw new InvalidOperationException("This setup does not contain a DisplayWeave package.");
            }
        }

        internal static string Version { get { string name = PackageName; return name.Substring("DisplayWeave-".Length, name.Length - "DisplayWeave-".Length - ".zip".Length); } }

        internal static string InstalledVersion(string root)
        {
            try
            {
                Match match = Regex.Match(File.ReadAllText(Path.Combine(root, "current.json")), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static bool StartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                return key != null && key.GetValue("DisplayWeave") != null;
        }

        internal static int Install(Program.Options options, out string output)
        {
            string package = PackageName;
            int code = RunInstallScript(delegate(string directory)
            {
                return "-PackagePath \"" + Path.Combine(directory, package) + "\" -ChecksumPath \"" + Path.Combine(directory, package + ".sha256") +
                    "\" -InstallRoot \"" + options.Root + "\" -Startup " + (options.Startup ?? "Preserve") + (options.NoLaunch ? " -NoLaunch" : "");
            }, out output);
            if (code != 0) return code;
            try
            {
                string uninstaller = Path.Combine(options.Root, Program.UninstallerName);
                if (!String.Equals(Path.GetFullPath(Application.ExecutablePath), uninstaller, StringComparison.OrdinalIgnoreCase))
                    File.Copy(Application.ExecutablePath, uninstaller, true);
                // Only the default installation appears in Settings > Apps; test roots stay unregistered.
                if (IsDefaultRoot(options.Root)) Register(options.Root, uninstaller);
            }
            catch (Exception ex)
            {
                output += Environment.NewLine + "DisplayWeave is installed, but its Settings > Apps entry could not be written: " + ex.Message;
                return 3;
            }
            return 0;
        }

        internal static int Uninstall(Program.Options options, out string output)
        {
            if (File.Exists(Path.Combine(options.Root, "current.json")) || Directory.Exists(Path.Combine(options.Root, "versions")))
            {
                int code = RunInstallScript(delegate(string directory)
                {
                    return "-Uninstall -InstallRoot \"" + options.Root + "\"" + (options.RemoveSettings ? " -RemoveSettings" : "");
                }, out output);
                if (code != 0) return code;
            }
            else output = "DisplayWeave's program files were already removed.";
            if (IsDefaultRoot(options.Root)) Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false);
            string self = Path.GetFullPath(Application.ExecutablePath);
            if (String.Equals(Path.GetDirectoryName(self), Path.GetFullPath(options.Root), StringComparison.OrdinalIgnoreCase))
            {
                // A running program cannot delete itself; remove it and the then-empty folder once this process ends.
                ProcessStartInfo cleanup = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\" & rd \"" + options.Root + "\"");
                cleanup.CreateNoWindow = true;
                cleanup.UseShellExecute = false;
                Process.Start(cleanup).Dispose();
            }
            else
            {
                string copy = Path.Combine(options.Root, Program.UninstallerName);
                if (File.Exists(copy)) File.Delete(copy);
                if (Directory.Exists(options.Root) && Directory.GetFileSystemEntries(options.Root).Length == 0) Directory.Delete(options.Root);
            }
            return 0;
        }

        private static bool IsDefaultRoot(string root)
        {
            return String.Equals(Path.GetFullPath(root).TrimEnd('\\'), Program.DefaultRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void Register(string root, string uninstaller)
        {
            string version = Version;
            long bytes = 0;
            string versionDirectory = Path.Combine(root, "versions", version);
            foreach (string file in Directory.GetFiles(versionDirectory, "*", SearchOption.AllDirectories)) bytes += new FileInfo(file).Length;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
            {
                key.SetValue("DisplayName", "DisplayWeave");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "DisplayWeave");
                key.SetValue("DisplayIcon", Path.Combine(versionDirectory, "DisplayWeave.exe"));
                key.SetValue("InstallLocation", root);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --quiet");
                key.SetValue("EstimatedSize", (int)Math.Min(Int32.MaxValue, bytes / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // Extracts the embedded script and package to a private temporary folder and runs the script there.
        private static int RunInstallScript(Func<string, string> arguments, out string output)
        {
            string directory = Path.Combine(Path.GetTempPath(), "DisplayWeaveSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                foreach (string name in new[] { "install.ps1", PackageName, PackageName + ".sha256" })
                    using (Stream input = assembly.GetManifestResourceStream(name))
                    using (FileStream file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew))
                        input.CopyTo(file);
                ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(directory, "install.ps1") + "\" " + arguments(directory));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.WorkingDirectory = directory;
                StringBuilder text = new StringBuilder();
                using (Process process = new Process { StartInfo = start })
                {
                    DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    lock (text) output = text.ToString().Trim();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = "Setup could not run the installer: " + ex.Message;
                return 1;
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Program.Options options;
        private readonly Label message = new Label();
        private readonly CheckBox first = new CheckBox(), second = new CheckBox();
        private readonly TextBox details = new TextBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button primary = new Button(), cancel = new Button();
        private bool finished, running;

        internal SetupForm(Program.Options options)
        {
            this.options = options;
            string version = Setup.Version, installed = Setup.InstalledVersion(options.Root);
            Text = options.Uninstall ? "Uninstall DisplayWeave" : "DisplayWeave " + version + " Setup";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 330);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var title = new Label { Text = options.Uninstall ? "Remove DisplayWeave" : (installed == null ? "Install DisplayWeave " + version : "Update DisplayWeave to " + version),
                Font = new Font("Segoe UI", 14F, FontStyle.Bold), Location = new Point(20, 16), AutoSize = true };
            message.Location = new Point(22, 56);
            message.Size = new Size(476, 80);
            if (options.Uninstall)
            {
                message.Text = "DisplayWeave will be closed and removed from this Windows account, including its Start menu shortcut and startup entry.";
                first.Text = "Also delete my regions and app rules";
            }
            else
            {
                message.Text = (installed == null ? "DisplayWeave will be installed for your Windows account in " + options.Root + "."
                    : "Version " + installed + " is installed. Your regions and app rules are kept.") +
                    " Administrator rights are not needed; a running DisplayWeave is closed and reopened.";
                first.Text = "Start DisplayWeave when I sign in";
                first.Checked = Setup.StartupEnabled();
                second.Text = "Open DisplayWeave when setup finishes";
                second.Checked = true;
            }
            first.Location = new Point(22, 140); first.AutoSize = true;
            second.Location = new Point(22, 166); second.AutoSize = true; second.Visible = !options.Uninstall;
            details.Location = new Point(22, 198); details.Size = new Size(476, 76);
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.Visible = false;
            progress.Location = new Point(22, 198); progress.Size = new Size(476, 18); progress.Style = ProgressBarStyle.Marquee; progress.Visible = false;
            primary.Text = options.Uninstall ? "Uninstall" : (installed == null ? "Install" : "Update");
            primary.Location = new Point(316, 284); primary.Size = new Size(88, 30);
            cancel.Text = "Cancel";
            cancel.Location = new Point(410, 284); cancel.Size = new Size(88, 30);
            primary.Click += delegate { if (finished) Close(); else Run(); };
            cancel.Click += delegate { Close(); };
            AcceptButton = primary;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { title, message, first, second, details, progress, primary, cancel });
            // Positions above are at 96 DPI; fonts already follow the screen, so scale the layout to match.
            using (Graphics graphics = CreateGraphics())
                if (graphics.DpiX != 96F) Scale(new SizeF(graphics.DpiX / 96F, graphics.DpiY / 96F));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The installer script keeps running on its own; closing now would hide its result.
            if (running) e.Cancel = true;
            base.OnFormClosing(e);
        }

        private void Run()
        {
            running = true;
            primary.Enabled = cancel.Enabled = first.Enabled = second.Enabled = false;
            progress.Visible = true;
            details.Visible = false;
            if (options.Uninstall) options.RemoveSettings = first.Checked;
            else
            {
                bool enabled = Setup.StartupEnabled();
                options.Startup = first.Checked == enabled ? "Preserve" : (first.Checked ? "Enable" : "Disable");
                options.NoLaunch = !second.Checked;
            }
            new Thread(delegate()
            {
                string output;
                int code;
                try { code = options.Uninstall ? Setup.Uninstall(options, out output) : Setup.Install(options, out output); }
                catch (Exception ex) { code = 1; output = ex.Message; }
                BeginInvoke((Action)delegate { Done(code, output); });
            }) { IsBackground = true }.Start();
        }

        private void Done(int code, string output)
        {
            running = false;
            progress.Visible = false;
            details.Visible = true;
            details.Text = output;
            if (code == 0)
            {
                message.Text = options.Uninstall ? "DisplayWeave was removed." : "DisplayWeave " + Setup.Version + " is installed. Open it from the Start menu.";
                finished = true;
                primary.Text = "Close";
                primary.Enabled = true;
            }
            else
            {
                message.Text = (options.Uninstall ? "DisplayWeave was not removed." : "Setup did not finish. The previous installation, if any, was kept.") + " Details:";
                primary.Text = "Retry";
                primary.Enabled = cancel.Enabled = first.Enabled = second.Enabled = true;
            }
        }
    }
}
