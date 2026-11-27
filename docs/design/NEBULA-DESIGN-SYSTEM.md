# Nebula — design system do WinCore Manager

Nome interno; o produto continua "WinCore Manager". Direção: azul-marinho profundo, contornos azul-acinzentados, ciano só em destaque, arte discreta. Autoridade visual: os três mockups aprovados (Painel, Atualizações, Limpeza).

## Onde mora

| Assunto | Arquivo |
| --- | --- |
| Cores e gradientes | `src/WinCoreManager/Themes/Tokens.{Dark,Light,HighContrast}.xaml`, **gerados** por `tools/New-ThemeTokens.py` |
| Estilos e templates | `Themes/Controls.xaml` |
| Ícones | `Themes/Icons.xaml` (incorporado por Controls.xaml) |
| Arte | `Assets/Nebula/*.png` (`Resource`) |
| Layout | `Helpers/SplitPanel.cs`, `Helpers/TileGrid.cs`, `Helpers/ArcGauge.cs` |

Para mudar uma cor, edite a tabela do script e rode `python -I tools/New-ThemeTokens.py`.

## Cores

| Papel | Chave | Dark | Light |
| --- | --- | --- | --- |
| Fundo | `WindowBgBrush` | #070F1E | #EEF3FA |
| Sidebar | `SidebarBgBrush` | #091426 | #E3EBF7 |
| Superfície | `CardBgBrush` | #0C1B30 | #FFFFFF |
| Superfície elevada | `PopupBgBrush`, `ButtonBgBrush` | #14263F | #FFFFFF, #EEF3FB |
| Contorno | `BorderBrush` / `BorderStrongBrush` | #1F3A5C / #2F5079 | #D3DFEF / #B3C4DD |
| Texto | `TextPrimaryBrush` / `TextSecondaryBrush` | #F1F6FF / #B2C2D8 | #0F1F36 / #44577A |
| Acento (com texto branco) | `AccentBrush` | #0A72E0 | #0A66CC |
| Ciano | `CyanBrush` | #28B9FF | #0A66CC |
| Sucesso / atenção / erro | `SuccessBrush` / `WarningBrush` / `DangerTextBrush` | #54E78F / #FFC46A / #FF8998 | #14703A / #8A5200 / #B3261E |

O acento de botão é um pouco mais escuro que o #087FF5 dos tokens do pacote para manter contraste 4,5:1 com texto branco. `ThemeTests` verifica o contraste WCAG das duas skins.

Gradientes: `PageGradientBrush`, `SidebarGradientBrush`, `CardGradientBrush`, `TableHeaderBrush`, `PrimaryGradientBrush`, `NavSelectedBrush`, `HeroBrush`, `Tile{Blue,Purple,Amber,Cyan}Brush`. No alto contraste cada um vira uma cor do sistema e `ArtOpacity` é 0.

## Medidas e tipografia

- Segoe UI Variable com reserva em Segoe UI. Nenhuma fonte é distribuída.
- Título de página 30 DIP bold; subtítulo 14; seção 16 semibold; corpo 14; legenda 12.
- Margem de página 28,22,28,24; intervalo entre blocos 12 a 14; padding de cartão 18.
- Raios: checkbox 4, controles 9, tiles 10, cartões 13.
- Botão e campo: 36 DIP de altura mínima. Linha de tabela de atualizações: 46 (38 em janela baixa). Cabeçalho de tabela: 40.

## Componentes

| Componente | Como usar |
| --- | --- |
| Cabeçalho de página | `HeaderedContentControl` com `Style=PageHeader`, `Tag={StaticResource Icon.X}`, `Header` = título + subtítulo, `Content` = ações. `Tag` nulo esconde o bloco do ícone. |
| Cartão | `CardBorder`; resumo numérico: `StatCard`; painel com arte: `HeroCard` (azul-marinho nas duas skins, texto `OnHero*`). |
| Botões | `AppButton` (padrão), `PrimaryButton`, `GhostButton`, `DangerButton`, `DangerOutlineButton`. |
| Campos | `TextBox`, `ComboBox`, `CheckBox`, `RadioButton`, `ToggleSwitch` (estilos implícitos ou nomeados). |
| Dados | `DataGrid` (virtualização preservada), `TabControl`, `Expander`, `ListBox`, `TreeView`, `ProgressBar`. |
| Estados | `InfoBanner`, `WarningBanner`, `DangerBanner`, `SuccessBanner`, `*Badge`, `EmptyStateText`. |
| Navegação | `NavRadioButton` / `NavLinkButton`; `Tag` recebe a geometria do ícone. |
| Ícone | `<Path Data="{StaticResource Icon.X}" Style="{StaticResource LineIcon}"/>`; a cor vem de `TextElement.Foreground`. |

## Responsividade

- `SplitPanel`: duas colunas acima de `Breakpoint`, empilhado abaixo.
- `TileGrid`: quantas colunas de pelo menos `MinItemWidth` couberem.
- Sidebar: 212 DIP abaixo de 1000 de largura, 248 até 1439, 280 a partir de 1440. Montanhas só com altura ≥ 940; assinatura da marca some abaixo de 640.
- Cartões de resumo somem em janelas baixas (Atualizações < 820, Inicializar < 700, Desinstalador < 800 de altura) para preservar a tabela.

## Regras para novas páginas

1. Comece pelo `PageHeader`; agrupe com cartões; uma ação primária por bloco.
2. Nenhuma cor literal em página ou em `Controls.xaml` (há teste). Use `DynamicResource`.
3. Nunca mostre número que não veio de um serviço. Sem leitura: "—" e o estado real.
4. Estado nunca só por cor: acompanhe de glifo e texto.
5. Vida útil restante de SSD não é espaço ocupado; rotule as duas.
6. Sem BlurEffect, sem animação contínua de brilho. Sombra apenas em popups.
7. Texto novo entra em `Strings.resx` e `Strings.en-US.resx`. Texto com argumentos precisa de um prefixo de chave que o `PresentationText` reconheça.
8. Arte é decoração: `IsHitTestVisible="False"` e nenhum texto dentro do bitmap.
