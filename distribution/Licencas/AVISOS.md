# Licenças de terceiros

| Componente | Versão | Licença e atribuição |
| --- | --- | --- |
| Runtime .NET e Windows Desktop/WPF | 10.0.15 | MIT. Microsoft, .NET Foundation e colaboradores. Textos e avisos em `NET-*` e `WPF-*`. |
| CommunityToolkit.Mvvm | 8.4.0 | MIT. .NET Foundation e colaboradores. Texto e avisos em `CommunityToolkit-MVVM-*`. |
| Microsoft.Data.Sqlite e Microsoft.Data.Sqlite.Core | 10.0.15 | MIT. Copyright Microsoft Corporation. Texto MIT em `NET-LICENSE.txt`. |
| Microsoft.Extensions.DependencyInjection e Abstractions | 10.0.0 | MIT. Copyright Microsoft Corporation. Texto MIT em `NET-LICENSE.txt` e avisos em `Microsoft-Extensions-DI-*`. |
| System.Management | 10.0.14 | MIT. Copyright Microsoft Corporation. Texto MIT em `NET-LICENSE.txt` e aviso em `System-Management-*`. |
| SQLitePCLRaw (bundle, config, core e provider) | 3.0.5 | Apache-2.0. Copyright 2014–2026 SourceGear, LLC, conforme os metadados dos pacotes. Texto em `SQLitePCLRaw-LICENSE.txt`. |
| SQLite (`e_sqlite3.dll`) | 3.53.4 | Domínio público. Texto do pacote em `SQLite-NuGet-LICENSE.txt`. |
| YamlDotNet | 18.1.0 | MIT. Copyright Antoine Aubry e colaboradores. Texto em `YamlDotNet.LICENSE.txt`. |
| Código adaptado do BCUninstaller | 6.3.0 de referência | Apache-2.0. Marcin Szeniak (Klocman). Licença e aviso de adaptações junto ao executável. |
| Regras e enumeração da Limpeza adaptadas do BitBroom | v1.2.6, `7117bc6cb4b031289dc5b0f01bf312b4c201ce7f` | MIT. Copyright (c) 2026 BitBroom Contributors. Texto em `BitBroom.LICENSE.txt`. |
| Diagnóstico de catálogos WinGet adaptado do UniGetUI | `ff61c877c4f8d6052b3910722b430cc10025efe4` | MIT. Copyright (c) 2025 Martí Climent. Texto em `UniGetUI.LICENSE.txt`. |
| Leitura SMART adaptada do DiskScope | `fc4ac0e15813071625d633969d12a438dc48592c` | MIT. Copyright (c) 2026 Syntaxo. Texto em `DiskScope.LICENSE.txt`. |

Integração opcional com NVML por meio do driver NVIDIA instalado no Windows. O pacote portátil distribui somente o código de chamadas à API. Os binários e termos do driver pertencem à instalação NVIDIA. [Documentação NVML](https://docs.nvidia.com/deploy/nvml-api/latest/index.html)


[Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) fornece código adaptado de descoberta, desinstalação e resíduos. [BitBroom](https://github.com/pwnapplehat/BitBroom) fornece regras e técnicas adaptadas para analisar e proteger arquivos na Limpeza. [UniGetUI](https://github.com/Devolutions/UniGetUI) fornece a classificação de erros de catálogos WinGet adaptada e serviu de referência para apresentar fila e resultados. [DiskScope](https://github.com/UnstableNT/diskscope) fornece trechos adaptados para leitura SMART somente leitura de unidades ATA e NVMe. As ações de cópia de diagnóstico e log são código do WinCore Manager. As licenças acima se aplicam aos respectivos componentes.
