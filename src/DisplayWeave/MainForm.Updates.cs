using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Automatic updates: the six-hourly release check and installing a downloaded setup.
    public sealed partial class MainForm
    {
        private readonly Timer updateTimer = new Timer();
        private readonly string installRoot = Updater.InstallRoot(Application.ExecutablePath);
        private DateTime nextUpdateCheck = DateTime.UtcNow;
        private bool checkingForUpdate;
        private string updateSetup, updateVersion, updateNotice = "";
        private ToolStripMenuItem trayUpdate;

        // Checks the releases every six hours. A downloaded update installs by itself while this window is closed
        // and no game is in its region through a Force fullscreen or Borderless in region rule, so it is never resized.
        // Other windows fitted from outside stay where they are; the new version adopts them.
        private void UpdateTick()
        {
            if (updateSetup != null)
            {
                if (!Visible && !fullscreen.HasExpandedApp) InstallUpdate();
                return;
            }
            if (checkingForUpdate || DateTime.UtcNow < nextUpdateCheck) return;
            checkingForUpdate = true;
            nextUpdateCheck = DateTime.UtcNow.AddHours(6);
            string current = Path.GetFileName(Path.GetDirectoryName(Application.ExecutablePath));
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string version = null, setup = null, problem = null;
                try { setup = Updater.DownloadNewer(current, out version); }
                catch (Exception ex) { problem = ex.Message; } // a worker-thread exception would end DisplayWeave
                try { BeginInvoke((Action)delegate { UpdateDownloaded(setup, version, problem); }); }
                catch (InvalidOperationException) { } // DisplayWeave exited meanwhile
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UpdateDownloaded(string setup, string version, string problem)
        {
            checkingForUpdate = false;
            updateNotice = problem != null ? "Update check failed: " + problem : "";
            if (setup == null) return;
            updateSetup = setup;
            updateVersion = version;
            trayUpdate.Text = "Install DisplayWeave " + version + " now";
            trayUpdate.Visible = true;
            updateNotice = "DisplayWeave " + version + " is ready. It installs when this window is closed and no game is in its region through a Force fullscreen or Borderless in region rule.";
            if (!Visible && !fullscreen.HasExpandedApp) { InstallUpdate(); return; }
            trayIcon.ShowBalloonTip(5000, "DisplayWeave " + version + " is ready", "It installs when no game is in its region through a Force fullscreen or Borderless in region rule. To install now, choose it in the tray menu.", ToolTipIcon.Info);
        }

        private void InstallUpdate()
        {
            string setup = updateSetup;
            updateSetup = null;
            trayUpdate.Visible = false;
            try
            {
                // The setup closes this instance with --exit-for-update, installs, then starts the new version.
                using (Process.Start(new ProcessStartInfo(setup, "--quiet --install-root \"" + installRoot + "\" --log \"" + setup + ".log\"") { UseShellExecute = false })) { }
            }
            catch (System.ComponentModel.Win32Exception ex) { updateNotice = "Could not start the DisplayWeave " + updateVersion + " setup: " + ex.Message; }
        }
    }
}
