using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    internal static class LauncherProcessProtection
    {
        internal const string RejectionMessage = "Battle.net launcher is excluded because fullscreen hooks can cause crashes; games remain eligible.";

        internal static bool IsExcludedExecutable(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;
            // Match launcher image names only: games in the same folder and games
            // started by the launcher must remain independently eligible.
            string name = Path.GetFileName(executablePath);
            return String.Equals(name, "Battle.net.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "Battle.net Helper.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Chromium stops itself with a breakpoint when its window handling is hooked (observed in a
    // browser and a launcher), so its windows only ever get the outside maximize fit.
    internal static class ChromiumWindowProtection
    {
        internal const string Message = "Apps built on Chromium stop themselves when their window handling is hooked, " +
            "so DisplayWeave never loads anything into them. A maximized window is fitted into its region from outside instead.";

        internal static bool IsChromiumWindow(string windowClass)
        {
            return windowClass != null && windowClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
        }
    }

    internal static class ShellProcessProtection
    {
        internal const string RejectionMessage = "Windows desktop, taskbar, and shell processes cannot be assigned to regions. Their hooks would also affect the desktop.";
        internal const string FolderMessage = "File Explorer folder windows share their process with the desktop and taskbar, so DisplayWeave never loads " +
            "anything into them. A maximized folder window is fitted into its region from outside; the desktop and taskbar keep their full size.";

        private static readonly HashSet<string> ProtectedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ShellHost.exe",
            "SearchHost.exe", "SearchApp.exe", "SearchUI.exe", "TextInputHost.exe", "InputApp.exe", "LockApp.exe",
            "CredentialUIBroker.exe", "dwm.exe", "sihost.exe", "LogonUI.exe", "winlogon.exe"
        };

        internal static bool IsExcludedExecutable(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;
            // Only the complete basename counts; ordinary applications in a similarly named folder are allowed.
            return ProtectedExecutables.Contains(Path.GetFileName(executablePath));
        }

        internal static bool IsExcludedWindowClass(string windowClass)
        {
            return String.Equals(windowClass, "Progman", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(windowClass, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(windowClass, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(windowClass, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(windowClass, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase) ||
                IsFolderWindow(windowClass);
        }

        // A folder window is its own top-level window: moving it from outside never touches the desktop or taskbar.
        internal static bool IsFolderWindow(string windowClass)
        {
            return String.Equals(windowClass, "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(windowClass, "ExploreWClass", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsExcludedTarget(string executablePath, string windowClass, bool ownsShellWindows)
        {
            return ownsShellWindows || IsExcludedExecutable(executablePath) || IsExcludedWindowClass(windowClass);
        }

        internal static bool IsShellProcessId(uint processId)
        {
            if (processId == 0) return false;
            if (OwnsWindow(processId, GetShellWindow()) || OwnsWindow(processId, GetDesktopWindow()) ||
                OwnsWindow(processId, FindWindow("Shell_TrayWnd", null))) return true;
            IntPtr previous = IntPtr.Zero;
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            while (true)
            {
                IntPtr secondary = FindWindowEx(IntPtr.Zero, previous, "Shell_SecondaryTrayWnd", null);
                if (secondary == IntPtr.Zero || !seen.Add(secondary)) return false;
                if (OwnsWindow(processId, secondary)) return true;
                previous = secondary;
            }
        }

        private static bool OwnsWindow(uint processId, IntPtr window)
        {
            uint owner;
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out owner) != 0 && owner == processId;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
