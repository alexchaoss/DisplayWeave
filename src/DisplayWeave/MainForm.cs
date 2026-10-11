using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    public sealed partial class MainForm : Form
    {
        private readonly WindowManager manager;
        private readonly string settingsPath;
        private readonly string dataDirectory;
        private readonly LayoutSettings settings;
        private readonly string rulesPath;
        private readonly ApplicationRules rules;
        private readonly FullscreenController fullscreen = new FullscreenController();
        private readonly ComboBox monitors = new ComboBox();
        private readonly ComboBox windows = new ComboBox();
        private readonly ComboBox presets = new ComboBox();
        private readonly CheckBox workArea = new CheckBox();
        private readonly CheckBox automaticRegions = new CheckBox();
        private readonly ComboBox savedRules = new ComboBox();
        private readonly Label pickedApp = new Label();
        private readonly Button picker = new Button();
        private readonly ListBox zoneList = new ListBox();
        private readonly NumericUpDown gridSpacing = new NumericUpDown();
        private readonly Label regionDimensions = new Label();
        private readonly TextBox status = new TextBox();
        private readonly Label layoutState = new Label();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private ToolStripMenuItem trayPause;
        private bool exitRequested;
        private bool trayHintShown;
        private bool startInTray;
        private bool layoutDirty;
        private readonly RulerCanvas canvas = new RulerCanvas();
        private RulerLayout rulerLayout;
        private readonly Timer timer = new Timer();
        private readonly Timer updateTimer = new Timer();
        private readonly string installRoot = Updater.InstallRoot(Application.ExecutablePath);
        private DateTime nextUpdateCheck = DateTime.UtcNow;
        private bool checkingForUpdate;
        private string updateSetup, updateVersion, updateNotice = "";
        private ToolStripMenuItem trayUpdate;
        private readonly List<int> registeredHotkeys = new List<int>();
        private Screen[] screens;
        private bool updating;
        private string notice = "Apps automatically use their current region when maximized or fullscreen. Use a manual rule if needed.";
        private string lastEngineError = "";
        private string hotkeyNotice = "";
        private bool picking;
        private ApplicationIdentity pickedIdentity;

        public MainForm(WindowManager manager, string automaticExecutableFilter = null, string dataDirectory = null, bool startInTray = false)
        {
            this.manager = manager;
            this.dataDirectory = Path.GetFullPath(dataDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            this.startInTray = startInTray;
            settingsPath = Path.Combine(this.dataDirectory, "layout.xml");
            rulesPath = Path.Combine(this.dataDirectory, "rules.xml");
            settings = LayoutSettings.Load(settingsPath);
            fullscreen.AutomaticExecutableFilter = automaticExecutableFilter;
            string ruleWarning;
            rules = ApplicationRules.Load(rulesPath, out ruleWarning);
            if (ruleWarning.Length > 0) notice = ruleWarning;
            Text = "DisplayWeave — virtual monitor preview";
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
            timer.Tick += delegate { fullscreen.Tick(manager.ListWindows(), rules, settings); UpdateStatus(); };
            timer.Start();
            if (installRoot != null && String.IsNullOrWhiteSpace(automaticExecutableFilter))
            {
                updateTimer.Interval = 60000;
                updateTimer.Tick += delegate { UpdateTick(); };
                updateTimer.Start();
            }
            FormClosing += HandleFormClosing;
        }

        // Checks the releases every six hours. A downloaded update installs by itself while this window
        // is closed and no app is maximized or fullscreen in a region, so a running game is never resized.
        private void UpdateTick()
        {
            if (updateSetup != null)
            {
                if (!Visible && !fullscreen.HasExpandedApp) InstallUpdate();
                return;
            }
            if (checkingForUpdate || DateTime.UtcNow < nextUpdateCheck) return;
            checkingForUpdate = true;
            nextUpdateCheck = DateTime.UtcNow.AddHours(6);
            string current = Path.GetFileName(Path.GetDirectoryName(Application.ExecutablePath));
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string version = null, setup = null, problem = null;
                try { setup = Updater.DownloadNewer(current, out version); }
                catch (Exception ex) { problem = ex.Message; } // a worker-thread exception would end DisplayWeave
                try { BeginInvoke((Action)delegate { UpdateDownloaded(setup, version, problem); }); }
                catch (InvalidOperationException) { } // DisplayWeave exited meanwhile
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateDownloaded(string setup, string version, string problem)
        {
            checkingForUpdate = false;
            updateNotice = problem != null ? "Update check failed: " + problem : "";
            if (setup == null) return;
            updateSetup = setup;
            updateVersion = version;
            trayUpdate.Text = "Install DisplayWeave " + version + " now";
            trayUpdate.Visible = true;
            updateNotice = "DisplayWeave " + version + " is ready. It installs when this window is closed and no app is maximized or fullscreen in a region.";
            if (!Visible && !fullscreen.HasExpandedApp) { InstallUpdate(); return; }
            trayIcon.ShowBalloonTip(5000, "DisplayWeave " + version + " is ready", "It installs when no app is maximized or fullscreen in a region. To install now, choose it in the tray menu.", ToolTipIcon.Info);
        }

        private void InstallUpdate()
        {
            string setup = updateSetup;
            updateSetup = null;
            trayUpdate.Visible = false;
            try
            {
                // The setup closes this instance with --exit-for-update, installs, then starts the new version.
                using (Process.Start(new ProcessStartInfo(setup, "--quiet --install-root \"" + installRoot + "\" --log \"" + setup + ".log\"") { UseShellExecute = false })) { }
            }
            catch (System.ComponentModel.Win32Exception ex) { updateNotice = "Could not start the DisplayWeave " + updateVersion + " setup: " + ex.Message; }
        }

        private Screen CurrentScreen { get { return screens != null && monitors.SelectedIndex >= 0 && monitors.SelectedIndex < screens.Length ? screens[monitors.SelectedIndex] : Screen.PrimaryScreen; } }
        private Rectangle Area { get { return workArea.Checked ? CurrentScreen.WorkingArea : CurrentScreen.Bounds; } }

        private void LoadMonitors()
        {
            updating = true;
            screens = Screen.AllScreens;
            monitors.Items.Clear();
            int selected = 0;
            for (int i = 0; i < screens.Length; i++)
            {
                monitors.Items.Add(screens[i].DeviceName + "  ·  " + screens[i].Bounds.Width + " × " + screens[i].Bounds.Height + (screens[i].Primary ? "  ·  Primary" : ""));
                if (screens[i].DeviceName == settings.MonitorDevice) selected = i;
            }
            monitors.SelectedIndex = selected;
            settings.MonitorDevice = CurrentScreen.DeviceName;
            updating = false;
        }

        private void RefreshWindows()
        {
            var old = windows.SelectedItem as WindowCandidate;
            windows.Items.Clear();
            foreach (WindowCandidate item in manager.ListWindows()) windows.Items.Add(item);
            if (old != null)
                for (int i = 0; i < windows.Items.Count; i++)
                    if (((WindowCandidate)windows.Items[i]).Handle == old.Handle) windows.SelectedIndex = i;
            UpdatePickedApp();
        }

        private void ReloadZones(int selected)
        {
            if (rulerLayout == null || rulerLayout.Area != Area)
            {
                rulerLayout = new RulerLayout(settings, Area);
                if (rulerLayout.WasRepaired)
                {
                    LayoutChanging();
                    notice = "The previous layout was adjusted to a non-overlapping ruler grid. Review the regions before applying saved rules.";
                    try
                    {
                        string backup = settingsPath + ".before-rulers.bak";
                        if (File.Exists(settingsPath) && !File.Exists(backup)) File.Copy(settingsPath, backup);
                    }
                    catch (IOException ex) { notice += " Layout backup could not be saved: " + ex.Message; }
                    catch (UnauthorizedAccessException ex) { notice += " Layout backup could not be saved: " + ex.Message; }
                }
            }
            updating = true;
            zoneList.Items.Clear();
            ruleRegion.Items.Clear();
            for (int i = 0; i < settings.Zones.Count; i++)
            {
                settings.Zones[i].Name = "Region " + (i + 1);
                zoneList.Items.Add(settings.Zones[i]);
                Rectangle region = settings.Zones[i].ToPixels(Area);
                ruleRegion.Items.Add(settings.Zones[i].Name + " · " + region.Width.ToString("N0") + " × " + region.Height.ToString("N0") + " px");
            }
            zoneList.SelectedIndex = Math.Max(0, Math.Min(selected, settings.Zones.Count - 1));
            SyncRuleRegion();
            canvas.Layout = rulerLayout;
            canvas.SelectedIndex = zoneList.SelectedIndex;
            gridSpacing.Value = rulerLayout.GridStep;
            canvas.Invalidate();
            updating = false;
            UpdateRegionDimensions();
            ShowRuleDetails(false);
            UpdateStatus();
        }

        private void UpdateRegionDimensions()
        {
            if (zoneList.SelectedIndex < 0 || zoneList.SelectedIndex >= settings.Zones.Count) { regionDimensions.Text = "Select a region."; return; }
            Rectangle r = settings.Zones[zoneList.SelectedIndex].ToPixels(Area);
            regionDimensions.Text = r.Width.ToString("N0") + " × " + r.Height.ToString("N0") + " px\nPosition " + (r.X - Area.X).ToString("N0") + ", " + (r.Y - Area.Y).ToString("N0") + " px";
        }

        private void ChangeGridSpacing()
        {
            if (rulerLayout == null) return;
            LayoutChanging();
            string error;
            if (!rulerLayout.TrySetGridStep((int)gridSpacing.Value, out error)) notice = error;
            else notice = "Ruler pins now snap every " + rulerLayout.GridStep.ToString("N0") + " pixels. Rules paused until you apply them.";
            ReloadZones(zoneList.SelectedIndex);
        }

        private void LayoutChanging()
        {
            fullscreen.Pause();
            lastEngineError = fullscreen.LastMessage;
            layoutDirty = true;
            SetLayoutState("Unsaved changes — Save layout to apply them", true);
            notice = "Region handling paused while editing. Save layout to apply your changes.";
        }

        private void Assign(IntPtr handle, int regionIndex, ApplicationAction action = ApplicationAction.ForceFullscreen)
        {
            if (regionIndex < 0 || regionIndex >= settings.Zones.Count) { notice = "There is no region for that shortcut."; UpdateStatus(); return; }
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(handle, out identity, out error)) { notice = error; UpdateStatus(); return; }
            var rule = new ApplicationRule { ExecutablePath = identity.ExecutablePath, WindowClass = identity.WindowClass,
                DisplayName = Path.GetFileNameWithoutExtension(identity.ExecutablePath), Action = action,
                MonitorDevice = CurrentScreen.DeviceName, ZoneId = settings.Zones[regionIndex].Id };
            rules.Put(rule);
            if (!SaveRulesAndLayout()) return;
            RefreshRules();
            Rectangle targetRegion;
            List<Rectangle> layoutRegions;
            if (!FullscreenController.TryResolveRegion(rule, settings, out targetRegion) ||
                !FullscreenController.TryResolveLayout(settings, out layoutRegions))
            {
                notice = "The region changed while saving. Review the layout and assign the application again.";
                UpdateStatus();
                return;
            }
            if (action == ApplicationAction.MaximizeInRegion)
            {
                // Nothing is attached; the next tick fits the window whenever it is maximized.
                fullscreen.Readmit(identity);
                notice = "Saved for " + Path.GetFileName(identity.ExecutablePath) + ": maximize it from its title bar and DisplayWeave fits it into " +
                    settings.Zones[regionIndex].Name + ". Nothing is loaded into the app.";
                lastEngineError = notice;
                UpdateStatus();
                return;
            }
            fullscreen.Attach(identity, targetRegion, true, layoutRegions);
            notice = fullscreen.LastMessage;
            lastEngineError = notice;
            UpdateStatus();
        }

        private void RestoreAll()
        {
            fullscreen.Pause();
            notice = fullscreen.LastMessage;
            lastEngineError = notice;
            UpdateStatus();
        }

        public void PauseFullscreenRules()
        {
            RestoreAll();
        }

        private void ApplySavedRules()
        {
            if (!SaveRulesAndLayout()) return;
            fullscreen.Resume();
            notice = fullscreen.LastMessage;
            lastEngineError = notice;
            UpdateStatus();
        }

        private void RestoreSelected()
        {
            var item = windows.SelectedItem as WindowCandidate;
            if (item == null) return;
            ReleaseWindow(item.Handle);
        }

        private void ReleaseWindow(IntPtr handle)
        {
            ApplicationIdentity identity;
            string error;
            if (FullscreenController.TryInspect(handle, out identity, out error))
            { fullscreen.ReleaseProcess(identity); notice = fullscreen.LastMessage; lastEngineError = notice; }
            else notice = error;
            UpdateStatus();
        }

        private void IgnoreSelected()
        {
            var item = windows.SelectedItem as WindowCandidate;
            if (item == null) { notice = "Select an application first."; UpdateStatus(); return; }
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(item.Handle, out identity, out error)) { notice = error; UpdateStatus(); return; }
            fullscreen.ReleaseProcess(identity);
            lastEngineError = fullscreen.LastMessage;
            rules.Put(new ApplicationRule { ExecutablePath = identity.ExecutablePath, WindowClass = identity.WindowClass,
                DisplayName = Path.GetFileNameWithoutExtension(identity.ExecutablePath), Action = ApplicationAction.Ignore });
            if (SaveRulesAndLayout()) notice = "Ignore rule saved for " + Path.GetFileName(identity.ExecutablePath) + ". Its fullscreen behavior is unchanged.";
            RefreshRules(); UpdateStatus();
        }

        private void RemoveRule()
        {
            var selected = savedRules.SelectedItem as ApplicationRule;
            if (selected == null) return;
            foreach (WindowCandidate window in manager.ListWindows())
            {
                ApplicationIdentity identity;
                string error;
                if (FullscreenController.TryInspect(window.Handle, out identity, out error) && selected.Matches(identity)) fullscreen.ReleaseProcess(identity);
            }
            rules.Rules.Remove(selected);
            lastEngineError = fullscreen.LastMessage;
            if (SaveRulesAndLayout()) notice = "Saved rule removed and matching applications released.";
            RefreshRules(); UpdateStatus();
        }

        private bool SaveRulesAndLayout()
        {
            ValidateChildren();
            try
            {
                settings.Save(settingsPath); rules.Save(rulesPath);
                layoutDirty = false;
                SetLayoutState("Layout and app rules saved", false);
                return true;
            }
            catch (Exception ex) { notice = "Could not save settings: " + ex.Message; SetLayoutState(notice, true); UpdateStatus(); return false; }
        }

        private void RefreshRules()
        {
            updating = true;
            savedRules.Items.Clear();
            foreach (ApplicationRule rule in rules.Rules) savedRules.Items.Add(rule);
            if (savedRules.Items.Count > 0) savedRules.SelectedIndex = savedRules.Items.Count - 1;
            updating = false;
            ShowRuleDetails(false);
            UpdateRuleActions();
        }

        private void ShowRuleDetails(bool announce = true)
        {
            if (updating) return;
            var rule = savedRules.SelectedItem as ApplicationRule;
            if (rule == null)
            {
                savedRuleDetails.Text = "No saved overrides. Add one above if an app needs it.";
                hints.SetToolTip(savedRuleDetails, null);
                return;
            }
            string details;
            if (rule.Action == ApplicationAction.Ignore) details = "Ignore: " + rule.ExecutablePath + " [" + rule.WindowClass + "]";
            else
            {
                Rectangle region;
                bool exists = FullscreenController.TryResolveRegion(rule, settings, out region);
                details = exists ? (rule.Action == ApplicationAction.MaximizeInRegion ? "Maximized window target: " : "Fullscreen target: ") + region.Width + " × " + region.Height + " at " + region.X + ", " + region.Y + " · " + rule.ExecutablePath
                    : "This rule's region or monitor is missing. Select its app and save a new region; it will not be retargeted automatically.";
            }
            savedRuleDetails.Text = details;
            hints.SetToolTip(savedRuleDetails, details);
            if (announce) { notice = details; UpdateStatus(); }
        }

        private void UpdatePickedApp()
        {
            pickedIdentity = null;
            var item = windows.SelectedItem as WindowCandidate;
            if (item == null) { pickedApp.Text = "No application selected"; return; }
            ApplicationIdentity identity;
            string error;
            if (FullscreenController.TryInspect(item.Handle, out identity, out error))
            {
                pickedIdentity = identity;
                pickedApp.Text = identity.ExecutablePath + "   ·   " + identity.WindowClass + (identity.Is32Bit ? "   ·   32-bit" : "   ·   64-bit");
            }
            else pickedApp.Text = error;
        }

        private void SelectWindowAtCursor()
        {
            IntPtr handle = GetAncestor(WindowFromPoint(Cursor.Position), 2);
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(handle, out identity, out error)) { notice = error; UpdateStatus(); return; }
            RefreshWindows();
            for (int i = 0; i < windows.Items.Count; i++)
                if (((WindowCandidate)windows.Items[i]).Handle == handle) { windows.SelectedIndex = i; notice = "Application selected. Choose a target region and Force fullscreen, or Ignore this app."; UpdateStatus(); return; }
            notice = "The selected window is not an eligible application window."; UpdateStatus();
        }

        private bool SaveLayout(bool showResult)
        {
            // End numeric editing before serializing: a freshly typed grid step
            // must not remain only in the edit control when Save is clicked.
            ValidateChildren();
            try
            {
                settings.Save(settingsPath);
                layoutDirty = false;
                if (showResult)
                {
                    fullscreen.Resume();
                    fullscreen.Tick(manager.ListWindows(), rules, settings);
                    lastEngineError = fullscreen.LastMessage;
                    notice = "Layout saved and applied. Apps can now maximize or go fullscreen in their current region.";
                }
                SetLayoutState("Saved" + (showResult ? " and applied" : "") + " · " + settings.Zones.Count + " regions · " + settings.GridStepPixels + " px grid", false);
            }
            catch (Exception ex)
            {
                notice = "Could not save layout: " + ex.Message;
                SetLayoutState(notice, true);
                if (showResult) MessageBox.Show(this, notice + "\n\nFile: " + settingsPath, "Layout not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus();
                return false;
            }
            UpdateStatus();
            return true;
        }

        private void SetLayoutState(string text, bool needsAttention)
        {
            layoutState.Text = text;
            hints.SetToolTip(layoutState, text);
            layoutState.ForeColor = needsAttention ? Color.FromArgb(150, 80, 14) : Color.FromArgb(31, 112, 73);
        }

        private void InitializeTray()
        {
            trayMenu.Items.Add("Open DisplayWeave", null, delegate { ShowSettingsWindow(); });
            trayPause = new ToolStripMenuItem("Pause region handling");
            trayPause.Click += delegate { if (fullscreen.Paused) SaveLayout(true); else RestoreAll(); };
            trayMenu.Items.Add(trayPause);
            trayUpdate = new ToolStripMenuItem("Install update now") { Visible = false };
            trayUpdate.Click += delegate { if (updateSetup != null) InstallUpdate(); };
            trayMenu.Items.Add(trayUpdate);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit DisplayWeave", null, delegate { ExitApplication(); });
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "DisplayWeave — automatic fullscreen regions";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettingsWindow(); };
            trayIcon.Visible = true;
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
            timer.Stop();
            fullscreen.Dispose();
            trayIcon.Visible = false;
        }

        private void ExitApplication()
        {
            exitRequested = true;
            Close();
        }

        private void RestartAsAdministrator()
        {
            if (ElevationRestart.IsElevated() || !SaveRulesAndLayout()) return;
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

        private void UpdateStatus()
        {
            if (!string.IsNullOrEmpty(fullscreen.LastMessage) && fullscreen.LastMessage != lastEngineError)
            {
                lastEngineError = fullscreen.LastMessage;
                notice = fullscreen.LastMessage;
            }
            string failure = fullscreen.GetFailure(pickedIdentity);
            if (String.IsNullOrEmpty(failure)) failure = fullscreen.LastFailure;
            bool hasFailure = !String.IsNullOrEmpty(failure);
            engineState.Text = (fullscreen.Paused ? "Paused" : hasFailure ? "App needs attention" : "Handling active") + "\n" + fullscreen.Count + " ready · " + fullscreen.PendingCount + " attaching";
            engineState.ForeColor = fullscreen.Paused || hasFailure ? Color.FromArgb(150, 80, 14) : Color.FromArgb(31, 112, 73);
            pauseHandling.Text = fullscreen.Paused ? "Resume handling" : "Pause handling";
            string feedback = hasFailure ? failure : notice;
            actionFeedback.ForeColor = hasFailure ? Color.FromArgb(150, 80, 14) : Color.FromArgb(75, 89, 110);
            if (actionFeedback.Text != feedback)
            {
                actionFeedback.Text = feedback;
                hints.SetToolTip(actionFeedback, feedback + "\nClick to open Status & help.");
            }
            string details = engineState.Text.Replace("\n", " · ") + "\r\n\r\n" + feedback;
            if (hasFailure && notice != feedback) details += "\r\n\r\nLatest activity: " + notice;
            if (hotkeyNotice.Length > 0) details += "\r\n" + hotkeyNotice.Trim(' ', '·');
            if (updateNotice.Length > 0) details += "\r\n" + updateNotice;
            if (status.Text != details) status.Text = details;
            if (trayPause != null) trayPause.Text = fullscreen.Paused ? "Resume region handling" : "Pause region handling";
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
            if (m.Msg == Program.ExitForUpdateMessage) { ExitApplication(); return; }
            if (m.Msg == 0x312)
            {
                int index = m.WParam.ToInt32() - 800;
                if (index == 10) RestoreAll();
                else if (index == 0) ReleaseWindow(manager.ForegroundWindow);
                else if (index >= 1 && index <= 9) Assign(manager.ForegroundWindow, index - 1);
            }
            else if (m.Msg == 0x7E && screens != null)
            {
                fullscreen.Pause();
                lastEngineError = fullscreen.LastMessage;
                BeginInvoke((Action)delegate { LoadMonitors(); ReloadZones(0); notice = "Display configuration changed. Rules paused; review the layout, then Apply saved rules."; UpdateStatus(); });
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { trayIcon.Visible = false; trayIcon.Dispose(); trayMenu.Dispose(); hints.Dispose(); timer.Dispose(); updateTimer.Dispose(); fullscreen.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    }

}
