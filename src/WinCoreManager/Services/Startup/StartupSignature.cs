using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinCoreManager.Services.Startup;

internal static class StartupSignature
{
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static string? VerifiedPublisher(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, filePtr, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = filePtr,
            StateAction = 1, Flags = 0x1000 | 0x2000, // Cache only; reject MD2/MD4.
        };
        var action = GenericVerify;
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0 || data.State == IntPtr.Zero)
                return null;

            var provider = WTHelperProvDataFromStateData(data.State);
            if (provider == IntPtr.Zero) return null;
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return null;
            var certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return null;
            var context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Context;
            if (context == IntPtr.Zero) return null;
            using var x509 = new X509Certificate2(context);
            return x509.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        finally
        {
            if (data.State != IntPtr.Zero)
            {
                data.StateAction = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            Marshal.DestroyStructure<TrustFile>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyData, SipData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State, Url;
        public uint Flags, UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public IntPtr Context;
    }

    [DllImport("wintrust.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
