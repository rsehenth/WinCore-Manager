using System.Security.Cryptography;
using WinCoreManager.Services;

internal static class ReceiptWorker
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        var root = arguments[2];
        var store = new WinGetInterruptionStore(root, () => "100");
        var path = Path.Combine(root, "winget-update.json");
        switch (arguments[1])
        {
            case "compete":
                File.WriteAllText(Path.Combine(root, arguments[3]), "1");
                await WaitAsync(Path.Combine(root, "race-start"));
                try
                {
                    using var winner = store.BeginExternal(PackageChangeKind.Uninstall, arguments[3]);
                    await WaitAsync(Path.Combine(root, "race-finish"));
                    return 0;
                }
                catch (IOException) { return 3; }
            case "contend":
                try { using var unexpected = store.BeginExternal(PackageChangeKind.Maintenance, "Contender"); }
                catch (IOException) { return store.HasReceipt ? 0 : 4; }
                return 5;
            case "abandon":
            case "fail-identity":
                using (var receipt = store.BeginExternal(PackageChangeKind.Maintenance, "Synthetic maintenance"))
                {
                    var fixture = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                    fixture.ArgumentList.Add("wait-for-release");
                    fixture.ArgumentList.Add(Path.Combine(root, "child"));
                    fixture.ArgumentList.Add(Path.Combine(root, "release"));
                    using var child = System.Diagnostics.Process.Start(fixture)!;
                    var control = new WinGetProcessControl { PersistRoot = receipt.RememberRoot };
                    if (arguments[1] == "fail-identity")
                    {
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) control.CaptureRoot(child);
                        if (control.RootIdentity is null || control.IdentityPersistenceError is null) return 6;
                    }
                    await WaitAsync(Path.Combine(root, "child"));
                    File.WriteAllText(Path.Combine(root, "ready"),
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WinGetInterruptionStore).Assembly.Location))));
                    await WaitAsync(Path.Combine(root, "depart"));
                    Environment.Exit(0); // Deliberately bypass Dispose and process observation.
                }
                return 7;
            default:
                return 8;
        }
    }

    private static async Task WaitAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(20, timeout.Token);
    }
}
