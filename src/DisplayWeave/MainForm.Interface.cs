using System;
using System.Drawing;
using System.Windows.Forms;

namespace DisplayWeave
{
    public sealed partial class MainForm
    {
        private readonly TabControl pages = new TabControl();
        private readonly ComboBox ruleRegion = new ComboBox();
        private readonly Label engineState = new Label();
        private readonly Label actionFeedback = new Label();
        private readonly Label savedRuleDetails = new Label();
        private readonly Button pauseHandling = new Button();
        private readonly ToolTip hints = new ToolTip();
        private Button forceApp, maximizeApp, ignoreApp, restoreApp, removeRule;
        private bool syncingRegion;

        private void BuildInterface()
        {
            var root = Table(1, 3);
            root.Padding = new Padding(24, 16, 24, 16);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            Controls.Add(root);

            var header = Table(3, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            header.Controls.Add(new Label { Text = "DisplayWeave", Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 23F, FontStyle.Bold), ForeColor = Color.FromArgb(25, 38, 65), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            engineState.Dock = DockStyle.Fill;
            engineState.TextAlign = ContentAlignment.MiddleRight;
            engineState.Padding = new Padding(0, 0, 18, 0);
            header.Controls.Add(engineState, 1, 0);
            StyleButton(pauseHandling);
            pauseHandling.Text = "Pause handling";
            pauseHandling.Margin = new Padding(0, 18, 0, 18);
            pauseHandling.Click += delegate { if (fullscreen.Paused) SaveLayout(true); else RestoreAll(); };
            header.Controls.Add(pauseHandling, 2, 0);
            root.Controls.Add(header, 0, 0);

            pages.Dock = DockStyle.Fill;
            pages.Padding = new Point(24, 10);
            pages.Margin = new Padding(0, 4, 0, 12);
            pages.AccessibleName = "Settings sections";
            var layoutPage = Page("Layout");
            var rulesPage = Page("App rules");
            var statusPage = Page("Status & help");
            pages.TabPages.AddRange(new[] { layoutPage, rulesPage, statusPage });
            pages.SelectedIndexChanged += delegate
            {
                if (pages.SelectedIndex != 1 && picking)
                {
                    picking = false; picker.Capture = false; Cursor = Cursors.Default;
                }
            };
            BuildLayoutPage(layoutPage);
            BuildRulesPage(rulesPage);
            BuildStatusPage(statusPage);
            root.Controls.Add(pages, 0, 1);

            var footer = Table(2, 2);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layoutState.Dock = DockStyle.Fill;
            layoutState.TextAlign = ContentAlignment.MiddleLeft;
            layoutState.AutoEllipsis = true;
            actionFeedback.Dock = DockStyle.Fill;
            actionFeedback.AutoEllipsis = true;
            actionFeedback.ForeColor = Color.FromArgb(75, 89, 110);
            actionFeedback.Cursor = Cursors.Hand;
            actionFeedback.AccessibleName = "Latest activity. Click for status details.";
            actionFeedback.Click += delegate { pages.SelectedIndex = 2; };
            var save = Button("Save layout", delegate { SaveLayout(true); });
            save.BackColor = Color.FromArgb(34, 99, 176);
            save.ForeColor = Color.White;
            save.FlatAppearance.BorderSize = 0;
            save.Margin = new Padding(12, 5, 0, 9);
            footer.Controls.Add(layoutState, 0, 0);
            footer.Controls.Add(actionFeedback, 0, 1);
            footer.Controls.Add(save, 1, 0); footer.SetRowSpan(save, 2);
            root.Controls.Add(footer, 0, 2);

            monitors.SelectedIndexChanged += delegate { if (!updating) { LayoutChanging(); settings.MonitorDevice = CurrentScreen.DeviceName; ReloadZones(zoneList.SelectedIndex); } };
            workArea.CheckedChanged += delegate { if (!updating) { LayoutChanging(); settings.UseWorkArea = workArea.Checked; ReloadZones(zoneList.SelectedIndex); } };
            windows.SelectedIndexChanged += delegate { UpdatePickedApp(); UpdateRuleActions(); UpdateStatus(); };
            savedRules.SelectedIndexChanged += delegate { ShowRuleDetails(); UpdateRuleActions(); };
            zoneList.SelectedIndexChanged += delegate
            {
                if (updating) return;
                canvas.SelectedIndex = zoneList.SelectedIndex;
                UpdateRegionDimensions();
                SyncRuleRegion();
                canvas.Invalidate();
            };
            ruleRegion.SelectedIndexChanged += delegate
            {
                if (!updating && !syncingRegion && ruleRegion.SelectedIndex >= 0)
                    zoneList.SelectedIndex = ruleRegion.SelectedIndex;
            };
            gridSpacing.ValueChanged += delegate { if (!updating) ChangeGridSpacing(); };
            automaticRegions.CheckedChanged += delegate
            {
                if (updating) return;
                settings.AutomaticRegions = automaticRegions.Checked;
                SaveLayout(true);
            };
            UpdateRuleActions();
        }

        private void BuildLayoutPage(TabPage page)
        {
            page.AutoScroll = true;
            var layout = Table(1, 4);
            layout.Dock = DockStyle.Top;
            layout.MinimumSize = new Size(0, 530);
            layout.Height = 530;
            // Keep the editor spacious on large windows; on a short display the
            // page scrolls while the global Save action remains in view.
            page.SizeChanged += delegate { layout.Height = Math.Max(layout.MinimumSize.Height, page.ClientSize.Height - page.Padding.Vertical); };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            page.Controls.Add(layout);
            layout.Controls.Add(Introduction("Arrange your screen", "Drag ruler pins to set your regions. Every region stays separate, with no gaps or overlaps."), 0, 0);

            var display = Table(3, 2);
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            display.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            display.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            display.Controls.Add(Caption("Display"), 0, 0);
            SetupCombo(monitors); monitors.AccessibleName = "Display";
            display.Controls.Add(monitors, 0, 1);
            display.Controls.Add(Button("Refresh displays", delegate { LayoutChanging(); LoadMonitors(); ReloadZones(zoneList.SelectedIndex); }), 1, 1);
            workArea.Text = "Keep the taskbar visible";
            workArea.Dock = DockStyle.Fill;
            workArea.Margin = new Padding(16, 0, 0, 0);
            display.Controls.Add(workArea, 2, 1);
            layout.Controls.Add(display, 0, 1);

            var editor = Table(2, 1);
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 274));
            editor.Padding = new Padding(0, 12, 0, 10);
            canvas.Dock = DockStyle.Fill;
            canvas.Margin = new Padding(0, 0, 22, 0);
            canvas.SelectionChanged += delegate { if (!updating) zoneList.SelectedIndex = canvas.SelectedIndex; };
            canvas.LayoutEditing += LayoutChanging;
            canvas.LayoutEdited += delegate { ReloadZones(canvas.SelectedIndex); };
            canvas.Notice += delegate(string message) { notice = message; UpdateStatus(); };
            editor.Controls.Add(canvas, 0, 0);

            var side = Table(2, 7);
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            AddWide(side, Caption("Regions"), 0);
            zoneList.Dock = DockStyle.Fill;
            zoneList.IntegralHeight = false;
            zoneList.BorderStyle = BorderStyle.FixedSingle;
            zoneList.AccessibleName = "Layout regions";
            zoneList.Margin = new Padding(0, 0, 0, 8);
            AddWide(side, zoneList, 1);
            regionDimensions.Dock = DockStyle.Fill;
            regionDimensions.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(side, regionDimensions, 2);
            side.Controls.Add(Caption("Grid every (px)"), 0, 3);
            gridSpacing.Minimum = 1; gridSpacing.Maximum = 100000; gridSpacing.Value = 100;
            gridSpacing.Increment = 10; gridSpacing.ThousandsSeparator = true;
            gridSpacing.Dock = DockStyle.Fill;
            gridSpacing.AccessibleName = "Ruler grid spacing in pixels";
            side.Controls.Add(gridSpacing, 1, 3);
            side.Controls.Add(Button("Add width pin", delegate { canvas.AddDivider(true); }), 0, 4);
            side.Controls.Add(Button("Add height pin", delegate { canvas.AddDivider(false); }), 1, 4);
            var removePin = Button("Remove selected pin", delegate { canvas.RemoveSelectedPin(); });
            removePin.Enabled = false;
            canvas.PinSelectionChanged += delegate { removePin.Enabled = canvas.HasSelectedPin; };
            canvas.LayoutEdited += delegate { removePin.Enabled = canvas.HasSelectedPin; };
            AddWide(side, removePin, 5);
            AddWide(side, Caption("Up to 9 regions per display"), 6);
            editor.Controls.Add(side, 1, 0);
            layout.Controls.Add(editor, 0, 2);

            var presetRow = Table(4, 1);
            presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            presetRow.Padding = new Padding(0, 8, 0, 4);
            presetRow.Controls.Add(Caption("Preset"), 0, 0);
            SetupCombo(presets); presets.AccessibleName = "Layout preset";
            presets.Items.AddRange(new object[] { "Choose a preset…", "Two halves", "Three columns", "Wide center", "Four quadrants" });
            presets.SelectedIndex = 0;
            presetRow.Controls.Add(presets, 1, 0);
            presetRow.Controls.Add(Button("Use preset", delegate { if (presets.SelectedIndex <= 0) { notice = "Choose a preset first, or keep your current custom layout."; UpdateStatus(); return; } LayoutChanging(); rulerLayout.ApplyPreset(presets.SelectedIndex - 1); ReloadZones(0); }), 2, 0);
            var tip = Caption("Double-click a ruler to add a pin.");
            tip.TextAlign = ContentAlignment.MiddleRight;
            presetRow.Controls.Add(tip, 3, 0);
            layout.Controls.Add(presetRow, 0, 3);
        }

        private void BuildRulesPage(TabPage page)
        {
            page.AutoScroll = true;
            var body = Table(1, 4);
            body.Dock = DockStyle.Top; body.Height = 520;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 236));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
            page.Controls.Add(body);
            body.Controls.Add(Introduction("Let apps find their region", "Automatic handling is the default. Add an override only when an app needs different behavior."), 0, 0);
            var auto = Table(1, 2);
            auto.BackColor = Color.FromArgb(237, 245, 253);
            auto.Padding = new Padding(12, 5, 12, 5);
            auto.Margin = new Padding(0, 0, 0, 12);
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            auto.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            automaticRegions.Text = "Automatically use each app's current region";
            automaticRegions.Dock = DockStyle.Fill;
            auto.Controls.Add(automaticRegions, 0, 0);
            auto.Controls.Add(Caption("Apps see the virtual regions. Move a window into a region, then maximize it or enter fullscreen."), 0, 1);
            body.Controls.Add(auto, 0, 1);

            var assign = Table(3, 6);
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            assign.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            AddWide(assign, Caption("1. Choose an application"), 0);
            SetupCombo(windows); windows.AccessibleName = "Application window";
            assign.Controls.Add(windows, 0, 1);
            StyleButton(picker); picker.Text = "⊕ Drag onto app";
            picker.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { picking = true; picker.Capture = true; Cursor = Cursors.Cross; notice = "Keep holding and drag onto the application window, then release."; UpdateStatus(); } };
            picker.MouseUp += delegate(object sender, MouseEventArgs e) { if (!picking) return; picking = false; picker.Capture = false; Cursor = Cursors.Default; SelectWindowAtCursor(); };
            picker.MouseCaptureChanged += delegate
            {
                if (!picker.Capture && picking && IsHandleCreated)
                    BeginInvoke((Action)delegate { if (!picker.Capture && picking) { picking = false; Cursor = Cursors.Default; } });
            };
            assign.Controls.Add(picker, 1, 1);
            assign.Controls.Add(Button("Refresh windows", RefreshWindows), 2, 1);
            pickedApp.Dock = DockStyle.Fill; pickedApp.AutoEllipsis = true;
            pickedApp.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(assign, pickedApp, 2);
            AddWide(assign, Caption("2. Choose a target region or exclude the app"), 3);
            SetupCombo(ruleRegion); ruleRegion.AccessibleName = "App rule target region";
            assign.Controls.Add(ruleRegion, 0, 4);
            forceApp = Button("Force fullscreen", delegate { var item = windows.SelectedItem as WindowCandidate; if (item != null) Assign(item.Handle, ruleRegion.SelectedIndex); });
            assign.Controls.Add(forceApp, 1, 4);
            ignoreApp = Button("Ignore this app", IgnoreSelected);
            assign.Controls.Add(ignoreApp, 2, 4);
            assign.Controls.Add(Caption("Saved rules take priority over automatic handling."), 0, 5);
            maximizeApp = Button("Maximize in region", delegate { var item = windows.SelectedItem as WindowCandidate; if (item != null) Assign(item.Handle, ruleRegion.SelectedIndex, ApplicationAction.MaximizeInRegion); });
            hints.SetToolTip(maximizeApp, "Automatic handling already fits maximized windows of apps that refuse DisplayWeave's component.\nThis rule pins an app to one region and never tries to load the component into it:\nrun the app windowed and maximize it from its title bar.");
            assign.Controls.Add(maximizeApp, 1, 5);
            restoreApp = Button("Restore selected", RestoreSelected);
            assign.Controls.Add(restoreApp, 2, 5);
            body.Controls.Add(assign, 0, 2);

            var saved = Table(3, 3);
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            saved.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            saved.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            saved.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            saved.Padding = new Padding(0, 8, 0, 0);
            AddWide(saved, Caption("Saved overrides"), 0);
            SetupCombo(savedRules); savedRules.AccessibleName = "Saved application overrides";
            saved.Controls.Add(savedRules, 0, 1);
            saved.Controls.Add(Button("Apply saved rules", ApplySavedRules), 1, 1);
            removeRule = Button("Remove saved rule", RemoveRule);
            saved.Controls.Add(removeRule, 2, 1);
            savedRuleDetails.Dock = DockStyle.Fill; savedRuleDetails.AutoEllipsis = true;
            savedRuleDetails.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(saved, savedRuleDetails, 2);
            body.Controls.Add(saved, 0, 3);
        }

        private void BuildStatusPage(TabPage page)
        {
            page.AutoScroll = true;
            var body = Table(1, 7);
            body.Dock = DockStyle.Top; body.Height = 700;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            page.Controls.Add(body);
            body.Controls.Add(Introduction("Status & help", "Check handling activity, keyboard shortcuts, and how to keep DisplayWeave running."), 0, 0);
            var activity = new GroupBox { Text = "Current activity", Dock = DockStyle.Fill, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 16) };
            status.Dock = DockStyle.Fill; status.Multiline = true; status.ReadOnly = true;
            status.BorderStyle = BorderStyle.None; status.ScrollBars = ScrollBars.Vertical;
            status.BackColor = Color.White; status.ForeColor = Color.FromArgb(35, 59, 89);
            status.AccessibleName = "Region handling status";
            activity.Controls.Add(status); body.Controls.Add(activity, 0, 1);
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
            body.Controls.Add(access, 0, 2);
            body.Controls.Add(Introduction("Keyboard shortcuts", "Ctrl + Alt + Shift + 1…9     Force the active app into a region\nCtrl + Alt + Shift + 0          Release the active app\nCtrl + Alt + Shift + Backspace     Pause all handling"), 0, 3);
            body.Controls.Add(Introduction("Keep it in the tray", "X hides this window and keeps region handling running.\nDouble-click the tray icon or launch DisplayWeave again to reopen settings.\nExit DisplayWeave stops handling, restores controlled windows, and quits."), 0, 4);
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
            body.Controls.Add(startup, 0, 5);
            var bottom = Table(2, 1);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            bottom.Controls.Add(Caption("Editing pauses handling. Save layout applies changes and resumes it."), 0, 0);
            bottom.Controls.Add(Button("Exit DisplayWeave", ExitApplication), 1, 0);
            body.Controls.Add(bottom, 0, 6);
        }

        private void SyncRuleRegion()
        {
            syncingRegion = true;
            ruleRegion.SelectedIndex = zoneList.SelectedIndex;
            syncingRegion = false;
            UpdateRuleActions();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle available = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            Location = new Point(Math.Max(available.Left, Math.Min(Left, available.Right - Width)),
                Math.Max(available.Top, Math.Min(Top, available.Bottom - Height)));
        }

        private void UpdateRuleActions()
        {
            bool selected = windows.SelectedItem is WindowCandidate;
            if (forceApp != null) forceApp.Enabled = selected && ruleRegion.SelectedIndex >= 0;
            if (maximizeApp != null) maximizeApp.Enabled = forceApp.Enabled;
            if (ignoreApp != null) ignoreApp.Enabled = selected;
            if (restoreApp != null) restoreApp.Enabled = selected;
            if (removeRule != null) removeRule.Enabled = savedRules.SelectedItem is ApplicationRule;
        }

        private static TableLayoutPanel Table(int columns, int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = new Padding(0) };
        }

        private static TabPage Page(string title)
        {
            return new TabPage(title) { BackColor = Color.White, Padding = new Padding(20, 14, 20, 14) };
        }

        private static Control Introduction(string title, string description)
        {
            var panel = Table(1, 2);
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(25, 38, 65) }, 0, 0);
            panel.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(75, 89, 110) }, 0, 1);
            return panel;
        }

        private static Label Caption(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(75, 89, 110) };
        }

        private static void AddWide(TableLayoutPanel table, Control control, int row)
        {
            table.Controls.Add(control, 0, row); table.SetColumnSpan(control, table.ColumnCount);
        }

        private static void StyleButton(Button button)
        {
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(247, 249, 252);
            button.ForeColor = Color.FromArgb(35, 59, 89);
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 215, 228);
            button.Margin = new Padding(3, 2, 3, 6);
            button.Cursor = Cursors.Hand;
        }

        private static Button Button(string text, Action action)
        {
            var result = new Button { Text = text };
            StyleButton(result);
            result.Click += delegate { action(); };
            return result;
        }

        private static void SetupCombo(ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Dock = DockStyle.Fill;
            combo.DropDownWidth = 600;
            combo.Margin = new Padding(0, 4, 8, 4);
        }
    }
}
