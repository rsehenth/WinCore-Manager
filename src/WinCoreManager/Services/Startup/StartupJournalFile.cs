using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinCoreManager.Services.Startup;

internal static class StartupJournalFile
{
    // The caller pins the parent and holds the operation lock for the whole write.
    public static void Save<T>(string file, T value, bool replace)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(file)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            const uint writeThrough = 8;
            const uint replaceExisting = 1;
            if (!MoveFileExW(temporary, file, writeThrough | (replace ? replaceExisting : 0)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string destination, uint flags);
}

