using System.ComponentModel;
using System.Diagnostics;
using System.Text;

// Sonda de diagnóstico WinGet: replica a chamada de WinGetService.DetectAsync (via ProcessRunner)
// e variações. Executa somente `winget --version` — nenhuma atualização ou modificação.
var alias = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Microsoft", "WindowsApps", "winget.exe");

Console.WriteLine($"Alias existe: {File.Exists(alias)} -> {alias}");
Console.WriteLine($"Processo elevado: {Environment.IsPrivilegedProcess}");
Console.WriteLine($"Tamanho do alias: {new FileInfo(alias).Length} bytes");
Console.WriteLine();

Probe("1) UseShellExecute=false + CreateNoWindow=true + redirect (igual ao ProcessRunner)",
    new ProcessStartInfo { FileName = alias, Arguments = "--version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 });

Probe("2) UseShellExecute=false + CreateNoWindow=false + redirect",
    new ProcessStartInfo { FileName = alias, Arguments = "--version", UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = true, RedirectStandardError = true });

Probe("3) UseShellExecute=false + sem redirect",
    new ProcessStartInfo { FileName = alias, Arguments = "--version", UseShellExecute = false, CreateNoWindow = true });

Probe("4) UseShellExecute=true (shell)",
    new ProcessStartInfo { FileName = alias, Arguments = "--version", UseShellExecute = true });

static void Probe(string label, ProcessStartInfo startInfo)
{
    Console.WriteLine($"--- {label}");
    try
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            Console.WriteLine("   Start() retornou false");
            return;
        }

        if (startInfo.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"   stdout: {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"   stderr: {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        if (process.WaitForExit(15000))
        {
            Console.WriteLine($"   ExitCode: {process.ExitCode}");
        }
        else
        {
            Console.WriteLine("   TIMEOUT 15s");
        }
    }
    catch (Exception ex)
    {
        if (ex is Win32Exception w32)
        {
            Console.WriteLine($"   Win32Exception: NativeErrorCode={w32.NativeErrorCode} (0x{w32.NativeErrorCode:X8}) Mensagem={w32.Message}");
        }
        else
        {
            Console.WriteLine($"   {ex.GetType().Name}: {ex.Message}");
        }
    }
}
