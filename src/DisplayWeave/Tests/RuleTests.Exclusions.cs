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
        // The launcher and Chromium exclusions: never hooked, Chromium windows only fitted from outside.
        private static void ExclusionTests(Action<string, Action> test)
        {
            test("Battle.net exclusions match only the three complete launcher image names", delegate
            {
                string[] names = { "Battle.net.exe", "Battle.net Launcher.exe", "Battle.net Helper.exe" };
                foreach (string name in names)
                {
                    Assert(LauncherProcessProtection.IsExcludedExecutable(name), "Launcher image was not excluded: " + name);
                    Assert(LauncherProcessProtection.IsExcludedExecutable(@"C:\Program Files (x86)\Battle.net\" + name.ToUpperInvariant()), "Full path or uppercase launcher image bypassed protection: " + name);
                    Assert(LauncherProcessProtection.IsExcludedExecutable("C:/Program Files (x86)/Battle.net/" + name.ToLowerInvariant()), "Alternate path separators bypassed launcher protection: " + name);
                }
            });
            test("Battle.net exclusions leave games and similarly named applications eligible", delegate
            {
                string[] allowed = { "Game.exe", "Game64.exe", "Client.exe", "Shooter.exe", "Agent.exe",
                    "MyBattle.net.exe", "Battle.net.exe.backup", "Battle.netGame.exe", "Battle.net Helper2.exe", "LauncherGame.exe",
                    @"C:\Battle.net.exe\Game.exe", @"C:\Games\Battle.net\Game.exe",
                    @"C:\Games\Battle.net Launcher.exe\Shooter.exe", "C:/Battle.net Helper.exe/Client.exe", null, "" };
                foreach (string path in allowed)
                    Assert(!LauncherProcessProtection.IsExcludedExecutable(path), "An eligible game or near-match was excluded: " + path);
            });
            test("Manual and saved-rule attachments cannot bypass the Battle.net launcher exclusion", delegate
            {
                string[] names = { "Battle.net.exe", "Battle.net Launcher.exe", "Battle.net Helper.exe" };
                foreach (string name in names)
                {
                    // An impossible PID and fake HWND keep this test independent of user applications.
                    ApplicationIdentity launcher = new ApplicationIdentity { ExecutablePath = @"C:\Apps\" + name,
                        WindowClass = "OrdinaryMainWindow", ProcessId = UInt32.MaxValue, CreationTime = 1, Handle = new IntPtr(1) };
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.AutomaticExecutableFilter = launcher.ExecutablePath;
                        Assert(!controller.Attach(launcher, new Rectangle(100, 100, 800, 600), true), "Explicit Force bypassed launcher protection: " + name);
                        Assert(!controller.Attach(launcher, new Rectangle(100, 100, 800, 600), false), "Saved-rule attachment bypassed launcher protection: " + name);
                        Assert(controller.LastMessage == LauncherProcessProtection.RejectionMessage, "Launcher rejection lost its compatibility explanation.");
                        controller.Resume();
                        Assert(!controller.Attach(launcher, new Rectangle(100, 100, 800, 600), false), "Resume removed built-in launcher protection.");
                        Assert(controller.Count == 0 && controller.PendingCount == 0 && controller.FailureCount == 0,
                            "Launcher rejection launched a helper or created a retryable failure.");
                    }
                }
            });
            test("A rule may load a Chromium window; default and Maximize handling keep the outside fit", delegate
            {
                // The gate is open by default (DISPLAYWEAVE_FORCE_CHROMIUM defaults on), so a Force rule is free to load a
                // Chromium window. Real injection launches a helper into the target, so it is validated live, not here.
                foreach (string cls in new[] { "Chrome_WidgetWin_1", "Chrome_WidgetWin_0" })
                    Assert(FullscreenController.OutsideOnlyReason(cls) == null, "The Chromium gate is closed by default: " + cls);
                foreach (string cls in new[] { "Chrome_RenderWidgetHostHWND", "MyChrome_WidgetWin_1", "chrome_widgetwin_1", "", null })
                    Assert(!ChromiumWindowProtection.IsChromiumWindow(cls), "A non-Chromium class was excluded: " + cls);
                Assert(FullscreenController.KeepsMaximizedFit(RegionControlMode.Automatic, true) &&
                    FullscreenController.KeepsMaximizedFit(RegionControlMode.Force, true), "Automatic or Force handling dropped the outside fit.");
                // A Maximize rule never injects, so a Chromium window under one stays on the outside fit and starts no helper.
                using (System.Windows.Forms.Form window = new System.Windows.Forms.Form())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationIdentity browser = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Browser.exe", WindowClass = "Chrome_WidgetWin_1",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    rules.Rules.Add(new ApplicationRule { ExecutablePath = browser.ExecutablePath, WindowClass = browser.WindowClass, DisplayName = "Browser",
                        Action = ApplicationAction.MaximizeInRegion, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[1].Id });
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Browser", Identity = browser } }, rules, layout);
                        Assert(controller.Count == 0 && controller.PendingCount == 0 && controller.OutsideFitCount == 1,
                            "A Maximize rule on a Chromium window started a helper instead of the outside fit.");
                    }
                }
            });
            test("A maximized window that refuses moves, as Chromium does, is still fitted into its region", delegate
            {
                using (RefusingWindow window = new RefusingWindow())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationIdentity editor = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "EditorWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    rules.Rules.Add(new ApplicationRule { ExecutablePath = editor.ExecutablePath, WindowClass = editor.WindowClass, DisplayName = "Editor",
                        Action = ApplicationAction.MaximizeInRegion, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[0].Id });
                    Rectangle region = layout.Zones[0].ToPixels(layout.UseWorkArea ? primary.WorkingArea : primary.Bounds);
                    Assert(window.Zoomed && window.VisibleFrame != region, "The test window did not start maximized over the whole display.");
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } }, rules, layout);
                        Assert(window.VisibleFrame == region, "A maximized window that refuses moves was not fitted: " + window.VisibleFrame + " instead of " + region + ".");
                    }
                }
            });
        }
    }
}
