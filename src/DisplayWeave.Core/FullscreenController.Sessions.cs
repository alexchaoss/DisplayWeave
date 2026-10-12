using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace DisplayWeave
{
    // Force rules: one native helper per app loads the component and keeps it on its region.
    public sealed partial class FullscreenController
    {
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
                HelperSession excludedSession;
                if (sessions.TryGetValue(target.ProcessId, out excludedSession))
                {
                    excludedSession.Stop();
                    sessions.Remove(target.ProcessId);
                }
                return false;
            }
            string outsideOnly = OutsideOnlyReason(target.WindowClass);
            if (outsideOnly != null)
            {
                LastMessage = outsideOnly;
                return false;
            }
            if (IsProtectedShellTarget(target))
            {
                RejectShellTarget(target);
                HelperSession unsafeSession;
                if (sessions.TryGetValue(target.ProcessId, out unsafeSession))
                {
                    unsafeSession.Stop();
                    sessions.Remove(target.ProcessId);
                }
                return false;
            }
            if (!WithinExecutableScope(target))
            {
                LastMessage = "Test mode only permits its selected executable.";
                return false;
            }
            if (explicitRequest) { failed.Remove(target.Key); faulted.Remove(target.Key); released.Remove(target.ProcessKey); }
            else if (Paused || failed.Contains(target.Key) || released.Contains(target.ProcessKey)) return false;
            string layoutKey;
            if (!HelperProtocol.TrySerializeRegions(regions ?? new Rectangle[] { region }, region, out layoutKey))
            {
                LastMessage = "The display layout is invalid. Review its regions before assigning this application.";
                return false;
            }
            HelperSession existing;
            if (sessions.TryGetValue(target.ProcessId, out existing))
            {
                if (existing.Target.Key == target.Key && existing.HostExited())
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
                    if (existing.Region != region && !Send(existing, "UPDATE " + HelperProtocol.BoundsArguments(region))) return false;
                    existing.Region = region;
                    if (!existing.Enabled && !Send(existing, "ON")) return false;
                    existing.Enabled = true;
                    return true;
                }
                existing.Stop();
                sessions.Remove(target.ProcessId);
            }
            string architecture = target.Is32Bit ? "native-v2-x86" : "native-v2-x64";
            string helper = Path.Combine(HelperDirectory, architecture, "RegionHookHost.exe");
            if (!File.Exists(helper) || !File.Exists(Path.Combine(Path.GetDirectoryName(helper), "RegionHook.dll")))
            { RecordFailure(target, "The " + architecture + " fullscreen helper is missing. Build the native engine first."); return false; }
            HelperSession session = new HelperSession { Target = target, Region = region, LayoutKey = layoutKey,
                Started = DateTime.UtcNow, Automatic = automatic, AutomaticZoneId = automatic ? automaticZoneId : null };
            ProcessStartInfo start = new ProcessStartInfo(helper, target.ProcessId.ToString(CultureInfo.InvariantCulture) + " 0x" +
                target.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture) + " " + HelperProtocol.BoundsArguments(region) +
                " --regions \"" + layoutKey + "\"" + (automatic ? " --auto" : String.Empty));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.WorkingDirectory = Path.GetDirectoryName(helper);
            session.Host = new Process { StartInfo = start };
            session.Host.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { session.QueueMessage(e.Data, false); };
            session.Host.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { session.QueueMessage(e.Data, true); };
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
                session.Stop();
                RecordFailure(target, "Fullscreen helper failed: " + ex.Message);
                return false;
            }
        }

        // Reads each helper's messages, ends sessions whose app or helper is gone, and keeps the others' mode and regions current.
        private void TickSessions(ApplicationRules rules, LayoutSettings layout, bool validLayout, List<Rectangle> layoutRegions, string layoutKey)
        {
            List<uint> remove = new List<uint>();
            foreach (KeyValuePair<uint, HelperSession> pair in sessions)
            {
                HelperSession session = pair.Value;
                if (faulted.Contains(session.Target.Key)) { session.Stop(); remove.Add(pair.Key); continue; }
                try
                {
                    if (LauncherProcessProtection.IsExcludedExecutable(session.Target.ExecutablePath))
                    {
                        RejectLauncherTarget(session.Target);
                        session.Stop();
                        remove.Add(pair.Key);
                        continue;
                    }
                    if (IsProtectedShellTarget(session.Target))
                    {
                        RejectShellTarget(session.Target);
                        session.Stop();
                        remove.Add(pair.Key);
                        continue;
                    }
                    if (!WithinExecutableScope(session.Target)) { session.Stop(); remove.Add(pair.Key); continue; }
                    bool hostExited = session.HostExited();
                    bool outputComplete = !hostExited || session.WaitForExitedOutput();
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
                                if (!HelperProtocol.TryParseReady(message, session.Target.ProcessId, out reused, out capabilities))
                                {
                                    failure = FailureMessages.PreferFailure(failure, "Native helper sent an invalid readiness response or acknowledged another process.");
                                    continue;
                                }
                                session.Ready = true;
                                failed.Remove(session.Target.Key);
                                failureHistory.ClearTarget(session.Target);
                                LastMessage = HelperProtocol.FormatReady(session.Target, session.Automatic, session.Enabled, reused, capabilities);
                            }
                            else if (message.StartsWith("STATE ", StringComparison.Ordinal))
                            {
                                if (message == "STATE NORMAL") session.NativeExpanded = false;
                                else if (message == "STATE MAXIMIZED" || message == "STATE FULLSCREEN" || message == "STATE FORCED")
                                    session.NativeExpanded = true;
                            }
                            else if (message.StartsWith("ERROR", StringComparison.Ordinal)) failure = FailureMessages.PreferFailure(failure, message);
                            else if (message.Length != 0 && !message.StartsWith("OK ", StringComparison.Ordinal) && message != "STOPPED") LastMessage = message;
                        }
                    }
                    uint currentPid;
                    if (!IsWindow(session.Target.Handle) || GetWindowThreadProcessId(session.Target.Handle, out currentPid) == 0 || currentPid != session.Target.ProcessId)
                    { session.Stop(); remove.Add(pair.Key); continue; }
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
                        session.Stop(); remove.Add(pair.Key);
                    }
                    else if (!Paused)
                    {
                        if (!validLayout) { Disable(session); continue; }
                        // Hidden application windows do not appear in the eligible candidate list.
                        // Preference/rule changes must still disarm their existing native hooks now.
                        ApplicationRule currentRule = MatchingRule(rules, session.Target, layout);
                        Rectangle ignoredRegion;
                        bool ruleResolved = RegionGeometry.TryResolveRegion(currentRule, layout, out ignoredRegion);
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
                            session.Stop();
                            remove.Add(pair.Key);
                        }
                        else if (session.Region != currentRegion)
                        {
                            // Hidden and minimized windows do not appear in the
                            // candidate list, but still need current region bounds.
                            if (Send(session, "UPDATE " + HelperProtocol.BoundsArguments(currentRegion))) session.Region = currentRegion;
                            else { session.Stop(); remove.Add(pair.Key); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // An error with one app stops only that app's handling.
                    Quarantine(session.Target, ex);
                    session.Stop();
                    remove.Add(pair.Key);
                }
            }
            foreach (uint pid in remove) sessions.Remove(pid);
        }

        private void Disable(HelperSession session)
        {
            if (session != null && session.Enabled)
            {
                if (!Send(session, "OFF")) session.Stop();
                session.Enabled = false;
            }
        }

        private bool Send(HelperSession session, string command)
        {
            try { session.Host.StandardInput.WriteLine(command); session.Host.StandardInput.Flush(); return true; }
            catch (Exception ex) { RecordFailure(session.Target, "Cannot communicate with fullscreen helper: " + ex.Message); return false; }
        }

        private bool UpdateSessionLayout(HelperSession session, string layoutKey)
        {
            if (String.Equals(session.LayoutKey, layoutKey, StringComparison.Ordinal)) return true;
            if (!Send(session, "LAYOUT " + layoutKey)) return false;
            session.LayoutKey = layoutKey;
            return true;
        }
    }
}
