using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;

// The self-tests in DisplayWeave.exe check internal engine helpers.
[assembly: InternalsVisibleTo("DisplayWeave")]

namespace DisplayWeave
{
    // DisplayWeave without its UI: loads and saves the layout and app rules, drives the engine and
    // applies rules to windows. Call Tick about every 250 ms, always from the same thread. Actions
    // return a message for the user; saving failures surface as IOException.
    public sealed class RegionService : IDisposable
    {
        public RegionService(string dataDirectory, string automaticExecutableFilter = null)
        {
            LayoutPath = Path.Combine(dataDirectory, "layout.xml");
            RulesPath = Path.Combine(dataDirectory, "rules.xml");
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
            LoadWarning = warning;
            Engine = new FullscreenController { AutomaticExecutableFilter = automaticExecutableFilter };
        }

        // Raised after the layout, or the layout and rules, were written.
        public event EventHandler Saved;

        public string LayoutPath { get; private set; }
        public string RulesPath { get; private set; }
        public LayoutSettings Layout { get; private set; }
        public ApplicationRules Rules { get; private set; }
        public FullscreenController Engine { get; private set; }
        // Empty unless rules.xml could not be read as saved.
        public string LoadWarning { get; private set; }

        public void Tick()
        {
            Engine.Tick(WindowManager.ListWindows(), Rules, Layout);
        }

        public void SaveLayout()
        {
            Write(delegate { Layout.Save(LayoutPath); });
        }

        public void Save()
        {
            Write(delegate { Layout.Save(LayoutPath); Rules.Save(RulesPath); });
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
            if (!FullscreenController.TryResolveRegion(rule, Layout, out region) ||
                !FullscreenController.TryResolveLayout(Layout, out regions))
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

        private static ApplicationRule NewRule(ApplicationIdentity identity, ApplicationAction action)
        {
            return new ApplicationRule { ExecutablePath = identity.ExecutablePath, WindowClass = identity.WindowClass,
                DisplayName = Path.GetFileNameWithoutExtension(identity.ExecutablePath), Action = action };
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
