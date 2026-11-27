# Third-party notices

The [MIT License](LICENSE) covers the original WinCore Manager code. It does not replace the licenses of the components listed here. Versions are those resolved at the time of writing; license data comes from each NuGet package's metadata and bundled files.

Full texts and upstream notices for redistributed components are in [`distribution/Licencas/`](distribution/Licencas/) and, for YamlDotNet, [`src/WinCoreManager/ThirdParty/`](src/WinCoreManager/ThirdParty/). `dotnet publish` copies them all into the package's `Licencas/` folder, together with this file and the MIT license as `Licencas/WinCoreManager-LICENSE.txt`. Paths linked here are repository paths.

## Adapted source code

| Component | Version / origin | License | Use | Obligations |
| --- | --- | --- | --- | --- |
| [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller), © 2017 Marcin Szeniak (Klocman) | Reference 6.3.0 | Apache-2.0 | Parts of the uninstaller and leftover services are ported or adapted | Keep file headers, state changes, include [the license](LICENSE-BCUninstaller.txt) and [NOTICE](NOTICE-BCUninstaller.md) |
| [BitBroom](https://github.com/pwnapplehat/BitBroom), Copyright (c) 2026 BitBroom Contributors | [v1.2.6, 7117bc6cb4b031289dc5b0f01bf312b4c201ce7f](https://github.com/pwnapplehat/BitBroom/tree/7117bc6cb4b031289dc5b0f01bf312b4c201ce7f) | MIT | Cleanup rules, metadata filtering and protected paths | Preserve the copyright and [full MIT license](distribution/Licencas/BitBroom.LICENSE.txt) |
| [UniGetUI](https://github.com/Devolutions/UniGetUI), Copyright (c) 2025 Martí Climent | [ff61c877c4f8d6052b3910722b430cc10025efe4](https://github.com/Devolutions/UniGetUI/tree/ff61c877c4f8d6052b3910722b430cc10025efe4) | MIT | WinGet listing exit-code and source-warning classification | Preserve the copyright and [full MIT license](distribution/Licencas/UniGetUI.LICENSE.txt) |

`NOTICE-BCUninstaller.md` lists every derived file. Those portions remain under Apache-2.0. It also credits Siderite for the Sift4 algorithm.

BitBroom's `Catalog/CategoryCatalog.cs` informed the selected rules in `CleanupCatalog.cs`. `Engine/FileSystemWalker.cs` and `Engine/PathGuard.cs` informed metadata-first enumeration and path exclusions in `CleanupFiles.cs`. The WinCore implementation retains its existing NTFS identity checks and handle-based deletion.

UniGetUI's [`WinGetCliHelper.cs`](https://github.com/Devolutions/UniGetUI/blob/ff61c877c4f8d6052b3910722b430cc10025efe4/src/UniGetUI.PackageEngine.Managers.WinGet/ClientHelpers/WinGetCliHelper.cs) supplied the listing error classification adapted in `src/WinCoreManager/Services/WinGetService.cs`. WinCore applies it to verified catalogs individually and recognizes Portuguese WinGet diagnostics. UniGetUI’s operation cards and history views informed the layout of the queue and results. The WinCore clipboard actions are original code.

## Components redistributed in a self-contained build

| Component | Version | License | Attribution / texts |
| --- | --- | --- | --- |
| .NET runtime and Windows Desktop (WPF), © .NET Foundation and contributors | 10.0.15 (portable builds) | MIT, with upstream third-party notices | `NET-*`, `WPF-*` |
| CommunityToolkit.Mvvm, © .NET Foundation and Contributors | 8.4.0 | MIT | `CommunityToolkit-MVVM-*` |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core, © Microsoft Corporation | 10.0.15 | MIT | `NET-LICENSE.txt` |
| Microsoft.Extensions.DependencyInjection and Abstractions, © Microsoft Corporation | 10.0.0 | MIT | `Microsoft-Extensions-DI-*` |
| System.Management and System.CodeDom, © Microsoft Corporation | 10.0.14 | MIT | `System-Management-*`, `System-CodeDom-*` |
| SQLitePCLRaw (bundle, config, core, provider), © SourceGear, LLC | 3.0.5 | Apache-2.0 | `SQLitePCLRaw-LICENSE.txt` |
| SQLite native library (`e_sqlite3.dll`, NuGet package `SQLite`) | 3.53.4 | Public domain | `SQLite-NuGet-LICENSE.txt` |
| YamlDotNet, © Antoine Aubry and contributors | 18.1.0 | MIT | `YamlDotNet.LICENSE.txt` |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) | 0.9.6 | MPL-2.0, with upstream third-party components | `LibreHardwareMonitorLib-LICENSE.txt`, `LibreHardwareMonitorLib-THIRD-PARTY-NOTICES.txt` |
| [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules/tree/0.1.6), copyright namazso and contributors | 0.1.6, signed modules embedded by LibreHardwareMonitor | LGPL-2.1-or-later | `PawnIO.Modules-LICENSE.txt` |
| [DiskInfoToolkit](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) and [RAMSPDToolkit-NDD](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) | 1.1.2 / 1.4.2 | MPL-2.0 | `DiskInfoToolkit-LICENSE.txt`, `RAMSPDToolkit-NDD-LICENSE.txt` |
| [BlackSharp.Core](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) | 1.0.7 | MPL-2.0 | `BlackSharp.Core-LICENSE.txt` |
| [HidSharp](https://www.nuget.org/packages/HidSharp/2.6.4), copyright James F. Bellinger | 2.6.4 | Apache-2.0 | `HidSharp-LICENSE.txt` |
| [Mono.Posix.NETStandard](https://www.nuget.org/packages/Mono.Posix.NETStandard/1.0.0) | 1.0.0 | MIT, with upstream third-party terms | `Mono.Posix.NETStandard-LICENSE.txt` |
| [System.IO.Ports](https://github.com/dotnet/dotnet/tree/c2435c3e0f46de784341ac3ed62863ce77e117b4) and matching native assets where applicable | 10.0.3 | MIT | `NET-LICENSE.txt`, `System-IO-Ports-THIRD-PARTY-NOTICES.txt` |

The monitoring assemblies are distributed as unmodified, separately replaceable DLLs. Their corresponding MPL source is available at the revisions linked above. Any distributed changes to MPL-covered files must be made available under MPL-2.0. The PawnIO module source, build scripts and dependencies are available at the linked 0.1.6 release. WinCore Manager's MIT terms permit replacing components and reverse engineering to debug modifications to the LGPL modules. The official driver enforces its own signed-module policy.

## Build and test dependencies (not redistributed)

| Component | Version | License |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage, Microsoft.TestPlatform.* | 17.14.1 | MIT |
| xunit and xunit.runner.visualstudio | 2.9.3 / 3.1.1 | Apache-2.0 |
| xunit.analyzers, xunit.abstractions | 1.18.0 / 2.0.3 | Apache-2.0 (upstream repository) |
| Newtonsoft.Json (transitive) | 13.0.3 | MIT |

## Assets, fonts and external tools

- `src/WinCoreManager/Assets/app.ico` is the project's own mark.
- Fonts and interface glyphs (Segoe UI, Segoe Fluent Icons, Segoe MDL2 Assets, Cascadia Mono, Consolas) are referenced from the user's Windows installation and are not redistributed.
- NVIDIA NVML is called through the driver installed on the user's machine. No NVIDIA binary is distributed.
- The optional [PawnIO driver and installer](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) are external to the package. The [driver license](https://github.com/namazso/PawnIO/blob/2.2.0/COPYING) is GPL-2.0-or-later with the upstream exception.
- [smartmontools 7.5](https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5) was used as an external diagnostic reference. The GPL-licensed executable and source are absent from the distributed application.
- WinGet, SFC, DISM, CHKDSK and other Windows tools are not distributed.
- Applications listed under App Center, and Microsoft Activation Scripts, are links to third-party sites. None of their code or binaries is included. Their names and trademarks belong to their owners.

## DiskScope — MIT

Copyright (c) 2026 Syntaxo. WindowsSmart.cs adapts the read-only NVMe SMART
query and the ATA/NVMe attribute decoding from SmartProbe.cs, AtaSmart.cs
and NvmeHealth.cs at [DiskScope fc4ac0e](https://github.com/UnstableNT/diskscope/tree/fc4ac0e15813071625d633969d12a438dc48592c).
WinCore uses its existing Windows storage device identity checks. It adds stricter
validation, keeps unsupported metrics unavailable, and selects explicit Windows
query operations. See distribution/Licencas/DiskScope.LICENSE.txt, also shipped
in the Licencas directory in the portable package. The Sensors page shares this attributed
reader for ATA and NVMe telemetry. Windows storage, WDDM and disk performance counters
come from the installed Windows platform. Advanced Space Analysis uses the
project scanner and the .NET SHA-256 API. WinDirStat served as a workflow reference.
