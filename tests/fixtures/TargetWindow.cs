using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// A stand-in app for tests\test-outside-fit.ps1: an ordinary window in a process of its own, which the engine handles
// like any app (it never inspects its own process). That script builds it into test-results; it is never packaged.
// Commands, one per line on standard input: "hang <ms>" stops reading messages for that long, "maximize",
// "fullscreen" (borderless, covering its monitor), "screen" (reports the monitor it sees), and "exit" closes it.
// Standard input closing also closes it, so a stopped test leaves no window behind.
internal sealed class TargetWindow : Form
{
    protected override bool ShowWithoutActivation { get { return true; } } // never takes focus from the user

    [STAThread]
    private static void Main()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor, as DisplayWeave itself
        catch (EntryPointNotFoundException) { }
        TargetWindow window = new TargetWindow { Text = "DisplayWeave test target", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(160, 160, 640, 480) };
        window.Shown += delegate
        {
            Reply("ready " + window.Handle.ToInt64());
            new Thread(delegate()
            {
                string line;
                // UTF-8, whose reader drops the byte order mark Windows PowerShell starts its input with.
                System.IO.StreamReader input = new System.IO.StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
                while ((line = input.ReadLine()) != null && line != "exit")
                {
                    string command = line;
                    if (!Post(window, delegate { window.Run(command); })) return;
                }
                Post(window, window.Close);
            }) { IsBackground = true }.Start();
        };
        Application.Run(window);
    }

    private void Run(string command)
    {
        int milliseconds;
        if (command.StartsWith("hang ", StringComparison.Ordinal) && Int32.TryParse(command.Substring(5), out milliseconds))
        {
            BeginInvoke(new Action(delegate { })); // left unread: Windows counts a window as hung by its waiting messages
            Reply("hanging");
            Thread.Sleep(milliseconds);
            Reply("awake");
        }
        else if (command == "maximize") { WindowState = FormWindowState.Maximized; Reply("maximized"); }
        else if (command == "fullscreen")
        {
            // Borderless fullscreen as games and players do it: no title bar or border, covering the whole monitor.
            Rectangle monitor = Screen.FromHandle(Handle).Bounds;
            SetWindowLongPtr(Handle, -16, new IntPtr(GetWindowLongPtr(Handle, -16).ToInt64() & ~0x00C40000L)); // WS_CAPTION | WS_THICKFRAME
            SetWindowPos(Handle, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0020 | 0x0004 | 0x0010); // FRAMECHANGED | NOZORDER | NOACTIVATE
            Reply("fullscreen");
        }
        else if (command == "screen")
        {
            // The monitor as this app sees it; with DisplayWeave's component loaded, its region.
            Rectangle monitor = Screen.FromHandle(Handle).Bounds;
            Reply("screen " + monitor.X + " " + monitor.Y + " " + monitor.Width + " " + monitor.Height);
        }
        else Reply("unknown " + command);
    }

    // False once the window is gone, for example closed by hand.
    private static bool Post(Form window, Action action)
    {
        try { window.BeginInvoke(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static void Reply(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
