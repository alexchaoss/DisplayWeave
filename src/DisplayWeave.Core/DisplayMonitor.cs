using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    // A monitor's device name, bounds and scaling, read straight from Windows so the engine needs no UI library.
    public sealed class DisplayMonitor
    {
        private const uint MonitorDefaultToNearest = 2;

        public string DeviceName { get; private set; }
        public Rectangle Bounds { get; private set; }
        public Rectangle WorkingArea { get; private set; }
        // Windows display scaling, such as 125; 100 when Windows cannot report it.
        public int ScalePercent { get; private set; }
        private string deviceId;

        // The monitor's device path, which stays the same when Windows renumbers the \.\DISPLAYn names; empty if unknown.
        // It identifies the monitor model and port, so it never goes into a diagnostic report.
        public string DeviceId
        {
            get
            {
                if (deviceId != null) return deviceId;
                DisplayDevice device = new DisplayDevice();
                device.Size = Marshal.SizeOf(typeof(DisplayDevice));
                return deviceId = EnumDisplayDevices(DeviceName, 0, ref device, 1) ? device.DeviceID ?? String.Empty : String.Empty; // EDD_GET_DEVICE_INTERFACE_NAME
            }
        }

        public static List<DisplayMonitor> All()
        {
            List<DisplayMonitor> monitors = new List<DisplayMonitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref NativeRect area, IntPtr data)
            {
                DisplayMonitor found = Read(monitor);
                if (found != null) monitors.Add(found);
                return true;
            }, IntPtr.Zero);
            return monitors;
        }

        public static DisplayMonitor Find(string deviceName)
        {
            foreach (DisplayMonitor monitor in All())
                if (String.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return monitor;
            return null;
        }

        // The nearest monitor, like WinForms' Screen.FromHandle; null only if Windows cannot describe it.
        public static DisplayMonitor FromWindow(IntPtr window)
        {
            return Read(MonitorFromWindow(window, MonitorDefaultToNearest));
        }

        // The monitor with the largest overlap, or the nearest one, like WinForms' Screen.FromRectangle.
        public static DisplayMonitor FromRectangle(Rectangle area)
        {
            NativeRect rect = new NativeRect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
            return Read(MonitorFromRect(ref rect, MonitorDefaultToNearest));
        }

        // Exclusive fullscreen centered by the graphics driver lines up with a region only when the region sits in
        // the middle of its monitor. The mode is the region's width and, for a region as tall as the layout, the
        // monitor's full height (the taskbar is hidden in exclusive fullscreen).
        public static bool TryCenteredMode(Rectangle region, Rectangle monitor, Rectangle layoutArea, out Size mode)
        {
            bool fullHeight = region.Top == layoutArea.Top && region.Bottom == layoutArea.Bottom;
            Rectangle shown = fullHeight ? new Rectangle(region.X, monitor.Y, region.Width, monitor.Height) : region;
            mode = shown.Size;
            return shown.Width > 0 && shown.Height > 0 && monitor.Contains(shown) &&
                Math.Abs((long)(shown.Left - monitor.Left) - (monitor.Right - shown.Right)) <= 2 &&
                Math.Abs((long)(shown.Top - monitor.Top) - (monitor.Bottom - shown.Bottom)) <= 2;
        }

        // Whether Windows lists this resolution for the monitor, at any refresh rate.
        public bool OffersMode(Size mode)
        {
            DevMode device = new DevMode();
            device.Size = (ushort)Marshal.SizeOf(typeof(DevMode));
            for (int i = 0; EnumDisplaySettings(DeviceName, i, ref device); i++)
                if (device.PelsWidth == mode.Width && device.PelsHeight == mode.Height) return true;
            return false;
        }

        private static DisplayMonitor Read(IntPtr monitor)
        {
            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            return new DisplayMonitor
            {
                DeviceName = info.Device,
                Bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                WorkingArea = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
                ScalePercent = ReadScale(monitor)
            };
        }

        private static int ReadScale(IntPtr monitor)
        {
            uint dpiX, dpiY;
            try { if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0) return (int)Math.Round(dpiX * 100.0 / 96); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 100;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType,
                Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect area, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect area, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode settings);
    }
}
