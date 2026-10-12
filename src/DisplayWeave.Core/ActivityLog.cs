using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    // What DisplayWeave did, one line each, for a bug report: <data>\logs\activity.log, rolled at about 1 MB
    // into activity.1.log. Callers write only when a message changes. Nothing is written until Open is called,
    // and writing never throws: a full or read-only disk must not stop region handling.
    public static class ActivityLog
    {
        private const long MaximumBytes = 1024 * 1024;
        private static readonly object gate = new object();
        private static string path;

        // null stops writing.
        public static void Open(string dataDirectory)
        {
            lock (gate) path = dataDirectory == null ? null : System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), "logs", "activity.log");
        }

        public static void Write(string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " +
                message.Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
            lock (gate)
            {
                if (path == null) return;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    FileInfo file = new FileInfo(path);
                    if (file.Exists && file.Length >= MaximumBytes)
                    {
                        string older = System.IO.Path.ChangeExtension(path, ".1.log");
                        File.Delete(older);
                        File.Move(path, older);
                    }
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // The latest lines, oldest first; empty when nothing was written.
        public static List<string> Tail(int count)
        {
            List<string> lines = new List<string>();
            lock (gate)
            {
                if (path == null) return lines;
                try { if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path, Encoding.UTF8)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (lines.Count > count) lines.RemoveRange(0, lines.Count - count);
            return lines;
        }
    }
}
