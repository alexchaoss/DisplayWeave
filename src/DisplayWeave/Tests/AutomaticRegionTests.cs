using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    public static class AutomaticRegionTests
    {
        public static int Run(string reportPath)
        {
            string fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            StringBuilder report = new StringBuilder();
            int passed = 0, failed = 0;
            Action<string, Action> test = delegate(string name, Action action)
            {
                try { action(); passed++; report.AppendLine("PASS " + name); }
                catch (Exception exception) { failed++; report.AppendLine("FAIL " + name + ": " + exception.Message); }
            };
            Rectangle area = new Rectangle(-2000, -100, 2000, 1000);

            test("Greatest overlap wins on negative-origin monitors", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                string id; Rectangle region;
                Assert(AutomaticRegionPolicy.TrySelectRegion(new Rectangle(-1200, 0, 800, 500), zones, area, out id, out region), "No target.");
                Assert(id == zones[1].Id && region == zones[1].ToPixels(area), "Greatest-overlap region was not selected.");
                Assert(AutomaticRegionPolicy.TrySelectRegion(new Rectangle(-2100, 0, 200, 300), zones, area, out id, out region), "Partly visible window was rejected.");
                Assert(id == zones[0].Id, "Wrong clipped edge region.");
            });

            test("Equal overlap uses the center then stable layout order", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                string id; Rectangle region;
                Assert(AutomaticRegionPolicy.TrySelectRegion(new Rectangle(-1200, 0, 400, 500), zones, area, out id, out region), "No tie target.");
                Assert(id == zones[1].Id, "Center on the divider should belong to the right cell.");
                Assert(AutomaticRegionPolicy.TrySelectRegion(new Rectangle(-2000, 800, 2000, 500), zones, area, out id, out region), "No off-center target.");
                Assert(id == zones[0].Id, "Ties with center outside all cells should be deterministic.");
            });

            test("Automatic target follows ordinary movement and survives maximize", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                string id; Rectangle region;
                Assert(policy.Observe("10:100:1", new Rectangle(-1900, 0, 600, 400), Rectangle.Empty, false, false, area, zones, out id, out region), "Normal observation failed.");
                Assert(id == zones[0].Id, "Normal window should start left.");
                Assert(policy.Observe("10:100:1", area, Rectangle.Empty, true, false, area, zones, out id, out region), "Maximized observation failed.");
                Assert(id == zones[0].Id, "Maximize lost its original region to the monitor center.");
                Assert(policy.Observe("10:100:1", new Rectangle(-800, 0, 600, 400), Rectangle.Empty, false, false, area, zones, out id, out region), "Restored movement failed.");
                Assert(id == zones[1].Id, "Restored movement did not update the region.");
                Assert(policy.Observe("10:100:1", area, Rectangle.Empty, true, false, area, zones, out id, out region) && id == zones[1].Id,
                    "Fullscreen did not retain the moved window's target.");
            });

            test("Already-maximized apps select from their restored bounds", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                string id; Rectangle region;
                Assert(policy.Observe("first", area, new Rectangle(-1900, 0, 600, 400), true, false, area, zones, out id, out region), "Restored bounds were not usable.");
                Assert(id == zones[0].Id, "Already-maximized app selected from fullscreen bounds.");
            });

            test("Outside-monitor windows and minimized coordinates never choose a target", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                string id; Rectangle region;
                Assert(!AutomaticRegionPolicy.TrySelectRegion(new Rectangle(20, 0, 800, 500), zones, area, out id, out region), "Outside window selected a region.");
                Assert(!AutomaticRegionPolicy.TrySelectRegion(new Rectangle(0, 0, 800, 500), zones, area, out id, out region), "Touching an edge counts as overlap.");
                Assert(policy.Observe("tracked", new Rectangle(-1900, 0, 600, 400), Rectangle.Empty, false, false, area, zones, out id, out region), "Initial observation failed.");
                Assert(!policy.Observe("tracked", new Rectangle(-32000, -32000, 200, 100), Rectangle.Empty, false, true, area, zones, out id, out region), "Minimized window selected a region.");
                Assert(policy.Observe("tracked", area, Rectangle.Empty, true, false, area, zones, out id, out region) && id == zones[0].Id,
                    "Minimization erased the remembered region.");
                Assert(!policy.Observe("tracked", new Rectangle(100, 0, 600, 400), Rectangle.Empty, false, false, area, zones, out id, out region), "Outside movement was not ignored.");
                Assert(!policy.Observe("tracked", area, new Rectangle(100, 0, 600, 400), true, false, area, zones, out id, out region),
                    "An app with an outside restored location was silently brought onto this monitor.");
            });

            test("Deleted regions do not silently retarget fullscreen apps", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                string id; Rectangle region;
                Assert(policy.Observe("tracked", new Rectangle(-1900, 0, 600, 400), Rectangle.Empty, false, false, area, zones, out id, out region), "Initial observation failed.");
                zones = LayoutSettings.Preset(3);
                Assert(!policy.Observe("tracked", area, Rectangle.Empty, true, false, area, zones, out id, out region), "Deleted fullscreen target silently retargeted.");
                Assert(policy.Observe("tracked", new Rectangle(-1900, 0, 600, 300), Rectangle.Empty, false, false, area, zones, out id, out region),
                    "Restored window could not choose a new region.");
            });

            test("Process/window identity changes cannot inherit an old fullscreen target", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(0);
                AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                string id; Rectangle region;
                policy.Observe("10:100:1", new Rectangle(-1900, 0, 600, 300), Rectangle.Empty, false, false, area, zones, out id, out region);
                Assert(policy.Observe("10:200:1", area, new Rectangle(-800, 0, 600, 300), true, false, area, zones, out id, out region) && id == zones[1].Id,
                    "Reused PID/window inherited another process lifetime's region.");
                policy.Forget("10:100:1");
                Assert(policy.Observe("10:100:1", area, new Rectangle(-800, 0, 600, 300), true, false, area, zones, out id, out region) && id == zones[1].Id,
                    "Explicit restore did not clear its observation.");
                policy.Retain(new HashSet<string>());
                Assert(policy.Observe("10:200:1", area, new Rectangle(-1900, 0, 600, 300), true, false, area, zones, out id, out region) && id == zones[0].Id,
                    "Closed windows were retained.");
            });

            test("Pause, restore, Ignore and Force take precedence over automatic mode", delegate
            {
                Assert(AutomaticRegionPolicy.Decide(true, false, false, null, false) == RegionControlMode.Automatic, "Unassigned app is not automatic.");
                Assert(AutomaticRegionPolicy.Decide(false, false, false, null, false) == RegionControlMode.None, "Disabled automatic setting ignored.");
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.Ignore, false) == RegionControlMode.None, "Ignore rule did not win.");
                Assert(AutomaticRegionPolicy.Decide(false, false, false, ApplicationAction.ForceFullscreen, true) == RegionControlMode.Force, "Valid force rule depends on automatic setting.");
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.ForceFullscreen, false) == RegionControlMode.None, "Broken force rule silently became automatic.");
                Assert(AutomaticRegionPolicy.Decide(true, true, false, ApplicationAction.ForceFullscreen, true) == RegionControlMode.None, "Pause lost to force.");
                Assert(AutomaticRegionPolicy.Decide(true, false, true, ApplicationAction.ForceFullscreen, true) == RegionControlMode.None, "Restore suppression lost to force.");
                Assert(AutomaticRegionPolicy.Decide(true, false, true, null, false) == RegionControlMode.None, "Restore suppression lost to automatic.");
            });

            test("Maximize-in-region rules never attach and still yield to pause and release", delegate
            {
                Assert(AutomaticRegionPolicy.Decide(false, false, false, ApplicationAction.MaximizeInRegion, true) == RegionControlMode.Maximize, "Valid maximize rule was not applied.");
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.MaximizeInRegion, false) == RegionControlMode.None, "Broken maximize rule fell back to the native component.");
                Assert(AutomaticRegionPolicy.Decide(true, true, false, ApplicationAction.MaximizeInRegion, true) == RegionControlMode.None, "Pause lost to maximize.");
                Assert(AutomaticRegionPolicy.Decide(true, false, true, ApplicationAction.MaximizeInRegion, true) == RegionControlMode.None, "Release lost to maximize.");
                Assert(FullscreenController.KeepsMaximizedFit(RegionControlMode.Maximize, false), "Rule-fitted window was returned.");
                Assert(FullscreenController.KeepsMaximizedFit(RegionControlMode.Automatic, true), "Automatic fallback window was returned while its component is unavailable.");
                Assert(FullscreenController.KeepsMaximizedFit(RegionControlMode.Automatic, false), "Automatic handling returned a window it fitted from outside.");
                Assert(FullscreenController.KeepsMaximizedFit(RegionControlMode.Force, true), "Force rule without a component lost its fallback.");
                Assert(!FullscreenController.KeepsMaximizedFit(RegionControlMode.None, true) && !FullscreenController.KeepsMaximizedFit(RegionControlMode.Force, false),
                    "Ignored, released or component-handled window stayed fitted from outside.");
            });

            test("Borderless rules never attach and centered-fullscreen apps are left alone", delegate
            {
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.BorderlessInRegion, true) == RegionControlMode.Borderless, "Valid borderless rule was not applied.");
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.BorderlessInRegion, false) == RegionControlMode.None, "Broken borderless rule fell back to another mode.");
                Assert(AutomaticRegionPolicy.Decide(true, true, false, ApplicationAction.BorderlessInRegion, true) == RegionControlMode.None &&
                    AutomaticRegionPolicy.Decide(true, false, true, ApplicationAction.BorderlessInRegion, true) == RegionControlMode.None, "Pause or release lost to a borderless rule.");
                Assert(AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.CenteredFullscreen, true) == RegionControlMode.None &&
                    AutomaticRegionPolicy.Decide(true, false, false, ApplicationAction.CenteredFullscreen, false) == RegionControlMode.None, "A centered-fullscreen app was handled.");
            });

            test("Maximized bounds keep the window's invisible border overhang around the region", delegate
            {
                // A maximized window on a 7680x2100 work area, with 8 px invisible borders.
                Rectangle window = Rectangle.FromLTRB(-8, -8, 7688, 2108), frame = new Rectangle(0, 0, 7680, 2100);
                Rectangle region = new Rectangle(1900, 0, 3900, 2100);
                Assert(RegionGeometry.MaximizedBounds(window, frame, region) == Rectangle.FromLTRB(1892, -8, 5808, 2108), "Visible frame does not land on the region.");
                Assert(RegionGeometry.MaximizedBounds(window, window, region) == region, "Borderless window was not placed on the region exactly.");
                Assert(RegionGeometry.MaximizedBounds(Rectangle.FromLTRB(1892, -8, 5808, 2108), region, frame) == window, "Return to the monitor does not reproduce Windows' maximized bounds.");
            });

            test("Malformed or pixel-collapsed layouts never get automatic targets", delegate
            {
                List<Zone> zones = new List<Zone> { new Zone("A", 0, 0, 0.75, 1), new Zone("B", 0.5, 0, 0.5, 1) };
                string id; Rectangle region;
                Assert(!AutomaticRegionPolicy.TrySelectRegion(new Rectangle(-1900, 0, 600, 300), zones, area, out id, out region), "Overlap was accepted.");
                zones = LayoutSettings.Preset(0);
                Assert(!AutomaticRegionPolicy.TrySelectRegion(new Rectangle(0, 0, 1, 1), zones, new Rectangle(0, 0, 1, 1), out id, out region), "Collapsed pixel cells were accepted.");
                zones[0] = null;
                Assert(!AutomaticRegionPolicy.TrySelectRegion(area, zones, area, out id, out region), "Null zone accepted.");
            });

            test("Automatic preference defaults on for legacy layouts and persists off", delegate
            {
                string path = Path.Combine(Path.GetDirectoryName(fullPath), "automatic-settings-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(path, "<LayoutSettings><UseWorkArea>true</UseWorkArea></LayoutSettings>");
                    LayoutSettings legacy = LayoutSettings.Load(path);
                    Assert(legacy.AutomaticRegions, "Legacy settings did not enable automatic regions by default.");
                    legacy.AutomaticRegions = false;
                    legacy.Save(path);
                    Assert(!LayoutSettings.Load(path).AutomaticRegions, "Disabled preference did not survive reload.");
                    legacy.AutomaticRegions = true;
                    legacy.Save(path);
                    Assert(LayoutSettings.Load(path).AutomaticRegions, "Enabled preference did not survive reload.");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });

            test("Saving the layout preserves a live automatic fullscreen selection", delegate
            {
                string path = Path.Combine(Path.GetDirectoryName(fullPath), "automatic-target-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    LayoutSettings settings = new LayoutSettings();
                    AutomaticRegionPolicy policy = new AutomaticRegionPolicy();
                    string id; Rectangle region;
                    Assert(policy.Observe("saved", new Rectangle(-1900, 0, 600, 400), Rectangle.Empty, false, false,
                        area, settings.Zones, out id, out region), "Normal selection failed.");
                    string savedId = id;
                    settings.Save(path);
                    LayoutSettings reloaded = LayoutSettings.Load(path);
                    Assert(policy.Observe("saved", area, Rectangle.Empty, true, false, area, reloaded.Zones, out id, out region),
                        "Save/reload invalidated fullscreen target.");
                    Assert(id == savedId, "Save/reload changed the chosen cell identity.");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });

            report.AppendLine();
            report.AppendLine(passed + " passed; " + failed + " failed.");
            File.WriteAllText(fullPath, report.ToString(), new UTF8Encoding(false));
            Console.Write(report.ToString());
            return failed == 0 ? 0 : 1;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
