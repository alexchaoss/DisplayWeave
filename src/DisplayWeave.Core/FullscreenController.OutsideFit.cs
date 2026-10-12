using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    // Windows moved from outside, with nothing loaded into the app: maximized and borderless fullscreen windows,
    // and windows shown without their title bar under a Borderless in region rule.
    public sealed partial class FullscreenController
    {
        // Windows fitted from outside, pinned to their process identity for restoration.
        private sealed class MaximizedFit
        {
            internal ApplicationIdentity Target;
            internal int Attempts;
            internal DateTime LastAttempt;
            internal Rectangle Region;
            // Why the component is not used, kept after a successful fit clears the failure.
            internal string Reason;
            // A Borderless in region rule removed these styles; they and the window's first place return on release.
            internal bool Styled;
            internal long RemovedStyle, RemovedExStyle;
            internal Rectangle OriginalBounds;
        }
        private readonly Dictionary<IntPtr, MaximizedFit> maximized = new Dictionary<IntPtr, MaximizedFit>();

        private const int MaximumFitAttempts = 4;
        // A game can take a second to rebuild its swap chain before it handles the move.
        private static readonly TimeSpan FitRetryDelay = TimeSpan.FromSeconds(2);

        // Before an update: forget the windows fitted from outside instead of returning them to their whole monitor.
        // The next version adopts the maximized ones where they are, so they do not flash across the display; a borderless
        // fullscreen window stays in its region and is taken over when it next enters fullscreen. A window shown without
        // its title bar gets it back on exit as usual: the next version could not know its original style.
        public void LeaveFittedWindows()
        {
            foreach (IntPtr handle in new List<IntPtr>(maximized.Keys)) if (!maximized[handle].Styled) maximized.Remove(handle);
        }

        private void FitMaximized(ApplicationIdentity target, Rectangle region)
        {
            IntPtr handle = target.Handle;
            MaximizedFit fit;
            maximized.TryGetValue(handle, out fit);
            if (fit != null && fit.Target.Key != target.Key) { maximized.Remove(handle); fit = null; }
            Rectangle current, wanted, frame;
            if (!TryMaximizedBounds(handle, region, out current, out wanted, out frame)) return;
            // A restored window is back where Windows keeps its normal position; nothing to undo.
            if (IsIconic(handle) || !(IsZoomed(handle) || BorderlessFullscreen(handle, frame, fit)))
            {
                if (fit == null || IsIconic(handle)) return;
                maximized.Remove(handle);
                // Restoring is the retry for a failed fit: show the component's reason again, not the old fit failure.
                if (fit.Attempts >= MaximumFitAttempts)
                {
                    if (String.IsNullOrEmpty(fit.Reason)) failureHistory.ClearTarget(target); else failureHistory.Record(target, fit.Reason);
                }
                return;
            }
            if (current == wanted)
            {
                // Already in its region, as the previous version leaves it during an update: adopt it, so exiting returns it.
                if (fit == null) maximized.Add(handle, new MaximizedFit { Target = target, Region = region,
                    Reason = OutsideOnlyReason(target.WindowClass) ?? failureHistory.For(target) });
                else if (fit.Attempts != 0) { fit.Attempts = 0; failureHistory.ClearTarget(target); }
                return;
            }
            if (fit == null) maximized.Add(handle, fit = new MaximizedFit { Target = target,
                Reason = OutsideOnlyReason(target.WindowClass) ?? failureHistory.For(target) });
            fit.Region = region;
            if (fit.Attempts >= MaximumFitAttempts || DateTime.UtcNow - fit.LastAttempt < FitRetryDelay) return;
            fit.LastAttempt = DateTime.UtcNow;
            // A borderless game that resizes itself back once always will: each further move only risks crashing
            // an overlay that hooks its graphics.
            bool zoomed = IsZoomed(handle);
            if (++fit.Attempts >= (zoomed ? MaximumFitAttempts : 2))
            {
                fit.Attempts = MaximumFitAttempts;
                RecordFailure(target, zoomed ? "It keeps changing its maximized size, so DisplayWeave stopped fitting it. Restore and maximize it again to retry"
                    : "It resized itself back to fill its monitor, so DisplayWeave stopped moving it. Set it to Windowed and give it a Borderless in region rule on App rules", true);
                return;
            }
            if (!SetWindowPos(handle, IntPtr.Zero, wanted.X, wanted.Y, wanted.Width, wanted.Height, FitFlags))
            {
                fit.Attempts = MaximumFitAttempts;
                RecordFailure(target, "Windows did not move " + Path.GetFileName(target.ExecutablePath) + " (Windows error " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "). An app running as administrator needs DisplayWeave restarted as administrator.");
                return;
            }
            LastMessage = "Fitted the " + Path.GetFileName(target.ExecutablePath) + " window into its region. Nothing is loaded into the app.";
        }

        // A Borderless in region rule: the window without its title bar and border, sized to its region, as a game
        // set to Windowed accepts. Nothing is loaded into the app. Minimized and maximized windows are left as they are.
        private void ShowBorderless(ApplicationIdentity target, Rectangle region)
        {
            IntPtr handle = target.Handle;
            readyApps.Add(new HandledApp { Target = target, Region = region, State = HandledAppState.Ready });
            MaximizedFit fit;
            maximized.TryGetValue(handle, out fit);
            if (fit != null && (fit.Target.Key != target.Key || !fit.Styled)) { maximized.Remove(handle); fit = null; }
            NativeRect bounds;
            // Changing a style waits for the window's thread; a window that stopped responding would block this one.
            if (IsIconic(handle) || IsZoomed(handle) || IsHungAppWindow(handle) || !GetWindowRect(handle, out bounds)) return;
            Rectangle current = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            long style = WindowLong(handle, GwlStyle), extended = WindowLong(handle, GwlExStyle);
            // Some games put their title bar back and grow the window so the picture still fills the region, the title
            // bar above it (off screen for a region at the top of the display). That is fitted, not a fight over styles.
            if (((style & BorderStyles) == 0 && (extended & BorderExStyles) == 0 && current == region) || ClientBounds(handle) == region)
            {
                if (fit != null && fit.Attempts != 0 && fit.Attempts < MaximumFitAttempts) { fit.Attempts = 0; failureHistory.ClearTarget(target); }
                return;
            }
            if (fit == null) maximized.Add(handle, fit = new MaximizedFit { Target = target, Styled = true, OriginalBounds = current });
            fit.Region = region;
            if (fit.Attempts >= MaximumFitAttempts || DateTime.UtcNow - fit.LastAttempt < FitRetryDelay) return;
            fit.LastAttempt = DateTime.UtcNow;
            if (++fit.Attempts == MaximumFitAttempts)
            {
                RecordFailure(target, "It keeps putting back its title bar or size, so DisplayWeave stopped. Check that it is set to Windowed, then pause and resume handling to retry", true);
                return;
            }
            // Styles the app added back since its first fit return with it on release too.
            fit.RemovedStyle |= style & BorderStyles;
            fit.RemovedExStyle |= extended & BorderExStyles;
            SetWindowLong(handle, GwlStyle, style & ~BorderStyles);
            SetWindowLong(handle, GwlExStyle, extended & ~BorderExStyles);
            if (!SetWindowPos(handle, IntPtr.Zero, region.X, region.Y, region.Width, region.Height, FitFlags | SwpFrameChanged))
            {
                fit.Attempts = MaximumFitAttempts;
                RecordFailure(target, "Windows did not move " + Path.GetFileName(target.ExecutablePath) + " (Windows error " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "). An app running as administrator needs DisplayWeave restarted as administrator.");
                return;
            }
            LastMessage = "Showed the " + Path.GetFileName(target.ExecutablePath) + " window without its title bar in its region. Nothing is loaded into the app.";
        }

        // The window's client area in screen coordinates, or empty when Windows does not report it.
        private static Rectangle ClientBounds(IntPtr handle)
        {
            NativeRect client;
            NativePoint origin = new NativePoint();
            if (!GetClientRect(handle, out client) || !ClientToScreen(handle, ref origin)) return Rectangle.Empty;
            return new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
        }

        // Gives a window shown borderless its title bar, border and first place back. A minimized or maximized
        // window keeps its place; only its frame returns.
        internal static void RestoreBorderless(IntPtr handle, long removedStyle, long removedExStyle, Rectangle original)
        {
            if (IsHungAppWindow(handle)) return;
            SetWindowLong(handle, GwlStyle, WindowLong(handle, GwlStyle) | removedStyle);
            SetWindowLong(handle, GwlExStyle, WindowLong(handle, GwlExStyle) | removedExStyle);
            uint flags = FitFlags | SwpFrameChanged | (IsIconic(handle) || IsZoomed(handle) ? SwpNoMove | SwpNoSize : 0);
            SetWindowPos(handle, IntPtr.Zero, original.X, original.Y, original.Width, original.Height, flags);
        }

        private void FitFromOutside(ApplicationIdentity target, Rectangle region)
        {
            FitMaximized(target, region);
            readyApps.Add(new HandledApp { Target = target, Region = region, State = HandledAppState.Ready,
                Error = OutsideOnlyReason(target.WindowClass) ?? String.Empty });
        }

        private bool OutsideFitOnly(ApplicationIdentity target)
        {
            return failed.Contains(target.Key) || OutsideOnlyReason(target.WindowClass) != null;
        }

        // Why a window class only ever gets the outside maximize fit; null when its component may be used.
        internal static string OutsideOnlyReason(string windowClass)
        {
            if (ChromiumWindowProtection.IsChromiumWindow(windowClass)) return ChromiumWindowProtection.Message;
            if (ShellProcessProtection.IsFolderWindow(windowClass)) return ShellProcessProtection.FolderMessage;
            return null;
        }

        // A fitted window stays in its region under a MaximizeInRegion rule or automatic handling,
        // or under a Force rule while its component is unavailable.
        internal static bool KeepsMaximizedFit(RegionControlMode mode, bool componentFailed)
        {
            return mode == RegionControlMode.Maximize || mode == RegionControlMode.Automatic ||
                (mode == RegionControlMode.Force && componentFailed);
        }

        // Windows no longer fitted by a rule or the automatic fallback go back to filling their monitor.
        private void ReturnUnruledMaximized(ApplicationRules rules, LayoutSettings layout)
        {
            foreach (MaximizedFit fit in new List<MaximizedFit>(maximized.Values))
            {
                try
                {
                    ApplicationIdentity current;
                    string error;
                    if (TryInspect(fit.Target.Handle, out current, out error) && current.Key == fit.Target.Key)
                    {
                        ApplicationRule rule = MatchingRule(rules, current);
                        Rectangle region;
                        bool resolved = RegionGeometry.TryResolveRegion(rule, layout, out region);
                        RegionControlMode mode = AutomaticRegionPolicy.Decide(layout.AutomaticRegions, false, released.Contains(current.ProcessKey),
                            rule == null ? (ApplicationAction?)null : rule.Action, resolved);
                        if (fit.Styled ? mode == RegionControlMode.Borderless : KeepsMaximizedFit(mode, OutsideFitOnly(current))) continue;
                        ReturnToMonitor(fit);
                    }
                }
                catch (Exception ex) { Quarantine(fit.Target, ex); } // the other fitted windows are still checked
                maximized.Remove(fit.Target.Handle);
            }
        }

        private void ReturnAllMaximized()
        {
            foreach (MaximizedFit fit in maximized.Values)
            {
                ApplicationIdentity current;
                string error;
                if (TryInspect(fit.Target.Handle, out current, out error) && current.Key == fit.Target.Key) ReturnToMonitor(fit);
            }
            maximized.Clear();
        }

        private static void ReturnToMonitor(MaximizedFit fit)
        {
            IntPtr handle = fit.Target.Handle;
            if (fit.Styled) { RestoreBorderless(handle, fit.RemovedStyle, fit.RemovedExStyle, fit.OriginalBounds); return; }
            Rectangle current, wanted, frame;
            DisplayMonitor monitor = DisplayMonitor.FromWindow(handle);
            bool zoomed = IsZoomed(handle);
            // A borderless fullscreen window goes back to covering its whole monitor, taskbar included.
            if (monitor == null || IsIconic(handle) || !TryMaximizedBounds(handle, zoomed ? monitor.WorkingArea : monitor.Bounds,
                out current, out wanted, out frame) || current == wanted || !(zoomed || BorderlessFullscreen(handle, frame, fit))) return;
            SetWindowPos(handle, IntPtr.Zero, wanted.X, wanted.Y, wanted.Width, wanted.Height, FitFlags);
        }

        private static bool TryMaximizedBounds(IntPtr handle, Rectangle target, out Rectangle current, out Rectangle wanted, out Rectangle visible)
        {
            current = wanted = visible = Rectangle.Empty;
            NativeRect window, frame;
            if (!GetWindowRect(handle, out window)) return false;
            if (DwmGetWindowAttribute(handle, 9, out frame, Marshal.SizeOf(typeof(NativeRect))) != 0) frame = window; // DWMWA_EXTENDED_FRAME_BOUNDS
            current = Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom);
            visible = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            wanted = RegionGeometry.MaximizedBounds(current, visible, target);
            return true;
        }

        // A window without a title bar covering its monitor is in borderless (windowed) fullscreen; once fitted, it covers
        // its region instead. Topmost, click-through and never-activated windows are overlays or capture tools: left alone.
        private static bool BorderlessFullscreen(IntPtr handle, Rectangle frame, MaximizedFit fit)
        {
            long style = WindowLong(handle, GwlStyle), extended = WindowLong(handle, GwlExStyle);
            DisplayMonitor monitor = DisplayMonitor.FromWindow(handle);
            return (style & WsCaption) == 0 && (extended & OverlayExStyles) == 0 && monitor != null &&
                (RegionGeometry.NearlyCovers(frame, monitor.Bounds) || (fit != null && RegionGeometry.NearlyEquals(frame, fit.Region)));
        }
    }
}
