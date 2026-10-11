using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave
{
    public sealed class WindowCandidate
    {
        public IntPtr Handle;
        public string Title;

        public override string ToString()
        {
            return Title ?? String.Empty;
        }
    }

    // Call this object on the UI thread. It manages only explicitly assigned
    // windows; it neither injects code nor creates virtual display devices.
    public sealed class WindowManager : IDisposable
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsChild = 0x40000000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long FrameBits = 0x00C40000L; // WS_CAPTION | WS_THICKFRAME
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const uint WpfAsyncWindowPlacement = 0x0004;
        private const uint SwShowMinNoActive = 7;
        private const uint EventObjectDestroy = 0x8001;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int DwmwaCloaked = 14;
        private const int RetryDelayMilliseconds = 200;
        private const int MaximumConsecutiveAttempts = 8;
        private const int MaximumAttemptsInWindow = 20;
        private const int AttemptWindowMilliseconds = 10000;

        private sealed class Assignment
        {
            internal IntPtr Handle;
            internal uint ProcessId;
            internal uint ThreadId;
            internal long ProcessCreationTime;
            internal string ClassName;
            internal string Title;
            internal WindowPlacement OriginalPlacement;
            internal long OriginalStyle;
            internal Rectangle Region;
            internal bool Borderless;
            internal bool ChangedFrame;
            internal bool Destroyed;
            internal int ConsecutiveAttempts;
            internal int AttemptsInWindow;
            internal long AttemptWindowStart;
            internal long LastAttempt = -RetryDelayMilliseconds;
        }

        private readonly Dictionary<IntPtr, Assignment> assignments = new Dictionary<IntPtr, Assignment>();
        private readonly uint ownProcessId = (uint)Process.GetCurrentProcess().Id;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly WinEventDelegate destroyCallback;
        private IntPtr destroyHook;
        private bool disposed;
        private bool ticking;
        private string lastError = String.Empty;

        public WindowManager()
        {
            destroyCallback = OnWindowDestroyed;
            destroyHook = Native.SetWinEventHook(EventObjectDestroy, EventObjectDestroy,
                IntPtr.Zero, destroyCallback, 0, 0, WinEventSkipOwnProcess);
            // The hook is out of context and needs this thread's message loop.
            // PID/thread/class/start-time checks also protect each mutation.
            if (destroyHook == IntPtr.Zero)
                lastError = "Window lifetime monitoring could not start. " + NativeError(Marshal.GetLastWin32Error());
        }

        public int Count { get { return assignments.Count; } }
        public string LastError { get { return lastError; } }
        public IntPtr ForegroundWindow { get { return Native.GetForegroundWindow(); } }

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

        public List<WindowCandidate> ListWindows()
        {
            ThrowIfDisposed();
            List<WindowCandidate> result = new List<WindowCandidate>();
            EnumWindowsDelegate callback = delegate(IntPtr handle, IntPtr parameter)
            {
                uint processId;
                uint threadId;
                string className;
                string title;
                if (IsEligible(handle, out processId, out threadId, out className, out title))
                    result.Add(new WindowCandidate { Handle = handle, Title = title });
                return true;
            };
            if (!Native.EnumWindows(callback, IntPtr.Zero))
                lastError = "The window list could not be refreshed. " + NativeError(Marshal.GetLastWin32Error());
            GC.KeepAlive(callback);
            result.Sort(delegate(WindowCandidate left, WindowCandidate right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
            });
            return result;
        }

        public void Assign(IntPtr handle, Rectangle region, bool borderless)
        {
            ThrowIfDisposed();
            lastError = String.Empty;
            if (!IsValidRegion(region))
            {
                lastError = "Choose a region with a positive width and height and valid screen coordinates.";
                return;
            }

            Assignment assignment;
            if (assignments.TryGetValue(handle, out assignment) && !IdentityMatches(assignment))
            {
                assignments.Remove(handle);
                assignment = null;
            }
            if (assignment == null)
            {
                uint processId;
                uint threadId;
                string className;
                string title;
                if (!IsEligible(handle, out processId, out threadId, out className, out title))
                {
                    lastError = "Choose a visible application window. Desktop, taskbar, tool, and DisplayWeave windows are excluded.";
                    return;
                }
                WindowPlacement placement = NewPlacement();
                if (!Native.GetWindowPlacement(handle, ref placement))
                {
                    lastError = "The original window position could not be saved. " + NativeError(Marshal.GetLastWin32Error());
                    return;
                }
                long originalStyle;
                int styleError;
                if (!TryGetStyle(handle, GwlStyle, out originalStyle, out styleError))
                {
                    lastError = "The original window frame could not be saved. " + NativeError(styleError);
                    return;
                }
                assignment = new Assignment
                {
                    Handle = handle,
                    ProcessId = processId,
                    ThreadId = threadId,
                    ProcessCreationTime = GetProcessCreationTime(processId),
                    ClassName = className,
                    Title = title,
                    OriginalPlacement = placement,
                    OriginalStyle = originalStyle,
                    AttemptWindowStart = clock.ElapsedMilliseconds
                };
                assignments.Add(handle, assignment);
            }

            assignment.Region = region;
            assignment.Borderless = borderless;
            assignment.ConsecutiveAttempts = 0;
            assignment.AttemptsInWindow = 0;
            assignment.AttemptWindowStart = clock.ElapsedMilliseconds;
            assignment.LastAttempt = -RetryDelayMilliseconds;
            Enforce(assignment);
        }

        public void Tick()
        {
            if (disposed || ticking) return;
            ticking = true;
            try
            {
                foreach (Assignment assignment in new List<Assignment>(assignments.Values))
                {
                    if (!IdentityMatches(assignment))
                    {
                        // A closed or recycled handle must never receive restore calls.
                        assignments.Remove(assignment.Handle);
                        continue;
                    }
                    Enforce(assignment);
                }
            }
            finally { ticking = false; }
        }

        public void Release(IntPtr handle)
        {
            ThrowIfDisposed();
            lastError = String.Empty;
            Assignment assignment;
            if (!assignments.TryGetValue(handle, out assignment)) return;
            assignments.Remove(handle);
            Restore(assignment);
        }

        public void ReleaseAll()
        {
            if (disposed) return;
            lastError = String.Empty;
            List<Assignment> previous = new List<Assignment>(assignments.Values);
            assignments.Clear();
            foreach (Assignment assignment in previous) Restore(assignment);
        }

        public void Dispose()
        {
            if (disposed) return;
            ReleaseAll();
            if (destroyHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(destroyHook);
                destroyHook = IntPtr.Zero;
            }
            disposed = true;
            GC.KeepAlive(destroyCallback);
        }

        internal static bool IsValidRegion(Rectangle region)
        {
            return region.Width > 0 && region.Height > 0 &&
                (long)region.X + region.Width <= Int32.MaxValue &&
                (long)region.Y + region.Height <= Int32.MaxValue;
        }

        private void Enforce(Assignment assignment)
        {
            if (!IdentityMatches(assignment))
            {
                assignments.Remove(assignment.Handle);
                return;
            }
            // Minimized or temporarily hidden windows are left alone, including
            // their styles, until the application brings them back itself.
            if (Native.IsIconic(assignment.Handle) || !Native.IsWindowVisible(assignment.Handle) || IsCloaked(assignment.Handle))
                return;

            NativeRect bounds;
            if (!Native.GetWindowRect(assignment.Handle, out bounds))
            {
                StopManaging(assignment, "its window bounds could not be read. " + NativeError(Marshal.GetLastWin32Error()));
                return;
            }
            long currentStyle;
            int error;
            if (!TryGetStyle(assignment.Handle, GwlStyle, out currentStyle, out error))
            {
                StopManaging(assignment, "its window style could not be read. " + NativeError(error));
                return;
            }
            long desiredStyle = currentStyle;
            if (assignment.Borderless) desiredStyle &= ~FrameBits;
            else if (assignment.ChangedFrame) desiredStyle = (currentStyle & ~FrameBits) | (assignment.OriginalStyle & FrameBits);

            bool frameChange = desiredStyle != currentStyle;
            bool boundsMatch = bounds.Left == assignment.Region.Left && bounds.Top == assignment.Region.Top &&
                (long)bounds.Right - bounds.Left == assignment.Region.Width &&
                (long)bounds.Bottom - bounds.Top == assignment.Region.Height;
            if (boundsMatch && !frameChange)
            {
                assignment.ConsecutiveAttempts = 0;
                return;
            }
            long now = clock.ElapsedMilliseconds;
            if (now - assignment.LastAttempt < RetryDelayMilliseconds) return;
            if (now - assignment.AttemptWindowStart >= AttemptWindowMilliseconds)
            {
                assignment.AttemptWindowStart = now;
                assignment.AttemptsInWindow = 0;
            }
            if (assignment.ConsecutiveAttempts >= MaximumConsecutiveAttempts || assignment.AttemptsInWindow >= MaximumAttemptsInWindow)
            {
                StopManaging(assignment, "it repeatedly refused the region. Its original position was requested; try the application's windowed or borderless mode.");
                return;
            }

            assignment.LastAttempt = now;
            assignment.ConsecutiveAttempts++;
            assignment.AttemptsInWindow++;
            if (frameChange)
            {
                if (!TrySetStyle(assignment.Handle, desiredStyle, out error))
                {
                    StopManaging(assignment, "its frame could not be changed. " + NativeError(error));
                    return;
                }
                // Keep this true even after toggling borderless off: restoration
                // should reapply only the frame bits that this utility owns.
                assignment.ChangedFrame = true;
            }
            if (!IdentityMatches(assignment))
            {
                assignments.Remove(assignment.Handle);
                return;
            }
            uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos;
            if (frameChange) flags |= SwpFrameChanged;
            Rectangle region = assignment.Region;
            if (!Native.SetWindowPos(assignment.Handle, IntPtr.Zero, region.X, region.Y, region.Width, region.Height, flags))
            {
                error = Marshal.GetLastWin32Error();
                StopManaging(assignment, "Windows rejected the resize. " + NativeError(error));
            }
            // Async acceptance does not mean the target used the rectangle.
            // A later Tick verifies it and bounds the number of retries.
        }

        private void StopManaging(Assignment assignment, string reason)
        {
            assignments.Remove(assignment.Handle);
            string restoreError = Restore(assignment);
            lastError = "Stopped managing " + assignment.Title + ": " + reason;
            if (!String.IsNullOrEmpty(restoreError)) lastError += " " + restoreError;
        }

        private string Restore(Assignment assignment)
        {
            if (!IdentityMatches(assignment)) return String.Empty;
            string failure = String.Empty;
            bool minimized = Native.IsIconic(assignment.Handle);
            if (assignment.ChangedFrame)
            {
                long currentStyle;
                int error;
                if (!TryGetStyle(assignment.Handle, GwlStyle, out currentStyle, out error) ||
                    !TrySetStyle(assignment.Handle, (currentStyle & ~FrameBits) | (assignment.OriginalStyle & FrameBits), out error))
                {
                    failure = "The saved frame could not be restored. " + NativeError(error);
                }
                else if (IdentityMatches(assignment))
                {
                    // Commit cached frame data without moving, activating or
                    // changing the order of any window.
                    if (!Native.SetWindowPos(assignment.Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos | SwpFrameChanged | 0x0001 | 0x0002))
                        failure = "The restored frame could not be applied. " + NativeError(Marshal.GetLastWin32Error());
                }
            }
            if (IdentityMatches(assignment))
            {
                WindowPlacement placement = assignment.OriginalPlacement;
                placement.Length = (uint)Marshal.SizeOf(typeof(WindowPlacement));
                placement.Flags |= WpfAsyncWindowPlacement;
                // SW_SHOWNORMAL can activate a restored window; its nonactivating
                // equivalent preserves focus. Leave currently minimized apps minimized.
                if (minimized) placement.ShowCmd = SwShowMinNoActive;
                else if (placement.ShowCmd == 1 || placement.ShowCmd == 9) placement.ShowCmd = 4;
                if (!Native.SetWindowPlacement(assignment.Handle, ref placement))
                {
                    if (failure.Length != 0) failure += " ";
                    failure += "The saved position could not be restored. " + NativeError(Marshal.GetLastWin32Error());
                }
            }
            if (failure.Length != 0) lastError = assignment.Title + ": " + failure;
            return failure;
        }

        private bool IsEligible(IntPtr handle, out uint processId, out uint threadId, out string className, out string title)
        {
            processId = 0;
            threadId = 0;
            className = String.Empty;
            title = String.Empty;
            if (handle == IntPtr.Zero || !Native.IsWindow(handle) || !Native.IsWindowVisible(handle) ||
                handle == Native.GetDesktopWindow() || handle == Native.GetShellWindow() || Native.GetAncestor(handle, 2) != handle)
                return false;
            threadId = Native.GetWindowThreadProcessId(handle, out processId);
            if (threadId == 0 || processId == 0 || processId == 4 || processId == ownProcessId) return false;
            long style;
            long exStyle;
            int error;
            if (!TryGetStyle(handle, GwlStyle, out style, out error) || !TryGetStyle(handle, GwlExStyle, out exStyle, out error)) return false;
            if ((style & WsChild) != 0 || (exStyle & WsExToolWindow) != 0 || IsCloaked(handle)) return false;
            className = ReadClassName(handle);
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" ||
                className == "Shell_SecondaryTrayWnd" || className == "#32768" || className == "tooltips_class32" || className == "SysShadow")
                return false;
            StringBuilder text = new StringBuilder(1024);
            Native.GetWindowText(handle, text, text.Capacity);
            title = text.ToString().Trim();
            if (title.Length == 0) return false;
            // Eligibility must exclude the entire Explorer/shell process: a
            // folder HWND can share process-wide hooks with the desktop/taskbar.
            ApplicationIdentity identity;
            string rejection;
            return FullscreenController.TryInspect(handle, out identity, out rejection);
        }

        private bool IdentityMatches(Assignment assignment)
        {
            if (assignment.Destroyed || !Native.IsWindow(assignment.Handle)) return false;
            uint processId;
            uint threadId = Native.GetWindowThreadProcessId(assignment.Handle, out processId);
            if (processId != assignment.ProcessId || threadId != assignment.ThreadId || processId == ownProcessId) return false;
            if (!String.Equals(ReadClassName(assignment.Handle), assignment.ClassName, StringComparison.Ordinal)) return false;
            if (assignment.ProcessCreationTime != 0)
            {
                long currentCreation = GetProcessCreationTime(processId);
                if (currentCreation == 0 || currentCreation != assignment.ProcessCreationTime) return false;
            }
            return true;
        }

        private void OnWindowDestroyed(IntPtr hook, uint eventType, IntPtr handle, int objectId, int childId, uint threadId, uint eventTime)
        {
            if (disposed || objectId != 0 || childId != 0) return;
            Assignment assignment;
            if (assignments.TryGetValue(handle, out assignment)) assignment.Destroyed = true;
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

        private static long GetProcessCreationTime(uint processId)
        {
            IntPtr process = Native.OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return 0;
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                return Native.GetProcessTimes(process, out creation, out exit, out kernel, out user) ? creation : 0;
            }
            finally { Native.CloseHandle(process); }
        }

        private static WindowPlacement NewPlacement()
        {
            WindowPlacement result = new WindowPlacement();
            result.Length = (uint)Marshal.SizeOf(typeof(WindowPlacement));
            return result;
        }

        private static bool TryGetStyle(IntPtr handle, int index, out long value, out int error)
        {
            Native.SetLastError(0);
            IntPtr result = IntPtr.Size == 8 ? Native.GetWindowLongPtr64(handle, index) : new IntPtr(Native.GetWindowLong32(handle, index));
            error = Marshal.GetLastWin32Error();
            value = result.ToInt64();
            return result != IntPtr.Zero || error == 0;
        }

        private static bool TrySetStyle(IntPtr handle, long style, out int error)
        {
            Native.SetLastError(0);
            IntPtr result = IntPtr.Size == 8 ? Native.SetWindowLongPtr64(handle, GwlStyle, new IntPtr(style)) :
                new IntPtr(Native.SetWindowLong32(handle, GwlStyle, unchecked((int)style)));
            error = Marshal.GetLastWin32Error();
            return result != IntPtr.Zero || error == 0;
        }

        private static string NativeError(int error)
        {
            if (error == 5) return "Access was denied; elevated or protected applications may not allow window control.";
            if (error == 0) return "The application or Windows did not accept the request.";
            return new Win32Exception(error).Message + " (Windows error " + error.ToString() + ").";
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("WindowManager");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            internal uint Length;
            internal uint Flags;
            internal uint ShowCmd;
            internal NativePoint MinPosition;
            internal NativePoint MaxPosition;
            internal NativeRect NormalPosition;
        }

        private delegate bool EnumWindowsDelegate(IntPtr handle, IntPtr parameter);
        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr handle, int objectId, int childId, uint threadId, uint eventTime);

        private static class Native
        {
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr handle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr handle);
            [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr handle, uint flags);
            [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetClassName(IntPtr handle, StringBuilder text, int capacity);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong32(IntPtr handle, int index);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong32(IntPtr handle, int index, int value);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
            [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(IntPtr hook);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
            [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int awareness);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDPIAware();
            [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
            [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
        }
    }
}
