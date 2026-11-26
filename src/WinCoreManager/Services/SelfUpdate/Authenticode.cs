using System.Runtime.InteropServices;

namespace WinCoreManager.Services.SelfUpdate;

internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool Verify(string path)
    {
        var file = new TrustFile
        {
            Size = (uint)Marshal.SizeOf<TrustFile>(),
            Path = path,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var action = GenericVerifyV2;
        Marshal.StructureToPtr(file, filePointer, false);
        try
        {
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UIChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = filePointer,
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                ProviderFlags = 0x1000 | 0x80, // Cached revocation, entire chain except root.
            };
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        public string Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyData;
        public IntPtr SipData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
}
