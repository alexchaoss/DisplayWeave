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

        public void SaveLayout()
        {
            Write(delegate { StampMonitor(); Layout.Save(LayoutPath); });
        }

        public void Save()
        {
            Write(delegate { StampMonitor(); Layout.Save(LayoutPath); Rules.Save(RulesPath); });
        }

        public void SaveShortcuts()
        {
            try { Shortcuts.Save(ShortcutsPath); }
            catch (IOException) { throw; }
            catch (Exception ex) { throw new IOException(ex.Message, ex); }
        }

        public string ApplyLayout()
        {
            SaveLayout();
            Engine.Resume();
            Tick();
            return "Layout saved and applied. Apps can now maximize or go fullscreen in their current region.";
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
            if (regionIndex < 0 || regionIndex >= Layout.Zones.Count) return "There is no region for that shortcut.";
            ApplicationIdentity identity;
            string error;
            if (!FullscreenController.TryInspect(window, out identity, out error)) return error;
            if (action == ApplicationAction.BorderlessInRegion && ShellProcessProtection.IsFolderWindow(identity.WindowClass))
                return "File Explorer folder windows can only use Maximize in region.";
            ApplicationRule rule = NewRule(identity, action);
            rule.MonitorDevice = Layout.MonitorDevice;
            rule.ZoneId = Layout.Zones[regionIndex].Id;
            Rules.Put(rule);
            Save();
            Rectangle region;
            List<Rectangle> regions;
            // Saving normalizes the layout, so the region is resolved afterwards.
            if (!RegionGeometry.TryResolveRegion(rule, Layout, out region) ||
                !RegionGeometry.TryResolveLayout(Layout, out regions))
                return "The region changed while saving. Review the layout and assign the application again.";
            string name = Path.GetFileName(identity.ExecutablePath);
            if (action == ApplicationAction.MaximizeInRegion)
            {
                // Nothing is attached; the next tick fits the window whenever it is maximized.
                Engine.Readmit(identity);
                return "Saved for " + name + ": maximize it or use its borderless fullscreen mode, and DisplayWeave fits it into " +
                    Layout.Zones[regionIndex].Name + ". Nothing is loaded into the app.";
            }
            if (action == ApplicationAction.BorderlessInRegion)
            {
                Engine.Readmit(identity);
                return "Saved for " + name + ": set it to Windowed, and DisplayWeave shows it without its title bar in " +
                    Layout.Zones[regionIndex].Name + ". Nothing is loaded into the app.";
            }
            if (action == ApplicationAction.CenteredFullscreen)
                return "Saved for " + name + ": DisplayWeave leaves its window alone. Run it in exclusive fullscreen at the size App rules shows.";
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
            foreach (WindowCandidate candidate in WindowManager.ListWindows())
            {
                ApplicationIdentity identity;
                string error;
                if (FullscreenController.TryInspect(candidate.Handle, out identity, out error) && rule.Matches(identity))
                    Engine.ReleaseProcess(identity);
            }
            Rules.Rules.Remove(rule);
            Save();
            return "Saved rule removed and matching applications released.";
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
