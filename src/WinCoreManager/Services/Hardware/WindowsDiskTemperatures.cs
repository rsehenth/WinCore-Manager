using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Hardware;

internal sealed record DiskTemperatureResult(ReadingState State, IReadOnlyList<(ushort Index, short Celsius)> Values);
internal interface IDiskTemperatures { DiskTemperatureResult Read(uint number, string? privateSerial); }

internal sealed unsafe class WindowsDiskTemperatures : IDiskTemperatures
{
    public const string Source = "Windows IOCTL_STORAGE_QUERY_PROPERTY";
    private const uint QueryProperty = 0x002D1400;
    private const uint DeviceDescriptor = 0;
    private const uint DeviceTemperature = 52;

    public DiskTemperatureResult Read(uint number, string? privateSerial)
    {
        if (privateSerial is null) return new(ReadingState.NoReading, []);
        using var disk = OpenQueryHandle(number);
        if (disk.IsInvalid) return new(Error(Marshal.GetLastPInvokeError()), []);
        var status = VerifyIdentity(disk, privateSerial);
        if (status != ReadingState.Available) return new(status, []);
        Span<byte> buffer = stackalloc byte[4096];
        status = Query(disk, DeviceTemperature, buffer, out var length);
        if (status != ReadingState.Available) return new(status, []);
        return ParseTemperatures(buffer[..length]);
    }

    // A query handle requests zero desired access and supports only the read-only queries used here.
    internal static SafeFileHandle OpenQueryHandle(uint number) =>
        CreateFile($@"\\.\PhysicalDrive{number}", 0, 3, 0, 3, 0, 0);

    internal static ReadingState VerifyIdentity(SafeFileHandle disk, string privateSerial)
    {
        Span<byte> buffer = stackalloc byte[4096];
        var status = Query(disk, DeviceDescriptor, buffer, out var length);
        if (status != ReadingState.Available) return status;
        return string.Equals(ReadSerial(buffer[..length]), privateSerial, StringComparison.OrdinalIgnoreCase)
            ? ReadingState.Available : ReadingState.NoReading;
    }

    private static string? ReadSerial(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 36) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]);
        if (size > buffer.Length || offset < 36 || offset >= size) return null;
        var field = buffer[(int)offset..(int)size];
        var end = field.IndexOf((byte)0);
        return end > 0 ? InventoryText.Clean(Encoding.ASCII.GetString(field[..end])) : null;
    }

    internal static DiskTemperatureResult ParseTemperatures(ReadOnlySpan<byte> buffer)
    {
        const int header = 24;
        const int entry = 16;
        if (buffer.Length < header) return new(ReadingState.Error, []);
        var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(buffer[12..]);
        if (descriptorSize > buffer.Length || header + count * entry > descriptorSize)
            return new(ReadingState.Error, []);
        var values = new List<(ushort Index, short Celsius)>();
        for (var n = 0; n < count; n++)
        {
            var span = buffer[(header + n * entry)..];
            var index = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var value = BinaryPrimitives.ReadInt16LittleEndian(span[2..]);
            if (value is >= -273 and < short.MaxValue) values.Add((index, value));
        }
        var unique = values.GroupBy(value => value.Index).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        return new(unique.Length > 0 ? ReadingState.Available : ReadingState.NoReading, unique);
    }

    internal static ReadingState Query(SafeFileHandle disk, uint property, Span<byte> buffer, out int length)
    {
        // STORAGE_PROPERTY_QUERY with PropertyStandardQuery and a zeroed AdditionalParameters.
        uint* request = stackalloc uint[3] { property, 0, 0 };
        fixed (byte* output = buffer)
        {
            if (!DeviceIoControl(disk, QueryProperty, request, 12, output, (uint)buffer.Length, out var written, 0))
            {
                length = 0;
                return Error(Marshal.GetLastPInvokeError());
            }
            length = written <= buffer.Length ? (int)written : 0;
            return length > 0 ? ReadingState.Available : ReadingState.Error;
        }
    }

    internal static ReadingState Error(int error) => error switch
    {
        5 or 1314 => ReadingState.Restricted,
        1 or 50 => ReadingState.Unsupported,
        2 or 3 or 21 or 1167 => ReadingState.NoReading,
        _ => ReadingState.Error
    };

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputLength,
        void* output, uint outputLength, out uint written, nint overlapped);
}
