using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Asks for a new keyboard shortcut: the keys, pressed in a box, and what they do.
    internal static class ShortcutDialog
    {
        // null when cancelled.
        internal static Shortcut Ask(IWin32Window owner, Font font, int regionCount, List<string> profiles, List<Shortcut> existing)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "Add a keyboard shortcut";
                dialog.Font = font;
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = Color.White;
                dialog.ClientSize = new Size(560, 280);
                TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(16) };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                foreach (int height in new[] { 40, 30, 40, 40, 40, 50 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                dialog.Controls.Add(body);

                Shortcut result = new Shortcut();
                TextBox keys = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Text = "Press the keys here",
                    AccessibleName = "Shortcut keys. Press the keys while this box has focus." };
                Label problem = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(150, 80, 14), AutoEllipsis = true };
                ComboBox action = Combo("What it does"), region = Combo("Region"), profile = Combo("Layout profile");
                foreach (ShortcutAction value in Enum.GetValues(typeof(ShortcutAction))) action.Items.Add(Shortcut.ActionName(value));
                for (int i = 1; i <= regionCount; i++) region.Items.Add("Region " + i);
                foreach (string name in profiles) profile.Items.Add(name);
                action.SelectedIndex = 0;
                if (region.Items.Count > 0) region.SelectedIndex = 0;
                if (profile.Items.Count > 0) profile.SelectedIndex = 0;
                Button add = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

                Action validate = delegate
                {
                    result.Action = (ShortcutAction)action.SelectedIndex;
                    result.Region = region.SelectedIndex + 1;
                    result.Profile = profile.SelectedItem as string;
                    region.Enabled = Shortcut.UsesRegion(result.Action);
                    profile.Enabled = result.Action == ShortcutAction.SwitchProfile;
                    string why = result.Key == 0 ? "Hold Ctrl or Alt and press another key."
                        : existing.Exists(delegate(Shortcut s) { return s.SameKeys(result); }) ? MainForm.Describe(result) + " is already a shortcut."
                        : Shortcut.UsesRegion(result.Action) && region.SelectedIndex < 0 ? "The layout has no region."
                        : result.Action == ShortcutAction.SwitchProfile && profile.SelectedIndex < 0 ? "There is no layout profile."
                        : !result.IsValid() ? "Use Ctrl or Alt with another key." : "";
                    problem.Text = why;
                    add.Enabled = why.Length == 0;
                };
                keys.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    e.SuppressKeyPress = e.Handled = true;
                    if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
                    result.Modifiers = (e.Control ? Shortcut.Control : 0) | (e.Alt ? Shortcut.Alt : 0) | (e.Shift ? Shortcut.Shift : 0);
                    result.Key = (e.Control || e.Alt) ? (int)e.KeyCode : 0;
                    keys.Text = result.Key == 0 ? "Press the keys here" : MainForm.Describe(result);
                    validate();
                };
                // Tab, arrows and Enter would otherwise move focus or press a button instead of being captured.
                keys.PreviewKeyDown += delegate(object sender, PreviewKeyDownEventArgs e) { e.IsInputKey = true; };
                action.SelectedIndexChanged += delegate { validate(); };
                region.SelectedIndexChanged += delegate { validate(); };
                profile.SelectedIndexChanged += delegate { validate(); };

                body.Controls.Add(Label("Keys"), 0, 0); body.Controls.Add(keys, 1, 0);
                body.Controls.Add(problem, 1, 1);
                body.Controls.Add(Label("What it does"), 0, 2); body.Controls.Add(action, 1, 2);
                body.Controls.Add(Label("Region"), 0, 3); body.Controls.Add(region, 1, 3);
                body.Controls.Add(Label("Layout profile"), 0, 4); body.Controls.Add(profile, 1, 4);
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.AddRange(new Control[] { cancel, add });
                body.Controls.Add(buttons, 0, 5); body.SetColumnSpan(buttons, 2);
                dialog.CancelButton = cancel;
                dialog.ActiveControl = keys;
                validate();
                return dialog.ShowDialog(owner) == DialogResult.OK ? result : null;
            }
        }

        private static ComboBox Combo(string name)
        {
            return new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name };
        }

        private static Label Label(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }
    }
}
