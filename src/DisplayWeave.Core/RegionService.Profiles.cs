using System;
using System.Collections.Generic;
using System.IO;

namespace DisplayWeave
{
    // Layout profiles, and the layout following its monitor when Windows renames the display.
    // The active profile is layout.xml; the others wait in profiles\profile-<name>.xml.
    public sealed partial class RegionService
    {
        public List<string> Profiles()
        {
            List<string> names = new List<string> { Layout.ProfileName };
            string directory = Path.Combine(dataDirectory, "profiles");
            if (Directory.Exists(directory))
                foreach (string file in Directory.GetFiles(directory, "profile-*.xml"))
                {
                    string name = Path.GetFileNameWithoutExtension(file).Substring("profile-".Length);
                    if (LayoutSettings.ValidProfileName(name) && !names.Exists(delegate(string n) { return Same(n, name); })) names.Add(name);
                }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        // Saves the active profile, loads the other one and applies it. Fitted windows go back to their monitor
        // first, then into the new profile's regions.
        public string SwitchProfile(string name)
        {
            if (Same(name, Layout.ProfileName)) return "Layout profile " + Layout.ProfileName + " is already in use.";
            string path = ProfilePath(name);
            if (!File.Exists(path)) return "There is no layout profile named " + name + ".";
            LayoutSettings next = LayoutSettings.Load(path);
            Write(delegate { StampMonitor(); Layout.Save(ProfilePath(Layout.ProfileName)); });
            LayoutSettings previous = new LayoutSettings();
            previous.CopyFrom(Layout);
            bool wasPaused = Engine.Paused;
            Engine.Pause();
            try
            {
                Layout.CopyFrom(next);
                Layout.ProfileName = name;
                RebindMonitor();
                SaveLayout();
            }
            catch (IOException)
            {
                // The previous profile stays in use and handling goes on as before: a failed save must not leave every app unhandled.
                Layout.CopyFrom(previous);
                try { SaveLayout(); }
                catch (IOException) { }
                if (!wasPaused) Engine.Resume();
                throw;
            }
            Engine.ForgetRegions();
            Engine.Resume();
            string message = "Switched to layout profile " + name + ".";
            ActivityLog.Write(message);
            return message;
        }

        // A copy of the active profile and its app rules, made active. Its regions keep their identifiers,
        // so the copied rules point at the same regions.
        public string CreateProfile(string name)
        {
            name = (name ?? String.Empty).Trim();
            if (!LayoutSettings.ValidProfileName(name))
                return "Use up to 32 letters, digits, spaces, hyphens or underscores, starting with a letter or digit.";
            if (Profiles().Exists(delegate(string n) { return Same(n, name); })) return "A layout profile named " + name + " already exists.";
            Write(delegate { StampMonitor(); Layout.Save(ProfilePath(Layout.ProfileName)); });
            foreach (ApplicationRule rule in Rules.Rules.ToArray())
                if (Same(rule.ProfileName, Layout.ProfileName) && Rules.Rules.Count < 512)
                    Rules.Rules.Add(new ApplicationRule { ExecutablePath = rule.ExecutablePath, WindowClass = rule.WindowClass, AppId = rule.AppId,
                        DisplayName = rule.DisplayName, Action = rule.Action, MonitorDevice = rule.MonitorDevice, ZoneId = rule.ZoneId, ProfileName = name });
            Layout.ProfileName = name;
            Save();
            return "Layout profile " + name + " created from the current one, with its app rules. Edit its regions, then Save layout.";
        }

        // Deletes a profile that is not in use, with its app rules.
        public string DeleteProfile(string name)
        {
            if (Same(name, Layout.ProfileName)) return "Switch to another layout profile before deleting " + name + ".";
            string path = ProfilePath(name);
            if (!File.Exists(path)) return "There is no layout profile named " + name + ".";
            Rules.Rules.RemoveAll(delegate(ApplicationRule rule) { return Same(rule.ProfileName, name); });
            Write(delegate { Rules.Save(RulesPath); File.Delete(path); });
            return "Layout profile " + name + " and its app rules deleted.";
        }

        // Windows can rename displays (\\.\DISPLAYn) after a restart, a driver update or a cable change. The layout
        // and the active profile's rules follow their monitor by its device path or, when the named display is gone,
        // the only display of the same size. Returns what moved, or null.
        public string RebindMonitor()
        {
            if (String.IsNullOrEmpty(Layout.MonitorId)) return null;
            List<DisplayMonitor> monitors = DisplayMonitor.All();
            DisplayMonitor named = monitors.Find(delegate(DisplayMonitor m) { return Same(m.DeviceName, Layout.MonitorDevice); });
            if (named != null && named.DeviceId == Layout.MonitorId) return null;
            DisplayMonitor match = monitors.Find(delegate(DisplayMonitor m) { return m.DeviceId.Length > 0 && m.DeviceId == Layout.MonitorId; });
            if (match == null && named == null)
            {
                List<DisplayMonitor> sameSize = monitors.FindAll(delegate(DisplayMonitor m) { return m.Bounds.Width == Layout.MonitorWidth && m.Bounds.Height == Layout.MonitorHeight; });
                if (sameSize.Count == 1) match = sameSize[0];
            }
            if (match == null || Same(match.DeviceName, Layout.MonitorDevice)) return null;
            string old = Layout.MonitorDevice;
            Layout.MonitorDevice = match.DeviceName;
            foreach (ApplicationRule rule in Rules.Rules)
                if (rule.AppliesTo(Layout.ProfileName) && Same(rule.MonitorDevice, old)) rule.MonitorDevice = match.DeviceName;
            string message = "Windows renamed the layout's display " + old + " to " + match.DeviceName + "; the layout and its app rules moved with it.";
            try { Save(); }
            catch (IOException ex) { message += " This could not be saved: " + ex.Message; }
            ActivityLog.Write(message);
            return message;
        }

        // Records which monitor the layout is for, so RebindMonitor can find it again.
        private void StampMonitor()
        {
            DisplayMonitor monitor = DisplayMonitor.Find(Layout.MonitorDevice);
            if (monitor == null) return;
            Layout.MonitorId = monitor.DeviceId;
            Layout.MonitorWidth = monitor.Bounds.Width;
            Layout.MonitorHeight = monitor.Bounds.Height;
        }

        private string ProfilePath(string name)
        {
            if (!LayoutSettings.ValidProfileName(name)) throw new ArgumentException("Invalid layout profile name.", "name");
            return Path.Combine(dataDirectory, "profiles", "profile-" + name + ".xml");
        }

        private static bool Same(string first, string second)
        {
            return String.Equals(first ?? String.Empty, second ?? String.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
