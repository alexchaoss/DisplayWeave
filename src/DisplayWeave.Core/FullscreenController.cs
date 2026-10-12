using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace DisplayWeave
{
    // UI-thread coordinator for every handled window, driven by Tick. Most windows are fitted into their region from
    // outside (FullscreenController.OutsideFit.cs); a Force rule starts the architecture-matched native helper, which
    // loads the component into that one app (FullscreenController.Sessions.cs and HelperSession). Window inspection
    // and the Win32 declarations are in FullscreenController.Windows.cs.
    public sealed partial class FullscreenController : IDisposable
    {
        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;

        // Folder holding native-v2-x64 and native-v2-x86. An elevated host points it at a copy only
        // administrators can change, because an elevated helper must not start from a user-writable folder.
        public static string HelperDirectory = AppDomain.CurrentDomain.BaseDirectory;

        private readonly Dictionary<uint, HelperSession> sessions = new Dictionary<uint, HelperSession>();
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> released = new HashSet<string>(StringComparer.Ordinal);
        private readonly AutomaticRegionPolicy automaticPolicy = new AutomaticRegionPolicy();
        private readonly FullscreenFailureHistory failureHistory = new FullscreenFailureHistory();

        // Listed windows whose saved rule names a missing region or monitor, from the latest tick.
        private readonly List<HandledApp> ruleProblems = new List<HandledApp>();
        // Listed windows that only get the outside maximize fit and are not maximized, from the latest tick.
        private readonly List<HandledApp> readyApps = new List<HandledApp>();
        // Apps whose handling threw; left alone until their window closes, a Force or a resume.
        private readonly HashSet<string> faulted = new HashSet<string>(StringComparer.Ordinal);
        // Self-tests only: runs at the start of each window's handling, so a test can make one app fail.
        internal Action<ApplicationIdentity> FaultForTests;

        private DateTime nextAutomaticLaunch = DateTime.MinValue;
        private bool disposed;
        public bool Paused { get; private set; }
        public string LastMessage { get; private set; }
        public string LastFailure { get { return failureHistory.Latest; } }
        // Why the latest tick could not use the layout at all; empty when it could.
        public string LayoutProblem { get; private set; }
        // The full exception of the latest app whose handling threw, for a bug report; empty if none did.
        public string LastInternalError { get; private set; }
        public string GetFailure(ApplicationIdentity target) { return failureHistory.For(target); }
        public int Count { get { int count = 0; foreach (HelperSession s in sessions.Values) if (s.Enabled && s.Ready) count++; return count; } }
        public int PendingCount { get { int count = 0; foreach (HelperSession s in sessions.Values) if (s.Enabled && !s.Ready) count++; return count; } }
        public int AutomaticCount { get { int count = 0; foreach (HelperSession s in sessions.Values) if (s.Automatic && s.Enabled && s.Ready) count++; return count; } }
        public int FailureCount { get { return failureHistory.Count; } }
        // Windows handled from outside (Ready or fitted) in the latest tick; nothing is loaded into them.
        public int OutsideFitCount { get { return readyApps.Count; } }
        // A hooked app maximized or fullscreen in its region, or not yet reporting its state, or a window shown without
        // its title bar: restarting DisplayWeave would resize it. Other windows fitted from outside stay for the next version.
        public bool HasExpandedApp
        {
            get
            {
                foreach (HelperSession s in sessions.Values) if (s.Enabled && s.NativeExpanded != false) return true;
                foreach (MaximizedFit fit in maximized.Values) if (fit.Styled) return true;
                return false;
            }
        }

        // Test scope only: automatic preparation is limited to this exact executable path when set.
        public string AutomaticExecutableFilter { get; set; }

        public FullscreenController() { LastMessage = LayoutProblem = LastInternalError = String.Empty; }

        // Apps with a helper, a maximized fit, a broken rule or a recorded failure. Call from the Tick thread.
        public List<HandledApp> HandledApps()
        {
            Dictionary<string, HandledApp> apps = new Dictionary<string, HandledApp>(StringComparer.Ordinal);
            foreach (HelperSession s in sessions.Values)
                apps[s.Target.Key] = new HandledApp { Target = s.Target, Region = s.Region,
                    State = !s.Enabled ? HandledAppState.NotActive : s.Ready ? HandledAppState.Working : HandledAppState.Starting };
            foreach (MaximizedFit fit in maximized.Values)
                if (!apps.ContainsKey(fit.Target.Key))
                    apps[fit.Target.Key] = new HandledApp { Target = fit.Target, Region = fit.Region, Error = fit.Reason ?? String.Empty,
                        State = fit.Attempts >= MaximumFitAttempts ? HandledAppState.Error : HandledAppState.FittedFromOutside };
            foreach (HandledApp ready in readyApps)
                if (!apps.ContainsKey(ready.Target.Key)) apps[ready.Target.Key] = ready;
            foreach (HandledApp problem in ruleProblems) apps[problem.Target.Key] = problem;
            foreach (ApplicationIdentity target in failureHistory.Targets)
            {
                HandledApp app;
                if (!apps.TryGetValue(target.Key, out app)) apps[target.Key] = app = new HandledApp { Target = target, State = HandledAppState.Error };
                app.Error = failureHistory.For(target);
            }
            return new List<HandledApp>(apps.Values);
        }

        public void Tick(IEnumerable<WindowCandidate> windows, ApplicationRules rules, LayoutSettings layout)
        {
            if (disposed) return;
            PruneClosedFailures();
            ruleProblems.Clear();
            readyApps.Clear();
            List<Rectangle> layoutRegions;
            bool validLayout = RegionGeometry.TryResolveLayout(layout, out layoutRegions);
            string layoutKey = null;
            if (validLayout) validLayout = HelperProtocol.TrySerializeRegions(layoutRegions, layoutRegions[0], out layoutKey);
            LayoutProblem = String.Empty;
            if (!validLayout)
            {
                string device = layout == null ? String.Empty : layout.MonitorDevice;
                List<string> displays = new List<string>();
                foreach (DisplayMonitor monitor in DisplayMonitor.All()) displays.Add(monitor.DeviceName);
                LayoutProblem = displays.Exists(delegate(string name) { return String.Equals(name, device, StringComparison.OrdinalIgnoreCase); })
                    ? "No app is handled: the regions do not fit display " + device + ". Open Layout, review the regions, then Save layout."
                    : "No app is handled: the layout's display " + device + " was not found; Windows reports " + String.Join(", ", displays) +
                        ". Open Layout, choose a display, then Save layout. Another display or region tool can rename displays; exit it first.";
            }
            TickSessions(rules, layout, validLayout, layoutRegions, layoutKey);
            if (Paused || !validLayout || windows == null) return;
            ReturnUnruledMaximized(rules, layout);
            TickWindows(windows, rules, layout, layoutRegions);
        }

        // Decides, for each listed window, whether it is left alone, fitted from outside or handled by its helper.
        private void TickWindows(IEnumerable<WindowCandidate> windows, ApplicationRules rules, LayoutSettings layout, List<Rectangle> layoutRegions)
        {
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
                // ListWindows already inspected this window during the same tick.
                ApplicationIdentity identity = window.Identity;
                string error;
                if (identity == null && !TryInspect(window.Handle, out identity, out error)) continue;
                if (!WithinExecutableScope(identity) || faulted.Contains(identity.Key)) continue;
                try
                {
                    if (FaultForTests != null) FaultForTests(identity);
                    observed.Add(identity.Key);
                    HelperSession existing;
                    sessions.TryGetValue(identity.ProcessId, out existing);
                    if (existing != null && existing.AwaitingExitOutput) continue;
                    // Native hooks target one main window per process; never retarget a secondary window implicitly.
                    if (existing != null && existing.Target.Key != identity.Key) continue;
                    ApplicationRule rule = MatchingRule(rules, identity);
                    Rectangle region;
                    bool resolved = RegionGeometry.TryResolveRegion(rule, layout, out region);
                    RegionControlMode mode = AutomaticRegionPolicy.Decide(layout.AutomaticRegions, Paused,
                        released.Contains(identity.ProcessKey), rule == null ? (ApplicationAction?)null : rule.Action, resolved);
                    if (mode == RegionControlMode.None)
                    {
                        if (rule != null && rule.Action != ApplicationAction.Ignore && rule.Action != ApplicationAction.CenteredFullscreen &&
                            !resolved && !released.Contains(identity.ProcessKey))
                            ruleProblems.Add(new HandledApp { Target = identity, State = HandledAppState.Error, Error = Path.GetFileName(identity.ExecutablePath) +
                                " (PID " + identity.ProcessId + "): its saved rule names a region or display that no longer exists, so DisplayWeave leaves it alone." +
                                " On App rules, choose a region for it again or remove the rule." });
                        Disable(existing);
                        continue;
                    }
                    if (mode == RegionControlMode.Maximize)
                    {
                        Disable(existing);
                        FitFromOutside(identity, region);
                        continue;
                    }
                    if (mode == RegionControlMode.Borderless)
                    {
                        Disable(existing);
                        // A folder window only ever gets the maximize fit (user decision, 2026-10-11).
                        if (ShellProcessProtection.IsFolderWindow(identity.WindowClass)) FitFromOutside(identity, region);
                        else ShowBorderless(identity, region);
                        continue;
                    }
                    if (mode == RegionControlMode.Force)
                    {
                        if (existing == null && OutsideFitOnly(identity)) FitFromOutside(identity, region);
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
                    // Automatic handling never loads the component: it fits maximized and borderless fullscreen windows from
                    // outside. Only the live hook fixtures, scoped to one executable, still load it automatically.
                    if (String.IsNullOrWhiteSpace(AutomaticExecutableFilter) || (existing == null && OutsideFitOnly(identity)))
                    { Disable(existing); FitFromOutside(identity, region); continue; }
                    if (existing != null) AttachCore(identity, region, false, true, layoutRegions, zoneId);
                    else if (!failed.Contains(identity.Key) && DateTime.UtcNow >= nextAutomaticLaunch)
                    {
                        // Prepare gradually. Foreground applications are considered first on every tick.
                        nextAutomaticLaunch = DateTime.UtcNow.AddMilliseconds(750);
                        AttachCore(identity, region, false, true, layoutRegions, zoneId);
                    }
                }
                catch (Exception ex) { Quarantine(identity, ex); } // the other apps are still handled
            }
            // A fullscreen app may temporarily hide its HWND. Keep its original selection
            // until the session actually ends, instead of selecting from fullscreen bounds.
            foreach (HelperSession session in sessions.Values) observed.Add(session.Target.Key);
            automaticPolicy.Retain(observed);
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

        // ownGuidance: the detail already says what to do, so no generic retry advice is added.
        private void RecordFailure(ApplicationIdentity target, string detail, bool ownGuidance = false)
        {
            failed.Add(target.Key);
            string previous = failureHistory.For(target);
            bool genericExit = detail != null && detail.StartsWith("Native helper exited", StringComparison.OrdinalIgnoreCase);
            string message = !String.IsNullOrEmpty(previous) &&
                (genericExit || (FailureMessages.FailureRequiresRestart(previous) && !FailureMessages.FailureRequiresRestart(detail)))
                ? previous : FailureMessages.FormatFailure(target, detail, ownGuidance);
            failureHistory.Record(target, message);
            LastMessage = message;
        }

        // Leaves one app alone after its handling threw, so the bug cannot stop the others, and says why on its row.
        private void Quarantine(ApplicationIdentity target, Exception error)
        {
            faulted.Add(target.Key);
            LastInternalError = error.ToString();
            RecordFailure(target, "DisplayWeave hit an internal error with this app and now leaves it alone (" +
                error.GetType().Name + ": " + error.Message.TrimEnd('.', ' ') + ")");
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
                    faulted.Remove(target.Key);
                }
            }
        }

        public void Pause()
        {
            Paused = true;
            foreach (HelperSession session in sessions.Values)
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
            faulted.Clear();
            released.Clear();
            Paused = false;
            LastMessage = "Automatic regions and saved rules active; waiting for application windows.";
        }

        public void ReleaseProcess(ApplicationIdentity identity)
        {
            released.Add(identity.ProcessKey);
            failureHistory.ClearProcess(identity);
            automaticPolicy.Forget(identity.Key);
            HelperSession session;
            if (sessions.TryGetValue(identity.ProcessId, out session))
            { session.Stop(); sessions.Remove(identity.ProcessId); }
            LastMessage = "Selected application released from automatic regions and rules for this session. Apply saved rules to resume.";
        }

        // A newly saved rule applies now, even to a process released earlier in this session.
        public void Readmit(ApplicationIdentity identity)
        {
            released.Remove(identity.ProcessKey);
            failed.Remove(identity.Key);
            faulted.Remove(identity.Key);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReturnAllMaximized();
            foreach (HelperSession session in sessions.Values) session.Stop();
            sessions.Clear();
        }
    }
}
