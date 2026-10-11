using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DisplayWeave
{
    [Serializable]
    public class Zone
    {
        // Rules refer to this identifier, so renaming or reordering never retargets an app.
        public string Id = Guid.NewGuid().ToString("N");
        public string Name;
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public Zone() : this("Zone", 0, 0, 1, 1) { }

        public Zone(string name, double x, double y, double width, double height)
        {
            Name = name;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Normalize("Zone");
        }

        public Rectangle ToPixels(Rectangle area)
        {
            CheckArea(area);
            double x, y, width, height;
            NormalizedGeometry(out x, out y, out width, out height);
            // Round shared edges, not widths, so adjacent zones stay adjacent on odd resolutions.
            int left = Math.Min(area.Width - 1, RoundEdge(x, area.Width));
            int top = Math.Min(area.Height - 1, RoundEdge(y, area.Height));
            int right = Math.Max(left + 1, RoundEdge(x + width, area.Width));
            int bottom = Math.Max(top + 1, RoundEdge(y + height, area.Height));
            return new Rectangle(area.X + left, area.Y + top, right - left, bottom - top);
        }

        public static Zone FromPixels(string name, Rectangle region, Rectangle area)
        {
            CheckArea(area);
            if (region.Width <= 0 || region.Height <= 0)
                throw new ArgumentOutOfRangeException("region", "A region must have positive dimensions.");
            double left = Clamp((double)region.X - area.X, 0, area.Width - 1);
            double top = Clamp((double)region.Y - area.Y, 0, area.Height - 1);
            double right = Clamp((double)region.X + region.Width - area.X, left + 1, area.Width);
            double bottom = Clamp((double)region.Y + region.Height - area.Y, top + 1, area.Height);
            return new Zone(name, left / area.Width, top / area.Height,
                (right - left) / area.Width, (bottom - top) / area.Height);
        }

        public override string ToString()
        {
            return String.IsNullOrWhiteSpace(Name) ? "Zone" : Name;
        }

        internal void Normalize(string fallbackName)
        {
            Guid parsed;
            if (!Guid.TryParse(Id, out parsed)) Id = Guid.NewGuid().ToString("N");
            else Id = parsed.ToString("N");
            Name = SafeText(Name, fallbackName, 64);
            double x, y, width, height;
            NormalizedGeometry(out x, out y, out width, out height);
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        internal static string SafeText(string value, string fallback, int maximumLength)
        {
            if (value == null) return fallback;
            StringBuilder result = new StringBuilder();
            string trimmed = value.Trim();
            for (int i = 0; i < trimmed.Length && result.Length < maximumLength; ++i)
            {
                char c = trimmed[i];
                if (Char.IsHighSurrogate(c) && i + 1 < trimmed.Length &&
                    Char.IsLowSurrogate(trimmed[i + 1]))
                {
                    if (result.Length + 2 > maximumLength) break;
                    result.Append(c);
                    result.Append(trimmed[++i]);
                }
                else if (XmlConvert.IsXmlChar(c) && !Char.IsSurrogate(c)) result.Append(c);
            }
            return result.Length == 0 ? fallback : result.ToString();
        }

        private void NormalizedGeometry(out double x, out double y, out double width, out double height)
        {
            const double minimum = 0.000001;
            x = Clamp(FiniteOr(X, 0), 0, 1 - minimum);
            y = Clamp(FiniteOr(Y, 0), 0, 1 - minimum);
            width = Clamp(FiniteOr(Width, 0.5), minimum, 1 - x);
            height = Clamp(FiniteOr(Height, 0.5), minimum, 1 - y);
        }

        private static double FiniteOr(double value, double fallback)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) ? fallback : value;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static int RoundEdge(double fraction, int length)
        {
            return (int)Math.Round(Clamp(fraction, 0, 1) * length, MidpointRounding.AwayFromZero);
        }

        private static void CheckArea(Rectangle area)
        {
            if (area.Width <= 0 || area.Height <= 0)
                throw new ArgumentOutOfRangeException("area", "A display area must have positive dimensions.");
        }
    }

    [Serializable]
    [XmlRoot("LayoutSettings")]
    public class LayoutSettings
    {
        private const long MaximumXmlCharacters = 1024 * 1024;
        public string MonitorDevice;
        public bool UseWorkArea = true;
        public bool AutomaticRegions = true;
        public bool Borderless = false;
        public int GridStepPixels = 100;

        [XmlIgnore]
        internal bool LayoutWasRepaired;

        [XmlIgnore]
        public List<Zone> Zones;

        // Arrays are replaced on deserialization. A serialized List would append to constructor defaults.
        [XmlArray("Zones")]
        [XmlArrayItem("Zone")]
        public Zone[] SerializedZones
        {
            get { return Zones == null ? null : Zones.ToArray(); }
            set { Zones = value == null ? null : new List<Zone>(value); }
        }

        public LayoutSettings()
        {
            MonitorDevice = String.Empty;
            Zones = Preset(0);
        }

        public static LayoutSettings Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return new LayoutSettings();
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumXmlCharacters * 2) return RepairedDefaults();
                    XmlReaderSettings readerSettings = new XmlReaderSettings();
                    readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                    readerSettings.XmlResolver = null;
                    readerSettings.MaxCharactersInDocument = MaximumXmlCharacters;
                    readerSettings.MaxCharactersFromEntities = 0;
                    using (XmlReader reader = XmlReader.Create(stream, readerSettings))
                    {
                        LayoutSettings settings = (LayoutSettings)new XmlSerializer(typeof(LayoutSettings)).Deserialize(reader);
                        if (settings == null) return RepairedDefaults();
                        settings.Normalize();
                        return settings;
                    }
                }
            }
            catch (FileNotFoundException) { return new LayoutSettings(); }
            catch (DirectoryNotFoundException) { return new LayoutSettings(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
            catch (XmlException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (SecurityException) { }
            return RepairedDefaults();
        }

        public void Save(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", "path");
            Normalize();
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string token = Guid.NewGuid().ToString("N");
            string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + token + ".tmp");
            string backupPath = temporaryPath + ".backup";
            try
            {
                XmlWriterSettings writerSettings = new XmlWriterSettings();
                writerSettings.Encoding = new UTF8Encoding(false);
                writerSettings.Indent = true;
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, writerSettings))
                    {
                        new XmlSerializer(typeof(LayoutSettings)).Serialize(writer, this);
                        writer.Flush();
                        stream.Flush(true);
                    }
                }
                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Replace(temporaryPath, fullPath, backupPath, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithRollback(temporaryPath, fullPath, backupPath);
                    }
                    catch (IOException)
                    {
                        // Some filesystems cannot atomically replace. Never overwrite an uncertain backup.
                        if (!File.Exists(temporaryPath) || !File.Exists(fullPath) || File.Exists(backupPath)) throw;
                        ReplaceWithRollback(temporaryPath, fullPath, backupPath);
                    }
                }
                else File.Move(temporaryPath, fullPath);
                TryDelete(backupPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        public static List<Zone> Preset(int kind)
        {
            switch (kind)
            {
                case 1:
                    return new List<Zone> {
                        new Zone("Left", 0, 0, 1.0 / 3, 1),
                        new Zone("Center", 1.0 / 3, 0, 1.0 / 3, 1),
                        new Zone("Right", 2.0 / 3, 0, 1.0 / 3, 1) };
                case 2:
                    return new List<Zone> {
                        new Zone("Left", 0, 0, 0.25, 1),
                        new Zone("Center", 0.25, 0, 0.5, 1),
                        new Zone("Right", 0.75, 0, 0.25, 1) };
                case 3:
                    return new List<Zone> {
                        new Zone("Top left", 0, 0, 0.5, 0.5),
                        new Zone("Top right", 0.5, 0, 0.5, 0.5),
                        new Zone("Bottom left", 0, 0.5, 0.5, 0.5),
                        new Zone("Bottom right", 0.5, 0.5, 0.5, 0.5) };
                default:
                    return new List<Zone> {
                        new Zone("Left", 0, 0, 0.5, 1),
                        new Zone("Right", 0.5, 0, 0.5, 1) };
            }
        }

        private void Normalize()
        {
            MonitorDevice = Zone.SafeText(MonitorDevice, String.Empty, 256);
            if (GridStepPixels < 1 || GridStepPixels > 100000)
            {
                GridStepPixels = 100;
                LayoutWasRepaired = true;
            }
            List<Zone> normalized = new List<Zone>();
            HashSet<string> identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool invalidGeometry = false;
            if (Zones != null)
            {
                foreach (Zone zone in Zones)
                {
                    if (zone == null) continue;
                    if (!LayoutGeometry.HasValidGeometry(zone)) invalidGeometry = true;
                    zone.Normalize("Zone " + (normalized.Count + 1));
                    if (!identifiers.Add(zone.Id)) { zone.Id = Guid.NewGuid().ToString("N"); identifiers.Add(zone.Id); }
                    normalized.Add(zone);
                    if (normalized.Count > 9) { invalidGeometry = true; break; }
                }
            }
            List<double> xEdges, yEdges;
            List<Zone> ordered;
            if (invalidGeometry || !LayoutGeometry.TryReadCartesian(normalized, out xEdges, out yEdges, out ordered))
            {
                // Invalid legacy layouts must never reach a fullscreen rule resolver.
                Zones = Preset(0);
                LayoutWasRepaired = true;
                return;
            }
            // Canonicalize shared boundaries while preserving the saved list order and identifiers.
            int columns = xEdges.Count - 1;
            for (int i = 0; i < ordered.Count; ++i)
            {
                int column = i % columns;
                int row = i / columns;
                ordered[i].X = xEdges[column];
                ordered[i].Y = yEdges[row];
                ordered[i].Width = xEdges[column + 1] - xEdges[column];
                ordered[i].Height = yEdges[row + 1] - yEdges[row];
            }
            Zones = normalized;
        }

        private static LayoutSettings RepairedDefaults()
        {
            LayoutSettings settings = new LayoutSettings();
            settings.LayoutWasRepaired = true;
            return settings;
        }

        private static void ReplaceWithRollback(string temporaryPath, string fullPath, string backupPath)
        {
            File.Move(fullPath, backupPath);
            try
            {
                File.Move(temporaryPath, fullPath);
            }
            catch
            {
                if (!File.Exists(fullPath)) File.Move(backupPath, fullPath);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class LayoutGeometry
    {
        private const double Tolerance = 0.000000001;

        internal static bool HasValidGeometry(Zone zone)
        {
            return zone != null && Finite(zone.X) && Finite(zone.Y) && Finite(zone.Width) && Finite(zone.Height) &&
                zone.X >= 0 && zone.Y >= 0 && zone.Width > 0 && zone.Height > 0 &&
                zone.X + zone.Width <= 1 + Tolerance && zone.Y + zone.Height <= 1 + Tolerance;
        }

        internal static bool TryReadCartesian(IList<Zone> zones, out List<double> xEdges,
            out List<double> yEdges, out List<Zone> ordered)
        {
            xEdges = new List<double> { 0, 1 };
            yEdges = new List<double> { 0, 1 };
            ordered = new List<Zone>();
            if (zones == null || zones.Count == 0 || zones.Count > 9) return false;
            foreach (Zone zone in zones)
            {
                if (!HasValidGeometry(zone)) return false;
                AddEdge(xEdges, zone.X);
                AddEdge(xEdges, Math.Min(1, zone.X + zone.Width));
                AddEdge(yEdges, zone.Y);
                AddEdge(yEdges, Math.Min(1, zone.Y + zone.Height));
            }
            xEdges.Sort();
            yEdges.Sort();
            int columns = xEdges.Count - 1;
            int rows = yEdges.Count - 1;
            if (columns * rows != zones.Count) return false;
            Zone[] cells = new Zone[zones.Count];
            foreach (Zone zone in zones)
            {
                int left = FindEdge(xEdges, zone.X);
                int top = FindEdge(yEdges, zone.Y);
                int right = FindEdge(xEdges, zone.X + zone.Width);
                int bottom = FindEdge(yEdges, zone.Y + zone.Height);
                if (left < 0 || top < 0 || right != left + 1 || bottom != top + 1) return false;
                int index = top * columns + left;
                if (index < 0 || index >= cells.Length || cells[index] != null) return false;
                cells[index] = zone;
            }
            foreach (Zone cell in cells)
            {
                if (cell == null) return false;
                ordered.Add(cell);
            }
            return true;
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        private static void AddEdge(List<double> edges, double edge)
        {
            if (FindEdge(edges, edge) < 0) edges.Add(edge);
        }

        private static int FindEdge(List<double> edges, double edge)
        {
            for (int i = 0; i < edges.Count; ++i)
                if (Math.Abs(edges[i] - edge) <= Tolerance) return i;
            return -1;
        }
    }
}
