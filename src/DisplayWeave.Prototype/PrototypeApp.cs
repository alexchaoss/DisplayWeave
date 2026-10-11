using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DisplayWeave.Prototype
{
    public static class Program
    {
        public const string Version = "0.1.0-preview";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--worker") return new SessionWorker(args[1]).Run();
            if (args.Length == 2 && args[0] == "--self-test") return PrototypeTests.Run(args[1]);
            if (args.Length != 0) return 2;
            bool created;
            using (var instance = new Mutex(true, @"Local\DisplayWeavePrototype.UI", out created))
            {
                if (!created) { MessageBox.Show("DisplayWeave Prototype is already open.", "DisplayWeave Prototype"); return 1; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PrototypeForm());
            }
            return 0;
        }
    }

    public sealed class RegionChoice
    {
        public string Name;
        public Rectangle Bounds;
        public override string ToString() { return Name + "  (" + Bounds.X + ", " + Bounds.Y + "  " + Bounds.Width + " × " + Bounds.Height + ")"; }
    }

    public sealed class PrototypeForm : Form
    {
        private readonly ListBox apps = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly ComboBox regions = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
        private readonly NumericUpDown x = Number(), y = Number(), width = Number(), height = Number();
        private readonly ComboBox duration = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly CheckBox interact = new CheckBox { Text = "Keyboard and mouse control (click the region to start; Ctrl+Alt+F10 ends the session)", AutoSize = true, Checked = true };
        private readonly CheckBox borderless = new CheckBox { Text = "Remove the window frame while it is on the virtual display (restored afterwards)", AutoSize = true, Checked = true };
        private readonly Button start = new Button { Text = "Start", Width = 110, Height = 32 };
        private readonly Button stop = new Button { Text = "Stop", Width = 110, Height = 32, Enabled = false };
        private readonly Button restore = new Button { Text = "Restore window", Width = 130, Height = 32, Enabled = false };
        private readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 250 };
        private Process worker;
        private string session, lastMessage;
        private bool closeWhenDone, sessionEnded;

        public PrototypeForm()
        {
            Text = "DisplayWeave Prototype " + Program.Version;
            MinimumSize = new Size(760, 560);
            Size = new Size(860, 680);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(820, 0),
                Text = "Experimental: shows one app from a temporary virtual display inside a region of your screen. Nothing happens until you press Start. " +
                    "Windows asks for administrator approval to create the display; it is removed when the session ends. No code is injected into the app."
            });

            var appGroup = new GroupBox { Text = "1. Application", Dock = DockStyle.Fill };
            var refresh = new Button { Text = "Refresh", Dock = DockStyle.Right, Width = 90 };
            appGroup.Controls.Add(apps); appGroup.Controls.Add(refresh);
            layout.Controls.Add(appGroup);

            var regionGroup = new GroupBox { Text = "2. Region on your display (physical pixels)", Dock = DockStyle.Top, AutoSize = true };
            var regionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            regionFlow.Controls.Add(regions);
            regionFlow.SetFlowBreak(regions, true);
            foreach (var pair in new[] { Tuple.Create("X", x), Tuple.Create("Y", y), Tuple.Create("Width", width), Tuple.Create("Height", height) })
            {
                regionFlow.Controls.Add(new Label { Text = pair.Item1, AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                regionFlow.Controls.Add(pair.Item2);
            }
            regionGroup.Controls.Add(regionFlow);
            layout.Controls.Add(regionGroup);

            var sessionGroup = new GroupBox { Text = "3. Session", Dock = DockStyle.Top, AutoSize = true };
            var sessionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var durationRow = new FlowLayoutPanel { AutoSize = true };
            durationRow.Controls.Add(new Label { Text = "Duration", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            durationRow.Controls.Add(duration);
            duration.Items.AddRange(new object[] { "30 seconds", "60 seconds", "90 seconds" });
            duration.SelectedIndex = 2;
            sessionFlow.Controls.Add(durationRow); sessionFlow.Controls.Add(interact); sessionFlow.Controls.Add(borderless);
            sessionGroup.Controls.Add(sessionFlow);
            layout.Controls.Add(sessionGroup);

            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(start); buttons.Controls.Add(stop); buttons.Controls.Add(restore);
            layout.Controls.Add(buttons);
            layout.Controls.Add(log);
            Controls.Add(layout);

            refresh.Click += delegate { LoadApps(); };
            regions.SelectedIndexChanged += delegate
            {
                var choice = regions.SelectedItem as RegionChoice;
                if (choice != null) { x.Value = choice.Bounds.X; y.Value = choice.Bounds.Y; width.Value = choice.Bounds.Width; height.Value = choice.Bounds.Height; }
            };
            start.Click += delegate { StartSession(); };
            stop.Click += delegate { RequestStop(); };
            restore.Click += delegate { Append(SessionWorker.Recover(session)); restore.Enabled = false; };
            poll.Tick += delegate { Poll(); };

            LoadApps();
            LoadRegions();
            FindUnfinishedSession();
            string missing = Paths.MissingComponent();
            if (missing != null) Append("Not ready: missing " + missing + ". Reinstall with install-prototype.ps1 (it fetches and verifies the display driver).");
        }

        private static NumericUpDown Number() { return new NumericUpDown { Minimum = -32768, Maximum = 32767, Width = 80 }; }

        private void LoadApps()
        {
            object selected = apps.SelectedItem;
            apps.Items.Clear();
            foreach (var choice in Win32.ListWindows()) apps.Items.Add(choice);
            var previous = selected as WindowChoice;
            if (previous != null)
                for (int i = 0; i < apps.Items.Count; ++i)
                    if (((WindowChoice)apps.Items[i]).Handle == previous.Handle) apps.SelectedIndex = i;
        }

        // Regions come read-only from the legacy app's saved layout when one
        // exists; otherwise a centered 16:9 region on the primary display.
        private void LoadRegions()
        {
            var screen = Screen.PrimaryScreen;
            if (File.Exists(Paths.LegacyLayout))
            {
                try
                {
                    var settings = LayoutSettings.Load(Paths.LegacyLayout);
                    var monitor = Screen.AllScreens.FirstOrDefault(s => String.Equals(s.DeviceName, settings.MonitorDevice, StringComparison.OrdinalIgnoreCase)) ?? screen;
                    Rectangle area = settings.UseWorkArea ? monitor.WorkingArea : monitor.Bounds;
                    foreach (var zone in settings.Zones) regions.Items.Add(new RegionChoice { Name = zone.Name, Bounds = zone.ToPixels(area) });
                }
                catch (Exception error) { Append("Saved DisplayWeave layout could not be read: " + error.Message); }
            }
            Rectangle b = screen.Bounds;
            int w = Math.Min(1280, b.Width), h = Math.Min(720, b.Height);
            regions.Items.Add(new RegionChoice { Name = "Centered 1280 × 720 on the primary display", Bounds = new Rectangle(b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h) });
            regions.SelectedIndex = 0;
        }

        private void FindUnfinishedSession()
        {
            if (!Directory.Exists(Paths.Sessions)) return;
            var latest = new DirectoryInfo(Paths.Sessions).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).FirstOrDefault();
            if (latest == null || !File.Exists(Path.Combine(latest.FullName, "status.json"))) return;
            try
            {
                var status = JsonFile.ReadObject(Path.Combine(latest.FullName, "status.json"));
                bool unfinished = !Policy.Terminal(JsonFile.Text(status, "state")) && !WorkerAlive((int)JsonFile.Number(status, "workerPid"));
                if ((JsonFile.Flag(status, "recoveryNeeded") || unfinished) && File.Exists(Path.Combine(latest.FullName, "window.json")))
                {
                    session = latest.FullName; restore.Enabled = true;
                    Append("The previous session did not restore its window. Use Restore window if the app is still on the virtual display or misplaced.");
                }
            }
            catch (Exception error) { Append("Previous session status could not be read: " + error.Message); }
        }

        private static bool WorkerAlive(int pid)
        {
            try { return pid > 0 && Process.GetProcessById(pid).ProcessName.Equals("DisplayWeavePrototype", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        private void StartSession()
        {
            var choice = apps.SelectedItem as WindowChoice;
            if (choice == null) { Append("Choose an application first."); return; }
            string missing = Paths.MissingComponent();
            if (missing != null) { Append("Cannot start: missing " + missing + "."); return; }
            long created; string path;
            if (!Win32.ProcessIdentity(choice.ProcessId, out created, out path) || created != choice.ProcessCreated || !Win32.IsWindow(choice.Handle))
            {
                Append("That app closed or changed. Refreshing the list."); LoadApps(); return;
            }
            string id = Guid.NewGuid().ToString("N");
            session = Path.Combine(Paths.Sessions, id);
            Directory.CreateDirectory(session);
            var request = new SessionRequest
            {
                SchemaVersion = 1, SessionId = id, Mode = interact.Checked ? "interactive" : "visual",
                DurationSeconds = 30 * (duration.SelectedIndex + 1), Borderless = borderless.Checked,
                SourceHwnd = choice.Handle.ToInt64().ToString(), SourcePid = (int)choice.ProcessId, SourceStartedUtcFileTime = created.ToString(),
                SourceExecutable = path, ReturnHwnd = Handle.ToInt64().ToString(),
                Region = new[] { (int)x.Value, (int)y.Value, (int)width.Value, (int)height.Value }
            };
            string invalid = request.Validate(id);
            if (invalid != null) { Append(invalid); return; }
            JsonFile.Write(Path.Combine(session, "request.json"), request);
            sessionEnded = false;
            worker = Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = "--worker \"" + session + "\"", UseShellExecute = false, CreateNoWindow = true });
            lastMessage = null;
            Append("Starting " + choice.Title + " in " + RegionText(request.RegionRectangle) + " for up to " + request.DurationSeconds + " s.");
            SetRunning(true);
            poll.Start();
        }

        private void RequestStop()
        {
            if (session == null || worker == null || worker.HasExited) return;
            File.WriteAllText(Path.Combine(session, "stop.request"), DateTime.UtcNow.ToString("o"));
            Append("Stop requested.");
            stop.Enabled = false;
        }

        private void Poll()
        {
            string statusPath = Path.Combine(session, "status.json");
            Dictionary<string, object> status = null;
            try { if (File.Exists(statusPath)) status = JsonFile.ReadObject(statusPath); }
            catch (IOException) { return; }
            if (status != null)
            {
                string message = JsonFile.Text(status, "message");
                long remaining = JsonFile.Number(status, "remainingSeconds");
                if (message != lastMessage) { Append(message); lastMessage = message; }
                Text = "DisplayWeave Prototype " + Program.Version + " — " + JsonFile.Text(status, "state") + (remaining > 0 ? " " + remaining + " s" : "");
                if (JsonFile.Flag(status, "terminal"))
                {
                    Finish(JsonFile.Flag(status, "recoveryNeeded"));
                    return;
                }
            }
            if (worker.HasExited)
            {
                Append("The session worker ended unexpectedly (exit " + worker.ExitCode + "). The display lease removes itself when its time expires; use Restore window if the app is misplaced.");
                Finish(File.Exists(Path.Combine(session, "window.json")));
            }
        }

        private void Finish(bool recoveryNeeded)
        {
            poll.Stop();
            sessionEnded = true;
            SetRunning(false);
            restore.Enabled = recoveryNeeded;
            Text = "DisplayWeave Prototype " + Program.Version;
            LoadApps();
            if (closeWhenDone) Close();
        }

        private void SetRunning(bool running)
        {
            start.Enabled = !running; stop.Enabled = running; restore.Enabled = false;
            apps.Enabled = regions.Enabled = duration.Enabled = interact.Enabled = borderless.Enabled = !running;
            x.Enabled = y.Enabled = width.Enabled = height.Enabled = !running;
        }

        private void Append(string text)
        {
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing stops the session but the window stays until cleanup ends.
            if (worker != null && !worker.HasExited && !sessionEnded)
            {
                e.Cancel = true; closeWhenDone = true; RequestStop();
                Append("Closing after the session is cleaned up.");
            }
            base.OnFormClosing(e);
        }

        private static string RegionText(Rectangle r) { return r.Width + " × " + r.Height + " at (" + r.X + ", " + r.Y + ")"; }
    }
}
