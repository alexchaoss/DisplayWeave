using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DisplayWeave.Prototype
{
    // Pure checks: no windows, processes, displays or input are touched.
    public static class PrototypeTests
    {
        public static int Run(string reportPath)
        {
            var lines = new List<string>();
            int passed = 0, failed = 0;
            Action<bool, string> check = (condition, name) => { lines.Add((condition ? "PASS " : "FAIL ") + name); if (condition) ++passed; else ++failed; };

            Func<SessionRequest> valid = () => new SessionRequest
            {
                SchemaVersion = 1, SessionId = "abc", Mode = "interactive", DurationSeconds = 90, Borderless = true,
                SourceHwnd = "4660", SourcePid = 1234, SourceStartedUtcFileTime = "133000000000000000", SourceExecutable = @"C:\Games\game.exe",
                ReturnHwnd = "22136", Region = new[] { 0, 0, 2560, 1440 }
            };
            check(valid().Validate("abc") == null, "complete request accepted");
            var r = valid(); check(r.Validate("other") != null, "request bound to its session folder");
            r = valid(); r.DurationSeconds = 120; check(r.Validate("abc") != null, "duration outside 30/60/90 rejected");
            r = valid(); r.Mode = "fullscreen"; check(r.Validate("abc") != null, "unknown mode rejected");
            r = valid(); r.SourceHwnd = "0x1234"; check(r.Validate("abc") != null, "handles are exact decimal strings");
            r = valid(); r.SourcePid = 4; check(r.Validate("abc") != null, "system process rejected");
            r = valid(); r.SourceExecutable = "game.exe"; check(r.Validate("abc") != null, "relative executable rejected");
            r = valid(); r.Region = new[] { 0, 0, 32, 720 }; check(r.Validate("abc") != null, "tiny region rejected");
            r = valid(); r.Region = new[] { 0, 0, 100 }; check(r.Validate("abc") != null, "malformed region rejected");
            r = valid(); r.ReturnHwnd = null; check(r.Validate("abc") == null, "return window is optional");
            r = valid(); r.SchemaVersion = 2; check(r.Validate("abc") != null, "future schema rejected");

            var copy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<SessionRequest>(JsonFile.Serialize(valid()));
            check(copy.Validate("abc") == null && copy.SourceHwnd == "4660" && copy.Region[2] == 2560 && copy.Interactive, "request JSON round trip keeps exact values");

            check(Policy.EffectiveSeconds(90, 118.9) == 90, "full requested duration when lease time allows");
            check(Policy.EffectiveSeconds(90, 100) == 85, "projection shortened to keep cleanup reserve");
            check(Policy.EffectiveSeconds(30, 34) == 0, "too little lease time refuses to start");
            check(Policy.EffectiveSeconds(30, 35) == 20, "minimum projection length is accepted");
            check(Policy.AllowedDuration(60) && !Policy.AllowedDuration(45), "only offered durations allowed");

            string begin = @"Local\DisplayWeaveProjectionRelease-42-0123456789abcdef0123456789abcdef-Begin";
            check(Policy.ValidEventName(begin, "Begin"), "lease event name accepted");
            check(!Policy.ValidEventName(begin, "Ack"), "lease event role enforced");
            check(!Policy.ValidEventName(@"Global\DisplayWeaveProjectionRelease-42-ab-Begin", "Begin"), "global event namespace rejected");
            check(!Policy.ValidEventName(@"Local\DisplayWeaveProjectionRelease-4 2-Begin", "Begin"), "unexpected characters rejected");
            check(!Policy.ValidEventName(null, "Begin"), "missing event rejected");

            var physical = new List<Rectangle> { new Rectangle(0, 0, 7680, 2160) };
            var virtualOutput = new Rectangle(7680, 0, 1280, 720);
            check(Policy.ValidateRegion(new Rectangle(0, 0, 2560, 2160), physical, virtualOutput) == null, "region inside the ultrawide accepted");
            check(Policy.ValidateRegion(new Rectangle(7000, 0, 1280, 720), physical, virtualOutput) != null, "region overlapping the virtual output rejected");
            check(Policy.ValidateRegion(new Rectangle(7000, 0, 600, 720), new List<Rectangle> { new Rectangle(0, 0, 7680, 2160), new Rectangle(-1920, 0, 1920, 1080) }, virtualOutput) == null, "region on one of several outputs accepted");
            check(Policy.ValidateRegion(new Rectangle(-100, 0, 600, 720), new List<Rectangle> { new Rectangle(0, 0, 7680, 2160), new Rectangle(-1920, 0, 1920, 1080) }, virtualOutput) != null, "region spanning two outputs rejected");

            check(Win32.ExcludedByName(@"C:\Windows\explorer.exe", "CabinetWClass"), "whole Explorer process excluded");
            check(Win32.ExcludedByName(@"C:\Games\game.exe", "Shell_TrayWnd"), "taskbar class excluded");
            check(Win32.ExcludedByName(@"C:\x\DisplayWeave.exe", "WindowsForms10"), "legacy DisplayWeave excluded");
            check(!Win32.ExcludedByName(@"C:\Games\game.exe", "GameWindow"), "ordinary app allowed");
            check(Win32.ExcludedByName(null, "GameWindow"), "unknown executable excluded");

            check(Win32.HasFrame(new WindowSnapshot { Style = "13565952", ExStyle = "256" }), "captioned overlapped window has a frame to remove");
            check(Win32.HasFrame(new WindowSnapshot { Style = "2147483648", ExStyle = "512" }), "popup with a client edge still needs edge removal");
            check(!Win32.HasFrame(new WindowSnapshot { Style = "2147483648", ExStyle = "0" }), "plain popup is already borderless");

            check(Policy.Terminal("completed") && Policy.Terminal("failed") && !Policy.Terminal("running") && !Policy.Terminal("cleaning"), "terminal states");

            string elevated = ElevatedFixture.Arguments(@"C:\Users\O'Neil\App\v1", @"C:\Program Files\DisplayWeavePrototype\sessions", "abc", 120, ElevatedFixture.Verified);
            check(elevated.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"", StringComparison.Ordinal) &&
                elevated.IndexOf('"', 64) == elevated.Length - 1, "elevated bootstrap is one quoted command argument");
            check(elevated.Contains("'C:\\Users\\O''Neil\\App\\v1'") && elevated.Contains("'C:\\Program Files\\DisplayWeavePrototype\\sessions\\abc'"), "bootstrap paths are escaped literals");
            check(elevated.Contains("'" + BuildHashes.Fixture + "'") && elevated.Contains("'" + BuildHashes.Lease + "'") && BuildHashes.Fixture.Length == 64 &&
                BuildHashes.Lease.Length == 64, "bootstrap pins the fixture and lease hashes of this build");
            check(elevated.Length < 2000, "bootstrap fits a ShellExecute command line");
            check(ElevatedFixture.Reports("abc").StartsWith(ElevatedFixture.ProtectedRoot + @"\abc\", StringComparison.Ordinal), "reports stay in the protected session folder");

            lines.Add(passed + " passed; " + failed + " failed.");
            File.WriteAllLines(reportPath, lines.ToArray(), new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }
    }
}
