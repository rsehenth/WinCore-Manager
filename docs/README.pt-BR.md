[English](../README.md) | Português (Brasil)

# WinCore Manager

O WinCore Manager é um utilitário desktop para gerenciamento e manutenção supervisionada do Windows. **0.2.0-rc.4** é a pré-release atual, com a interface redesenhada, criada com C#, WPF e .NET 10.

## Recursos

| Área | O que faz |
| --- | --- |
| Painel e Saúde dos Discos | Mostra a situação do sistema, a atividade recente e a saúde das unidades. **Mais detalhes** exibe as evidências SMART de NVMe e ATA disponíveis, em modo somente leitura. |
| Atualizações | Encontra atualizações pelo WinGet, permite revisar a fila, mostra andamento e diagnóstico por pacote e oferece recuperação supervisionada de operações interrompidas. O diagnóstico de catálogo adapta código do UniGetUI. |
| Manutenção | Executa ferramentas do Windows sob demanda, como SFC, DISM RestoreHealth, CHKDSK `/scan` e limpeza do cache DNS. A área detalhada de saúde mostra as métricas disponíveis para cada disco. |
| Desinstalador | Lista e filtra programas instalados. Executa os desinstaladores em sequência após confirmação. A aba **Aplicativos do Windows** lista opcionais elegíveis, incluindo pacotes APPX compatíveis do usuário atual. |
| Resíduos | Procura evidências em arquivos e no Registro associadas a um programa desinstalado. Arquivos elegíveis podem ir para quarentena. Chaves elegíveis exigem backup verificado e confirmação. |
| Inicializar | Lista o que inicia com o Windows e permite alterar valores Run e atalhos elegíveis. |
| Limpeza e Análise Avançada de Espaço | Regras do BitBroom agrupam temporários e caches para limpeza confirmada. A análise de espaço escolhe discos ou pasta, mostra prévia, mapa e rankings e pode confirmar duplicados por SHA-256. |
| Tweaks | Ajusta opções compatíveis de energia e do Explorador, oferece desfazer quando disponível e abre os controles pertinentes do Windows. |
| Meu Computador | Mostra o inventário de hardware e do Windows. |
| Sensores | Monitora CPU, RAM, GPU e atividade de discos. Exibe CPU Package da Intel com opção de expandir as temperaturas por núcleo e mostra as leituras SATA e NVMe disponíveis. |
| Central de Apps | Detecta aplicativos e runtimes globais selecionados e oferece links para as fontes oficiais. |
| Histórico | Mantém um registro local pesquisável das operações e dos resultados, com retenção configurável. |
| Configurações e Sobre | Permitem escolher tema claro ou escuro, PT-BR ou EN-US, modo portátil e retenção do histórico. Exibem versão e créditos. |
| Autoatualização do WinCore | Consulta versões estáveis do WinCore e pode encaminhar à página oficial. As ferramentas Update e Recovery acompanham o portátil. |

## Interface

As capturas do [Painel](assets/screenshots/wincore-manager-dashboard.png) e de [Atualizações](assets/screenshots/wincore-manager-updates.png) mostram esta versão com dados de demonstração. O aplicativo abre no tema claro; para o visual das capturas, escolha Configurações, Aparência, Dark. Os componentes compartilhados estão no [design system](design/NEBULA-DESIGN-SYSTEM.md).

## Requisitos e execução

- **Executar** no Windows 10 ou 11 x64. O portátil inclui o runtime .NET 10. Para usar Atualizações, instale o WinGet pelo Instalador de Aplicativo da Microsoft.
- **Compilar** no Windows x64 com a versão do [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) fixada em [global.json](../global.json) e acesso ao NuGet para restaurar dependências.
- **Usar o portátil** baixando o ZIP e o `SHA256SUMS.txt` da [página do release](https://github.com/rsehenth/WinCore-Manager/releases/tag/v0.2.0-rc.4). Confira o hash do ZIP, extraia `WinCoreManager-0.2.0-rc.4-win-x64-portable.zip` em uma pasta local nova com permissão de gravação e abra `WinCoreManager/WinCoreManager.exe`. O Windows pode exibir um aviso do SmartScreen para esta compilação sem assinatura de código.

### Compilar pela raiz do repositório

```powershell
dotnet restore WinCoreManager.sln
dotnet build WinCoreManager.sln -c Release --no-restore
dotnet test WinCoreManager.sln -c Release --no-build -- xUnit.ParallelizeTestCollections=false
dotnet run --project src/WinCoreManager/WinCoreManager.csproj -c Release --no-build
```

Execute os testes em série. Eles compartilham o estado do WPF e do idioma. Três testes entre volumes exigem que `WINCORE_QUARANTINE_SECOND_VOLUME` aponte para um volume NTFS local diferente daquele de `TEMP`.

Para montar o pacote Release completo com Update, Recovery, licenças, ZIP e somas SHA-256, execute no Windows PowerShell a partir da raiz:

```powershell
.\tools\New-PortableRelease.ps1
```

Use `-PortableDirectory` e `-ReleaseDirectory` para compilar em novos caminhos se as saídas já existirem. O script preserva instalações e pacotes existentes.

## Estrutura do repositório

| Caminho | Conteúdo |
| --- | --- |
| `WinCoreManager.sln` | Solução da raiz para o aplicativo, o atualizador e os testes |
| `src/` | Aplicativo WPF e fontes compartilhadas por Update e Recovery |
| `tests/` | Testes automatizados |
| `tools/` | Empacotamento, ferramentas de teste e ferramentas visuais |
| `distribution/Licencas/` | Textos de licenças e avisos de terceiros |
| `docs/` | README em português e recursos da documentação |
| `publish/portable/`, `artifacts/release/` | Portátil e ZIP com hashes gerados localmente |

## Permissões e limites da validação

Execute como usuário padrão. Ferramentas protegidas do Windows e ações para toda a máquina podem exigir validação ou elevação. Para usar o WinGet, mantenha o WinCore sem elevação. Os instaladores podem pedir aprovação administrativa. Atualizar aceita os termos dos pacotes selecionados e das fontes. Um instalador externo pode continuar em execução após o cancelamento. Revise resultados pendentes antes de começar outra operação de pacote.

A saúde dos discos é somente leitura e depende do disco, controlador, driver e permissões. Métricas SMART ausentes ficam indisponíveis. A remoção de APPX opcionais depende da identidade do pacote, da conta atual e das políticas do Windows. O fallback supervisionado para chaves inteiras elegíveis em `HKCU` fica disponível quando o gerenciador de transações retorna `6801` em uma sessão sem elevação. Ele exige backup verificado e pode ser interrompido após uma remoção parcial. Leitura SMART em hardware variado, remoção APPX real e fallback HKCU não atômico em diferentes configurações do Windows ainda precisam de homologação em máquina real. Os testes do pacote incluíram casos automatizados e abertura do aplicativo para consulta.

CPU Package da Intel depende do PawnIO 2.2 ou posterior, instalado separadamente. O Windows controla o acesso ao driver por sessão. O portátil inicia com os privilégios de quem o abriu. Na instalação padrão do PawnIO, ler a temperatura da CPU exige uma sessão elevada; sem ela o aplicativo informa acesso restrito. O driver não acompanha o pacote.

## Futuras atualizações e recuperação

Esta pré-release é um pacote sem assinatura, destinado à instalação manual. A autoatualização em produção exige uma versão estável compatível, assinaturas do publicador e a identidade de confiança correspondente. Até existir uma versão assim, obtenha um pacote de origem confiável, confira o hash publicado e extraia em outra pasta. Feche o WinCore e siga o [guia de recuperação e transferência de dados](../tasks/RECUPERACAO-RECOVERY.md) se precisar copiar preferências e a árvore `Data` completa de uma instalação. O ZIP manual tem a pasta superior `WinCoreManager/`. O pacote assinado da autoatualização segue o formato próprio do atualizador.

## Dados pessoais

| Dado | Local |
| --- | --- |
| Preferências | `settings.json` ao lado do executável no modo portátil, ou em `%LOCALAPPDATA%\WinCoreManager` |
| Histórico SQLite | `Data\history.db` ao lado do executável no modo portátil, ou `%LOCALAPPDATA%\WinCoreManager\history.db` |
| Recibos e fila do WinGet | `%LOCALAPPDATA%\WinCoreManager`, inclusive no modo portátil |
| Quarentena | `Quarantine` na pasta de dados ativa. Backups anteriores podem estar em `%LOCALAPPDATA%\WinCoreManager\Quarantine`. |
| Backups de inicialização e diário de ajustes | `%APPDATA%\WinCoreManager\StartupBackups`, `%LOCALAPPDATA%\WinCoreManager\StartupRunBackups` e `Tweaks`. Valores Run da máquina usam `%ProgramFiles%\WinCoreManager.StartupBackups`. |

O portátil novo começa com a pasta `Data/` vazia e uma configuração genérica de modo portátil. Para copiar uma instalação fechada, preserve com o banco os arquivos auxiliares SQLite WAL e SHM que existirem.

## Créditos e licenças

- [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller), de Marcin Szeniak, Apache-2.0. [Licença](../LICENSE-BCUninstaller.txt) e [aviso](../NOTICE-BCUninstaller.md).
- [BitBroom](https://github.com/pwnapplehat/BitBroom), MIT. [Licença](../distribution/Licencas/BitBroom.LICENSE.txt).
- [UniGetUI](https://github.com/Devolutions/UniGetUI), MIT. [Licença](../distribution/Licencas/UniGetUI.LICENSE.txt).
- [DiskScope](https://github.com/UnstableNT/diskscope), MIT. O leitor SMART é compartilhado por Sensores e Saúde dos Discos. [Licença](../distribution/Licencas/DiskScope.LICENSE.txt).
- .NET, WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection, System.Management, System.CodeDom, SQLite, SQLitePCLRaw e YamlDotNet. Os testes usam xUnit e o SDK de testes .NET. [Avisos de terceiros](../THIRD-PARTY-NOTICES.md).

Criado por [Lucas Sehnem](https://github.com/rsehenth). O código original usa a [Licença MIT](../LICENSE). Consulte o [guia de contribuição](../CONTRIBUTING.md) e a [política de segurança](../SECURITY.md).
