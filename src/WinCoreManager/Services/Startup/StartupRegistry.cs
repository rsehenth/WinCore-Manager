using WinCoreManager.Localization;
using Microsoft.Win32;

namespace WinCoreManager.Services.Startup;

internal interface IStartupRegistry
{
    bool HasSeparateMachineViews { get; }
    IReadOnlyDictionary<string, object> Read(RegistryHive hive, RegistryView view, string path);
}

internal sealed record ExpandableStartupCommand(string Text);

internal sealed class WindowsStartupRegistry : IStartupRegistry
{
    public bool HasSeparateMachineViews => Environment.Is64BitOperatingSystem;

    public IReadOnlyDictionary<string, object> Read(RegistryHive hive, RegistryView view, string path)
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path, writable: false);
        if (key is null) return values;
        foreach (var name in key.GetValueNames())
        {
            try
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
                    values[name] = value is string command && key.GetValueKind(name) == RegistryValueKind.ExpandString
                        ? new ExpandableStartupCommand(command) : value;
            }
            catch (Exception ex) when (StartupErrors.Expected(ex))
            {
                values[name] = Unreadable;
            }
        }

        return values;
    }

    internal static object Unreadable { get; } = new();
}

internal sealed record StartupApproval(StartupStatus Status, string Token)
{
    private const string Prefix = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static IReadOnlyDictionary<string, object>? Read(IStartupRegistry registry, RegistryHive hive, string kind)
    {
        try
        {
            return registry.Read(hive, registry.HasSeparateMachineViews ? RegistryView.Registry64 : RegistryView.Default,
                Prefix + kind);
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return null; }
    }

    public static StartupApproval For(string name, IReadOnlyDictionary<string, object>? user,
        IReadOnlyDictionary<string, object>? machine)
    {
        var first = ReadValue(user, name);
        var second = ReadValue(machine, name);
        var status = (first.Status, second.Status) switch
        {
            (StartupStatus.Unknown, _) or (_, StartupStatus.Unknown) => StartupStatus.Unknown,
            (StartupStatus.Disabled, _) or (_, StartupStatus.Disabled) => StartupStatus.Disabled,
            _ => StartupStatus.Enabled,
        };
        return new(status, $"{first.Token}/{second.Token}");
    }

    private static StartupApproval ReadValue(IReadOnlyDictionary<string, object>? key, string name)
    {
        if (key is null) return new(StartupStatus.Unknown, "?");
        if (!key.TryGetValue(name, out var value)) return new(StartupStatus.Enabled, "-");
        if (value is not byte[] { Length: 12 } bytes) return new(StartupStatus.Unknown, "?");
        var state = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var status = state switch
        {
            2 or 6 => StartupStatus.Enabled,
            3 or 7 => StartupStatus.Disabled,
            _ => StartupStatus.Unknown,
        };
        return new(status, Convert.ToHexString(bytes));
    }

    internal static bool TryEnable(string token, out string enabled)
    {
        enabled = "";
        if (token.Length != 26 || !token.EndsWith("/-", StringComparison.Ordinal) ||
            !token.StartsWith("03000000", StringComparison.Ordinal) ||
            !token.AsSpan(8, 16).ToArray().All(char.IsAsciiHexDigit))
            return false;
        enabled = "020000000000000000000000/-";
        return true;
    }

    public static IReadOnlyDictionary<string, object> Empty { get; } = new Dictionary<string, object>();
}

internal sealed class StartupRegistrySource(IStartupRegistry registry, IStartupProductReader products, StartupPolicy policy,
    StartupRunSource? userRun = null, MachineStartupSources? machineRun = null)
    : IStartupSource
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string Label => LocalizationService.Source("Message.Registry");

    public void Read(List<StartupEntry> entries, List<string> warnings, CancellationToken token)
    {
        var native = registry.HasSeparateMachineViews ? RegistryView.Registry64 : RegistryView.Default;
        if (userRun is null) ReadRun(RegistryHive.CurrentUser, native, StartupSource.UserRun, "Run");
        else
        {
            try { userRun.Read(entries, warnings, token); }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { warnings.Add(LocalizationService.Source("Message.HKCURunIncompleteScan")); }
        }
        if (machineRun is null)
        {
            ReadRun(RegistryHive.LocalMachine, native, StartupSource.MachineRun64, "Run");
            if (registry.HasSeparateMachineViews)
                ReadRun(RegistryHive.LocalMachine, RegistryView.Registry32, StartupSource.MachineRun32, "Run32");
        }
        else
        {
            ReadMachine(machineRun.Native);
            if (registry.HasSeparateMachineViews) ReadMachine(machineRun.Wow);
        }

        void ReadMachine(StartupRunSource run)
        {
            try { run.Read(entries, warnings, token); }
            catch (Exception ex) when (StartupErrors.Expected(ex))
            { warnings.Add(LocalizationService.Source("Message.0IncompleteScan", run.Label)); }
        }

        void ReadRun(RegistryHive hive, RegistryView view, StartupSource source, string approvalKey)
        {
            var root = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var suffix = source == StartupSource.MachineRun32 ? " (32 bits)"
                : source == StartupSource.MachineRun64 && registry.HasSeparateMachineViews ? " (64 bits)" : "";
            try
            {
                var values = registry.Read(hive, view, RunKey);
                if (values.Count == 0) return;
                var user = StartupApproval.Read(registry, RegistryHive.CurrentUser, approvalKey);
                var machine = hive == RegistryHive.LocalMachine
                    ? StartupApproval.Read(registry, RegistryHive.LocalMachine, approvalKey) : StartupApproval.Empty;
                if (user is null || machine is null) warnings.Add(LocalizationService.Source("Message.01PartialStatus", root, suffix));
                foreach (var (name, command) in values)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var text = command switch { string literal => literal, ExpandableStartupCommand expansion => expansion.Text, _ => null };
                    if (text is null)
                    {
                        warnings.Add(LocalizationService.Source("Message.01UnreadableOrInvalidStartupValues", root, suffix));
                        continue;
                    }
                    var target = StartupPaths.Command(text, expand: command is ExpandableStartupCommand);
                    var product = products.Read(target.Path);
                    entries.Add(new($"{source}:{name}", name, product?.Publisher, source,
                        StartupApproval.For(name, user, machine).Status, $@"{root}\{RunKey}{suffix} · {name}",
                        target.Path, target.Arguments, target.Problem, policy.Recognized(name, product),
                        LocalizationService.Source("Message.RegistryEntriesAreReadOnly"),
                        OriginalCommand: target.Problem is null ? null : text));
                }
            }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { warnings.Add(LocalizationService.Source("Message.01IncompleteScan", root, suffix)); }
        }
    }
}

internal sealed record MachineStartupSources(StartupRunSource Native, StartupRunSource Wow);
