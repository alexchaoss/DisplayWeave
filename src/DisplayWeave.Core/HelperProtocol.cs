using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace DisplayWeave
{
    // The text exchanged with the native helper: its command-line geometry and its readiness line.
    internal static class HelperProtocol
    {
        // Only integer geometry enters the helper command line and control pipe.
        // Validate before serializing so malformed layouts cannot partially update a host.
        internal static bool TrySerializeRegions(IEnumerable<Rectangle> regions, Rectangle selected, out string serialized)
        {
            serialized = null;
            if (regions == null) return false;
            List<Rectangle> checkedRegions = new List<Rectangle>();
            StringBuilder text = new StringBuilder();
            bool includesSelected = false;
            foreach (Rectangle region in regions)
            {
                if (checkedRegions.Count == 9 || region.Width <= 0 || region.Height <= 0 ||
                    (long)region.X + region.Width > Int32.MaxValue || (long)region.Y + region.Height > Int32.MaxValue)
                    return false;
                foreach (Rectangle previous in checkedRegions)
                    if (previous.IntersectsWith(region)) return false;
                checkedRegions.Add(region);
                if (region == selected) includesSelected = true;
                if (text.Length != 0) text.Append(';');
                text.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", region.X, region.Y, region.Width, region.Height);
            }
            if (!includesSelected) return false;
            serialized = text.ToString();
            return true;
        }

        internal static bool TryParseReady(string message, uint expectedProcessId, out bool reused)
        {
            uint capabilities;
            return TryParseReady(message, expectedProcessId, out reused, out capabilities);
        }

        internal static bool TryParseReady(string message, uint expectedProcessId, out bool reused, out uint capabilities)
        {
            reused = false;
            capabilities = 0;
            if (String.IsNullOrEmpty(message) || expectedProcessId == 0) return false;
            string[] parts = message.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 5 || parts[0] != "READY") return false;
            uint processId;
            if (!UInt32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) || processId != expectedProcessId)
                return false;
            int next = 2;
            if (next < parts.Length && parts[next] == "REUSED")
            {
                reused = true;
                next++;
            }
            if (next == parts.Length) return true; // Legacy helpers do not report capabilities.
            return next + 2 == parts.Length && parts[next] == "CAPS" &&
                UInt32.TryParse(parts[next + 1], NumberStyles.None, CultureInfo.InvariantCulture, out capabilities) &&
                ((capabilities & 5) == 5 || (capabilities & 3) == 3);
        }

        internal static string FormatReady(ApplicationIdentity target, bool automatic, bool enabled, bool reused, uint capabilities = 0)
        {
            string name = target == null ? "the application" : Path.GetFileName(target.ExecutablePath);
            if (String.IsNullOrWhiteSpace(name)) name = "the application";
            string message = reused ? "Reconnected region handling for " + name + " using its existing compatible hooks" :
                (automatic ? "Automatic virtual monitor and window handling ready for " : "Virtual monitor and window handling ready for ") + name;
            if ((capabilities & 5) != 5 && (capabilities & 3) == 3)
                message += " (legacy fullscreen engine)";
            return message + (enabled ? "." : "; control is paused.");
        }

        internal static string BoundsArguments(Rectangle r)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", r.X, r.Y, r.Width, r.Height);
        }
    }
}
