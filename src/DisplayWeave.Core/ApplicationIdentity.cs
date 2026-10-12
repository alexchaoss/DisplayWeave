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
        public string ProcessKey { get { return ProcessId + ":" + CreationTime; } }
        public string Key { get { return ProcessId + ":" + CreationTime + ":" + Handle.ToInt64(); } }
    }
}
