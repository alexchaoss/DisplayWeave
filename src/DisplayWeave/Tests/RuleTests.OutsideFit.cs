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
        // Windows fitted from outside: automatic handling, borderless fullscreen, Borderless in region and Centered fullscreen.
        private static void OutsideFitTests(Action<string, Action> test)
        {
            test("An update leaves fitted windows in their regions and the next version adopts them", delegate
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
                    WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } };
                    using (FullscreenController current = new FullscreenController())
                    {
                        current.Tick(windows, rules, layout);
                        Assert(window.VisibleFrame == region, "The maximized window was not fitted: " + window.VisibleFrame + " instead of " + region + ".");
                        Assert(!current.HasExpandedApp, "A window fitted from outside held back an update.");
                        current.LeaveFittedWindows(); // as when the setup asks DisplayWeave to exit for an update
                    }
                    Assert(window.VisibleFrame == region, "Exiting for an update returned a fitted window to the whole display.");
                    using (FullscreenController next = new FullscreenController())
                    {
                        next.Tick(windows, rules, layout);
                        Assert(window.VisibleFrame == region && next.HandledApps().Exists(delegate(HandledApp app) { return app.State == HandledAppState.FittedFromOutside; }),
                            "The next version did not adopt the window already in its region.");
                    }
                    // Adopted windows are returned on an ordinary exit like any other fit; that path inspects the window,
                    // which TryInspect never does for this test process, so it is not repeated here.
                }
            });
            test("Automatic handling fits a maximized window from outside and starts no helper", delegate
            {
                using (RefusingWindow window = new RefusingWindow())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationIdentity editor = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "EditorWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } };
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(windows, new ApplicationRules(), layout);
                        Assert(controller.Count == 0 && controller.PendingCount == 0, "Automatic handling started the component.");
                        Assert(layout.Zones.Exists(delegate(Zone zone) { return zone.ToPixels(primary.WorkingArea) == window.VisibleFrame; }),
                            "The maximized window was not fitted into a region: " + window.VisibleFrame + ".");
                    }
                }
            });

            test("Borderless fullscreen windows are fitted from outside and overlays are left alone", delegate
            {
                DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                Rectangle region = layout.Zones[0].ToPixels(layout.UseWorkArea ? primary.WorkingArea : primary.Bounds);
                foreach (int extended in new[] { 0, 0x20, 0x08, 0x08000000 }) // none, WS_EX_TRANSPARENT, WS_EX_TOPMOST, WS_EX_NOACTIVATE
                {
                    using (BorderlessWindow window = new BorderlessWindow(primary.Bounds, extended))
                    {
                        ApplicationIdentity player = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Player.exe", WindowClass = "PlayerWindow",
                            ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                        ApplicationRules rules = new ApplicationRules();
                        rules.Rules.Add(new ApplicationRule { ExecutablePath = player.ExecutablePath, WindowClass = player.WindowClass, DisplayName = "Player",
                            Action = ApplicationAction.MaximizeInRegion, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[0].Id });
                        WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Player", Identity = player } };
                        using (FullscreenController controller = new FullscreenController())
                        {
                            controller.Tick(windows, rules, layout);
                            bool fitted = controller.HandledApps().Exists(delegate(HandledApp app) { return app.State == HandledAppState.FittedFromOutside; });
                            if (extended == 0) Assert(window.Bounds == region && fitted, "The borderless fullscreen window was not fitted: " + window.Bounds + " instead of " + region + ".");
                            else Assert(window.Bounds == primary.Bounds && !fitted, "An overlay window was moved; extended style 0x" + extended.ToString("X") + ".");
                        }
                    }
                }
                // Not covered here: staying tracked in its region and the return to the whole monitor. Both need a
                // window in another process, because the engine never inspects its own.
            });

            test("Borderless in region removes the title bar from outside and puts it back", delegate
            {
                using (System.Windows.Forms.Form window = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = new Rectangle(100, 100, 800, 600) })
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    Rectangle region = layout.Zones[0].ToPixels(layout.UseWorkArea ? primary.WorkingArea : primary.Bounds);
                    ApplicationIdentity game = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Game.exe", WindowClass = "GameWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    rules.Rules.Add(new ApplicationRule { ExecutablePath = game.ExecutablePath, WindowClass = game.WindowClass, DisplayName = "Game",
                        Action = ApplicationAction.BorderlessInRegion, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[0].Id });
                    WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Game", Identity = game } };
                    long style = WindowStyle(window.Handle, -16), extended = WindowStyle(window.Handle, -20);
                    Rectangle original = WindowBounds(window.Handle);
                    Assert((style & 0x00C40000L) == 0x00C40000L, "The test window has no title bar to remove.");
                    // A thread that has not checked its messages for a while counts as hung; the engine skips hung windows.
                    System.Windows.Forms.Application.DoEvents();
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(windows, rules, layout);
                        Assert((WindowStyle(window.Handle, -16) & 0x00C40000L) == 0 && WindowBounds(window.Handle) == region,
                            "The window was not shown without its title bar in its region: " + WindowBounds(window.Handle) + " instead of " + region + ".");
                        Assert(controller.HasExpandedApp && controller.HandledApps().Exists(delegate(HandledApp app) { return app.State == HandledAppState.FittedFromOutside; }),
                            "A borderless window did not hold back updates or was not listed.");
                        controller.LeaveFittedWindows();
                        Assert(controller.HasExpandedApp, "An update forgot a window whose title bar it must put back.");
                    }
                    // The engine never inspects its own process, so the release is checked directly.
                    System.Windows.Forms.Application.DoEvents();
                    FullscreenController.RestoreBorderless(window.Handle, style & 0x00C40000L, extended & 0x00020301L, original);
                    Assert((WindowStyle(window.Handle, -16) & 0x00C40000L) == 0x00C40000L && WindowBounds(window.Handle) == original,
                        "The title bar or place did not come back: " + WindowBounds(window.Handle) + " instead of " + original + ".");
                }
            });

            test("A game that puts its title bar back around a picture filling the region counts as fitted", delegate
            {
                using (System.Windows.Forms.Form window = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = new Rectangle(100, 100, 800, 600) })
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    Rectangle region = layout.Zones[0].ToPixels(layout.UseWorkArea ? primary.WorkingArea : primary.Bounds);
                    ApplicationIdentity game = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Game.exe", WindowClass = "GameWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    rules.Rules.Add(new ApplicationRule { ExecutablePath = game.ExecutablePath, WindowClass = game.WindowClass, DisplayName = "Game",
                        Action = ApplicationAction.BorderlessInRegion, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[0].Id });
                    WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Game", Identity = game } };
                    long style = WindowStyle(window.Handle, -16), extended = WindowStyle(window.Handle, -20);
                    Rectangle original = WindowBounds(window.Handle), client = window.RectangleToScreen(window.ClientRectangle);
                    System.Windows.Forms.Application.DoEvents();
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(windows, rules, layout);
                        // As such a game does: its title bar comes back and the window grows so its client area stays in the region.
                        System.Windows.Forms.Application.DoEvents();
                        FullscreenController.RestoreBorderless(window.Handle, style & 0x00C40000L, extended & 0x00020301L, Rectangle.FromLTRB(
                            region.Left - (client.Left - original.Left), region.Top - (client.Top - original.Top),
                            region.Right + (original.Right - client.Right), region.Bottom + (original.Bottom - client.Bottom)));
                        System.Threading.Thread.Sleep(2100); // past the retry delay, so another removal would show
                        System.Windows.Forms.Application.DoEvents();
                        controller.Tick(windows, rules, layout);
                        System.Windows.Forms.Application.DoEvents();
                        Assert((WindowStyle(window.Handle, -16) & 0x00C40000L) == 0x00C40000L && window.RectangleToScreen(window.ClientRectangle) == region,
                            "DisplayWeave fought a title bar around a picture already in its region: client " + window.RectangleToScreen(window.ClientRectangle) + ".");
                        // The engine never inspects its own process, so this window is listed as Ready rather than Fitted from outside.
                        Assert(!controller.HandledApps().Exists(delegate(HandledApp app) { return app.State == HandledAppState.Error; }),
                            "A picture filling its region was reported as an error.");
                    }
                }
            });

            test("Saved rules keep the borderless and centered actions", delegate
            {
                string path = Path.Combine(Path.GetTempPath(), "DisplayWeave-rules-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    ApplicationRules saved = new ApplicationRules();
                    saved.Rules.Add(new ApplicationRule { ExecutablePath = @"C:\Apps\A.exe", WindowClass = "A", Action = ApplicationAction.BorderlessInRegion, ZoneId = "z" });
                    saved.Rules.Add(new ApplicationRule { ExecutablePath = @"C:\Apps\B.exe", WindowClass = "B", Action = ApplicationAction.CenteredFullscreen, ZoneId = "z" });
                    saved.Save(path);
                    string warning;
                    ApplicationRules loaded = ApplicationRules.Load(path, out warning);
                    Assert(warning.Length == 0 && loaded.Rules.Count == 2 && loaded.Rules[0].Action == ApplicationAction.BorderlessInRegion &&
                        loaded.Rules[1].Action == ApplicationAction.CenteredFullscreen, "A new rule action was lost on load: " + warning);
                }
                finally { File.Delete(path); }
            });

            test("Centered fullscreen lines up only with a region in the middle of its display", delegate
            {
                Rectangle monitor = new Rectangle(0, 0, 7680, 2160), work = new Rectangle(0, 0, 7680, 2100);
                Size mode;
                Assert(DisplayMonitor.TryCenteredMode(new Rectangle(1980, 0, 3720, 2100), monitor, work, out mode) && mode == new Size(3720, 2160),
                    "The middle of 1980 | 3720 | 1980 did not give 3720 x 2160: " + mode + ".");
                Assert(!DisplayMonitor.TryCenteredMode(new Rectangle(0, 0, 1980, 2100), monitor, work, out mode), "A side region was offered centered fullscreen.");
                Assert(!DisplayMonitor.TryCenteredMode(new Rectangle(1980, 0, 3720, 1050), monitor, work, out mode), "A top region was offered centered fullscreen.");
                Rectangle second = new Rectangle(7680, 0, 3840, 2160);
                Assert(DisplayMonitor.TryCenteredMode(new Rectangle(8640, 0, 1920, 2160), second, second, out mode) && mode == new Size(1920, 2160),
                    "The middle of a second display was refused: " + mode + ".");
            });

            test("Outside fit failures carry their own advice instead of suggesting the component", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Game.exe", ProcessId = 7 };
                string message = FailureMessages.FormatFailure(target, "It resized itself back to fill its monitor, so DisplayWeave stopped moving it", true);
                Assert(message == "Game.exe (PID 7): It resized itself back to fill its monitor, so DisplayWeave stopped moving it.", "Unexpected fit failure: " + message);
            });
        }
    }
}
