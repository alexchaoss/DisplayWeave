using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave
{
    public static partial class RuleTests
    {
        // The handled-app list, isolating one app's error, and RegionService without a UI.
        private static void HandlingTests(Action<string, Action> test, string temporary)
        {
            test("The handled-app list shows why an attach failed and a rule whose region is gone", delegate
            {
                // An impossible PID and fake HWND keep this test independent of user applications.
                ApplicationIdentity app = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Game.exe", WindowClass = "GameWindow",
                    ProcessId = UInt32.MaxValue - 1, CreationTime = 1, Handle = new IntPtr(1) };
                string helpers = FullscreenController.HelperDirectory;
                FullscreenController.HelperDirectory = temporary; // holds no helper
                try
                {
                    using (FullscreenController controller = new FullscreenController())
                    {
                        Assert(controller.HandledApps().Count == 0, "An idle engine listed an app.");
                        Assert(!controller.Attach(app, new Rectangle(0, 0, 800, 600), true), "Attached without a helper.");
                        List<HandledApp> apps = controller.HandledApps();
                        Assert(apps.Count == 1 && apps[0].Target == app && apps[0].State == HandledAppState.Error &&
                            apps[0].Error.Contains("Game.exe (PID") && apps[0].Error.Contains("helper is missing"), "The failed attach was not listed with its reason.");
                    }
                }
                finally { FullscreenController.HelperDirectory = helpers; }
                DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                ApplicationRules rules = new ApplicationRules();
                rules.Rules.Add(new ApplicationRule { ExecutablePath = app.ExecutablePath, WindowClass = app.WindowClass, DisplayName = "Game",
                    Action = ApplicationAction.ForceFullscreen, MonitorDevice = primary.DeviceName, ZoneId = "removed-region" });
                WindowCandidate[] windows = { new WindowCandidate { Handle = app.Handle, Title = "Game", Identity = app } };
                using (FullscreenController controller = new FullscreenController())
                {
                    controller.Tick(windows, rules, layout);
                    List<HandledApp> apps = controller.HandledApps();
                    Assert(apps.Count == 1 && apps[0].State == HandledAppState.Error && apps[0].Error.Contains("Game.exe (PID") &&
                        apps[0].Error.Contains("no longer exists"), "A rule naming a removed region was not reported.");
                    rules.Rules[0].Action = ApplicationAction.Ignore;
                    controller.Tick(windows, rules, layout);
                    Assert(controller.HandledApps().Count == 0 && controller.LayoutProblem == "", "An ignored app was listed.");
                    layout.MonitorDevice = @"\\.\UNPLUGGED9";
                    controller.Tick(windows, rules, layout);
                    Assert(controller.LayoutProblem.Contains(@"\\.\UNPLUGGED9 was not found"), "A missing layout display handled nothing without saying why.");
                    layout.MonitorDevice = primary.DeviceName;
                    controller.Tick(windows, rules, layout);
                    Assert(controller.LayoutProblem == "", "The layout problem outlived its cause.");
                }
            });
            test("An error with one app leaves only that app alone; the others are still handled", delegate
            {
                // Real (hidden) windows of this process keep the failure from being pruned as a closed window.
                using (System.Windows.Forms.Form first = new System.Windows.Forms.Form(), second = new System.Windows.Forms.Form())
                {
                    uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                    ApplicationIdentity broken = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Broken.exe", WindowClass = "GameWindow",
                        ProcessId = pid, CreationTime = 1, Handle = first.Handle };
                    ApplicationIdentity healthy = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Healthy.exe", WindowClass = "GameWindow",
                        ProcessId = pid, CreationTime = 1, Handle = second.Handle };
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationRules rules = new ApplicationRules();
                    foreach (ApplicationIdentity app in new[] { broken, healthy })
                        rules.Rules.Add(new ApplicationRule { ExecutablePath = app.ExecutablePath, WindowClass = app.WindowClass, DisplayName = "App",
                            Action = ApplicationAction.ForceFullscreen, MonitorDevice = primary.DeviceName, ZoneId = "removed-region" });
                    WindowCandidate[] windows = { new WindowCandidate { Handle = broken.Handle, Title = "Broken", Identity = broken },
                        new WindowCandidate { Handle = healthy.Handle, Title = "Healthy", Identity = healthy } };
                    int faults = 0;
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.FaultForTests = delegate(ApplicationIdentity app)
                        {
                            if (app == broken) { faults++; throw new InvalidOperationException("Simulated bug"); }
                        };
                        controller.Tick(windows, rules, layout);
                        controller.Tick(windows, rules, layout);
                        Dictionary<string, HandledApp> apps = new Dictionary<string, HandledApp>();
                        foreach (HandledApp app in controller.HandledApps()) apps[Path.GetFileName(app.Target.ExecutablePath)] = app;
                        Assert(faults == 1, "The failed app was handled again on the next tick.");
                        Assert(apps.ContainsKey("Broken.exe") && apps["Broken.exe"].State == HandledAppState.Error &&
                            apps["Broken.exe"].Error.Contains("internal error") && apps["Broken.exe"].Error.Contains("Simulated bug"),
                            "The failed app did not say why it is left alone.");
                        Assert(apps.ContainsKey("Healthy.exe") && apps["Healthy.exe"].Error.Contains("no longer exists"),
                            "One app's error stopped the handling of another app.");
                        Assert(!controller.Paused && controller.LastInternalError.Contains("Simulated bug"), "The error paused handling or lost its details.");
                        controller.Resume();
                        controller.Tick(windows, rules, layout);
                        Assert(faults == 2, "Resuming did not retry the failed app.");
                    }
                }
            });
            test("RegionService saves, reloads and reports problems without a UI", delegate
            {
                string data = Path.Combine(temporary, "service");
                string blocker = Path.Combine(temporary, "service-blocker");
                Directory.CreateDirectory(data);
                File.WriteAllText(blocker, "");
                try
                {
                    int saves = 0;
                    using (RegionService service = new RegionService(data))
                    {
                        service.Saved += delegate { saves++; };
                        Assert(service.LoadWarning == "" && service.Rules.Rules.Count == 0 && service.Layout.Zones.Count > 0 &&
                            service.Layout.MonitorDevice == System.Windows.Forms.Screen.PrimaryScreen.DeviceName, "Defaults did not load on the primary monitor.");
                        Assert(service.Assign(IntPtr.Zero, 99, ApplicationAction.ForceFullscreen) == "There is no region for that shortcut.",
                            "A missing region was not reported.");
                        Assert(!String.IsNullOrEmpty(service.Assign(IntPtr.Zero, 0, ApplicationAction.ForceFullscreen)), "An invalid window was not reported.");
                        Assert(service.Rules.Rules.Count == 0 && saves == 0 && !File.Exists(service.RulesPath), "A rejected action saved something.");
                        service.Save();
                        Assert(saves == 1 && File.Exists(service.LayoutPath) && File.Exists(service.RulesPath), "Save did not write both files.");
                    }
                    using (RegionService reloaded = new RegionService(data))
                        Assert(reloaded.LoadWarning == "", "Saved files did not reload cleanly.");
                    File.WriteAllText(Path.Combine(data, "rules.xml"), "<not rules");
                    using (RegionService unreadable = new RegionService(data))
                        Assert(unreadable.LoadWarning.Contains("rules.xml.unreadable.bak") &&
                            File.ReadAllText(Path.Combine(data, "rules.xml.unreadable.bak")) == "<not rules",
                            "An unreadable rules file was not kept before the next save could replace it.");
                    using (RegionService blocked = new RegionService(blocker))
                    {
                        bool raised = false;
                        blocked.Saved += delegate { raised = true; };
                        try { blocked.Save(); throw new InvalidOperationException("Saving under a file path did not fail."); }
                        catch (IOException) { }
                        Assert(!raised, "Saved was raised for a failed save.");
                    }
                }
                finally
                {
                    foreach (string path in Directory.GetFiles(data)) File.Delete(path);
                    Directory.Delete(data);
                    File.Delete(blocker);
                }
            });
        }
    }
}
