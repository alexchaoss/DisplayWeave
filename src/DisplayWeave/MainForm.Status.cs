using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The Status & help tab and the status shown in the header and footer: handled apps, activity and errors.
    public sealed partial class MainForm
    {
        private readonly TextBox status = new TextBox();
        private readonly ListView handledApps = new ListView();
        private readonly TextBox handledDetails = new TextBox();
        private string internalError = "";
        private string handledSignature;
        private string loggedNotice, loggedUpdateNotice = "";

        private void BuildStatusPage(TabPage page)
        {
            page.AutoScroll = true;
            var body = Table(1, 8);
            body.Dock = DockStyle.Top; body.Height = 1030;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 330));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            page.Controls.Add(body);
            body.Controls.Add(Introduction("Status & help", "Check handling activity, keyboard shortcuts, and how to keep DisplayWeave running."), 0, 0);
            var appsBox = new GroupBox { Text = "Apps DisplayWeave is handling · select one to see exactly what happened", Dock = DockStyle.Fill,
                Padding = new Padding(14), Margin = new Padding(0, 0, 0, 16) };
            var appsBody = Table(1, 2);
            appsBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            appsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            handledApps.Dock = DockStyle.Fill; handledApps.View = View.Details; handledApps.FullRowSelect = true;
            handledApps.MultiSelect = false; handledApps.HideSelection = false; handledApps.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            handledApps.AccessibleName = "Apps DisplayWeave is handling";
            handledApps.Columns.Add("App"); handledApps.Columns.Add("Display"); handledApps.Columns.Add("Region"); handledApps.Columns.Add("Status");
            handledApps.SizeChanged += delegate
            {
                int width = handledApps.ClientSize.Width;
                handledApps.Columns[0].Width = width * 30 / 100; handledApps.Columns[1].Width = width * 14 / 100;
                handledApps.Columns[2].Width = width * 30 / 100; handledApps.Columns[3].Width = width - width * 74 / 100 - 4;
            };
            handledApps.SelectedIndexChanged += delegate { ShowHandledDetails(); };
            handledDetails.Dock = DockStyle.Fill; handledDetails.Multiline = true; handledDetails.ReadOnly = true;
            handledDetails.BorderStyle = BorderStyle.None; handledDetails.ScrollBars = ScrollBars.Vertical;
            handledDetails.BackColor = Color.White; handledDetails.ForeColor = Color.FromArgb(35, 59, 89);
            handledDetails.Margin = new Padding(0, 8, 0, 0);
            handledDetails.AccessibleName = "Selected app details";
            appsBody.Controls.Add(handledApps, 0, 0);
            appsBody.Controls.Add(handledDetails, 0, 1);
            appsBox.Controls.Add(appsBody); body.Controls.Add(appsBox, 0, 1);
            var activity = new GroupBox { Text = "Current activity", Dock = DockStyle.Fill, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 16) };
            status.Dock = DockStyle.Fill; status.Multiline = true; status.ReadOnly = true;
            status.BorderStyle = BorderStyle.None; status.ScrollBars = ScrollBars.Vertical;
            status.BackColor = Color.White; status.ForeColor = Color.FromArgb(35, 59, 89);
            status.AccessibleName = "Region handling status";
            activity.Controls.Add(status); body.Controls.Add(activity, 0, 2);
            bool elevated = ElevationRestart.IsElevated();
            var access = Table(2, 1);
            access.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            access.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            access.Controls.Add(Introduction("Apps running as administrator", elevated
                ? "Administrator access is active for this session.\nExit from the tray before installing an update."
                : "Use administrator access to handle apps already running as administrator.\nWindows will ask you to approve this restart."), 0, 0);
            var restart = Button(elevated ? "Running as administrator" : "Restart as administrator", RestartAsAdministrator);
            restart.Enabled = !elevated;
            restart.Margin = new Padding(12, 24, 0, 26);
            access.Controls.Add(restart, 1, 0);
            body.Controls.Add(access, 0, 3);
            body.Controls.Add(Introduction("Shortcuts and Shift", "Make your own keyboard shortcuts on the Shortcuts tab; DisplayWeave has none of its own.\n" +
                "Hold Shift while you maximize a window to leave it filling its display this once.\nWhat DisplayWeave did is in logs\\activity.log next to its settings."), 0, 4);
            body.Controls.Add(Introduction("Keep it in the tray", "X hides this window and keeps region handling running.\nDouble-click the tray icon or launch DisplayWeave again to reopen settings.\nExit DisplayWeave stops handling, restores controlled windows, and quits."), 0, 5);
            var startup = Table(1, 2);
            startup.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            startup.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var startWithWindows = new CheckBox { Text = "Start DisplayWeave with Windows", Dock = DockStyle.Fill,
                AccessibleName = "Start DisplayWeave with Windows" };
            var startupHelp = Caption("Open quietly in the system tray when you sign in. Changes take effect immediately.");
            try { startWithWindows.Checked = StartupRegistration.IsEnabled(); }
            catch (Exception ex) { startWithWindows.Enabled = false; startupHelp.Text = "Could not read the startup setting: " + ex.Message; }
            bool changingStartup = false;
            startWithWindows.CheckedChanged += delegate
            {
                if (changingStartup) return;
                try
                {
                    StartupRegistration.SetEnabled(startWithWindows.Checked, Application.ExecutablePath, dataDirectory);
                    startupHelp.Text = startWithWindows.Checked ? "Enabled. DisplayWeave will open in the system tray when you sign in."
                        : "Disabled. You can still open DisplayWeave from its shortcut.";
                }
                catch (Exception ex)
                {
                    changingStartup = true;
                    startWithWindows.Checked = !startWithWindows.Checked;
                    changingStartup = false;
                    startupHelp.Text = "Startup setting was not changed: " + ex.Message;
                }
            };
            startup.Controls.Add(startWithWindows, 0, 0);
            startup.Controls.Add(startupHelp, 0, 1);
            body.Controls.Add(startup, 0, 6);
            var bottom = Table(3, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            bottom.Controls.Add(Caption("Editing pauses handling. Save layout applies changes and resumes it."), 0, 0);
            var report = Button("Diagnostic report…", ShowDiagnosticReport);
            hints.SetToolTip(report, "Preview and copy a privacy-safe report to paste into a GitHub issue.");
            bottom.Controls.Add(report, 1, 0);
            bottom.Controls.Add(Button("Exit DisplayWeave", ExitApplication), 2, 0);
            body.Controls.Add(bottom, 0, 7);
        }

        private void UpdateStatus()
        {
            if (!string.IsNullOrEmpty(fullscreen.LastMessage) && fullscreen.LastMessage != lastEngineError)
            {
                lastEngineError = fullscreen.LastMessage;
                notice = fullscreen.LastMessage;
            }
            // The engine logs its own messages.
            if (notice != loggedNotice) { loggedNotice = notice; if (notice != fullscreen.LastMessage) ActivityLog.Write(notice); }
            if (updateNotice != loggedUpdateNotice) { loggedUpdateNotice = updateNotice; ActivityLog.Write(updateNotice); }
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
            if (saveLayout != null) saveLayout.Text = displayWait.IsEmpty ? "Save layout" : "Use this display size";
            if (Visible && pages.SelectedTab == statusPage)
            {
                // The list only displays state; a problem showing it must not reach handling.
                try { RefreshHandledApps(); }
                catch (Exception ex) { handledDetails.Text = "The app list could not be shown: " + ex.Message; }
            }
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
            if (paused && !Visible) ShowBalloon("DisplayWeave paused region handling", error.Message, ToolTipIcon.Error);
        }

        private static readonly string[] HandledStateNames = { "Starting", "Working", "Not active", "Fitted from outside", "Error", "Ready" };
        private static readonly string[] HandledStateHelp =
        {
            "DisplayWeave is starting its component in this app. This normally takes a few seconds.",
            "DisplayWeave's component runs in this app: maximize and fullscreen stay inside its region.",
            "DisplayWeave's component is loaded but not confining this app right now: handling is paused, the window is on another display, or its rule changed.",
            "Nothing is loaded into this app. DisplayWeave fitted its window into its region from outside: maximized, in borderless fullscreen, or without its title bar under a Borderless in region rule. Exclusive fullscreen is not confined.",
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

        private void ShowHandledDetails()
        {
            string text = handledApps.SelectedItems.Count > 0 ? (string)handledApps.SelectedItems[0].Tag
                : handledApps.Items.Count == 0 ? (!fullscreen.Paused && fullscreen.LayoutProblem.Length > 0 ? fullscreen.LayoutProblem
                    : "No app is handled right now. Move an app into a region, then maximize it or enter fullscreen, or give it a rule on App rules.")
                : "Select an app to see exactly what is happening with it.";
            if (handledDetails.Text != text) handledDetails.Text = text;
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
    }
}
