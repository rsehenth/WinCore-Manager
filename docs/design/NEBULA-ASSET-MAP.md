# Nebula — mapa de assets

Origem: pacote `WinCore-Nebula-Assets`, fornecido pelo proprietário. Os recortes são pixels dos mockups aprovados (`04-guia/origem-recortes.json`); as ilustrações e ícones são reconstruções do pacote. Total de arte incorporada: **353.226 bytes**.

## Incorporados (WPF `Resource`, `src/WinCoreManager/Assets/Nebula/`)

| Asset original | Onde é usado | Recurso WPF (`pack://…/Assets/Nebula/`) | Tamanho | Observações |
| --- | --- | --- | --- | --- |
| `arte/prontos-para-compor/montanhas-painel.png` | Rodapé da sidebar | `montanhas-painel.png` | 278×169, 57.110 B | `ArtOpacity`; só com altura ≥ 940 DIP |
| `arte/prontos-para-compor/banner-bode-e-montanhas.png` | Cabeçalho do Painel e painel de identidade do Sobre (mascote bode) | `banner-bode-e-montanhas.png` | 760×103, 109.334 B | Máscara de opacidade suaviza as bordas |
| `arte/prontos-para-compor/ondas-windows-azuis.png` | Cartão "Windows e Ativação" | `ondas-windows-azuis.png` | 230×169, 58.740 B | Decorativo |
| `arte/prontos-para-compor/reciclagem-3d.png` | Estimativa da seleção na Limpeza | `reciclagem-3d.png` | 310×245, 99.510 B | Máscara radial; sempre sobre `HeroBrush` |
| `ilustracoes/empty-history.png` | Histórico vazio | `empty-history.png` | 264×188, 5.560 B | |
| `ilustracoes/hdd.png` | Saúde dos Discos | `hdd.png` | 264×188, 7.398 B | Só quando o disco informa tipo HDD |
| `ilustracoes/ssd.png` | Saúde dos Discos | `ssd.png` | 264×188, 6.856 B | Tipos SSD e SSD SATA |
| `ilustracoes/nvme.png` | Saúde dos Discos | `nvme.png` | 264×188, 8.718 B | Tipo NVMe |

Nenhuma imagem é esticada (todas `Stretch=Uniform`). Nenhum mockup ou captura vai para o pacote.

## Vetores

`Themes/Icons.xaml` traz 27 geometrias adaptadas de `03-wpf/WinCore.Icons.xaml` (`Chart` e `Sensor` redesenhados). 26 estão em uso; `Icon.Warning` está incluído e ainda sem uso. Os tokens de `04-guia/design-tokens.json` e `WinCore.Nebula.Dark.xaml` foram convertidos para as chaves que o aplicativo já usava. `WinCore.Components.xaml` serviu só de referência.

## Não utilizados

| Asset | Motivo |
| --- | --- |
| `icones/png` e `icones/svg` (7 cores) | Substituídos por geometrias que herdam a cor do tema |
| `logo-w-original`, `logo-w-glow`, `logo-concept` | O proprietário forneceu um logo próprio (`logo.png`, bode), convertido para `Assets/app.ico` |
| `mascote-bode` | O banner já traz o mascote; o recorte não é isolado |
| `montanhas-atualizacoes`, `montanhas-limpeza` | Uma única variação na sidebar, por consistência |
| `tile-*`, `status-saude` | Desenhados em XAML; o selo de saúde depende do estado real |
| `arte/header-wave`, `hero-diagonal`, `sidebar-base` | Gradientes XAML cumprem o papel |
| `arte/recortes-originais/*` | A versão "prontos para compor" foi suficiente |
| `05-previews`, `01-referencias` | Material de referência |

Ícones de programas instalados e logotipos de navegadores não foram inventados: a lista de limpeza usa ícones genéricos por grupo.
