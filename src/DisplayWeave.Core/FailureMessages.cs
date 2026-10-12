using System;
using System.Globalization;
using System.IO;

namespace DisplayWeave
{
    // Classifies helper and fit failures and words them for the user, with the advice that fits each kind.
    internal static class FailureMessages
    {
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

        internal static string FormatFailure(ApplicationIdentity target, string detail, bool ownGuidance = false)
        {
            string name = target == null ? "Application" : Path.GetFileName(target.ExecutablePath);
            if (String.IsNullOrWhiteSpace(name)) name = "Application";
            string clean = String.IsNullOrWhiteSpace(detail) ? "Fullscreen helper failed" : detail.Trim();
            while (clean.StartsWith("ERROR ", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(6).TrimStart();
            clean = clean.Replace('\r', ' ').Replace('\n', ' ').TrimEnd('.', ' ');
            string targetText = name + (target == null ? String.Empty : " (PID " + target.ProcessId + ")");
            string guidance = ownGuidance || FailureIsLauncherExcluded(clean) ? String.Empty : FailureRequiresRestart(clean)
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
    }
}
