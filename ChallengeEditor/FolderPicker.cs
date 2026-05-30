using System;
using System.Runtime.InteropServices;

namespace ChallengeEditor;

// Modern Windows folder picker via the Common Item Dialog (IFileOpenDialog with
// FOS_PICKFOLDERS). Doesn't require a WinForms message pump or owner window —
// works reliably from inside a Veldrid/SDL render loop.
public static class FolderPicker
{
    public static IntPtr OwnerHwnd { get; set; } = IntPtr.Zero;

    public static string? Pick(string title, string? initialPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("FolderPicker requires Windows.");

        Type? t = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")); // CLSID_FileOpenDialog
        if (t is null) return null;
        object? raw = Activator.CreateInstance(t);
        if (raw is not IFileOpenDialog dlg) return null;

        try
        {
            dlg.GetOptions(out FOS opts);
            dlg.SetOptions(opts | FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM | FOS.FOS_NOCHANGEDIR);
            dlg.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialPath) && System.IO.Directory.Exists(initialPath))
            {
                if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out object? si) == 0 && si is IShellItem item)
                {
                    dlg.SetFolder(item);
                }
            }

            int hr = dlg.Show(OwnerHwnd);
            if (hr != 0) return null; // user canceled or error
            dlg.GetResult(out IShellItem? result);
            if (result is null) return null;
            result.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string? path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dlg);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [Flags]
    private enum FOS : uint
    {
        FOS_PICKFOLDERS     = 0x00000020,
        FOS_FORCEFILESYSTEM = 0x00000040,
        FOS_NOCHANGEDIR     = 0x00000008,
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS fos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem? ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid([MarshalAs(UnmanagedType.LPStruct)] Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }
}
