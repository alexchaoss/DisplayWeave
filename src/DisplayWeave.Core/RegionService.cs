using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;

// The self-tests in DisplayWeave.exe check internal engine helpers.
[assembly: InternalsVisibleTo("DisplayWeave")]

namespace DisplayWeave
{
    // DisplayWeave without its UI: loads and saves the layout, app rules and shortcuts, drives the engine and
    // applies rules to windows. Call Tick about every 250 ms, always from the same thread. Actions
    // return a message for the user; saving failures surface as IOException. Layout profiles and following
    // a renamed display are in RegionService.Profiles.cs.
    public sealed partial class RegionService : IDisposable
    {
        private readonly string dataDirectory;

        public RegionService(string dataDirectory, string automaticExecutableFilter = null)
        {
            this.dataDirectory = Path.GetFullPath(dataDirectory);
            LayoutPath = Path.Combine(dataDirectory, "layout.xml");
            RulesPath = Path.Combine(dataDirectory, "rules.xml");
            ShortcutsPath = Path.Combine(dataDirectory, "shortcuts.xml");
            Layout = LayoutSettings.Load(LayoutPath);
            // A new layout names no monitor; use the primary one, which always contains 0,0.
            if (String.IsNullOrEmpty(Layout.MonitorDevice))
            {
                DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                if (primary != null) Layout.MonitorDevice = primary.DeviceName;
            }
            string warning;
            Rules = ApplicationRules.Load(RulesPath, out warning);
            if (warning.Length > 0)
            {
                // The next save replaces rules.xml; keep the unreadable copy so its rules can still be recovered.
                try { File.Copy(RulesPath, RulesPath + ".unreadable.bak", true); warning += " The unreadable file was copied to rules.xml.unreadable.bak."; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string shortcutWarning;
            Shortcuts = ShortcutSettings.Load(ShortcutsPath, out shortcutWarning);
            string moved = RebindMonitor();
            LoadWarning = String.Join(" ", new List<string>(new[] { warning, shortcutWarning, moved ?? String.Empty }).FindAll(delegate(string text) { return text.Length > 0; }));
            if (LoadWarning.Length > 0) ActivityLog.Write(LoadWarning);
            Engine = new FullscreenController { AutomaticExecutableFilter = automaticExecutableFilter };
            Engine.UseDataDirectory(this.dataDirectory);
        }

        // Raised after the layout, or the layout and rules, were written.
        public event EventHandler Saved;

        public string LayoutPath { get; private set; }
        public string RulesPath { get; private set; }
        public string ShortcutsPath { get; private set; }
        public LayoutSettings Layout { get; private set; }
        public ApplicationRules Rules { get; private set; }
        public ShortcutSettings Shortcuts { get; private set; }
        public FullscreenController Engine { get; private set; }
        // What the user should know after loading: a settings file could not be read, or the layout followed its renamed display.
        public string LoadWarning { get; private set; }

        public void Tick()
        {
            Engine.Tick(WindowManager.ListWindows(), Rules, Layout);
            if (!Engine.RulesUsed) return;
            // Only the rules' last-used dates changed, so nothing else needs to know.
            Engine.RulesUsed = false;
            try { Rules.Save(RulesPath); }
            catch (Exception ex) { ActivityLog.Write("The rules' last-used dates could not be saved: " + ex.Message); }
        }

        // Returns what happened to rules whose pinned region the saved layout no longer has, or null.
        public string SaveLayout()
        {
            string unpinned = null;
            Write(delegate { StampMonitor(); Layout.Save(LayoutPath); unpinned = UnpinMissingRegions(); if (unpinned != null) Rules.Save(RulesPath); });
            return unpinned;
        }

        public void Save()
        {
            Write(delegate { StampMonitor(); Layout.Save(LayoutPath); UnpinMissingRegions(); Rules.Save(RulesPath); });
        }

        // Splitting or merging regions gives them new identities. The active profile's Force and Maximize rules pinned to a
        // region that is gone then use the region the app is in; Borderless and Centered need a region, so they wait for one.
        // Saving normalizes the layout first, so this runs after it.
        private string UnpinMissingRegions()
        {
            int unpinned = 0, waiting = 0;
            foreach (ApplicationRule rule in Rules.Rules)
            {
                if (String.IsNullOrEmpty(rule.ZoneId) || !Same(rule.ProfileName, Layout.ProfileName) ||
                    Layout.Zones.Exists(delegate(Zone zone) { return zone.Id == rule.ZoneId; })) continue;
                if (rule.Action == ApplicationAction.ForceFullscreen || rule.Action == ApplicationAction.MaximizeInRegion) { rule.ZoneId = null; unpinned++; }
                else if (rule.Action != ApplicationAction.Ignore) waiting++;
            }
            if (unpinned == 0) return null;
            string message = unpinned + (unpinned == 1 ? " app rule's region was" : " app rules' regions were") +
                " split or merged, so " + (unpinned == 1 ? "it now uses" : "they now use") + " the region the app is in." +
                (waiting > 0 ? " " + waiting + " Borderless or Centered " + (waiting == 1 ? "rule needs" : "rules need") + " a region again on App rules." : String.Empty);
            ActivityLog.Write(message);
            return message;
        }

        public void SaveShortcuts()
        {
            try { Shortcuts.Save(ShortcutsPath); }
            catch (IOException) { throw; }
            catch (Exception ex) { throw new IOException(ex.Message, ex); }
        }

        public string ApplyLayout()
        {
            string unpinned = SaveLayout();
            Engine.Resume();
            Tick();
            return "Layout saved and applied. Apps can now maximize or go fullscreen in their current region." + (unpinned == null ? String.Empty : " " + unpinned);
        }

        public string ApplySavedRules()
        {
            Save();
            Engine.Resume();
            return Engine.LastMessage;
        }

        public string Pause()
        {
            Engine.Pause();
            return Engine.LastMessage;
        }

        // Saves a rule for the window's app and applies it: Force attaches now, Maximize fits the window
        // whenever it is maximized, Borderless shows it without its title bar, Centered leaves it alone.
        public string Assign(IntPtr window, int regionIndex, ApplicationAction action)
        {
            // regionIndex -1 pins no region: the app is handled in whichever region it is currently in. A positive index
            // past the layout is still an error. Borderless and Centered always need a region.
            if (regionIndex >= Layout.Zones.Count) return "There is no region for that shortcut.";
            bool pin = regionIndex >= 0;
            if (!pin && (action == ApplicationAction.BorderlessInRegion || action == ApplicationAction.CenteredFullscreen))
                return "Choose a region for this option, or use a different option.";
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(window, out identity, out error)) return error;
            if (action == ApplicationAction.BorderlessInRegion && ShellProcessProtection.IsFolderWindow(identity.WindowClass))
                return "File Explorer folder windows can only use Maximize in region.";
            ApplicationRule rule = NewRule(identity, action);
            rule.MonitorDevice = Layout.MonitorDevice;
            rule.ZoneId = pin ? Layout.Zones[regionIndex].Id : null;
            Rules.Put(rule);
            Save();
            string name = Path.GetFileName(identity.ExecutablePath);
            string where = pin ? Layout.Zones[regionIndex].Name : "the region it is in";
            Rectangle region = Rectangle.Empty;
            List<Rectangle> regions = null;
            // Saving normalizes the layout, so a pinned region is resolved afterwards.
            if (pin && (!RegionGeometry.TryResolveRegion(rule, Layout, out region) || !RegionGeometry.TryResolveLayout(Layout, out regions)))
                return "The region changed while saving. Review the layout and assign the application again.";
            if (action == ApplicationAction.MaximizeInRegion)
            {
                // Nothing is attached; the next tick fits the window whenever it is maximized.
                Engine.Readmit(identity);
                return "Saved for " + name + ": maximize it or use its borderless fullscreen mode, and DisplayWeave fits it into " +
                    where + ". Nothing is loaded into the app.";
            }
            if (action == ApplicationAction.BorderlessInRegion)
            {
                Engine.Readmit(identity);
                return "Saved for " + name + ": set it to Windowed, and DisplayWeave shows it without its title bar in " +
                    where + ". Nothing is loaded into the app.";
            }
            if (action == ApplicationAction.CenteredFullscreen)
                return "Saved for " + name + ": DisplayWeave leaves its window alone. Run it in exclusive fullscreen at the size App rules shows.";
            // Force: a pinned region attaches now; without a pin the next tick injects it into the region it is in.
            if (!pin) { Engine.Readmit(identity); return "Saved for " + name + ": DisplayWeave loads the component and confines its fullscreen to the region it is in."; }
            Engine.Attach(identity, region, true, regions);
            return Engine.LastMessage;
        }

        public string Ignore(IntPtr window)
        {
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(window, out identity, out error)) return error;
            Engine.ReleaseProcess(identity);
            Rules.Put(NewRule(identity, ApplicationAction.Ignore));
            Save();
            return "Ignore rule saved for " + Path.GetFileName(identity.ExecutablePath) + ". Its fullscreen behavior is unchanged.";
        }

        public string Release(IntPtr window)
        {
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(window, out identity, out error)) return error;
            Engine.ReleaseProcess(identity);
            return Engine.LastMessage;
        }

        public string RemoveRule(ApplicationRule rule)
        {
            Readmit(rule);
            Rules.Rules.Remove(rule);
            Save();
            return "Saved rule removed. Its apps are now handled like any app without a rule.";
        }

        // Changes a saved rule from the app identity it already holds, so its app need not be running. regionIndex is as for Assign.
        public string ChangeRule(ApplicationRule rule, int regionIndex, ApplicationAction action)
        {
            if (regionIndex >= Layout.Zones.Count) return "There is no such region in this layout.";
            bool pin = regionIndex >= 0 && action != ApplicationAction.Ignore;
            if (!pin && (action == ApplicationAction.BorderlessInRegion || action == ApplicationAction.CenteredFullscreen))
                return "Choose a region for this option, or use a different option.";
            if (action == ApplicationAction.BorderlessInRegion && ShellProcessProtection.IsFolderWindow(rule.WindowClass))
                return "File Explorer folder windows can only use Maximize in region.";
            ApplicationRule changed = new ApplicationRule { ExecutablePath = rule.ExecutablePath, WindowClass = rule.WindowClass, AppId = rule.AppId,
                DisplayName = rule.DisplayName, Action = action, MonitorDevice = action == ApplicationAction.Ignore ? null : Layout.MonitorDevice,
                ZoneId = pin ? Layout.Zones[regionIndex].Id : null, ProfileName = action == ApplicationAction.Ignore ? null : Layout.ProfileName,
                LastMatched = rule.LastMatched };
            // An app started since the list was read is handled again under the changed rule.
            Readmit(rule);
            Rules.Rules.Remove(rule);
            Rules.Put(changed);
            Save();
            return "Saved for " + rule.DisplayName + ": " + ApplicationRule.ActionName(action) +
                (pin ? " in " + Layout.Zones[regionIndex].Name : action == ApplicationAction.Ignore ? String.Empty : " in the region it is in") +
                ". It applies the next time the app starts.";
        }

        // Released to stop what the rule did now, then readmitted: under its changed rule, or without one like any other
        // app (automatic handling included), at once instead of after handling is paused and resumed.
        private void Readmit(ApplicationRule rule)
        {
            foreach (WindowCandidate candidate in WindowManager.ListWindows())
            {
                ApplicationIdentity identity;
                string error;
                if (FullscreenController.TryInspect(candidate.Handle, out identity, out error) && rule.Matches(identity))
                { Engine.ReleaseProcess(identity); Engine.Readmit(identity); }
            }
        }

        public void Dispose()
        {
            Engine.Dispose();
        }

        // An Ignore rule applies in every layout profile; the others belong to the active one.
        private ApplicationRule NewRule(ApplicationIdentity identity, ApplicationAction action)
        {
            return new ApplicationRule { ExecutablePath = identity.ExecutablePath, WindowClass = identity.WindowClass, AppId = identity.AppId,
                DisplayName = identity.AppId != null ? StoreAppIdentity.DisplayName(identity.AppId) : Path.GetFileNameWithoutExtension(identity.ExecutablePath),
                Action = action, ProfileName = action == ApplicationAction.Ignore ? null : Layout.ProfileName };
        }

        private void Write(Action save)
        {
            try { save(); }
            catch (IOException) { throw; }
            catch (Exception ex) { throw new IOException(ex.Message, ex); }
            EventHandler saved = Saved;
            if (saved != null) saved(this, EventArgs.Empty);
        }
    }
}
