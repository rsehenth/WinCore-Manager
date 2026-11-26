using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

switch (args[0])
{
    case "success":
        Console.WriteLine("Successfully installed");
        Console.Error.WriteLine("fixture stderr");
        return 0;
    case "stdin":
        Console.WriteLine("waiting for input");
        Console.WriteLine(Console.ReadLine() is null ? "stdin closed" : "stdin available");
        return 0;
    case "exit-code":
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    case "silent":
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "never":
        Console.WriteLine($"ready {Environment.ProcessId}");
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 42;
    case "brief":
        Console.WriteLine("ready");
        await Task.Delay(60);
        return 0;
    case "flood":
        Console.WriteLine("ready");
        for (var index = 0; index < 2_000; index++)
        {
            Console.WriteLine($"stdout {index}");
            Console.Error.WriteLine(new string('x', 512));
        }
        return 0;
    case "flood-live":
        for (var index = 0; index < 1_200; index++)
        {
            Console.WriteLine($"Downloading fixture update {index}");
            Console.Error.WriteLine($"fixture stderr {index}");
            await Task.Delay(25);
        }
        return 0;
    case "child":
    case "orphan":
        // Give the launcher time to enroll the root before reproducing heredity.
        await Task.Delay(250);
        using (var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!, ArgumentList = { "silent" }, UseShellExecute = false, CreateNoWindow = true,
        })!)
        {
            Console.WriteLine($"child {child.Id}");
            if (args[0] == "child") await child.WaitForExitAsync();
        }
        return 0;
    case "pipes-out":
    case "pipes-err":
        (args[0] == "pipes-out" ? Console.Out : Console.Error).Write(new string('x', 1024 * 1024));
        Console.WriteLine("ready");
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "escaped":
        await Task.Delay(250);
        Console.WriteLine($"escaped {FixtureProcess.StartBreakaway()}");
        return 0;
    case "quiet-delegate":
    case "quiet-controlled":
        await Task.Delay(500);
        Console.WriteLine(FixtureProcess.StartQuiet(args[1], args[2], args[0] == "quiet-delegate"));
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (!File.Exists(args[1])) await Task.Delay(20, wait.Token);
        return 0;
    case "wait-for-release":
        File.WriteAllText(args[1], Environment.ProcessId.ToString());
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try { while (!File.Exists(args[2])) await Task.Delay(20, wait.Token); }
            catch (OperationCanceledException) { }
        }
        return 0;
    case "receipt-worker":
        return await ReceiptWorker.RunAsync(args);
    default:
        return 9;
}

internal static class FixtureProcess
{
    internal static uint StartQuiet(string marker, string release, bool breakaway)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var path = Environment.ProcessPath!;
        var command = new StringBuilder($"\"{path}\" wait-for-release \"{marker}\" \"{release}\"");
        if (!CreateProcess(path, command, 0, 0, false, 0x08000000u | (breakaway ? 0x01000000u : 0),
                0, null, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception();
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return info.ProcessId;
    }

    internal static uint StartBreakaway()
    {
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100,
            Input = GetStdHandle(-10), Output = GetStdHandle(-11), Error = GetStdHandle(-12),
        };
        var path = Environment.ProcessPath!;
        if (!CreateProcess(path, new StringBuilder($"\"{path}\" silent"), 0, 0, true,
                0x01000000 | 0x08000000, 0, null, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception();
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return info.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedSize;
        internal nint ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder arguments, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
