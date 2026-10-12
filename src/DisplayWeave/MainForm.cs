using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DisplayWeave
{
    public sealed partial class MainForm : Form
    {
        private readonly string dataDirectory;
        private readonly RegionService service;
        private readonly LayoutSettings settings;
        private readonly ApplicationRules rules;
        private readonly FullscreenController fullscreen;
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
        private MaximizeWatcher maximizeWatcher;
        private bool ticking;
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
        private string internalError = "";
        private string handledSignature;
        private string hotkeyNotice = "";
        private bool picking;
        private ApplicationIdentity pickedIdentity;

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

        // Checks the releases every six hours. A downloaded update installs by itself while this window is
        // closed and no hooked app is maximized or fullscreen in a region, so a running game is never resized.
        // Windows fitted from outside stay where they are; the new version adopts them.
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
            updateNotice = "DisplayWeave " + version + " is ready. It installs when this window is closed and no game or other hooked app is maximized or fullscreen in a region.";
            if (!Visible && !fullscreen.HasExpandedApp) { InstallUpdate(); return; }
            trayIcon.ShowBalloonTip(5000, "DisplayWeave " + version + " is ready", "It installs when no game or other hooked app is maximized or fullscreen in a region. To install now, choose it in the tray menu.", ToolTipIcon.Info);
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

        private int ScalePercent
        {
            get { DisplayMonitor monitor = DisplayMonitor.Find(CurrentScreen.DeviceName); return monitor == null ? 100 : monitor.ScalePercent; }
        }

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
            foreach (WindowCandidate item in WindowManager.ListWindows()) windows.Items.Add(item);
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
                // A first start uses this display's halves: nothing saved to review, so no pause.
                if (!File.Exists(service.LayoutPath)) rulerLayout.ApplyPreset(0, ScalePercent);
                else if (rulerLayout.WasRepaired)
                {
                    LayoutChanging();
                    notice = "The previous layout was adjusted to a non-overlapping ruler grid. Review the regions before applying saved rules.";
                    try
                    {
                        string backup = service.LayoutPath + ".before-rulers.bak";
                        if (File.Exists(service.LayoutPath) && !File.Exists(backup)) File.Copy(service.LayoutPath, backup);
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
            RunSaving(delegate { return service.Assign(handle, regionIndex, action); }, true);
        }

        private void RestoreAll()
        {
            Report(service.Pause());
        }

        public void PauseFullscreenRules()
        {
            RestoreAll();
        }

        // Shows an unexpected error, with details for a bug report in Status & help. Only an engine error outside
        // any one app's handling pauses (releases) every window; the engine already isolates per-app errors.
        public void ReportInternalError(Exception error, bool pause)
        {
            bool paused = false;
            if (pause)
            {
                try { if (!fullscreen.Paused) { service.Pause(); paused = true; } }
                catch (Exception) { } // the engine may be what failed; the report must still show
            }
            internalError = error.ToString();
            string reason = error.Message.TrimEnd('.', ' ');
            Report(pause ? "Region handling paused after an internal error: " + reason + ". Apply saved rules to resume."
                : "DisplayWeave hit an internal error: " + reason + ". Region handling continues.");
            if (paused && !Visible) trayIcon.ShowBalloonTip(5000, "DisplayWeave paused region handling", error.Message, ToolTipIcon.Error);
        }

        private void ApplySavedRules()
        {
            RunSaving(service.ApplySavedRules, false);
        }

        private void RestoreSelected()
        {
            var item = windows.SelectedItem as WindowCandidate;
            if (item == null) return;
            ReleaseWindow(item.Handle);
        }

        private void ReleaseWindow(IntPtr handle)
        {
            Report(service.Release(handle));
        }

        private void IgnoreSelected()
        {
            var item = windows.SelectedItem as WindowCandidate;
            if (item == null) { notice = "Select an application first."; UpdateStatus(); return; }
            RunSaving(delegate { return service.Ignore(item.Handle); }, true);
        }

        private void RemoveRule()
        {
            var selected = savedRules.SelectedItem as ApplicationRule;
            if (selected == null) return;
            RunSaving(delegate { return service.RemoveRule(selected); }, true);
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
                if (showResult)
                {
                    notice = service.ApplyLayout();
                    lastEngineError = fullscreen.LastMessage;
                }
                else service.SaveLayout();
                SetLayoutState("Saved" + (showResult ? " and applied" : "") + " · " + settings.Zones.Count + " regions · " + settings.GridStepPixels + " px grid", false);
            }
            catch (IOException ex)
            {
                notice = "Could not save layout: " + ex.Message;
                SetLayoutState(notice, true);
                if (showResult) MessageBox.Show(this, notice + "\n\nFile: " + service.LayoutPath, "Layout not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        private void UpdateStatus()
        {
            if (!string.IsNullOrEmpty(fullscreen.LastMessage) && fullscreen.LastMessage != lastEngineError)
            {
                lastEngineError = fullscreen.LastMessage;
                notice = fullscreen.LastMessage;
            }
            // A layout the engine cannot use stops all handling, so it outranks one app's failure.
            bool layoutProblem = !fullscreen.Paused && fullscreen.LayoutProblem.Length > 0;
            string failure = layoutProblem ? fullscreen.LayoutProblem : fullscreen.GetFailure(pickedIdentity);
            if (String.IsNullOrEmpty(failure)) failure = fullscreen.LastFailure;
            bool hasFailure = !String.IsNullOrEmpty(failure);
            engineState.Text = (fullscreen.Paused ? "Paused" : layoutProblem ? "Layout needs attention" : hasFailure ? "App needs attention" : "Handling active") +
                "\n" + (fullscreen.Count + fullscreen.OutsideFitCount) + " handled · " + fullscreen.PendingCount + " attaching";
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
            if (internalError.Length > 0) details += "\r\n\r\nLast internal error (for a bug report):\r\n" + internalError;
            if (fullscreen.LastInternalError.Length > 0) details += "\r\n\r\nLast app handling error (for a bug report):\r\n" + fullscreen.LastInternalError;
            if (status.Text != details) status.Text = details;
            if (trayPause != null) trayPause.Text = fullscreen.Paused ? "Resume region handling" : "Pause region handling";
            if (Visible && pages.SelectedIndex == 2)
            {
                // The list only displays state; a problem showing it must not reach handling.
                try { RefreshHandledApps(); }
                catch (Exception ex) { handledDetails.Text = "The app list could not be shown: " + ex.Message; }
            }
        }

        private static readonly string[] HandledStateNames = { "Starting", "Working", "Not active", "Fitted from outside", "Error", "Ready" };
        private static readonly string[] HandledStateHelp =
        {
            "DisplayWeave is starting its component in this app. This normally takes a few seconds.",
            "DisplayWeave's component runs in this app: maximize and fullscreen stay inside its region.",
            "DisplayWeave's component is loaded but not confining this app right now: handling is paused, the window is on another display, or its rule changed.",
            "Nothing is loaded into this app. DisplayWeave fitted its maximized or borderless fullscreen window into its region from outside. Exclusive fullscreen is not confined.",
            "",
            "Nothing is loaded into this app. Maximize it, or use its borderless (windowed) fullscreen mode, and DisplayWeave fits the window into this region from outside. Exclusive fullscreen still fills the monitor."
        };

        // Rebuilt only when a row changes, so the selection survives the 250 ms refresh.
        private void RefreshHandledApps()
        {
            List<HandledApp> apps = fullscreen.HandledApps();
            apps.Sort(delegate(HandledApp left, HandledApp right)
            {
                int order = String.Compare(Path.GetFileName(left.Target.ExecutablePath), Path.GetFileName(right.Target.ExecutablePath), StringComparison.CurrentCultureIgnoreCase);
                return order != 0 ? order : String.CompareOrdinal(left.Target.Key, right.Target.Key);
            });
            var rows = new List<ListViewItem>();
            var signature = new StringBuilder();
            foreach (HandledApp app in apps)
            {
                DisplayMonitor monitor = app.Region.IsEmpty ? DisplayMonitor.FromWindow(app.Target.Handle) : DisplayMonitor.FromRectangle(app.Region);
                var row = new ListViewItem(new[] { Path.GetFileName(app.Target.ExecutablePath), monitor == null ? "—" : monitor.DeviceName.Replace(@"\\.\", ""),
                    RegionName(app.Region), HandledStateNames[(int)app.State] }) { Name = app.Target.Key, Tag = HandledDetails(app), UseItemStyleForSubItems = false };
                row.SubItems[3].ForeColor = app.State == HandledAppState.Error ? Color.FromArgb(178, 54, 32) : app.State == HandledAppState.Ready ? Color.FromArgb(31, 112, 73)
                    : app.Error.Length > 0 ? Color.FromArgb(150, 80, 14)
                    : app.State == HandledAppState.Working || app.State == HandledAppState.FittedFromOutside || app.State == HandledAppState.Ready
                        ? Color.FromArgb(31, 112, 73) : Color.FromArgb(75, 89, 110);
                foreach (ListViewItem.ListViewSubItem cell in row.SubItems) signature.Append(cell.Text).Append('\t');
                signature.Append(row.Name).Append('\t').Append(row.Tag).Append('\n');
                rows.Add(row);
            }
            if (signature.ToString() != handledSignature)
            {
                handledSignature = signature.ToString();
                string selected = handledApps.SelectedItems.Count > 0 ? handledApps.SelectedItems[0].Name : null;
                handledApps.BeginUpdate();
                handledApps.Items.Clear();
                handledApps.Items.AddRange(rows.ToArray());
                if (selected != null && handledApps.Items.ContainsKey(selected)) handledApps.Items[selected].Selected = true;
                handledApps.EndUpdate();
            }
            ShowHandledDetails();
        }

        private string RegionName(Rectangle region)
        {
            if (region.IsEmpty) return "—";
            for (int i = 0; i < settings.Zones.Count; i++)
                if (settings.Zones[i].ToPixels(Area) == region) return settings.Zones[i].Name + " · " + region.Width + " × " + region.Height;
            return region.Width + " × " + region.Height + " at " + region.X + ", " + region.Y;
        }

        // The error first: a long path must not push it out of view.
        private static string HandledDetails(HandledApp app)
        {
            string text = app.State == HandledAppState.Error ? "Error: " + app.Error : HandledStateNames[(int)app.State] + ": " + HandledStateHelp[(int)app.State];
            if (app.State != HandledAppState.Error && app.Error.Length > 0)
                text += "\r\n\r\n" + (app.State == HandledAppState.FittedFromOutside || app.State == HandledAppState.Ready ? "Why its component is not used: " : "Problem: ") + app.Error;
            return text + "\r\n\r\n" + app.Target.ExecutablePath + " (PID " + app.Target.ProcessId + ")";
        }

        // Built when shown and on each toggle, so it matches what Status & help shows now.
        private void ShowDiagnosticReport()
        {
            DiagnosticReport.ShowDialog(this, Font, delegate(bool includeAppNames)
            {
                return DiagnosticReport.Build(engineState.Text.Replace("\n", " · "), settings, fullscreen.HandledApps(),
                    new[] { fullscreen.LayoutProblem, fullscreen.LastFailure, fullscreen.LastInternalError, internalError, updateNotice }, includeAppNames);
            });
        }

        private void ShowHandledDetails()
        {
            string text = handledApps.SelectedItems.Count > 0 ? (string)handledApps.SelectedItems[0].Tag
                : handledApps.Items.Count == 0 ? (!fullscreen.Paused && fullscreen.LayoutProblem.Length > 0 ? fullscreen.LayoutProblem
                    : "No app is handled right now. Move an app into a region, then maximize it or enter fullscreen, or give it a rule on App rules.")
                : "Select an app to see exactly what is happening with it.";
            if (handledDetails.Text != text) handledDetails.Text = text;
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
            if (disposing)
            {
                if (maximizeWatcher != null) maximizeWatcher.Dispose();
                trayIcon.Visible = false; trayIcon.Dispose(); trayMenu.Dispose(); hints.Dispose(); timer.Dispose(); updateTimer.Dispose(); service.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    }

}
