using System;
using System.Drawing;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The options App rules offers and what each one explains, including the Centered fullscreen setup steps.
    public sealed partial class MainForm
    {
        // The ways a rule can handle an app, in the order App rules shows them. Centered fullscreen's details
        // depend on the chosen region, so they are built when it is selected.
        private sealed class RuleOption
        {
            internal ApplicationAction Action;
            internal string Title, Summary, Details;
            internal RadioButton Choice;
        }

        private readonly RuleOption[] ruleOptions =
        {
            new RuleOption { Action = ApplicationAction.MaximizeInRegion, Title = "Maximize in region", Summary = "Nothing loaded · keeps its title bar",
                Details = "Keeps this app in the region, wherever its window is. Maximize it from its title bar, or use its borderless fullscreen mode, " +
                    "and DisplayWeave fits the window into the region from outside. A maximized window keeps its title bar. Nothing is loaded into the app.\r\n\r\n" +
                    "Works with almost every app. A game that resizes itself back to the whole monitor needs Borderless in region instead." },
            new RuleOption { Action = ApplicationAction.BorderlessInRegion, Title = "Borderless in region", Summary = "Nothing loaded · game set to Windowed",
                Details = "For games. In the game's settings, choose Windowed (not Fullscreen or Windowed fullscreen). DisplayWeave then removes the title bar " +
                    "and border from outside and sizes the window to the region, so it looks like fullscreen there. Nothing is loaded into the game.\r\n\r\n" +
                    "If the game keeps putting its title bar back, DisplayWeave stops after a few tries and says so. Pausing handling, removing the rule or " +
                    "exiting DisplayWeave gives the window its title bar and place back. Automatic updates wait while it is shown this way." },
            new RuleOption { Action = ApplicationAction.CenteredFullscreen, Title = "Centered fullscreen", Summary = "Nothing loaded · middle region only" },
            new RuleOption { Action = ApplicationAction.ForceFullscreen, Title = "Force fullscreen", Summary = "⚠ Loads a DLL into the app",
                Details = "Loads DisplayWeave's component (RegionHook.dll) into the app. The app then sees this region as its whole monitor, so its own " +
                    "fullscreen and windowed-fullscreen modes stay in the region.\r\n\r\n" +
                    "Use it only when the other options do not work and the app's terms allow it: anti-cheat software can treat any loaded DLL as tampering, " +
                    "and some apps refuse it. The component stays in the app until the app closes. The Ctrl+Alt+Shift+1…9 shortcuts save this rule too." },
            new RuleOption { Action = ApplicationAction.Ignore, Title = "Ignore this app", Summary = "Never moved or resized",
                Details = "DisplayWeave never moves or resizes this app, even when automatic handling is on. No region is needed." },
        };

        private RuleOption CheckedRuleOption()
        {
            return Array.Find(ruleOptions, delegate(RuleOption option) { return option.Choice != null && option.Choice.Checked; });
        }

        // The steps for Centered fullscreen with the chosen region. Not usable when the region is not in the middle of its display.
        private string CenteredGuide(out bool usable)
        {
            usable = false;
            int index = ruleRegion.SelectedIndex;
            if (index < 0 || index >= settings.Zones.Count) return "Choose a region to see what to set up.";
            DisplayMonitor monitor = DisplayMonitor.Find(CurrentScreen.DeviceName);
            Rectangle region = settings.Zones[index].ToPixels(Area);
            string name = settings.Zones[index].Name;
            Size mode;
            if (monitor == null || !DisplayMonitor.TryCenteredMode(region, monitor.Bounds, Area, out mode))
                return name + " is not in the middle of its display, so a game centered by the graphics driver would not line up with it. " +
                    "Choose the middle region, or use Borderless in region.";
            usable = true;
            string size = mode.Width.ToString("N0") + " × " + mode.Height.ToString("N0");
            string adapter;
            DiagnosticReport.DisplayAdapters().TryGetValue(monitor.DeviceName, out adapter);
            return "For games in exclusive fullscreen. Your graphics driver shows the game at " + size + " in the middle of the display, with black " +
                "on both sides. Nothing is loaded into the game, and DisplayWeave changes no settings: you set this up once.\r\n\r\n" +
                DriverSteps(adapter ?? String.Empty, size) + "\r\n   " +
                (monitor.OffersMode(mode) ? "Windows already offers " + size + "." : "Windows does not offer " + size + " yet.") + "\r\n" +
                "3. In the game, choose Fullscreen at " + size + ".\r\nMenu names vary between driver versions.\r\n\r\n" +
                "While the game has focus, the other regions are black, and Windows can move other windows while the display is smaller. " +
                "DisplayWeave pauses while the display is smaller and resumes by itself when it is back to " +
                monitor.Bounds.Width.ToString("N0") + " × " + monitor.Bounds.Height.ToString("N0") + ".";
        }

        private static string DriverSteps(string adapter, string size)
        {
            if (adapter.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || adapter.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. AMD Software: Settings → Display. Turn on GPU Scaling and set Scaling Mode to Center.\r\n" +
                    "2. Under Custom Resolutions, create " + size + " at your usual refresh rate.";
            if (adapter.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. NVIDIA Control Panel: Adjust desktop size and position. Choose No scaling, performed on the GPU.\r\n" +
                    "2. Change resolution → Customize → Create Custom Resolution: " + size + ".";
            if (adapter.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                return "1. Intel Graphics Command Center: Display. Set Scale to Centered.\r\n2. Add a custom resolution of " + size + ".";
            return "1. In your graphics driver's settings, turn on GPU scaling and choose centered (no scaling).\r\n" +
                "2. Add a custom resolution of " + size + ".";
        }
    }
}
