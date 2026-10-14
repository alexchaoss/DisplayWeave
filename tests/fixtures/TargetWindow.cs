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
    private static void Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor, as DisplayWeave itself
        catch (EntryPointNotFoundException) { }
        if (args.Length == 1 && args[0] == "--browser-class") { BrowserClassWindow.Run(); return; }
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

    internal static void Reply(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

// "--browser-class": a plain Win32 window of the class browser windows use, which DisplayWeave treats as a browser
// (the component never subclasses it). WinForms cannot make one: it renames every window class it registers.
// Commands: "screen" and "exit", as above.
internal static class BrowserClassWindow
{
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    private const uint WmDestroy = 0x0002, WmClose = 0x0010, WmCommand = 0x8000; // WM_APP: a command is waiting
    private static readonly WindowProcedure procedure = Procedure; // kept alive while the window exists
    private static readonly System.Collections.Generic.Queue<string> commands = new System.Collections.Generic.Queue<string>();

    internal static void Run()
    {
        IntPtr instance = Marshal.GetHINSTANCE(typeof(BrowserClassWindow).Module);
        WindowClass windowClass = new WindowClass { Size = Marshal.SizeOf(typeof(WindowClass)), Procedure = procedure, Instance = instance,
            Background = new IntPtr(6), ClassName = "Chrome_WidgetWin_1" }; // COLOR_WINDOW + 1
        if (RegisterClassEx(ref windowClass) == 0) { TargetWindow.Reply("error class " + Marshal.GetLastWin32Error()); return; }
        // WS_OVERLAPPEDWINDOW | WS_VISIBLE, at the stand-in's usual place.
        IntPtr window = CreateWindowEx(0, "Chrome_WidgetWin_1", "DisplayWeave test target", 0x10CF0000, 160, 160, 640, 480,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero) { TargetWindow.Reply("error window " + Marshal.GetLastWin32Error()); return; }
        TargetWindow.Reply("ready " + window.ToInt64());
        new System.Threading.Thread(delegate()
        {
            System.IO.StreamReader input = new System.IO.StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            string line;
            while ((line = input.ReadLine()) != null && line != "exit")
            {
                lock (commands) commands.Enqueue(line);
                PostMessage(window, WmCommand, IntPtr.Zero, IntPtr.Zero);
            }
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true }.Start();
        Message message;
        while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }

    private static IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmCommand)
        {
            string command;
            lock (commands) command = commands.Count > 0 ? commands.Dequeue() : null;
            if (command == "screen")
            {
                Rectangle monitor = Screen.FromHandle(window).Bounds;
                TargetWindow.Reply("screen " + monitor.X + " " + monitor.Y + " " + monitor.Width + " " + monitor.Height);
            }
            else if (command != null) TargetWindow.Reply("unknown " + command);
            return IntPtr.Zero;
        }
        if (message == WmDestroy) PostQuitMessage(0);
        return DefWindowProc(window, message, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
