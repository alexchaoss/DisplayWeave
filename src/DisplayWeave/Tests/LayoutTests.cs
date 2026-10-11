using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    public static class LayoutTests
    {
        public static int Run(string reportPath)
        {
            string fullReportPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullReportPath);
            Directory.CreateDirectory(directory);
            string testDirectory = Path.Combine(directory, "layout-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            StringBuilder report = new StringBuilder();
            int passed = 0;
            int failed = 0;
            Action<string, Action> test = delegate(string name, Action body)
            {
                try
                {
                    body();
                    ++passed;
                    report.AppendLine("PASS " + name);
                }
                catch (Exception exception)
                {
                    ++failed;
                    report.AppendLine("FAIL " + name + ": " + exception.GetType().Name + ": " + exception.Message);
                }
            };

            try
            {
                test("Presets cover odd ultrawide resolutions with shared edges", delegate
                {
                    Rectangle[] areas = { new Rectangle(0, 0, 3441, 1441),
                        new Rectangle(-5121, -300, 5121, 2161), new Rectangle(200, -1440, 3440, 1440) };
                    foreach (Rectangle area in areas)
                        for (int kind = 0; kind < 3; ++kind)
                        {
                            List<Zone> zones = LayoutSettings.Preset(kind);
                            int edge = area.Left;
                            int totalWidth = 0;
                            foreach (Zone zone in zones)
                            {
                                Rectangle pixels = zone.ToPixels(area);
                                Assert(pixels.Left == edge, "Gap or overlap at a split edge.");
                                Assert(pixels.Top == area.Top && pixels.Height == area.Height, "Incorrect height.");
                                totalWidth += pixels.Width;
                                edge = pixels.Right;
                            }
                            Assert(edge == area.Right && totalWidth == area.Width, "Incomplete width coverage.");
                        }
                    Rectangle odd = new Rectangle(-3441, -180, 3441, 1441);
                    Assert(LayoutSettings.Preset(0)[0].ToPixels(odd).Width == 1721, "Half-pixel rounding differs.");
                });

                test("Four-way preset covers an odd resolution without overlap", delegate
                {
                    Rectangle area = new Rectangle(-3441, -1441, 3441, 1441);
                    List<Zone> zones = LayoutSettings.Preset(3);
                    long covered = 0;
                    for (int i = 0; i < zones.Count; ++i)
                    {
                        Rectangle pixels = zones[i].ToPixels(area);
                        Assert(area.Contains(pixels), "Zone is outside its display.");
                        covered += (long)pixels.Width * pixels.Height;
                        for (int j = 0; j < i; ++j)
                        {
                            Rectangle intersection = Rectangle.Intersect(pixels, zones[j].ToPixels(area));
                            Assert(intersection.Width == 0 || intersection.Height == 0, "Quadrants overlap.");
                        }
                    }
                    Assert(covered == (long)area.Width * area.Height, "Missing pixels.");
                });

                test("Pixel normalization round-trips with negative monitor origins", delegate
                {
                    Rectangle area = new Rectangle(-3440, -120, 3440, 1440);
                    Rectangle[] regions = { new Rectangle(-1500, 30, 1234, 777),
                        new Rectangle(-3440, -120, 1, 1), new Rectangle(-1, 1319, 1, 1), area };
                    foreach (Rectangle region in regions)
                        Assert(Zone.FromPixels("Round trip", region, area).ToPixels(area) == region,
                            "Pixel normalization changed the rectangle.");
                    Rectangle clipped = Zone.FromPixels("Clipped", new Rectangle(-3500, -200, 200, 200), area).ToPixels(area);
                    Assert(clipped == new Rectangle(-3440, -120, 140, 120), "Clipping is incorrect.");
                });

                test("Tiny and invalid zones remain at least one pixel within the display", delegate
                {
                    Rectangle area = new Rectangle(-100, -50, 17, 9);
                    Zone[] zones = { new Zone("Tiny", 1, 1, 0, 0),
                        new Zone("Outside", -2, 4, 9, -1),
                        new Zone("Invalid", Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, Double.NaN) };
                    foreach (Zone zone in zones)
                    {
                        Rectangle pixels = zone.ToPixels(area);
                        Assert(pixels.Width >= 1 && pixels.Height >= 1 && area.Contains(pixels), "Invalid pixel bounds.");
                    }
                    Assert(new Zone().ToPixels(new Rectangle(-1, -1, 1, 1)) == new Rectangle(-1, -1, 1, 1),
                        "Single-pixel displays must work.");
                    bool rejected = false;
                    try { new Zone().ToPixels(Rectangle.Empty); }
                    catch (ArgumentOutOfRangeException) { rejected = true; }
                    Assert(rejected, "Empty display should be rejected.");
                });

                test("Save and load preserve valid Cartesian geometry, IDs, order and options", delegate
                {
                    string path = Path.Combine(testDirectory, "roundtrip.xml");
                    LayoutSettings original = new LayoutSettings();
                    original.MonitorDevice = @"\\.\DISPLAY2";
                    original.UseWorkArea = false;
                    original.Borderless = true;
                    original.GridStepPixels = 75;
                    original.Zones = new List<Zone> { new Zone("Right", 0.75, 0, 0.25, 1),
                        new Zone("Editor & tools", 0, 0, 0.25, 1), new Zone("Center", 0.25, 0, 0.5, 1) };
                    original.Save(path);
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    Assert(loaded.Zones.Count == 3, "Loaded zones were appended to constructor defaults.");
                    Assert(loaded.MonitorDevice == original.MonitorDevice && !loaded.UseWorkArea && loaded.Borderless,
                        "Options failed to round-trip.");
                    Assert(loaded.GridStepPixels == 75, "Grid spacing failed to round-trip.");
                    for (int i = 0; i < original.Zones.Count; ++i)
                    {
                        Zone a = original.Zones[i];
                        Zone b = loaded.Zones[i];
                        Assert(a.Id == b.Id && a.Name == b.Name && a.X == b.X && a.Y == b.Y &&
                            a.Width == b.Width && a.Height == b.Height, "Zone failed to round-trip.");
                    }
                    loaded.Zones = LayoutSettings.Preset(3);
                    loaded.Save(path);
                    Assert(LayoutSettings.Load(path).Zones.Count == 4, "Replacing settings failed.");
                    Assert(Directory.GetFiles(testDirectory, "*.tmp*").Length == 0, "Save left temporary files.");
                });

                test("Malformed, missing and DTD-based XML recover safe defaults", delegate
                {
                    string path = Path.Combine(testDirectory, "bad.xml");
                    File.WriteAllText(path, "<LayoutSettings><Zones>");
                    AssertDefaults(LayoutSettings.Load(path));
                    AssertDefaults(LayoutSettings.Load(Path.Combine(testDirectory, "missing.xml")));
                    File.WriteAllText(path, "<!DOCTYPE LayoutSettings [<!ENTITY payload 'unexpected'>]>" +
                        "<LayoutSettings><MonitorDevice>&payload;</MonitorDevice></LayoutSettings>");
                    AssertDefaults(LayoutSettings.Load(path));
                    File.WriteAllText(path, "<LayoutSettings><Zones /></LayoutSettings>");
                    AssertDefaults(LayoutSettings.Load(path));
                });

                test("Unsafe nonfinite or oversized zone collections migrate to a partition", delegate
                {
                    string path = Path.Combine(testDirectory, "validation.xml");
                    StringBuilder xml = new StringBuilder("<LayoutSettings><Zones>");
                    for (int i = 0; i < 14; ++i)
                        xml.Append("<Zone><Name> </Name><X>NaN</X><Y>INF</Y><Width>-3</Width><Height>INF</Height></Zone>");
                    xml.Append("</Zones></LayoutSettings>");
                    File.WriteAllText(path, xml.ToString());
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    AssertDefaults(loaded);
                    Assert(loaded.LayoutWasRepaired, "Invalid source should report migration.");
                    Rectangle area = new Rectangle(-2000, 0, 2000, 1000);
                    foreach (Zone zone in loaded.Zones)
                    {
                        Assert(!String.IsNullOrWhiteSpace(zone.Name), "Name was not repaired.");
                        Assert(!Double.IsNaN(zone.X) && !Double.IsInfinity(zone.Y), "Nonfinite position survived.");
                        Assert(zone.Width > 0 && zone.Height > 0 && zone.X + zone.Width <= 1 && zone.Y + zone.Height <= 1,
                            "Invalid normalized dimensions survived.");
                        Assert(area.Contains(zone.ToPixels(area)), "Repaired zone extends outside display.");
                    }
                });

                test("Oversized settings are rejected", delegate
                {
                    string path = Path.Combine(testDirectory, "oversized.xml");
                    File.WriteAllText(path, "<LayoutSettings><MonitorDevice>" + new String('a', 1024 * 1024 + 1) +
                        "</MonitorDevice></LayoutSettings>");
                    AssertDefaults(LayoutSettings.Load(path));
                });

                test("Legacy overlapping and gapped layouts are replaced before fullscreen use", delegate
                {
                    string path = Path.Combine(testDirectory, "legacy.xml");
                    LayoutSettings settings = new LayoutSettings();
                    settings.Zones = new List<Zone> { new Zone("Overlap A", 0, 0, 0.7, 1),
                        new Zone("Overlap B", 0.4, 0, 0.6, 1) };
                    string firstId = settings.Zones[0].Id;
                    string secondId = settings.Zones[1].Id;
                    settings.Save(path);
                    AssertDefaults(settings);
                    Assert(settings.Zones[0].Id != firstId && settings.Zones[1].Id != secondId,
                        "Migration reused IDs from invalid source cells.");
                    AssertPartition(settings.Zones, new Rectangle(-3441, -50, 3441, 1441));
                    File.WriteAllText(path, "<LayoutSettings><Zones>" +
                        "<Zone><Name>Left</Name><X>0</X><Y>0</Y><Width>0.4</Width><Height>1</Height></Zone>" +
                        "<Zone><Name>Right</Name><X>0.6</X><Y>0</Y><Width>0.4</Width><Height>1</Height></Zone>" +
                        "</Zones></LayoutSettings>");
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    AssertDefaults(loaded);
                    RulerLayout model = new RulerLayout(loaded, new Rectangle(0, 0, 1200, 800));
                    Assert(model.WasRepaired, "Load migration was not passed to the model.");
                    AssertPartition(model.Zones, model.Area);
                    settings.Zones = new List<Zone> { new Zone("First", 0, 0, 0.75, 1),
                        new Zone("Second", 0.5, 0, 0.5, 1) };
                    model = new RulerLayout(settings, new Rectangle(0, 0, 1200, 800));
                    Assert(model.WasRepaired, "Direct construction did not report overlapping legacy geometry.");
                    AssertPartition(model.Zones, model.Area);
                });

                test("Ruler presets tile odd monitors and snap only interior edges", delegate
                {
                    Rectangle area = new Rectangle(-3441, -1441, 3441, 1441);
                    RulerLayout model = new RulerLayout(new LayoutSettings(), area);
                    for (int kind = 0; kind < 4; ++kind)
                    {
                        model.ApplyPreset(kind);
                        AssertPartition(model.Zones, area);
                        foreach (int pin in model.VerticalPins) Assert(pin > 0 && pin < area.Width && pin % 100 == 0, "Invalid width pin.");
                        foreach (int pin in model.HorizontalPins) Assert(pin > 0 && pin < area.Height && pin % 100 == 0, "Invalid height pin.");
                    }
                    bool readOnly = false;
                    try { model.VerticalPins.Add(7); }
                    catch (NotSupportedException) { readOnly = true; }
                    Assert(readOnly, "External callers can corrupt ruler pins.");
                });

                test("Divider collisions and edge drags clamp without crossing neighbors", delegate
                {
                    LayoutSettings settings = new LayoutSettings();
                    RulerLayout model = new RulerLayout(settings, new Rectangle(0, 0, 1200, 800));
                    model.ApplyPreset(1);
                    string error;
                    Assert(!model.TryAddDivider(true, 402, out error) && error.Length > 0, "Duplicate grid line was accepted.");
                    Assert(!model.TryAddDivider(true, -100, out error), "Outside divider was accepted.");
                    Assert(model.TryMoveDivider(true, 0, 5000, out error), error);
                    Assert(model.VerticalPins[0] == 700 && model.VerticalPins[1] == 800, "Moved pin crossed its neighbor.");
                    Assert(model.TryMoveDivider(true, 0, -5000, out error) && model.VerticalPins[0] == 100, "Negative drag did not clamp.");
                    Assert(model.TrySetGridStep(1, out error), error);
                    Assert(!model.TryAddDivider(true, 101, out error), "A one-pixel sliver was accepted on a normal monitor.");
                    Assert(model.TryMoveDivider(true, 0, -1, out error) && model.VerticalPins[0] == 32, "Minimum cell width was not enforced.");
                    Assert(!model.TryMoveDivider(true, -1, 500, out error), "Invalid divider index was accepted.");
                    AssertPartition(model.Zones, model.Area);
                });

                test("Grid spacing changes preserve IDs and reject impossible spacing atomically", delegate
                {
                    LayoutSettings settings = new LayoutSettings();
                    RulerLayout model = new RulerLayout(settings, new Rectangle(-1200, -100, 1200, 800));
                    string error;
                    Assert(model.TryAddDivider(true, 300, out error), error);
                    List<string> ids = ZoneIds(model.Zones);
                    Assert(model.TrySetGridStep(350, out error), error);
                    Assert(model.VerticalPins[0] == 350 && model.VerticalPins[1] == 700, "Pins were not resnapped.");
                    AssertIds(model.Zones, ids);
                    Assert(!model.TrySetGridStep(100000, out error), "Impossible spacing was accepted.");
                    Assert(model.GridStep == 350 && model.VerticalPins[0] == 350 && model.VerticalPins[1] == 700,
                        "Failed spacing changed the model.");
                    Assert(!model.TrySetGridStep(0, out error) && !model.TrySetGridStep(100001, out error), "Invalid spacing bounds accepted.");
                    AssertIds(model.Zones, ids);
                    AssertPartition(model.Zones, model.Area);
                });

                test("Splits and merges invalidate only affected cell IDs", delegate
                {
                    RulerLayout model = new RulerLayout(new LayoutSettings(), new Rectangle(0, 0, 1200, 800));
                    string leftId = model.Zones[0].Id;
                    string rightId = model.Zones[1].Id;
                    string error;
                    Assert(model.TryAddDivider(true, 300, out error), error);
                    Assert(model.Zones[0].Id != leftId && model.Zones[1].Id != leftId,
                        "A split silently retained an old cell target.");
                    Assert(model.Zones[2].Id == rightId, "An unaffected cell lost its ID.");
                    List<string> ids = ZoneIds(model.Zones);
                    Assert(model.TryMoveDivider(true, 0, 400, out error), error);
                    AssertIds(model.Zones, ids);
                    Assert(model.TryMoveDivider(true, 0, 402, out error), error);
                    AssertIds(model.Zones, ids);
                    Assert(model.TryRemoveDivider(true, 0, out error), error);
                    Assert(model.Zones[0].Id != ids[0] && model.Zones[0].Id != ids[1], "A merged cell reused a source ID.");
                    Assert(model.Zones[1].Id == rightId, "Merge changed the unaffected cell ID.");
                    AssertPartition(model.Zones, model.Area);
                });

                test("Horizontal splits and merges preserve unaffected rows", delegate
                {
                    LayoutSettings settings = new LayoutSettings();
                    settings.Zones = LayoutSettings.Preset(3);
                    RulerLayout model = new RulerLayout(settings, new Rectangle(0, 0, 1200, 1200));
                    string lowerLeft = model.Zones[2].Id;
                    string lowerRight = model.Zones[3].Id;
                    string error;
                    Assert(model.TryAddDivider(false, 300, out error), error);
                    Assert(model.Zones[4].Id == lowerLeft && model.Zones[5].Id == lowerRight, "Horizontal split remapped lower row.");
                    Assert(model.TryRemoveDivider(false, 0, out error), error);
                    Assert(model.Zones[2].Id == lowerLeft && model.Zones[3].Id == lowerRight, "Horizontal merge remapped lower row.");
                    AssertPartition(model.Zones, model.Area);
                });

                test("Nine-region cap rejects a full crossing divider without mutation", delegate
                {
                    RulerLayout model = new RulerLayout(new LayoutSettings(), new Rectangle(0, 0, 1200, 1200));
                    model.ApplyPreset(3);
                    string error;
                    Assert(model.TryAddDivider(true, 300, out error), error);
                    Assert(model.TryAddDivider(false, 300, out error), error);
                    Assert(model.Zones.Count == 9, "Expected a three-by-three grid.");
                    List<string> ids = ZoneIds(model.Zones);
                    Assert(!model.TryAddDivider(true, 900, out error) && !model.TryAddDivider(false, 900, out error), "Exceeded nine regions.");
                    AssertIds(model.Zones, ids);
                    AssertPartition(model.Zones, model.Area);
                });

                test("Ruler save/load preserves pixel pins and stable IDs", delegate
                {
                    string path = Path.Combine(testDirectory, "rulers.xml");
                    Rectangle area = new Rectangle(-3441, -50, 3441, 1441);
                    LayoutSettings settings = new LayoutSettings();
                    RulerLayout model = new RulerLayout(settings, area);
                    string error;
                    Assert(model.TryAddDivider(false, 700, out error), error);
                    Assert(model.TrySetGridStep(75, out error), error);
                    List<string> ids = ZoneIds(model.Zones);
                    int widthPin = model.VerticalPins[0];
                    int heightPin = model.HorizontalPins[0];
                    settings.Save(path);
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    RulerLayout restored = new RulerLayout(loaded, area);
                    Assert(restored.GridStep == 75 && restored.VerticalPins[0] == widthPin && restored.HorizontalPins[0] == heightPin,
                        "Saved pixel grid changed on reload.");
                    AssertIds(restored.Zones, ids);
                    Assert(!restored.WasRepaired, "A valid saved ruler layout was marked invalid.");
                    AssertPartition(restored.Zones, area);
                });

                test("Tiny displays use whole-area fallback and can split with fine spacing", delegate
                {
                    RulerLayout model = new RulerLayout(new LayoutSettings(), new Rectangle(-17, -9, 17, 9));
                    Assert(model.Zones.Count == 1, "Coarse grid cannot split a tiny display.");
                    string error;
                    Assert(model.TrySetGridStep(1, out error), error);
                    Assert(model.TryAddDivider(true, 8, out error), error);
                    Assert(model.TryAddDivider(false, 4, out error), error);
                    AssertPartition(model.Zones, model.Area);
                    RulerLayout single = new RulerLayout(new LayoutSettings(), new Rectangle(0, 0, 1, 1));
                    Assert(single.Zones.Count == 1, "Single pixel must remain one cell.");
                    AssertPartition(single.Zones, single.Area);
                });

                test("Save repairs null zones and XML-invalid text", delegate
                {
                    string path = Path.Combine(testDirectory, "repair.xml");
                    LayoutSettings settings = new LayoutSettings();
                    settings.MonitorDevice = "Display\u0000name";
                    settings.Zones = new List<Zone> { null, new Zone("Valid", 0, 0, 1, 1) };
                    settings.Zones[1].Name = "Custom\u0000 name";
                    settings.Save(path);
                    LayoutSettings loaded = LayoutSettings.Load(path);
                    Assert(loaded.Zones.Count == 1 && loaded.Zones[0].Name == "Custom name", "Text repair failed.");
                    Assert(loaded.MonitorDevice == "Displayname", "Monitor text repair failed.");
                    settings.Zones = null;
                    settings.Save(path);
                    AssertDefaults(LayoutSettings.Load(path));
                });
            }
            finally
            {
                // This unique test directory contains only files created by this run. Never recurse.
                try
                {
                    foreach (string path in Directory.GetFiles(testDirectory)) File.Delete(path);
                    Directory.Delete(testDirectory);
                }
                catch (IOException exception)
                {
                    ++failed;
                    report.AppendLine("FAIL temporary test cleanup: " + exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ++failed;
                    report.AppendLine("FAIL temporary test cleanup: " + exception.Message);
                }
            }

            report.AppendLine();
            report.AppendLine(String.Format("{0} passed; {1} failed.", passed, failed));
            File.WriteAllText(fullReportPath, report.ToString(), new UTF8Encoding(false));
            Console.Write(report.ToString());
            return failed == 0 ? 0 : 1;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertDefaults(LayoutSettings settings)
        {
            Assert(settings.Zones != null && settings.Zones.Count == 2, "Expected default zones.");
            Assert(settings.Zones[0].X == 0 && settings.Zones[0].Width == 0.5 &&
                settings.Zones[1].X == 0.5 && settings.Zones[1].Width == 0.5, "Defaults have wrong geometry.");
        }

        private static List<string> ZoneIds(IList<Zone> zones)
        {
            List<string> ids = new List<string>();
            foreach (Zone zone in zones) ids.Add(zone.Id);
            return ids;
        }

        private static void AssertIds(IList<Zone> zones, IList<string> ids)
        {
            Assert(zones.Count == ids.Count, "Cell count changed.");
            for (int i = 0; i < ids.Count; ++i) Assert(zones[i].Id == ids[i], "Cell ID changed at " + i + ".");
        }

        private static void AssertPartition(IList<Zone> zones, Rectangle area)
        {
            Assert(zones.Count > 0 && zones.Count <= 9, "Invalid cell count.");
            long total = 0;
            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < zones.Count; ++i)
            {
                Rectangle region = zones[i].ToPixels(area);
                Assert(region.Width > 0 && region.Height > 0 && area.Contains(region), "Cell is outside the display.");
                Assert(ids.Add(zones[i].Id), "Duplicate cell ID.");
                total += (long)region.Width * region.Height;
                for (int j = 0; j < i; ++j)
                {
                    Rectangle overlap = Rectangle.Intersect(region, zones[j].ToPixels(area));
                    Assert(overlap.Width == 0 || overlap.Height == 0, "Cells overlap.");
                }
            }
            Assert(total == (long)area.Width * area.Height, "Cells leave a gap.");
        }
    }
}
