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
        // The Windows shell and File Explorer: never hooked; folder windows only fitted from outside.
        private static void ShellTests(Action<string, Action> test)
        {
            test("Native launcher rejection remains distinct from an ordinary unsupported operation", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Battle.net.exe", ProcessId = 90 };
                string native = "ERROR Battle.net launcher is excluded because fullscreen hooks can cause crashes; games remain eligible (Windows error 50)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureIsLauncherExcluded(native), "Native compatibility exclusion was not recognized.");
                Assert(message.Contains("Battle.net.exe (PID 90)") && message.Contains("games remain eligible") && message.Contains("Windows error 50"), "Launcher guidance lost native evidence or game eligibility.");
                Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator"), "Built-in launcher exclusion suggests a futile bypass.");
                Assert(!FailureMessages.FailureIsLauncherExcluded("ERROR Requested operation is unavailable (Windows error 50)") &&
                    !FailureMessages.FailureIsLauncherExcluded(null), "Generic unsupported operation was classified as a launcher exclusion.");
                string generic = "Native helper exited before the application closed.";
                Assert(FailureMessages.PreferFailure(generic, native) == native && FailureMessages.PreferFailure(native, generic) == native,
                    "Generic helper exit erased the compatibility exclusion.");
            });
            test("Windows shell executable exclusions match exact basenames case-insensitively", delegate
            {
                string[] shellNames = { "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ShellHost.exe",
                    "SearchHost.exe", "SearchApp.exe", "SearchUI.exe", "TextInputHost.exe", "InputApp.exe", "LockApp.exe", "CredentialUIBroker.exe",
                    "dwm.exe", "sihost.exe", "LogonUI.exe", "winlogon.exe" };
                foreach (string name in shellNames)
                    Assert(ShellProcessProtection.IsExcludedExecutable(@"C:\Windows\SystemApps\" + name.ToUpperInvariant()), "Unprotected shell executable: " + name);
                Assert(!ShellProcessProtection.IsExcludedExecutable(@"C:\Apps\Editor.exe"), "Normal app was treated as shell.");
                Assert(!ShellProcessProtection.IsExcludedExecutable(@"C:\Apps\explorer.exe\Editor.exe"), "Folder name triggered shell protection.");
                Assert(!ShellProcessProtection.IsExcludedExecutable(@"C:\Apps\MyExplorer.exe"), "Partial executable name was blocked.");
                Assert(!ShellProcessProtection.IsExcludedExecutable(null), "Unknown path was falsely identified as a shell executable.");
            });
            test("Desktop ownership blocks ordinary folder windows and alternate shell executables", delegate
            {
                Assert(ShellProcessProtection.IsExcludedTarget(@"C:\Windows\explorer.exe", "CabinetWClass", false),
                    "An ordinary Explorer folder bypassed process-wide shell protection.");
                Assert(ShellProcessProtection.IsExcludedTarget(@"C:\Desktop\CustomShell.exe", "OrdinaryMainWindow", true),
                    "Actual desktop/taskbar ownership was ignored for an unfamiliar executable.");
                Assert(!ShellProcessProtection.IsExcludedTarget(@"C:\Apps\Editor.exe", "OrdinaryMainWindow", false),
                    "A normal application without shell ownership was excluded.");
                string[] shellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView", "CabinetWClass", "ExploreWClass" };
                foreach (string cls in shellClasses)
                    Assert(ShellProcessProtection.IsExcludedTarget(@"C:\Desktop\CustomShell.exe", cls.ToLowerInvariant(), false),
                        "Shell window class was not protected: " + cls);
                Assert(!ShellProcessProtection.IsExcludedWindowClass("Editor_MainWindow"), "Ordinary application class was blocked.");
            });
            test("Explicit and saved-rule attachment cannot bypass Explorer protection", delegate
            {
                // A deliberately nonexistent PID ensures this test never targets the real shell.
                ApplicationIdentity shell = new ApplicationIdentity { ExecutablePath = @"C:\Windows\explorer.exe",
                    WindowClass = "Progman", ProcessId = UInt32.MaxValue, CreationTime = 1, Handle = new IntPtr(1) };
                using (FullscreenController controller = new FullscreenController())
                {
                    controller.AutomaticExecutableFilter = shell.ExecutablePath;
                    Assert(!controller.Attach(shell, new Rectangle(100, 100, 800, 600), true), "Manual Force attached to Explorer.");
                    Assert(controller.LastMessage == ShellProcessProtection.RejectionMessage, "Manual rejection did not explain shell protection.");
                    Assert(!controller.Attach(shell, new Rectangle(100, 100, 800, 600), false), "Saved-rule attachment bypassed shell protection.");
                    Assert(controller.Count == 0 && controller.PendingCount == 0 && controller.FailureCount == 0,
                        "Shell rejection launched a helper or left a misleading app failure.");
                }
            });
            test("A maximize is reported once per change, not for the fit's own move", delegate
            {
                using (MaximizeWatcher watcher = new MaximizeWatcher(delegate { }))
                {
                    IntPtr first = new IntPtr(11), second = new IntPtr(12);
                    Assert(watcher.BecameMaximized(first, true), "A maximize was not reported.");
                    Assert(!watcher.BecameMaximized(first, true), "Moving a maximized window, as the fit does, was reported as a new maximize.");
                    Assert(!watcher.BecameMaximized(first, false) && watcher.BecameMaximized(first, true), "A restore and second maximize was missed.");
                    Assert(watcher.BecameMaximized(second, true), "Another window's maximize was missed.");
                }
            });
            test("Folder windows are never hooked and are fitted from outside when maximized", delegate
            {
                foreach (string cls in new[] { "CabinetWClass", "exploreWClass" })
                {
                    Assert(ShellProcessProtection.IsFolderWindow(cls) && ShellProcessProtection.IsExcludedWindowClass(cls),
                        "A folder window class lost its outside-only handling or its hook exclusion: " + cls);
                    // A deliberately nonexistent PID ensures this test never targets the real shell.
                    ApplicationIdentity folder = new ApplicationIdentity { ExecutablePath = @"C:\Windows\explorer.exe",
                        WindowClass = cls, ProcessId = UInt32.MaxValue, CreationTime = 1, Handle = new IntPtr(1) };
                    using (FullscreenController controller = new FullscreenController())
                    {
                        Assert(!controller.Attach(folder, new Rectangle(100, 100, 800, 600), true) &&
                            controller.LastMessage == ShellProcessProtection.FolderMessage, "Explicit Force hooked a folder window or lost the explanation.");
                        Assert(!controller.Attach(folder, new Rectangle(100, 100, 800, 600), false), "A saved rule hooked a folder window.");
                        Assert(controller.Count == 0 && controller.PendingCount == 0 && controller.FailureCount == 0,
                            "A folder window started a helper or recorded a failure.");
                    }
                }
                foreach (string cls in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "SHELLDLL_DefView", "", null })
                    Assert(!ShellProcessProtection.IsFolderWindow(cls), "The desktop or taskbar was treated as a folder window: " + cls);
                // A maximized folder window with an automatic region is fitted into it.
                using (RefusingWindow window = new RefusingWindow())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationIdentity folder = new ApplicationIdentity { ExecutablePath = @"C:\Windows\explorer.exe", WindowClass = "CabinetWClass",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    rules.Rules.Add(new ApplicationRule { ExecutablePath = folder.ExecutablePath, WindowClass = folder.WindowClass, DisplayName = "Folder",
                        Action = ApplicationAction.ForceFullscreen, MonitorDevice = primary.DeviceName, ZoneId = layout.Zones[1].Id });
                    Rectangle region = layout.Zones[1].ToPixels(layout.UseWorkArea ? primary.WorkingArea : primary.Bounds);
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Folder", Identity = folder } }, rules, layout);
                        List<HandledApp> apps = controller.HandledApps();
                        Assert(window.VisibleFrame == region, "A maximized folder window was not fitted: " + window.VisibleFrame + " instead of " + region + ".");
                        Assert(apps.Count == 1 && apps[0].State == HandledAppState.FittedFromOutside && apps[0].Error == ShellProcessProtection.FolderMessage &&
                            controller.Count == 0 && controller.PendingCount == 0, "A fitted folder window was hooked or not listed with its reason.");
                    }
                }
            });
        }
    }
}
