English | [Português (Brasil)](docs/README.pt-BR.md)

# WinCore Manager

WinCore Manager is a Windows desktop utility for supervised maintenance and system management. **0.2.0-rc.4** is the current pre-release, with a redesigned interface, built with C#, WPF and .NET 10.

## Features

| Area | What it does |
| --- | --- |
| Dashboard and Drive Health | Shows system and maintenance status, recent activity and drive health. **More details** shows available read-only NVMe and ATA SMART evidence. |
| Updates | Finds WinGet updates, reviews the selected queue, shows progress and diagnostics per package, and offers supervised recovery of interrupted operations. Catalog diagnostics adapt code from UniGetUI. |
| Maintenance | Runs selected Windows tools, including SFC, DISM RestoreHealth, CHKDSK `/scan` and DNS cache flush. The detailed drive health view shows the measurements available for each disk. |
| Uninstaller | Lists and filters installed programs. Runs their uninstallers sequentially after confirmation. The **Windows apps** tab lists eligible optional apps, including supported current-user APPX packages. |
| Leftovers | Looks for file and Registry evidence associated with an uninstalled program. Eligible files can be quarantined. Eligible Registry keys require a verified backup and confirmation. |
| Startup | Lists Windows startup entries and allows changes to eligible Run values and shortcuts. |
| Cleanup and Advanced Space Analysis | BitBroom rules group temporary files and caches for confirmed cleanup. Space analysis scans selected drives or one folder, shows a live preview, map and rankings, and can confirm duplicates by SHA-256. |
| Tweaks | Changes supported power and File Explorer settings, provides undo where available and opens relevant Windows controls. |
| My Computer | Shows Windows and hardware inventory. |
| Sensors | Monitors CPU, RAM, GPU and drive activity. Shows Intel CPU Package temperature, expandable core readings, and available SATA and NVMe telemetry, with a link to Drive Health. |
| App Center | Checks for selected applications and global runtimes and links to official sources. |
| History | Keeps a searchable local record of operations and results, with configurable retention. |
| Settings and About | Configure light or dark theme, English (US) or Portuguese (Brazil), portable mode and history retention. View version and credits. |
| WinCore self-update | Checks for stable WinCore releases and can direct you to the official release page. The update and recovery tools are included in the portable package. |

## Interface

The [Dashboard](docs/assets/screenshots/wincore-manager-dashboard.png) and [Updates](docs/assets/screenshots/wincore-manager-updates.png) screenshots show this build with synthetic data. The app opens with the light theme; choose Settings, Appearance, Dark for the look in the screenshots. The shared components are described in the [design system (Portuguese)](docs/design/NEBULA-DESIGN-SYSTEM.md).

## Requirements and use

- **Run** on x64 Windows 10 or 11. The portable bundle includes the .NET 10 runtime. Install WinGet through Microsoft App Installer to use the Updates area.
- **Build** on x64 Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) version pinned in [global.json](global.json) and access to NuGet for restore.
- **Run the portable copy** by downloading the ZIP and `SHA256SUMS.txt` from the [release page](https://github.com/rsehenth/WinCore-Manager/releases/tag/v0.2.0-rc.4). Check the ZIP hash, extract `WinCoreManager-0.2.0-rc.4-win-x64-portable.zip` into a new writable local folder and open `WinCoreManager/WinCoreManager.exe`. Windows may show a SmartScreen prompt for this unsigned build.

### Build from the repository root

```powershell
dotnet restore WinCoreManager.sln
dotnet build WinCoreManager.sln -c Release --no-restore
dotnet test WinCoreManager.sln -c Release --no-build -- xUnit.ParallelizeTestCollections=false
dotnet run --project src/WinCoreManager/WinCoreManager.csproj -c Release --no-build
```

The Windows tests share WPF and language state. Run them in series. Three cross-volume cases require `WINCORE_QUARANTINE_SECOND_VOLUME` on a local NTFS volume different from `TEMP`.

To assemble the full Release package, including Update, Recovery, licenses, the ZIP and SHA-256 checksums, use Windows PowerShell from the root:

```powershell
.\tools\New-PortableRelease.ps1
```

Use `-PortableDirectory` and `-ReleaseDirectory` to build into new paths when outputs already exist. The script preserves existing installations and package files.

## Repository layout

| Path | Contents |
| --- | --- |
| `WinCoreManager.sln` | Root solution for the app, update helper and tests |
| `src/` | WPF app and shared Update/Recovery helper source |
| `tests/` | Automated tests |
| `tools/` | Packaging scripts and test and visual tools |
| `distribution/Licencas/` | Upstream license texts and notices |
| `docs/` | Portuguese README and documentation assets |
| `publish/portable/`, `artifacts/release/` | Local generated package and ZIP with checksums |

## Permissions and validation limits

Run as a standard user. Protected Windows tools and machine-wide actions can require validation or elevation. The WinGet workflow requires WinCore to remain non-elevated, while individual installers may request elevation. Updating accepts the selected package and source agreements. An external installer may keep running after a cancellation. Review a pending result before starting another package operation.

Drive health is read-only and depends on the drive, controller, driver, and access level. Missing SMART values remain unavailable. Optional APPX removal depends on the package identity, the current user and Windows policy. The supervised fallback for eligible whole Registry keys under `HKCU` is available when the kernel transaction manager returns `6801` in a non-elevated session. It requires a verified backup and can be interrupted after a partial removal. Hardware-specific SMART behavior, live APPX removal, and the non-atomic HKCU fallback across Windows configurations still need machine-level validation. The release checks used automated cases and a read-only startup.

Intel CPU Package uses a separately installed PawnIO 2.2 or later driver. Windows controls whether the current session can open it. The portable app starts with the caller's privileges. With the default PawnIO installation, reading the CPU temperature needs an elevated session; otherwise the app reports restricted access. The driver is not included in the package.

## Future updates and recovery

This pre-release is an unsigned package for manual installation. Production self-update requires a compatible stable release, publisher signatures and the corresponding trust identity. Until such a release is available, obtain a trusted package, verify its published checksum, and extract it into a separate directory. Close WinCore and use the [portable recovery and data-transfer guide](tasks/RECUPERACAO-RECOVERY.md) to copy the preferences and complete `Data` tree from a single installation if needed. The manual ZIP has a top-level `WinCoreManager/` directory. The signed automatic-update payload follows the updater's separate package format.

## Personal data

| Data | Location |
| --- | --- |
| Preferences | `settings.json` beside the executable in portable mode, or `%LOCALAPPDATA%\WinCoreManager` |
| SQLite history | `Data\history.db` beside the executable in portable mode, or `%LOCALAPPDATA%\WinCoreManager\history.db` |
| WinGet receipts and queue checkpoints | `%LOCALAPPDATA%\WinCoreManager`, including in portable mode |
| Quarantine | `Quarantine` under the active data directory. Legacy backups can remain in `%LOCALAPPDATA%\WinCoreManager\Quarantine`. |
| Startup backups and tweak journal | `%APPDATA%\WinCoreManager\StartupBackups`, `%LOCALAPPDATA%\WinCoreManager\StartupRunBackups` and `Tweaks`. Machine Run backups use `%ProgramFiles%\WinCoreManager.StartupBackups`. |

The portable package starts with an empty `Data/` and a generic portable-mode setting. Preserve any SQLite WAL and SHM sidecars with the database when backing up a closed copy.

## Credits and licenses

- [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) by Marcin Szeniak, Apache-2.0. [License](LICENSE-BCUninstaller.txt) and [notice](NOTICE-BCUninstaller.md).
- [BitBroom](https://github.com/pwnapplehat/BitBroom), MIT. [License](distribution/Licencas/BitBroom.LICENSE.txt).
- [UniGetUI](https://github.com/Devolutions/UniGetUI), MIT. [License](distribution/Licencas/UniGetUI.LICENSE.txt).
- [DiskScope](https://github.com/UnstableNT/diskscope), MIT. Sensors and Drive Health share the SMART reader. [License](distribution/Licencas/DiskScope.LICENSE.txt).
- .NET, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection, System.Management, System.CodeDom, SQLite, SQLitePCLRaw and YamlDotNet. Tests use xUnit and the .NET test SDK. [Third-party notices](THIRD-PARTY-NOTICES.md).

Created by [Lucas Sehnem](https://github.com/rsehenth). Original code is under the [MIT License](LICENSE). See the [contribution guide](CONTRIBUTING.md) and [security policy](SECURITY.md).
