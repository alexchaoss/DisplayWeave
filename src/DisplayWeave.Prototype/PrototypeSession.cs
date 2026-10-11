using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DisplayWeave.Prototype
{
    // request.json, written once by the UI before the worker starts. 64-bit
    // handles and FILETIMEs are exact decimal strings.
    public sealed class SessionRequest
    {
        public int SchemaVersion { get; set; }
        public string SessionId { get; set; }
        public string Mode { get; set; }
        public int DurationSeconds { get; set; }
        public bool Borderless { get; set; }
        public string SourceHwnd { get; set; }
        public int SourcePid { get; set; }
        public string SourceStartedUtcFileTime { get; set; }
        public string SourceExecutable { get; set; }
        public string ReturnHwnd { get; set; }
        public int[] Region { get; set; }

        public bool Interactive { get { return Mode == "interactive"; } }
        public Rectangle RegionRectangle { get { return new Rectangle(Region[0], Region[1], Region[2], Region[3]); } }

        public string Validate(string expectedSessionId)
        {
            long value;
            if (SchemaVersion != 1) return "Unsupported request schema.";
            if (String.IsNullOrEmpty(SessionId) || SessionId != expectedSessionId) return "Request does not belong to this session folder.";
            if (Mode != "visual" && Mode != "interactive") return "Mode must be visual or interactive.";
            if (!Policy.AllowedDuration(DurationSeconds)) return "Duration must be 30, 60 or 90 seconds.";
            if (!Int64.TryParse(SourceHwnd, out value) || value == 0) return "Invalid source window handle.";
            if (SourcePid <= 4) return "Invalid source process.";
            if (!Int64.TryParse(SourceStartedUtcFileTime, out value) || value <= 0) return "Invalid source process start time.";
            if (String.IsNullOrEmpty(SourceExecutable) || !Path.IsPathRooted(SourceExecutable)) return "Invalid source executable.";
            if (!String.IsNullOrEmpty(ReturnHwnd) && !Int64.TryParse(ReturnHwnd, out value)) return "Invalid return window handle.";
            if (Region == null || Region.Length != 4 || Region[2] < 64 || Region[3] < 64 || Region[2] > 16384 || Region[3] > 16384) return "Choose a region of at least 64 x 64 pixels.";
            return null;
        }
    }

    public static class Policy
    {
        public const int LeaseSeconds = 120;        // RegionDisplayLease maximum lifetime.
        public const int CleanupReserveSeconds = 15; // Release, removal and journals after projection.
        public const int MinimumProjectionSeconds = 20;

        public static bool AllowedDuration(int seconds) { return seconds == 30 || seconds == 60 || seconds == 90; }

        // The lease lifetime starts before device creation; projection gets
        // what remains after setup, never more than the user requested.
        public static int EffectiveSeconds(int requested, double leaseRemainingSeconds)
        {
            int available = (int)Math.Floor(leaseRemainingSeconds) - CleanupReserveSeconds;
            int result = Math.Min(requested, available);
            return result >= MinimumProjectionSeconds ? result : 0;
        }

        public static bool ValidEventName(string name, string suffix)
        {
            const string prefix = "Local\\DisplayWeaveProjectionRelease-";
            string ending = "-" + suffix;
            if (name == null || name.Length > 120 || name.Length <= prefix.Length + ending.Length ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(ending, StringComparison.Ordinal)) return false;
            for (int i = prefix.Length; i < name.Length - ending.Length; ++i)
                if (!Uri.IsHexDigit(name[i]) && name[i] != '-') return false;
            return true;
        }

        // The projected region must sit inside exactly one physical output and
        // never overlap the virtual source output it displays.
        public static string ValidateRegion(Rectangle region, IList<Rectangle> physicalOutputs, Rectangle virtualOutput)
        {
            if (region.IntersectsWith(virtualOutput)) return "The region overlaps the virtual display.";
            int containing = physicalOutputs.Count(output => output.Contains(region));
            return containing == 1 ? null : "The region must fit inside exactly one physical display.";
        }

        public static bool Terminal(string state) { return state == "completed" || state == "failed"; }
    }

    public static class JsonFile
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static T Read<T>(string path) { return Serializer.Deserialize<T>(ReadText(path)); }
        public static Dictionary<string, object> ReadObject(string path) { return Serializer.Deserialize<Dictionary<string, object>>(ReadText(path)); }
        public static string Serialize(object value) { return Serializer.Serialize(value); }

        // Share delete so a concurrent atomic replace by the worker never fails.
        private static string ReadText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        // Readers never see a partially written file. Other readers (scripts)
        // may briefly hold the target without delete sharing, so retry.
        public static void Write(string path, object value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, Serializer.Serialize(value), new UTF8Encoding(false));
            for (int attempt = 0; ; ++attempt)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 20) { File.Delete(temporary); throw; }
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 20) { File.Delete(temporary); throw; }
                    Thread.Sleep(25);
                }
            }
        }

        public static object Get(IDictionary<string, object> value, params string[] path)
        {
            object current = value;
            foreach (string key in path)
            {
                var map = current as IDictionary<string, object>;
                if (map == null || !map.TryGetValue(key, out current)) return null;
            }
            return current;
        }
        public static bool Flag(IDictionary<string, object> value, params string[] path) { object result = Get(value, path); return result is bool && (bool)result; }
        public static string Text(IDictionary<string, object> value, params string[] path) { object result = Get(value, path); return result == null ? null : Convert.ToString(result); }
        public static long Number(IDictionary<string, object> value, params string[] path) { object result = Get(value, path); return result == null ? 0 : Convert.ToInt64(result); }
    }

    public static class Paths
    {
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayWeavePrototype"); } }
        public static string Sessions { get { return Path.Combine(Root, "sessions"); } }
        public static string Base { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string Presenter { get { return Path.Combine(Base, "projection-x64", "RegionPresenter.exe"); } }
        public static string Lease { get { return Path.Combine(Base, "projection-x64", "RegionDisplayLease.exe"); } }
        public static string DisplayFixture { get { return Path.Combine(Base, "test-virtual-display.ps1"); } }
        public static string Driver { get { return Path.Combine(Base, "driver"); } }
        public static string LegacyLayout { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayWeave", "data", "layout.xml"); } }

        public static string MissingComponent()
        {
            foreach (string file in new[] { Presenter, Lease, DisplayFixture, Path.Combine(Driver, "MttVDD.inf"), Path.Combine(Driver, "MttVDD.dll"), Path.Combine(Driver, "mttvdd.cat") })
                if (!File.Exists(file)) return file;
            return null;
        }
    }

    // The elevated display step runs only files a non-elevated program cannot change. A short bootstrap
    // travels on the command line: it copies the display fixture, the lease and the driver into a fresh
    // session folder under Program Files, checks the fixture and lease against hashes compiled into this
    // build (the fixture then checks the driver), and runs the copies. Their reports stay in that folder.
    public static class ElevatedFixture
    {
        public static string ProtectedRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DisplayWeavePrototype", "sessions"); } }
        public static string Reports(string sessionId) { return Path.Combine(ProtectedRoot, sessionId, "display"); }
        public static readonly IDictionary<string, string> Verified = new Dictionary<string, string>
        {
            { "test-virtual-display.ps1", BuildHashes.Fixture }, { @"projection-x64\RegionDisplayLease.exe", BuildHashes.Lease }
        };
        private static readonly string[] Unverified = { @"driver\MttVDD.inf", @"driver\MttVDD.dll", @"driver\mttvdd.cat" };

        // powershell.exe arguments. The script holds no double quote, so it stays one argument.
        public static string Arguments(string source, string root, string sessionId, int seconds, IDictionary<string, string> verified)
        {
            var script = new StringBuilder("$ErrorActionPreference='Stop';$source=" + Quote(source) + ";$session=" + Quote(Path.Combine(root, sessionId)) +
                ";$display=Join-Path $session 'display';$tools=Join-Path $session 'tools';$verified=@{");
            foreach (var pair in verified) script.Append(Quote(pair.Key) + "=" + Quote(pair.Value) + ";");
            script.Append("};$files=@($verified.Keys)+@(" + String.Join(",", Unverified.Select(Quote)) + ");try{" +
                // Reports of sessions older than a week are removed.
                "$parent=Split-Path -Parent $session;if(Test-Path -LiteralPath $parent){Get-ChildItem -LiteralPath $parent -Directory|" +
                "Where-Object{$_.CreationTimeUtc -lt [DateTime]::UtcNow.AddDays(-7)}|Remove-Item -Recurse -Force -ErrorAction SilentlyContinue};" +
                "if(Test-Path -LiteralPath $session){throw 'The protected session folder already exists.'};" +
                "foreach($name in $files){$to=Join-Path $tools $name;New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force|Out-Null;" +
                "Copy-Item -LiteralPath (Join-Path $source $name) -Destination $to};" +
                "foreach($name in $verified.Keys){if((Get-FileHash -LiteralPath (Join-Path $tools $name) -Algorithm SHA256).Hash -ne $verified[$name])" +
                "{throw ($name+' does not match this DisplayWeave Prototype build.')}}}" +
                "catch{New-Item -ItemType Directory -Path $display -Force|Out-Null;@{error=$_.Exception.Message}|ConvertTo-Json|" +
                "Set-Content -LiteralPath (Join-Path $display 'fatal.json');exit 1};" +
                "& (Join-Path $tools 'test-virtual-display.ps1') -DriverPackageDirectory (Join-Path $tools 'driver') -BuildDirectory $tools -Seconds " +
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -UseExistingPinnedPackage -SelectedAppSession -ReportDirectory $display");
            if (script.ToString().IndexOf('"') >= 0) throw new ArgumentException("A path contains a character the elevated command cannot carry.");
            return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"";
        }

        // PowerShell single-quoted literal; it also treats typographic single quotes as quotes.
        private static string Quote(string text)
        {
            var result = new StringBuilder("'");
            foreach (char c in text) { if (c == '\'' || (c >= '\u2018' && c <= '\u201B')) result.Append(c); result.Append(c); }
            return result.Append('\'').ToString();
        }
    }

    // One bounded session: elevated temporary display, one window move,
    // projection (optionally modal input), coordinated release, restoration.
    public sealed class SessionWorker
    {
        private readonly string directory;
        private readonly Dictionary<string, object> status = new Dictionary<string, object>();
        private readonly List<string> problems = new List<string>();
        private SessionRequest request;
        private WindowSnapshot snapshot;
        private bool windowMoved, windowRestored, windowLeftVirtualOutput, appClosed;
        private string virtualDevice;

        public SessionWorker(string sessionDirectory) { directory = Path.GetFullPath(sessionDirectory); }

        private string File_(string name) { return Path.Combine(directory, name); }
        private bool StopRequested { get { return File.Exists(File_("stop.request")); } }

        private void Publish(string state, string message, int remaining = 0)
        {
            status["schemaVersion"] = 1; status["sessionId"] = request == null ? Path.GetFileName(directory) : request.SessionId;
            status["state"] = state; status["message"] = message; status["remainingSeconds"] = remaining;
            status["terminal"] = Policy.Terminal(state); status["workerPid"] = Process.GetCurrentProcess().Id;
            status["updatedUtc"] = DateTime.UtcNow.ToString("o");
            if (!status.ContainsKey("cleanupComplete")) status["cleanupComplete"] = false;
            // Best effort: a status write that keeps colliding with a reader must
            // never abort the session or its cleanup. The next publish retries.
            try { JsonFile.Write(File_("status.json"), status); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public int Run()
        {
            // Only folders under this user's session root are accepted.
            string root = Path.GetFullPath(Paths.Sessions).TrimEnd('\\') + "\\";
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(directory)) return 2;
            Process elevated = null; IntPtr leaseHandle = IntPtr.Zero, begin = IntPtr.Zero, complete = IntPtr.Zero, ack = IntPtr.Zero; Process presenter = null;
            Dictionary<string, object> presenterReport = null;
            bool ackObserved = false, completeSent = false;
            try
            {
                request = JsonFile.Read<SessionRequest>(File_("request.json"));
                string invalid = request.Validate(Path.GetFileName(directory));
                if (invalid != null) throw new InvalidOperationException(invalid);
                Publish("starting", "Checking the selected app and display.");
                string missing = Paths.MissingComponent();
                if (missing != null) throw new InvalidOperationException("A required component is missing: " + missing);
                Process[] legacy = Process.GetProcessesByName("DisplayWeave");
                foreach (Process process in legacy) process.Dispose();
                if (legacy.Length != 0)
                    throw new InvalidOperationException("Exit the legacy DisplayWeave app from its tray menu first; its hooks must not touch this session.");
                IntPtr hwnd = new IntPtr(Int64.Parse(request.SourceHwnd));
                snapshot = Win32.Capture(hwnd);
                if (snapshot.ProcessId != request.SourcePid || snapshot.ProcessCreatedUtcFileTime != request.SourceStartedUtcFileTime ||
                    !String.Equals(snapshot.Executable, request.SourceExecutable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected app closed or changed. Refresh the list and choose it again.");

                Publish("awaitingApproval", "Approve the Windows administrator prompt to create the temporary display.");
                string reports = ElevatedFixture.Reports(request.SessionId);
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = ElevatedFixture.Arguments(Paths.Base, ElevatedFixture.ProtectedRoot, request.SessionId, Policy.LeaseSeconds, ElevatedFixture.Verified),
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                try { elevated = Process.Start(start); }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 1223) throw new InvalidOperationException("Administrator approval was declined. Nothing was changed.");
                    throw;
                }

                Publish("starting", "Creating the temporary virtual display.");
                string lease = Path.Combine(reports, "lease.json");
                DateTime readyDeadline = DateTime.UtcNow.AddSeconds(150);
                while (!File.Exists(lease + ".ready.json"))
                {
                    if (elevated.HasExited) throw new InvalidOperationException("The temporary display could not be created. " + FixtureFailure(reports));
                    if (DateTime.UtcNow > readyDeadline) throw new TimeoutException("The temporary display did not become ready.");
                    if (StopRequested) Publish("stopping", "Stopping: the display is still being created and will be released as soon as it is ready.");
                    Thread.Sleep(250);
                }
                Thread.Sleep(200); // The lease writes the ready journal once; let the write settle.
                var ready = JsonFile.ReadObject(lease + ".ready.json");
                int leasePid = (int)JsonFile.Number(ready, "pid");
                virtualDevice = JsonFile.Text(ready, "outputDevice");
                string beginName = JsonFile.Text(ready, "releaseProtocol", "beginEvent"), completeName = JsonFile.Text(ready, "releaseProtocol", "completeEvent"),
                    ackName = JsonFile.Text(ready, "releaseProtocol", "ackEvent");
                if (leasePid <= 0 || !JsonFile.Flag(ready, "ownershipVerified") || !JsonFile.Flag(ready, "physicalPreserved") || String.IsNullOrEmpty(virtualDevice) ||
                    JsonFile.Number(ready, "releaseProtocol", "maxDurationMs") != 5000 || !Policy.ValidEventName(beginName, "Begin") ||
                    !Policy.ValidEventName(completeName, "Complete") || !Policy.ValidEventName(ackName, "Ack") ||
                    !(JsonFile.Text(ready, "instanceId") ?? "").StartsWith("SWD\\DisplayWeaveProjection\\Fixture-" + leasePid + "-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The temporary display did not verify ownership, physical preservation and its release protocol.");
                // Keep the exact lease process object (opened before its identity
                // is checked) so a reused PID can never stand in for it.
                leaseHandle = Win32.OpenForWait((uint)leasePid);
                long leaseCreated; string leasePath;
                if (!Win32.ProcessIdentity((uint)leasePid, out leaseCreated, out leasePath) || Win32.Exited(leaseHandle) ||
                    !String.Equals(Path.GetFullPath(leasePath), Path.GetFullPath(Paths.Lease), StringComparison.OrdinalIgnoreCase) ||
                    DateTime.FromFileTimeUtc(leaseCreated) > File.GetLastWriteTimeUtc(lease + ".ready.json"))
                    throw new InvalidOperationException("The ready journal does not belong to this session's display lease.");
                begin = Win32.OpenReleaseEvent(beginName, true);
                complete = Win32.OpenReleaseEvent(completeName, true);
                ack = Win32.OpenReleaseEvent(ackName, false);
                double remaining = (DateTime.FromFileTimeUtc(leaseCreated).AddSeconds(JsonFile.Number(ready, "seconds")) - DateTime.UtcNow).TotalSeconds;
                int seconds = Policy.EffectiveSeconds(request.DurationSeconds, remaining);
                if (seconds == 0) throw new InvalidOperationException("Display setup took too long; not enough lease time remains for a session.");
                if (StopRequested) throw new OperationCanceledException("Stopped before projection started.");

                Rectangle virtualBounds; bool sdr;
                List<Rectangle> physical = ReadOutputs(out virtualBounds, out sdr);
                if (!sdr) throw new InvalidOperationException("The virtual display is not SDR; this prototype projects SDR only.");
                string regionProblem = Policy.ValidateRegion(request.RegionRectangle, physical, virtualBounds);
                if (regionProblem != null) throw new InvalidOperationException(regionProblem);

                if (!Win32.SameWindow(snapshot)) throw new InvalidOperationException("The selected app closed or changed before it could be moved.");
                // Recorded before any change so recovery always knows to restore the frame.
                snapshot.BorderlessApplied = request.Borderless && Win32.HasFrame(snapshot);
                JsonFile.Write(File_("window.json"), snapshot);
                status["recoveryNeeded"] = true; // Cleared after verified restoration.
                Publish("running", "Moving the app to the virtual display.");
                windowMoved = true;
                Win32.MoveToOutput(snapshot, virtualBounds);
                if (!String.Equals(Win32.MonitorDevice(hwnd), virtualDevice, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows did not move the app onto the virtual display (an administrator app cannot be moved by this prototype).");

                string report = File_("presenter.json");
                string arguments = "--source-device \"" + virtualDevice + "\" --destination " + RegionText(request.RegionRectangle) + " --seconds " + seconds + " --report \"" + report + "\"";
                if (request.Interactive)
                {
                    arguments += " --interactive-source-hwnd " + request.SourceHwnd + " --interactive-source-pid " + request.SourcePid + " --interactive-click-to-enter" +
                        " --lease-release-begin-event " + beginName + " --lease-release-ack-event " + ackName;
                    if (!String.IsNullOrEmpty(request.ReturnHwnd)) arguments += " --interactive-return-hwnd " + request.ReturnHwnd;
                }
                presenter = Process.Start(new ProcessStartInfo { FileName = Paths.Presenter, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true });
                DateTime started = DateTime.UtcNow, hardStop = started.AddSeconds(seconds + 10);
                bool closeRequested = false;
                while (!presenter.WaitForExit(250))
                {
                    // Click-to-enter needs the client to cover the virtual output exactly.
                    string hint = !request.Interactive ? "Running (view only)."
                        : Win32.ClientMatches(hwnd, virtualBounds) ? "Running. Click the region to control the app. Ctrl+Alt+F10 ends the session."
                        : "Running, but the app does not fill the virtual display, so control cannot start. Use \"Remove the window frame\" or the app's own fullscreen setting.";
                    int left = Math.Max(0, seconds - (int)(DateTime.UtcNow - started).TotalSeconds);
                    Publish("running", hint, left);
                    if (Win32.Exited(leaseHandle)) problems.Add("The temporary display ended before the session finished.");
                    if (!appClosed && !Win32.SameWindow(snapshot)) appClosed = true; // The user closed the app: end early.
                    if (!closeRequested && (StopRequested || appClosed || Win32.Exited(leaseHandle) || DateTime.UtcNow > hardStop))
                    {
                        IntPtr preview = Win32.FindProcessWindow(presenter.Id, "DisplayWeaveProjectionPreview");
                        if (preview != IntPtr.Zero) Win32.RequestClose(preview);
                        closeRequested = true;
                        Publish("stopping", "Stopping the session.");
                    }
                    if (closeRequested && DateTime.UtcNow > hardStop.AddSeconds(5)) { presenter.Kill(); problems.Add("The presenter did not stop and was terminated."); }
                }
                if (File.Exists(report)) presenterReport = JsonFile.ReadObject(report);
                if (presenter.ExitCode != 0) problems.Add("Projection ended with an error: " + (presenterReport == null ? "no report" : JsonFile.Text(presenterReport, "reason")));
                if (presenterReport != null && JsonFile.Flag(presenterReport, "input", "entered")) status["inputEntered"] = true;
            }
            catch (Exception error)
            {
                problems.Add(error.Message);
            }
            finally
            {
                try
                {
                    Publish("cleaning", "Releasing the virtual display and restoring the app.");
                    if (presenter != null && !presenter.HasExited)
                    {
                        IntPtr preview = Win32.FindProcessWindow(presenter.Id, "DisplayWeaveProjectionPreview");
                        if (preview != IntPtr.Zero) Win32.RequestClose(preview);
                        if (!presenter.WaitForExit(3000)) { presenter.Kill(); presenter.WaitForExit(3000); }
                        if (File.Exists(File_("presenter.json"))) presenterReport = JsonFile.ReadObject(File_("presenter.json"));
                    }
                    if (begin != IntPtr.Zero)
                    {
                        // The presenter or its guardian claims BEGIN when an input
                        // session ends. Otherwise this worker sends the only BEGIN.
                        bool claimed = presenterReport != null && JsonFile.Flag(presenterReport, "input", "leaseReleaseStarted");
                        // A presenter that died after entry leaves BEGIN to its guardian
                        // (heartbeat timeout 2.5 s); a second BEGIN would be rejected.
                        if (!claimed && presenterReport == null && File.Exists(File_("presenter.json.active.ready.json"))) Win32.Signaled(ack, 3500);
                        if (!claimed && !Win32.Signaled(ack, 0)) Win32.Signal(begin);
                        ackObserved = Win32.Signaled(ack, 2000);
                        if (!ackObserved) problems.Add("The display lease did not acknowledge the release request.");
                    }
                    RestoreWindow();
                    bool inputClean = presenterReport == null || !JsonFile.Flag(presenterReport, "input", "entered") ||
                        (!JsonFile.Flag(presenterReport, "input", "active") && !JsonFile.Flag(presenterReport, "input", "releaseIncomplete"));
                    if (!inputClean) problems.Add("Keyboard/mouse restoration was incomplete; see presenter.json.");
                    if (ackObserved && (presenter == null || presenter.HasExited) && inputClean && (windowLeftVirtualOutput || !windowMoved))
                    {
                        Win32.Signal(complete); completeSent = true;
                    }
                    else if (ackObserved) problems.Add("Release was not completed because cleanup could not be confirmed.");
                    if (elevated != null)
                    {
                        Publish("cleaning", "Removing the temporary display.");
                        if (!elevated.WaitForExit(180000)) problems.Add("The display cleanup helper is still running.");
                    }
                    var fixture = elevated == null ? null : ReadFixtureResult(ElevatedFixture.Reports(request.SessionId));
                    bool displayClean = elevated == null || (fixture != null && JsonFile.Flag(fixture, "deviceRemoved") &&
                        JsonFile.Flag(fixture, "configurationRemoved") && JsonFile.Flag(fixture, "physicalRestored"));
                    if (elevated != null && fixture == null) problems.Add("No display cleanup result was written.");
                    else if (fixture != null && !JsonFile.Flag(fixture, "success")) problems.Add("Display fixture: " + (JsonFile.Text(fixture, "failure") ?? "cleanup reported a problem") + ".");
                    status["releaseCompleteSent"] = completeSent;
                    status["windowRestored"] = windowRestored;
                    status["displayCleanupComplete"] = displayClean;
                    status["cleanupComplete"] = displayClean && (!windowMoved || windowRestored || windowLeftVirtualOutput);
                    status["recoveryNeeded"] = windowMoved && !windowRestored && !windowLeftVirtualOutput;
                    status["problems"] = problems.Distinct().ToArray();
                    status["appClosed"] = appClosed;
                    if (problems.Count == 0) Publish("completed", appClosed
                        ? "Session finished. The app was closed during the session; the temporary display was removed."
                        : "Session finished. The app was restored and the temporary display was removed.");
                    else Publish("failed", String.Join(" ", problems.Distinct()));
                }
                catch (Exception error)
                {
                    status["recoveryNeeded"] = windowMoved && !windowRestored;
                    Publish("failed", "Cleanup failed: " + error.Message);
                }
                foreach (IntPtr handle in new[] { begin, complete, ack, leaseHandle }) Win32.Close(handle);
            }
            return problems.Count == 0 ? 0 : 1;
        }

        private void RestoreWindow()
        {
            if (!windowMoved || snapshot == null) return;
            if (!Win32.SameWindow(snapshot)) { appClosed = windowRestored = windowLeftVirtualOutput = true; return; } // App closed: nothing of ours remains.
            IntPtr hwnd = new IntPtr(Int64.Parse(snapshot.Hwnd));
            for (int attempt = 0; attempt < 2 && !windowRestored; ++attempt)
            {
                if (attempt > 0) Thread.Sleep(300); // An exclusive app may finish leaving fullscreen first.
                windowRestored = Win32.Restore(snapshot);
            }
            windowLeftVirtualOutput = !String.Equals(Win32.MonitorDevice(hwnd), virtualDevice, StringComparison.OrdinalIgnoreCase);
            if (!windowRestored) problems.Add(windowLeftVirtualOutput
                ? "The app left the virtual display but its exact original placement could not be confirmed."
                : "The app could not be moved back from the virtual display.");
        }

        // Recovery for a worker that ended without restoring the window.
        public static string Recover(string sessionDirectory)
        {
            string path = Path.Combine(sessionDirectory, "window.json");
            if (!File.Exists(path)) return "This session never moved a window.";
            var saved = JsonFile.Read<WindowSnapshot>(path);
            if (!Win32.SameWindow(saved)) return "The original app window no longer exists; nothing to restore.";
            bool restored = Win32.Restore(saved);
            if (restored)
            {
                var current = File.Exists(Path.Combine(sessionDirectory, "status.json")) ? JsonFile.ReadObject(Path.Combine(sessionDirectory, "status.json")) : new Dictionary<string, object>();
                current["recoveryNeeded"] = false; current["terminal"] = true;
                if (!Policy.Terminal(JsonFile.Text(current, "state"))) { current["state"] = "failed"; current["message"] = "The worker ended early; the window was restored manually."; }
                JsonFile.Write(Path.Combine(sessionDirectory, "status.json"), current);
            }
            return restored ? "The window's original position and frame were restored." : "Windows did not accept the saved placement; move the window manually.";
        }

        private List<Rectangle> ReadOutputs(out Rectangle virtualBounds, out bool sdr)
        {
            var start = new ProcessStartInfo { FileName = Paths.Presenter, Arguments = "--list-outputs", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            string text;
            using (Process list = Process.Start(start)) { text = list.StandardOutput.ReadToEnd(); list.WaitForExit(); if (list.ExitCode != 0) throw new InvalidOperationException("Display discovery failed."); }
            var outputs = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text)["outputs"] as ArrayList;
            var physical = new List<Rectangle>(); virtualBounds = Rectangle.Empty; sdr = false; int matches = 0;
            foreach (Dictionary<string, object> output in outputs)
            {
                if (!JsonFile.Flag(output, "attached")) continue;
                var bounds = new Rectangle((int)JsonFile.Number(output, "bounds", "x"), (int)JsonFile.Number(output, "bounds", "y"),
                    (int)JsonFile.Number(output, "bounds", "width"), (int)JsonFile.Number(output, "bounds", "height"));
                if (String.Equals(JsonFile.Text(output, "device"), virtualDevice, StringComparison.OrdinalIgnoreCase))
                {
                    virtualBounds = bounds; sdr = JsonFile.Flag(output, "colorSpaceKnown") && JsonFile.Flag(output, "sdr"); ++matches;
                }
                else physical.Add(bounds);
            }
            if (matches != 1 || virtualBounds.Width <= 0) throw new InvalidOperationException("The virtual display is not uniquely attached.");
            return physical;
        }

        private static Dictionary<string, object> ReadFixtureResult(string reports)
        {
            string path = Path.Combine(reports, "result.json");
            try { return File.Exists(path) ? JsonFile.ReadObject(path) : null; } catch (ArgumentException) { return null; }
        }

        private static string FixtureFailure(string reports)
        {
            var result = ReadFixtureResult(reports);
            string fatal = Path.Combine(reports, "fatal.json");
            if (result != null && JsonFile.Text(result, "failure") != null) return JsonFile.Text(result, "failure");
            if (File.Exists(fatal)) return JsonFile.Text(JsonFile.ReadObject(fatal), "error");
            return "See " + reports + ".";
        }

        private static string RegionText(Rectangle r) { return r.X + "," + r.Y + "," + r.Width + "," + r.Height; }
    }
}
