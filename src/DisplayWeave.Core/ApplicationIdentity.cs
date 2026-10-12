using System;

namespace DisplayWeave
{
    public sealed class ApplicationIdentity
    {
        public IntPtr Handle;
        public uint ProcessId;
        public long CreationTime;
        public string ExecutablePath;
        public string WindowClass;
        public bool Is32Bit;
        // A Store app's AppUserModelID; null for every other app.
        public string AppId;
        public string ProcessKey { get { return ProcessId + ":" + CreationTime; } }
        public string Key { get { return ProcessId + ":" + CreationTime + ":" + Handle.ToInt64(); } }
    }
}
