using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The App rules tab: the apps on the left (saved rules first, then running apps without one) and the selected
    // app's option, region and Save on the right.
    public sealed partial class MainForm
    {
        // A saved rule (its app may not be running) or a running app without a rule.
        private sealed class AppEntry
        {
            internal ApplicationRule Rule;
            internal WindowCandidate Window;
        }

        private readonly ListView apps = new ListView();
        private readonly ListViewGroup savedGroup = new ListViewGroup("Saved");
        private readonly ListViewGroup unsavedGroup = new ListViewGroup("Not saved");
        private readonly CheckBox automaticRegions = new CheckBox();
        private readonly Label appName = new Label();
        private readonly Label pickedApp = new Label();
        private readonly Button picker = new Button();
        private bool picking;
        private ApplicationIdentity pickedIdentity;
        private readonly ComboBox ruleRegion = new ComboBox();
        private readonly CheckBox pinRegion = new CheckBox();
        private readonly Label savedRuleDetails = new Label();
        private readonly TextBox ruleExplanation = new TextBox();
        private readonly Label ruleHint = new Label();
        private Button saveRule, restoreApp;
        private bool syncingRegion;

        private void BuildRulesPage(TabPage page)
        {
            page.AutoScroll = true;
            // Below this height the page scrolls instead of squeezing the options.
            page.AutoScrollMinSize = new Size(0, 560);
            hints.AutoPopDelay = 30000;
            var body = Table(1, 3);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(body);
            body.Controls.Add(Introduction("App rules", "DisplayWeave fits apps by itself. Add a rule only for an app that needs something different."), 0, 0);
            var auto = Table(2, 2);
            auto.BackColor = Color.FromArgb(237, 245, 253);
            auto.Padding = new Padding(12, 5, 12, 5);
            auto.Margin = new Padding(0, 0, 0, 12);
            auto.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            auto.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            auto.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            auto.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            automaticRegions.Text = "Automatically use each app's current region";
            automaticRegions.Dock = DockStyle.Fill;
            auto.Controls.Add(automaticRegions, 0, 0);
            auto.Controls.Add(Caption("Move a window into a region, then maximize it or use borderless fullscreen. Nothing is loaded into apps."), 0, 1);
            var apply = Button("Apply saved rules", ApplySavedRules);
            apply.Margin = new Padding(3, 10, 0, 10);
            auto.Controls.Add(apply, 1, 0); auto.SetRowSpan(apply, 2);
            body.Controls.Add(auto, 0, 1);

            var split = Table(2, 1);
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Controls.Add(split, 0, 2);

            var left = Table(2, 2);
            left.Margin = new Padding(0, 0, 16, 0);
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            StyleButton(picker); picker.Text = "⊕ Drag onto app";
            hints.SetToolTip(picker, "Hold the mouse button down here, drag onto the app's window, then release.");
            picker.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { picking = true; picker.Capture = true; Cursor = Cursors.Cross; notice = "Keep holding and drag onto the application window, then release."; UpdateStatus(); } };
            picker.MouseUp += delegate(object sender, MouseEventArgs e) { if (!picking) return; picking = false; picker.Capture = false; Cursor = Cursors.Default; SelectWindowAtCursor(); };
            picker.MouseCaptureChanged += delegate
            {
                if (!picker.Capture && picking && IsHandleCreated)
                    BeginInvoke((Action)delegate { if (!picker.Capture && picking) { picking = false; Cursor = Cursors.Default; } });
            };
            left.Controls.Add(picker, 0, 0);
            left.Controls.Add(Button("Refresh", RefreshRules), 1, 0);
            apps.View = View.Details;
            apps.FullRowSelect = true;
            apps.HideSelection = false;
            apps.MultiSelect = false;
            apps.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            apps.Dock = DockStyle.Fill;
            apps.AccessibleName = "Apps";
            apps.Columns.Add("App", 120);
            apps.Columns.Add("Rule", 114);
            apps.ShowItemToolTips = true;
            apps.Groups.Add(savedGroup);
            apps.Groups.Add(unsavedGroup);
            AddWide(left, apps, 1);
            split.Controls.Add(left, 0, 0);

            var detail = Table(3, 8);
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            foreach (int height in new[] { 30, 22, 24, 34 * ruleOptions.Length + 4, 28, 40, 50 }) detail.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            appName.Dock = DockStyle.Fill; appName.AutoEllipsis = true; appName.UseMnemonic = false;
            appName.Font = new Font("Segoe UI", 12F, FontStyle.Bold); appName.ForeColor = Color.FromArgb(25, 38, 65);
            AddWide(detail, appName, 0);
            pickedApp.Dock = DockStyle.Fill; pickedApp.AutoEllipsis = true;
            pickedApp.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(detail, pickedApp, 1);
            savedRuleDetails.Dock = DockStyle.Fill; savedRuleDetails.AutoEllipsis = true;
            savedRuleDetails.ForeColor = Color.FromArgb(75, 89, 110);
            AddWide(detail, savedRuleDetails, 2);

            var how = Table(2, 1);
            how.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 530));
            how.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var choices = Table(3, ruleOptions.Length);
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            for (int i = 0; i < ruleOptions.Length; i++)
            {
                RuleOption option = ruleOptions[i];
                choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                string tip = Wrap(option.Details ?? "For games in exclusive fullscreen: your graphics driver shows the game at the region's size in the middle " +
                    "of the display. Nothing is loaded into the game. Select it to see the steps for the chosen region.", 90);
                option.Choice = new RadioButton { Text = option.Title, Dock = DockStyle.Fill, Checked = i == 0, AccessibleDescription = option.Summary };
                option.Choice.CheckedChanged += delegate { if (option.Choice.Checked) UpdateRuleActions(); };
                var summary = new Label { Text = option.Summary, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
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
            how.Controls.Add(choices, 0, 0);
            ruleExplanation.Multiline = true; ruleExplanation.ReadOnly = true; ruleExplanation.ScrollBars = ScrollBars.Vertical;
            ruleExplanation.Dock = DockStyle.Fill; ruleExplanation.BorderStyle = BorderStyle.FixedSingle;
            ruleExplanation.BackColor = Color.FromArgb(247, 249, 252); ruleExplanation.ForeColor = Color.FromArgb(35, 59, 89);
            ruleExplanation.Margin = new Padding(14, 2, 0, 6);
            ruleExplanation.AccessibleName = "What the selected option does";
            how.Controls.Add(ruleExplanation, 1, 0);
            AddWide(detail, how, 3);
            pinRegion.Text = "Pin to a specific region (otherwise DisplayWeave uses the region the app is currently in)";
            pinRegion.Dock = DockStyle.Fill;
            pinRegion.AccessibleName = "Pin the app to a specific region";
            pinRegion.CheckedChanged += delegate { if (!updating) UpdateRuleActions(); };
            AddWide(detail, pinRegion, 4);
            SetupCombo(ruleRegion); ruleRegion.AccessibleName = "App rule target region";
            detail.Controls.Add(ruleRegion, 0, 5);
            ruleHint.Dock = DockStyle.Fill; ruleHint.TextAlign = ContentAlignment.MiddleLeft; ruleHint.AutoEllipsis = true;
            detail.Controls.Add(ruleHint, 0, 6);
            restoreApp = Button("Release for now", RestoreSelected);
            restoreApp.Margin = new Padding(3, 8, 3, 6);
            hints.SetToolTip(restoreApp, "Returns this app to normal until you choose Apply saved rules or restart DisplayWeave. Nothing is saved.");
            detail.Controls.Add(restoreApp, 1, 6);
            saveRule = Button("Save", SaveRule);
            saveRule.ForeColor = Color.White;
            saveRule.FlatAppearance.BorderSize = 0;
            saveRule.Margin = new Padding(3, 8, 0, 6);
            detail.Controls.Add(saveRule, 2, 6);
            split.Controls.Add(detail, 1, 0);
        }

        // Shows, enables and explains what applies to the selected app and option; Centered fullscreen's steps follow the chosen region.
        private void UpdateRuleActions()
        {
            AppEntry entry = SelectedEntry();
            bool running = entry != null && entry.Window != null;
            RuleOption option = CheckedRuleOption();
            bool automatic = option == null || option.Action == null;
            // Borderless and Centered always need a region; Force and Maximize may pin one or follow the app's current region.
            bool needsRegion = option != null && (option.Action == ApplicationAction.BorderlessInRegion || option.Action == ApplicationAction.CenteredFullscreen);
            bool pinnable = option != null && (option.Action == ApplicationAction.ForceFullscreen || option.Action == ApplicationAction.MaximizeInRegion);
            bool wantRegion = needsRegion || (pinnable && pinRegion.Checked);
            bool usable = !wantRegion || ruleRegion.SelectedIndex >= 0;
            string explanation = option == null ? String.Empty : option.Details;
            if (option != null && option.Action == ApplicationAction.CenteredFullscreen)
            {
                bool centered;
                explanation = CenteredGuide(out centered);
                usable &= centered;
            }
            if (ruleExplanation.Text != explanation) ruleExplanation.Text = explanation;
            // Always shown, so pinning stays discoverable while Automatic is selected.
            pinRegion.Enabled = pinnable;
            ruleRegion.Enabled = wantRegion;
            // Save is offered only for a change: another option, or another pinned region.
            ApplicationRule rule = entry == null ? null : entry.Rule;
            bool changed = entry != null && option != null && (option.Action != (rule == null ? (ApplicationAction?)null : rule.Action) ||
                (wantRegion ? ruleRegion.SelectedIndex : -1) != PinnedIndex(rule));
            if (saveRule != null)
            {
                saveRule.Enabled = changed && usable;
                // A disabled flat button keeps its colour and still looks clickable.
                saveRule.BackColor = saveRule.Enabled ? Color.FromArgb(34, 99, 176) : Color.FromArgb(200, 207, 217);
            }
            // Says why Save is unavailable instead of leaving a disabled button unexplained.
            string hint = entry == null ? "Choose an app on the left. Not listed? Use Drag onto app."
                : !usable && option.Action == ApplicationAction.CenteredFullscreen && ruleRegion.SelectedIndex >= 0 ? "Centered fullscreen needs the region in the middle of the display."
                : !usable ? "Choose its region."
                : !changed ? (rule == null ? "Handled automatically. Choose an option to add a rule." : "Saved. Saved rules take priority over automatic handling.")
                : automatic ? "Save to remove this rule and return the app to automatic handling."
                : !running ? "Not running: the changed rule applies the next time it starts."
                : "Saved rules take priority over automatic handling.";
            if (ruleHint.Text != hint) ruleHint.Text = hint;
            ruleHint.ForeColor = entry != null && usable ? Color.FromArgb(75, 89, 110) : Color.FromArgb(150, 80, 14);
            if (restoreApp != null) restoreApp.Enabled = running;
        }

        // The rule's pinned region in the list, -1 without a pin, -2 when its region is gone.
        private int PinnedIndex(ApplicationRule rule)
        {
            if (rule == null || String.IsNullOrEmpty(rule.ZoneId)) return -1;
            int index = settings.Zones.FindIndex(delegate(Zone zone) { return zone.Id == rule.ZoneId; });
            return index < 0 ? -2 : index;
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
            AppEntry entry = SelectedEntry();
            RuleOption option = CheckedRuleOption();
            if (entry == null || option == null) return;
            if (option.Action == null)
            {
                if (entry.Rule != null) RunSaving(delegate { return service.RemoveRule(entry.Rule); }, true);
                return;
            }
            // Borderless and Centered always pin a region; Force and Maximize pin only when the box is checked, else -1 (current region).
            bool wantRegion = option.Action == ApplicationAction.BorderlessInRegion || option.Action == ApplicationAction.CenteredFullscreen ||
                ((option.Action == ApplicationAction.ForceFullscreen || option.Action == ApplicationAction.MaximizeInRegion) && pinRegion.Checked);
            int regionIndex = wantRegion ? ruleRegion.SelectedIndex : -1;
            // A saved rule of an app that is not running changes from the identity it holds.
            if (entry.Window == null) { RunSaving(delegate { return service.ChangeRule(entry.Rule, regionIndex, option.Action.Value); }, true); return; }
            if (!StillOpen(entry.Window)) return;
            if (option.Action == ApplicationAction.Ignore) { RunSaving(delegate { return service.Ignore(entry.Window.Handle); }, true); return; }
            Assign(entry.Window.Handle, regionIndex, option.Action.Value);
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
            AppEntry entry = SelectedEntry();
            if (entry == null || entry.Window == null || !StillOpen(entry.Window)) return;
            ReleaseWindow(entry.Window.Handle);
        }

        // The list can be older than the window: a closed app's handle may now belong to another app, which must not get its rule.
        private bool StillOpen(WindowCandidate window)
        {
            ApplicationIdentity current;
            string error;
            if (FullscreenController.TryInspect(window.Handle, out current, out error) && current.Key == window.Identity.Key) return true;
            Report(System.IO.Path.GetFileName(window.Identity.ExecutablePath) + " has closed, so nothing was changed. Refresh the list and try again.");
            return false;
        }

        private void ReleaseWindow(IntPtr handle)
        {
            Report(service.Release(handle));
        }

        private AppEntry SelectedEntry()
        {
            return apps.SelectedItems.Count > 0 ? apps.SelectedItems[0].Tag as AppEntry : null;
        }

        private string RuleRegionName(ApplicationRule rule)
        {
            if (rule.Action == ApplicationAction.Ignore) return "—";
            Rectangle region;
            int index = settings.Zones.FindIndex(delegate(Zone zone) { return zone.Id == rule.ZoneId; });
            return index < 0 || !RegionGeometry.TryResolveRegion(rule, settings, out region) ? "Missing" : settings.Zones[index].Name;
        }

        private string RuleSummary(ApplicationRule rule)
        {
            return ApplicationRule.ActionName(rule.Action) + (String.IsNullOrEmpty(rule.ZoneId) ? String.Empty : " · " + RuleRegionName(rule));
        }

        // Lists the saved rules, then the running apps without one, and keeps the selected app selected.
        private void RefreshRules()
        {
            AppEntry old = SelectedEntry();
            updating = true;
            apps.BeginUpdate();
            apps.Items.Clear();
            var running = WindowManager.ListApps();
            // Rules of other layout profiles name their regions, which this layout may not have.
            foreach (ApplicationRule rule in rules.Rules)
            {
                if (!rule.AppliesTo(settings.ProfileName)) continue;
                var entry = new AppEntry { Rule = rule, Window = running.Find(delegate(WindowCandidate app) { return rule.Matches(app.Identity); }) };
                var item = new ListViewItem(new[] { rule.DisplayName, RuleSummary(rule) }, savedGroup) { Tag = entry,
                    ToolTipText = rule.DisplayName + "  ·  " + RuleSummary(rule) + (entry.Window == null ? "  ·  not running" : String.Empty) };
                if (entry.Window == null) item.ForeColor = Color.FromArgb(130, 140, 155);
                apps.Items.Add(item);
            }
            foreach (WindowCandidate app in running)
                if (!rules.Rules.Exists(delegate(ApplicationRule rule) { return rule.AppliesTo(settings.ProfileName) && rule.Matches(app.Identity); }))
                    apps.Items.Add(new ListViewItem(new[] { app.Title, "Automatic" }, unsavedGroup) { Tag = new AppEntry { Window = app }, ToolTipText = app.Title });
            savedGroup.Header = service.Profiles().Count > 1 ? "Saved for layout profile " + settings.ProfileName : "Saved";
            if (old != null)
                foreach (ListViewItem item in apps.Items)
                    if (SameApp((AppEntry)item.Tag, old)) { item.Selected = true; item.EnsureVisible(); break; }
            apps.EndUpdate();
            updating = false;
            ShowSelectedApp();
        }

        // The same app after a refresh: the same saved rule, or a rule or window of the same program.
        private static bool SameApp(AppEntry entry, AppEntry other)
        {
            if (other.Rule != null && entry.Rule == other.Rule) return true;
            ApplicationIdentity identity = other.Window != null ? other.Window.Identity : null;
            return identity != null && (entry.Window != null ? entry.Window.SameApp(identity) : entry.Rule.Matches(identity));
        }

        // Loads the selected app's saved rule (or Automatic) into the options on the right.
        private void ShowSelectedApp()
        {
            if (updating) return;
            AppEntry entry = SelectedEntry();
            ApplicationRule rule = entry == null ? null : entry.Rule;
            ApplicationAction? action = rule == null ? (ApplicationAction?)null : rule.Action;
            updating = true;
            foreach (RuleOption option in ruleOptions) option.Choice.Checked = option.Action == action;
            pinRegion.Checked = PinnedIndex(rule) != -1;
            if (PinnedIndex(rule) >= 0) ruleRegion.SelectedIndex = PinnedIndex(rule);
            updating = false;
            appName.Text = entry == null ? "No app selected" : rule != null ? rule.DisplayName : entry.Window.Title;
            UpdatePickedApp();
            ShowRuleDetails();
            UpdateRuleActions();
            UpdateStatus();
        }

        private void ShowRuleDetails()
        {
            AppEntry entry = SelectedEntry();
            ApplicationRule rule = entry == null ? null : entry.Rule;
            string details;
            Rectangle region;
            if (entry == null) details = apps.Items.Count == 0 ? "No apps to show. Open an app, then choose Refresh." : String.Empty;
            else if (rule == null) details = "No rule: handled automatically.";
            else if (rule.Action == ApplicationAction.Ignore) details = "Never moved or resized.";
            else if (String.IsNullOrEmpty(rule.ZoneId)) details = ApplicationRule.ActionName(rule.Action) + " in the region the app is in.";
            else if (!RegionGeometry.TryResolveRegion(rule, settings, out region))
                details = "This rule's region or monitor is missing. Choose a region and Save; it will not be retargeted automatically.";
            else
            {
                string where = region.Width + " × " + region.Height + " at " + region.X + ", " + region.Y;
                details = rule.Action == ApplicationAction.MaximizeInRegion ? "Fitted into " + where + " when maximized"
                    : rule.Action == ApplicationAction.BorderlessInRegion ? "Shown without its title bar at " + where
                    : rule.Action == ApplicationAction.CenteredFullscreen ? "Left alone; its exclusive fullscreen is centered by the graphics driver over " + where
                    : "Component loaded; fullscreen in " + where;
            }
            if (rule != null)
                details += "  ·  Last used " + (rule.LastMatched == DateTime.MinValue ? "never" : rule.LastMatched.Date == DateTime.UtcNow.Date ? "today" : rule.LastMatched.ToLocalTime().ToShortDateString());
            savedRuleDetails.Text = details;
            hints.SetToolTip(savedRuleDetails, details);
        }

        private void UpdatePickedApp()
        {
            pickedIdentity = null;
            AppEntry entry = SelectedEntry();
            if (entry == null) { pickedApp.Text = String.Empty; return; }
            if (entry.Window == null) { pickedApp.Text = entry.Rule.ExecutablePath + "   ·   " + entry.Rule.WindowClass + "   ·   not running"; return; }
            ApplicationIdentity identity;
            string error;
            if (FullscreenController.TryInspect(entry.Window.Handle, out identity, out error))
            {
                pickedIdentity = identity;
                pickedApp.Text = identity.ExecutablePath + "   ·   " + identity.WindowClass + (identity.Is32Bit ? "   ·   32-bit" : "   ·   64-bit");
            }
            else pickedApp.Text = error;
            hints.SetToolTip(pickedApp, pickedApp.Text);
        }

        private void SelectWindowAtCursor()
        {
            IntPtr handle = GetAncestor(WindowFromPoint(Cursor.Position), 2);
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(handle, out identity, out error)) { notice = error; UpdateStatus(); return; }
            RefreshRules();
            foreach (ListViewItem item in apps.Items)
            {
                var entry = (AppEntry)item.Tag;
                if (entry.Window == null || !entry.Window.SameApp(identity)) continue;
                // The app now stands for the window it was dragged onto: a Force rule attaches to that one.
                entry.Window.Handle = handle; entry.Window.Identity = identity;
                if (item.Selected) ShowSelectedApp(); else { item.Selected = true; item.EnsureVisible(); }
                notice = "Application selected. Choose how DisplayWeave handles it, then Save.";
                UpdateStatus();
                return;
            }
            notice = "The selected window is not an eligible application window."; UpdateStatus();
        }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    }
}
