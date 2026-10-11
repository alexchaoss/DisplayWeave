using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave
{
    public sealed class WindowCandidate
    {
        public IntPtr Handle;
        public string Title;
        // The app behind the window, read when it was listed.
        public ApplicationIdentity Identity;

        public override string ToString()
        {
            return Title ?? String.Empty;
        }
    }

    // Lists the top-level application windows DisplayWeave may handle. It never changes a window.
    public static class WindowManager
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsChild = 0x40000000L;
        private const long WsExToolWindow = 0x00000080L;
        private const int DwmwaCloaked = 14;
        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;

        public static IntPtr ForegroundWindow { get { return Native.GetForegroundWindow(); } }

        public static void EnableDpiAwareness()
        {
            // This must run before creating a WinForms window. Windows may reject
            // a change when a manifest or host has already selected a DPI mode.
            try
            {
                if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
                if (Marshal.GetLastWin32Error() == 5) return;
            }
            catch (EntryPointNotFoundException) { }
            try
            {
                int result = Native.SetProcessDpiAwareness(2);
                if (result == 0 || result == unchecked((int)0x80070005)) return;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            try { Native.SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }

        public static List<WindowCandidate> ListWindows()
        {
            List<WindowCandidate> result = new List<WindowCandidate>();
            EnumWindowsDelegate callback = delegate(IntPtr handle, IntPtr parameter)
            {
                string title;
                ApplicationIdentity identity;
                if (IsEligible(handle, out title, out identity)) result.Add(new WindowCandidate { Handle = handle, Title = title, Identity = identity });
                return true;
            };
            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            result.Sort(delegate(WindowCandidate left, WindowCandidate right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
            });
            return result;
        }

        private static bool IsEligible(IntPtr handle, out string title, out ApplicationIdentity identity)
        {
            title = String.Empty;
            identity = null;
            if (handle == IntPtr.Zero || !Native.IsWindow(handle) || !Native.IsWindowVisible(handle) ||
                handle == Native.GetDesktopWindow() || handle == Native.GetShellWindow() || Native.GetAncestor(handle, 2) != handle)
                return false;
            uint processId;
            uint threadId = Native.GetWindowThreadProcessId(handle, out processId);
            if (threadId == 0 || processId == 0 || processId == 4 || processId == OwnProcessId) return false;
            long style;
            long exStyle;
            if (!TryGetStyle(handle, GwlStyle, out style) || !TryGetStyle(handle, GwlExStyle, out exStyle)) return false;
            if ((style & WsChild) != 0 || (exStyle & WsExToolWindow) != 0 || IsCloaked(handle)) return false;
            string className = ReadClassName(handle);
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" ||
                className == "Shell_SecondaryTrayWnd" || className == "#32768" || className == "tooltips_class32" || className == "SysShadow")
                return false;
            StringBuilder text = new StringBuilder(1024);
            Native.GetWindowText(handle, text, text.Capacity);
            title = text.ToString().Trim();
            if (title.Length == 0) return false;
            // Excludes the shell process except its folder windows, which are only ever moved from outside.
            string rejection;
            return FullscreenController.TryInspect(handle, out identity, out rejection);
        }

        private static string ReadClassName(IntPtr handle)
        {
            StringBuilder result = new StringBuilder(256);
            Native.GetClassName(handle, result, result.Capacity);
            return result.ToString();
        }

        private static bool IsCloaked(IntPtr handle)
        {
            try
            {
                int cloaked;
                return Native.DwmGetWindowAttribute(handle, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static bool TryGetStyle(IntPtr handle, int index, out long value)
        {
            Native.SetLastError(0);
            IntPtr result = IntPtr.Size == 8 ? Native.GetWindowLongPtr64(handle, index) : new IntPtr(Native.GetWindowLong32(handle, index));
            int error = Marshal.GetLastWin32Error();
            value = result.ToInt64();
            return result != IntPtr.Zero || error == 0;
        }

        private delegate bool EnumWindowsDelegate(IntPtr handle, IntPtr parameter);

        private static class Native
        {
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr handle);
            [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr handle, uint flags);
            [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetClassName(IntPtr handle, StringBuilder text, int capacity);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong32(IntPtr handle, int index);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
            [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int awareness);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDPIAware();
            [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
            [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
        }
    }
}
