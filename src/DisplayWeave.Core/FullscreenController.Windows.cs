using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave
{
    // Reading windows and processes: identity, state, and the Win32 declarations the controller uses.
    public sealed partial class FullscreenController
    {
        public static bool TryInspect(IntPtr handle, out ApplicationIdentity identity, out string error)
        {
            identity = null;
            error = String.Empty;
            uint pid;
            if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || GetAncestor(handle, 2) != handle ||
                GetWindow(handle, 4) != IntPtr.Zero || GetWindowThreadProcessId(handle, out pid) == 0)
            { error = "Select a visible application main window."; return false; }
            if (pid == OwnProcessId || pid <= 4)
            { error = "Select another application."; return false; }
            StringBuilder className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            string cls = className.ToString();
            // A folder window may run in the shell's process; it is only ever moved from outside, never hooked.
            bool folder = ShellProcessProtection.IsFolderWindow(cls);
            if (!folder && (ShellProcessProtection.IsShellProcessId(pid) || ShellProcessProtection.IsExcludedWindowClass(cls)))
            { error = ShellProcessProtection.RejectionMessage; return false; }
            if (cls.Length == 0) { error = "The application window class could not be read."; return false; }
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) { error = "Cannot inspect this application; elevated or protected apps may deny access."; return false; }
            try
            {
                StringBuilder path = new StringBuilder(32768);
                uint length = (uint)path.Capacity;
                long creation, exit, kernel, user;
                bool wow64;
                if (!QueryFullProcessImageName(process, 0, path, ref length) || !GetProcessTimes(process, out creation, out exit, out kernel, out user)
                    || !IsWow64Process(process, out wow64))
                { error = "Windows did not expose the application's identity or architecture."; return false; }
                if (LauncherProcessProtection.IsExcludedExecutable(path.ToString()))
                { error = LauncherProcessProtection.RejectionMessage; return false; }
                if (!folder && ShellProcessProtection.IsExcludedExecutable(path.ToString()))
                { error = ShellProcessProtection.RejectionMessage; return false; }
                identity = new ApplicationIdentity { Handle = handle, ProcessId = pid, CreationTime = creation,
                    ExecutablePath = path.ToString(), WindowClass = cls, Is32Bit = !Environment.Is64BitOperatingSystem || wow64 };
                if (StoreAppIdentity.IsHost(identity.ExecutablePath)) identity.AppId = StoreAppIdentity.Read(handle);
                return true;
            }
            finally { CloseHandle(process); }
        }

        private static bool TryReadWindowState(IntPtr handle, Rectangle monitor, HelperSession session,
            out Rectangle current, out Rectangle restored, out bool expanded, out bool minimized)
        {
            current = restored = Rectangle.Empty;
            expanded = false;
            minimized = IsIconic(handle);
            NativeRect bounds;
            if (!GetWindowRect(handle, out bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
            current = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            WindowPlacement placement = new WindowPlacement();
            placement.Length = (uint)Marshal.SizeOf(typeof(WindowPlacement));
            DisplayMonitor windowMonitor;
            if (GetWindowPlacement(handle, ref placement) && placement.NormalPosition.Right > placement.NormalPosition.Left &&
                placement.NormalPosition.Bottom > placement.NormalPosition.Top && (windowMonitor = DisplayMonitor.FromWindow(handle)) != null)
            {
                // WINDOWPLACEMENT uses workspace coordinates for ordinary top-level windows.
                int offsetX = windowMonitor.WorkingArea.Left - windowMonitor.Bounds.Left;
                int offsetY = windowMonitor.WorkingArea.Top - windowMonitor.Bounds.Top;
                restored = new Rectangle(placement.NormalPosition.Left + offsetX, placement.NormalPosition.Top + offsetY,
                    placement.NormalPosition.Right - placement.NormalPosition.Left,
                    placement.NormalPosition.Bottom - placement.NormalPosition.Top);
            }
            long style = WindowLong(handle, GwlStyle);
            bool borderless = (style & WsCaption) == 0;
            expanded = IsZoomed(handle) || placement.ShowCommand == 3 || (style & WsMaximize) != 0 ||
                (session != null && session.Enabled && session.NativeExpanded == true) ||
                (borderless && (RegionGeometry.NearlyCovers(current, monitor) ||
                    (session != null && session.Enabled && !session.NativeExpanded.HasValue && RegionGeometry.NearlyEquals(current, session.Region))));
            return true;
        }

        private static long WindowLong(IntPtr handle, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(handle, index).ToInt64() : GetWindowLong(handle, index);
        }

        private static void SetWindowLong(IntPtr handle, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(handle, index, new IntPtr(value)); else SetWindowLong32(handle, index, unchecked((int)value));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            internal uint Length, Flags, ShowCommand;
            internal NativePoint MinimumPosition, MaximumPosition;
            internal NativeRect NormalPosition;
        }

        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020,
            SwpNoOwnerZOrder = 0x0200, SwpNoSendChanging = 0x0400, SwpAsyncWindowPos = 0x4000;
        private const int GwlStyle = -16, GwlExStyle = -20;
        private const long WsCaption = 0x00C00000L, WsMaximize = 0x01000000L, WsThickFrame = 0x00040000L;
        // The title bar and sizing border; the dialog, window, client and static edges.
        private const long BorderStyles = WsCaption | WsThickFrame,BorderExStyles = 0x00000001L | 0x00000100L | 0x00000200L | 0x00020000L;
        // Overlays and capture tools: never-activated, click-through or always-on-top windows.
        private const long OverlayExStyles = 0x08000000L | 0x00000020L | 0x00000008L;
        // Chromium-based apps reset any move of their maximized window in WM_WINDOWPOSCHANGING, so the outside fit skips that message.
        private const uint FitFlags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging | SwpAsyncWindowPos;
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr handle, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int capacity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll")] private static extern bool IsWow64Process(IntPtr process, out bool wow64);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
