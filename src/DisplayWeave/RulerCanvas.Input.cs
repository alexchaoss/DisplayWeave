using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Mouse and keyboard editing of the ruler pins: hit testing, dragging, adding and removing dividers.
    internal sealed partial class RulerCanvas
    {
        private bool HitPin(Point point, out bool vertical, out int index)
        {
            vertical = true; index = -1;
            if (rulerLayout == null) return false;
            for (int axis = 0; axis < 2; axis++)
            {
                bool widthPin = axis == 0;
                IList<int> pins = Pins(widthPin);
                for (int i = 0; i < pins.Count; i++)
                {
                    float location = widthPin ? monitor.Left + pins[i] * monitor.Width / ScreenArea.Width : monitor.Top + pins[i] * monitor.Height / ScreenArea.Height;
                    bool hit = widthPin ? Math.Abs(point.X - location) <= 9 &&
                        ((point.Y >= monitor.Top - 18 && point.Y <= monitor.Top + 3) || (point.Y >= monitor.Bottom - 3 && point.Y <= monitor.Bottom + 18)) :
                        Math.Abs(point.Y - location) <= 9 && ((point.X >= monitor.Left - 18 && point.X <= monitor.Left + 3) ||
                        (point.X >= monitor.Right - 3 && point.X <= monitor.Right + 18));
                    if (hit) { vertical = widthPin; index = i; return true; }
                }
            }
            return false;
        }

        private bool HitRuler(Point point, out bool vertical)
        {
            vertical = true;
            if (point.X >= monitor.Left && point.X <= monitor.Right &&
                ((point.Y >= monitor.Top - 37 && point.Y < monitor.Top) || (point.Y > monitor.Bottom && point.Y <= monitor.Bottom + 37))) return true;
            vertical = false;
            return point.Y >= monitor.Top && point.Y <= monitor.Bottom &&
                ((point.X >= monitor.Left - 60 && point.X < monitor.Left) || (point.X > monitor.Right && point.X <= monitor.Right + 60));
        }

        private int PixelAt(Point point, bool vertical)
        {
            double fraction = vertical ? (point.X - monitor.Left) / Math.Max(1, monitor.Width) : (point.Y - monitor.Top) / Math.Max(1, monitor.Height);
            return (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * (vertical ? ScreenArea.Width : ScreenArea.Height), MidpointRounding.AwayFromZero);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (rulerLayout == null) return;
            bool vertical; int pin;
            if (HitPin(e.Location, out vertical, out pin))
            {
                SelectPin(vertical, pin);
                if (e.Button == MouseButtons.Right) { RemoveSelectedPin(); return; }
                if (e.Button != MouseButtons.Left) return;
                originalPixel = SelectedPinPixel;
                dragging = true; dragChanged = false; editingRaised = false; dragError = String.Empty;
                Capture = true;
                Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS;
                Invalidate(); return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (monitor.Contains(e.Location))
            {
                selectedPin = -1;
                if (PinSelectionChanged != null) PinSelectionChanged();
                IList<Zone> zones = LiveZones;
                for (int i = 0; i < zones.Count; i++)
                    if (DisplayRect(zones[i]).Contains(e.Location))
                    { SelectedIndex = i; if (SelectionChanged != null) SelectionChanged(); break; }
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                MoveSelectedPin(PixelAt(e.Location, pinVertical), false);
                return;
            }
            bool vertical; int pin;
            bool hit = HitPin(e.Location, out vertical, out pin);
            if (hoverPin != (hit ? pin : -1) || hoverVertical != vertical)
            { hoverPin = hit ? pin : -1; hoverVertical = vertical; Invalidate(); }
            Cursor = hit ? (vertical ? Cursors.SizeWE : Cursors.SizeNS) : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragging) { hoverPin = -1; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && e.Button == MouseButtons.Left) FinishDrag(false);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture) FinishDrag(false);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || rulerLayout == null) return;
            bool vertical; int pin;
            if (HitPin(e.Location, out vertical, out pin) || !HitRuler(e.Location, out vertical)) return;
            AddDividerAt(vertical, PixelAt(e.Location, vertical));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && dragging) { FinishDrag(true); e.Handled = true; return; }
            if (e.KeyCode == Keys.Delete && HasSelectedPin) { RemoveSelectedPin(); e.Handled = true; return; }
            if (!HasSelectedPin || rulerLayout == null || dragging) return;
            int direction = (pinVertical && e.KeyCode == Keys.Left) || (!pinVertical && e.KeyCode == Keys.Up) ? -1 :
                (pinVertical && e.KeyCode == Keys.Right) || (!pinVertical && e.KeyCode == Keys.Down) ? 1 : 0;
            if (direction == 0) return;
            long pixel = SelectedPinPixel + (long)direction * Math.Max(1, rulerLayout.GridStep) * (e.Shift ? 10 : 1);
            editingRaised = false;
            MoveSelectedPin((int)Math.Max(0, Math.Min(Int32.MaxValue, pixel)), true);
            editingRaised = false;
            e.Handled = true;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Delete || key == Keys.Escape || base.IsInputKey(keyData);
        }

        private void SelectPin(bool vertical, int index)
        {
            pinVertical = vertical; selectedPin = index;
            if (PinSelectionChanged != null) PinSelectionChanged();
            Invalidate();
        }

        private bool MoveSelectedPin(int pixel, bool finishImmediately)
        {
            if (rulerLayout == null || !HasSelectedPin) return false;
            int before = SelectedPinPixel;
            int grid = Math.Max(1, rulerLayout.GridStep);
            long snapped = (long)Math.Round((double)pixel / grid, MidpointRounding.AwayFromZero) * grid;
            if (snapped == before) return false;
            if (!editingRaised) { if (LayoutEditing != null) LayoutEditing(); editingRaised = true; }
            string error;
            if (!rulerLayout.TryMoveDivider(pinVertical, selectedPin, pixel, out error))
            {
                if (error != dragError) { dragError = error; SendNotice(error); }
                return false;
            }
            bool changed = SelectedPinPixel != before;
            if (changed)
            {
                dragChanged = true;
                if (PinSelectionChanged != null) PinSelectionChanged();
                Invalidate();
                if (finishImmediately) { editingRaised = false; dragChanged = false; if (LayoutEdited != null) LayoutEdited(); }
            }
            return changed;
        }

        private void FinishDrag(bool cancel)
        {
            if (!dragging) return;
            if (cancel && dragChanged && rulerLayout != null)
            {
                string error;
                if (!rulerLayout.TryMoveDivider(pinVertical, selectedPin, originalPixel, out error)) SendNotice(error);
                else SendNotice("Pin move cancelled.");
            }
            bool edited = dragChanged;
            dragging = false; dragChanged = false; editingRaised = false;
            Capture = false; Cursor = Cursors.Default;
            if (PinSelectionChanged != null) PinSelectionChanged();
            if (edited && LayoutEdited != null) LayoutEdited();
            Invalidate();
        }

        internal bool AddDivider(bool vertical)
        {
            if (rulerLayout == null) return false;
            IList<int> pins = Pins(vertical);
            int extent = vertical ? ScreenArea.Width : ScreenArea.Height;
            int previous = 0, largestStart = 0, largestWidth = 0;
            for (int i = 0; i <= pins.Count; i++)
            {
                int next = i == pins.Count ? extent : pins[i];
                if (next - previous > largestWidth) { largestStart = previous; largestWidth = next - previous; }
                previous = next;
            }
            return AddDividerAt(vertical, largestStart + largestWidth / 2);
        }

        private bool AddDividerAt(bool vertical, int pixel)
        {
            if (rulerLayout == null) return false;
            if (dragging) FinishDrag(false);
            List<int> previous = new List<int>(Pins(vertical));
            if (LayoutEditing != null) LayoutEditing();
            string error;
            if (!rulerLayout.TryAddDivider(vertical, pixel, out error)) { SendNotice(error); return false; }
            IList<int> current = Pins(vertical);
            for (int i = 0; i < current.Count; i++) if (!previous.Contains(current[i])) { SelectPin(vertical, i); break; }
            SelectedIndex = Math.Max(0, Math.Min(SelectedIndex, LiveZones.Count - 1));
            if (LayoutEdited != null) LayoutEdited();
            SendNotice(SelectedPinLabel + ". Drag either matching ruler pin to adjust it.");
            Invalidate(); return true;
        }

        internal bool RemoveSelectedPin()
        {
            if (rulerLayout == null || !HasSelectedPin) { SendNotice("Select a pin on a ruler first."); return false; }
            if (dragging) FinishDrag(false);
            if (LayoutEditing != null) LayoutEditing();
            string error;
            if (!rulerLayout.TryRemoveDivider(pinVertical, selectedPin, out error)) { SendNotice(error); return false; }
            selectedPin = -1; hoverPin = -1;
            SelectedIndex = Math.Max(0, Math.Min(SelectedIndex, LiveZones.Count - 1));
            if (PinSelectionChanged != null) PinSelectionChanged();
            if (LayoutEdited != null) LayoutEdited();
            SendNotice("Divider removed; adjacent cells merged.");
            Invalidate(); return true;
        }

        private int FindPin(bool vertical, int pixel)
        {
            IList<int> pins = Pins(vertical);
            for (int i = 0; i < pins.Count; i++) if (pins[i] == pixel) return i;
            return -1;
        }

        private int HoverPinPixel()
        {
            IList<int> pins = Pins(hoverVertical);
            return hoverPin >= 0 && hoverPin < pins.Count ? pins[hoverPin] : -1;
        }

        private void SendNotice(string text)
        {
            if (!String.IsNullOrEmpty(text) && Notice != null) Notice(text);
        }
    }
}
