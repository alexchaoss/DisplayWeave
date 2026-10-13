using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DisplayWeave
{
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DisplayWeave";
        public const int MaximumCommandLength = 260;

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (key == null) return false;
                string command = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                return IsUsableCommand(command);
            }
        }

        public static void SetEnabled(bool enabled, string executable, string dataDirectory)
        {
            if (!enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null) key.DeleteValue(ValueName, false);
                return;
            }
            // Validate the complete command before making any registry change.
            string command = BuildCommand(executable, dataDirectory);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new IOException("Windows did not allow the startup setting to be saved.");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }

        public static string BuildCommand(string executable, string dataDirectory)
        {
            return BuildCommand(executable, dataDirectory, File.Exists);
        }

        // Injectable existence check keeps installed/portable selection tests
        // independent of disk contents. No file or registry writes occur here.
        public static string BuildCommand(string executable, string dataDirectory, Func<string, bool> fileExists)
        {
            if (fileExists == null) throw new ArgumentNullException("fileExists");
            string fullExecutable = NormalizePath(executable, "executable");
            string fullData = NormalizePath(dataDirectory, "dataDirectory");
            string launcher = null;
            if (String.Equals(Path.GetFileName(fullData), "data", StringComparison.OrdinalIgnoreCase))
            {
                string root = Path.GetDirectoryName(fullData);
                if (!String.IsNullOrEmpty(root))
                {
                    string versions = Path.Combine(root, "versions") + Path.DirectorySeparatorChar;
                    string candidate = Path.Combine(root, "DisplayWeave.Launcher.exe");
                    if (fullExecutable.StartsWith(versions, StringComparison.OrdinalIgnoreCase) && fileExists(candidate))
                        launcher = candidate;
                }
            }
            // The launcher reads the data directory from current.json beside it, so the startup command
            // only passes --minimized. It is a signed program, not powershell.exe running a script.
            string command = launcher != null ?
                QuoteArgument(launcher) + " --minimized" :
                QuoteArgument(fullExecutable) + " --minimized --data-dir " + QuoteArgument(fullData);
            if (!IsUsableCommand(command))
                throw new ArgumentException("The Windows startup command exceeds the 260-character limit. Move DisplayWeave to a shorter path before enabling startup.");
            return command;
        }

        private static string NormalizePath(string path, string parameter)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", parameter);
            string result = Path.GetFullPath(path);
            string root = Path.GetPathRoot(result);
            while (result.Length > root.Length && (result[result.Length - 1] == Path.DirectorySeparatorChar ||
                result[result.Length - 1] == Path.AltDirectorySeparatorChar))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        internal static bool IsUsableCommand(string command)
        {
            return !String.IsNullOrWhiteSpace(command) && command.Length <= MaximumCommandLength &&
                command.IndexOfAny(new char[] { '\0', '\r', '\n' }) < 0;
        }

        // Standard Windows argv quoting: backslashes are literal except before
        // a quote, and trailing backslashes must be doubled before our end quote.
        internal static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') quoted.Append('\\', slashes * 2 + 1);
                else quoted.Append('\\', slashes);
                quoted.Append(character);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
