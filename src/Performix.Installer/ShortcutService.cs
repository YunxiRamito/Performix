using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Performix.Installer;

/// <summary>
/// Creates the <c>.lnk</c> files.
///
/// This is the one place the installer talks to COM, and it does so through the documented
/// <c>IShellLinkW</c> + <c>IPersistFile</c> pair rather than the deprecated
/// <c>IWshShell</c> (WScript.Shell is a scripting-host dependency and is missing on locked-down
/// machines). The interop is hand-written because the project takes no NuGet packages and
/// because CsWin32/Xamarin.Essentials-style packages would add megabytes to a 15 MB installer.
///
/// Note that a shortcut's *working directory* is not cosmetic here: Performix reads
/// <c>preysense.json</c> from <c>AppContext.BaseDirectory</c>, and the shortcut is what makes
/// "double-click from the start menu" and "double-click a copy of the exe" behave the same.
/// </summary>
public static class ShortcutService
{
    /// <summary>Creates/replaces the start-menu shortcut and returns its path.</summary>
    public static string CreateStartMenuShortcut(string executablePath, string workingDirectory)
        => CreateShortcut(InstallerPaths.StartMenuShortcutPath, executablePath, workingDirectory,
            "Performix — 性能模式与风扇控制");

    /// <summary>Creates/replaces the desktop shortcut and returns its path.</summary>
    public static string CreateDesktopShortcut(string executablePath, string workingDirectory)
        => CreateShortcut(InstallerPaths.DesktopShortcutPath, executablePath, workingDirectory,
            "Performix — 性能模式与风扇控制");

    /// <summary>
    /// Writes a shortcut pointing at <paramref name="targetPath"/>, with
    /// <paramref name="workingDirectory"/> as its "start in" folder.
    /// </summary>
    public static string CreateShortcut(
        string shortcutPath, string targetPath, string workingDirectory, string description = "Performix")
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        object? shellLinkObject = null;
        try
        {
            shellLinkObject = CreateShellLinkInstance();
            var shellLink = (IShellLinkW)shellLinkObject;

            shellLink.SetPath(targetPath);
            shellLink.SetWorkingDirectory(workingDirectory);
            shellLink.SetDescription(description);
            shellLink.SetIconLocation(targetPath, 0);
            shellLink.SetShowCmd(SwShownormal);

            ((IPersistFile)shellLinkObject).Save(shortcutPath, true);
            return shortcutPath;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                $"创建快捷方式失败（0x{ex.HResult:X8}）：{ex.Message}", ex);
        }
        finally
        {
            ReleaseComObject(shellLinkObject);
        }
    }

    /// <summary>Deletes a shortcut if it exists. Returns true when something was removed.</summary>
    public static bool DeleteShortcut(string shortcutPath)
    {
        try
        {
            if (!File.Exists(shortcutPath))
            {
                return false;
            }

            File.Delete(shortcutPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Both shortcuts the installer can create, for uninstall to remove.</summary>
    public static IReadOnlyList<string> AllShortcutPaths()
        => new[] { InstallerPaths.StartMenuShortcutPath, InstallerPaths.DesktopShortcutPath };

    private const int SwShownormal = 1;

    /// <summary>
    /// Instantiates CLSID_ShellLink. The declared coclass is used first (it keeps the activation
    /// metadata in our own IL, which the trimmer can see); the CLSID lookup is the fallback for
    /// the rare case where the coclass activation is unavailable.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "CLSID activation goes through the COM catalog, not the managed parameterless constructor the analyzer warns about.")]
    private static object CreateShellLinkInstance()
    {
        try
        {
            return new ShellLinkCoClass();
        }
        catch (Exception)
        {
            Type? type = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: false);
            object? instance = type is null ? null : Activator.CreateInstance(type);
            return instance ?? throw new InvalidOperationException("无法创建 ShellLink COM 对象（CLSID_ShellLink 未注册）");
        }
    }

    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                Marshal.FinalReleaseComObject(comObject);
            }
            catch (Exception)
            {
                // Already released; nothing useful to do.
            }
        }
    }

    // ── COM declarations ────────────────────────────────────────────────────────────────

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellLinkCoClass
    {
    }

    /// <summary>
    /// <c>IShellLinkW</c>. The vtable order is load-bearing: every method must be declared in
    /// interface order, including the ones we never call, or the wrong function gets invoked.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary><c>IPersistFile</c> — its first slot comes from <c>IPersist::GetClassID</c>.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
