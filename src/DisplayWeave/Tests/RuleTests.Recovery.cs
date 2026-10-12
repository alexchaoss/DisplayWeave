using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace DisplayWeave
{
    public static partial class RuleTests
    {
        // What survives a restart or a crash, and what the user can choose in the moment: the borderless journal, the
        // remembered regions, Shift while maximizing, shortcuts and the activity log. The one-click rule offer is made where
        // an outside fit gives up, which needs a window in another process: the engine never inspects its own.
        private static void RecoveryTests(Action<string, Action> test)
        {
            test("The borderless journal reads back what it wrote and skips damaged lines", delegate
            {
                BorderlessJournal entry = new BorderlessJournal { Handle = new IntPtr(0x1234), ProcessId = 42, CreationTime = 133000000000000000,
                    RemovedStyle = 0x00C40000L, RemovedExStyle = 0x00000100L, OriginalBounds = new Rectangle(-10, 20, 800, 600), ExecutablePath = @"D:\Games\Game.exe" };
                List<BorderlessJournal> read = BorderlessJournal.Parse(BorderlessJournal.Format(new[] { entry }) + "garbage\n1|2|3\n");
                Assert(read.Count == 1 && read[0].Handle == entry.Handle && read[0].ProcessId == 42 && read[0].CreationTime == entry.CreationTime &&
                    read[0].RemovedStyle == entry.RemovedStyle && read[0].RemovedExStyle == entry.RemovedExStyle &&
                    read[0].OriginalBounds == entry.OriginalBounds && read[0].ExecutablePath == entry.ExecutablePath, "The journal did not round-trip.");
                ApplicationIdentity same = new ApplicationIdentity { Handle = entry.Handle, ProcessId = 42, CreationTime = entry.CreationTime, ExecutablePath = @"d:\games\game.exe" };
                ApplicationIdentity reused = new ApplicationIdentity { Handle = entry.Handle, ProcessId = 42, CreationTime = entry.CreationTime + 1, ExecutablePath = entry.ExecutablePath };
                Assert(read[0].Matches(same) && !read[0].Matches(reused), "A reused process ID was taken for the journaled window.");
            });
            test("A window shown borderless is journaled until it is released", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "DisplayWeave-journal-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    using (System.Windows.Forms.Form window = new System.Windows.Forms.Form { StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                        Bounds = new Rectangle(100, 100, 800, 600) })
                    {
                        DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                        LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                        ApplicationIdentity game = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Game.exe", WindowClass = "GameWindow",
                            ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                        ApplicationRules rules = new ApplicationRules();
                        ApplicationRule rule = Rule(game.ExecutablePath, game.WindowClass, ApplicationAction.BorderlessInRegion);
                        rule.MonitorDevice = primary.DeviceName; rule.ZoneId = layout.Zones[0].Id;
                        rules.Rules.Add(rule);
                        long style = WindowStyle(window.Handle, -16), extended = WindowStyle(window.Handle, -20);
                        Rectangle original = WindowBounds(window.Handle);
                        System.Windows.Forms.Application.DoEvents();
                        string journal = Path.Combine(directory, "borderless.journal");
                        using (FullscreenController controller = new FullscreenController())
                        {
                            controller.UseDataDirectory(directory);
                            controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Game", Identity = game } }, rules, layout);
                            List<BorderlessJournal> entries = BorderlessJournal.Read(journal);
                            Assert(entries.Count == 1 && entries[0].Handle == window.Handle && entries[0].OriginalBounds == original &&
                                (entries[0].RemovedStyle & 0x00C40000L) == 0x00C40000L, "The borderless window was not journaled with what was removed.");
                        }
                        Assert(!File.Exists(journal), "The journal stayed after the window was released.");
                        // The engine never inspects its own process, so the title bar is put back directly.
                        System.Windows.Forms.Application.DoEvents();
                        FullscreenController.RestoreBorderless(window.Handle, style & 0x00C40000L, extended & 0x00020301L, original);
                    }
                }
                finally { Directory.Delete(directory, true); }
            });
            test("Holding Shift while maximizing leaves the window filling its display until it is restored", delegate
            {
                using (RefusingWindow window = new RefusingWindow())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName };
                    ApplicationIdentity editor = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "EditorWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    WindowCandidate[] windows = { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } };
                    Rectangle before = window.VisibleFrame;
                    bool shift = true;
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.ShiftHeld = delegate { return shift; };
                        controller.Tick(windows, new ApplicationRules(), layout);
                        Assert(window.VisibleFrame == before && controller.LastMessage.Contains("Shift"), "The window was fitted although Shift was held.");
                        shift = false;
                        controller.Tick(windows, new ApplicationRules(), layout);
                        Assert(window.VisibleFrame == before, "The window was fitted before it was restored.");
                    }
                }
            });
            test("Remembered app regions are kept per app and profile and capped", delegate
            {
                string path = Path.Combine(Path.GetTempPath(), "DisplayWeave-regions-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    AppRegionMemory memory = AppRegionMemory.Load(path);
                    ApplicationIdentity app = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "HwndWrapper[Editor.exe;;3f2504e0-4f89-11d3-9a0c-0305e82c3301]" };
                    ApplicationIdentity nextRun = new ApplicationIdentity { ExecutablePath = @"c:\apps\EDITOR.exe", WindowClass = "HwndWrapper[Editor.exe;;9b7e8a1c-0000-4d6e-8f00-1234567890ab]" };
                    memory.Remember(AppRegionMemory.Key(app, "Default"), "left");
                    Assert(AppRegionMemory.Load(path).Find(AppRegionMemory.Key(nextRun, "Default")) == "left", "The region was not remembered for the app's next run.");
                    Assert(memory.Find(AppRegionMemory.Key(app, "Gaming")) == null, "A region leaked into another profile.");
                    memory.Path = null; // the cap needs no 300 saves
                    for (int i = 0; i < 300; i++) memory.Remember("app" + i, "zone");
                    Assert(memory.Entries.Count == 256 && memory.Find(AppRegionMemory.Key(app, "Default")) == null && memory.Find("app299") == "zone",
                        "The oldest apps were not dropped first.");
                }
                finally { File.Delete(path); }
            });
            test("A remembered region is used only for a window seen for the first time", delegate
            {
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                Rectangle area = new Rectangle(0, 0, 3000, 1000);
                List<Zone> zones = LayoutSettings.Preset(1);
                string zoneId;
                Rectangle region;
                policy.Prefer("new", zones[2].Id);
                Assert(policy.Observe("new", area, new Rectangle(100, 100, 400, 300), true, false, area, zones, out zoneId, out region) && zoneId == zones[2].Id,
                    "A window that started maximized did not go to its remembered region.");
                Assert(policy.Observe("seen", new Rectangle(100, 100, 400, 300), Rectangle.Empty, false, false, area, zones, out zoneId, out region), "Setup failed.");
                policy.Prefer("seen", zones[2].Id);
                Assert(policy.Observe("seen", area, Rectangle.Empty, true, false, area, zones, out zoneId, out region) && zoneId == zones[0].Id,
                    "A remembered region overrode where the user had put the window.");
            });
            test("Saved shortcuts keep only valid, distinct keys", delegate
            {
                string path = Path.Combine(Path.GetTempPath(), "DisplayWeave-shortcuts-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    ShortcutSettings settings = new ShortcutSettings();
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Control | Shortcut.Alt, Key = 0x31, Action = ShortcutAction.FitInRegion, Region = 1 });
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Control | Shortcut.Alt, Key = 0x31, Action = ShortcutAction.PauseOrResume });
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Shift, Key = 0x41, Action = ShortcutAction.PauseOrResume });
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Alt, Key = 0x32, Action = ShortcutAction.BorderlessInRegion, Region = 0 });
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Alt, Key = 0x33, Action = ShortcutAction.SwitchProfile, Profile = "..\\x" });
                    settings.Items.Add(new Shortcut { Modifiers = Shortcut.Windows | Shortcut.Shift, Key = 0x50, Action = ShortcutAction.SwitchProfile, Profile = "Gaming" });
                    settings.Save(path);
                    string warning;
                    ShortcutSettings loaded = ShortcutSettings.Load(path, out warning);
                    Assert(warning.Length == 0 && loaded.Items.Count == 2 && loaded.Items[0].Action == ShortcutAction.FitInRegion && loaded.Items[1].Profile == "Gaming",
                        "Invalid or duplicate shortcuts were kept, or valid ones lost.");
                    Assert(MainForm.Describe(loaded.Items[0]) == "Ctrl + Alt + 1", "The shortcut reads " + MainForm.Describe(loaded.Items[0]) + ".");
                }
                finally { File.Delete(path); }
            });
            test("The activity log keeps one line per message, rolls over and stops when closed", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "DisplayWeave-log-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ActivityLog.Open(directory);
                    ActivityLog.Write("First line\r\nwith a second part");
                    ActivityLog.Write("   ");
                    List<string> tail = ActivityLog.Tail(40);
                    Assert(tail.Count == 1 && tail[0].EndsWith("First line with a second part"), "A message was not written as one line.");
                    string big = new string('x', 64 * 1024);
                    for (int i = 0; i < 17; i++) ActivityLog.Write(big);
                    ActivityLog.Write("After the roll");
                    Assert(File.Exists(Path.Combine(directory, @"logs\activity.1.log")) && new FileInfo(Path.Combine(directory, @"logs\activity.log")).Length < 1024 * 1024,
                        "The log did not roll over at 1 MB.");
                    Assert(ActivityLog.Tail(1)[0].EndsWith("After the roll"), "The newest line is not last.");
                    ActivityLog.Open(null);
                    ActivityLog.Write("Not written");
                    Assert(ActivityLog.Tail(5).Count == 0, "A closed log still answered.");
                }
                finally { ActivityLog.Open(null); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}
