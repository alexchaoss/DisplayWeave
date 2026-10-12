using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The Shortcuts tab: keyboard shortcuts the user makes, registered with Windows while DisplayWeave runs.
    public sealed partial class MainForm
    {
        private const int FirstHotkeyId = 800;
        private readonly ListView shortcutList = new ListView();
        private readonly HashSet<int> unavailableShortcuts = new HashSet<int>();

        private void BuildShortcutsPage(TabPage page)
        {
            var body = Table(3, 4);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            page.Controls.Add(body);
            AddWide(body, Introduction("Keyboard shortcuts", "Make shortcuts for the app in front of you. They work in any app while DisplayWeave runs;\n" +
                "DisplayWeave has none of its own. A region shortcut saves a rule for the app, as App rules does."), 0);
            shortcutList.View = View.Details; shortcutList.FullRowSelect = true; shortcutList.HideSelection = false; shortcutList.MultiSelect = false;
            shortcutList.HeaderStyle = ColumnHeaderStyle.Nonclickable; shortcutList.Dock = DockStyle.Fill;
            shortcutList.AccessibleName = "Keyboard shortcuts";
            shortcutList.Columns.Add("Keys", 200);
            shortcutList.Columns.Add("Does", 420);
            shortcutList.Columns.Add("Status", 220);
            AddWide(body, shortcutList, 1);
            var remove = Button("Remove shortcut", RemoveShortcut);
            remove.Enabled = false;
            remove.Margin = new Padding(3, 8, 3, 6);
            shortcutList.SelectedIndexChanged += delegate { remove.Enabled = shortcutList.SelectedItems.Count > 0; };
            var add = Button("Add shortcut…", AddShortcut);
            add.Margin = new Padding(3, 8, 3, 6);
            body.Controls.Add(add, 1, 2);
            body.Controls.Add(remove, 2, 2);
            AddWide(body, Caption("Tip: hold Shift while you maximize a window to leave it filling its display this once."), 3);
        }

        // Registers every shortcut again; one another app already uses is listed as unavailable.
        private void RegisterShortcuts()
        {
            if (!IsHandleCreated) return;
            foreach (int id in registeredHotkeys) UnregisterHotKey(Handle, id);
            registeredHotkeys.Clear();
            unavailableShortcuts.Clear();
            List<Shortcut> items = service.Shortcuts.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (RegisterHotKey(Handle, FirstHotkeyId + i, (uint)items[i].Modifiers | 0x4000, (uint)items[i].Key)) registeredHotkeys.Add(FirstHotkeyId + i); // MOD_NOREPEAT
                else unavailableShortcuts.Add(i);
            }
            hotkeyNotice = unavailableShortcuts.Count == 0 ? "" : "  ·  " + unavailableShortcuts.Count + " shortcuts unavailable (already in use by another app).";
            shortcutList.BeginUpdate();
            shortcutList.Items.Clear();
            for (int i = 0; i < items.Count; i++)
                shortcutList.Items.Add(new ListViewItem(new[] { Describe(items[i]), Explain(items[i]),
                    unavailableShortcuts.Contains(i) ? "In use by another app" : "Ready" }) { Tag = items[i] });
            shortcutList.EndUpdate();
        }

        private void RunShortcut(int index)
        {
            if (index < 0 || index >= service.Shortcuts.Items.Count) return;
            Shortcut shortcut = service.Shortcuts.Items[index];
            IntPtr active = WindowManager.ForegroundWindow;
            if (Shortcut.UsesRegion(shortcut.Action))
            {
                ApplicationIdentity identity;
                string error;
                if (!FullscreenController.TryInspect(active, out identity, out error)) { Report(error); return; }
                if (shortcut.Region > settings.Zones.Count) { Report("The layout has no Region " + shortcut.Region + " for that shortcut."); return; }
                ApplicationAction action = shortcut.Action == ShortcutAction.FitInRegion ? ApplicationAction.MaximizeInRegion
                    : shortcut.Action == ShortcutAction.BorderlessInRegion ? ApplicationAction.BorderlessInRegion : ApplicationAction.ForceFullscreen;
                Assign(active, shortcut.Region - 1, action);
                // The rule fits the window whenever it is maximized, so the shortcut maximizes it.
                if (action == ApplicationAction.MaximizeInRegion && !IsZoomed(active)) ShowWindowAsync(active, 3); // SW_MAXIMIZE
            }
            else if (shortcut.Action == ShortcutAction.ReleaseActiveApp) ReleaseWindow(active);
            else if (shortcut.Action == ShortcutAction.PauseOrResume) { if (fullscreen.Paused) ResumeHandling(); else RestoreAll(); }
            else SwitchProfile(shortcut.Profile);
        }

        private void AddShortcut()
        {
            if (service.Shortcuts.Items.Count >= ShortcutSettings.MaximumShortcuts) { Report("DisplayWeave keeps up to " + ShortcutSettings.MaximumShortcuts + " shortcuts."); return; }
            Shortcut shortcut = ShortcutDialog.Ask(this, Font, settings.Zones.Count, service.Profiles(), service.Shortcuts.Items);
            if (shortcut == null) return;
            service.Shortcuts.Items.Add(shortcut);
            SaveShortcuts("Shortcut " + Describe(shortcut) + " added: " + Explain(shortcut) + ".");
        }

        private void RemoveShortcut()
        {
            if (shortcutList.SelectedItems.Count == 0) return;
            Shortcut shortcut = (Shortcut)shortcutList.SelectedItems[0].Tag;
            service.Shortcuts.Items.Remove(shortcut);
            SaveShortcuts("Shortcut " + Describe(shortcut) + " removed.");
        }

        private void SaveShortcuts(string message)
        {
            try { service.SaveShortcuts(); }
            catch (IOException ex) { message = "The shortcut works until DisplayWeave exits, but could not be saved: " + ex.Message; }
            RegisterShortcuts();
            Report(message + (hotkeyNotice.Length > 0 ? hotkeyNotice : ""));
        }

        internal static string Describe(Shortcut shortcut)
        {
            List<string> parts = new List<string>();
            if ((shortcut.Modifiers & Shortcut.Control) != 0) parts.Add("Ctrl");
            if ((shortcut.Modifiers & Shortcut.Alt) != 0) parts.Add("Alt");
            if ((shortcut.Modifiers & Shortcut.Shift) != 0) parts.Add("Shift");
            if ((shortcut.Modifiers & Shortcut.Windows) != 0) parts.Add("Win");
            Keys key = (Keys)shortcut.Key;
            parts.Add(key >= Keys.D0 && key <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : new KeysConverter().ConvertToString(key));
            return String.Join(" + ", parts);
        }

        private static string Explain(Shortcut shortcut)
        {
            switch (shortcut.Action)
            {
                case ShortcutAction.FitInRegion: return "Fit the active app into Region " + shortcut.Region + " (Maximize in region)";
                case ShortcutAction.BorderlessInRegion: return "Show the active app borderless in Region " + shortcut.Region;
                case ShortcutAction.ForceFullscreen: return "Force the active app's fullscreen into Region " + shortcut.Region;
                case ShortcutAction.SwitchProfile: return "Switch to layout profile " + shortcut.Profile;
                default: return Shortcut.ActionName(shortcut.Action);
            }
        }

        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    }
}
