using System;
using System.Collections.Generic;
using System.Drawing;

namespace DisplayWeave
{
    // Pixel geometry of a layout: resolving its regions on a display, validating them, and comparing window bounds.
    public static class RegionGeometry
    {
        public static bool TryResolveLayout(LayoutSettings layout, out List<Rectangle> regions)
        {
            regions = null;
            if (layout == null) return false;
            DisplayMonitor selected = DisplayMonitor.Find(layout.MonitorDevice);
            return selected != null && TryResolveLayoutInArea(layout.Zones,
                layout.UseWorkArea ? selected.WorkingArea : selected.Bounds, out regions);
        }

        internal static bool TryResolveLayoutInArea(IList<Zone> zones, Rectangle area, out List<Rectangle> regions)
        {
            regions = null;
            Rectangle first;
            if (zones == null || zones.Count == 0 || zones[0] == null ||
                !TryResolveRegionInArea(zones[0].Id, zones, area, out first)) return false;
            regions = new List<Rectangle>(zones.Count);
            foreach (Zone zone in zones) regions.Add(zone.ToPixels(area));
            return true;
        }

        public static bool TryResolveRegion(ApplicationRule rule, LayoutSettings layout, out Rectangle region)
        {
            region = Rectangle.Empty;
            if (rule == null || layout == null) return false;
            if (!String.Equals(rule.MonitorDevice, layout.MonitorDevice, StringComparison.OrdinalIgnoreCase)) return false;
            DisplayMonitor selected = DisplayMonitor.Find(rule.MonitorDevice);
            if (selected == null) return false;
            return TryResolveRegionInArea(rule.ZoneId, layout.Zones,
                layout.UseWorkArea ? selected.WorkingArea : selected.Bounds, out region);
        }

        // Resolve without repairing or rewriting IDs. Even a valid normalized
        // layout can collapse at a smaller resolution: ToPixels' one-pixel
        // minimum would then make adjacent cells overlap. Reject the whole
        // partition before permitting any saved rule to control a window.
        internal static bool TryResolveRegionInArea(string zoneId, IList<Zone> zones, Rectangle area, out Rectangle region)
        {
            region = Rectangle.Empty;
            if (String.IsNullOrEmpty(zoneId) || zones == null || zones.Count == 0 || zones.Count > 9 ||
                area.Width <= 0 || area.Height <= 0 || (long)area.X + area.Width > Int32.MaxValue ||
                (long)area.Y + area.Height > Int32.MaxValue) return false;

            const double epsilon = 0.000000001;
            HashSet<string> identifiers = new HashSet<string>(StringComparer.Ordinal);
            List<Rectangle> pixels = new List<Rectangle>();
            long coveredPixels = 0;
            double normalizedArea = 0;
            Rectangle candidate = Rectangle.Empty;
            bool found = false;
            foreach (Zone zone in zones)
            {
                if (zone == null || String.IsNullOrEmpty(zone.Id) || !identifiers.Add(zone.Id) ||
                    !Finite(zone.X) || !Finite(zone.Y) || !Finite(zone.Width) || !Finite(zone.Height) ||
                    zone.X < 0 || zone.Y < 0 || zone.Width <= 0 || zone.Height <= 0 ||
                    zone.X >= 1 || zone.Y >= 1 || zone.X + zone.Width > 1 + epsilon ||
                    zone.Y + zone.Height > 1 + epsilon) return false;
                Rectangle cell = zone.ToPixels(area);
                if (cell.Width <= 0 || cell.Height <= 0 || cell.Left < area.Left || cell.Top < area.Top ||
                    (long)cell.X + cell.Width > (long)area.X + area.Width ||
                    (long)cell.Y + cell.Height > (long)area.Y + area.Height) return false;
                foreach (Rectangle previous in pixels)
                    if (previous.IntersectsWith(cell)) return false;
                pixels.Add(cell);
                coveredPixels += (long)cell.Width * cell.Height;
                normalizedArea += zone.Width * zone.Height;
                if (String.Equals(zone.Id, zoneId, StringComparison.Ordinal)) { candidate = cell; found = true; }
            }
            // Validate source geometry as well: rounding must not hide malformed
            // normalized overlaps or gaps from a legacy/edited settings file.
            for (int i = 0; i < zones.Count; i++)
                for (int j = i + 1; j < zones.Count; j++)
                {
                    Zone a = zones[i], b = zones[j];
                    double overlapWidth = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                    double overlapHeight = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                    if (overlapWidth > epsilon && overlapHeight > epsilon) return false;
                }
            if (!found || Math.Abs(normalizedArea - 1) > epsilon * zones.Count ||
                coveredPixels != (long)area.Width * area.Height) return false;
            region = candidate;
            return true;
        }

        private static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        // Keep the window's own invisible-border overhang, as Windows does when maximizing,
        // so its visible frame lands exactly on the target.
        internal static Rectangle MaximizedBounds(Rectangle window, Rectangle frame, Rectangle target)
        {
            return Rectangle.FromLTRB(target.Left - (frame.Left - window.Left), target.Top - (frame.Top - window.Top),
                target.Right + (window.Right - frame.Right), target.Bottom + (window.Bottom - frame.Bottom));
        }

        internal static bool NearlyCovers(Rectangle window, Rectangle monitor)
        {
            return window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
                (long)window.X + window.Width >= (long)monitor.X + monitor.Width - 2 &&
                (long)window.Y + window.Height >= (long)monitor.Y + monitor.Height - 2;
        }

        internal static bool NearlyEquals(Rectangle first, Rectangle second)
        {
            return Math.Abs((long)first.Left - second.Left) <= 2 && Math.Abs((long)first.Top - second.Top) <= 2 &&
                Math.Abs((long)first.Width - second.Width) <= 2 && Math.Abs((long)first.Height - second.Height) <= 2;
        }
    }
}
