using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace DisplayWeave
{
    // One native helper process and what it reported, for the app it loads the component into.
    internal sealed class HelperSession
    {
        internal ApplicationIdentity Target;
        internal Process Host;
        internal Rectangle Region;
        internal string LayoutKey;
        internal string AutomaticZoneId;
        internal DateTime Started;
        internal bool Ready;
        internal bool Enabled = true;
        internal bool Automatic;
        internal bool? NativeExpanded;
        internal bool OutputClosed;
        internal bool ErrorClosed;
        internal bool AwaitingExitOutput;
        internal DateTime ExitObserved;
        internal readonly Queue<string> Messages = new Queue<string>();

        internal void Stop()
        {
            if (Host == null) return;
            try
            {
                if (!Host.HasExited)
                {
                    Host.StandardInput.WriteLine("QUIT"); Host.StandardInput.Flush();
                    // Native shutdown includes a 200 ms restoration grace period.
                    // Keep this bounded even if a target fails during attachment.
                    Host.WaitForExit(600);
                }
            }
            catch (Exception) { } // best effort: the helper also switches off when its input closes
            finally { Host.Dispose(); }
        }

        internal bool HostExited()
        {
            try { return Host.HasExited; } catch (InvalidOperationException) { return true; }
        }

        internal bool WaitForExitedOutput()
        {
            lock (Messages)
            {
                if (OutputClosed && ErrorClosed) return true;
                if (ExitObserved == DateTime.MinValue) ExitObserved = DateTime.UtcNow;
                // Never block the UI for an unbounded asynchronous stream flush.
                // Most exits already have EOF; delayed callbacks get up to one second across ticks.
                if ((DateTime.UtcNow - ExitObserved).TotalSeconds < 1)
                    Monitor.Wait(Messages, 40);
                return (OutputClosed && ErrorClosed) ||
                    (DateTime.UtcNow - ExitObserved).TotalSeconds >= 1;
            }
        }

        internal void QueueMessage(string message, bool errorStream)
        {
            lock (Messages)
            {
                if (message == null)
                {
                    if (errorStream) ErrorClosed = true; else OutputClosed = true;
                    Monitor.PulseAll(Messages);
                    return;
                }
                if (errorStream && !message.StartsWith("ERROR", StringComparison.Ordinal)) message = "ERROR " + message;
                if (Messages.Count < 64) Messages.Enqueue(message);
                Monitor.PulseAll(Messages);
            }
        }
    }
}
