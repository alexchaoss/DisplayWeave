using System;
using System.Drawing;
using System.Text;
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
        private readonly TextBox ruleExplanation = new TextBox();
        private readonly Label ruleHint = new Label();
        private readonly Button pauseHandling = new Button();
        private readonly ListView handledApps = new ListView();
        private readonly TextBox handledDetails = new TextBox();
        private readonly ToolTip hints = new ToolTip();
        private Button saveRule, restoreApp, removeRule;
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
            engineState.Cursor = Cursors.Hand;
            engineState.Click += delegate { pages.SelectedIndex = 2; };
            hints.SetToolTip(engineState, "Click to see each app's status in Status & help.");
            header.Controls.Add(engineState, 1, 0);
            StyleButton(pauseHandling);
            pauseHandling.Text = "Pause handling";
            pauseHandling.Margin = new Padding(0, 18, 0, 18);
            pauseHandling.Click += delegate { if (fullscreen.Paused) ResumeHandling(); else RestoreAll(); };
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
            // Save layout while waiting for the display keeps the display as it is now.
            var save = Button("Save layout", delegate { EndDisplayWait(); SaveLayout(true); });
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
                // While waiting for the display, the choice is saved and applies when handling resumes.
                SaveLayout(displayWait.IsEmpty);
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
            presetRow.Controls.Add(Button("Use preset", delegate { if (presets.SelectedIndex <= 0) { notice = "Choose a preset first, or keep your current custom layout."; UpdateStatus(); return; } LayoutChanging(); rulerLayout.ApplyPreset(presets.SelectedIndex - 1, ScalePercent); ReloadZones(0); }), 2, 0);
            var tip = Caption("Double-click a ruler to add a pin.");
            tip.TextAlign = ContentAlignment.MiddleRight;
            presetRow.Controls.Add(tip, 3, 0);
            layout.Controls.Add(presetRow, 0, 3);
        }

        // The ways a rule can handle an app, in the order App rules shows them. Centered fullscreen's details
        // depend on the chosen region, so they are built when it is selected.
        private sealed class RuleOption
        {
            internal ApplicationAction Action;
            internal string Title, Summary, Details;
            internal RadioButton Choice;
        }

        private readonly RuleOption[] ruleOptions =
        {
            new RuleOption { Action = ApplicationAction.MaximizeInRegion, Title = "Maximize in region", Summary = "Nothing loaded · keeps its title bar",
                Details = "Keeps this app in the region, wherever its window is. Maximize it from its title bar, or use its borderless fullscreen mode, " +
                    "and DisplayWeave fits the window into the region from outside. A maximized window keeps its title bar. Nothing is loaded into the app.\r\n\r\n" +
                    "Works with almost every app. A game that resizes itself back to the whole monitor needs Borderless in region instead." },
            new RuleOption { Action = ApplicationAction.BorderlessInRegion, Title = "Borderless in region", Summary = "Nothing loaded · game set to Windowed",
                Details = "For games. In the game's settings, choose Windowed (not Fullscreen or Windowed fullscreen). DisplayWeave then removes the title bar " +
                    "and border from outside and sizes the window to the region, so it looks like fullscreen there. Nothing is loaded into the game.\r\n\r\n" +
                    "If the game keeps putting its title bar back, DisplayWeave stops after a few tries and says so. Pausing handling, removing the rule or " +
                    "exiting DisplayWeave gives the window its title bar and place back. Automatic updates wait while it is shown this way." },
            new RuleOption { Action = ApplicationAction.CenteredFullscreen, Title = "Centered fullscreen", Summary = "Nothing loaded · middle region only" },
            new RuleOption { Action = ApplicationAction.ForceFullscreen, Title = "Force fullscreen", Summary = "⚠ Loads a DLL into the app",
                Details = "Loads DisplayWeave's component (RegionHook.dll) into the app. The app then sees this region as its whole monitor, so its own " +
                    "fullscreen and windowed-fullscreen modes stay in the region.\r\n\r\n" +
                    "Use it only when the other options do not work and the app's terms allow it: anti-cheat software can treat any loaded DLL as tampering, " +
                    "and some apps refuse it. The component stays in the app until the app closes. The Ctrl+Alt+Shift+1…9 shortcuts save this rule too." },
            new RuleOption { Action = ApplicationAction.Ignore, Title = "Ignore this app", Summary = "Never moved or resized",
                Details = "DisplayWeave never moves or resizes this app, even when automatic handling is on. No region is needed." },
        };

        private void BuildRulesPage(TabPage page)
        {
            page.AutoScroll = true;
            hints.AutoPopDelay = 30000;
            var body = Table(1, 4);
            body.Dock = DockStyle.Top; body.Height = 860;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 504));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            page.Controls.Add(body);
            body.Controls.Add(Introduction("App rules", "DisplayWeave fits apps by itself. Add a rule only for an app that needs something different."), 0, 0);
            var auto = Table(1, 2);
            auto.BackColor = Color.FromArgb(237, 245, 253);
            auto.Padding = new Padding(12, 5, 12, 5);
            auto.Margin = new Padding(0, 0, 0, 12);
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            auto.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            automaticRegions.Text = "Automatically use each app's current region";
            automaticRegions.Dock = DockStyle.Fill;
            auto.Controls.Add(automaticRegions, 0, 0);
            auto.Controls.Add(Caption("Move a window into a region, then maximize it or use borderless fullscreen. Nothing is loaded into apps."), 0, 1);
            body.Controls.Add(auto, 0, 1);

            var assign = Table(3, 9);
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            assign.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            foreach (int height in new[] { 34, 24, 42, 24, 24, 42, 26, 236, 50 }) assign.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            AddWide(assign, Heading("Add a rule for one app"), 0);
            AddWide(assign, Caption("1. Choose the app"), 1);
            SetupCombo(windows); windows.AccessibleName = "Application window";
            // An app started after DisplayWeave appears as soon as the list opens.
            windows.DropDown += delegate { RefreshWindows(); };
            assign.Controls.Add(windows, 0, 2);
            StyleButton(picker); picker.Text = "⊕ Drag onto app";
            hints.SetToolTip(picker, "Hold the mouse button down here, drag onto the app's window, then release.");
            picker.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { picking = true; picker.Capture = true; Cursor = Cursors.Cross; notice = "Keep holding and drag onto the application window, then release."; UpdateStatus(); } };
            picker.MouseUp += delegate(object sender, MouseEventArgs e) { if (!picking) return; picking = false; picker.Capture = false; Cursor = Cursors.Default; SelectWindowAtCursor(); };
            picker.MouseCaptureChanged += delegate
            {
                if (!picker.Capture && picking && IsHandleCreated)
                    BeginInvoke((Action)delegate { if (!picker.Capture && picking) { picking = false; Cursor = Cursors.Default; } });
            };
            assign.Controls.Add(picker, 1, 2);
            assign.Controls.Add(Button("Refresh windows", RefreshWindows), 2, 2);
            pickedApp.Dock = DockStyle.Fill; pickedApp.AutoEllipsis = true;
            pickedApp.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(assign, pickedApp, 3);
            AddWide(assign, Caption("2. Choose its region"), 4);
            SetupCombo(ruleRegion); ruleRegion.AccessibleName = "App rule target region";
            assign.Controls.Add(ruleRegion, 0, 5);
            AddWide(assign, Caption("3. Choose how DisplayWeave handles it (hover ⓘ for details)"), 6);

            var how = Table(2, 1);
            how.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));
            how.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var choices = Table(3, ruleOptions.Length);
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            for (int i = 0; i < ruleOptions.Length; i++)
            {
                RuleOption option = ruleOptions[i];
                choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                string tip = Wrap(option.Details ?? "For games in exclusive fullscreen: your graphics driver shows the game at the region's size in the middle " +
                    "of the display. Nothing is loaded into the game. Select it to see the steps for the chosen region.", 90);
                option.Choice = new RadioButton { Text = option.Title, Dock = DockStyle.Fill, Checked = i == 0, AccessibleDescription = option.Summary };
                option.Choice.CheckedChanged += delegate { if (option.Choice.Checked) UpdateRuleActions(); };
                var summary = new Label { Text = option.Summary, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = option.Action == ApplicationAction.ForceFullscreen ? Color.FromArgb(150, 80, 14) : Color.FromArgb(75, 89, 110) };
                summary.Click += delegate { option.Choice.Checked = true; };
                var info = new Label { Text = "ⓘ", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Help,
                    ForeColor = Color.FromArgb(34, 99, 176), Font = new Font("Segoe UI", 11F) };
                info.Click += delegate { option.Choice.Checked = true; };
                foreach (Control control in new Control[] { option.Choice, summary, info }) hints.SetToolTip(control, tip);
                choices.Controls.Add(option.Choice, 0, i);
                choices.Controls.Add(summary, 1, i);
                choices.Controls.Add(info, 2, i);
            }
            // Rows keep their height; the list does not stretch to the explanation's height.
            choices.Dock = DockStyle.Top;
            choices.Height = 38 * ruleOptions.Length;
            how.Controls.Add(choices, 0, 0);
            ruleExplanation.Multiline = true; ruleExplanation.ReadOnly = true; ruleExplanation.ScrollBars = ScrollBars.Vertical;
            ruleExplanation.Dock = DockStyle.Fill; ruleExplanation.BorderStyle = BorderStyle.FixedSingle;
            ruleExplanation.BackColor = Color.FromArgb(247, 249, 252); ruleExplanation.ForeColor = Color.FromArgb(35, 59, 89);
            ruleExplanation.Margin = new Padding(14, 2, 0, 6);
            ruleExplanation.AccessibleName = "What the selected option does";
            how.Controls.Add(ruleExplanation, 1, 0);
            AddWide(assign, how, 7);
            ruleHint.Dock = DockStyle.Fill; ruleHint.TextAlign = ContentAlignment.MiddleLeft; ruleHint.AutoEllipsis = true;
            assign.Controls.Add(ruleHint, 0, 8);
            saveRule = Button("Save rule", SaveRule);
            saveRule.BackColor = Color.FromArgb(34, 99, 176);
            saveRule.ForeColor = Color.White;
            saveRule.FlatAppearance.BorderSize = 0;
            saveRule.Margin = new Padding(3, 8, 3, 6);
            assign.Controls.Add(saveRule, 1, 8);
            restoreApp = Button("Release for now", RestoreSelected);
            restoreApp.Margin = new Padding(3, 8, 3, 6);
            hints.SetToolTip(restoreApp, "Returns this app to normal until you choose Apply saved rules or restart DisplayWeave. Nothing is saved.");
            assign.Controls.Add(restoreApp, 2, 8);
            body.Controls.Add(assign, 0, 2);

            var saved = Table(3, 4);
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            saved.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            foreach (int height in new[] { 34, 112, 26, 46 }) saved.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            AddWide(saved, Heading("Saved rules"), 0);
            savedRules.View = View.Details;
            savedRules.FullRowSelect = true;
            savedRules.HideSelection = false;
            savedRules.MultiSelect = false;
            savedRules.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            savedRules.Dock = DockStyle.Fill;
            savedRules.AccessibleName = "Saved rules";
            savedRules.Columns.Add("App", 200);
            savedRules.Columns.Add("Region", 160);
            savedRules.Columns.Add("How", 190);
            savedRules.Columns.Add("Window class", 280);
            AddWide(saved, savedRules, 1);
            savedRuleDetails.Dock = DockStyle.Fill; savedRuleDetails.AutoEllipsis = true;
            savedRuleDetails.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(saved, savedRuleDetails, 2);
            saved.Controls.Add(Button("Apply saved rules", ApplySavedRules), 1, 3);
            removeRule = Button("Remove rule", RemoveRule);
            saved.Controls.Add(removeRule, 2, 3);
            body.Controls.Add(saved, 0, 3);
        }

        private RuleOption CheckedRuleOption()
        {
            return Array.Find(ruleOptions, delegate(RuleOption option) { return option.Choice != null && option.Choice.Checked; });
        }

        private void SaveRule()
        {
            var item = windows.SelectedItem as WindowCandidate;
            RuleOption option = CheckedRuleOption();
            if (item == null || option == null) return;
            if (option.Action == ApplicationAction.Ignore) IgnoreSelected();
            else Assign(item.Handle, ruleRegion.SelectedIndex, option.Action);
        }

        // The steps for Centered fullscreen with the chosen region. Not usable when the region is not in the middle of its display.
        private string CenteredGuide(out bool usable)
        {
            usable = false;
            int index = ruleRegion.SelectedIndex;
            if (index < 0 || index >= settings.Zones.Count) return "Choose a region to see what to set up.";
            DisplayMonitor monitor = DisplayMonitor.Find(CurrentScreen.DeviceName);
            Rectangle region = settings.Zones[index].ToPixels(Area);
            string name = settings.Zones[index].Name;
            Size mode;
            if (monitor == null || !DisplayMonitor.TryCenteredMode(region, monitor.Bounds, Area, out mode))
                return name + " is not in the middle of its display, so a game centered by the graphics driver would not line up with it. " +
                    "Choose the middle region, or use Borderless in region.";
            usable = true;
            string size = mode.Width.ToString("N0") + " × " + mode.Height.ToString("N0");
            string adapter;
            DiagnosticReport.DisplayAdapters().TryGetValue(monitor.DeviceName, out adapter);
            return "For games in exclusive fullscreen. Your graphics driver shows the game at " + size + " in the middle of the display, with black " +
                "on both sides. Nothing is loaded into the game, and DisplayWeave changes no settings: you set this up once.\r\n\r\n" +
                DriverSteps(adapter ?? String.Empty, size) + "\r\n   " +
                (monitor.OffersMode(mode) ? "Windows already offers " + size + "." : "Windows does not offer " + size + " yet.") + "\r\n" +
                "3. In the game, choose Fullscreen at " + size + ".\r\nMenu names vary between driver versions.\r\n\r\n" +
                "While the game has focus, the other regions are black, and Windows can move other windows while the display is smaller. " +
                "DisplayWeave pauses while the display is smaller and resumes by itself when it is back to " +
                monitor.Bounds.Width.ToString("N0") + " × " + monitor.Bounds.Height.ToString("N0") + ".";
        }

        private static string DriverSteps(string adapter, string size)
        {
            if (adapter.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || adapter.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. AMD Software: Settings → Display. Turn on GPU Scaling and set Scaling Mode to Center.\r\n" +
                    "2. Under Custom Resolutions, create " + size + " at your usual refresh rate.";
            if (adapter.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. NVIDIA Control Panel: Adjust desktop size and position. Choose No scaling, performed on the GPU.\r\n" +
                    "2. Change resolution → Customize → Create Custom Resolution: " + size + ".";
            if (adapter.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. Intel Graphics Command Center: Display. Set Scale to Centered.\r\n2. Add a custom resolution of " + size + ".";
            return "1. In your graphics driver's settings, turn on GPU scaling and choose centered (no scaling).\r\n" +
                "2. Add a custom resolution of " + size + ".";
        }

        // ToolTips do not wrap; break long text into lines of about this many characters.
        private static string Wrap(string text, int width)
        {
            var result = new StringBuilder();
            foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
            {
                int line = 0;
                foreach (string word in paragraph.Split(' '))
                {
                    if (line > 0 && line + 1 + word.Length > width) { result.Append('\n'); line = 0; }
                    else if (line > 0) { result.Append(' '); line++; }
                    result.Append(word);
                    line += word.Length;
                }
                result.Append('\n');
            }
            return result.ToString().TrimEnd('\n');
        }

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
            body.Controls.Add(Introduction("Keyboard shortcuts", "Ctrl + Alt + Shift + 1…9     Force the active app into a region\nCtrl + Alt + Shift + 0          Release the active app\nCtrl + Alt + Shift + Backspace     Pause all handling"), 0, 4);
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

        // Enables the rule buttons and explains the selected option; Centered fullscreen's steps follow the chosen region.
        private void UpdateRuleActions()
        {
            bool selected = windows.SelectedItem is WindowCandidate;
            RuleOption option = CheckedRuleOption();
            bool usable = option != null && (option.Action == ApplicationAction.Ignore || ruleRegion.SelectedIndex >= 0);
            string explanation = option == null ? String.Empty : option.Details;
            if (option != null && option.Action == ApplicationAction.CenteredFullscreen)
            {
                bool centered;
                explanation = CenteredGuide(out centered);
                usable &= centered;
            }
            if (ruleExplanation.Text != explanation) ruleExplanation.Text = explanation;
            ruleRegion.Enabled = option == null || option.Action != ApplicationAction.Ignore;
            if (saveRule != null)
            {
                saveRule.Enabled = selected && usable;
                // A disabled flat button keeps its colour and still looks clickable.
                saveRule.BackColor = saveRule.Enabled ? Color.FromArgb(34, 99, 176) : Color.FromArgb(200, 207, 217);
            }
            // Says why Save rule is unavailable instead of leaving a disabled button unexplained.
            string hint = !selected ? "Choose the app in step 1. Not listed? Open the list again, or use Drag onto app."
                : option == null ? "Choose how DisplayWeave handles it in step 3."
                : !usable && option.Action == ApplicationAction.CenteredFullscreen && ruleRegion.SelectedIndex >= 0 ? "Centered fullscreen needs the region in the middle of the display."
                : !usable ? "Choose its region in step 2." : "Saved rules take priority over automatic handling.";
            if (ruleHint.Text != hint) ruleHint.Text = hint;
            ruleHint.ForeColor = selected && usable ? Color.FromArgb(75, 89, 110) : Color.FromArgb(150, 80, 14);
            if (restoreApp != null) restoreApp.Enabled = selected;
            if (removeRule != null) removeRule.Enabled = SelectedRule() != null;
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
            panel.Controls.Add(new Label { Text = title, UseMnemonic = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(25, 38, 65) }, 0, 0);
            panel.Controls.Add(new Label { Text = description, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(75, 89, 110) }, 0, 1);
            return panel;
        }

        private static Label Heading(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold), ForeColor = Color.FromArgb(25, 38, 65) };
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
