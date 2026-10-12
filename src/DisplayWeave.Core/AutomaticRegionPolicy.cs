using System;
using System.Collections.Generic;
using System.Drawing;

namespace DisplayWeave
{
    public enum RegionControlMode { None, Automatic, Force, Maximize, Borderless }

    // Pure geometry and intent policy: observing an ordinary window never requests fullscreen.
    public sealed class AutomaticRegionPolicy
    {
        private sealed class Observation
        {
            internal Rectangle NormalBounds;
            internal string ZoneId;
        }

        private readonly Dictionary<string, Observation> observations = new Dictionary<string, Observation>(StringComparer.Ordinal);

        public static RegionControlMode Decide(bool automaticEnabled, bool paused, bool released,
            ApplicationAction? matchedAction, bool ruleResolved)
        {
            // A centered-fullscreen app runs in exclusive fullscreen; the graphics driver places it, not DisplayWeave.
            if (paused || released || matchedAction == ApplicationAction.Ignore || matchedAction == ApplicationAction.CenteredFullscreen)
                return RegionControlMode.None;
            if (matchedAction == ApplicationAction.BorderlessInRegion)
                return ruleResolved ? RegionControlMode.Borderless : RegionControlMode.None;
            if (matchedAction == ApplicationAction.ForceFullscreen)
                return ruleResolved ? RegionControlMode.Force : RegionControlMode.None;
            if (matchedAction == ApplicationAction.MaximizeInRegion)
                return ruleResolved ? RegionControlMode.Maximize : RegionControlMode.None;
            return automaticEnabled ? RegionControlMode.Automatic : RegionControlMode.None;
        }

        public bool Observe(string identityKey, Rectangle currentBounds, Rectangle restoredBounds,
            bool expanded, bool minimized, Rectangle area, IList<Zone> zones, out string zoneId, out Rectangle region)
        {
            zoneId = null;
            region = Rectangle.Empty;
            if (String.IsNullOrEmpty(identityKey) || minimized || !Positive(currentBounds) || !Positive(area)) return false;
            Observation observation;
            if (!observations.TryGetValue(identityKey, out observation))
            {
                observation = new Observation();
                observations.Add(identityKey, observation);
            }
            if (!Intersects(currentBounds, area))
            {
                if (!expanded) { observation.NormalBounds = currentBounds; observation.ZoneId = null; }
                return false;
            }
            if (expanded && !String.IsNullOrEmpty(observation.ZoneId))
            {
                // A split/deleted target does not silently remap an already fullscreen application.
                if (!RegionGeometry.TryResolveRegionInArea(observation.ZoneId, zones, area, out region)) return false;
                zoneId = observation.ZoneId;
                return true;
            }
            Rectangle source = currentBounds;
            if (expanded)
            {
                if (Positive(observation.NormalBounds)) source = observation.NormalBounds;
                else if (Positive(restoredBounds)) source = restoredBounds;
            }
            else observation.NormalBounds = currentBounds;
            if (!TrySelectRegion(source, zones, area, out zoneId, out region)) return false;
            observation.ZoneId = zoneId;
            return true;
        }

        public void Forget(string identityKey) { if (identityKey != null) observations.Remove(identityKey); }

        public void Retain(ISet<string> identities)
        {
            List<string> remove = new List<string>();
            foreach (string identity in observations.Keys) if (!identities.Contains(identity)) remove.Add(identity);
            foreach (string identity in remove) observations.Remove(identity);
        }

        public static bool TrySelectRegion(Rectangle bounds, IList<Zone> zones, Rectangle area, out string zoneId, out Rectangle region)
        {
            zoneId = null;
            region = Rectangle.Empty;
            if (!Positive(bounds) || !Positive(area) || zones == null || zones.Count == 0 || !Intersects(bounds, area)) return false;
            Rectangle ignored;
            if (!RegionGeometry.TryResolveRegionInArea(zones[0] == null ? null : zones[0].Id, zones, area, out ignored)) return false;
            double centerX = bounds.X + bounds.Width / 2.0;
            double centerY = bounds.Y + bounds.Height / 2.0;
            long bestOverlap = -1;
            bool bestContainsCenter = false;
            foreach (Zone zone in zones)
            {
                Rectangle cell = zone.ToPixels(area);
                long overlap = IntersectionArea(bounds, cell);
                bool containsCenter = centerX >= cell.Left && centerX < (long)cell.X + cell.Width &&
                    centerY >= cell.Top && centerY < (long)cell.Y + cell.Height;
                if (overlap > bestOverlap || (overlap == bestOverlap && containsCenter && !bestContainsCenter))
                {
                    bestOverlap = overlap;
                    bestContainsCenter = containsCenter;
                    zoneId = zone.Id;
                    region = cell;
                }
            }
            return bestOverlap > 0;
        }

        private static bool Positive(Rectangle rectangle) { return rectangle.Width > 0 && rectangle.Height > 0; }
        private static bool Intersects(Rectangle first, Rectangle second) { return IntersectionArea(first, second) > 0; }
        private static long IntersectionArea(Rectangle first, Rectangle second)
        {
            long width = Math.Min((long)first.X + first.Width, (long)second.X + second.Width) - Math.Max(first.X, second.X);
            long height = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height) - Math.Max(first.Y, second.Y);
            return width <= 0 || height <= 0 ? 0 : width * height;
        }
    }
}
