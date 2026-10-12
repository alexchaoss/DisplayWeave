using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DisplayWeave
{
    public static partial class RuleTests
    {
        // The native helper's readiness line and how its failures are classified and worded.
        private static void DiagnosticTests(Action<string, Action> test)
        {
            test("Incompatible hook failures identify the app and require restart rather than Force retry", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", ProcessId = 572,
                    CreationTime = 100, Handle = new IntPtr(50) };
                string native = "ERROR Restart the target application to use this DisplayWeave version (Windows error 1306)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureRequiresRestart(native), "Protocol mismatch was not classified as restart-required.");
                Assert(message.Contains("Editor.exe (PID 572)") && message.Contains("Windows error 1306"), "Failure lost application identity or native detail.");
                Assert(message.Contains("Close and reopen") && !message.Contains("Force fullscreen"), "Restart-required failure suggests a futile Force retry.");
                Assert(message.Contains("cannot safely reconnect to this window") && !message.Contains("cannot be upgraded") &&
                    !message.Contains("incompatible with this DisplayWeave version"), "Restart guidance replaced the native reason with an unsupported version assumption.");
                Assert(FailureMessages.FailureRequiresRestart("ERROR Failed (WINDOWS ERROR 1306)"), "Revision error code alone was not recognized.");
                Assert(!FailureMessages.FailureRequiresRestart("Windows error 13060"), "A different error code was classified as revision mismatch.");
            });
            test("Readiness accepts normal or reused hooks only for the intended process", delegate
            {
                bool reused;
                Assert(HelperProtocol.TryParseReady("READY 572", 572, out reused) && !reused, "Normal readiness was rejected or reported as reused.");
                Assert(HelperProtocol.TryParseReady("READY 572 REUSED", 572, out reused) && reused, "Compatible resident hooks were not recognized.");
                Assert(!HelperProtocol.TryParseReady("READY 573 REUSED", 572, out reused), "Another process's acknowledgement was accepted.");
                Assert(!HelperProtocol.TryParseReady("READY REUSED", 572, out reused), "Missing process identity was accepted.");
                Assert(!HelperProtocol.TryParseReady("READY 0", 0, out reused), "An empty process identity was accepted.");
                Assert(!HelperProtocol.TryParseReady("READY -572 REUSED", 572, out reused), "Invalid process identity was accepted.");
                Assert(!HelperProtocol.TryParseReady("READY 572 REUSED EXTRA", 572, out reused), "Unknown readiness fields were accepted.");
                Assert(!HelperProtocol.TryParseReady("READY 572 INCOMPATIBLE", 572, out reused), "Unrecognized resident state was accepted.");
            });
            test("Readiness distinguishes virtual monitor and legacy capabilities", delegate
            {
                bool reused;
                uint capabilities;
                Assert(HelperProtocol.TryParseReady("READY 572 CAPS 5", 572, out reused, out capabilities) && !reused && capabilities == 5,
                    "Virtual monitor capabilities were rejected.");
                Assert(HelperProtocol.TryParseReady("READY 572 REUSED CAPS 3", 572, out reused, out capabilities) && reused && capabilities == 3,
                    "Legacy resident capabilities were rejected.");
                Assert(HelperProtocol.TryParseReady("READY 572 REUSED", 572, out reused, out capabilities) && reused && capabilities == 0,
                    "Legacy helpers without capability details lost compatibility.");
                string[] malformed = { "READY 572 CAPS", "READY 572 CAPS 1", "READY 572 CAPS 4", "READY 572 CAPS 0", "READY 572 CAPS -5",
                    "READY 572 CAPS 5 EXTRA", "READY 572 REUSED CAPS 5 EXTRA", "READY 572 CAPS 5 REUSED", "READY 572 CAPS 3 CAPS 5" };
                foreach (string message in malformed)
                    Assert(!HelperProtocol.TryParseReady(message, 572, out reused, out capabilities), "Invalid capabilities were accepted: " + message);
            });
            test("Compatible resident readiness reports reconnection without claiming a DLL upgrade", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Example.exe", ProcessId = 572 };
                string reconnected = HelperProtocol.FormatReady(target, true, true, true);
                Assert(reconnected.Contains("Reconnected") && reconnected.Contains("Example.exe") && reconnected.Contains("existing compatible hooks"), "Reconnection did not explain the successful takeover.");
                Assert(reconnected.IndexOf("restart", StringComparison.OrdinalIgnoreCase) < 0 && reconnected.IndexOf("upgraded", StringComparison.OrdinalIgnoreCase) < 0,
                    "Compatible reuse incorrectly requires a restart or claims the resident DLL was upgraded.");
                Assert(HelperProtocol.FormatReady(target, false, false, true).Contains("control is paused"), "Late reused readiness lost pause state.");
                Assert(HelperProtocol.FormatReady(target, true, true, false, 5) == "Automatic virtual monitor and window handling ready for Example.exe.", "Automatic readiness did not describe virtual monitor handling.");
                Assert(HelperProtocol.FormatReady(target, false, true, false, 5) == "Virtual monitor and window handling ready for Example.exe.", "Manual readiness did not describe virtual monitor handling.");
                Assert(HelperProtocol.FormatReady(target, false, true, true, 3).Contains("legacy fullscreen engine"), "Resident legacy capabilities were presented as the current engine.");
            });
            test("Ordinary helper failures retain their reason and offer normal retry", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Example.exe", ProcessId = 90 };
                string message = FailureMessages.FormatFailure(target, "ERROR Requested operation is unavailable (Windows error 50)");
                Assert(message.Contains("Example.exe (PID 90)") && message.Contains("Windows error 50"), "Failure lost its identity or reason.");
                Assert(message.Contains("Force fullscreen") && !FailureMessages.FailureRequiresRestart(message), "Ordinary failure got incorrect restart guidance.");
                Assert(!FailureMessages.FailureIsAccessDenied(message) && !FailureMessages.FailureRequiresElevation(message), "An unrelated error received permission guidance.");
                Assert(!message.Contains(": ERROR "), "Raw protocol prefix leaked into the user message.");
            });
            test("Confirmed elevation mismatch directs the user to administrator restart", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 17752 };
                string native = "ERROR Target application is elevated but DisplayWeave is not. Restart DisplayWeave as administrator (Windows error 740)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureRequiresElevation(native), "Confirmed privilege mismatch was not recognized.");
                Assert(message.Contains("Game.exe (PID 17752)") && message.Contains("Target application is elevated") && message.Contains("Windows error 740"), "Mismatch guidance lost the application or native evidence.");
                Assert(message.Contains("Status & help > Restart as administrator") && message.Contains("Windows prompt"), "Mismatch guidance does not identify the available administrator action.");
                Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("Close and reopen this application"), "Mismatch suggests a retry or restarting the target instead of DisplayWeave.");
                Assert(FailureMessages.FailureRequiresElevation("ERROR Failed (WINDOWS ERROR 740)"), "Elevation error code alone was not recognized.");
                Assert(FailureMessages.FailureRequiresElevation("ERROR Restart DisplayWeave as administrator before retrying"), "Native elevation instruction alone was not recognized.");
                Assert(!FailureMessages.FailureRequiresElevation("Windows error 7400") &&
                    !FailureMessages.FailureRequiresElevation("Windows error 740x") &&
                    !FailureMessages.FailureRequiresElevation("Windows error 5") &&
                    !FailureMessages.FailureRequiresElevation(null), "Unknown or unrelated errors were reported as confirmed elevation mismatches.");
            });
            test("Access denial preserves its stage without assuming a privilege mismatch", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Example.exe", ProcessId = 90 };
                string native = "ERROR MODULE_INSPECTION_DENIED FindResidentHooks failed (Windows error 5)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureIsAccessDenied(native), "Access-denied error code was not recognized.");
                Assert(message.Contains("Example.exe (PID 90)") && message.Contains("MODULE_INSPECTION_DENIED") && message.Contains("FindResidentHooks") && message.Contains("Windows error 5"), "Access guidance lost the application or inspection stage.");
                Assert(message.Contains("Windows denied access") && message.Contains("does not identify the cause"), "Access denial was not explained with its diagnostic limit.");
                Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator") &&
                    !FailureMessages.FailureRequiresElevation(message), "Access denial alone suggests elevation or a futile retry.");
                Assert(!FailureMessages.FailureIsAccessDenied("Windows error 50") &&
                    !FailureMessages.FailureIsAccessDenied("Windows error 500") &&
                    !FailureMessages.FailureIsAccessDenied("Windows error 5x") &&
                    !FailureMessages.FailureIsAccessDenied(null), "Access-denied detection matched a different error or missing detail.");
            });
            test("Unobserved loader module preserves observations without inventing a Windows reason", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 90 };
                string native = "ERROR LOADER_MODULE_NOT_OBSERVED: Fullscreen component was not observed after the loader thread ended; PID 90, thread result DWORD 0x00000000, target state running, RegionHook.dll copies 0; remote loader error unavailable. Initialization was not invoked (DisplayWeave status 0x20005201)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureIsLoaderModuleNotObserved(native), "Explicit loader category was not recognized.");
                Assert(message.Contains("Game.exe (PID 90)") && message.Contains("LOADER_MODULE_NOT_OBSERVED") &&
                    message.Contains("thread result DWORD 0x00000000") && message.Contains("target state running") &&
                    message.Contains("RegionHook.dll copies 0") && message.Contains("DisplayWeave status 0x20005201"),
                    "Loader diagnostic lost its application, raw result, process state, or application-specific status.");
                Assert(message.Contains("Fullscreen component did not start") && message.Contains("detailed Windows failure reason is unavailable"),
                    "Missing component was not explained with its diagnostic limit.");
                Assert(!message.Contains("Windows error") && !message.Contains("Force fullscreen") && !message.Contains("Resume handling") &&
                    !message.Contains("administrator") && !message.Contains("Close and reopen") && !message.Contains("responsive"),
                    "Unobserved module invented a Windows error or suggested an unsupported retry, elevation, or restart.");
                Assert(!FailureMessages.FailureIsAccessDenied(message) && !FailureMessages.FailureRequiresElevation(message) &&
                    !FailureMessages.FailureRequiresRestart(message) && !FailureMessages.FailureIsInitializationTimeout(message),
                    "Unknown loader reason was misclassified as a confirmed failure cause.");
            });
            test("Loader diagnostic categories require complete tokens without inferring from error numbers", delegate
            {
                Assert(FailureMessages.FailureIsLoaderModuleNotObserved("error loader_module_not_observed: unavailable"), "Category matching is unexpectedly case sensitive.");
                Assert(FailureMessages.FailureIsLoaderModuleNotObserved("LOADER_MODULE_NOT_OBSERVED"), "Standalone category was not recognized.");
                string[] unrelated = { null, "", "LOADER_MODULE_NOT_OBSERVED_OLD", "OTHER_LOADER_MODULE_NOT_OBSERVED", "XLOADER_MODULE_NOT_OBSERVED",
                    "LOADER_MODULE_NOT_OBSERVED2", "LOADER_MODULE_NOT_OBSERVEDx", "LOADER_MODULE_NOT_OBSERVEDé",
                    "ERROR The module was not observed", "ERROR LoadLibrary failed (Windows error 1114)", "ERROR thread result DWORD 0x0000045A" };
                foreach (string detail in unrelated)
                    Assert(!FailureMessages.FailureIsLoaderModuleNotObserved(detail), "A different category or raw error was treated as unknown loader evidence: " + detail);
                string known = FailureMessages.FormatFailure(null, "ERROR DLL initialization failed (Windows error 1114)");
                Assert(known.Contains("Windows error 1114") && !known.Contains("detailed Windows failure reason is unavailable"),
                    "An actual supplied Windows loader error was replaced with an unknown cause.");
            });
            test("Specific loader observations survive generic helper exit and timeout messages", delegate
            {
                string loader = "ERROR LOADER_MODULE_NOT_OBSERVED: thread result DWORD 0x00000000; target state running; remote loader error unavailable";
                string generic = "Native helper exited before the application closed.";
                string timeout = "ERROR Native startup timed out (Windows error 1460)";
                Assert(FailureMessages.PreferFailure(generic, loader) == loader && FailureMessages.PreferFailure(loader, generic) == loader,
                    "Generic helper exit erased the loader observation.");
                Assert(FailureMessages.PreferFailure(timeout, loader) == loader && FailureMessages.PreferFailure(loader, timeout) == loader,
                    "Generic startup timeout erased the more specific loader stage.");
                Assert(FailureMessages.PreferFailure(loader, "ERROR MODULE_INSPECTION_DENIED (Windows error 5)").Contains("MODULE_INSPECTION_DENIED"),
                    "A real inspection access failure lost precedence to an unknown loader cause.");
            });
            test("Startup timeout preserves the native stage without inventing an access failure", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Game.exe", ProcessId = 9192 };
                string native = "ERROR Native fullscreen startup timed out: no bootstrap callback acknowledged (Windows error 1460)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(FailureMessages.FailureIsInitializationTimeout(native), "Startup timeout was not recognized.");
                Assert(message.Contains("Game.exe (PID 9192)") && message.Contains("no bootstrap callback acknowledged") && message.Contains("Windows error 1460"), "Timeout guidance lost its application or native stage.");
                Assert(message.Contains("did not finish starting") && message.Contains("window open and responsive"), "Timeout guidance does not explain unfinished startup or window responsiveness.");
                Assert(!message.Contains("Force fullscreen") && !message.Contains("Resume handling") && !message.Contains("administrator") &&
                    !message.Contains("protected") && !message.Contains("incompatible") && !message.Contains("Close and reopen"), "Timeout guidance invented a cause or requires an unsupported restart.");
                Assert(!FailureMessages.FailureRequiresElevation(message) && !FailureMessages.FailureIsAccessDenied(message) &&
                    !FailureMessages.FailureRequiresRestart(message), "Initialization timeout was classified as a permission failure or restart requirement.");
                foreach (string stage in new string[] { "graphics initialization in progress", "waiting for the selected window callback", "waiting for fresh worker acknowledgment" })
                {
                    string staged = FailureMessages.FormatFailure(target, "ERROR Native fullscreen startup timed out: " + stage + " (Windows error 1460)");
                    Assert(staged.Contains(stage) && !staged.Contains("Force fullscreen") && !staged.Contains("administrator"), "An observed native startup stage was lost or misclassified: " + stage);
                }
                Assert(FailureMessages.FailureIsInitializationTimeout("ERROR Timeout (WINDOWS ERROR 1460)"), "Timeout code matching is unexpectedly case sensitive.");
                Assert(!FailureMessages.FailureIsInitializationTimeout("Windows error 14600") &&
                    !FailureMessages.FailureIsInitializationTimeout("Windows error 1460x") &&
                    !FailureMessages.FailureIsInitializationTimeout("Windows error 740") &&
                    !FailureMessages.FailureIsInitializationTimeout("Windows error 5") &&
                    !FailureMessages.FailureIsInitializationTimeout(null), "Timeout classification matched a different error or missing detail.");
            });
            test("Explicit native restart instructions remain authoritative for a timeout", delegate
            {
                ApplicationIdentity target = new ApplicationIdentity { ExecutablePath = @"C:\Games\Example.exe", ProcessId = 90 };
                string native = "ERROR Restart the target application; resident startup cannot continue (Windows error 1460)";
                string message = FailureMessages.FormatFailure(target, native);
                Assert(message.Contains("Close and reopen") && !message.Contains("check the reported startup stage"), "Generic timeout advice overrode an explicit native restart requirement.");
                string staged = "ERROR Native fullscreen startup timed out: graphics initialization in progress (Windows error 1460)";
                string generic = "Native helper exited before the application closed.";
                Assert(FailureMessages.PreferFailure(generic, staged) == staged && FailureMessages.PreferFailure(staged, generic) == staged,
                    "Generic helper exit erased the startup stage.");
                Assert(FailureMessages.PreferFailure(staged, native) == native, "Explicit native restart instruction lost to a timeout.");
            });
            test("Confirmed permission diagnostics survive less specific helper output", delegate
            {
                string elevation = "ERROR Restart DisplayWeave as administrator (Windows error 740)";
                string denied = "ERROR MODULE_INSPECTION_DENIED (Windows error 5)";
                string generic = "Native helper exited before the application closed.";
                Assert(FailureMessages.PreferFailure(denied, elevation) == elevation, "Confirmed mismatch did not replace generic access denial.");
                Assert(FailureMessages.PreferFailure(elevation, denied) == elevation &&
                    FailureMessages.PreferFailure(elevation, generic) == elevation, "Later output erased the confirmed mismatch.");
                Assert(FailureMessages.PreferFailure(generic, denied) == denied &&
                    FailureMessages.PreferFailure(denied, generic) == denied, "Generic exit erased the access-denied reason.");
                string restart = "ERROR Restart the target application (Windows error 1306)";
                Assert(FailureMessages.PreferFailure(elevation, restart) == restart, "Existing target restart precedence changed.");
            });
            test("Specific helper diagnostics win over generic exit messages", delegate
            {
                string specific = "ERROR Restart the target application to use this DisplayWeave version (Windows error 1306)";
                string generic = "Native helper exited before the application closed.";
                Assert(FailureMessages.PreferFailure(generic, specific) == specific, "Late native output was discarded.");
                Assert(FailureMessages.PreferFailure(specific, generic) == specific, "Generic exit erased the restart reason.");
                Assert(FailureMessages.PreferFailure("ERROR Cannot map application controls", specific) == specific, "Restart reason lost to a less specific error.");
                Assert(FailureMessages.PreferFailure(generic, "ERROR Access denied") == "ERROR Access denied", "Generic exit hid a native access error.");
            });
            test("Per-app failure records survive unrelated readiness and clear only the relevant identity", delegate
            {
                FullscreenFailureHistory history = new FullscreenFailureHistory();
                ApplicationIdentity first = new ApplicationIdentity { ProcessId = 10, CreationTime = 100, Handle = new IntPtr(1) };
                ApplicationIdentity second = new ApplicationIdentity { ProcessId = 20, CreationTime = 200, Handle = new IntPtr(2) };
                ApplicationIdentity anotherWindow = new ApplicationIdentity { ProcessId = 20, CreationTime = 200, Handle = new IntPtr(3) };
                ApplicationIdentity reusedPid = new ApplicationIdentity { ProcessId = 20, CreationTime = 300, Handle = new IntPtr(2) };
                history.Record(first, "First app failure"); history.Record(second, "Second app failure");
                history.ClearTarget(reusedPid);
                Assert(history.Latest == "Second app failure" && history.For(first) == "First app failure", "Unrelated readiness erased a failure.");
                history.ClearTarget(second);
                Assert(history.Latest == "First app failure", "Clearing the newest failure hid another unresolved failure.");
                history.Record(second, "Second app failed again"); history.Record(anotherWindow, "Other window failed");
                history.ClearProcess(reusedPid);
                Assert(history.Count == 3, "PID reuse cleared an unrelated process lifetime.");
                history.ClearProcess(second);
                Assert(history.Count == 1 && history.Latest == "First app failure", "Explicit release failed to clear this process's windows.");
                history.ClearTarget(first);
                Assert(history.Latest == String.Empty && history.Count == 0, "Destroyed target did not clear its warning.");
            });
        }
    }
}
