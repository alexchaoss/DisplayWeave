using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The App rules tab: choosing an app, its region and an option, saving the rule, and the saved-rules list.
    public sealed partial class MainForm
    {
        private readonly ComboBox windows = new ComboBox();
        private readonly CheckBox automaticRegions = new CheckBox();
        private readonly ListView savedRules = new ListView();
        private readonly Label pickedApp = new Label();
        private readonly Button picker = new Button();
        private bool picking;
        private ApplicationIdentity pickedIdentity;
        private readonly ComboBox ruleRegion = new ComboBox();
        private readonly Label savedRuleDetails = new Label();
        private readonly TextBox ruleExplanation = new TextBox();
        private readonly Label ruleHint = new Label();
        private Button saveRule, restoreApp, removeRule;
        private bool syncingRegion;

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

        private void SyncRuleRegion()
        {
            syncingRegion = true;
            ruleRegion.SelectedIndex = zoneList.SelectedIndex;
            syncingRegion = false;
            UpdateRuleActions();
        }

        private void SaveRule()
        {
            var item = windows.SelectedItem as WindowCandidate;
            RuleOption option = CheckedRuleOption();
            if (item == null || option == null) return;
            if (option.Action == ApplicationAction.Ignore) IgnoreSelected();
            else Assign(item.Handle, ruleRegion.SelectedIndex, option.Action);
        }

        private void Assign(IntPtr handle, int regionIndex, ApplicationAction action = ApplicationAction.ForceFullscreen)
        {
            RunSaving(delegate { return service.Assign(handle, regionIndex, action); }, true);
        }

        private void ApplySavedRules()
        {
            if (!displayWait.IsEmpty) { ReportDisplayWait(); return; }
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

        private ApplicationRule SelectedRule()
        {
            return savedRules.SelectedItems.Count > 0 ? savedRules.SelectedItems[0].Tag as ApplicationRule : null;
        }

        private string RuleRegionName(ApplicationRule rule)
        {
            if (rule.Action == ApplicationAction.Ignore) return "—";
            Rectangle region;
            int index = settings.Zones.FindIndex(delegate(Zone zone) { return zone.Id == rule.ZoneId; });
            return index < 0 || !RegionGeometry.TryResolveRegion(rule, settings, out region) ? "Missing" : settings.Zones[index].Name;
        }

        private void RemoveRule()
        {
            var selected = SelectedRule();
            if (selected == null) return;
            RunSaving(delegate { return service.RemoveRule(selected); }, true);
        }

        private void RefreshRules()
        {
            updating = true;
            savedRules.BeginUpdate();
            savedRules.Items.Clear();
            foreach (ApplicationRule rule in rules.Rules)
                savedRules.Items.Add(new ListViewItem(new[] { rule.DisplayName, RuleRegionName(rule), ApplicationRule.ActionName(rule.Action), rule.WindowClass }) { Tag = rule });
            // The newest rule is last; select it so its details show.
            if (savedRules.Items.Count > 0) savedRules.Items[savedRules.Items.Count - 1].Selected = true;
            savedRules.EndUpdate();
            updating = false;
            ShowRuleDetails(false);
            UpdateRuleActions();
        }

        private void ShowRuleDetails(bool announce = true)
        {
            if (updating) return;
            var rule = SelectedRule();
            if (rule == null)
            {
                savedRuleDetails.Text = savedRules.Items.Count == 0 ? "No saved rules. Add one above if an app needs it." : "Select a rule to see its details.";
                hints.SetToolTip(savedRuleDetails, null);
                return;
            }
            string details;
            Rectangle region;
            if (rule.Action == ApplicationAction.Ignore) details = "Never moved or resized: " + rule.ExecutablePath + " [" + rule.WindowClass + "]";
            else if (!RegionGeometry.TryResolveRegion(rule, settings, out region))
                details = "This rule's region or monitor is missing. Select its app and save a new region; it will not be retargeted automatically.";
            else
            {
                string where = region.Width + " × " + region.Height + " at " + region.X + ", " + region.Y;
                details = (rule.Action == ApplicationAction.MaximizeInRegion ? "Fitted into " + where + " when maximized"
                    : rule.Action == ApplicationAction.BorderlessInRegion ? "Shown without its title bar at " + where
                    : rule.Action == ApplicationAction.CenteredFullscreen ? "Left alone; its exclusive fullscreen is centered by the graphics driver over " + where
                    : "Component loaded; fullscreen in " + where) + " · " + rule.ExecutablePath;
            }
            savedRuleDetails.Text = details;
            hints.SetToolTip(savedRuleDetails, details);
            if (announce) { notice = details; UpdateStatus(); }
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
                if (((WindowCandidate)windows.Items[i]).Handle == handle) { windows.SelectedIndex = i; notice = "Application selected. Choose its region and how DisplayWeave handles it, then Save rule."; UpdateStatus(); return; }
            notice = "The selected window is not an eligible application window."; UpdateStatus();
        }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    }
}
