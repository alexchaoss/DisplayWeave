using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace DisplayWeave
{
    // The region each app was last fitted into by automatic handling, per layout profile, so an app that starts
    // maximized or in borderless fullscreen returns to it. Saved in app-regions.xml; the oldest of 256 apps goes first.
    [Serializable, XmlRoot("AppRegions")]
    public sealed class AppRegionMemory
    {
        private const int MaximumApps = 256;

        [Serializable]
        public sealed class Entry
        {
            [XmlAttribute] public string App;
            [XmlAttribute] public string Zone;
        }

        [XmlElement("App")] public List<Entry> Entries = new List<Entry>();
        [XmlIgnore] internal string Path;

        internal static AppRegionMemory Load(string path)
        {
            AppRegionMemory memory;
            try { memory = XmlFile.Read<AppRegionMemory>(path) ?? new AppRegionMemory(); }
            catch (Exception ex)
            {
                ActivityLog.Write("The remembered app regions could not be read and start empty: " + ex.Message);
                memory = new AppRegionMemory();
            }
            memory.Entries.RemoveAll(delegate(Entry entry) { return entry == null || String.IsNullOrEmpty(entry.App) || String.IsNullOrEmpty(entry.Zone); });
            memory.Path = path;
            return memory;
        }

        internal static string Key(ApplicationIdentity identity, string profileName)
        {
            return (identity.ExecutablePath ?? String.Empty).ToUpperInvariant() + "|" + ApplicationRule.StableClass(identity.WindowClass) + "|" +
                (identity.AppId ?? String.Empty).ToUpperInvariant() + "|" + (profileName ?? String.Empty).ToUpperInvariant();
        }

        internal string Find(string key)
        {
            Entry entry = Entries.Find(delegate(Entry e) { return e.App == key; });
            return entry == null ? null : entry.Zone;
        }

        // Saves only when the region changed; a failed save keeps the memory for this session.
        internal void Remember(string key, string zoneId)
        {
            int index = Entries.FindIndex(delegate(Entry e) { return e.App == key; });
            if (index >= 0 && Entries[index].Zone == zoneId) return;
            if (index >= 0) Entries.RemoveAt(index);
            Entries.Add(new Entry { App = key, Zone = zoneId });
            if (Entries.Count > MaximumApps) Entries.RemoveRange(0, Entries.Count - MaximumApps);
            if (Path == null) return;
            try { XmlFile.Write(Path, this); }
            catch (Exception ex) { ActivityLog.Write("The remembered app regions could not be saved: " + ex.Message); }
        }
    }
}
