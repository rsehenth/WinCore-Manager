using System.IO;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.Uninstaller;

internal sealed record OptionalAppDefinition(string Family, string Name, bool Suggested, string? StoreId = null, bool Review = false)
{
    internal bool CanSuggest => Suggested && !Review;
    internal string Key => Family.Split('_')[0];
    internal string DisplayName => LocalizationService.Resources.GetString($"Optional.Name.{Key}", LocalizationService.Current.Culture) ?? Name;
    internal string Impact => LocalizationService.Source($"Optional.Impact.{Key}");
    internal Uri? StorePage => StoreId is null ? null : new($"https://apps.microsoft.com/detail/{StoreId}");
}

internal enum OptionalAppState { Available, NotInstalled, Unverified, Protected, Ambiguous }
internal sealed record OptionalApp(OptionalAppDefinition Definition, OptionalAppState State, InstalledProgram? Program);
internal sealed record OptionalPackage(string Family, string FullName, string Publisher, string Version, bool Eligible);

internal static class OptionalWindowsApps
{
    private const string Microsoft = "_8wekyb3d8bbwe";
    internal static readonly OptionalAppDefinition OneDrive = new("Win32.OneDrive", "Microsoft OneDrive", false);

    internal static IReadOnlyList<OptionalAppDefinition> Catalog { get; } =
    [
        new("Microsoft.MicrosoftOfficeHub" + Microsoft, "Microsoft 365 Copilot (hub)", true),
        new("Microsoft.GetHelp" + Microsoft, "Get Help", true),
        new("Microsoft.BingNews" + Microsoft, "Microsoft News", true),
        new("Microsoft.BingWeather" + Microsoft, "MSN Weather", true),
        new("Microsoft.WindowsFeedbackHub" + Microsoft, "Feedback Hub", true),
        new("Microsoft.MicrosoftSolitaireCollection" + Microsoft, "Microsoft Solitaire Collection", true),
        new("Clipchamp.Clipchamp_yxz26nhyzhsrt", "Clipchamp", true, "9P1J8S7CCWWT", Review: true),
        new("Microsoft.Todos" + Microsoft, "Microsoft To Do", true, "9NBLGGH5R558", Review: true),
        new("Microsoft.WindowsMaps" + Microsoft, "Windows Maps", true, Review: true),
        new("Microsoft.Getstarted" + Microsoft, "Get Started", true),
        new("Microsoft.MSPaint" + Microsoft, "Paint 3D", true, Review: true),
        new("Microsoft.Microsoft3DViewer" + Microsoft, "3D Viewer", true),
        new("Microsoft.SkypeApp_kzf8qxf38zg5c", "Skype", true),
        new("Microsoft.Copilot" + Microsoft, "Microsoft Copilot", true),
        new("Microsoft.PowerAutomateDesktop" + Microsoft, "Power Automate", false),
        new("Microsoft.YourPhone" + Microsoft, "Phone Link", false, "9NMPJ99VJBWV"),
        new("Microsoft.ZuneMusic" + Microsoft, "Media Player", false),
        new("Microsoft.WindowsSoundRecorder" + Microsoft, "Sound Recorder", false),
        new("Microsoft.ScreenSketch" + Microsoft, "Snipping Tool", false, "9MZ95KL8MR0L"),
        new("Microsoft.WindowsCamera" + Microsoft, "Windows Camera", false, "9WZDNCRFJBBG"),
        new("Microsoft.Windows.Photos" + Microsoft, "Microsoft Photos", false),
        new("Microsoft.Paint" + Microsoft, "Paint", false, "9PCFS5B6T72H"),
        new("Microsoft.WindowsAlarms" + Microsoft, "Windows Clock", false, "9WZDNCRFJ3PR"),
        new("Microsoft.OutlookForWindows" + Microsoft, "Outlook for Windows", false),
        new("MicrosoftTeams" + Microsoft, "Microsoft Teams (personal)", false),
        new("Microsoft.MicrosoftStickyNotes" + Microsoft, "Microsoft Sticky Notes", false, "9NBLGGH4QGHW"),
        new("Microsoft.GamingApp" + Microsoft, "Xbox", false, "9MV0B5HZVK9Z"),
    ];

    internal static bool IsApprovedTarget(InstalledProgram program) =>
        program is { Kind: ProgramKind.StoreApp, IsSystemComponent: false, IsProtected: false, IsUpdate: false } &&
        program.OptionalPackageFamily is { } family && program.OptionalPackageFullName is { } full &&
        Catalog.Any(item => item.Family.Equals(family, StringComparison.OrdinalIgnoreCase)) && MatchesFullName(family, full);

    internal static bool MatchesFullName(string family, string full)
    {
        var parts = full.Split('_');
        return full.Length <= 255 && parts.Length == 5 && parts[3].Length == 0 &&
            parts[2] is "x64" or "x86" or "arm64" or "arm" or "neutral" &&
            Version.TryParse(parts[1], out var version) && version.Revision >= 0 &&
            parts[0].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-') &&
            parts[4].All(char.IsAsciiLetterOrDigit) &&
            $"{parts[0]}_{parts[4]}".Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<OptionalApp> Classify(IReadOnlyList<OptionalPackage>? packages,
        IReadOnlyList<InstalledProgram>? programs, IUninstallPlanner planner)
    {
        var rows = Catalog.Select(def =>
        {
            if (packages is null) return new OptionalApp(def, OptionalAppState.Unverified, null);
            var matches = packages.Where(p => p.Family.Equals(def.Family, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return new(def, OptionalAppState.NotInstalled, null);
            if (matches is not [var item]) return new(def, OptionalAppState.Ambiguous, null);

            var program = new InstalledProgram
            {
                Name = def.DisplayName, Publisher = item.Publisher, DisplayVersion = item.Version,
                Kind = ProgramKind.StoreApp, Origin = item.FullName, IsProtected = !item.Eligible,
                OptionalPackageFamily = item.Family, OptionalPackageFullName = item.FullName
            };
            return new(def, IsApprovedTarget(program) ? OptionalAppState.Available : OptionalAppState.Protected, program);
        }).ToList();

        if (programs is null) rows.Add(new(OneDrive, OptionalAppState.Unverified, null));
        else
        {
            var matches = programs.Where(IsOneDrive).GroupBy(p => p.RegistryPath, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.DistinctBy(p => (p.UninstallString, p.DisplayVersion))).ToArray();
            if (matches is [var program])
                rows.Add(new(OneDrive, IsSignedOneDrivePlan(planner.CreatePlan(program, silentPreferred: false))
                    ? OptionalAppState.Available : OptionalAppState.Protected, program));
            else
                rows.Add(new(OneDrive, matches.Length == 0 ? OptionalAppState.NotInstalled : OptionalAppState.Ambiguous, null));
        }
        return rows;
    }

    internal static bool IsOneDrive(InstalledProgram program) =>
        program.RegistryKeyName.Equals("OneDriveSetup.exe", StringComparison.OrdinalIgnoreCase) &&
        program.Name.Equals("Microsoft OneDrive", StringComparison.OrdinalIgnoreCase) &&
        program.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
        UninstallCommandSplitter.TrySeparate(program.UninstallString, out var command) &&
        Path.GetFileName(command.FileName).Equals("OneDriveSetup.exe", StringComparison.OrdinalIgnoreCase) &&
        (command.Arguments.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
         MachineUninstallPolicy.IsMachine(program) && command.Arguments.Equals("/uninstall /allusers", StringComparison.OrdinalIgnoreCase));

    private static bool IsSignedOneDrivePlan(UninstallPlan plan) => plan.IsExecutable &&
        plan.Method == UninstallMethod.Executable && StartupSignature.VerifiedPublisher(plan.ExecutablePath) == "Microsoft Corporation";
}

internal sealed class OptionalAppInventory(IProgramInventoryService inventory, IUninstallPlanner planner)
{
    internal async Task<IReadOnlyList<OptionalApp>> ReadAsync(CancellationToken token)
    {
        var packagesTask = OfficialAppx.ReadAsync(token);
        IReadOnlyList<InstalledProgram>? programs;
        try { programs = await inventory.GetInstalledProgramsAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        { programs = null; }
        return OptionalWindowsApps.Classify(await packagesTask.ConfigureAwait(false), programs, planner);
}
}
