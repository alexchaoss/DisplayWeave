using System;
using System.Collections.Generic;
using System.IO;

namespace DisplayWeave
{
    // What the engine keeps on disk between runs: the region each app was last fitted into, and the windows
    // it shows without their title bar, so a run that stopped unexpectedly can be recovered.
    public sealed partial class FullscreenController
    {
        private AppRegionMemory memory = new AppRegionMemory();
        private string journalPath, journalText;

        // Without a data directory, both stay in memory only, as in the self-tests.
        internal void UseDataDirectory(string directory)
        {
            memory = AppRegionMemory.Load(Path.Combine(directory, "app-regions.xml"));
            journalPath = Path.Combine(directory, "borderless.journal");
            // A previous run that stopped without restoring its borderless windows left them listed. They are taken
            // over, so their rule keeps them as they are or, without one, their title bar comes back.
            foreach (BorderlessJournal entry in BorderlessJournal.Read(journalPath))
            {
                ApplicationIdentity current;
                string error;
                if (!TryInspect(entry.Handle, out current, out error) || !entry.Matches(current) || maximized.ContainsKey(entry.Handle)) continue;
                maximized.Add(entry.Handle, new MaximizedFit { Target = current, Styled = true, RemovedStyle = entry.RemovedStyle,
                    RemovedExStyle = entry.RemovedExStyle, OriginalBounds = entry.OriginalBounds });
                ActivityLog.Write("Took over the " + Path.GetFileName(current.ExecutablePath) + " window the last run showed without its title bar.");
            }
            SaveJournal();
        }

        // Rewrites the journal when the borderless windows or what was removed from them changed.
        private void SaveJournal()
        {
            if (journalPath == null) return;
            List<BorderlessJournal> entries = new List<BorderlessJournal>();
            foreach (MaximizedFit fit in maximized.Values)
                if (fit.Styled)
                    entries.Add(new BorderlessJournal { Handle = fit.Target.Handle, ProcessId = fit.Target.ProcessId, CreationTime = fit.Target.CreationTime,
                        RemovedStyle = fit.RemovedStyle, RemovedExStyle = fit.RemovedExStyle, OriginalBounds = fit.OriginalBounds,
                        ExecutablePath = fit.Target.ExecutablePath });
            string text = BorderlessJournal.Format(entries);
            if (text == journalText) return;
            journalText = text;
            BorderlessJournal.Write(journalPath, text);
        }

        // An app that starts maximized or in borderless fullscreen goes back to the region it was last fitted into.
        // Windows already open when DisplayWeave starts stay where the user left them.
        private void PreferRememberedRegion(ApplicationIdentity identity, LayoutSettings layout)
        {
            if (!startupTickDone) return;
            string zoneId = memory.Find(AppRegionMemory.Key(identity, layout.ProfileName));
            if (zoneId != null && layout.Zones.Exists(delegate(Zone zone) { return zone.Id == zoneId; })) automaticPolicy.Prefer(identity.Key, zoneId);
        }

        private void RememberRegion(ApplicationIdentity identity, LayoutSettings layout, string zoneId)
        {
            if (zoneId != null) memory.Remember(AppRegionMemory.Key(identity, layout.ProfileName), zoneId);
        }
    }
}
