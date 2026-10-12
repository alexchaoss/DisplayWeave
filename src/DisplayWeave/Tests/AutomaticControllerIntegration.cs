using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Runs the production automatic discovery path against only the fixture
    // process this method launches. Never calls Attach or writes saved settings.
    // The executable filter keeps automatic handling loading the component for this
    // fixture; without a filter, automatic handling only fits windows from outside.
    public static class AutomaticControllerIntegration
    {
        public static int RunMonitor(string probePath, string reportPath)
        {
            string fixture = Path.GetFullPath(probePath), report = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            StringBuilder log = new StringBuilder();
            log.AppendLine("Managed automatic virtual monitor integration");
            log.AppendLine("Fixture: " + fixture);
            log.AppendLine("Discovery: production Tick, restricted to the launched fixture PID, creation time and executable.");
            log.AppendLine("Manual Attach calls: none. App rules: none. Saved settings: unchanged.");
            Process probe = null;
            FullscreenController controller = null;
            ApplicationContext loop = null;
            System.Windows.Forms.Timer timer = null;
            int result = 1;
            try
            {
                if (!File.Exists(fixture)) throw new FileNotFoundException("The monitor probe is missing.", fixture);
                if (File.Exists(report) || File.Exists(report + ".ready.json") || File.Exists(report + ".controller.txt"))
                    throw new IOException("Choose a fresh report path for the monitor fixture.");
                WindowManager.EnableDpiAwareness();
                Screen selected = Screen.PrimaryScreen;
                if (selected == null) throw new InvalidOperationException("No primary monitor is available.");
                LayoutSettings layout = new LayoutSettings { MonitorDevice = selected.DeviceName, UseWorkArea = false,
                    AutomaticRegions = true, Zones = LayoutSettings.Preset(1) };
                List<Rectangle> regions;
                if (!FullscreenController.TryResolveLayoutInArea(layout.Zones, selected.Bounds, out regions))
                    throw new InvalidOperationException("The monitor cannot contain three fixture regions.");
                ApplicationRules emptyRules = new ApplicationRules();
                controller = new FullscreenController { AutomaticExecutableFilter = fixture };
                loop = new ApplicationContext();
                timer = new System.Windows.Forms.Timer { Interval = 50 };
                ProcessStartInfo start = new ProcessStartInfo(fixture, "--verify-monitor --report " + Quote(report));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Normal;
                start.WorkingDirectory = Path.GetDirectoryName(fixture);
                probe = Process.Start(start);
                if (probe == null) throw new IOException("The monitor probe did not start.");
                uint fixturePid = (uint)probe.Id;
                long fixtureCreation = probe.StartTime.ToUniversalTime().ToFileTimeUtc();
                log.AppendLine("Launched fixture PID: " + fixturePid.ToString(CultureInfo.InvariantCulture));
                string encoded;
                FullscreenController.TrySerializeRegions(regions, regions[1], out encoded);
                log.AppendLine("Complete three-column layout: " + encoded);
                IntPtr fixtureWindow = IntPtr.Zero;
                int phase = 0, pendingPhase = 0, discoveryTicks = 0;
                long nextPhaseAt = 0;
                string lastMessage = String.Empty;
                Stopwatch elapsed = Stopwatch.StartNew();
                timer.Tick += delegate
                {
                    try
                    {
                        if (probe.HasExited) throw new InvalidOperationException("The monitor fixture exited before its checks completed (" + probe.ExitCode + ").");
                        if (elapsed.Elapsed.TotalSeconds > 40) throw new TimeoutException("Automatic monitor integration timed out.");
                        if (fixtureWindow == IntPtr.Zero)
                        {
                            if (!TryReadMonitorReady(report + ".ready.json", fixturePid, selected.Bounds, out fixtureWindow)) return;
                        }
                        uint currentPid;
                        if (GetWindowThreadProcessId(fixtureWindow, out currentPid) == 0 || currentPid != fixturePid)
                            throw new InvalidOperationException("The fixture window identity changed.");
                        List<WindowCandidate> candidates = new List<WindowCandidate>();
                        foreach (WindowCandidate candidate in WindowManager.ListWindows())
                        {
                            if (candidate.Handle != fixtureWindow) continue;
                            ApplicationIdentity identity;
                            string error;
                            if (FullscreenController.TryInspect(candidate.Handle, out identity, out error) &&
                                identity.ProcessId == fixturePid && identity.CreationTime == fixtureCreation &&
                                String.Equals(identity.ExecutablePath, fixture, StringComparison.OrdinalIgnoreCase)) candidates.Add(candidate);
                        }
                        controller.Tick(candidates, emptyRules, layout);
                        discoveryTicks++;
                        if (controller.Count + controller.PendingCount > 1 || emptyRules.Rules.Count != 0)
                            throw new InvalidOperationException("Automatic fixture isolation invariant failed.");
                        if (controller.LastMessage != lastMessage)
                        {
                            lastMessage = controller.LastMessage;
                            log.AppendLine(elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms: " + lastMessage);
                        }
                        if (controller.FailureCount != 0) throw new InvalidOperationException(controller.LastFailure);
                        // The native fixture checks APIs against the shared geometry.
                        // Independently check the controller chose the intended middle
                        // cell and sent all cells, so both sides cannot agree on a
                        // consistently wrong region and produce a false pass.
                        if (phase < 5 && controller.AutomaticCount == 1 &&
                            !MonitorConfigurationMatches(fixturePid, fixtureWindow, regions)) return;
                        if (pendingPhase != 0)
                        {
                            if (!MonitorPhaseCompleted(report, pendingPhase)) return;
                            phase = pendingPhase; pendingPhase = 0;
                            log.AppendLine("PASS fixture phase " + phase.ToString(CultureInfo.InvariantCulture));
                            if (phase == 9)
                            {
                                result = 0;
                                log.AppendLine("Discovery ticks: " + discoveryTicks.ToString(CultureInfo.InvariantCulture));
                                timer.Stop(); loop.ExitThread(); return;
                            }
                            if (phase == 5)
                            {
                                controller.Pause();
                                log.AppendLine("Controller.Pause requested physical monitor and window restoration.");
                            }
                            nextPhaseAt = elapsed.ElapsedMilliseconds + 600;
                            return;
                        }
                        if (elapsed.ElapsedMilliseconds < nextPhaseAt || (phase == 0 && controller.AutomaticCount != 1)) return;
                        pendingPhase = phase == 5 ? 9 : phase + 1;
                        UIntPtr answer;
                        if (SendMessageTimeout(fixtureWindow, 0x8000 + 100, new UIntPtr((uint)pendingPhase), IntPtr.Zero,
                            0x0001 | 0x0002 | 0x0020, 500, out answer) == IntPtr.Zero || answer.ToUInt64() != 1)
                            throw new InvalidOperationException("The owned fixture did not acknowledge phase " + pendingPhase + ".");
                    }
                    catch (Exception ex)
                    {
                        result = 1;
                        log.AppendLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                        timer.Stop(); loop.ExitThread();
                    }
                };
                timer.Start(); Application.Run(loop);
            }
            catch (Exception ex) { result = 1; log.AppendLine("FAIL " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (controller != null) { controller.Pause(); controller.Dispose(); }
                if (probe != null)
                {
                    try
                    {
                        if (!probe.HasExited)
                        {
                            probe.CloseMainWindow();
                            if (!probe.WaitForExit(1500)) { probe.Kill(); probe.WaitForExit(1500); }
                        }
                        if (result == 0 && probe.ExitCode != 0) { result = 1; log.AppendLine("FAIL fixture exit code: " + probe.ExitCode); }
                    }
                    catch (Exception ex) { result = 1; log.AppendLine("Fixture cleanup failed: " + ex.Message); }
                    probe.Dispose();
                }
                if (loop != null) loop.Dispose();
                log.AppendLine(result == 0 ? "PASS automatic discovery, all-region monitor APIs, maximize, borderless fullscreen, windowed rendering and Pause restoration."
                    : "FAIL automatic virtual monitor integration; inspect fixture report and controller log.");
                File.WriteAllText(report + ".controller.txt", log.ToString(), new UTF8Encoding(false));
            }
            return result;
        }

        private static bool TryReadMonitorReady(string path, uint pid, Rectangle physical, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (!File.Exists(path)) return false;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { return false; }
            if (!json.TrimEnd().EndsWith("}", StringComparison.Ordinal)) return false;
            long reportedPid = JsonInteger(json, "pid");
            Match window = Regex.Match(json, "\"hwnd\"\\s*:\\s*\"([0-9a-fA-F]+)\"");
            long rawWindow;
            if (reportedPid != pid || !window.Success || !Int64.TryParse(window.Groups[1].Value, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out rawWindow) || rawWindow <= 0 ||
                JsonInteger(json, "x") != physical.X || JsonInteger(json, "y") != physical.Y ||
                JsonInteger(json, "width") != physical.Width || JsonInteger(json, "height") != physical.Height)
                throw new InvalidOperationException("The monitor ready marker does not match the owned fixture and selected display.");
            handle = new IntPtr(rawWindow); return true;
        }

        private static bool MonitorPhaseCompleted(string path, int phase)
        {
            if (!File.Exists(path)) return false;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { return false; }
            long reportedPhase = JsonInteger(json, "phase");
            if (reportedPhase != phase) return false;
            if (JsonInteger(json, "failures") != 0 || JsonInteger(json, "failedPresents") != 0 || JsonInteger(json, "checks") <= 0)
                throw new InvalidOperationException("Monitor fixture phase " + phase + " failed; see " + path + ".txt.");
            return true;
        }

        private static bool MonitorConfigurationMatches(uint pid, IntPtr window, IList<Rectangle> regions)
        {
            byte[] control = ReadMappingSnapshot("Local\\DisplayWeave_" + pid.ToString(CultureInfo.InvariantCulture), 96);
            byte[] layout = ReadMappingSnapshot("Local\\DisplayWeave_Layout_" + pid.ToString(CultureInfo.InvariantCulture), 176);
            if (control == null || layout == null) return false;
            Rectangle expected = regions[1];
            bool matches = BitConverter.ToUInt32(control, 0) == 0x52534731 && BitConverter.ToUInt32(control, 4) == 2 &&
                BitConverter.ToUInt32(control, 8) == 96 && BitConverter.ToUInt32(control, 16) == pid &&
                BitConverter.ToUInt32(control, 20) != 0 && BitConverter.ToInt64(control, 24) == window.ToInt64() &&
                BitConverter.ToInt32(control, 40) == expected.X && BitConverter.ToInt32(control, 44) == expected.Y &&
                BitConverter.ToInt32(control, 48) == expected.Width && BitConverter.ToInt32(control, 52) == expected.Height &&
                BitConverter.ToInt32(control, 56) == 1 && BitConverter.ToInt32(control, 64) == 1 &&
                BitConverter.ToUInt32(control, 72) == pid && (BitConverter.ToUInt32(control, 76) & 7) == 5 &&
                BitConverter.ToInt32(control, 88) == 0 && BitConverter.ToUInt32(layout, 0) == 0x52534c31 &&
                BitConverter.ToUInt32(layout, 4) == 1 && BitConverter.ToUInt32(layout, 8) == 176 &&
                BitConverter.ToUInt32(layout, 16) == pid && BitConverter.ToUInt32(layout, 20) == BitConverter.ToUInt32(control, 20) &&
                BitConverter.ToInt32(layout, 24) == regions.Count;
            for (int i = 0; i < regions.Count && matches; i++)
            {
                int offset = 32 + i * 16;
                Rectangle region = regions[i];
                matches = BitConverter.ToInt32(layout, offset) == region.X && BitConverter.ToInt32(layout, offset + 4) == region.Y &&
                    BitConverter.ToInt32(layout, offset + 8) == region.Width && BitConverter.ToInt32(layout, offset + 12) == region.Height;
            }
            if (!matches) throw new InvalidOperationException("The controller did not publish the expected middle region and complete three-column monitor layout.");
            return true;
        }

        private static byte[] ReadMappingSnapshot(string name, int size)
        {
            IntPtr mapping = OpenFileMapping(0x0004, false, name); // FILE_MAP_READ only.
            if (mapping == IntPtr.Zero) return null;
            try
            {
                IntPtr view = MapViewOfFile(mapping, 0x0004, 0, 0, new UIntPtr((uint)size));
                if (view == IntPtr.Zero) return null;
                try
                {
                    int sequence = Marshal.ReadInt32(view, 12);
                    if ((sequence & 1) != 0) return null;
                    System.Threading.Thread.MemoryBarrier();
                    byte[] bytes = new byte[size];
                    Marshal.Copy(view, bytes, 0, size);
                    System.Threading.Thread.MemoryBarrier();
                    return Marshal.ReadInt32(view, 12) == sequence && BitConverter.ToInt32(bytes, 12) == sequence ? bytes : null;
                }
                finally { UnmapViewOfFile(view); }
            }
            finally { CloseHandle(mapping); }
        }

        private static long JsonInteger(string json, string name)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?[0-9]+)");
            long value;
            if (!match.Success || !Int64.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value)) throw new InvalidOperationException("Invalid fixture report field: " + name);
            return value;
        }

        public static int Run(string probePath, string reportPath)
        {
            string fixture = Path.GetFullPath(probePath);
            string report = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            StringBuilder log = new StringBuilder();
            log.AppendLine("Managed automatic controller integration");
            log.AppendLine("Fixture: " + fixture);
            log.AppendLine("Report: " + report);
            log.AppendLine("Discovery: WindowManager.ListWindows -> FullscreenController.Tick, every 50 ms.");
            log.AppendLine("Isolation: exact executable filter plus only the newly launched process ID.");
            log.AppendLine("Manual Attach calls: none. Saved/manual rules: none. Settings files: not modified.");
            Process probe = null;
            FullscreenController controller = null;
            ApplicationContext loop = null;
            System.Windows.Forms.Timer timer = null;
            int result = 1;
            try
            {
                if (!File.Exists(fixture)) throw new FileNotFoundException("The fullscreen probe is missing.", fixture);
                if (File.Exists(report) || File.Exists(report + ".ready.json") || File.Exists(report + ".await-release.json"))
                    throw new IOException("Choose a new report path so stale fixture markers cannot affect the test.");
                WindowManager.EnableDpiAwareness();
                LayoutSettings layout = CreateFixtureLayout();
                ApplicationRules emptyRules = new ApplicationRules();
                controller = new FullscreenController { AutomaticExecutableFilter = fixture };
                loop = new ApplicationContext();
                timer = new System.Windows.Forms.Timer { Interval = 50 };
                ProcessStartInfo start = new ProcessStartInfo(fixture,
                    "--verify-auto 240 180 960 540 --initial-position 300 230 --delay-ms 5000 --report " + Quote(report));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Normal;
                start.WorkingDirectory = Path.GetDirectoryName(fixture);
                probe = Process.Start(start);
                if (probe == null) throw new IOException("The fullscreen probe did not start.");
                uint fixturePid = (uint)probe.Id;
                long fixtureCreation = probe.StartTime.ToUniversalTime().ToFileTimeUtc();
                log.AppendLine("Launched fixture PID: " + fixturePid.ToString(CultureInfo.InvariantCulture));
                log.AppendLine("Layout: a complete 3 x 3 partition of the primary monitor; selected central cell is 240,180,960,540.");
                bool readySeen = false;
                bool automaticReadySeen = false;
                bool pauseRequested = false;
                int discoveryTicks = 0;
                string lastMessage = String.Empty;
                Stopwatch elapsed = Stopwatch.StartNew();
                timer.Tick += delegate
                {
                    try
                    {
                        if (probe.HasExited)
                        {
                            result = probe.ExitCode == 0 && File.Exists(report) && readySeen && automaticReadySeen
                                && pauseRequested && emptyRules.Rules.Count == 0 ? 0 : 1;
                            log.AppendLine("Probe exit code: " + probe.ExitCode.ToString(CultureInfo.InvariantCulture));
                            log.AppendLine("Automatic ready observed: " + automaticReadySeen);
                            log.AppendLine("Release requested through controller.Pause: " + pauseRequested);
                            log.AppendLine("Discovery ticks: " + discoveryTicks.ToString(CultureInfo.InvariantCulture));
                            timer.Stop(); loop.ExitThread(); return;
                        }
                        if (elapsed.Elapsed.TotalSeconds > 35) throw new TimeoutException("The automatic controller integration test timed out.");
                        if (!readySeen)
                        {
                            readySeen = ReadyMarkerMatches(report + ".ready.json", fixturePid);
                            if (!readySeen) return;
                            log.AppendLine("Fixture initialized; beginning automatic discovery with an empty rule set.");
                        }
                        List<WindowCandidate> fixtureWindows = new List<WindowCandidate>();
                        foreach (WindowCandidate candidate in WindowManager.ListWindows())
                        {
                            uint candidatePid;
                            GetWindowThreadProcessId(candidate.Handle, out candidatePid);
                            ApplicationIdentity identity;
                            string inspectionError;
                            if (candidatePid == fixturePid && FullscreenController.TryInspect(candidate.Handle, out identity, out inspectionError)
                                && identity.CreationTime == fixtureCreation) fixtureWindows.Add(candidate);
                        }
                        // Even another live instance of FullscreenProbe is excluded.
                        controller.Tick(fixtureWindows, emptyRules, layout);
                        discoveryTicks++;
                        if (controller.AutomaticCount > 0) automaticReadySeen = true;
                        if (controller.Count + controller.PendingCount > 1)
                            throw new InvalidOperationException("Fixture isolation invariant failed.");
                        if (controller.LastMessage != lastMessage)
                        {
                            lastMessage = controller.LastMessage;
                            log.AppendLine(elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms: " + lastMessage);
                        }
                        if (!pauseRequested && File.Exists(report + ".await-release.json"))
                        {
                            controller.Pause();
                            pauseRequested = true;
                            log.AppendLine("Controller.Pause requested OFF restoration.");
                        }
                    }
                    catch (Exception ex)
                    {
                        result = 1;
                        log.AppendLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
                        timer.Stop(); loop.ExitThread();
                    }
                };
                timer.Start();
                Application.Run(loop);
            }
            catch (Exception ex)
            {
                result = 1;
                log.AppendLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (timer != null) { timer.Stop(); timer.Dispose(); }
                if (controller != null) { controller.Pause(); controller.Dispose(); }
                // Own only the process returned by Process.Start; never enumerate
                // and terminate applications by executable name or window title.
                if (probe != null)
                {
                    try
                    {
                        if (!probe.HasExited)
                        {
                            probe.CloseMainWindow();
                            if (!probe.WaitForExit(1500)) { probe.Kill(); probe.WaitForExit(1500); }
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { log.AppendLine("Fixture cleanup: " + ex.Message); result = 1; }
                    probe.Dispose();
                }
                if (loop != null) loop.Dispose();
                log.AppendLine(result == 0 ? "PASS automatic discovery, region selection, fullscreen/maximize, and Pause restoration."
                    : "FAIL managed automatic controller integration; inspect fixture JSON and controller messages.");
                File.WriteAllText(report + ".controller.txt", log.ToString(), new UTF8Encoding(false));
            }
            return result;
        }

        private static LayoutSettings CreateFixtureLayout()
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen == null) throw new InvalidOperationException("No primary monitor is available.");
            Rectangle area = screen.Bounds;
            int[] x = { area.Left, 240, 1200, area.Right };
            int[] y = { area.Top, 180, 720, area.Bottom };
            for (int i = 1; i < 4; i++)
                if (x[i] <= x[i - 1] || y[i] <= y[i - 1])
                    throw new InvalidOperationException("The primary monitor must contain the 240,180,960,540 fixture region with room on all sides.");
            LayoutSettings layout = new LayoutSettings
            {
                MonitorDevice = screen.DeviceName,
                UseWorkArea = false,
                AutomaticRegions = true,
                Zones = new List<Zone>()
            };
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    layout.Zones.Add(Zone.FromPixels("Fixture " + row + "," + column,
                        Rectangle.FromLTRB(x[column], y[row], x[column + 1], y[row + 1]), area));
            List<double> xEdges, yEdges;
            List<Zone> ordered;
            if (!LayoutGeometry.TryReadCartesian(layout.Zones, out xEdges, out yEdges, out ordered)
                || layout.Zones[4].ToPixels(area) != new Rectangle(240, 180, 960, 540))
                throw new InvalidOperationException("The fixture layout did not form the expected exact partition.");
            return layout;
        }

        private static bool ReadyMarkerMatches(string path, uint pid)
        {
            if (!File.Exists(path)) return false;
            try
            {
                Match match = Regex.Match(File.ReadAllText(path), "\"pid\"\\s*:\\s*([0-9]+)");
                uint reportedPid;
                return match.Success && UInt32.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out reportedPid) && reportedPid == pid;
            }
            catch (IOException) { return false; }
        }

        private static string Quote(string value)
        {
            // File paths cannot contain quotation marks; the report is a file,
            // so it also cannot end with a directory separator.
            if (value.IndexOf('"') >= 0 || value.EndsWith("\\", StringComparison.Ordinal))
                throw new ArgumentException("Invalid report file path.");
            return "\"" + value + "\"";
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam,
            uint flags, uint timeout, out UIntPtr result);
        [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr size);
        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
