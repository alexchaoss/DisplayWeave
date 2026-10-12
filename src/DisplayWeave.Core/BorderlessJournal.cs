using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    // The windows a Borderless in region rule showed without their title bar, kept on disk while they are, so the
    // next start can give them back what DisplayWeave removed if it stopped without restoring them. One line per
    // window: handle|process ID|process creation time|removed style|removed extended style|x|y|width|height|program path.
    internal sealed class BorderlessJournal
    {
        internal IntPtr Handle;
        internal uint ProcessId;
        internal long CreationTime;
        internal long RemovedStyle, RemovedExStyle;
        internal Rectangle OriginalBounds;
        internal string ExecutablePath;

        // The same process window it was written for; a reused handle or process ID is someone else's.
        internal bool Matches(ApplicationIdentity current)
        {
            return current != null && current.Handle == Handle && current.ProcessId == ProcessId && current.CreationTime == CreationTime &&
                String.Equals(current.ExecutablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Format(IEnumerable<BorderlessJournal> entries)
        {
            StringBuilder text = new StringBuilder();
            foreach (BorderlessJournal entry in entries)
                text.Append(String.Join("|", new[] { entry.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                    entry.ProcessId.ToString(CultureInfo.InvariantCulture), entry.CreationTime.ToString(CultureInfo.InvariantCulture),
                    entry.RemovedStyle.ToString(CultureInfo.InvariantCulture), entry.RemovedExStyle.ToString(CultureInfo.InvariantCulture),
                    entry.OriginalBounds.X.ToString(CultureInfo.InvariantCulture), entry.OriginalBounds.Y.ToString(CultureInfo.InvariantCulture),
                    entry.OriginalBounds.Width.ToString(CultureInfo.InvariantCulture), entry.OriginalBounds.Height.ToString(CultureInfo.InvariantCulture),
                    entry.ExecutablePath })).Append('\n');
            return text.ToString();
        }

        // Lines that do not parse are skipped: the file only ever helps put a title bar back.
        internal static List<BorderlessJournal> Parse(string text)
        {
            List<BorderlessJournal> entries = new List<BorderlessJournal>();
            foreach (string line in (text ?? String.Empty).Split('\n'))
            {
                string[] parts = line.TrimEnd('\r').Split('|');
                long handle, creation, style, extended;
                uint pid;
                int x, y, width, height;
                if (parts.Length != 10 || !Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out handle) ||
                    !UInt32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) ||
                    !Int64.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out creation) ||
                    !Int64.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out style) ||
                    !Int64.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out extended) ||
                    !Int32.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                    !Int32.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                    !Int32.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                    !Int32.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) ||
                    width <= 0 || height <= 0 || !Path.IsPathRooted(parts[9])) continue;
                entries.Add(new BorderlessJournal { Handle = new IntPtr(handle), ProcessId = pid, CreationTime = creation, RemovedStyle = style,
                    RemovedExStyle = extended, OriginalBounds = new Rectangle(x, y, width, height), ExecutablePath = parts[9] });
            }
            return entries;
        }

        internal static List<BorderlessJournal> Read(string path)
        {
            try { return Parse(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null); }
            catch (IOException) { return new List<BorderlessJournal>(); }
            catch (UnauthorizedAccessException) { return new List<BorderlessJournal>(); }
        }

        // Replaced atomically; removed when no window is shown borderless. A failure only loses the crash recovery.
        internal static void Write(string path, string text)
        {
            try
            {
                if (text.Length == 0) { File.Delete(path); return; }
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch (IOException ex) { ActivityLog.Write("The borderless window list could not be saved: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { ActivityLog.Write("The borderless window list could not be saved: " + ex.Message); }
        }

        // After an unexpected error: give every listed window that still belongs to its process its title bar back.
        public static void RestoreAll(string path)
        {
            foreach (BorderlessJournal entry in Read(path))
            {
                ApplicationIdentity current;
                string error;
                if (FullscreenController.TryInspect(entry.Handle, out current, out error) && entry.Matches(current))
                    FullscreenController.RestoreBorderless(entry.Handle, entry.RemovedStyle, entry.RemovedExStyle, entry.OriginalBounds);
            }
            Write(path, String.Empty);
        }
    }
}
