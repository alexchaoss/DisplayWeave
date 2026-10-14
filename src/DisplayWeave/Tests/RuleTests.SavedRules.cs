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
        // Saved rules and regions: persistence, matching, region identity and resolution, and the helper's layout text.
        private static void SavedRuleAndRegionTests(Action<string, Action> test, string temporary)
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
            test("A rule file held open for a moment is read once it is free, not taken as unreadable", delegate
            {
                string path = Path.Combine(temporary, "locked.xml"), warning;
                ApplicationRules rules = new ApplicationRules();
                rules.Put(Rule("C:\\Games\\Example.exe", "GameWindow", ApplicationAction.ForceFullscreen)); rules.Save(path);
                FileStream holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { System.Threading.Thread.Sleep(200); holder.Dispose(); });
                Assert(ApplicationRules.Load(path, out warning).Rules.Count == 1 && warning.Length == 0, "A briefly locked rule file was read as unreadable: " + warning);
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
                    Assert(RegionGeometry.TryResolveRegionInArea(zone.Id, zones, area, out resolved), "A valid odd-sized grid was rejected.");
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
                Assert(!RegionGeometry.TryResolveRegionInArea(first, zones, new Rectangle(0, 0, 1920, 1080), out resolved), "A collapsed narrow cell overlapped its neighbor.");
                Assert(resolved.IsEmpty && zones[0].Id == first && zones[1].Id == second, "Failed resolution mutated the layout or returned a target.");
                zones = LayoutSettings.Preset(0);
                Assert(!RegionGeometry.TryResolveRegionInArea(zones[0].Id, zones, new Rectangle(0, 0, 1, 10), out resolved), "Two cells fit into one pixel.");
            });
            test("Saved regions reject gaps and overlaps across the whole layout", delegate
            {
                Rectangle area = new Rectangle(0, 0, 1000, 800), resolved;
                List<Zone> gaps = new List<Zone> { new Zone("Left", 0, 0, 0.4, 1), new Zone("Right", 0.6, 0, 0.4, 1) };
                Assert(!RegionGeometry.TryResolveRegionInArea(gaps[0].Id, gaps, area, out resolved), "A gapped layout was resolved.");
                List<Zone> overlap = new List<Zone> { new Zone("Left", 0, 0, 0.6, 1), new Zone("Right", 0.4, 0, 0.6, 1) };
                Assert(!RegionGeometry.TryResolveRegionInArea(overlap[0].Id, overlap, area, out resolved), "An overlapping layout was resolved.");
            });
            test("Saved regions reject invalid geometry, duplicate IDs, and missing targets", delegate
            {
                Rectangle area = new Rectangle(0, 0, 1000, 800), resolved;
                List<Zone> zones = LayoutSettings.Preset(0);
                Assert(!RegionGeometry.TryResolveRegionInArea(Guid.NewGuid().ToString("N"), zones, area, out resolved), "Missing target was redirected to another cell.");
                zones[1].Id = zones[0].Id;
                Assert(!RegionGeometry.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "An ambiguous duplicate ID was resolved.");
                zones = LayoutSettings.Preset(0); zones[0].X = -0.1;
                Assert(!RegionGeometry.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Outside geometry was silently clamped.");
                zones = LayoutSettings.Preset(0); zones[1].Width = Double.NaN;
                Assert(!RegionGeometry.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Nonfinite geometry in another cell was ignored.");
                zones = LayoutSettings.Preset(0); zones[1].Height = 0;
                Assert(!RegionGeometry.TryResolveRegionInArea(zones[0].Id, zones, area, out resolved), "Zero-area geometry was expanded instead of rejected.");
            });
            test("Virtual monitor layout includes every cell at negative coordinates", delegate
            {
                List<Zone> zones = LayoutSettings.Preset(3);
                Rectangle area = new Rectangle(-1921, -1081, 1921, 1081);
                List<Rectangle> regions;
                Assert(RegionGeometry.TryResolveLayoutInArea(zones, area, out regions), "Valid monitor partition was rejected.");
                Assert(regions.Count == 4, "Only the selected region was supplied to the virtual monitor facade.");
                string encoded;
                Assert(HelperProtocol.TrySerializeRegions(regions, regions[2], out encoded), "Valid region list was not serialized.");
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
                    Assert(HelperProtocol.TrySerializeRegions(new Rectangle[] { top, bottom }, bottom, out encoded), "Portrait layout was rejected.");
                    Assert(encoded == "-1080,0,1080,960;-1080,960,1080,960", "Culture changed the helper wire format.");
                    Assert(encoded.IndexOf('"') < 0 && encoded.IndexOf('\n') < 0 && encoded.IndexOf(' ') < 0, "Geometry introduced command delimiters.");
                }
                finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
            });
            test("Virtual monitor serialization rejects malformed or unrelated selections atomically", delegate
            {
                Rectangle first = new Rectangle(0, 0, 100, 100);
                string encoded;
                Assert(!HelperProtocol.TrySerializeRegions(null, first, out encoded) && encoded == null, "Missing layout was accepted.");
                Assert(!HelperProtocol.TrySerializeRegions(new Rectangle[0], first, out encoded) && encoded == null, "Empty layout was accepted.");
                Assert(!HelperProtocol.TrySerializeRegions(new Rectangle[] { first, new Rectangle(50, 0, 100, 100) }, first, out encoded) && encoded == null, "Overlapping layout was accepted.");
                Assert(!HelperProtocol.TrySerializeRegions(new Rectangle[] { first, Rectangle.Empty }, first, out encoded) && encoded == null, "Empty cell was accepted.");
                Assert(!HelperProtocol.TrySerializeRegions(new Rectangle[] { first, new Rectangle(Int32.MaxValue, 100, 1, 10) }, first, out encoded) && encoded == null, "Overflowing bounds were accepted.");
                Assert(!HelperProtocol.TrySerializeRegions(new Rectangle[] { first }, new Rectangle(100, 0, 100, 100), out encoded) && encoded == null, "Selected rectangle outside the layout was accepted.");
                List<Rectangle> tooMany = new List<Rectangle>();
                for (int i = 0; i < 10; i++) tooMany.Add(new Rectangle(i * 100, 0, 100, 100));
                Assert(!HelperProtocol.TrySerializeRegions(tooMany, first, out encoded) && encoded == null, "More than nine regions were accepted.");
            });
            test("Invalid source partitions cannot become a virtual monitor layout", delegate
            {
                List<Rectangle> regions;
                List<Zone> zones = new List<Zone> { new Zone("Narrow", 0, 0, 0.000001, 1), new Zone("Rest", 0.000001, 0, 0.999999, 1) };
                Assert(!RegionGeometry.TryResolveLayoutInArea(zones, new Rectangle(0, 0, 1920, 1080), out regions) && regions == null,
                    "Pixel-collapsed regions were partially exposed to the application.");
                Assert(!RegionGeometry.TryResolveLayoutInArea(LayoutSettings.Preset(0), new Rectangle(Int32.MaxValue - 10, 0, 20, 100), out regions),
                    "A layout outside representable screen coordinates was accepted.");
            });
        }
    }
}
