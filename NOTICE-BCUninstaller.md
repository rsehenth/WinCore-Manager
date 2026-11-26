# NOTICE — Componentes derivados do Bulk Crap Uninstaller

Este produto (WinCore Manager) inclui código adaptado do projeto
**Bulk Crap Uninstaller (BCUninstaller)**, de Marcin Szeniak (Klocman),
distribuído sob a **Apache License Version 2.0**.

- Projeto original: https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- Versão de referência: **6.3.0** (assemblies `UninstallTools.dll` 6.3.0.0,
  SHA-256 `3c82ca47f91d92eeadc34620a35d4e26d7acaa91338314bb2bba00eef4a0cf79`,
  analisados por inspeção estática)
- Texto completo da licença: [`LICENSE-BCUninstaller.txt`](LICENSE-BCUninstaller.txt)
- NOTICE original do projeto: incluído abaixo.

## Arquivos derivados (código portado e adaptado)

| Arquivo no WinCore Manager | Origem no BCUninstaller | Principais alterações |
|---|---|---|
| `src/WinCoreManager/Services/Uninstaller/UninstallCommandSplitter.cs` | `source/KlocTools/Tools/ProcessTools.cs` (`SeparateArgsFromCommand`) | Classe dedicada; mensagens pt-BR; método `TrySeparate` |
| `src/WinCoreManager/Services/Uninstaller/RegistryProgramInventory.cs` | `source/UninstallTools/Factory/RegistryFactory.cs` | Abstração `IUninstallRegistryView` p/ testes; leitura por `RegistryView` (64/32) em vez de `Wow6432Node`; deduplicação; nomes pt-BR |
| `src/WinCoreManager/Services/Uninstaller/UninstallPlanner.cs` | `source/UninstallTools/Uninstaller/UninstallManager.cs` | Planejamento separado da execução; fallback de shell **removido** (recusa segura); `cmd/cscript/wscript/mshta` recusados; MSI sempre com `REBOOT=ReallySuppress /norestart` |
| `src/WinCoreManager/Services/Uninstaller/UninstallExecution.cs` | `source/UninstallTools/Uninstaller/UninstallManager.cs` + `BulkUninstallTask` | Execução com `UseShellExecute=false`; cancelamento nunca encerra desinstalador ativo; lote estritamente sequencial; histórico integrado |
| `src/WinCoreManager/Services/Uninstaller/ResidueAnalysis.cs` | Conceitos de `source/UninstallTools/Junk` | Estrutura própria; análise somente leitura (pré-visualização) |
| `src/WinCoreManager/Services/Uninstaller/ResidueDetection.cs` | Conceitos de `UninstallTools/Junk` (`JunkManager`, `ConfidenceGenerators`, `SoftwareRegKeyScanner`, `UninstallerKeySearcher`, `ProgramFilesOrphans`) | Classificação determinística em 4 classes; proteção mais rígida que o BCU; Alta exige local registrado, nome compatível, conteúdo verificado e programa desinstalado; detecção não destrutiva |
| `src/WinCoreManager/Services/Uninstaller/ResidueSimilarity.cs` | `Klocman/Tools/Sift4.cs`, `Klocman/Tools/StringTools.cs` e `UninstallTools/Junk/Confidence/ConfidenceGenerators.cs` (Sift4: algoritmo de Siderite) | Apenas `SimplestDistance` é portado; nomes e documentação em pt-BR |
| `src/WinCoreManager/Services/Uninstaller/ResidueRegistry.cs` | Conceitos de `Junk/Containers/RegistryKeyJunk.cs` e `Junk/Finders/Registry/SoftwareRegKeyScanner.cs`; `MsiGuidPacker` portado de Klocman `MsiTools` | Leitura e escrita separadas; leitura estrita para backup; detecção de links do Registro; exclusão limitada a uma chave sem subchaves |
| `src/WinCoreManager/Services/Uninstaller/ResidueQuarantine.cs` | Conceito de backup de `Junk/Containers/RegistryKeyJunk.Backup` | Arquivos e pastas movidos para área controlada, sem cópia entre volumes; manifesto persistido; exportação `.reg` própria (sem `reg.exe`); snapshot JSON; recuperação supervisionada sem sobrescrita |
| `src/WinCoreManager/Services/Uninstaller/ResidueCleanup.cs` | Conceitos de `JunkManager`/`CleanUpResults` e `RegistryKeyJunk.Backup`/`Delete` | Plano revalidado por nova detecção e impressão digital; backup obrigatório; execução sequencial; cancelamento preserva itens concluídos; sem shell e sem tomada de posse de arquivos |
| `src/WinCoreManager/Models/UninstallerModels.cs` | Taxonomia `UninstallerType` | Enumeração renomeada para `ProgramKind` e simplificada |

## Componentes NÃO reutilizados

Interface gráfica do BCU (WPF/ObjectListView), `UninstallerAutomatizer` (automação
UI de desinstaladores), helpers externos (Steam/StoreApp/Oculus/WinUpdate helpers),
NBug, licenças de terceiros do bundle original. A taxonomia de tipos de
desinstalador (`UninstallerType`) foi reproduzida de forma simplificada.

## Declaração de modificações

Todos os arquivos derivados contêm cabeçalho apontando a origem, o copyright e a
licença, além do resumo das alterações exigidas pela Apache License 2.0, item 4(b).
A licença do WinCore Manager não é alterada por este NOTICE.

---

## NOTICE original do Bulk Crap Uninstaller (preservado)

Bulk Crap Uninstaller is a free open-source project hosted on GitHub. Marcin Szeniak is the project manager and lead developer.
The official project is hosted at https://github.com/Klocman/Bulk-Crap-Uninstaller

In case of issues, please update to the latest official release. Non-official releases are not supported.
If the issue presists, please post a bug report to https://github.com/Klocman/Bulk-Crap-Uninstaller/issues

If you wish to contribute, donate or otherwise help the project, please read the CONTRIBUTING file.
https://github.com/Klocman/Bulk-Crap-Uninstaller/blob/master/CONTRIBUTING.md
