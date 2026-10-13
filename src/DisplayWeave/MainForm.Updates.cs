using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace DisplayWeave
{
    // Automatic updates: the hourly release check, installing a downloaded setup, telling the launcher this
    // version runs, and going back to the previous version.
    public sealed partial class MainForm
    {
        private readonly Timer updateTimer = new Timer();
        private readonly string installRoot = Updater.InstallRoot(Application.ExecutablePath);
        private DateTime nextUpdateCheck = DateTime.UtcNow;
        private bool checkingForUpdate;
        private string updateSetup, updateVersion, updateNotice = "";
        private ToolStripMenuItem trayUpdate, trayGoBack;
        private readonly DateTime started = DateTime.UtcNow;
        private bool healthMarked;

        // An installed copy runs from versions\<version>\; the folder name is the version current.json names.
        private static string InstalledVersion { get { return Path.GetFileName(Path.GetDirectoryName(Application.ExecutablePath)); } }

        // Checks the releases right after start and then every hour. A downloaded update installs by itself while this
        // window is closed and no game is in its region through a Force fullscreen or Borderless in region rule, so it is
        // never resized. Other windows fitted from outside stay where they are; the new version adopts them.
        private void UpdateTick()
        {
            if (updateTimer.Interval != 60000) updateTimer.Interval = 60000;
            if (updateSetup != null)
            {
                if (!Visible && !fullscreen.HasExpandedApp) InstallUpdate();
                return;
            }
            if (checkingForUpdate || DateTime.UtcNow < nextUpdateCheck) return;
            checkingForUpdate = true;
            nextUpdateCheck = DateTime.UtcNow.AddHours(1);
            string current = InstalledVersion;
            System.Collections.Generic.HashSet<string> failed = Updater.FailedVersions(installRoot);
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string version = null, setup = null, problem = null;
                try { setup = Updater.DownloadNewer(current, failed, out version); }
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
            // Right after sign-in the network may not be up yet, so a failed check is tried again sooner.
            if (problem != null) nextUpdateCheck = DateTime.UtcNow.AddMinutes(5);
            if (setup == null) return;
            updateSetup = setup;
            updateVersion = version;
            trayUpdate.Text = "Install DisplayWeave " + version + " now";
            trayUpdate.Visible = true;
            updateNotice = "DisplayWeave " + version + " is ready. It installs when this window is closed and no game is in its region through a Force fullscreen or Borderless in region rule.";
            if (!Visible && !fullscreen.HasExpandedApp) { InstallUpdate(); return; }
            ShowBalloon("DisplayWeave " + version + " is ready", "It installs when no game is in its region through a Force fullscreen or Borderless in region rule. To install now, choose it in the tray menu.", ToolTipIcon.Info);
        }

        private void InstallUpdate()
        {
            string setup = updateSetup;
            updateSetup = null;
            trayUpdate.Visible = false;
            try
            {
                // The setup waited in the user's temporary folder since it was checked: it is held open, so it cannot
                // change, and checked again before Windows starts it.
                using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!Updater.IsSignedRelease(setup, updateVersion, Updater.ReleaseKeys))
                    {
                        updateNotice = "The downloaded DisplayWeave " + updateVersion + " setup changed after it was checked, so it was not installed. It is downloaded again at the next check.";
                        nextUpdateCheck = DateTime.UtcNow;
                        return;
                    }
                    // The Inno setup closes this instance (--exit-for-update), installs into the same root, then
                    // relaunches the new version. /VERYSILENT keeps it windowless; /NORESTART leaves Windows alone.
                    using (Process.Start(new ProcessStartInfo(setup,
                        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"" + installRoot + "\" /LOG=\"" + setup + ".log\"") { UseShellExecute = false })) { }
                }
            }
            catch (System.ComponentModel.Win32Exception ex) { updateNotice = "Could not start the DisplayWeave " + updateVersion + " setup: " + ex.Message; }
            catch (IOException ex) { updateNotice = "Could not open the DisplayWeave " + updateVersion + " setup: " + ex.Message; nextUpdateCheck = DateTime.UtcNow; }
        }

        // Tells the launcher this version works: after a minute of running, or on a clean exit. Without it, two
        // failed starts send the launcher back to the previous version.
        private void MarkHealthy()
        {
            if (installRoot == null || healthMarked) return;
            healthMarked = true;
            try { File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(installRoot, "health")).FullName, InstalledVersion + ".ok"), String.Empty); }
            catch (IOException ex) { ActivityLog.Write("Could not record that this version runs: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { ActivityLog.Write("Could not record that this version runs: " + ex.Message); }
        }

        private void RefreshGoBack()
        {
            string previous = installRoot == null ? null : Updater.PreviousVersion(installRoot, InstalledVersion);
            trayGoBack.Visible = previous != null;
            if (previous != null) trayGoBack.Text = "Go back to DisplayWeave " + previous;
        }

        // The earlier version starts once this one has exited, and this version is not offered as an update again.
        private void GoBack()
        {
            string previous = Updater.PreviousVersion(installRoot, InstalledVersion);
            if (previous == null) return;
            if (MessageBox.Show(this, "Go back to DisplayWeave " + previous + "? DisplayWeave restarts, and " + InstalledVersion +
                " is not installed as an update again.", "Go back to an earlier version", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                Updater.GoBack(installRoot, InstalledVersion, previous);
                using (Process current = Process.GetCurrentProcess())
                using (Process.Start(new ProcessStartInfo(Path.Combine(installRoot, "versions", previous, "DisplayWeave.exe"),
                    "--data-dir " + StartupRegistration.QuoteArgument(dataDirectory) + " --wait-for-exit " + current.Id) { UseShellExecute = false })) { }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.ComponentModel.Win32Exception)) throw;
                Report("Could not go back to DisplayWeave " + previous + ": " + ex.Message);
                return;
            }
            ActivityLog.Write("Going back to DisplayWeave " + previous + ".");
            ExitApplication();
        }
    }
}
