using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The settings window and tray icon. This file holds startup, the handling timer, the tray and exit; each tab,
    // layout profiles and the update check are in their own MainForm.*.cs file. Actions call RegionService.
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
        // Ticks every 250 ms until then, after a window moved; otherwise once a second while this window is hidden.
        private DateTime fastTicksUntil;
        // A Borderless in region rule offered in a notification, applied if the user clicks it; one offer per app per session.
        private HandledApp offeredRule;
        private readonly HashSet<string> offeredApps = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<int> registeredHotkeys = new List<int>();
        private bool updating;
        private string notice = "Apps automatically use their current region when maximized or fullscreen. Use a manual rule if needed.";
        private string lastEngineError = "";
        private string hotkeyNotice = "";

        public MainForm(string automaticExecutableFilter = null, string dataDirectory = null, bool startInTray = false)
        {
            this.dataDirectory = Path.GetFullPath(dataDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            this.startInTray = startInTray;
            ActivityLog.Open(this.dataDirectory);
            ActivityLog.Write("DisplayWeave " + Program.Version + " started" + (ElevationRestart.IsElevated() ? " as administrator." : "."));
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
            RefreshRules();
            RefreshProfiles();
            InitializeTray();
            if (!layoutDirty) SetLayoutState("Saved layout loaded · " + settings.Zones.Count + " regions · " + settings.GridStepPixels + " px grid", false);
            timer.Interval = 250;
            timer.Tick += delegate { TickNow(); };
            timer.Start();
            // A window fitted from outside first fills the display; fit it as soon as it maximizes, not on the next tick.
            maximizeWatcher = new MaximizeWatcher(TickNow, delegate(IntPtr window)
            {
                fastTicksUntil = DateTime.UtcNow.AddSeconds(2);
                if (timer.Interval != 250) timer.Interval = 250;
                // A fullscreen Chromium window spreads over the whole display on each click or focus change.
                if (fullscreen.RefitNow(window)) TickNow();
            });
            // Administrator mode never updates: the setup would run elevated from a user-writable folder.
            if (installRoot != null && String.IsNullOrWhiteSpace(automaticExecutableFilter) && !ElevationRestart.IsElevated())
            {
                updateTimer.Interval = 1000; // the first tick checks right after start; UpdateTick then slows it to a minute
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
            trayProfiles = new ToolStripMenuItem("Layout profile");
            trayMenu.Items.Add(trayProfiles);
            trayMenu.Opening += delegate { RefreshTrayProfiles(); RefreshGoBack(); };
            RefreshTrayProfiles();
            trayUpdate = new ToolStripMenuItem("Install update now") { Visible = false };
            trayUpdate.Click += delegate { if (updateSetup != null) InstallUpdate(); };
            trayMenu.Items.Add(trayUpdate);
            trayGoBack = new ToolStripMenuItem { Visible = false };
            trayGoBack.Click += delegate { GoBack(); };
            trayMenu.Items.Add(trayGoBack);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit DisplayWeave", null, delegate { ExitApplication(); });
            trayIcon.Icon = Icon;
            trayIcon.Text = "DisplayWeave " + Program.Version;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettingsWindow(); };
            trayIcon.BalloonTipClicked += delegate { HandledApp offer = offeredRule; offeredRule = null; if (offer != null) ApplyOfferedRule(offer); };
            trayIcon.Visible = true;
        }

        // Every notification goes through here: clicking one other than the rule offer must not save that rule.
        private void ShowBalloon(string title, string text, ToolTipIcon icon, HandledApp offer = null)
        {
            offeredRule = offer;
            trayIcon.ShowBalloonTip(offer != null ? 10000 : 5000, title, text, icon);
        }

        // A borderless game that resized itself back: offer the rule that fits it, once per app this session.
        private void OfferBorderlessRule(HandledApp app)
        {
            int index = settings.Zones.FindIndex(delegate(Zone zone) { return zone.ToPixels(Area) == app.Region; });
            if (index < 0 || !offeredApps.Add(app.Target.ProcessKey)) return;
            ShowBalloon(Path.GetFileName(app.Target.ExecutablePath) + " resized itself back", "Click to give it a Borderless in region rule for " +
                settings.Zones[index].Name + ". Then set it to Windowed in its own settings.", ToolTipIcon.Info, app);
        }

        private void ApplyOfferedRule(HandledApp offer)
        {
            ApplicationIdentity current;
            string error;
            int index = settings.Zones.FindIndex(delegate(Zone zone) { return zone.ToPixels(Area) == offer.Region; });
            if (!FullscreenController.TryInspect(offer.Target.Handle, out current, out error) || current.Key != offer.Target.Key)
                Report(Path.GetFileName(offer.Target.ExecutablePath) + " has closed, so no rule was saved.");
            else if (index < 0) Report("The layout changed since the offer. Add the rule for " + Path.GetFileName(offer.Target.ExecutablePath) + " on App rules.");
            else Assign(offer.Target.Handle, index, ApplicationAction.BorderlessInRegion);
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
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) MarkHealthy();
                HandledApp offer = fullscreen.TakeSuggestion();
                if (offer != null) OfferBorderlessRule(offer);
                UpdateStatus();
                // Maximizing is caught at once by the watcher; the timer only needs to be quick while windows move,
                // this window is open or a helper is working.
                int interval = Visible || fullscreen.Count + fullscreen.PendingCount > 0 || DateTime.UtcNow < fastTicksUntil ? 250 : 1000;
                if (timer.Interval != interval) timer.Interval = interval;
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
                    ShowBalloon("DisplayWeave is still running",
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
            MarkHealthy();
            ActivityLog.Write("DisplayWeave exited.");
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
            RegisterShortcuts();
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
            if (m.Msg == Program.ExitForUpdateMessage)
            {
                ActivityLog.Write("Closing for an update; windows fitted from outside stay where they are.");
                fullscreen.LeaveFittedWindows();
                ExitApplication();
                return;
            }
            if (m.Msg == 0x312) RunShortcut(m.WParam.ToInt32() - FirstHotkeyId);
            else if (m.Msg == 0x7E && screens != null && displayWait.IsEmpty)
            {
                // A game in exclusive fullscreen changes the display until it leaves: keep the layout, pause and resume
                // when the display is back. Without such a game the change stays (DisplayChangedForGood). While
                // waiting, later changes are left to the timer.
                bool wasRunning = !fullscreen.Paused;
                fullscreen.Pause();
                lastEngineError = fullscreen.LastMessage;
                if (wasRunning && !layoutDirty && rulerLayout != null)
                {
                    displayWait = rulerLayout.Area;
                    displayWaitSince = DateTime.UtcNow;
                    Report("The display changed, as a game in exclusive fullscreen does. Region handling is paused and resumes when the display is back to " +
                        displayWait.Width.ToString("N0") + " × " + displayWait.Height.ToString("N0") + ". To use the display as it is now, choose Use this display size.");
                }
                else BeginInvoke((Action)DisplayChangedForGood);
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
