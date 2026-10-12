using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DisplayWeave
{
    // The canvas edits divider coordinates through RulerLayout only. Regions are
    // the resulting non-overlapping cells; their interiors are selection targets.
    internal sealed partial class RulerCanvas : Control
    {
        private RulerLayout rulerLayout;
        internal List<Zone> Zones = new List<Zone>();
        internal int SelectedIndex = -1;
        internal event Action SelectionChanged;
        internal event Action LayoutEditing;
        internal event Action LayoutEdited;
        internal event Action PinSelectionChanged;
        internal event Action<string> Notice;

        private RectangleF monitor;
        private Rectangle fallbackArea = new Rectangle(0, 0, 1920, 1080);
        private bool pinVertical = true;
        private int selectedPin = -1;
        private bool dragging;
        private bool editingRaised;
        private bool dragChanged;
        private int originalPixel;
        private bool hoverVertical;
        private int hoverPin = -1;
        private string dragError = String.Empty;

        internal RulerCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(19, 29, 48);
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
            AccessibleName = "Monitor rulers and region layout";
            AccessibleDescription = "Double-click a ruler to add a divider. Drag ruler pins to change regions. Arrow keys move the selected pin; Delete removes it.";
        }

        internal new RulerLayout Layout
        {
            get { return rulerLayout; }
            set
            {
                int previous = SelectedPinPixel;
                rulerLayout = value;
                selectedPin = previous < 0 ? -1 : FindPin(pinVertical, previous);
                hoverPin = -1;
                UpdateGeometry();
                Invalidate();
            }
        }

        internal Rectangle ScreenArea
        {
            get { return rulerLayout == null ? fallbackArea : rulerLayout.Area; }
            set { fallbackArea = value; UpdateGeometry(); Invalidate(); }
        }

        internal bool HasSelectedPin { get { return SelectedPinPixel >= 0; } }
        internal int SelectedPinPixel
        {
            get
            {
                IList<int> pins = Pins(pinVertical);
                return selectedPin >= 0 && selectedPin < pins.Count ? pins[selectedPin] : -1;
            }
        }
        internal string SelectedPinLabel
        {
            get { return HasSelectedPin ? (pinVertical ? "Width" : "Height") + " pin at " + SelectedPinPixel + " px" : "Select a ruler pin to move or remove it"; }
        }

        private IList<Zone> LiveZones { get { return rulerLayout == null ? (IList<Zone>)Zones : rulerLayout.Zones; } }
        private IList<int> Pins(bool vertical)
        {
            if (rulerLayout == null) return new int[0];
            return vertical ? rulerLayout.VerticalPins : rulerLayout.HorizontalPins;
        }

        private void UpdateGeometry()
        {
            Rectangle area = ScreenArea;
            if (area.Width <= 0 || area.Height <= 0) { monitor = RectangleF.Empty; return; }
            float availableWidth = Math.Max(1, ClientSize.Width - 138);
            float availableHeight = Math.Max(1, ClientSize.Height - 118);
            float scale = Math.Min(availableWidth / area.Width, availableHeight / area.Height);
            float width = area.Width * scale, height = area.Height * scale;
            monitor = new RectangleF((ClientSize.Width - width) / 2,
                61 + (availableHeight - height) / 2, width, height);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateGeometry();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            UpdateGeometry();
            if (monitor.Width <= 0 || monitor.Height <= 0) return;
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Color.FromArgb(31, 44, 63))) graphics.FillRectangle(fill, monitor);
            IList<Zone> zones = LiveZones;
            for (int i = 0; i < zones.Count; i++) PaintZone(graphics, zones[i], i);
            using (var edge = new Pen(Color.FromArgb(156, 181, 207), 1))
                graphics.DrawRectangle(edge, monitor.X, monitor.Y, monitor.Width, monitor.Height);
            PaintRulers(graphics, true);
            PaintRulers(graphics, false);
            PaintPins(graphics, true);
            PaintPins(graphics, false);

            string heading = "RULER PINS  ·  DRAG TO RESIZE REGIONS";
            int pinPixel = dragging ? SelectedPinPixel : HoverPinPixel();
            if (pinPixel >= 0) heading = (dragging ? pinVertical : hoverVertical) ? "WIDTH DIVIDER  ·  " + pinPixel + " px" : "HEIGHT DIVIDER  ·  " + pinPixel + " px";
            string footer = ScreenArea.Width + " × " + ScreenArea.Height + " px" +
                (rulerLayout == null ? "" : "  ·  Grid " + rulerLayout.GridStep + " px") + "  ·  Double-click a ruler to add a pin";
            using (var brush = new SolidBrush(Color.FromArgb(192, 209, 229)))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                graphics.DrawString(heading, Font, brush, new RectangleF(12, 9, Math.Max(1, Width - 24), 24), format);
                graphics.DrawString(footer, Font, brush, new RectangleF(12, Math.Max(0, Height - 25), Math.Max(1, Width - 24), 22), format);
            }
            if (Focused) ControlPaint.DrawFocusRectangle(graphics, new Rectangle(3, 3, Math.Max(1, Width - 7), Math.Max(1, Height - 7)), Color.FromArgb(86, 151, 211), BackColor);
        }

        private RectangleF DisplayRect(Zone zone)
        {
            return new RectangleF(monitor.X + (float)zone.X * monitor.Width, monitor.Y + (float)zone.Y * monitor.Height,
                (float)zone.Width * monitor.Width, (float)zone.Height * monitor.Height);
        }

        private void PaintZone(Graphics graphics, Zone zone, int index)
        {
            RectangleF rectangle = DisplayRect(zone);
            bool selected = index == SelectedIndex;
            using (var brush = new SolidBrush(selected ? Color.FromArgb(42, 101, 156) : Color.FromArgb(37, 61, 89)))
                graphics.FillRectangle(brush, rectangle);
            using (var edge = new Pen(selected ? Color.FromArgb(112, 196, 255) : Color.FromArgb(96, 123, 153), selected ? 2 : 1))
                graphics.DrawRectangle(edge, rectangle.X + 1, rectangle.Y + 1, Math.Max(0, rectangle.Width - 2), Math.Max(0, rectangle.Height - 2));
            Rectangle pixels = zone.ToPixels(ScreenArea);
            RectangleF label = RectangleF.Inflate(rectangle, -5, -4);
            if (label.Width < 15 || label.Height < 13) return;
            string dimensions = pixels.Width + " × " + pixels.Height + " px";
            string text = "Region " + (index + 1) + "\n" + dimensions;
            if (label.Width < graphics.MeasureString(dimensions, Font).Width || label.Height < Font.Height * 2 + 3)
                text = (index + 1).ToString();
            GraphicsState state = graphics.Save();
            graphics.SetClip(label, CombineMode.Intersect);
            using (var brush = new SolidBrush(Color.FromArgb(233, 244, 255)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                graphics.DrawString(text, Font, brush, label, format);
            graphics.Restore(state);
        }

        private void PaintRulers(Graphics graphics, bool vertical)
        {
            int extent = vertical ? ScreenArea.Width : ScreenArea.Height;
            float displayed = vertical ? monitor.Width : monitor.Height;
            int grid = rulerLayout == null ? 16 : Math.Max(1, rulerLayout.GridStep);
            double scale = displayed / extent;
            // All marks remain exact grid multiples. Skip marks that would be
            // less than six screen pixels apart, bounding work on huge displays.
            long minorStep = Math.Max(1L, (long)Math.Ceiling(6.0 / Math.Max(0.000001, grid * scale))) * grid;
            long majorStep = minorStep * Math.Max(1L, (long)Math.Ceiling((vertical ? 64.0 : 35.0) / Math.Max(0.000001, minorStep * scale)));
            using (var tick = new Pen(Color.FromArgb(104, 131, 158)))
            using (var major = new Pen(Color.FromArgb(173, 195, 218)))
            using (var brush = new SolidBrush(Color.FromArgb(186, 204, 223)))
            using (var small = new Font(Font.FontFamily, Math.Max(7F, Font.Size - 1F)))
            {
                SizeF startSize = graphics.MeasureString("0", small);
                SizeF endSize = graphics.MeasureString(extent.ToString(), small);
                float startLength = vertical ? startSize.Width : startSize.Height;
                float endLength = vertical ? endSize.Width : endSize.Height;
                const float labelGap = 7;
                bool showStart = displayed >= startLength + endLength + labelGap;
                float previousLabelEnd = showStart ? startLength : -labelGap;
                float endLabelStart = displayed - endLength;
                for (long pixel = 0; pixel <= extent; pixel += minorStep) DrawTick(graphics, vertical, (int)pixel, 5, tick);
                for (long pixel = majorStep; pixel < extent; pixel += majorStep)
                {
                    DrawTick(graphics, vertical, (int)pixel, 9, major);
                    // Reserve the measured endpoint labels and the preceding
                    // interior label on the axis. The endpoint is edge-aligned,
                    // so a fixed distance from its center is not sufficient.
                    float location = (float)(pixel * scale);
                    SizeF size = graphics.MeasureString(pixel.ToString(), small);
                    float length = vertical ? size.Width : size.Height;
                    float labelStart = location - length / 2;
                    float labelEnd = location + length / 2;
                    if (labelStart < previousLabelEnd + labelGap || labelEnd > endLabelStart - labelGap) continue;
                    DrawRulerLabel(graphics, vertical, (int)pixel, small, brush, false);
                    previousLabelEnd = labelEnd;
                }
                DrawTick(graphics, vertical, 0, 9, major);
                DrawTick(graphics, vertical, extent, 9, major);
                if (showStart) DrawRulerLabel(graphics, vertical, 0, small, brush, true);
                DrawRulerLabel(graphics, vertical, extent, small, brush, true);
            }
        }

        private void DrawTick(Graphics graphics, bool vertical, int pixel, float length, Pen pen)
        {
            if (vertical)
            {
                float x = monitor.Left + pixel * monitor.Width / ScreenArea.Width;
                graphics.DrawLine(pen, x, monitor.Top - 2, x, monitor.Top - length - 2);
                graphics.DrawLine(pen, x, monitor.Bottom + 2, x, monitor.Bottom + length + 2);
            }
            else
            {
                float y = monitor.Top + pixel * monitor.Height / ScreenArea.Height;
                graphics.DrawLine(pen, monitor.Left - 2, y, monitor.Left - length - 2, y);
                graphics.DrawLine(pen, monitor.Right + 2, y, monitor.Right + length + 2, y);
            }
        }

        private void DrawRulerLabel(Graphics graphics, bool vertical, int pixel, Font font, Brush brush, bool endpoint)
        {
            string text = pixel.ToString();
            SizeF size = graphics.MeasureString(text, font);
            if (vertical)
            {
                float x = monitor.Left + pixel * monitor.Width / ScreenArea.Width - size.Width / 2;
                if (endpoint) x = pixel == 0 ? monitor.Left : monitor.Right - size.Width;
                graphics.DrawString(text, font, brush, x, monitor.Top - size.Height - 16);
                graphics.DrawString(text, font, brush, x, monitor.Bottom + 16);
            }
            else
            {
                float y = monitor.Top + pixel * monitor.Height / ScreenArea.Height - size.Height / 2;
                if (endpoint) y = pixel == 0 ? monitor.Top : monitor.Bottom - size.Height;
                graphics.DrawString(text, font, brush, monitor.Left - size.Width - 17, y);
                graphics.DrawString(text, font, brush, monitor.Right + 17, y);
            }
        }

        private void PaintPins(Graphics graphics, bool vertical)
        {
            IList<int> pins = Pins(vertical);
            for (int i = 0; i < pins.Count; i++)
            {
                bool selected = selectedPin == i && pinVertical == vertical;
                bool hovered = hoverPin == i && hoverVertical == vertical;
                Color color = selected ? Color.FromArgb(255, 196, 96) : hovered ? Color.White : Color.FromArgb(113, 201, 255);
                using (var fill = new SolidBrush(color))
                using (var line = new Pen(Color.FromArgb(selected ? 190 : 105, color), selected ? 2 : 1))
                {
                    float position = vertical ? monitor.Left + pins[i] * monitor.Width / ScreenArea.Width : monitor.Top + pins[i] * monitor.Height / ScreenArea.Height;
                    if (vertical)
                    {
                        graphics.DrawLine(line, position, monitor.Top, position, monitor.Bottom);
                        graphics.FillPolygon(fill, new[] { new PointF(position, monitor.Top), new PointF(position - 6, monitor.Top - 13), new PointF(position + 6, monitor.Top - 13) });
                        graphics.FillPolygon(fill, new[] { new PointF(position, monitor.Bottom), new PointF(position - 6, monitor.Bottom + 13), new PointF(position + 6, monitor.Bottom + 13) });
                    }
                    else
                    {
                        graphics.DrawLine(line, monitor.Left, position, monitor.Right, position);
                        graphics.FillPolygon(fill, new[] { new PointF(monitor.Left, position), new PointF(monitor.Left - 13, position - 6), new PointF(monitor.Left - 13, position + 6) });
                        graphics.FillPolygon(fill, new[] { new PointF(monitor.Right, position), new PointF(monitor.Right + 13, position - 6), new PointF(monitor.Right + 13, position + 6) });
                    }
                }
            }
        }
    }
}
