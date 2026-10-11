using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DisplayWeave
{
    public sealed class ApplicationIdentity
    {
        public IntPtr Handle;
        public uint ProcessId;
        public long CreationTime;
        public string ExecutablePath;
        public string WindowClass;
        public bool Is32Bit;
        public string ProcessKey { get { return ProcessId + ":" + CreationTime; } }
        public string Key { get { return ProcessId + ":" + CreationTime + ":" + Handle.ToInt64(); } }
    }

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

    internal static class ShellProcessProtection
    {
        internal const string RejectionMessage = "Windows desktop, taskbar, and shell processes cannot be assigned to regions. Their hooks would also affect the desktop.";

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
                String.Equals(windowClass, "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
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

    // Independent of transient activity messages, and keyed to one window/process lifetime.
    internal sealed class FullscreenFailureHistory
    {
        private sealed class Entry
        {
            internal ApplicationIdentity Target;
            internal string Message;
            internal long Sequence;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long sequence;
        internal int Count { get { return entries.Count; } }
        internal string Latest
        {
            get
            {
                Entry latest = null;
                foreach (Entry entry in entries.Values) if (latest == null || entry.Sequence > latest.Sequence) latest = entry;
                return latest == null ? String.Empty : latest.Message;
            }
        }
        internal IList<ApplicationIdentity> Targets
        {
            get
            {
                List<ApplicationIdentity> result = new List<ApplicationIdentity>();
                foreach (Entry entry in entries.Values) result.Add(entry.Target);
                return result;
            }
        }
        internal void Record(ApplicationIdentity target, string message)
        {
            entries[target.Key] = new Entry { Target = target, Message = message, Sequence = ++sequence };
            if (entries.Count <= 128) return;
            Entry oldest = null;
            foreach (Entry entry in entries.Values) if (oldest == null || entry.Sequence < oldest.Sequence) oldest = entry;
            entries.Remove(oldest.Target.Key);
        }
        internal string For(ApplicationIdentity target)
        {
            Entry entry;
            return target != null && entries.TryGetValue(target.Key, out entry) ? entry.Message : String.Empty;
        }
        internal void ClearTarget(ApplicationIdentity target) { if (target != null) entries.Remove(target.Key); }
        internal void ClearProcess(ApplicationIdentity target)
        {
            if (target == null) return;
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in entries)
                if (pair.Value.Target.ProcessKey == target.ProcessKey) remove.Add(pair.Key);
            foreach (string key in remove) entries.Remove(key);
        }
    }

    // UI-thread coordinator. All fullscreen interception occurs in the selected
    // process through the architecture-matched native helper, never a resize loop.
    // The one exception is for apps without a working component (a MaximizeInRegion rule, or
    // automatic/Force handling after the component failed): only a window the user maximized
    // is moved from outside, with bounded retries.
    public sealed class FullscreenController : IDisposable
    {
        private sealed class Session
        {
            internal ApplicationIdentity Target;
            internal Process Host;
            internal Rectangle Region;
            internal string LayoutKey;
            internal string AutomaticZoneId;
            internal DateTime Started;
            internal bool Ready;
            internal bool Enabled = true;
            internal bool Automatic;
            internal bool? NativeExpanded;
            internal bool OutputClosed;
            internal bool ErrorClosed;
            internal bool AwaitingExitOutput;
            internal DateTime ExitObserved;
            internal readonly Queue<string> Messages = new Queue<string>();
        }

        private readonly Dictionary<uint, Session> sessions = new Dictionary<uint, Session>();
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> released = new HashSet<string>(StringComparer.Ordinal);
        private readonly AutomaticRegionPolicy automaticPolicy = new AutomaticRegionPolicy();
        private readonly FullscreenFailureHistory failureHistory = new FullscreenFailureHistory();
        // Windows moved by MaximizeInRegion rules, pinned to their process identity for restoration.
        private sealed class MaximizedFit
        {
            internal ApplicationIdentity Target;
            internal int Attempts;
            internal DateTime LastAttempt;
        }
        private readonly Dictionary<IntPtr, MaximizedFit> maximized = new Dictionary<IntPtr, MaximizedFit>();
        private const int MaximumFitAttempts = 4;
        // A game can take a second to rebuild its swap chain before it handles the move.
        private static readonly TimeSpan FitRetryDelay = TimeSpan.FromSeconds(2);
        private DateTime nextAutomaticLaunch = DateTime.MinValue;
        private bool disposed;
        public bool Paused { get; private set; }
        public string LastMessage { get; private set; }
        public string LastFailure { get { return failureHistory.Latest; } }
        public string GetFailure(ApplicationIdentity target) { return failureHistory.For(target); }
        public int Count { get { int count = 0; foreach (Session s in sessions.Values) if (s.Enabled && s.Ready) count++; return count; } }
        public int PendingCount { get { int count = 0; foreach (Session s in sessions.Values) if (s.Enabled && !s.Ready) count++; return count; } }
        public int AutomaticCount { get { int count = 0; foreach (Session s in sessions.Values) if (s.Automatic && s.Enabled && s.Ready) count++; return count; } }
        public int AutomaticPendingCount { get { int count = 0; foreach (Session s in sessions.Values) if (s.Automatic && s.Enabled && !s.Ready) count++; return count; } }
        public int FailureCount { get { return failureHistory.Count; } }
        // An app maximized or fullscreen in a region, or not yet reporting its state; restarting DisplayWeave would resize it.
        public bool HasExpandedApp { get { if (maximized.Count > 0) return true; foreach (Session s in sessions.Values) if (s.Enabled && s.NativeExpanded != false) return true; return false; } }
        // Test scope only: automatic preparation is limited to this exact executable path when set.
        public string AutomaticExecutableFilter { get; set; }

        public FullscreenController() { LastMessage = String.Empty; }

        public static bool TryInspect(IntPtr handle, out ApplicationIdentity identity, out string error)
        {
            identity = null;
            error = String.Empty;
            uint pid;
            if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || GetAncestor(handle, 2) != handle ||
                GetWindow(handle, 4) != IntPtr.Zero || GetWindowThreadProcessId(handle, out pid) == 0)
            { error = "Select a visible application main window."; return false; }
            if (pid == (uint)Process.GetCurrentProcess().Id || pid <= 4)
            { error = "Select another application."; return false; }
            if (ShellProcessProtection.IsShellProcessId(pid))
            { error = ShellProcessProtection.RejectionMessage; return false; }
            StringBuilder className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            string cls = className.ToString();
            if (ShellProcessProtection.IsExcludedWindowClass(cls))
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
                if (ShellProcessProtection.IsExcludedExecutable(path.ToString()))
                { error = ShellProcessProtection.RejectionMessage; return false; }
                identity = new ApplicationIdentity { Handle = handle, ProcessId = pid, CreationTime = creation,
                    ExecutablePath = path.ToString(), WindowClass = cls, Is32Bit = !Environment.Is64BitOperatingSystem || wow64 };
                return true;
            }
            finally { CloseHandle(process); }
        }

        public bool Attach(ApplicationIdentity target, Rectangle region, bool explicitRequest, IEnumerable<Rectangle> regions = null)
        {
            return AttachCore(target, region, explicitRequest, false, regions);
        }

        private bool AttachCore(ApplicationIdentity target, Rectangle region, bool explicitRequest, bool automatic, IEnumerable<Rectangle> regions,
            string automaticZoneId = null)
        {
            if (disposed || target == null) return false;
            if (LauncherProcessProtection.IsExcludedExecutable(target.ExecutablePath))
            {
                RejectLauncherTarget(target);
                Session excludedSession;
                if (sessions.TryGetValue(target.ProcessId, out excludedSession))
                {
                    Stop(excludedSession);
                    sessions.Remove(target.ProcessId);
                }
                return false;
            }
            if (IsProtectedShellTarget(target))
            {
                RejectShellTarget(target);
                Session unsafeSession;
                if (sessions.TryGetValue(target.ProcessId, out unsafeSession))
                {
                    Stop(unsafeSession);
                    sessions.Remove(target.ProcessId);
                }
                return false;
            }
            if (!WithinExecutableScope(target))
            {
                LastMessage = "Test mode only permits its selected executable.";
                return false;
            }
            if (explicitRequest) { failed.Remove(target.Key); released.Remove(target.ProcessKey); }
            else if (Paused || failed.Contains(target.Key) || released.Contains(target.ProcessKey)) return false;
            string layoutKey;
            if (!TrySerializeRegions(regions ?? new Rectangle[] { region }, region, out layoutKey))
            {
                LastMessage = "The display layout is invalid. Review its regions before assigning this application.";
                return false;
            }
            Session existing;
            if (sessions.TryGetValue(target.ProcessId, out existing))
            {
                if (existing.Target.Key == target.Key && HostExited(existing))
                {
                    // Tick owns exited-helper diagnostics. Replacing the process here
                    // can discard a late stdout error before the UI has observed it.
                    existing.AwaitingExitOutput = true;
                    return false;
                }
                if (existing.Target.Key == target.Key)
                {
                    if (existing.Automatic != automatic && !Send(existing, automatic ? "MODE AUTO" : "MODE FORCE")) return false;
                    existing.Automatic = automatic;
                    existing.AutomaticZoneId = automatic ? automaticZoneId : null;
                    if (!UpdateSessionLayout(existing, layoutKey)) return false;
                    if (existing.Region != region && !Send(existing, "UPDATE " + BoundsArguments(region))) return false;
                    existing.Region = region;
                    if (!existing.Enabled && !Send(existing, "ON")) return false;
                    existing.Enabled = true;
                    return true;
                }
                Stop(existing);
                sessions.Remove(target.ProcessId);
            }
            string architecture = target.Is32Bit ? "native-v2-x86" : "native-v2-x64";
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, architecture, "RegionHookHost.exe");
            if (!File.Exists(helper) || !File.Exists(Path.Combine(Path.GetDirectoryName(helper), "RegionHook.dll")))
            { RecordFailure(target, "The " + architecture + " fullscreen helper is missing. Build the native engine first."); return false; }
            Session session = new Session { Target = target, Region = region, LayoutKey = layoutKey,
                Started = DateTime.UtcNow, Automatic = automatic, AutomaticZoneId = automatic ? automaticZoneId : null };
            ProcessStartInfo start = new ProcessStartInfo(helper, target.ProcessId.ToString(CultureInfo.InvariantCulture) + " 0x" +
                target.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture) + " " + BoundsArguments(region) +
                " --regions \"" + layoutKey + "\"" + (automatic ? " --auto" : String.Empty));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.WorkingDirectory = Path.GetDirectoryName(helper);
            session.Host = new Process { StartInfo = start };
            session.Host.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { QueueMessage(session, e.Data, false); };
            session.Host.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { QueueMessage(session, e.Data, true); };
            try
            {
                if (!session.Host.Start()) throw new IOException("The helper did not start.");
                session.Host.BeginOutputReadLine();
                session.Host.BeginErrorReadLine();
                sessions.Add(target.ProcessId, session);
                LastMessage = (automatic ? "Preparing automatic virtual monitor handling for " : "Preparing virtual monitor handling for ") + Path.GetFileName(target.ExecutablePath) + "…";
                return true;
            }
            catch (Exception ex)
            {
                Stop(session);
                RecordFailure(target, "Fullscreen helper failed: " + ex.Message);
                return false;
            }
        }

        public void Tick(IEnumerable<WindowCandidate> windows, ApplicationRules rules, LayoutSettings layout)
        {
            if (disposed) return;
            PruneClosedFailures();
            List<Rectangle> layoutRegions;
            bool validLayout = TryResolveLayout(layout, out layoutRegions);
            string layoutKey = null;
            if (validLayout) validLayout = TrySerializeRegions(layoutRegions, layoutRegions[0], out layoutKey);
            List<uint> remove = new List<uint>();
            foreach (KeyValuePair<uint, Session> pair in sessions)
            {
                Session session = pair.Value;
                if (LauncherProcessProtection.IsExcludedExecutable(session.Target.ExecutablePath))
                {
                    RejectLauncherTarget(session.Target);
                    Stop(session);
                    remove.Add(pair.Key);
                    continue;
                }
                if (IsProtectedShellTarget(session.Target))
                {
                    RejectShellTarget(session.Target);
                    Stop(session);
                    remove.Add(pair.Key);
                    continue;
                }
                if (!WithinExecutableScope(session.Target)) { Stop(session); remove.Add(pair.Key); continue; }
                bool hostExited = HostExited(session);
                bool outputComplete = !hostExited || WaitForExitedOutput(session);
                string failure = null;
                lock (session.Messages)
                {
                    while (session.Messages.Count > 0)
                    {
                        string message = session.Messages.Dequeue();
                        if (message.StartsWith("READY", StringComparison.Ordinal))
                        {
                            bool reused;
                            uint capabilities;
                            if (!TryParseReady(message, session.Target.ProcessId, out reused, out capabilities))
                            {
                                failure = PreferFailure(failure, "Native helper sent an invalid readiness response or acknowledged another process.");
                                continue;
                            }
                            session.Ready = true;
                            failed.Remove(session.Target.Key);
                            failureHistory.ClearTarget(session.Target);
                            LastMessage = FormatReady(session.Target, session.Automatic, session.Enabled, reused, capabilities);
                        }
                        else if (message.StartsWith("STATE ", StringComparison.Ordinal))
                        {
                            if (message == "STATE NORMAL") session.NativeExpanded = false;
                            else if (message == "STATE MAXIMIZED" || message == "STATE FULLSCREEN" || message == "STATE FORCED")
                                session.NativeExpanded = true;
                        }
                        else if (message.StartsWith("ERROR", StringComparison.Ordinal)) failure = PreferFailure(failure, message);
                        else if (message.Length != 0 && !message.StartsWith("OK ", StringComparison.Ordinal) && message != "STOPPED") LastMessage = message;
                    }
                }
                uint currentPid;
                if (!IsWindow(session.Target.Handle) || GetWindowThreadProcessId(session.Target.Handle, out currentPid) == 0 || currentPid != session.Target.ProcessId)
                { Stop(session); remove.Add(pair.Key); continue; }
                if (failure == null && hostExited && !outputComplete)
                {
                    // stdout callbacks can arrive after HasExited. Keep this session until
                    // their bounded drain completes rather than replacing a useful native error.
                    session.AwaitingExitOutput = true;
                    continue;
                }
                session.AwaitingExitOutput = false;
                if (failure == null && hostExited) failure = "Native helper exited before the application closed.";
                if (failure == null && !session.Ready && (DateTime.UtcNow - session.Started).TotalSeconds > 15)
                    failure = "Fullscreen integration did not acknowledge readiness before the startup deadline (Windows error 1460)";
                if (failure != null)
                {
                    RecordFailure(session.Target, failure);
                    Stop(session); remove.Add(pair.Key);
                }
                else if (!Paused)
                {
                    if (!validLayout) { Disable(session); continue; }
                    // Hidden application windows do not appear in the eligible candidate list.
                    // Preference/rule changes must still disarm their existing native hooks now.
                    ApplicationRule currentRule = MatchingRule(rules, session.Target);
                    Rectangle ignoredRegion;
                    bool ruleResolved = TryResolveRegion(currentRule, layout, out ignoredRegion);
                    RegionControlMode desired = AutomaticRegionPolicy.Decide(layout.AutomaticRegions, false,
                        released.Contains(session.Target.ProcessKey), currentRule == null ? (ApplicationAction?)null : currentRule.Action,
                        ruleResolved);
                    if (desired == RegionControlMode.None || desired == RegionControlMode.Maximize ||
                        (desired == RegionControlMode.Automatic && !AutomaticCandidate(session.Target)) ||
                        (desired == RegionControlMode.Automatic) != session.Automatic)
                    { Disable(session); continue; }
                    Rectangle currentRegion = ignoredRegion;
                    if (desired == RegionControlMode.Automatic)
                    {
                        int regionIndex = layout.Zones.FindIndex(delegate(Zone zone) { return zone.Id == session.AutomaticZoneId; });
                        // A hidden/fullscreen window retains its region identity;
                        // removed regions never silently select another cell.
                        if (regionIndex < 0) { Disable(session); continue; }
                        currentRegion = layoutRegions[regionIndex];
                    }
                    if (!UpdateSessionLayout(session, layoutKey))
                    {
                        Stop(session);
                        remove.Add(pair.Key);
                    }
                    else if (session.Region != currentRegion)
                    {
                        // Hidden and minimized windows do not appear in the
                        // candidate list, but still need current region bounds.
                        if (Send(session, "UPDATE " + BoundsArguments(currentRegion))) session.Region = currentRegion;
                        else { Stop(session); remove.Add(pair.Key); }
                    }
                }
            }
            foreach (uint pid in remove) sessions.Remove(pid);
            if (Paused || !validLayout || windows == null) return;
            ReturnUnruledMaximized(rules, layout);
            DisplayMonitor selected = DisplayMonitor.Find(layout.MonitorDevice);
            Rectangle area = selected == null ? Rectangle.Empty : (layout.UseWorkArea ? selected.WorkingArea : selected.Bounds);
            List<WindowCandidate> candidates = new List<WindowCandidate>(windows);
            IntPtr foreground = GetForegroundWindow();
            candidates.Sort(delegate(WindowCandidate left, WindowCandidate right)
            {
                bool leftFirst = left != null && left.Handle == foreground;
                bool rightFirst = right != null && right.Handle == foreground;
                return leftFirst == rightFirst ? 0 : leftFirst ? -1 : 1;
            });
            HashSet<string> observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (WindowCandidate window in candidates)
            {
                if (window == null) continue;
                ApplicationIdentity identity;
                string error;
                if (!TryInspect(window.Handle, out identity, out error)) continue;
                if (!WithinExecutableScope(identity)) continue;
                observed.Add(identity.Key);
                Session existing;
                sessions.TryGetValue(identity.ProcessId, out existing);
                if (existing != null && existing.AwaitingExitOutput) continue;
                // Native hooks target one main window per process; never retarget a secondary window implicitly.
                if (existing != null && existing.Target.Key != identity.Key) continue;
                ApplicationRule rule = MatchingRule(rules, identity);
                Rectangle region;
                bool resolved = TryResolveRegion(rule, layout, out region);
                RegionControlMode mode = AutomaticRegionPolicy.Decide(layout.AutomaticRegions, Paused,
                    released.Contains(identity.ProcessKey), rule == null ? (ApplicationAction?)null : rule.Action, resolved);
                if (mode == RegionControlMode.None)
                {
                    Disable(existing);
                    continue;
                }
                if (mode == RegionControlMode.Maximize)
                {
                    Disable(existing);
                    FitMaximized(identity, region);
                    continue;
                }
                if (mode == RegionControlMode.Force)
                {
                    if (existing == null && failed.Contains(identity.Key)) FitMaximized(identity, region);
                    else AttachCore(identity, region, false, false, layoutRegions);
                    continue;
                }
                if (selected == null || !AutomaticCandidate(identity)) { Disable(existing); continue; }
                Rectangle currentBounds, restoredBounds;
                bool expanded, minimized;
                if (!TryReadWindowState(identity.Handle, selected.Bounds, existing, out currentBounds,
                    out restoredBounds, out expanded, out minimized)) continue;
                if (minimized) continue;
                // A window spanning monitors belongs to the monitor with the greatest window overlap.
                DisplayMonitor currentMonitor = DisplayMonitor.FromRectangle(currentBounds);
                if (currentMonitor == null || !String.Equals(currentMonitor.DeviceName, selected.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    automaticPolicy.Forget(identity.Key);
                    Disable(existing);
                    continue;
                }
                string zoneId;
                if (!automaticPolicy.Observe(identity.Key, currentBounds, restoredBounds, expanded, false,
                    area, layout.Zones, out zoneId, out region)) { Disable(existing); continue; }
                // The component did not load (refused, elevated, timed out): fit the maximized window from outside instead.
                if (existing == null && failed.Contains(identity.Key)) { FitMaximized(identity, region); continue; }
                if (existing != null) AttachCore(identity, region, false, true, layoutRegions, zoneId);
                else if (!failed.Contains(identity.Key) && DateTime.UtcNow >= nextAutomaticLaunch)
                {
                    // Prepare gradually. Foreground applications are considered first on every tick.
                    nextAutomaticLaunch = DateTime.UtcNow.AddMilliseconds(750);
                    AttachCore(identity, region, false, true, layoutRegions, zoneId);
                }
            }
            // A fullscreen app may temporarily hide its HWND. Keep its original selection
            // until the session actually ends, instead of selecting from fullscreen bounds.
            foreach (Session session in sessions.Values) observed.Add(session.Target.Key);
            automaticPolicy.Retain(observed);
        }

        private void Disable(Session session)
        {
            if (session != null && session.Enabled)
            {
                if (!Send(session, "OFF")) Stop(session);
                session.Enabled = false;
            }
        }

        private void FitMaximized(ApplicationIdentity target, Rectangle region)
        {
            IntPtr handle = target.Handle;
            MaximizedFit fit;
            maximized.TryGetValue(handle, out fit);
            if (fit != null && fit.Target.Key != target.Key) { maximized.Remove(handle); fit = null; }
            // A restored window is back where Windows keeps its normal position; nothing to undo.
            if (IsIconic(handle) || !IsZoomed(handle)) { if (fit != null && !IsIconic(handle)) maximized.Remove(handle); return; }
            Rectangle current, wanted;
            if (!TryMaximizedBounds(handle, region, out current, out wanted)) return;
            if (current == wanted)
            {
                if (fit != null && fit.Attempts != 0) { fit.Attempts = 0; failureHistory.ClearTarget(target); }
                return;
            }
            if (fit == null) maximized.Add(handle, fit = new MaximizedFit { Target = target });
            if (fit.Attempts >= MaximumFitAttempts || DateTime.UtcNow - fit.LastAttempt < FitRetryDelay) return;
            fit.LastAttempt = DateTime.UtcNow;
            if (++fit.Attempts == MaximumFitAttempts)
            {
                RecordFailure(target, Path.GetFileName(target.ExecutablePath) + " keeps changing its maximized size, so DisplayWeave stopped fitting it. Restore and maximize it again to retry.");
                return;
            }
            if (!SetWindowPos(handle, IntPtr.Zero, wanted.X, wanted.Y, wanted.Width, wanted.Height, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos))
            {
                fit.Attempts = MaximumFitAttempts;
                RecordFailure(target, "Windows did not move " + Path.GetFileName(target.ExecutablePath) + " (Windows error " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "). An app running as administrator needs DisplayWeave restarted as administrator.");
                return;
            }
            LastMessage = "Fitted the maximized " + Path.GetFileName(target.ExecutablePath) + " window into its region. Nothing is loaded into the app.";
        }

        // A fitted window stays in its region under a MaximizeInRegion rule, or under
        // automatic or Force handling while its component is unavailable.
        internal static bool KeepsMaximizedFit(RegionControlMode mode, bool componentFailed)
        {
            return mode == RegionControlMode.Maximize ||
                ((mode == RegionControlMode.Automatic || mode == RegionControlMode.Force) && componentFailed);
        }

        // Windows no longer fitted by a rule or the automatic fallback go back to filling their monitor.
        private void ReturnUnruledMaximized(ApplicationRules rules, LayoutSettings layout)
        {
            foreach (MaximizedFit fit in new List<MaximizedFit>(maximized.Values))
            {
                ApplicationIdentity current;
                string error;
                if (TryInspect(fit.Target.Handle, out current, out error) && current.Key == fit.Target.Key)
                {
                    ApplicationRule rule = MatchingRule(rules, current);
                    Rectangle region;
                    bool resolved = TryResolveRegion(rule, layout, out region);
                    RegionControlMode mode = AutomaticRegionPolicy.Decide(layout.AutomaticRegions, false, released.Contains(current.ProcessKey),
                        rule == null ? (ApplicationAction?)null : rule.Action, resolved);
                    if (KeepsMaximizedFit(mode, failed.Contains(current.Key))) continue;
                    ReturnToMonitor(fit.Target.Handle);
                }
                maximized.Remove(fit.Target.Handle);
            }
        }

        private void ReturnAllMaximized()
        {
            foreach (MaximizedFit fit in maximized.Values)
            {
                ApplicationIdentity current;
                string error;
                if (TryInspect(fit.Target.Handle, out current, out error) && current.Key == fit.Target.Key) ReturnToMonitor(fit.Target.Handle);
            }
            maximized.Clear();
        }

        private static void ReturnToMonitor(IntPtr handle)
        {
            Rectangle current, wanted;
            DisplayMonitor monitor = DisplayMonitor.FromWindow(handle);
            if (monitor == null || IsIconic(handle) || !IsZoomed(handle) ||
                !TryMaximizedBounds(handle, monitor.WorkingArea, out current, out wanted) || current == wanted) return;
            SetWindowPos(handle, IntPtr.Zero, wanted.X, wanted.Y, wanted.Width, wanted.Height, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos);
        }

        private static bool TryMaximizedBounds(IntPtr handle, Rectangle target, out Rectangle current, out Rectangle wanted)
        {
            current = wanted = Rectangle.Empty;
            NativeRect window, frame;
            if (!GetWindowRect(handle, out window)) return false;
            if (DwmGetWindowAttribute(handle, 9, out frame, Marshal.SizeOf(typeof(NativeRect))) != 0) frame = window; // DWMWA_EXTENDED_FRAME_BOUNDS
            current = Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom);
            wanted = MaximizedBounds(current, Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom), target);
            return true;
        }

        // Keep the window's own invisible-border overhang, as Windows does when maximizing,
        // so its visible frame lands exactly on the target.
        internal static Rectangle MaximizedBounds(Rectangle window, Rectangle frame, Rectangle target)
        {
            return Rectangle.FromLTRB(target.Left - (frame.Left - window.Left), target.Top - (frame.Top - window.Top),
                target.Right + (window.Right - frame.Right), target.Bottom + (window.Bottom - frame.Bottom));
        }

        private bool AutomaticCandidate(ApplicationIdentity identity)
        {
            if (!String.IsNullOrWhiteSpace(AutomaticExecutableFilter))
                return String.Equals(identity.ExecutablePath, AutomaticExecutableFilter, StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(identity.ExecutablePath);
            return !String.Equals(name, "DisplayWeave.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(name, "RegionHookHost.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(name, "FullscreenProbe.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProtectedShellTarget(ApplicationIdentity target)
        {
            // Check process-wide exclusions even when the chosen HWND is an ordinary Explorer folder.
            return ShellProcessProtection.IsExcludedTarget(target.ExecutablePath, target.WindowClass, false) ||
                ShellProcessProtection.IsShellProcessId(target.ProcessId);
        }

        private void RejectShellTarget(ApplicationIdentity target)
        {
            failed.Remove(target.Key);
            failureHistory.ClearProcess(target);
            automaticPolicy.Forget(target.Key);
            LastMessage = ShellProcessProtection.RejectionMessage;
        }

        private void RejectLauncherTarget(ApplicationIdentity target)
        {
            failed.Remove(target.Key);
            failureHistory.ClearProcess(target);
            automaticPolicy.Forget(target.Key);
            LastMessage = LauncherProcessProtection.RejectionMessage;
        }

        private bool WithinExecutableScope(ApplicationIdentity identity)
        {
            return String.IsNullOrWhiteSpace(AutomaticExecutableFilter) ||
                String.Equals(identity.ExecutablePath, AutomaticExecutableFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static ApplicationRule MatchingRule(ApplicationRules rules, ApplicationIdentity identity)
        {
            if (rules == null || rules.Rules == null) return null;
            ApplicationRule force = null;
            foreach (ApplicationRule rule in rules.Rules)
            {
                if (rule == null || !rule.Matches(identity)) continue;
                if (rule.Action == ApplicationAction.Ignore) return rule;
                if (force == null) force = rule;
            }
            return force;
        }

        private static bool TryReadWindowState(IntPtr handle, Rectangle monitor, Session session,
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
            long style = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -16).ToInt64() : GetWindowLong(handle, -16);
            bool borderless = (style & 0x00C00000L) == 0;
            expanded = IsZoomed(handle) || placement.ShowCommand == 3 || (style & 0x01000000L) != 0 ||
                (session != null && session.Enabled && session.NativeExpanded == true) ||
                (borderless && (NearlyCovers(current, monitor) ||
                    (session != null && session.Enabled && !session.NativeExpanded.HasValue && NearlyEquals(current, session.Region))));
            return true;
        }

        private static bool NearlyCovers(Rectangle window, Rectangle monitor)
        {
            return window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
                (long)window.X + window.Width >= (long)monitor.X + monitor.Width - 2 &&
                (long)window.Y + window.Height >= (long)monitor.Y + monitor.Height - 2;
        }

        private static bool NearlyEquals(Rectangle first, Rectangle second)
        {
            return Math.Abs((long)first.Left - second.Left) <= 2 && Math.Abs((long)first.Top - second.Top) <= 2 &&
                Math.Abs((long)first.Width - second.Width) <= 2 && Math.Abs((long)first.Height - second.Height) <= 2;
        }

        public static bool TryResolveLayout(LayoutSettings layout, out List<Rectangle> regions)
        {
            regions = null;
            if (layout == null) return false;
            DisplayMonitor selected = DisplayMonitor.Find(layout.MonitorDevice);
            return selected != null && TryResolveLayoutInArea(layout.Zones,
                layout.UseWorkArea ? selected.WorkingArea : selected.Bounds, out regions);
        }

        internal static bool TryResolveLayoutInArea(IList<Zone> zones, Rectangle area, out List<Rectangle> regions)
        {
            regions = null;
            Rectangle first;
            if (zones == null || zones.Count == 0 || zones[0] == null ||
                !TryResolveRegionInArea(zones[0].Id, zones, area, out first)) return false;
            regions = new List<Rectangle>(zones.Count);
            foreach (Zone zone in zones) regions.Add(zone.ToPixels(area));
            return true;
        }

        // Only integer geometry enters the helper command line and control pipe.
        // Validate before serializing so malformed layouts cannot partially update a host.
        internal static bool TrySerializeRegions(IEnumerable<Rectangle> regions, Rectangle selected, out string serialized)
        {
            serialized = null;
            if (regions == null) return false;
            List<Rectangle> checkedRegions = new List<Rectangle>();
            StringBuilder text = new StringBuilder();
            bool includesSelected = false;
            foreach (Rectangle region in regions)
            {
                if (checkedRegions.Count == 9 || region.Width <= 0 || region.Height <= 0 ||
                    (long)region.X + region.Width > Int32.MaxValue || (long)region.Y + region.Height > Int32.MaxValue)
                    return false;
                foreach (Rectangle previous in checkedRegions)
                    if (previous.IntersectsWith(region)) return false;
                checkedRegions.Add(region);
                if (region == selected) includesSelected = true;
                if (text.Length != 0) text.Append(';');
                text.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", region.X, region.Y, region.Width, region.Height);
            }
            if (!includesSelected) return false;
            serialized = text.ToString();
            return true;
        }

        public static bool TryResolveRegion(ApplicationRule rule, LayoutSettings layout, out Rectangle region)
        {
            region = Rectangle.Empty;
            if (rule == null || layout == null) return false;
            if (!String.Equals(rule.MonitorDevice, layout.MonitorDevice, StringComparison.OrdinalIgnoreCase)) return false;
            DisplayMonitor selected = DisplayMonitor.Find(rule.MonitorDevice);
            if (selected == null) return false;
            return TryResolveRegionInArea(rule.ZoneId, layout.Zones,
                layout.UseWorkArea ? selected.WorkingArea : selected.Bounds, out region);
        }

        // Resolve without repairing or rewriting IDs. Even a valid normalized
        // layout can collapse at a smaller resolution: ToPixels' one-pixel
        // minimum would then make adjacent cells overlap. Reject the whole
        // partition before permitting any saved rule to control a window.
        internal static bool TryResolveRegionInArea(string zoneId, IList<Zone> zones, Rectangle area, out Rectangle region)
        {
            region = Rectangle.Empty;
            if (String.IsNullOrEmpty(zoneId) || zones == null || zones.Count == 0 || zones.Count > 9 ||
                area.Width <= 0 || area.Height <= 0 || (long)area.X + area.Width > Int32.MaxValue ||
                (long)area.Y + area.Height > Int32.MaxValue) return false;

            const double epsilon = 0.000000001;
            HashSet<string> identifiers = new HashSet<string>(StringComparer.Ordinal);
            List<Rectangle> pixels = new List<Rectangle>();
            long coveredPixels = 0;
            double normalizedArea = 0;
            Rectangle candidate = Rectangle.Empty;
            bool found = false;
            foreach (Zone zone in zones)
            {
                if (zone == null || String.IsNullOrEmpty(zone.Id) || !identifiers.Add(zone.Id) ||
                    !Finite(zone.X) || !Finite(zone.Y) || !Finite(zone.Width) || !Finite(zone.Height) ||
                    zone.X < 0 || zone.Y < 0 || zone.Width <= 0 || zone.Height <= 0 ||
                    zone.X >= 1 || zone.Y >= 1 || zone.X + zone.Width > 1 + epsilon ||
                    zone.Y + zone.Height > 1 + epsilon) return false;
                Rectangle cell = zone.ToPixels(area);
                if (cell.Width <= 0 || cell.Height <= 0 || cell.Left < area.Left || cell.Top < area.Top ||
                    (long)cell.X + cell.Width > (long)area.X + area.Width ||
                    (long)cell.Y + cell.Height > (long)area.Y + area.Height) return false;
                foreach (Rectangle previous in pixels)
                    if (previous.IntersectsWith(cell)) return false;
                pixels.Add(cell);
                coveredPixels += (long)cell.Width * cell.Height;
                normalizedArea += zone.Width * zone.Height;
                if (String.Equals(zone.Id, zoneId, StringComparison.Ordinal)) { candidate = cell; found = true; }
            }
            // Validate source geometry as well: rounding must not hide malformed
            // normalized overlaps or gaps from a legacy/edited settings file.
            for (int i = 0; i < zones.Count; i++)
                for (int j = i + 1; j < zones.Count; j++)
                {
                    Zone a = zones[i], b = zones[j];
                    double overlapWidth = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                    double overlapHeight = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                    if (overlapWidth > epsilon && overlapHeight > epsilon) return false;
                }
            if (!found || Math.Abs(normalizedArea - 1) > epsilon * zones.Count ||
                coveredPixels != (long)area.Width * area.Height) return false;
            region = candidate;
            return true;
        }

        private static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private void RecordFailure(ApplicationIdentity target, string detail)
        {
            failed.Add(target.Key);
            string previous = failureHistory.For(target);
            bool genericExit = detail != null && detail.StartsWith("Native helper exited", StringComparison.OrdinalIgnoreCase);
            string message = !String.IsNullOrEmpty(previous) &&
                (genericExit || (FailureRequiresRestart(previous) && !FailureRequiresRestart(detail)))
                ? previous : FormatFailure(target, detail);
            failureHistory.Record(target, message);
            LastMessage = message;
        }

        private void PruneClosedFailures()
        {
            foreach (ApplicationIdentity target in failureHistory.Targets)
            {
                uint currentPid;
                if (LauncherProcessProtection.IsExcludedExecutable(target.ExecutablePath) ||
                    IsProtectedShellTarget(target) || !IsWindow(target.Handle) ||
                    GetWindowThreadProcessId(target.Handle, out currentPid) == 0 || currentPid != target.ProcessId)
                {
                    failureHistory.ClearTarget(target);
                    failed.Remove(target.Key);
                }
            }
        }

        internal static bool FailureRequiresRestart(string detail)
        {
            if (String.IsNullOrEmpty(detail)) return false;
            if (detail.IndexOf("Restart the target application", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return ContainsWindowsError(detail, 1306); // ERROR_REVISION_MISMATCH
        }

        internal static bool FailureIsLauncherExcluded(string detail)
        {
            return !String.IsNullOrEmpty(detail) && detail.IndexOf(
                "Battle.net launcher is excluded because fullscreen hooks can cause crashes",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool FailureRequiresElevation(string detail)
        {
            return !String.IsNullOrEmpty(detail) &&
                (detail.IndexOf("Restart DisplayWeave as administrator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ContainsWindowsError(detail, 740)); // ERROR_ELEVATION_REQUIRED
        }

        internal static bool FailureIsAccessDenied(string detail)
        {
            return ContainsWindowsError(detail, 5); // ERROR_ACCESS_DENIED does not establish an elevation mismatch.
        }

        internal static bool FailureIsInitializationTimeout(string detail)
        {
            return ContainsWindowsError(detail, 1460); // ERROR_TIMEOUT does not identify a permissions failure.
        }

        internal static bool FailureIsLoaderModuleNotObserved(string detail)
        {
            // This explicit category reports an observation, not a Win32 loader
            // error. A remote thread's result cannot stand in for GetLastError.
            if (String.IsNullOrEmpty(detail)) return false;
            const string category = "LOADER_MODULE_NOT_OBSERVED";
            int start = 0;
            while (start < detail.Length)
            {
                int at = detail.IndexOf(category, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                int end = at + category.Length;
                if ((at == 0 || !CategoryCharacter(detail[at - 1])) &&
                    (end == detail.Length || !CategoryCharacter(detail[end]))) return true;
                start = end;
            }
            return false;
        }

        private static bool CategoryCharacter(char value)
        {
            return Char.IsLetterOrDigit(value) || value == '_';
        }

        private static bool ContainsWindowsError(string detail, int error)
        {
            if (String.IsNullOrEmpty(detail)) return false;
            string code = "Windows error " + error.ToString(CultureInfo.InvariantCulture);
            int start = 0;
            while (start < detail.Length)
            {
                int at = detail.IndexOf(code, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                int end = at + code.Length;
                if ((at == 0 || !Char.IsLetterOrDigit(detail[at - 1])) &&
                    (end == detail.Length || !Char.IsLetterOrDigit(detail[end]))) return true;
                start = end;
            }
            return false;
        }

        internal static bool TryParseReady(string message, uint expectedProcessId, out bool reused)
        {
            uint capabilities;
            return TryParseReady(message, expectedProcessId, out reused, out capabilities);
        }

        internal static bool TryParseReady(string message, uint expectedProcessId, out bool reused, out uint capabilities)
        {
            reused = false;
            capabilities = 0;
            if (String.IsNullOrEmpty(message) || expectedProcessId == 0) return false;
            string[] parts = message.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 5 || parts[0] != "READY") return false;
            uint processId;
            if (!UInt32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId != expectedProcessId)
                return false;
            int next = 2;
            if (next < parts.Length && parts[next] == "REUSED")
            {
                reused = true;
                next++;
            }
            if (next == parts.Length) return true; // Legacy helpers do not report capabilities.
            return next + 2 == parts.Length && parts[next] == "CAPS" &&
                UInt32.TryParse(parts[next + 1], NumberStyles.None, CultureInfo.InvariantCulture, out capabilities) &&
                ((capabilities & 5) == 5 || (capabilities & 3) == 3);
        }

        internal static string FormatReady(ApplicationIdentity target, bool automatic, bool enabled, bool reused, uint capabilities = 0)
        {
            string name = target == null ? "the application" : Path.GetFileName(target.ExecutablePath);
            if (String.IsNullOrWhiteSpace(name)) name = "the application";
            string message = reused ? "Reconnected region handling for " + name + " using its existing compatible hooks" :
                (automatic ? "Automatic virtual monitor and window handling ready for " : "Virtual monitor and window handling ready for ") + name;
            if ((capabilities & 5) != 5 && (capabilities & 3) == 3)
                message += " (legacy fullscreen engine)";
            return message + (enabled ? "." : "; control is paused.");
        }

        internal static string FormatFailure(ApplicationIdentity target, string detail)
        {
            string name = target == null ? "Application" : Path.GetFileName(target.ExecutablePath);
            if (String.IsNullOrWhiteSpace(name)) name = "Application";
            string clean = String.IsNullOrWhiteSpace(detail) ? "Fullscreen helper failed" : detail.Trim();
            while (clean.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(6).TrimStart();
            clean = clean.Replace('\r', ' ').Replace('\n', ' ').TrimEnd('.', ' ');
            string targetText = name + (target == null ? String.Empty : " (PID " + target.ProcessId + ")");
            string guidance = FailureIsLauncherExcluded(clean) ? String.Empty : FailureRequiresRestart(clean)
                ? " Close and reopen this application before retrying; its existing hooks cannot safely reconnect to this window."
                : FailureRequiresElevation(clean)
                    ? " Open Status & help > Restart as administrator, then approve the Windows prompt."
                    : FailureIsAccessDenied(clean)
                        ? " Windows denied access to this application. Retrying does not change its permissions; access denial alone does not identify the cause."
                        : FailureIsLoaderModuleNotObserved(clean)
                            ? " Fullscreen component did not start. A detailed Windows failure reason is unavailable."
                            : FailureIsInitializationTimeout(clean)
                                ? " Fullscreen integration did not finish starting. Keep this application's window open and responsive; check the reported startup stage before retrying."
                                : " Use Force fullscreen or Resume handling to retry.";
            return targetText + ": " + clean + "." + guidance;
        }

        internal static string PreferFailure(string current, string candidate)
        {
            if (String.IsNullOrWhiteSpace(candidate)) return current;
            if (String.IsNullOrWhiteSpace(current)) return candidate;
            if (FailureIsLauncherExcluded(candidate) && !FailureIsLauncherExcluded(current)) return candidate;
            if (FailureIsLauncherExcluded(current)) return current;
            if (FailureRequiresRestart(candidate) && !FailureRequiresRestart(current)) return candidate;
            if (FailureRequiresRestart(current)) return current;
            if (FailureRequiresElevation(candidate) && !FailureRequiresElevation(current)) return candidate;
            if (FailureRequiresElevation(current)) return current;
            if (FailureIsAccessDenied(candidate) && !FailureIsAccessDenied(current)) return candidate;
            if (FailureIsAccessDenied(current)) return current;
            if (FailureIsLoaderModuleNotObserved(candidate) && !FailureIsLoaderModuleNotObserved(current)) return candidate;
            if (FailureIsLoaderModuleNotObserved(current)) return current;
            if (FailureIsInitializationTimeout(candidate) && !FailureIsInitializationTimeout(current)) return candidate;
            if (FailureIsInitializationTimeout(current)) return current;
            bool genericCurrent = current.StartsWith("Native helper exited", StringComparison.OrdinalIgnoreCase);
            bool genericCandidate = candidate.StartsWith("Native helper exited", StringComparison.OrdinalIgnoreCase);
            return genericCurrent && !genericCandidate ? candidate : current;
        }

        public void Pause()
        {
            Paused = true;
            foreach (Session session in sessions.Values)
            {
                Disable(session);
            }
            ReturnAllMaximized();
            LastMessage = "Automatic regions and saved rules paused; original window positions requested. Apply saved rules to resume.";
        }

        public void Resume()
        {
            // Tick resolves paused sessions against the current rules and layout
            // before sending ON. Reuse their hosts to avoid reattachment races.
            failed.Clear();
            released.Clear();
            Paused = false;
            LastMessage = "Automatic regions and saved rules active; waiting for application windows.";
        }

        public void ReleaseProcess(ApplicationIdentity identity)
        {
            released.Add(identity.ProcessKey);
            failureHistory.ClearProcess(identity);
            automaticPolicy.Forget(identity.Key);
            Session session;
            if (sessions.TryGetValue(identity.ProcessId, out session))
            { Stop(session); sessions.Remove(identity.ProcessId); }
            LastMessage = "Selected application released from automatic regions and rules for this session. Apply saved rules to resume.";
        }

        // A newly saved rule applies now, even to a process released earlier in this session.
        public void Readmit(ApplicationIdentity identity)
        {
            released.Remove(identity.ProcessKey);
            failed.Remove(identity.Key);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReturnAllMaximized();
            foreach (Session session in sessions.Values) Stop(session);
            sessions.Clear();
        }

        private bool Send(Session session, string command)
        {
            try { session.Host.StandardInput.WriteLine(command); session.Host.StandardInput.Flush(); return true; }
            catch (Exception ex) { RecordFailure(session.Target, "Cannot communicate with fullscreen helper: " + ex.Message); return false; }
        }

        private bool UpdateSessionLayout(Session session, string layoutKey)
        {
            if (String.Equals(session.LayoutKey, layoutKey, StringComparison.Ordinal)) return true;
            if (!Send(session, "LAYOUT " + layoutKey)) return false;
            session.LayoutKey = layoutKey;
            return true;
        }

        private static void Stop(Session session)
        {
            if (session.Host == null) return;
            try
            {
                if (!session.Host.HasExited)
                {
                    session.Host.StandardInput.WriteLine("QUIT"); session.Host.StandardInput.Flush();
                    // Native shutdown includes a 200 ms restoration grace period.
                    // Keep this bounded even if a target fails during attachment.
                    session.Host.WaitForExit(600);
                }
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            finally { session.Host.Dispose(); }
        }

        private static bool HostExited(Session session)
        {
            try { return session.Host.HasExited; } catch (InvalidOperationException) { return true; }
        }

        private static bool WaitForExitedOutput(Session session)
        {
            lock (session.Messages)
            {
                if (session.OutputClosed && session.ErrorClosed) return true;
                if (session.ExitObserved == DateTime.MinValue) session.ExitObserved = DateTime.UtcNow;
                // Never block the UI for an unbounded asynchronous stream flush.
                // Most exits already have EOF; delayed callbacks get up to one second across ticks.
                if ((DateTime.UtcNow - session.ExitObserved).TotalSeconds < 1)
                    Monitor.Wait(session.Messages, 40);
                return (session.OutputClosed && session.ErrorClosed) ||
                    (DateTime.UtcNow - session.ExitObserved).TotalSeconds >= 1;
            }
        }

        private static void QueueMessage(Session session, string message, bool errorStream)
        {
            lock (session.Messages)
            {
                if (message == null)
                {
                    if (errorStream) session.ErrorClosed = true; else session.OutputClosed = true;
                    Monitor.PulseAll(session.Messages);
                    return;
                }
                if (errorStream && !message.StartsWith("ERROR", StringComparison.Ordinal)) message = "ERROR " + message;
                if (session.Messages.Count < 64) session.Messages.Enqueue(message);
                Monitor.PulseAll(session.Messages);
            }
        }

        private static string BoundsArguments(Rectangle r)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", r.X, r.Y, r.Width, r.Height);
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
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
        private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200, SwpAsyncWindowPos = 0x4000;
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
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
