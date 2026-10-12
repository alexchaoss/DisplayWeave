using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DisplayWeave
{
    // A Markdown summary for GitHub issues: versions, displays, scaling, graphics drivers, the layout, handling
    // state and the end of the activity log. It never holds window titles, paths, user or computer names, SIDs or
    // monitor serials; app file names appear only when the user ticks the box.
    internal static class DiagnosticReport
    {
        private const string NewIssue = "https://github.com/alexchaoss/DisplayWeave/issues/new?template=bug_report.yml";

        internal static string Build(string handling, LayoutSettings layout, IList<HandledApp> apps, IEnumerable<string> notes, bool includeAppNames)
        {
            List<string> appPaths = new List<string>();
            foreach (HandledApp app in apps) appPaths.Add(app.Target.ExecutablePath ?? "");
            Dictionary<string, string> adapters = DisplayAdapters();
            StringBuilder text = new StringBuilder();
            text.Append("### DisplayWeave diagnostic report\n\n| | |\n| --- | --- |\n");
            Row(text, "DisplayWeave", Program.Version + (Updater.InstallRoot(Application.ExecutablePath) != null ? " · installed" : " · portable or development copy") +
                (ElevationRestart.IsElevated() ? " · administrator mode" : ""));
            Row(text, "Windows", WindowsVersion());
            Row(text, "Handling", handling);
            DisplayMonitor display = DisplayMonitor.Find(layout.MonitorDevice ?? "");
            List<string> regions = new List<string>();
            if (display != null)
                foreach (Zone zone in layout.Zones)
                {
                    Rectangle region = zone.ToPixels(layout.UseWorkArea ? display.WorkingArea : display.Bounds);
                    regions.Add(region.Width + " × " + region.Height);
                }
            Row(text, "Layout", (display == null ? "display " + Code(layout.MonitorDevice) + " not found" : Code(layout.MonitorDevice)) +
                (layout.UseWorkArea ? " · work area" : " · full display") + " · automatic regions " + (layout.AutomaticRegions ? "on" : "off") +
                " · " + layout.Zones.Count + " regions" + (regions.Count > 0 ? ": " + String.Join(", ", regions) : "") + " · " + layout.GridStepPixels + " px grid");

            text.Append("\n#### Displays\n\n| Display | Primary | Bounds | Work area | Scaling | Adapter |\n| --- | --- | --- | --- | --- | --- |\n");
            foreach (DisplayMonitor monitor in DisplayMonitor.All())
            {
                string adapter;
                if (!adapters.TryGetValue(monitor.DeviceName, out adapter)) adapter = "";
                // Windows names displays \\.\DISPLAYn; another display or region tool can rename them.
                string name = Code(monitor.DeviceName) + (Regex.IsMatch(monitor.DeviceName, @"^\\\\\.\\DISPLAY\d+$") ? "" : " (renamed by another tool)");
                text.Append("| ").Append(name).Append(" | ").Append(monitor.Bounds.Location == Point.Empty ? "yes" : "no")
                    .Append(" | ").Append(Area(monitor.Bounds)).Append(" | ").Append(Area(monitor.WorkingArea))
                    .Append(" | ").Append(monitor.ScalePercent).Append("% | ").Append(Cell(adapter)).Append(" |\n");
            }

            text.Append("\n#### Graphics drivers\n\n");
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
                using (ManagementObjectCollection controllers = searcher.Get())
                    foreach (ManagementBaseObject controller in controllers)
                        using (controller)
                            text.Append("- ").Append(Cell(Convert.ToString(controller["Name"]))).Append(" · driver ").Append(Cell(Convert.ToString(controller["DriverVersion"]))).Append('\n');
            }
            catch (ManagementException ex) { text.Append("- Windows did not list them: ").Append(Cell(ex.Message)).Append('\n'); }
            catch (COMException ex) { text.Append("- Windows did not list them: ").Append(Cell(ex.Message)).Append('\n'); }

            text.Append("\n#### Handled apps\n\n");
            if (apps.Count == 0) text.Append("None.\n");
            else
            {
                text.Append("| App | Architecture | Status | Problem |\n| --- | --- | --- | --- |\n");
                for (int i = 0; i < apps.Count; i++)
                    text.Append("| ").Append(Cell(includeAppNames ? Path.GetFileName(appPaths[i]) : "App " + (i + 1))).Append(" | ")
                        .Append(apps[i].Target.Is32Bit ? "x86" : "x64").Append(" | ").Append(apps[i].State).Append(" | ")
                        .Append(Cell(Scrub(apps[i].Error, appPaths, includeAppNames))).Append(" |\n");
            }

            List<string> messages = new List<string>();
            foreach (string note in notes)
                if (!String.IsNullOrWhiteSpace(note) && !messages.Contains(note.Trim())) messages.Add(note.Trim());
            if (messages.Count > 0)
                text.Append("\n#### Recent messages\n\n```text\n").Append(Scrub(String.Join("\n\n", messages), appPaths, includeAppNames).Replace("```", "'''")).Append("\n```\n");
            List<string> activity = ActivityLog.Tail(40);
            if (activity.Count > 0)
                text.Append("\n#### Recent activity\n\n```text\n").Append(Scrub(String.Join("\n", activity), appPaths, includeAppNames).Replace("```", "'''")).Append("\n```\n");
            return text.ToString();
        }

        // Last pass over all free text. Known app paths go first so their names follow the user's choice.
        internal static string Scrub(string text, IList<string> appPaths, bool includeAppNames)
        {
            if (String.IsNullOrEmpty(text)) return text ?? "";
            for (int i = 0; i < appPaths.Count; i++)
            {
                if (appPaths[i].Length == 0) continue;
                string label = includeAppNames ? Path.GetFileName(appPaths[i]) : "App " + (i + 1);
                text = ReplaceText(text, Regex.Escape(appPaths[i]), label);
                if (!includeAppNames) text = ReplaceText(text, @"(?<![\w.-])" + Regex.Escape(Path.GetFileName(appPaths[i])) + @"(?![\w.-])", label);
            }
            text = ReplaceText(text, Regex.Escape(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "%USERPROFILE%");
            foreach (string name in new[] { Environment.UserName, Environment.MachineName, Environment.UserDomainName })
                if (name != null && name.Length > 2) text = ReplaceText(text, @"\b" + Regex.Escape(name) + @"\b", "<redacted>");
            text = Regex.Replace(text, @"S-1-5-21(-\d+){1,4}", "<SID>");
            // Paths outside the profile: up to .exe or .dll when they have one, so folder names with spaces go too.
            text = ReplaceText(text, @"[A-Za-z]:\\[^""'\r\n<>|]*?\.(?:exe|dll)\b", "<path>");
            text = ReplaceText(text, @"[A-Za-z]:\\[^\s""'<>|]*", "<path>");
            // The activity log names apps that are no longer handled, so no known path covers them.
            return includeAppNames ? text : ReplaceText(text, @"(?<![\w.-])[\w.-]+\.exe\b", "<app>");
        }

        internal static void ShowDialog(IWin32Window owner, Font font, Func<bool, string> build)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "DisplayWeave diagnostic report";
                dialog.Font = font;
                dialog.AutoScaleMode = AutoScaleMode.Dpi;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(820, 560);
                dialog.MinimumSize = new Size(560, 360);
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = Color.White;
                Label intro = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12, 10, 12, 0), ForeColor = Color.FromArgb(75, 89, 110),
                    Text = "Review this report, copy it, and paste it into a new GitHub issue. It has no window titles, paths, or user or computer names; app file names appear only if you include them." };
                TextBox preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                    BackColor = Color.White, Font = new Font("Consolas", 9.5F), AccessibleName = "Diagnostic report preview" };
                CheckBox names = new CheckBox { Text = "Include app file names", AutoSize = true, Margin = new Padding(12, 12, 12, 0) };
                Button copy = new Button { Text = "Copy to clipboard", AutoSize = true, Margin = new Padding(6, 8, 6, 8) };
                Button issue = new Button { Text = "Open a new issue", AutoSize = true, Margin = new Padding(6, 8, 6, 8) };
                Button close = new Button { Text = "Close", AutoSize = true, Margin = new Padding(6, 8, 12, 8), DialogResult = DialogResult.Cancel };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                buttons.Controls.AddRange(new Control[] { close, issue, copy, names });
                Action refresh = delegate { preview.Text = build(names.Checked).Replace("\n", "\r\n"); copy.Text = "Copy to clipboard"; };
                names.CheckedChanged += delegate { refresh(); };
                copy.Click += delegate
                {
                    try { Clipboard.SetText(preview.Text); copy.Text = "Copied"; }
                    catch (ExternalException) { copy.Text = "Clipboard busy, try again"; }
                };
                issue.Click += delegate
                {
                    try { using (Process.Start(NewIssue)) { } }
                    catch (System.ComponentModel.Win32Exception) { issue.Text = "No browser opened"; }
                };
                dialog.Controls.Add(preview);
                dialog.Controls.Add(buttons);
                dialog.Controls.Add(intro);
                dialog.CancelButton = close;
                dialog.ActiveControl = copy; // a focused preview would start with all its text selected
                refresh();
                dialog.ShowDialog(owner);
            }
        }

        private static void Row(StringBuilder text, string name, string value)
        {
            text.Append("| ").Append(name).Append(" | ").Append(Cell(value)).Append(" |\n");
        }

        private static string Cell(string value)
        {
            return (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // Device names such as \\.\DISPLAY1 keep their backslashes only inside a code span.
        private static string Code(string value)
        {
            return "`" + Cell(value).Replace("`", "'") + "`";
        }

        private static string Area(Rectangle area)
        {
            return area.Width + " × " + area.Height + " at " + area.X + ", " + area.Y;
        }

        private static string ReplaceText(string text, string pattern, string replacement)
        {
            return Regex.Replace(text, pattern, delegate(Match match) { return replacement; }, RegexOptions.IgnoreCase);
        }

        private static string WindowsVersion()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null) return Environment.OSVersion.VersionString;
                string build = Convert.ToString(key.GetValue("CurrentBuild"));
                int number;
                Int32.TryParse(build, out number);
                // ProductName still says Windows 10 on Windows 11; the build number tells them apart.
                return (number >= 22000 ? "Windows 11 " : "Windows 10 ") + key.GetValue("DisplayVersion") + " · build " + build + "." + key.GetValue("UBR") + " · " + key.GetValue("EditionID");
            }
        }

        // Display device name (\\.\DISPLAY1) to the adapter that drives it.
        internal static Dictionary<string, string> DisplayAdapters()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DisplayDevice device = new DisplayDevice();
            device.Size = Marshal.SizeOf(typeof(DisplayDevice));
            for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
            {
                result[device.DeviceName] = device.DeviceString;
                device.Size = Marshal.SizeOf(typeof(DisplayDevice));
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    }
}
