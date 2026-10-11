using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave.Prototype
{
    public sealed class WindowChoice
    {
        public IntPtr Handle;
        public uint ProcessId;
        public long ProcessCreated;
        public string Executable;
        public string Title;

        public override string ToString()
        {
            return Title + "  —  " + Path.GetFileName(Executable) + " (" + ProcessId + ")";
        }
    }

    // Saved before the only mutation the worker makes to the selected window.
    public sealed class WindowSnapshot
    {
        public string Hwnd { get; set; }
        public int ProcessId { get; set; }
        public int ThreadId { get; set; }
        public string ProcessCreatedUtcFileTime { get; set; }
        public string Executable { get; set; }
        public string ClassName { get; set; }
        public string Style { get; set; }
        public string ExStyle { get; set; }
        public int ShowCommand { get; set; }
        public int Flags { get; set; }
        public int[] MinPosition { get; set; }
        public int[] MaxPosition { get; set; }
        public int[] NormalPosition { get; set; }
        public int[] WindowRect { get; set; }
        public bool BorderlessApplied { get; set; }
    }

    public static class Win32
    {
        private const long WsChild = 0x40000000L;
        private const long WsExToolWindow = 0x00000080L;
        public const long FrameBits = 0x00C40000L; // WS_CAPTION | WS_THICKFRAME
        public const long EdgeBits = 0x00020301L;  // WS_EX_DLGMODALFRAME | WINDOWEDGE | CLIENTEDGE | STATICEDGE
        private const int GwlStyle = -16, GwlExStyle = -20;
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;
        private const uint ProcessQueryLimited = 0x1000, Synchronize = 0x00100000, EventModifyState = 0x0002;
        private const int SwRestore = 9;
        private static readonly HashSet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
            "TextInputHost.exe", "LockApp.exe", "dwm.exe", "DisplayWeave.exe", "DisplayWeavePrototype.exe", "RegionPresenter.exe",
            "RegionDisplayLease.exe", "ProjectionProbe.exe"
        };
        private static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32768", "tooltips_class32", "SysShadow",
            "DisplayWeaveProjectionPreview"
        };

        // Pure part of eligibility so it can be self-tested without windows.
        public static bool ExcludedByName(string executable, string className)
        {
            return String.IsNullOrEmpty(executable) || ShellProcesses.Contains(Path.GetFileName(executable)) || ShellClasses.Contains(className ?? String.Empty);
        }

        public static List<WindowChoice> ListWindows()
        {
            var result = new List<WindowChoice>();
            uint own = (uint)Process.GetCurrentProcess().Id;
            EnumWindowsProc callback = delegate (IntPtr hwnd, IntPtr unused)
            {
                uint pid;
                if (!IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return true;
                if (GetWindowThreadProcessId(hwnd, out pid) == 0 || pid == own || pid <= 4) return true;
                long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64(), ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                if ((style & WsChild) != 0 || (ex & WsExToolWindow) != 0 || IsCloaked(hwnd)) return true;
                string title = Text(hwnd);
                if (title.Length == 0) return true;
                long created; string path;
                if (!ProcessIdentity(pid, out created, out path) || ExcludedByName(path, ClassName(hwnd))) return true;
                result.Add(new WindowChoice { Handle = hwnd, ProcessId = pid, ProcessCreated = created, Executable = path, Title = title });
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
            return result;
        }

        public static bool ProcessIdentity(uint pid, out long created, out string path)
        {
            created = 0; path = null;
            IntPtr process = OpenProcess(ProcessQueryLimited, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long exit, kernel, user;
                if (!GetProcessTimes(process, out created, out exit, out kernel, out user)) return false;
                var text = new StringBuilder(32768); uint size = (uint)text.Capacity;
                if (!QueryFullProcessImageName(process, 0, text, ref size)) return false;
                path = text.ToString();
                return true;
            }
            finally { CloseHandle(process); }
        }

        public static string Text(IntPtr hwnd) { var text = new StringBuilder(1024); GetWindowText(hwnd, text, text.Capacity); return text.ToString().Trim(); }
        public static string ClassName(IntPtr hwnd) { var text = new StringBuilder(256); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }

        // Exact identity: HWND, PID, creating thread, process creation time,
        // executable and class. A PID or HWND alone can be reused.
        public static bool SameWindow(WindowSnapshot snapshot)
        {
            IntPtr hwnd = new IntPtr(Int64.Parse(snapshot.Hwnd));
            uint pid; long created; string path;
            if (!IsWindow(hwnd)) return false;
            uint thread = GetWindowThreadProcessId(hwnd, out pid);
            return thread == (uint)snapshot.ThreadId && pid == (uint)snapshot.ProcessId && ProcessIdentity(pid, out created, out path) &&
                created.ToString() == snapshot.ProcessCreatedUtcFileTime && String.Equals(path, snapshot.Executable, StringComparison.OrdinalIgnoreCase) &&
                ClassName(hwnd) == snapshot.ClassName;
        }

        public static WindowSnapshot Capture(IntPtr hwnd)
        {
            uint pid; uint thread = GetWindowThreadProcessId(hwnd, out pid);
            long created; string path;
            if (thread == 0 || !ProcessIdentity(pid, out created, out path)) throw new InvalidOperationException("The selected window's process identity is unavailable.");
            var placement = new WindowPlacement { Length = Marshal.SizeOf(typeof(WindowPlacement)) };
            Rect rect;
            if (!GetWindowPlacement(hwnd, ref placement) || !GetWindowRect(hwnd, out rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The original window placement could not be saved.");
            return new WindowSnapshot
            {
                Hwnd = hwnd.ToInt64().ToString(), ProcessId = (int)pid, ThreadId = (int)thread, ProcessCreatedUtcFileTime = created.ToString(),
                Executable = path, ClassName = ClassName(hwnd),
                Style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64().ToString(), ExStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64().ToString(),
                ShowCommand = placement.ShowCmd, Flags = placement.Flags,
                MinPosition = new[] { placement.MinPosition.X, placement.MinPosition.Y },
                MaxPosition = new[] { placement.MaxPosition.X, placement.MaxPosition.Y },
                NormalPosition = new[] { placement.Normal.Left, placement.Normal.Top, placement.Normal.Right, placement.Normal.Bottom },
                WindowRect = new[] { rect.Left, rect.Top, rect.Right, rect.Bottom }
            };
        }

        // One move (no continuous enforcement): restore from minimized or
        // maximized, optionally drop the caption/sizing frame, and cover the
        // virtual output. The application may then change its own mode.
        public static bool HasFrame(WindowSnapshot snapshot)
        {
            return (Int64.Parse(snapshot.Style) & FrameBits) != 0 || (Int64.Parse(snapshot.ExStyle) & EdgeBits) != 0;
        }

        public static void MoveToOutput(WindowSnapshot snapshot, Rectangle output)
        {
            IntPtr hwnd = new IntPtr(Int64.Parse(snapshot.Hwnd));
            if (IsIconic(hwnd) || IsZoomed(hwnd)) ShowWindow(hwnd, SwRestore);
            uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
            if (snapshot.BorderlessApplied)
            {
                SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(Int64.Parse(snapshot.Style) & ~FrameBits));
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(Int64.Parse(snapshot.ExStyle) & ~EdgeBits));
                flags |= SwpFrameChanged;
            }
            if (!SetWindowPos(hwnd, IntPtr.Zero, output.X, output.Y, output.Width, output.Height, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to move the selected window (it may be running as administrator).");
        }

        public static string MonitorDevice(IntPtr hwnd)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, 0);
            if (monitor == IntPtr.Zero) return null;
            var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
            return GetMonitorInfo(monitor, ref info) ? info.Device : null;
        }

        public static bool ClientMatches(IntPtr hwnd, Rectangle expected)
        {
            Rect client; var origin = new PointStruct();
            if (!GetClientRect(hwnd, out client) || !ClientToScreen(hwnd, ref origin)) return false;
            return origin.X == expected.X && origin.Y == expected.Y && client.Right == expected.Width && client.Bottom == expected.Height;
        }

        // Returns true when the saved style and placement were reapplied and read back.
        public static bool Restore(WindowSnapshot snapshot)
        {
            IntPtr hwnd = new IntPtr(Int64.Parse(snapshot.Hwnd));
            if (snapshot.BorderlessApplied)
            {
                SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(Int64.Parse(snapshot.Style)));
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(Int64.Parse(snapshot.ExStyle)));
            }
            var placement = new WindowPlacement
            {
                Length = Marshal.SizeOf(typeof(WindowPlacement)), Flags = snapshot.Flags, ShowCmd = snapshot.ShowCommand,
                MinPosition = new PointStruct { X = snapshot.MinPosition[0], Y = snapshot.MinPosition[1] },
                MaxPosition = new PointStruct { X = snapshot.MaxPosition[0], Y = snapshot.MaxPosition[1] },
                Normal = new Rect { Left = snapshot.NormalPosition[0], Top = snapshot.NormalPosition[1], Right = snapshot.NormalPosition[2], Bottom = snapshot.NormalPosition[3] }
            };
            SetWindowPlacement(hwnd, ref placement);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
            var actual = new WindowPlacement { Length = Marshal.SizeOf(typeof(WindowPlacement)) };
            return GetWindowPlacement(hwnd, ref actual) && actual.ShowCmd == placement.ShowCmd &&
                actual.Normal.Left == placement.Normal.Left && actual.Normal.Top == placement.Normal.Top &&
                actual.Normal.Right == placement.Normal.Right && actual.Normal.Bottom == placement.Normal.Bottom &&
                // The app may change its own style (e.g. for fullscreen); only ours is checked.
                (!snapshot.BorderlessApplied || (GetWindowLongPtr(hwnd, GwlStyle).ToInt64().ToString() == snapshot.Style &&
                    GetWindowLongPtr(hwnd, GwlExStyle).ToInt64().ToString() == snapshot.ExStyle));
        }

        public static IntPtr FindProcessWindow(int pid, string className)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindowsProc callback = delegate (IntPtr hwnd, IntPtr unused)
            {
                uint actual; GetWindowThreadProcessId(hwnd, out actual);
                if (actual == (uint)pid && ClassName(hwnd) == className) { found = hwnd; return false; }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        public static void RequestClose(IntPtr hwnd) { IntPtr result; SendMessageTimeout(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero, 3, 2000, out result); }

        // Kernel-handle wrappers for the lease's private release events.
        public static IntPtr OpenReleaseEvent(string name, bool modify)
        {
            IntPtr handle = OpenEvent(modify ? (EventModifyState | Synchronize) : Synchronize, false, name);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the lease release event.");
            return handle;
        }
        public static IntPtr OpenForWait(uint pid)
        {
            IntPtr handle = OpenProcess(Synchronize | ProcessQueryLimited, false, pid);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot hold the display lease process.");
            return handle;
        }
        public static bool Exited(IntPtr process) { return WaitForSingleObject(process, 0) == 0; }
        public static bool Signaled(IntPtr handle, int milliseconds) { return WaitForSingleObject(handle, (uint)milliseconds) == 0; }
        public static void Signal(IntPtr handle) { if (!SetEvent(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Signaling the lease release event failed."); }
        public static void Close(IntPtr handle) { if (handle != IntPtr.Zero) CloseHandle(handle); }

        private static bool IsCloaked(IntPtr hwnd)
        {
            try { int cloaked; return DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0; }
            catch (DllNotFoundException) { return false; }
        }

        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct PointStruct { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement { public int Length, Flags, ShowCmd; public PointStruct MinPosition, MaxPosition; public Rect Normal; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int capacity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref PointStruct point);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenEvent(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetEvent(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
