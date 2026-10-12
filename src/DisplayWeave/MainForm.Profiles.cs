using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Layout profiles: the Layout tab's profile row, the tray submenu and switching between them.
    public sealed partial class MainForm
    {
        private readonly ComboBox profiles = new ComboBox();
        private ToolStripMenuItem trayProfiles;

        private void AddProfileRow(TableLayoutPanel row, int column)
        {
            row.Controls.Add(Caption("Profile"), column, 0);
            SetupCombo(profiles); profiles.AccessibleName = "Layout profile";
            profiles.SelectedIndexChanged += delegate { if (!updating && profiles.SelectedItem != null) SwitchProfile((string)profiles.SelectedItem); };
            row.Controls.Add(profiles, column + 1, 0);
            var create = Button("New…", CreateProfile);
            hints.SetToolTip(create, "Copy this layout and its app rules into a new profile.");
            row.Controls.Add(create, column + 2, 0);
            row.Controls.Add(Button("Delete", DeleteProfile), column + 3, 0);
        }

        private void RefreshProfiles()
        {
            bool wasUpdating = updating;
            updating = true;
            profiles.Items.Clear();
            foreach (string name in service.Profiles()) profiles.Items.Add(name);
            profiles.SelectedItem = settings.ProfileName;
            updating = wasUpdating;
        }

        // The tray lists the profiles when there is more than one.
        private void RefreshTrayProfiles()
        {
            trayProfiles.DropDownItems.Clear();
            List<string> names = service.Profiles();
            foreach (string name in names)
            {
                string target = name;
                var item = new ToolStripMenuItem(name) { Checked = String.Equals(name, settings.ProfileName, StringComparison.OrdinalIgnoreCase) };
                item.Click += delegate { SwitchProfile(target); };
                trayProfiles.DropDownItems.Add(item);
            }
            trayProfiles.Visible = names.Count > 1;
        }

        private void SwitchProfile(string name)
        {
            if (String.Equals(name, settings.ProfileName, StringComparison.OrdinalIgnoreCase)) { RefreshProfiles(); return; }
            // Edits to the current profile are kept in it.
            if (layoutDirty && !SaveLayout(false)) { RefreshProfiles(); return; }
            displayWait = Rectangle.Empty;
            string message;
            try { message = service.SwitchProfile(name); }
            catch (IOException ex) { message = "Could not switch the layout profile: " + ex.Message; }
            catch (ArgumentException) { message = "There is no layout profile named " + name + "."; }
            ProfileChanged(message);
        }

        private void CreateProfile()
        {
            string name = AskText("New layout profile", "Name for a copy of the " + settings.ProfileName + " layout and its app rules:");
            if (name == null || (layoutDirty && !SaveLayout(false))) return;
            string message;
            try { message = service.CreateProfile(name); }
            catch (IOException ex) { message = "Could not create the layout profile: " + ex.Message; }
            ProfileChanged(message);
        }

        // Deletes the profile in use, after switching to another one.
        private void DeleteProfile()
        {
            string old = settings.ProfileName;
            List<string> names = service.Profiles();
            string next = names.Find(delegate(string name) { return !String.Equals(name, old, StringComparison.OrdinalIgnoreCase); });
            if (next == null) { Report("DisplayWeave needs at least one layout profile."); return; }
            if (MessageBox.Show(this, "Delete layout profile " + old + " and its app rules? DisplayWeave switches to " + next + ".", "Delete layout profile",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            SwitchProfile(next);
            if (!String.Equals(settings.ProfileName, next, StringComparison.OrdinalIgnoreCase)) return;
            string message;
            try { message = service.DeleteProfile(old); }
            catch (IOException ex) { message = "Could not delete the layout profile: " + ex.Message; }
            ProfileChanged(message);
        }

        // The layout object now holds another profile: rebuild everything shown from it.
        private void ProfileChanged(string message)
        {
            rulerLayout = null;
            updating = true;
            workArea.Checked = settings.UseWorkArea;
            automaticRegions.Checked = settings.AutomaticRegions;
            updating = false;
            LoadMonitors();
            ReloadZones(0);
            RefreshRules();
            RefreshProfiles();
            Report(message);
        }

        // null when cancelled.
        private string AskText(string title, string prompt)
        {
            using (Form dialog = new Form { Text = title, Font = Font, AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                BackColor = Color.White, ClientSize = new Size(460, 150) })
            {
                var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
                foreach (int height in new[] { 30, 36, 48 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                var answer = new TextBox { Dock = DockStyle.Fill, MaxLength = 32, AccessibleName = prompt };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.AddRange(new Control[] { cancel, ok });
                body.Controls.Add(new Label { Text = prompt, Dock = DockStyle.Fill }, 0, 0);
                body.Controls.Add(answer, 0, 1);
                body.Controls.Add(buttons, 0, 2);
                dialog.Controls.Add(body);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                return dialog.ShowDialog(this) == DialogResult.OK && answer.Text.Trim().Length > 0 ? answer.Text.Trim() : null;
            }
        }
    }
}
