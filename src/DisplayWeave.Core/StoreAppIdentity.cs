using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DisplayWeave
{
    // Store apps all run their windows in ApplicationFrameHost.exe with the same class; the window's
    // AppUserModelID (the property the taskbar groups by) tells them apart.
    internal static class StoreAppIdentity
    {
        internal static bool IsHost(string executablePath)
        {
            return String.Equals(Path.GetFileName(executablePath), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        }

        // null when the window has none or Windows does not report it.
        internal static string Read(IntPtr window)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0 || store == null) return null;
                PropertyKey key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 }; // PKEY_AppUserModel_ID
                PropVariant value = new PropVariant();
                if (store.GetValue(ref key, out value) != 0) return null;
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; } // VT_LPWSTR
                finally { PropVariantClear(ref value); }
            }
            catch (COMException) { return null; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        // A short name for a rule: the package name before its publisher suffix.
        internal static string DisplayName(string appId)
        {
            int end = appId.IndexOfAny(new[] { '_', '!' });
            return end > 0 ? appId.Substring(0, end) : appId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        // PROPVARIANT: the type, then a 16-byte union whose first member is the string pointer.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }
}
