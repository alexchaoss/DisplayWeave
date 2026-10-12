using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The settings window and tray icon. This file holds startup, the handling timer, the tray, hotkeys and exit;
    // each tab and the update check are in their own MainForm.*.cs file. Actions call RegionService.
    public sealed partial class MainForm : Form
    {
        private readonly string dataDirectory;
        private readonly RegionService service;
        private readonly LayoutSettings settings;
        private readonly ApplicationRules rules;
        private readonly FullscreenController fullscreen;
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private ToolStripMenuItem trayPause;
        private bool exitRequested;
        private bool trayHintShown;
        private bool startInTray;
        private readonly Timer timer = new Timer();
        private MaximizeWatcher maximizeWatcher;
        private bool ticking;
        private readonly List<int> registeredHotkeys = new List<int>();
        private bool updating;
        private string notice = "Apps automatically use their current region when maximized or fullscreen. Use a manual rule if needed.";
        private string lastEngineError = "";
        private string hotkeyNotice = "";

        public MainForm(string automaticExecutableFilter = null, string dataDirectory = null, bool startInTray = false)
        {
            this.dataDirectory = Path.GetFullPath(dataDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            this.startInTray = startInTray;
            service = new RegionService(this.dataDirectory, automaticExecutableFilter);
            settings = service.Layout;
            rules = service.Rules;
            fullscreen = service.Engine;
            if (service.LoadWarning.Length > 0) notice = service.LoadWarning;
            service.Saved += delegate { layoutDirty = false; SetLayoutState("Layout and app rules saved", false); };
            Text = "DisplayWeave " + Program.Version;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (!String.IsNullOrWhiteSpace(automaticExecutableFilter)) Text = "DisplayWeave — interface test";
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(246, 248, 252);
            ClientSize = new Size(1180, 850);
            MinimumSize = new Size(1040, 680);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildInterface();
            LoadMonitors();
            updating = true;
            workArea.Checked = settings.UseWorkArea;
            automaticRegions.Checked = settings.AutomaticRegions;
            updating = false;
            ReloadZones(0);
            RefreshWindows();
            RefreshRules();
            InitializeTray();
            if (!layoutDirty) SetLayoutState("Saved layout loaded · " + settings.Zones.Count + " regions · " + settings.GridStepPixels + " px grid", false);
            timer.Interval = 250;
            timer.Tick += delegate { TickNow(); };
            timer.Start();
            // A window fitted from outside first fills the display; fit it as soon as it maximizes, not on the next tick.
            maximizeWatcher = new MaximizeWatcher(TickNow);
            // Administrator mode never updates: the setup would run elevated from a user-writable folder.
            if (installRoot != null && String.IsNullOrWhiteSpace(automaticExecutableFilter) && !ElevationRestart.IsElevated())
            {
                updateTimer.Interval = 60000;
                updateTimer.Tick += delegate { UpdateTick(); };
                updateTimer.Start();
            }
            FormClosing += HandleFormClosing;
        }

        // Shows an action's result; the engine message it produced counts as already shown.
        private void Report(string message)
        {
            lastEngineError = fullscreen.LastMessage;
            notice = message;
            UpdateStatus();
        }

        // Runs an action that saves the layout and rules. A failed save is shown instead of its result.
        private bool RunSaving(Func<string> action, bool rulesChanged)
        {
            // End numeric editing first: a freshly typed grid step must be saved too.
            ValidateChildren();
            string message;
            bool saved = true;
            try { message = action(); }
            catch (IOException ex)
            {
                message = "Could not save settings: " + ex.Message;
                SetLayoutState(message, true);
                saved = false;
            }
            if (rulesChanged) RefreshRules();
            Report(message);
            return saved;
        }

        private void RestoreAll()
        {
            // A pause chosen while waiting for the display stays: nothing resumes by itself when it returns.
            displayWait = Rectangle.Empty;
            Report(service.Pause());
        }

        public void PauseFullscreenRules()
        {
            RestoreAll();
        }

        private void InitializeTray()
        {
            trayMenu.Items.Add("Open DisplayWeave", null, delegate { ShowSettingsWindow(); });
            trayPause = new ToolStripMenuItem("Pause region handling");
            trayPause.Click += delegate { if (fullscreen.Paused) ResumeHandling(); else RestoreAll(); };
            trayMenu.Items.Add(trayPause);
            trayUpdate = new ToolStripMenuItem("Install update now") { Visible = false };
            trayUpdate.Click += delegate { if (updateSetup != null) InstallUpdate(); };
            trayMenu.Items.Add(trayUpdate);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit DisplayWeave", null, delegate { ExitApplication(); });
            trayIcon.Icon = Icon;
            trayIcon.Text = "DisplayWeave " + Program.Version;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettingsWindow(); };
            trayIcon.Visible = true;
        }

        private void TickNow()
        {
            // A maximize event can arrive while a tick waits on Windows; the running tick already covers it.
            if (ticking) return;
            ticking = true;
            try
            {
                if (!displayWait.IsEmpty) ResumeWhenDisplayReturns();
                // The engine isolates each app's errors; one that still escapes affects every app, so handling
                // pauses. It must not end DisplayWeave or raise a dialog every tick.
                try { service.Tick(); }
                catch (Exception ex) { ReportInternalError(ex, true); }
                UpdateStatus();
            }
            finally { ticking = false; }
        }

        private void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    trayIcon.ShowBalloonTip(2500, "DisplayWeave is still running",
                        layoutDirty ? "Your edits are still pending. Open DisplayWeave from the tray and Save layout to apply them."
                            : "Double-click the tray icon to reopen. Choose Exit DisplayWeave to stop region handling.", ToolTipIcon.Info);
                }
                return;
            }
            if (!SaveLayout(false) && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                exitRequested = false;
                ShowSettingsWindow();
                MessageBox.Show(this, notice + "\n\nYour edits are still open. Save successfully before exiting.", "Layout not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            maximizeWatcher.Dispose();
            timer.Stop();
            service.Dispose();
            trayIcon.Visible = false;
        }

        private void ExitApplication()
        {
            exitRequested = true;
            Close();
        }

        private void RestartAsAdministrator()
        {
            if (ElevationRestart.IsElevated() || !RunSaving(delegate { service.Save(); return notice; }, false)) return;
            try
            {
                using (var current = Process.GetCurrentProcess())
                using (var next = Process.Start(ElevationRestart.CreateStartInfo(Application.ExecutablePath,
                    dataDirectory, fullscreen.AutomaticExecutableFilter, current.Id)))
                {
                    if (next == null) throw new InvalidOperationException("Windows did not start the new DisplayWeave instance.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                notice = ex.NativeErrorCode == 1223
                    ? "Administrator restart cancelled. DisplayWeave is still running."
                    : "DisplayWeave could not restart as administrator: " + ex.Message;
                UpdateStatus();
                return;
            }
            catch (Exception ex)
            {
                notice = "DisplayWeave could not restart as administrator: " + ex.Message;
                UpdateStatus();
                return;
            }
            // The approved child waits for this process to save, release its
            // helpers and exit before acquiring the single-instance mutex.
            ExitApplication();
        }

        private void ShowSettingsWindow()
        {
            startInTray = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (value && startInTray)
            {
                startInTray = false;
                if (!IsHandleCreated) CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int failed = 0;
            for (int i = 0; i <= 10; i++)
            {
                uint key = i == 10 ? (uint)Keys.Back : (uint)Keys.D0 + (uint)i;
                if (RegisterHotKey(Handle, 800 + i, 0x4007, key)) registeredHotkeys.Add(800 + i); else failed++;
            }
            hotkeyNotice = failed == 0 ? "" : "  ·  " + failed + " shortcuts unavailable (already in use).";
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            foreach (int id in registeredHotkeys) UnregisterHotKey(Handle, id);
            registeredHotkeys.Clear();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowSettingsMessage) { ShowSettingsWindow(); return; }
            // The new version adopts the windows fitted from outside where they are, so they are not returned first.
            if (m.Msg == Program.ExitForUpdateMessage) { fullscreen.LeaveFittedWindows(); ExitApplication(); return; }
            if (m.Msg == 0x312)
            {
                int index = m.WParam.ToInt32() - 800;
                if (index == 10) RestoreAll();
                else if (index == 0) ReleaseWindow(WindowManager.ForegroundWindow);
                else if (index >= 1 && index <= 9) Assign(WindowManager.ForegroundWindow, index - 1);
            }
            else if (m.Msg == 0x7E && screens != null && displayWait.IsEmpty)
            {
                // A game in exclusive fullscreen changes the display until it leaves: keep the layout, pause and resume
                // when the display is back. While waiting, later changes are left to the timer.
                bool wasRunning = !fullscreen.Paused;
                fullscreen.Pause();
                lastEngineError = fullscreen.LastMessage;
                if (wasRunning && !layoutDirty && rulerLayout != null)
                {
                    displayWait = rulerLayout.Area;
                    Report("The display changed, as a game in exclusive fullscreen does. Region handling is paused and resumes when the display is back to " +
                        displayWait.Width.ToString("N0") + " × " + displayWait.Height.ToString("N0") + ". To use the display as it is now, choose Save layout.");
                }
                else BeginInvoke((Action)delegate { LoadMonitors(); ReloadZones(0); notice = "Display configuration changed. Rules paused; review the layout, then Apply saved rules."; UpdateStatus(); });
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (maximizeWatcher != null) maximizeWatcher.Dispose();
                trayIcon.Visible = false; trayIcon.Dispose(); trayMenu.Dispose(); hints.Dispose(); timer.Dispose(); updateTimer.Dispose(); service.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
