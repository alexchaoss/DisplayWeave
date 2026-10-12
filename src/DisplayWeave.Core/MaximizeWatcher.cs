using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    // Calls back as soon as a top-level window becomes maximized, so an outside fit does not wait for
    // the next timer tick, and whenever a top-level window moves or resizes. Out-of-context WinEvents: Windows posts them to this thread's message loop
    // and nothing is loaded into other apps. Create it on a thread with a message loop.
    public sealed class MaximizeWatcher : IDisposable
    {
        private const uint EventObjectLocationChange = 0x800B, WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
        private readonly HashSet<IntPtr> zoomed = new HashSet<IntPtr>();
        private readonly Action maximized, moved;
        // Kept in a field so the delegate outlives the native hook.
        private readonly WinEventProc callback;
        private IntPtr hook;

        public MaximizeWatcher(Action maximized, Action moved = null)
        {
            this.maximized = maximized;
            this.moved = moved;
            callback = OnEvent;
            hook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, callback, 0, 0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        private void OnEvent(IntPtr eventHook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            // OBJID_WINDOW of the window itself; carets, cursors and child objects move constantly.
            if (objectId != 0 || childId != 0 || window == IntPtr.Zero) return;
            if (moved != null && GetAncestor(window, 2) == window) moved(); // GA_ROOT: a top-level window
            if (BecameMaximized(window, IsZoomed(window))) maximized();
        }

        // True only on the change to maximized; moves of a window that is already maximized, such as the fit itself, are ignored.
        internal bool BecameMaximized(IntPtr window, bool isZoomed)
        {
            if (!isZoomed) { zoomed.Remove(window); return false; }
            // shortcut: closed maximized windows stay listed until the cap clears them all; costs one extra tick per window afterwards.
            if (zoomed.Count >= 4096) zoomed.Clear();
            return zoomed.Add(window);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
        }

        private delegate void WinEventProc(IntPtr eventHook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
            uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
