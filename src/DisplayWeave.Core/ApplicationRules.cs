using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
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
        public string DisplayName;
        public ApplicationAction Action;
        public string MonitorDevice;
        public string ZoneId;

        public bool Matches(ApplicationIdentity identity)
        {
            return identity != null && String.Equals(ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && String.Equals(WindowClass, identity.WindowClass, StringComparison.Ordinal);
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
            if (!File.Exists(path)) return new ApplicationRules();
            try
            {
                XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 1024 * 1024, MaxCharactersFromEntities = 0 };
                using (XmlReader reader = XmlReader.Create(path, options))
                {
                    ApplicationRules result = (ApplicationRules)new XmlSerializer(typeof(ApplicationRules)).Deserialize(reader);
                    result.Rules.RemoveAll(delegate(ApplicationRule rule) { return rule == null || String.IsNullOrWhiteSpace(rule.ExecutablePath)
                        || !Path.IsPathRooted(rule.ExecutablePath) || String.IsNullOrEmpty(rule.WindowClass)
                        || !Enum.IsDefined(typeof(ApplicationAction), rule.Action); });
                    if (result.Rules.Count > 512) result.Rules.RemoveRange(512, result.Rules.Count - 512);
                    return result;
                }
            }
            catch (Exception ex)
            {
                warning = "Saved application rules could not be read: " + ex.Message;
                return new ApplicationRules();
            }
        }

        public void Put(ApplicationRule rule)
        {
            Rules.RemoveAll(delegate(ApplicationRule old) { return String.Equals(old.ExecutablePath, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && String.Equals(old.WindowClass, rule.WindowClass, StringComparison.Ordinal); });
            Rules.Add(rule);
        }

        public void Save(string path)
        {
            string full = Path.GetFullPath(path);
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Written through to disk before it replaces the old file, so a power cut cannot leave an empty rules file.
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                        new XmlSerializer(typeof(ApplicationRules)).Serialize(writer, this);
                    stream.Flush(true);
                }
                if (File.Exists(full)) File.Replace(temporary, full, null);
                else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
