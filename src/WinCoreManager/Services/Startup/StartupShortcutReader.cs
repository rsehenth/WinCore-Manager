using WinCoreManager.Localization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinCoreManager.Services.Startup;

internal interface IStartupShortcutReader
{
    StartupTarget Read(string path);
}

internal sealed class WindowsStartupShortcutReader : IStartupShortcutReader
{
    private static readonly Guid ShellLink = new("00021401-0000-0000-C000-000000000046");

    public StartupTarget Read(string path)
    {
        if (!StartupPaths.IsSafeFile(path))
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheShortcutIsUnavailableOrItsPathCould"));

        using var header = new BinaryReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        if (header.BaseStream.Length < 76 || header.ReadUInt32() != 76
            || new Guid(header.ReadBytes(16)) != ShellLink)
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheFileIsNotAValidWindowsShortcut"));

        var flags = header.ReadUInt32();
        const uint alternateLaunch = 0x00001000 | 0x00002000 | 0x00020000 | 0x00800000 | 0x02000000;
        if ((flags & alternateLaunch) != 0)
            return StartupTarget.Unresolved(LocalizationService.Source("Message.TheWindowsShortcutUsesAnIndirectLaunchMode"));

        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLink, throwOnError: true)!)!;
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH; nunca chama Resolve.
            link.GetArguments(arguments, arguments.Capacity);
            var executable = StartupPaths.Executable(target.ToString(), arguments.ToString(), expand: true);
            if (executable.Path is null || executable.Problem is not null) return executable;

            link.GetIDList(out var idList);
            try
            {
                if (idList != IntPtr.Zero)
                {
                    var itemPath = new StringBuilder(32768);
                    if (!SHGetPathFromIDListEx(idList, itemPath, (uint)itemPath.Capacity, 0)
                        || !StartupPaths.TryLocalPath(itemPath.ToString(), out var local)
                        || !local.Equals(executable.Path, StringComparison.OrdinalIgnoreCase))
                        return StartupTarget.Unresolved(LocalizationService.Source("Message.TheShortcutPointsToADifferentOrIndirect"),
                            executable.Arguments);
                }
                else if ((flags & 1) != 0)
                {
                    return StartupTarget.Unresolved(LocalizationService.Source("Message.TheUnderlyingShortcutTargetCouldNotBeVerified"), executable.Arguments);
                }
            }
            finally { Marshal.FreeCoTaskMem(idList); }
            return executable;
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr idList, [Out] StringBuilder path, uint capacity, uint flags);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList();
        void GetDescription();
        void SetDescription();
        void GetWorkingDirectory();
        void SetWorkingDirectory();
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    }
}
