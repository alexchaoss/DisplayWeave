using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The Layout tab: the display, the ruler editor and its regions, saving the layout, and waiting for the display
    // to return to its layout size after a game in exclusive fullscreen changed it.
    public sealed partial class MainForm
    {
        private readonly ComboBox monitors = new ComboBox();
        private readonly ComboBox presets = new ComboBox();
        private readonly CheckBox workArea = new CheckBox();
        // The layout area to wait for after the display changed size, as a game in exclusive fullscreen does; empty when not waiting.
        private Rectangle displayWait = Rectangle.Empty;
        private DateTime displayWaitSince;
        private readonly ListBox zoneList = new ListBox();
        private readonly NumericUpDown gridSpacing = new NumericUpDown();
        private readonly Label regionDimensions = new Label();
        private readonly Label layoutState = new Label();
        private bool layoutDirty;
        private readonly RulerCanvas canvas = new RulerCanvas();
        private RulerLayout rulerLayout;
        private Screen[] screens;

        private Screen CurrentScreen { get { return screens != null && monitors.SelectedIndex >= 0 && monitors.SelectedIndex < screens.Length ? screens[monitors.SelectedIndex] : Screen.PrimaryScreen; } }
        private Rectangle Area { get { return workArea.Checked ? CurrentScreen.WorkingArea : CurrentScreen.Bounds; } }

        private int ScalePercent
        {
            get { DisplayMonitor monitor = DisplayMonitor.Find(CurrentScreen.DeviceName); return monitor == null ? 100 : monitor.ScalePercent; }
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
            layout.Controls.Add(Introduction("Arrange your screen", "Drag ruler pins to set your regions; double-click a ruler to add a pin. Every region stays separate, with no gaps or overlaps."), 0, 0);

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

            var presetRow = Table(8, 1);
            foreach (int width in new[] { 64, 196, 120, 0, 60, 180, 84, 84 })
                presetRow.ColumnStyles.Add(width == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width));
            presetRow.Padding = new Padding(0, 8, 0, 4);
            presetRow.Controls.Add(Caption("Preset"), 0, 0);
            SetupCombo(presets); presets.AccessibleName = "Layout preset";
            presets.Items.AddRange(new object[] { "Choose a preset…", "Two halves", "Three columns", "Wide center", "Four quadrants" });
            presets.SelectedIndex = 0;
            presetRow.Controls.Add(presets, 1, 0);
            presetRow.Controls.Add(Button("Use preset", delegate { if (presets.SelectedIndex <= 0) { notice = "Choose a preset first, or keep your current custom layout."; UpdateStatus(); return; } LayoutChanging(); rulerLayout.ApplyPreset(presets.SelectedIndex - 1, ScalePercent); ReloadZones(0); }), 2, 0);
            AddProfileRow(presetRow, 4);
            layout.Controls.Add(presetRow, 0, 3);
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
            foreach (ListViewItem row in savedRules.Items) row.SubItems[1].Text = RuleRegionName((ApplicationRule)row.Tag);
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

        // Resume, unless waiting for the display: regions of the temporary display size would squeeze the apps.
        private void ResumeHandling()
        {
            if (!displayWait.IsEmpty) { ReportDisplayWait(); return; }
            SaveLayout(true);
        }

        private void ReportDisplayWait()
        {
            Report("Region handling resumes by itself when the display is back to " + displayWait.Width.ToString("N0") + " × " +
                displayWait.Height.ToString("N0") + ". To use the display as it is now, choose Save layout.");
        }

        // Resumes once the display is back to the size the layout was made for, after a change such as a game in
        // exclusive fullscreen makes. The layout is never rebuilt for the temporary size.
        private void ResumeWhenDisplayReturns()
        {
            DisplayMonitor monitor = DisplayMonitor.Find(settings.MonitorDevice);
            if (monitor == null || (settings.UseWorkArea ? monitor.WorkingArea : monitor.Bounds) != displayWait)
            {
                // A few seconds give a game time to report its exclusive fullscreen after changing the display.
                if (DateTime.UtcNow - displayWaitSince > TimeSpan.FromSeconds(3) && !ExclusiveFullscreenRunning()) DisplayChangedForGood();
                return;
            }
            Rectangle area = displayWait;
            EndDisplayWait();
            if (layoutDirty || !fullscreen.Paused) return;
            fullscreen.Resume();
            Report("The display is back to " + area.Width.ToString("N0") + " × " + area.Height.ToString("N0") + "; region handling resumed.");
        }

        // The display changed without a game in exclusive fullscreen, such as a new resolution or monitor: the layout is
        // read again for the display as it is now and stays paused for review. A renamed display is followed first.
        private void DisplayChangedForGood()
        {
            displayWait = Rectangle.Empty;
            string moved = layoutDirty ? null : service.RebindMonitor();
            LoadMonitors();
            ReloadZones(0);
            RefreshRules();
            Report(moved ?? "The display changed and no game is in exclusive fullscreen. Region handling is paused; review the layout, then Save layout.");
        }

        // Only a fullscreen game keeps the display changed while it runs; Windows says when one is in front.
        private static bool ExclusiveFullscreenRunning()
        {
            int state;
            return SHQueryUserNotificationState(out state) == 0 && (state == 3 || state == 2); // QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_BUSY
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        // Stops waiting for the display: the monitors and regions are read again for the display as it is now.
        private void EndDisplayWait()
        {
            if (displayWait.IsEmpty) return;
            displayWait = Rectangle.Empty;
            LoadMonitors();
            ReloadZones(zoneList.SelectedIndex);
        }
    }
}
