using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Vision.App.Infrastructure;

/// <summary>Explorer, the default browser, the Recycle Bin.</summary>
public static class ShellIntegration
{
    /// <summary>Open Explorer with the item selected.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing sensible to do.
        }
    }

    /// <summary>Open a folder in Explorer.</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    /// <summary>Open a file with its default app, or a URL in the default browser.</summary>
    public static void Open(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private const string FolderMenuKey = @"Software\Classes\Directory\shell\Vision";
    private const string BackgroundMenuKey = @"Software\Classes\Directory\Background\shell\Vision";

    /// <summary>Legacy: the keys of DSH's (Vision's former name) "Open with DSH". Removed whenever the menu is set, so
    /// Explorer never shows a stale entry pointing at DSH.exe next to "Open with Vision".</summary>
    private const string LegacyFolderMenuKey = @"Software\Classes\Directory\shell\DSH";
    private const string LegacyBackgroundMenuKey = @"Software\Classes\Directory\Background\shell\DSH";

    /// <summary>Whether "Open with Vision" is on the folder right-click menu (per user, no admin rights).</summary>
    public static bool IsFolderMenuInstalled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(FolderMenuKey + @"\command");
                return key?.GetValue(null) is string command && command.Contains(Environment.ProcessPath ?? "Vision.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Add or remove "Open with Vision" on folders and folder backgrounds in Explorer. The
    /// installer does this too; the switch in Settings serves the portable zip.</summary>
    public static bool SetFolderMenu(bool installed)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
            foreach (var legacy in new[] { LegacyFolderMenuKey, LegacyBackgroundMenuKey })
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(legacy, throwOnMissingSubKey: false);
            foreach (var (key, argument) in new[] { (FolderMenuKey, "%1"), (BackgroundMenuKey, "%V") })
            {
                if (!installed)
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                    continue;
                }
                using var entry = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
                entry.SetValue(null, "Open with Vision");
                entry.SetValue("Icon", $"\"{exe}\",0");
                using var command = entry.CreateSubKey("command");
                command.SetValue(null, $"\"{exe}\" \"{argument}\"");
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>After bringing DSH's data over: a portable DSH's "Open with DSH" (per user, pointing at DSH.exe)
    /// becomes "Open with Vision". Nothing happens when there is none.</summary>
    public static void ReplaceLegacyFolderMenu()
    {
        try
        {
            using var legacy = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LegacyFolderMenuKey);
            if (legacy is null) return;
        }
        catch (Exception)
        {
            return;
        }
        SetFolderMenu(true);
    }

    /// <summary>Move a file or folder to the Recycle Bin (recoverable). Returns false on failure.</summary>
    public static bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
