using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DisplayWeave
{
    internal static class Program
    {
        internal static readonly int ShowSettingsMessage = (int)RegisterWindowMessage("DisplayWeave.ShowSettings.1");
        internal static readonly int ExitForUpdateMessage = (int)RegisterWindowMessage("DisplayWeave.ExitForUpdate.1");

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                string report = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results.txt");
                int layout = LayoutTests.Run(report);
                int rules = RuleTests.Run(Path.ChangeExtension(report, "rules.txt"));
                int automatic = AutomaticRegionTests.Run(Path.ChangeExtension(report, "automatic.txt"));
                int lifecycle = LifecycleTests.Run(Path.ChangeExtension(report, "lifecycle.txt"));
                return layout == 0 && rules == 0 && automatic == 0 && lifecycle == 0 ? 0 : 1;
            }

            if (args.Length >= 3 && args[0] == "--verify-auto-controller")
                return AutomaticControllerIntegration.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length >= 3 && args[0] == "--verify-monitor-controller")
                return AutomaticControllerIntegration.RunMonitor(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            AppOptions options;
            try { options = AppOptions.Parse(args); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DisplayWeave could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            if (options.ExitForUpdate)
            {
                PostMessage(new IntPtr(0xffff), (uint)ExitForUpdateMessage, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            try { ElevationRestart.WaitForPreviousProcess(options.WaitForExitProcessId); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DisplayWeave could not restart", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            bool created;
            Mutex instanceMutex;
            try { instanceMutex = new Mutex(true, "Local\\DisplayWeave.Portable", out created); }
            catch (UnauthorizedAccessException)
            {
                if (!options.StartMinimized)
                    MessageBox.Show("DisplayWeave is already running with different permissions. Open it from its tray icon, or exit that instance before starting another.",
                        "DisplayWeave is already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            using (var mutex = instanceMutex)
            {
                if (!created)
                {
                    if (!options.StartMinimized)
                        PostMessage(new IntPtr(0xffff), (uint)ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                WindowManager.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string helpers = null;
                if (ElevationRestart.IsElevated())
                {
                    try { helpers = ElevationRestart.CopyHelpers(AppDomain.CurrentDomain.BaseDirectory, ElevationRestart.HelperRoot, ElevationRestart.UserSid()); }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                        MessageBox.Show("DisplayWeave could not prepare its helpers for administrator mode.\n\n" + ex.Message, "DisplayWeave could not start",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 2;
                    }
                    FullscreenController.HelperDirectory = helpers;
                }
                try
                {
                    using (var form = new MainForm(options.AutomaticExecutableFilter, options.DataDirectory, options.StartMinimized))
                    {
                        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                        {
                            form.PauseFullscreenRules();
                            MessageBox.Show("Windows have been released after an error.\n\n" + e.Exception.Message, "DisplayWeave");
                        };
                        try { Application.Run(form); }
                        finally { form.PauseFullscreenRules(); }
                    }
                }
                finally { if (helpers != null) ElevationRestart.RemoveHelpers(helpers); }
            }
            return 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
