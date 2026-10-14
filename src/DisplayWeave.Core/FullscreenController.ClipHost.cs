using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    // Experiment, off unless DISPLAYWEAVE_CLIPHOST=1: instead of chasing a fullscreen Chromium window every time it
    // spreads back over the monitor (which it does on every click or focus change, deciding its own placement), reparent
    // it into a borderless, region-sized window DisplayWeave owns. Windows clips a child to its parent, so the app can no
    // longer paint past its region and there is nothing to chase. Nothing is loaded into the app. Keyboard focus across
    // processes is not handled here, so this is for watching (mouse) first; see docs/testing.md for the measured result.
    public sealed partial class FullscreenController
    {
        private static readonly bool ClipHostEnv = ClipHostRequested();
        private bool clipHost = ClipHostEnv;
        // Self-tests set this; live runs read the environment variable once at start.
        internal bool ClipHost { get { return clipHost; } set { clipHost = value; } }

        private static bool ClipHostRequested()
        {
            string value = Environment.GetEnvironmentVariable("DISPLAYWEAVE_CLIPHOST");
            return value != null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        // Reparent the window into its host (created on first use), then keep it filling the host's client area. Chromium's
        // own moves back to the monitor land on a child and are clipped to the host, so they need no correction of their own.
        private void FitHosted(IntPtr handle, MaximizedFit fit, Rectangle region)
        {
            if (fit.Host == IntPtr.Zero)
            {
                fit.HostStyle = WindowLong(handle, GwlStyle);
                fit.HostExStyle = WindowLong(handle, GwlExStyle);
                NativeRect bounds;
                if (GetWindowRect(handle, out bounds)) fit.OriginalBounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                fit.Host = CreateWindowEx(WsExToolWindow, "Static", String.Empty, HostStyleFlags,
                    region.X, region.Y, region.Width, region.Height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (fit.Host == IntPtr.Zero)
                {
                    fit.Attempts = MaximumFitAttempts;
                    RecordFailure(fit.Target, "Windows did not create the clip host (Windows error " +
                        Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ").");
                    return;
                }
                SetWindowLong(handle, GwlStyle, (fit.HostStyle & ~WsPopup) | WsChild);
                SetParent(handle, fit.Host);
                LastMessage = "Clipping the " + Path.GetFileName(fit.Target.ExecutablePath) +
                    " fullscreen window inside its region (experimental clip host). Nothing is loaded into the app.";
            }
            else SetWindowPos(fit.Host, IntPtr.Zero, region.X, region.Y, region.Width, region.Height, SwpNoZOrder | SwpNoActivate);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, region.Width, region.Height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        // Undo FitHosted. The order matters: un-parent the app FIRST, because destroying a parent destroys its children, so
        // the host must never be destroyed while the app is still its child. Safe to call on any fit; it does nothing without
        // a host. Static so the restore paths (ReturnToMonitor, Dispose) can reach it.
        private static void ReleaseHost(MaximizedFit fit)
        {
            IntPtr host = fit.Host;
            if (host == IntPtr.Zero) return;
            fit.Host = IntPtr.Zero;
            IntPtr child = fit.Target.Handle;
            if (IsWindow(child))
            {
                SetParent(child, IntPtr.Zero);
                SetWindowLong(child, GwlStyle, fit.HostStyle);
                SetWindowLong(child, GwlExStyle, fit.HostExStyle);
                SetWindowPos(child, IntPtr.Zero, fit.OriginalBounds.X, fit.OriginalBounds.Y, fit.OriginalBounds.Width, fit.OriginalBounds.Height,
                    SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            DestroyWindow(host);
        }

        private const long WsChild = 0x40000000L, WsPopup = 0x80000000L;
        private const uint HostStyleFlags = 0x92000000; // WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN
        private const int WsExToolWindow = 0x00000080;   // keeps the host out of the taskbar and Alt+Tab

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr handle);
    }
}
