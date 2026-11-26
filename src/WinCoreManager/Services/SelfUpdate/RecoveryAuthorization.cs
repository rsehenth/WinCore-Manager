using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinCoreManager.Services.SelfUpdate;

internal static class RecoveryAuthorization
{
    internal const string FileName = "authorization.dpapi";
    private const int MaximumReceiptBytes = 16 * 1024;
    private const uint NoUi = 1;

    public static void Write(PortableFileSystem paths, string root, string work, SignedPortableManifest previous, SignedPortableManifest incoming)
    {
        var receipt = Transform(Binding(root, work, previous, incoming), protect: true);
        var path = Path.Combine(work, FileName);
        PortablePackage.CheckPath(path);
        using var file = paths.CreateNew(path);
        file.Write(receipt);
        file.Flush(flushToDisk: true);
    }

    public static void Verify(string root, string work, SignedPortableManifest previous, SignedPortableManifest incoming)
    {
        var path = Path.Combine(work, FileName);
        PortablePackage.CheckPath(path);
        using var file = PortableFileSystem.OpenRead(path);
        if (file.Length is <= 0 or > MaximumReceiptBytes)
            throw new UpdateException(UpdateState.VerificationFailed);
        var receipt = new byte[file.Length];
        file.ReadExactly(receipt);
        var actual = Transform(receipt, protect: false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Binding(root, work, previous, incoming)))
            throw new UpdateException(UpdateState.VerificationFailed);
    }

    private static byte[] Binding(string root, string work, SignedPortableManifest previous, SignedPortableManifest incoming) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', "WinCoreManager.Recovery.v1",
            root.ToUpperInvariant(), Path.GetFileName(work).ToUpperInvariant(), previous.ManifestDigest,
            previous.SignatureDigest, incoming.ManifestDigest, incoming.SignatureDigest)));

    private static unsafe byte[] Transform(byte[] value, bool protect)
    {
        fixed (byte* pointer = value)
        {
            var input = new DataBlob { Length = value.Length, Bytes = (IntPtr)pointer };
            var success = protect
                ? CryptProtectData(in input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUi, out var output)
                : CryptUnprotectData(in input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoUi, out output);
            if (!success)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new UpdateException(UpdateState.VerificationFailed, error);
            }
            try
            {
                if (output.Length is <= 0 or > MaximumReceiptBytes)
                    throw new UpdateException(UpdateState.VerificationFailed);
                var result = new byte[output.Length];
                Marshal.Copy(output.Bytes, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Bytes); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Bytes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(in DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(in DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
