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

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect area, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect area, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    }
}
