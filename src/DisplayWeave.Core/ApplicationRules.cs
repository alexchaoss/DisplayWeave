using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace DisplayWeave
{
    // MaximizeInRegion and BorderlessInRegion never load anything into the app: DisplayWeave moves the
    // window from outside. CenteredFullscreen leaves the app alone; the graphics driver centers its fullscreen.
    public enum ApplicationAction { ForceFullscreen, Ignore, MaximizeInRegion, BorderlessInRegion, CenteredFullscreen }

    [Serializable]
    public sealed class ApplicationRule
    {
        public string ExecutablePath;
        public string WindowClass;
        // A Store app's AppUserModelID: every Store app's window belongs to the same host program and class.
        public string AppId;
        public string DisplayName;
        public ApplicationAction Action;
        public string MonitorDevice;
        public string ZoneId;
        // The layout profile the rule belongs to; empty applies in every profile.
        public string ProfileName;
        // The first time on the latest day an app matched the rule, in UTC; DateTime.MinValue when never.
        public DateTime LastMatched;

        public bool Matches(ApplicationIdentity identity)
        {
            return identity != null && SameApp(ExecutablePath, WindowClass, AppId, identity.ExecutablePath, identity.WindowClass, identity.AppId);
        }

        internal bool AppliesTo(string profileName)
        {
            return String.IsNullOrEmpty(ProfileName) || String.Equals(ProfileName, profileName ?? String.Empty, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool SameApp(string path, string windowClass, string appId, string otherPath, string otherClass, string otherAppId)
        {
            return String.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(StableClass(windowClass), StableClass(otherClass), StringComparison.Ordinal) &&
                String.Equals(appId ?? String.Empty, otherAppId ?? String.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // Some frameworks put a per-run part in their window class: a GUID (WPF's HwndWrapper[app;;GUID]), the
        // WinForms hash suffix, or MFC and ATL addresses. That part is replaced, so a rule outlives a restart.
        public static string StableClass(string windowClass)
        {
            if (String.IsNullOrEmpty(windowClass)) return windowClass ?? String.Empty;
            string stable = Regex.Replace(windowClass, @"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?", "*");
            stable = Regex.Replace(stable, @"^(WindowsForms10\..+\.app\.0)\.[^.]+$", "$1.*");
            return Regex.Replace(stable, @"^(Afx|ATL):[0-9A-Fa-f:]+$", "$1:*");
        }

        public override string ToString()
        {
            return DisplayName + "  ·  " + ActionName(Action) + "  ·  " + WindowClass;
        }

        public static string ActionName(ApplicationAction action)
        {
            switch (action)
            {
                case ApplicationAction.Ignore: return "Ignore";
                case ApplicationAction.MaximizeInRegion: return "Maximize in region";
                case ApplicationAction.BorderlessInRegion: return "Borderless in region";
                case ApplicationAction.CenteredFullscreen: return "Centered fullscreen";
                default: return "Force fullscreen";
            }
        }
    }

    [Serializable, XmlRoot("ApplicationRules")]
    public sealed class ApplicationRules
    {
        [XmlIgnore] public List<ApplicationRule> Rules = new List<ApplicationRule>();
        [XmlArray("Rules"), XmlArrayItem("Rule")]
        public ApplicationRule[] SerializedRules
        {
            get { return Rules.ToArray(); }
            set { Rules = value == null ? new List<ApplicationRule>() : new List<ApplicationRule>(value); }
        }

        public static ApplicationRules Load(string path, out string warning)
        {
            warning = String.Empty;
            try
            {
                ApplicationRules result = XmlFile.Read<ApplicationRules>(path);
                if (result == null) return new ApplicationRules();
                result.Rules.RemoveAll(delegate(ApplicationRule rule) { return rule == null || String.IsNullOrWhiteSpace(rule.ExecutablePath)
                    || !Path.IsPathRooted(rule.ExecutablePath) || String.IsNullOrEmpty(rule.WindowClass)
                    || !Enum.IsDefined(typeof(ApplicationAction), rule.Action); });
                if (result.Rules.Count > 512) result.Rules.RemoveRange(512, result.Rules.Count - 512);
                return result;
            }
            catch (Exception ex)
            {
                warning = "Saved application rules could not be read: " + ex.Message;
                return new ApplicationRules();
            }
        }

        // One rule per app and profile. An Ignore rule applies in every profile, so it replaces all of the app's
        // rules, and any other rule replaces it.
        public void Put(ApplicationRule rule)
        {
            Rules.RemoveAll(delegate(ApplicationRule old)
            {
                return ApplicationRule.SameApp(old.ExecutablePath, old.WindowClass, old.AppId, rule.ExecutablePath, rule.WindowClass, rule.AppId) &&
                    (rule.Action == ApplicationAction.Ignore || old.Action == ApplicationAction.Ignore ||
                    String.Equals(old.ProfileName ?? String.Empty, rule.ProfileName ?? String.Empty, StringComparison.OrdinalIgnoreCase));
            });
            Rules.Add(rule);
        }

        public void Save(string path)
        {
            XmlFile.Write(path, this);
        }
    }
}
