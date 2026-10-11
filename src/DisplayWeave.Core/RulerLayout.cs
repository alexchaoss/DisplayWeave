using System;
using System.Collections.Generic;
using System.Drawing;

namespace DisplayWeave
{
    // Every ruler divider crosses the whole display. Their Cartesian cells form a complete partition.
    public sealed class RulerLayout
    {
        private readonly LayoutSettings settings;
        private List<int> verticalPins = new List<int>();
        private List<int> horizontalPins = new List<int>();

        public Rectangle Area { get; private set; }
        public int GridStep { get { return settings.GridStepPixels; } }
        public IList<int> VerticalPins { get { return verticalPins.AsReadOnly(); } }
        public IList<int> HorizontalPins { get { return horizontalPins.AsReadOnly(); } }
        public IList<Zone> Zones { get { return settings.Zones.AsReadOnly(); } }
        public bool WasRepaired { get; private set; }

        public RulerLayout(LayoutSettings settings, Rectangle area)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (area.Width <= 0 || area.Height <= 0)
                throw new ArgumentOutOfRangeException("area", "The display area must have positive dimensions.");
            this.settings = settings;
            Area = area;
            WasRepaired = settings.LayoutWasRepaired;
            settings.LayoutWasRepaired = false;
            if (settings.GridStepPixels < 1 || settings.GridStepPixels > 100000)
            {
                settings.GridStepPixels = 100;
                WasRepaired = true;
            }
            List<double> xEdges, yEdges;
            List<Zone> ordered;
            List<int> vertical, horizontal;
            if (!LayoutGeometry.TryReadCartesian(settings.Zones, out xEdges, out yEdges, out ordered) ||
                !TrySnapAxis(InteriorPixels(xEdges, area.Width), area.Width, GridStep, out vertical) ||
                !TrySnapAxis(InteriorPixels(yEdges, area.Height), area.Height, GridStep, out horizontal))
            {
                WasRepaired = true;
                BuildPreset(0, 100, false);
                return;
            }
            List<int> oldVertical = InteriorPixels(xEdges, area.Width);
            List<int> oldHorizontal = InteriorPixels(yEdges, area.Height);
            if (!SamePins(oldVertical, vertical) || !SamePins(oldHorizontal, horizontal)) WasRepaired = true;
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordered.Count; ++i)
            {
                string previousId = ordered[i].Id;
                ordered[i].Normalize("Region " + (i + 1));
                if (!ids.Add(ordered[i].Id))
                {
                    ordered[i].Id = Guid.NewGuid().ToString("N");
                    ids.Add(ordered[i].Id);
                }
                if (previousId != ordered[i].Id) WasRepaired = true;
            }
            Commit(vertical, horizontal, delegate(int column, int row) { return ordered[row * (vertical.Count + 1) + column]; });
        }

        public bool TryAddDivider(bool vertical, int pixel, out string error)
        {
            List<int> pins = vertical ? verticalPins : horizontalPins;
            int length = vertical ? Area.Width : Area.Height;
            int otherCells = (vertical ? horizontalPins.Count : verticalPins.Count) + 1;
            if ((pins.Count + 2) * otherCells > 9)
                return Fail("A layout can contain at most 9 regions.", out error);
            long rounded = (long)Math.Round((double)pixel / GridStep, MidpointRounding.AwayFromZero) * GridStep;
            if (rounded <= 0 || rounded >= length)
                return Fail("Place a divider inside the display on an available grid line.", out error);
            int snapped = (int)rounded;
            int index = pins.BinarySearch(snapped);
            if (index >= 0) return Fail("A divider already occupies that grid line.", out error);
            index = ~index;
            int previous = index == 0 ? 0 : pins[index - 1];
            int next = index == pins.Count ? length : pins[index];
            int minimum = MinimumCell(length);
            if (snapped - previous < minimum || next - snapped < minimum)
                return Fail("This divider would make a region smaller than " + minimum + " pixels.", out error);
            List<int> newVertical = new List<int>(verticalPins);
            List<int> newHorizontal = new List<int>(horizontalPins);
            (vertical ? newVertical : newHorizontal).Insert(index, snapped);
            List<Zone> old = settings.Zones;
            int oldColumns = verticalPins.Count + 1;
            Commit(newVertical, newHorizontal, delegate(int column, int row)
            {
                int coordinate = vertical ? column : row;
                if (coordinate == index || coordinate == index + 1) return null;
                int oldCoordinate = coordinate > index + 1 ? coordinate - 1 : coordinate;
                return old[(vertical ? row : oldCoordinate) * oldColumns + (vertical ? oldCoordinate : column)];
            });
            error = String.Empty;
            return true;
        }

        public bool TryMoveDivider(bool vertical, int index, int pixel, out string error)
        {
            List<int> pins = vertical ? verticalPins : horizontalPins;
            if (index < 0 || index >= pins.Count) return Fail("Select an existing divider.", out error);
            int length = vertical ? Area.Width : Area.Height;
            int minimum = MinimumCell(length);
            int lower = (index == 0 ? 0 : pins[index - 1]) + minimum;
            int upper = (index == pins.Count - 1 ? length : pins[index + 1]) - minimum;
            int snapped;
            if (!TrySnapBetween(pixel, lower, upper, GridStep, out snapped))
                return Fail("There is no available grid line between the neighboring dividers.", out error);
            if (snapped != pins[index])
            {
                List<int> newVertical = new List<int>(verticalPins);
                List<int> newHorizontal = new List<int>(horizontalPins);
                (vertical ? newVertical : newHorizontal)[index] = snapped;
                CommitPreservingCells(newVertical, newHorizontal);
            }
            error = String.Empty;
            return true;
        }

        public bool TryRemoveDivider(bool vertical, int index, out string error)
        {
            List<int> pins = vertical ? verticalPins : horizontalPins;
            if (index < 0 || index >= pins.Count) return Fail("Select an existing divider.", out error);
            List<int> newVertical = new List<int>(verticalPins);
            List<int> newHorizontal = new List<int>(horizontalPins);
            (vertical ? newVertical : newHorizontal).RemoveAt(index);
            List<Zone> old = settings.Zones;
            int oldColumns = verticalPins.Count + 1;
            Commit(newVertical, newHorizontal, delegate(int column, int row)
            {
                int coordinate = vertical ? column : row;
                if (coordinate == index) return null;
                int oldCoordinate = coordinate > index ? coordinate + 1 : coordinate;
                return old[(vertical ? row : oldCoordinate) * oldColumns + (vertical ? oldCoordinate : column)];
            });
            error = String.Empty;
            return true;
        }

        public bool TrySetGridStep(int step, out string error)
        {
            if (step < 1 || step > 100000) return Fail("Grid spacing must be between 1 and 100000 pixels.", out error);
            List<int> vertical, horizontal;
            if (!TrySnapAxis(verticalPins, Area.Width, step, out vertical) ||
                !TrySnapAxis(horizontalPins, Area.Height, step, out horizontal))
                return Fail("This spacing leaves too few grid lines for the current regions. Remove a divider or use a smaller spacing.", out error);
            settings.GridStepPixels = step;
            CommitPreservingCells(vertical, horizontal);
            error = String.Empty;
            return true;
        }

        // scalePercent is the display's Windows scaling, such as 125.
        public void ApplyPreset(int kind, int scalePercent = 100)
        {
            BuildPreset(kind, scalePercent, true);
        }

        private void BuildPreset(int kind, int scalePercent, bool preserveUnchanged)
        {
            List<int> desiredVertical, desiredHorizontal;
            PresetPins(kind, Area.Width, Area.Height, scalePercent, out desiredVertical, out desiredHorizontal);
            // Put the grid on every preset pin; the new spacing divides the old one, so other positions stay valid.
            foreach (int pin in desiredVertical) settings.GridStepPixels = Gcd(GridStep, Math.Abs(pin));
            foreach (int pin in desiredHorizontal) settings.GridStepPixels = Gcd(GridStep, Math.Abs(pin));
            List<int> vertical = BestFittingAxis(desiredVertical, Area.Width);
            List<int> horizontal = BestFittingAxis(desiredHorizontal, Area.Height);
            List<Zone> old = preserveUnchanged && settings.Zones != null ? new List<Zone>(settings.Zones) : new List<Zone>();
            Commit(vertical, horizontal, delegate(int column, int row)
            {
                Rectangle bounds = CellBounds(vertical, horizontal, column, row);
                foreach (Zone zone in old)
                    if (zone != null && zone.ToPixels(Area) == bounds) return zone;
                return null;
            });
        }

        // Equal splits stay exact when they land on whole logical pixels; other sizes round to 20 px that are
        // also whole logical pixels. Wide center keeps a 16:9 center at full height (2560 x 1440 on 3440 x 1440,
        // 3720 x 2100 on a 7680 x 2100 work area) unless that leaves sides under an eighth of the width.
        private static void PresetPins(int kind, int width, int height, int scalePercent,
            out List<int> vertical, out List<int> horizontal)
        {
            int unit = scalePercent > 0 ? scalePercent / Gcd(scalePercent, 100) : 1; // 5 at 125%, 3 at 150%
            vertical = new List<int>();
            horizontal = new List<int>();
            int side;
            switch (kind)
            {
                case 1:
                    side = Snap(width / 3.0, unit);
                    break;
                case 2:
                    side = Snap((width - height * 16.0 / 9) / 2, unit);
                    if (side < width / 8 || width - 2 * side < width / 8) side = Snap(width / 4.0, unit);
                    break;
                case 3:
                    vertical.Add(Snap(width / 2.0, unit));
                    horizontal.Add(Snap(height / 2.0, unit));
                    return;
                default:
                    vertical.Add(Snap(width / 2.0, unit));
                    return;
            }
            vertical.Add(side);
            vertical.Add(width - side);
        }

        private static int Snap(double ideal, int unit)
        {
            double exact = Math.Round(ideal);
            if (Math.Abs(ideal - exact) < 1e-6 && exact % unit == 0) return (int)exact;
            int step = unit / Gcd(unit, 20) * 20;
            return (int)Math.Round(ideal / step, MidpointRounding.AwayFromZero) * step;
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0) { int remainder = a % b; a = b; b = remainder; }
            return a;
        }

        private List<int> BestFittingAxis(List<int> desired, int length)
        {
            List<int> result;
            while (!TrySnapAxis(desired, length, GridStep, out result))
            {
                desired.RemoveAt(desired.Count - 1);
                WasRepaired = true;
            }
            return result;
        }

        private void CommitPreservingCells(List<int> vertical, List<int> horizontal)
        {
            List<Zone> old = settings.Zones;
            int columns = verticalPins.Count + 1;
            Commit(vertical, horizontal, delegate(int column, int row) { return old[row * columns + column]; });
        }

        private void Commit(List<int> vertical, List<int> horizontal, Func<int, int, Zone> previousCell)
        {
            List<Zone> zones = new List<Zone>();
            for (int row = 0; row <= horizontal.Count; ++row)
                for (int column = 0; column <= vertical.Count; ++column)
                {
                    Zone previous = previousCell(column, row);
                    Zone zone = Zone.FromPixels(previous == null ? "Region " + (zones.Count + 1) : previous.Name,
                        CellBounds(vertical, horizontal, column, row), Area);
                    if (previous != null) zone.Id = previous.Id;
                    zones.Add(zone);
                }
            verticalPins = vertical;
            horizontalPins = horizontal;
            settings.Zones = zones;
        }

        private Rectangle CellBounds(List<int> vertical, List<int> horizontal, int column, int row)
        {
            int left = column == 0 ? 0 : vertical[column - 1];
            int right = column == vertical.Count ? Area.Width : vertical[column];
            int top = row == 0 ? 0 : horizontal[row - 1];
            int bottom = row == horizontal.Count ? Area.Height : horizontal[row];
            return new Rectangle(Area.X + left, Area.Y + top, right - left, bottom - top);
        }

        private static List<int> InteriorPixels(List<double> edges, int length)
        {
            List<int> pins = new List<int>();
            for (int i = 1; i + 1 < edges.Count; ++i)
                pins.Add((int)Math.Round(edges[i] * length, MidpointRounding.AwayFromZero));
            return pins;
        }

        private static bool TrySnapAxis(IList<int> desired, int length, int step, out List<int> result)
        {
            result = new List<int>();
            if (desired.Count == 0) return true;
            int minimum = MinimumCell(length);
            long first = ((long)minimum + step - 1) / step;
            long last = ((long)length - minimum) / step;
            long separation = ((long)minimum + step - 1) / step;
            if (first + (desired.Count - 1) * separation > last) return false;
            long previous = first - separation;
            for (int i = 0; i < desired.Count; ++i)
            {
                long lower = Math.Max(first + i * separation, previous + separation);
                long upper = last - (desired.Count - i - 1) * separation;
                long nearest = (long)Math.Round((double)desired[i] / step, MidpointRounding.AwayFromZero);
                long selected = Math.Max(lower, Math.Min(upper, nearest));
                result.Add((int)(selected * step));
                previous = selected;
            }
            return true;
        }

        private static bool TrySnapBetween(int pixel, int lower, int upper, int step, out int snapped)
        {
            long first = ((long)lower + step - 1) / step;
            long last = (long)upper / step;
            snapped = 0;
            if (first > last) return false;
            long nearest = (long)Math.Round((double)pixel / step, MidpointRounding.AwayFromZero);
            snapped = (int)(Math.Max(first, Math.Min(last, nearest)) * step);
            return true;
        }

        private static int MinimumCell(int length) { return length < 64 ? 1 : 32; }

        private static bool SamePins(IList<int> first, IList<int> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; ++i) if (first[i] != second[i]) return false;
            return true;
        }

        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
