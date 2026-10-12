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
        public static int Run(string reportPath)
        {
            string fullReportPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullReportPath);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "rule-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            StringBuilder report = new StringBuilder();
            int passed = 0, failed = 0;
            Action<string, Action> test = delegate(string name, Action body)
            {
                try { body(); passed++; report.AppendLine("PASS " + name); }
                catch (Exception ex) { failed++; report.AppendLine("FAIL " + name + ": " + ex.Message); }
            };
            try
            {
                SavedRuleAndRegionTests(test, temporary);
                ExclusionTests(test);
                OutsideFitTests(test);
                ShellTests(test);
                DiagnosticTests(test);
                HandlingTests(test, temporary);
                IdentityTests(test);
                RecoveryTests(test);
            }
            finally
            {
                // Delete only this run's uniquely named, flat test directory.
                foreach (string path in Directory.GetFiles(temporary)) File.Delete(path);
                Directory.Delete(temporary);
            }
            report.AppendLine(); report.AppendLine(passed + " passed; " + failed + " failed.");
            File.WriteAllText(fullReportPath, report.ToString(), new UTF8Encoding(false));
            Console.Write(report.ToString());
            return failed == 0 ? 0 : 1;
        }

        private static ApplicationRule Rule(string path, string windowClass, ApplicationAction action)
        {
            return new ApplicationRule { ExecutablePath = path, WindowClass = windowClass, DisplayName = "Example", Action = action,
                MonitorDevice = "\\\\.\\DISPLAY1", ZoneId = Guid.NewGuid().ToString("N") };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private struct TestRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out TestRect rect);

        private static long WindowStyle(IntPtr handle, int index) { return GetWindowLongPtr(handle, index).ToInt64(); }

        private static Rectangle WindowBounds(IntPtr handle)
        {
            TestRect rect;
            GetWindowRect(handle, out rect);
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        // A hidden window without a title bar, as a game in borderless fullscreen or an overlay.
        private sealed class BorderlessWindow : System.Windows.Forms.Form
        {
            private readonly int extended;

            internal BorderlessWindow(Rectangle bounds, int extended)
            {
                this.extended = extended;
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                Bounds = bounds;
                CreateHandle();
            }

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get { System.Windows.Forms.CreateParams parameters = base.CreateParams; parameters.ExStyle |= extended; return parameters; }
            }
        }

        // Hidden and maximized; like Chromium, it refuses every move or resize in WM_WINDOWPOSCHANGING while maximized.
        private sealed class RefusingWindow : System.Windows.Forms.Form
        {
            [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr handle);
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
            [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect rect, int size);
            private struct NativeRect { public int Left, Top, Right, Bottom; }

            internal bool Zoomed { get { return IsZoomed(Handle); } }

            internal Rectangle VisibleFrame
            {
                get
                {
                    NativeRect rect;
                    if (DwmGetWindowAttribute(Handle, 9, out rect, 16) != 0) GetWindowRect(Handle, out rect); // DWMWA_EXTENDED_FRAME_BOUNDS
                    return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                }
            }

            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get { System.Windows.Forms.CreateParams parameters = base.CreateParams; parameters.Style |= 0x01000000; return parameters; } // WS_MAXIMIZE
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == 0x0046 && IsZoomed(m.HWnd)) // WM_WINDOWPOSCHANGING
                {
                    int flags = 2 * IntPtr.Size + 16; // WINDOWPOS.flags
                    Marshal.WriteInt32(m.LParam, flags, Marshal.ReadInt32(m.LParam, flags) | 0x0003); // SWP_NOSIZE | SWP_NOMOVE
                }
                base.WndProc(ref m);
            }
        }
    }
}
