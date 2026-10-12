using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace DisplayWeave
{
    // The region actions save a rule for the active app, as App rules does.
    public enum ShortcutAction { FitInRegion, BorderlessInRegion, ForceFullscreen, ReleaseActiveApp, PauseOrResume, SwitchProfile }

    // A keyboard shortcut the user made. DisplayWeave has none of its own.
    [Serializable]
    public sealed class Shortcut
    {
        public const int Alt = 1, Control = 2, Shift = 4, Windows = 8;

        // RegisterHotKey's modifiers and virtual-key code.
        public int Modifiers;
        public int Key;
        public ShortcutAction Action;
        // The region's number (1 for the first) for the region actions; the profile's name for SwitchProfile.
        public int Region;
        public string Profile;

        public static bool UsesRegion(ShortcutAction action)
        {
            return action == ShortcutAction.FitInRegion || action == ShortcutAction.BorderlessInRegion || action == ShortcutAction.ForceFullscreen;
        }

        // Shift alone would take a key from ordinary typing, so Ctrl, Alt or Windows is required.
        public bool IsValid()
        {
            return (Modifiers & ~(Alt | Control | Shift | Windows)) == 0 && (Modifiers & (Alt | Control | Windows)) != 0 &&
                Key > 0 && Key < 255 && Enum.IsDefined(typeof(ShortcutAction), Action) &&
                (!UsesRegion(Action) || (Region >= 1 && Region <= 9)) &&
                (Action != ShortcutAction.SwitchProfile || LayoutSettings.ValidProfileName(Profile));
        }

        public bool SameKeys(Shortcut other)
        {
            return other != null && other.Modifiers == Modifiers && other.Key == Key;
        }

        public static string ActionName(ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.FitInRegion: return "Fit the active app into a region";
                case ShortcutAction.BorderlessInRegion: return "Show the active app borderless in a region";
                case ShortcutAction.ForceFullscreen: return "Force the active app's fullscreen into a region";
                case ShortcutAction.ReleaseActiveApp: return "Release the active app";
                case ShortcutAction.PauseOrResume: return "Pause or resume region handling";
                default: return "Switch to a layout profile";
            }
        }
    }

    [Serializable, XmlRoot("Shortcuts")]
    public sealed class ShortcutSettings
    {
        public const int MaximumShortcuts = 32;
        [XmlElement("Shortcut")] public List<Shortcut> Items = new List<Shortcut>();

        // Invalid entries and later duplicates of the same keys are dropped.
        public static ShortcutSettings Load(string path, out string warning)
        {
            warning = String.Empty;
            ShortcutSettings settings;
            try { settings = XmlFile.Read<ShortcutSettings>(path) ?? new ShortcutSettings(); }
            catch (Exception ex)
            {
                warning = "Saved keyboard shortcuts could not be read: " + ex.Message;
                return new ShortcutSettings();
            }
            List<Shortcut> valid = new List<Shortcut>();
            foreach (Shortcut shortcut in settings.Items)
                if (shortcut != null && shortcut.IsValid() && valid.Count < MaximumShortcuts && !valid.Exists(delegate(Shortcut s) { return s.SameKeys(shortcut); }))
                    valid.Add(shortcut);
            settings.Items = valid;
            return settings;
        }

        public void Save(string path)
        {
            XmlFile.Write(path, this);
        }
    }
}
