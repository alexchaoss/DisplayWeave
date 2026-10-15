using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The window around the tabs (header, tabs, footer, the controls' events) and the helpers that build controls.
    public sealed partial class MainForm
    {
        private readonly TabControl pages = new TabControl();
        private readonly Label engineState = new Label();
        private readonly Label actionFeedback = new Label();
        private readonly Button pauseHandling = new Button();
        private readonly ToolTip hints = new ToolTip();
        private TabPage statusPage;
        private Button saveLayout;

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
            engineState.Click += delegate { pages.SelectedTab = statusPage; };
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
            var shortcutsPage = Page("Shortcuts");
            statusPage = Page("Status & help");
            pages.TabPages.AddRange(new[] { layoutPage, rulesPage, shortcutsPage, statusPage });
            pages.SelectedIndexChanged += delegate
            {
                if (pages.SelectedTab != rulesPage && picking)
                {
                    picking = false; picker.Capture = false; Cursor = Cursors.Default;
                }
                // An app started after DisplayWeave appears as soon as the tab opens.
                if (pages.SelectedTab == rulesPage) RefreshRules();
            };
            BuildLayoutPage(layoutPage);
            BuildRulesPage(rulesPage);
            BuildShortcutsPage(shortcutsPage);
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
            actionFeedback.Click += delegate { pages.SelectedTab = statusPage; };
            // While waiting for the display it reads Use this display size: the layout is saved for the display as it is now.
            var save = saveLayout = Button("Save layout", delegate { EndDisplayWait(); SaveLayout(true); });
            save.BackColor = Color.FromArgb(34, 99, 176);
            save.ForeColor = Color.White;
            save.FlatAppearance.BorderSize = 0;
            save.Margin = new Padding(12, 5, 0, 9);
            footer.Controls.Add(layoutState, 0, 0);
            footer.Controls.Add(actionFeedback, 0, 1);
            footer.Controls.Add(save, 1, 0); footer.SetRowSpan(save, 2);
            root.Controls.Add(footer, 0, 2);

            monitors.SelectedIndexChanged += delegate { if (!updating) { LayoutChanging(); settings.MonitorDevice = DisplayMissing ? missingDisplay : CurrentScreen.DeviceName; ReloadZones(zoneList.SelectedIndex); } };
            workArea.CheckedChanged += delegate { if (!updating) { LayoutChanging(); settings.UseWorkArea = workArea.Checked; ReloadZones(zoneList.SelectedIndex); } };
            apps.SelectedIndexChanged += delegate { ShowSelectedApp(); };
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
                if (!updating) UpdateRuleActions();
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle available = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            Location = new Point(Math.Max(available.Left, Math.Min(Left, available.Right - Width)),
                Math.Max(available.Top, Math.Min(Top, available.Bottom - Height)));
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
    }
}
