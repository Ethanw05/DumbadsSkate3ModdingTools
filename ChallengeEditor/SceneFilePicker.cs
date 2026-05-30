using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ChallengeEditor;

/// File-picker dialogs for .cescn scenes. Uses the Common Item Dialog COM
/// API (IFileOpenDialog / IFileSaveDialog) directly — NOT WinForms.
///
/// WinForms `OpenFileDialog.ShowDialog` spawns its own modal message pump
/// which deadlocks the SDL2-hosted editor: SDL's PumpEvents thread + the
/// WinForms modal pump fight over message dispatch and the dialog never
/// returns. FolderPicker.cs already uses the COM API and works fine; this
/// mirrors that pattern.
public static class SceneFilePicker
{
    /// Editor HWND used as dialog parent. Set once at startup by Program.cs.
    public static IntPtr OwnerHwnd { get; set; } = IntPtr.Zero;

    public static string? PickOpen(string title, string? initialPath = null)
        => ShowDialog(title, initialPath, isSave: false);

    public static string? PickSave(string title, string? initialPath = null)
        => ShowDialog(title, initialPath, isSave: true);

    private static string? ShowDialog(string title, string? initialPath, bool isSave)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SceneFilePicker requires Windows.");

        Guid clsid = isSave
            ? new Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B") // CLSID_FileSaveDialog
            : new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"); // CLSID_FileOpenDialog

        Type? t = Type.GetTypeFromCLSID(clsid);
        if (t is null) return null;
        object? raw = Activator.CreateInstance(t);
        if (raw is not IFileDialog dlg) return null;

        try
        {
            dlg.GetOptions(out FOS opts);
            FOS extra = FOS.FOS_FORCEFILESYSTEM | FOS.FOS_NOCHANGEDIR;
            if (isSave) extra |= FOS.FOS_OVERWRITEPROMPT;
            else extra |= FOS.FOS_PATHMUSTEXIST | FOS.FOS_FILEMUSTEXIST;
            dlg.SetOptions(opts | extra);
            dlg.SetTitle(title);
            dlg.SetDefaultExtension("cescn");

            // Filter: .cescn then all files.
            var specs = new COMDLG_FILTERSPEC[]
            {
                new() { pszName = "Challenge Editor Scene (*.cescn)", pszSpec = "*.cescn" },
                new() { pszName = "All Files (*.*)", pszSpec = "*.*" },
            };
            int size = Marshal.SizeOf<COMDLG_FILTERSPEC>();
            IntPtr mem = Marshal.AllocCoTaskMem(size * specs.Length);
            try
            {
                for (int i = 0; i < specs.Length; i++)
                    Marshal.StructureToPtr(specs[i], mem + i * size, false);
                dlg.SetFileTypes((uint)specs.Length, mem);
            }
            finally { Marshal.FreeCoTaskMem(mem); }

            // Seed initial folder / filename from initialPath.
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                string? dir = Path.GetDirectoryName(initialPath);
                string fileName = Path.GetFileName(initialPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    if (SHCreateItemFromParsingName(dir, IntPtr.Zero, typeof(IShellItem).GUID, out object? si) == 0 && si is IShellItem item)
                        dlg.SetFolder(item);
                }
                if (!string.IsNullOrWhiteSpace(fileName))
                    dlg.SetFileName(fileName);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_OVERWRITEPROMPT = 0x00000002,
        FOS_PATHMUSTEXIST   = 0x00000800,
        FOS_FILEMUSTEXIST   = 0x00001000,
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

    // IFileDialog covers BOTH IFileOpenDialog (CLSID_FileOpenDialog) and
    // IFileSaveDialog (CLSID_FileSaveDialog) — both derive from IFileDialog
    // and the methods we use here are all on the IFileDialog base.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
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
    }
}
