using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    public static class RuleTests
    {
        public static int Run(string reportPath)
        {
            string fullReportPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullReportPath);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "rule-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            StringBuilder report = new StringBuilder();
            int passed = 0, failed = 0;
            Action<string, Action> test = delegate(string name, Action body)
            {
                try { body(); passed++; report.AppendLine("PASS " + name); }
                catch (Exception ex) { failed++; report.AppendLine("FAIL " + name + ": " + ex.Message); }
            };
            try
            {
                test("Force and Ignore rules survive save/load with exact monitor and zone targets", delegate
                {
                    ApplicationRules rules = new ApplicationRules();
                    ApplicationRule force = Rule("C:\\Games\\Example.exe", "GameWindow", ApplicationAction.ForceFullscreen);
                    rules.Put(force);
                    rules.Put(Rule("C:\\Apps\\Example.exe", "PlayerWindow", ApplicationAction.Ignore));
                    rules.Put(Rule("D:\\Games\\Other.exe", "MainWindow", ApplicationAction.MaximizeInRegion));
                    string path = Path.Combine(temporary, "roundtrip.xml"), warning;
                    rules.Save(path);
                    ApplicationRules loaded = ApplicationRules.Load(path, out warning);
                    Assert(warning.Length == 0 && loaded.Rules.Count == 3, "Saved rules were not loaded exactly once.");
                    Assert(loaded.Rules[0].ZoneId == force.ZoneId && loaded.Rules[0].MonitorDevice == force.MonitorDevice, "Fullscreen target changed.");
                    Assert(loaded.Rules[1].Action == ApplicationAction.Ignore, "Ignore changed action.");
                    Assert(loaded.Rules[2].Action == ApplicationAction.MaximizeInRegion, "Maximize-in-region rule changed action.");
                });
                test("Matching uses full path case-insensitively and window class exactly", delegate
                {
                    ApplicationRule rule = Rule("C:\\Games\\Example.exe", "GameWindow", ApplicationAction.ForceFullscreen);
                    ApplicationIdentity identity = new ApplicationIdentity { ExecutablePath = "c:\\games\\EXAMPLE.EXE", WindowClass = "GameWindow" };
                    Assert(rule.Matches(identity), "Windows path casing broke matching.");
                    identity.ExecutablePath = "D:\\Games\\Example.exe";
                    Assert(!rule.Matches(identity), "Same filename in another folder was accepted.");
                    identity.ExecutablePath = rule.ExecutablePath; identity.WindowClass = "gamewindow";
                    Assert(!rule.Matches(identity), "Different window class was accepted.");
                });
                test("Changing a rule replaces the matching rule and preserves other classes", delegate
                {
                    ApplicationRules rules = new ApplicationRules();
                    rules.Put(Rule("C:\\Games\\Example.exe", "GameWindow", ApplicationAction.ForceFullscreen));
                    rules.Put(Rule("C:\\Games\\Example.exe", "LauncherWindow", ApplicationAction.ForceFullscreen));
                    rules.Put(Rule("c:\\games\\example.exe", "GameWindow", ApplicationAction.Ignore));
                    Assert(rules.Rules.Count == 2, "Replacement added a duplicate or removed another class.");
                    Assert(rules.Rules[1].Action == ApplicationAction.Ignore, "Force rule was not replaced by Ignore.");
                });
                test("Atomic overwrite and removal remain loadable", delegate
                {
                    string path = Path.Combine(temporary, "overwrite.xml"), warning;
                    ApplicationRules rules = new ApplicationRules();
                    rules.Put(Rule("C:\\Games\\Example.exe", "GameWindow", ApplicationAction.ForceFullscreen)); rules.Save(path);
                    rules.Rules.Clear(); rules.Save(path);
                    Assert(ApplicationRules.Load(path, out warning).Rules.Count == 0 && warning.Length == 0, "Removed rule returned after overwrite.");
                });
                test("Rule XML prohibits DTD and external entity resolution", delegate
                {
                    string path = Path.Combine(temporary, "dtd.xml"), warning;
                    File.WriteAllText(path, "<!DOCTYPE ApplicationRules [<!ENTITY xxe SYSTEM 'file:///does-not-exist'>]><ApplicationRules><Rules /></ApplicationRules>");
                    Assert(ApplicationRules.Load(path, out warning).Rules.Count == 0 && warning.Length != 0, "DTD was accepted.");
                });
                test("Invalid or missing rule files do not attach applications", delegate
                {
                    string path = Path.Combine(temporary, "bad.xml"), warning;
                    File.WriteAllText(path, "<ApplicationRules><Rules>");
                    Assert(ApplicationRules.Load(path, out warning).Rules.Count == 0 && warning.Length != 0, "Malformed rules were accepted.");
                    Assert(ApplicationRules.Load(Path.Combine(temporary, "missing.xml"), out warning).Rules.Count == 0 && warning.Length == 0, "Missing file did not yield an empty set.");
                });
                test("Region identifiers survive layout save/load, rename, and reorder", delegate
                {
                    LayoutSettings layout = new LayoutSettings();
                    string first = layout.Zones[0].Id, second = layout.Zones[1].Id;
                    layout.Zones[0].Name = "Renamed"; layout.Zones.Reverse();
                    string path = Path.Combine(temporary, "layout.xml"); layout.Save(path);
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    Assert(loaded.Zones[0].Id == second && loaded.Zones[1].Id == first, "Region identity followed a list index.");
                    List<Zone> preset = LayoutSettings.Preset(0);
                    Assert(preset[0].Id != first && preset[1].Id != second, "New preset silently retained old rule targets.");
                });
                test("Invalid and duplicate region identifiers are repaired uniquely", delegate
                {
                    LayoutSettings layout = new LayoutSettings();
                    layout.Zones = LayoutSettings.Preset(1);
                    string stable = layout.Zones[0].Id;
                    layout.Zones[1].Id = stable; layout.Zones[2].Id = "invalid";
                    string path = Path.Combine(temporary, "duplicate-zones.xml"); layout.Save(path);
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    Assert(loaded.Zones[0].Id == stable, "First valid identifier changed.");
                    HashSet<string> ids = new HashSet<string>();
                    foreach (Zone zone in loaded.Zones) { Guid parsed; Assert(Guid.TryParse(zone.Id, out parsed) && ids.Add(zone.Id), "Invalid or duplicate region identifier survived."); }
                });
                test("Process release identity remains stable when a window is recreated", delegate
                {
                    ApplicationIdentity first = new ApplicationIdentity { ProcessId = 20, CreationTime = 100, Handle = new IntPtr(1) };
                    ApplicationIdentity nextWindow = new ApplicationIdentity { ProcessId = 20, CreationTime = 100, Handle = new IntPtr(2) };
                    ApplicationIdentity reusedPid = new ApplicationIdentity { ProcessId = 20, CreationTime = 200, Handle = new IntPtr(2) };
                    Assert(first.ProcessKey == nextWindow.ProcessKey && first.Key != nextWindow.Key, "Restore suppression did not cover process window recreation.");
                    Assert(first.ProcessKey != reusedPid.ProcessKey, "New process reused a previous release identity.");
                });
                test("Saved regions resolve odd-sized partitions with negative monitor coordinates", delegate
                {
                    List<Zone> zones = LayoutSettings.Preset(3);
                    Rectangle area = new Rectangle(-1921, -57, 1921, 1081), resolved;
                    long total = 0;
                    foreach (Zone zone in zones)
                    {
                        Assert(FullscreenController.TryResolveRegionInArea(zone.Id, zones, area, out resolved), "A valid odd-sized grid was rejected.");
                        Assert(area.Contains(resolved), "Resolved cell left its monitor area.");
                        total += (long)resolved.Width * resolved.Height;
                    }
                    Assert(total == (long)area.Width * area.Height, "Shared rounded edges lost or duplicated pixels.");
                });
                test("Saved regions reject pixel-collapsed cells without rewriting identifiers", delegate
                {
                    List<Zone> zones = new List<Zone> { new Zone("Narrow", 0, 0, 0.000001, 1),
                        new Zone("Remainder", 0.000001, 0, 0.999999, 1) };
                    string first = zones[0].Id, second = zones[1].Id;
                    Rectangle resolved;
                    Assert(!FullscreenController.TryResolveRegionInArea(first, zones, new Rectangle(0, 0, 1920, 1080), out resolved), "A collapsed narrow cell overlapped its neighbor.");
                    Assert(resolved.IsEmpty && zones[0].Id == first && zones[1].Id == second, "Failed resolution mutated the layout or returned a target.");
                    zones = LayoutSettings.Preset(0);
                    Assert(!FullscreenController.TryResolveRegionInArea(zones[0].Id, zones, new Rectangle(0, 0, 1, 10), out resolved), "Two cells fit into one pixel.");
                });
                test("Saved regions reject gaps and overlaps across the whole layout", delegate
                {
                    Rectangle area = new Rectangle(0, 0, 1000, 800), resolved;
                    List<Zone> gaps = new List<Zone> { new Zone("Left", 0, 0, 0.4, 1), new Zone("Right", 0.6, 0, 0.4, 1) };
                    Assert(!FullscreenController.TryResolveRegionInArea(gaps[0].Id, gaps, area, out resolved), "A gapped layout was resolved.");
                    List<Zone> overlap = new List<Zone> { new Zone("Left", 0, 0, 0.6, 1), new Zone("Right", 0.4, 0, 0.6, 1) };
                    Assert(!FullscreenController.TryResolveRegionInArea(overlap[0].Id, overlap, area, out resolved), "An overlapping layout was resolved.");
                });
                test("Saved regions reject invalid geometry, duplicate IDs, and missing targets", delegate
                {
                    Rectangle area = new Rectangle(0, 0, 1000, 800), resolved;
                    List<Zone> zones = LayoutSettings.Preset(0);
                    Assert(!FullscreenController.TryResolveRegionInArea(Guid.NewGuid().ToString("N"), zones, area, out resolved), "Missing target was redirected to another cell.");
                    zones[1].Id = zones[0].Id;
                    Assert(!FullscreenController.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "An ambiguous duplicate ID was resolved.");
                    zones = LayoutSettings.Preset(0); zones[0].X = -0.1;
                    Assert(!FullscreenController.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Outside geometry was silently clamped.");
                    zones = LayoutSettings.Preset(0); zones[1].Width = Double.NaN;
                    Assert(!FullscreenController.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Nonfinite geometry in another cell was ignored.");
                    zones = LayoutSettings.Preset(0); zones[1].Height = 0;
                    Assert(!FullscreenController.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Zero-area geometry was expanded instead of rejected.");
                });
                test("Virtual monitor layout includes every cell at negative coordinates", delegate
                {
                    List<Zone> zones = LayoutSettings.Preset(3);
                    Rectangle area = new Rectangle(-1921, -1081, 1921, 1081);
                    List<Rectangle> regions;
                    Assert(FullscreenController.TryResolveLayoutInArea(zones, area, out regions), "Valid monitor partition was rejected.");
                    Assert(regions.Count == 4, "Only the selected region was supplied to the virtual monitor facade.");
                    string encoded;
                    Assert(FullscreenController.TrySerializeRegions(regions, regions[2], out encoded), "Valid region list was not serialized.");
                    Assert(encoded == "-1921,-1081,961,541;-960,-1081,960,541;-1921,-540,961,540;-960,-540,960,540",
                        "The whole layout lost its order, negative origin, or shared rounded edges: " + encoded);
                });
                test("Virtual monitor serialization is invariant for portrait regions", delegate
                {
                    System.Globalization.CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                    try
                    {
                        System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                        Rectangle top = new Rectangle(-1080, 0, 1080, 960), bottom = new Rectangle(-1080, 960, 1080, 960);
                        string encoded;
                        Assert(FullscreenController.TrySerializeRegions(new Rectangle[] { top, bottom }, bottom, out encoded), "Portrait layout was rejected.");
                        Assert(encoded == "-1080,0,1080,960;-1080,960,1080,960", "Culture changed the helper wire format.");
                        Assert(encoded.IndexOf('"') < 0 && encoded.IndexOf('\n') < 0 && encoded.IndexOf(' ') < 0, "Geometry introduced command delimiters.");
                    }
                    finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
                });
                test("Virtual monitor serialization rejects malformed or unrelated selections atomically", delegate
                {
                    Rectangle first = new Rectangle(0, 0, 100, 100);
                    string encoded;
                    Assert(!FullscreenController.TrySerializeRegions(null, first, out encoded) && encoded == null, "Missing layout was accepted.");
                    Assert(!FullscreenController.TrySerializeRegions(new Rectangle[0], first, out encoded) && encoded == null, "Empty layout was accepted.");
                    Assert(!FullscreenController.TrySerializeRegions(new Rectangle[] { first, new Rectangle(50, 0, 100, 100) }, first, out encoded) && encoded == null, "Overlapping layout was accepted.");
                    Assert(!FullscreenController.TrySerializeRegions(new Rectangle[] { first, Rectangle.Empty }, first, out encoded) && encoded == null, "Empty cell was accepted.");
                    Assert(!FullscreenController.TrySerializeRegions(new Rectangle[] { first, new Rectangle(Int32.MaxValue, 100, 1, 10) }, first, out encoded) && encoded == null, "Overflowing bounds were accepted.");
                    Assert(!FullscreenController.TrySerializeRegions(new Rectangle[] { first }, new Rectangle(100, 0, 100, 100), out encoded) && encoded == null, "Selected rectangle outside the layout was accepted.");
                    List<Rectangle> tooMany = new List<Rectangle>();
                    for (int i = 0; i < 10; i++) tooMany.Add(new Rectangle(i * 100, 0, 100, 100));
                    Assert(!FullscreenController.TrySerializeRegions(tooMany, first, out encoded) && encoded == null, "More than nine regions were accepted.");
                });
                test("Invalid source partitions cannot become a virtual monitor layout", delegate
                {
                    List<Rectangle> regions;
                    List<Zone> zones = new List<Zone> { new Zone("Narrow", 0, 0, 0.000001, 1), new Zone("Rest", 0.000001, 0, 0.999999, 1) };
                    Assert(!FullscreenController.TryResolveLayoutInArea(zones, new Rectangle(0, 0, 1920, 1080), out regions) && regions == null,
                        "Pixel-collapsed regions were partially exposed to the application.");
                    Assert(!FullscreenController.TryResolveLayoutInArea(LayoutSettings.Preset(0), new Rectangle(Int32.MaxValue - 10, 0, 20, 100), out regions),
                        "A layout outside representable screen coordinates was accepted.");
                });
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
                test("Native launcher rejection remains distinct from an ordinary unsupported operation", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Battle.net.exe", ProcessId = 90 };
                    string native = "ERROR Battle.net launcher is excluded because fullscreen hooks can cause crashes; games remain eligible (Windows error 50)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureIsLauncherExcluded(native), "Native compatibility exclusion was not recognized.");
                    Assert(message.Contains("Battle.net.exe (PID 90)") && message.Contains("games remain eligible") && message.Contains("Windows error 50"), "Launcher guidance lost native evidence or game eligibility.");
                    Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator"), "Built-in launcher exclusion suggests a futile bypass.");
                    Assert(!FullscreenController.FailureIsLauncherExcluded("ERROR Requested operation is unavailable (Windows error 50)") &&
                        !FullscreenController.FailureIsLauncherExcluded(null), "Generic unsupported operation was classified as a launcher exclusion.");
                    string generic = "Native helper exited before the application closed.";
                    Assert(FullscreenController.PreferFailure(generic, native) == native && FullscreenController.PreferFailure(native, generic) == native,
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
                        WindowClass = "CabinetWClass", ProcessId = UInt32.MaxValue, CreationTime = 1, Handle = new IntPtr(1) };
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
                test("Incompatible hook failures identify the app and require restart rather than Force retry", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", ProcessId = 572,
                        CreationTime = 100, Handle = new IntPtr(50) };
                    string native = "ERROR Restart the target application to use this DisplayWeave version (Windows error 1306)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureRequiresRestart(native), "Protocol mismatch was not classified as restart-required.");
                    Assert(message.Contains("Editor.exe (PID 572)") && message.Contains("Windows error 1306"), "Failure lost application identity or native detail.");
                    Assert(message.Contains("Close and reopen") && !message.Contains("Force fullscreen"), "Restart-required failure suggests a futile Force retry.");
                    Assert(message.Contains("cannot safely reconnect to this window") && !message.Contains("cannot be upgraded") &&
                        !message.Contains("incompatible with this DisplayWeave version"), "Restart guidance replaced the native reason with an unsupported version assumption.");
                    Assert(FullscreenController.FailureRequiresRestart("ERROR Failed (WINDOWS ERROR 1306)"), "Revision error code alone was not recognized.");
                    Assert(!FullscreenController.FailureRequiresRestart("Windows error 13060"), "A different error code was classified as revision mismatch.");
                });
                test("Readiness accepts normal or reused hooks only for the intended process", delegate
                {
                    bool reused;
                    Assert(FullscreenController.TryParseReady("READY 572", 572, out reused) && !reused, "Normal readiness was rejected or reported as reused.");
                    Assert(FullscreenController.TryParseReady("READY 572 REUSED", 572, out reused) && reused, "Compatible resident hooks were not recognized.");
                    Assert(!FullscreenController.TryParseReady("READY 573 REUSED", 572, out reused), "Another process's acknowledgement was accepted.");
                    Assert(!FullscreenController.TryParseReady("READY REUSED", 572, out reused), "Missing process identity was accepted.");
                    Assert(!FullscreenController.TryParseReady("READY 0", 0, out reused), "An empty process identity was accepted.");
                    Assert(!FullscreenController.TryParseReady("READY -572 REUSED", 572, out reused), "Invalid process identity was accepted.");
                    Assert(!FullscreenController.TryParseReady("READY 572 REUSED EXTRA", 572, out reused), "Unknown readiness fields were accepted.");
                    Assert(!FullscreenController.TryParseReady("READY 572 INCOMPATIBLE", 572, out reused), "Unrecognized resident state was accepted.");
                });
                test("Readiness distinguishes virtual monitor and legacy capabilities", delegate
                {
                    bool reused;
                    uint capabilities;
                    Assert(FullscreenController.TryParseReady("READY 572 CAPS 5", 572, out reused, out capabilities) && !reused && capabilities == 5,
                        "Virtual monitor capabilities were rejected.");
                    Assert(FullscreenController.TryParseReady("READY 572 REUSED CAPS 3", 572, out reused, out capabilities) && reused && capabilities == 3,
                        "Legacy resident capabilities were rejected.");
                    Assert(FullscreenController.TryParseReady("READY 572 REUSED", 572, out reused, out capabilities) && reused && capabilities == 0,
                        "Legacy helpers without capability details lost compatibility.");
                    string[] malformed = { "READY 572 CAPS", "READY 572 CAPS 1", "READY 572 CAPS 4", "READY 572 CAPS 0", "READY 572 CAPS -5",
                        "READY 572 CAPS 5 EXTRA", "READY 572 REUSED CAPS 5 EXTRA", "READY 572 CAPS 5 REUSED", "READY 572 CAPS 3 CAPS 5" };
                    foreach (string message in malformed)
                        Assert(!FullscreenController.TryParseReady(message, 572, out reused, out capabilities), "Invalid capabilities were accepted: " + message);
                });
                test("Compatible resident readiness reports reconnection without claiming a DLL upgrade", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Example.exe", ProcessId = 572 };
                    string reconnected = FullscreenController.FormatReady(target, true, true, true);
                    Assert(reconnected.Contains("Reconnected") && reconnected.Contains("Example.exe") && reconnected.Contains("existing compatible hooks"), "Reconnection did not explain the successful takeover.");
                    Assert(reconnected.IndexOf("restart", StringComparison.OrdinalIgnoreCase) < 0 && reconnected.IndexOf("upgraded", StringComparison.OrdinalIgnoreCase) < 0,
                        "Compatible reuse incorrectly requires a restart or claims the resident DLL was upgraded.");
                    Assert(FullscreenController.FormatReady(target, false, false, true).Contains("control is paused"), "Late reused readiness lost pause state.");
                    Assert(FullscreenController.FormatReady(target, true, true, false, 5) == "Automatic virtual monitor and window handling ready for Example.exe.", "Automatic readiness did not describe virtual monitor handling.");
                    Assert(FullscreenController.FormatReady(target, false, true, false, 5) == "Virtual monitor and window handling ready for Example.exe.", "Manual readiness did not describe virtual monitor handling.");
                    Assert(FullscreenController.FormatReady(target, false, true, true, 3).Contains("legacy fullscreen engine"), "Resident legacy capabilities were presented as the current engine.");
                });
                test("Ordinary helper failures retain their reason and offer normal retry", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Example.exe", ProcessId = 90 };
                    string message = FullscreenController.FormatFailure(target, "ERROR Requested operation is unavailable (Windows error 50)");
                    Assert(message.Contains("Example.exe (PID 90)") && message.Contains("Windows error 50"), "Failure lost its identity or reason.");
                    Assert(message.Contains("Force fullscreen") && !FullscreenController.FailureRequiresRestart(message), "Ordinary failure got incorrect restart guidance.");
                    Assert(!FullscreenController.FailureIsAccessDenied(message) && !FullscreenController.FailureRequiresElevation(message), "An unrelated error received permission guidance.");
                    Assert(!message.Contains(": ERROR "), "Raw protocol prefix leaked into the user message.");
                });
                test("Confirmed elevation mismatch directs the user to administrator restart", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 17752 };
                    string native = "ERROR Target application is elevated but DisplayWeave is not. Restart DisplayWeave as administrator (Windows error 740)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureRequiresElevation(native), "Confirmed privilege mismatch was not recognized.");
                    Assert(message.Contains("Game.exe (PID 17752)") && message.Contains("Target application is elevated") && message.Contains("Windows error 740"), "Mismatch guidance lost the application or native evidence.");
                    Assert(message.Contains("Status & help > Restart as administrator") && message.Contains("Windows prompt"), "Mismatch guidance does not identify the available administrator action.");
                    Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("Close and reopen this application"), "Mismatch suggests a retry or restarting the target instead of DisplayWeave.");
                    Assert(FullscreenController.FailureRequiresElevation("ERROR Failed (WINDOWS ERROR 740)"), "Elevation error code alone was not recognized.");
                    Assert(FullscreenController.FailureRequiresElevation("ERROR Restart DisplayWeave as administrator before retrying"), "Native elevation instruction alone was not recognized.");
                    Assert(!FullscreenController.FailureRequiresElevation("Windows error 7400") &&
                        !FullscreenController.FailureRequiresElevation("Windows error 740x") &&
                        !FullscreenController.FailureRequiresElevation("Windows error 5") &&
                        !FullscreenController.FailureRequiresElevation(null), "Unknown or unrelated errors were reported as confirmed elevation mismatches.");
                });
                test("Access denial preserves its stage without assuming a privilege mismatch", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Example.exe", ProcessId = 90 };
                    string native = "ERROR MODULE_INSPECTION_DENIED FindResidentHooks failed (Windows error 5)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureIsAccessDenied(native), "Access-denied error code was not recognized.");
                    Assert(message.Contains("Example.exe (PID 90)") && message.Contains("MODULE_INSPECTION_DENIED") && message.Contains("FindResidentHooks") && message.Contains("Windows error 5"), "Access guidance lost the application or inspection stage.");
                    Assert(message.Contains("Windows denied access") && message.Contains("does not identify the cause"), "Access denial was not explained with its diagnostic limit.");
                    Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator") &&
                        !FullscreenController.FailureRequiresElevation(message), "Access denial alone suggests elevation or a futile retry.");
                    Assert(!FullscreenController.FailureIsAccessDenied("Windows error 50") &&
                        !FullscreenController.FailureIsAccessDenied("Windows error 500") &&
                        !FullscreenController.FailureIsAccessDenied("Windows error 5x") &&
                        !FullscreenController.FailureIsAccessDenied(null), "Access-denied detection matched a different error or missing detail.");
                });
                test("Unobserved loader module preserves observations without inventing a Windows reason", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 90 };
                    string native = "ERROR LOADER_MODULE_NOT_OBSERVED: Fullscreen component was not observed after the loader thread ended; PID 90, thread result DWORD 0x00000000, target state running, RegionHook.dll copies 0; remote loader error unavailable. Initialization was not invoked (DisplayWeave status 0x20005201)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureIsLoaderModuleNotObserved(native), "Explicit loader category was not recognized.");
                    Assert(message.Contains("Game.exe (PID 90)") && message.Contains("LOADER_MODULE_NOT_OBSERVED") &&
                        message.Contains("thread result DWORD 0x00000000") && message.Contains("target state running") &&
                        message.Contains("RegionHook.dll copies 0") && message.Contains("DisplayWeave status 0x20005201"),
                        "Loader diagnostic lost its application, raw result, process state, or application-specific status.");
                    Assert(message.Contains("Fullscreen component did not start") && message.Contains("detailed Windows failure reason is unavailable"),
                        "Missing component was not explained with its diagnostic limit.");
                    Assert(!message.Contains("Windows error") && !message.Contains("Force fullscreen") && !message.Contains("Resume handling") &&
                        !message.Contains("administrator") && !message.Contains("Close and reopen") && !message.Contains("responsive"),
                        "Unobserved module invented a Windows error or suggested an unsupported retry, elevation, or restart.");
                    Assert(!FullscreenController.FailureIsAccessDenied(message) && !FullscreenController.FailureRequiresElevation(message) &&
                        !FullscreenController.FailureRequiresRestart(message) && !FullscreenController.FailureIsInitializationTimeout(message),
                        "Unknown loader reason was misclassified as a confirmed failure cause.");
                });
                test("Loader diagnostic categories require complete tokens without inferring from error numbers", delegate
                {
                    Assert(FullscreenController.FailureIsLoaderModuleNotObserved("error loader_module_not_observed: unavailable"), "Category matching is unexpectedly case sensitive.");
                    Assert(FullscreenController.FailureIsLoaderModuleNotObserved("LOADER_MODULE_NOT_OBSERVED"), "Standalone category was not recognized.");
                    string[] unrelated = { null, "", "LOADER_MODULE_NOT_OBSERVED_OLD", "OTHER_LOADER_MODULE_NOT_OBSERVED", "XLOADER_MODULE_NOT_OBSERVED",
                        "LOADER_MODULE_NOT_OBSERVED2", "LOADER_MODULE_NOT_OBSERVEDx", "LOADER_MODULE_NOT_OBSERVEDé",
                        "ERROR The module was not observed", "ERROR LoadLibrary failed (Windows error 1114)", "ERROR thread result DWORD 0x0000045A" };
                    foreach (string detail in unrelated)
                        Assert(!FullscreenController.FailureIsLoaderModuleNotObserved(detail), "A different category or raw error was treated as unknown loader evidence: " + detail);
                    string known = FullscreenController.FormatFailure(null, "ERROR DLL initialization failed (Windows error 1114)");
                    Assert(known.Contains("Windows error 1114") && !known.Contains("detailed Windows failure reason is unavailable"),
                        "An actual supplied Windows loader error was replaced with an unknown cause.");
                });
                test("Specific loader observations survive generic helper exit and timeout messages", delegate
                {
                    string loader = "ERROR LOADER_MODULE_NOT_OBSERVED: thread result DWORD 0x00000000; target state running; remote loader error unavailable";
                    string generic = "Native helper exited before the application closed.";
                    string timeout = "ERROR Native startup timed out (Windows error 1460)";
                    Assert(FullscreenController.PreferFailure(generic, loader) == loader && FullscreenController.PreferFailure(loader, generic) == loader,
                        "Generic helper exit erased the loader observation.");
                    Assert(FullscreenController.PreferFailure(timeout, loader) == loader && FullscreenController.PreferFailure(loader, timeout) == loader,
                        "Generic startup timeout erased the more specific loader stage.");
                    Assert(FullscreenController.PreferFailure(loader, "ERROR MODULE_INSPECTION_DENIED (Windows error 5)").Contains("MODULE_INSPECTION_DENIED"),
                        "A real inspection access failure lost precedence to an unknown loader cause.");
                });
                test("Startup timeout preserves the native stage without inventing an access failure", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 9192 };
                    string native = "ERROR Native fullscreen startup timed out: no bootstrap callback acknowledged (Windows error 1460)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(FullscreenController.FailureIsInitializationTimeout(native), "Startup timeout was not recognized.");
                    Assert(message.Contains("Game.exe (PID 9192)") && message.Contains("no bootstrap callback acknowledged") && message.Contains("Windows error 1460"), "Timeout guidance lost its application or native stage.");
                    Assert(message.Contains("did not finish starting") && message.Contains("window open and responsive"), "Timeout guidance does not explain unfinished startup or window responsiveness.");
                    Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator") &&
                        !message.Contains("protected") && !message.Contains("incompatible") && !message.Contains("Close and reopen"), "Timeout guidance invented a cause or requires an unsupported restart.");
                    Assert(!FullscreenController.FailureRequiresElevation(message) && !FullscreenController.FailureIsAccessDenied(message) &&
                        !FullscreenController.FailureRequiresRestart(message), "Initialization timeout was classified as a permission failure or restart requirement.");
                    foreach (string stage in new string[] { "graphics initialization in progress", "waiting for the selected window callback", "waiting for fresh worker acknowledgment" })
                    {
                        string staged = FullscreenController.FormatFailure(target, "ERROR Native fullscreen startup timed out: " + stage + " (Windows error 1460)");
                        Assert(staged.Contains(stage) && !staged.Contains("Force fullscreen") && !staged.Contains("administrator"), "An observed native startup stage was lost or misclassified: " + stage);
                    }
                    Assert(FullscreenController.FailureIsInitializationTimeout("ERROR Timeout (WINDOWS ERROR 1460)"), "Timeout code matching is unexpectedly case sensitive.");
                    Assert(!FullscreenController.FailureIsInitializationTimeout("Windows error 14600") &&
                        !FullscreenController.FailureIsInitializationTimeout("Windows error 1460x") &&
                        !FullscreenController.FailureIsInitializationTimeout("Windows error 740") &&
                        !FullscreenController.FailureIsInitializationTimeout("Windows error 5") &&
                        !FullscreenController.FailureIsInitializationTimeout(null), "Timeout classification matched a different error or missing detail.");
                });
                test("Explicit native restart instructions remain authoritative for a timeout", delegate
                {
                    ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Example.exe", ProcessId = 90 };
                    string native = "ERROR Restart the target application; resident startup cannot continue (Windows error 1460)";
                    string message = FullscreenController.FormatFailure(target, native);
                    Assert(message.Contains("Close and reopen") && !message.Contains("check the reported startup stage"), "Generic timeout advice overrode an explicit native restart requirement.");
                    string staged = "ERROR Native fullscreen startup timed out: graphics initialization in progress (Windows error 1460)";
                    string generic = "Native helper exited before the application closed.";
                    Assert(FullscreenController.PreferFailure(generic, staged) == staged && FullscreenController.PreferFailure(staged, generic) == staged,
                        "Generic helper exit erased the startup stage.");
                    Assert(FullscreenController.PreferFailure(staged, native) == native, "Explicit native restart instruction lost to a timeout.");
                });
                test("Confirmed permission diagnostics survive less specific helper output", delegate
                {
                    string elevation = "ERROR Restart DisplayWeave as administrator (Windows error 740)";
                    string denied = "ERROR MODULE_INSPECTION_DENIED (Windows error 5)";
                    string generic = "Native helper exited before the application closed.";
                    Assert(FullscreenController.PreferFailure(denied, elevation) == elevation, "Confirmed mismatch did not replace generic access denial.");
                    Assert(FullscreenController.PreferFailure(elevation, denied) == elevation &&
                        FullscreenController.PreferFailure(elevation, generic) == elevation, "Later output erased the confirmed mismatch.");
                    Assert(FullscreenController.PreferFailure(generic, denied) == denied &&
                        FullscreenController.PreferFailure(denied, generic) == denied, "Generic exit erased the access-denied reason.");
                    string restart = "ERROR Restart the target application (Windows error 1306)";
                    Assert(FullscreenController.PreferFailure(elevation, restart) == restart, "Existing target restart precedence changed.");
                });
                test("Specific helper diagnostics win over generic exit messages", delegate
                {
                    string specific = "ERROR Restart the target application to use this DisplayWeave version (Windows error 1306)";
                    string generic = "Native helper exited before the application closed.";
                    Assert(FullscreenController.PreferFailure(generic, specific) == specific, "Late native output was discarded.");
                    Assert(FullscreenController.PreferFailure(specific, generic) == specific, "Generic exit erased the restart reason.");
                    Assert(FullscreenController.PreferFailure("ERROR Cannot map application controls", specific) == specific, "Restart reason lost to a less specific error.");
                    Assert(FullscreenController.PreferFailure(generic, "ERROR Access denied") == "ERROR Access denied", "Generic exit hid a native access error.");
                });
                test("Per-app failure records survive unrelated readiness and clear only the relevant identity", delegate
                {
                    FullscreenFailureHistory history = new FullscreenFailureHistory();
                    ApplicationIdentity first = new ApplicationIdentity { ProcessId = 10, CreationTime = 100, Handle = new IntPtr(1) };
                    ApplicationIdentity second = new ApplicationIdentity { ProcessId = 20, CreationTime = 200, Handle = new IntPtr(2) };
                    ApplicationIdentity anotherWindow = new ApplicationIdentity { ProcessId = 20, CreationTime = 200, Handle = new IntPtr(3) };
                    ApplicationIdentity reusedPid = new ApplicationIdentity { ProcessId = 20, CreationTime = 300, Handle = new IntPtr(2) };
                    history.Record(first, "First app failure"); history.Record(second, "Second app failure");
                    history.ClearTarget(reusedPid);
                    Assert(history.Latest == "Second app failure" && history.For(first) == "First app failure", "Unrelated readiness erased a failure.");
                    history.ClearTarget(second);
                    Assert(history.Latest == "First app failure", "Clearing the newest failure hid another unresolved failure.");
                    history.Record(second, "Second app failed again"); history.Record(anotherWindow, "Other window failed");
                    history.ClearProcess(reusedPid);
                    Assert(history.Count == 3, "PID reuse cleared an unrelated process lifetime.");
                    history.ClearProcess(second);
                    Assert(history.Count == 1 && history.Latest == "First app failure", "Explicit release failed to clear this process's windows.");
                    history.ClearTarget(first);
                    Assert(history.Latest == String.Empty && history.Count == 0, "Destroyed target did not clear its warning.");
                });
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
            finally
            {
                // Delete only this run's uniquely named, flat test directory.
                foreach (string path in Directory.GetFiles(temporary)) File.Delete(path);
                Directory.Delete(temporary);
            }
            report.AppendLine(); report.AppendLine(passed + " passed; " + failed + " failed.");
            File.WriteAllText(fullReportPath, report.ToString(), new UTF8Encoding(false));
            Console.Write(report.ToString());
            return failed == 0 ? 0 : 1;
        }

        private static ApplicationRule Rule(string path, string windowClass, ApplicationAction action)
        {
            return new ApplicationRule { ExecutablePath = path, WindowClass = windowClass, DisplayName = "Example", Action = action,
                MonitorDevice = "\\\\.\\DISPLAY1", ZoneId = Guid.NewGuid().ToString("N") };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
