using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

switch (args[0])
{
    case "success":
        Console.WriteLine("Successfully installed");
        Console.Error.WriteLine("fixture stderr");
        return 0;
    case "success-with-reboot-warning":
        Console.WriteLine("Restart your PC to finish installation.");
        Console.WriteLine("Successfully installed");
        return 0;
    case "stdin":
        Console.WriteLine("waiting for input");
        Console.WriteLine(Console.ReadLine() is null ? "stdin closed" : "stdin available");
        return 0;
    case "exit-code":
        return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    case "update-stages":
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Downloading synthetic payload");
        Console.WriteLine("40.0 MB / 200 MB");
        await Task.Delay(700);
        Console.WriteLine("85.4 MB / 200 MB");
        await Task.Delay(650);
        Console.WriteLine("Successfully verified installer hash");
        Console.WriteLine("Starting package install...");
        await Task.Delay(650);
        var code = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine(code == 0 ? "Synthetic installation completed" : $"Installer failed with exit code: {code}");
        return code;
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
    case "download-progress":
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Downloading synthetic payload");
        Console.Write("\r\u001b[32m██▒ 40.0 MB / 200 MB\u001b[0m\r");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Console.Write("\r███▒ 85.4 MB / 200 MB\r");
        Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(15));
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
    case "uninstall-fixture":
        return await UninstallFixture.RunAsync(args);
    default:
        return 9;
}

internal static class UninstallFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var folder = AppContext.BaseDirectory;
        var installed = Path.Combine(folder, "synthetic-registration");
        // The test owns this marker. This executable cannot modify a real uninstall key or app.
        if (!File.Exists(installed)) return 1605;
        var started = Path.Combine(folder, "synthetic-started");
        var release = Path.Combine(folder, "synthetic-release");
        if (args[1] == "delegated")
        {
            await Task.Delay(500);
            FixtureProcess.StartQuiet(started, release, breakaway: false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(started)) await Task.Delay(20, deadline.Token);
            File.Delete(installed);
            return 0;
        }
        if (args[1] == "interactive")
        {
            File.WriteAllText(started, Environment.ProcessId.ToString());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(release)) await Task.Delay(20, deadline.Token);
        }
        if (args[1] is "remove" or "interactive") File.Delete(installed);
        return args[1] switch { "failure" => 23, "reboot" => 3010, "installer-cancel" => 1602, _ => 0 };
    }
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
