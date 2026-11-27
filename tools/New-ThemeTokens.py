# Gera Themes/Tokens.{Dark,Light,HighContrast}.xaml a partir de uma tabela, para as três skins não divergirem.
# Uso (na raiz do repositório): python -I tools/New-ThemeTokens.py
import pathlib

THEMES = pathlib.Path(__file__).resolve().parent.parent / "src" / "WinCoreManager" / "Themes"


def sys_color(name):
    return "{DynamicResource {x:Static SystemColors.%sColorKey}}" % name


# (chave, dark, light, alto contraste); strings soltas viram comentários de seção.
SOLID = [
    "superfícies",
    ("SidebarBgBrush", "#091426", "#E3EBF7", "Window"),
    ("WindowBgBrush", "#070F1E", "#EEF3FA", "Window"),
    ("CardBgBrush", "#0C1B30", "#FFFFFF", "Window"),
    ("CardHoverBrush", "#122541", "#F6F9FD", "Control"),
    ("SubtleBgBrush", "#0F2138", "#F3F7FC", "Window"),
    ("InsetBgBrush", "#081324", "#E9F0FA", "Window"),
    ("PopupBgBrush", "#14263F", "#FFFFFF", "Window"),
    ("ButtonBgBrush", "#14263F", "#EEF3FB", "Control"),
    ("RowHoverBrush", "#132844", "#EAF2FD", "Control"),
    ("RowAltBrush", "#06FFFFFF", "#05000000", "Window"),
    ("HoverLayerBrush", "#1A28B9FF", "#120A66CC", "Control"),
    "contornos",
    ("BorderBrush", "#1F3A5C", "#D3DFEF", "WindowText"),
    ("BorderStrongBrush", "#2F5079", "#B3C4DD", "WindowText"),
    ("BorderControlBrush", "#5B7BA3", "#5F7392", "WindowText"),
    ("GridLineBrush", "#1A314F", "#E2E9F4", "WindowText"),
    ("ScrollThumbBrush", "#5B7BA3", "#6E819F", "WindowText"),
    ("ScrollThumbHoverBrush", "#7395BF", "#5F7392", "Highlight"),
    ("ScrollThumbDragBrush", "#8AAFDB", "#4B5E7C", "Highlight"),
    ("ToggleOffBrush", "#2A3C50", "#CBD6E6", "GrayText"),
    "texto",
    ("TextPrimaryBrush", "#F1F6FF", "#0F1F36", "WindowText"),
    ("TextSecondaryBrush", "#B2C2D8", "#44577A", "WindowText"),
    ("TextMutedBrush", "#93A6C2", "#4F6283", "WindowText"),
    ("TextDisabledBrush", "#5A6B85", "#8E9DB5", "GrayText"),
    "acento",
    ("AccentBrush", "#0A72E0", "#0A66CC", "Highlight"),
    ("AccentHoverBrush", "#1481F0", "#0857AE", "Highlight"),
    ("AccentPressedBrush", "#0860BF", "#064A93", "Highlight"),
    ("OnAccentBrush", "#FFFFFF", "#FFFFFF", "HighlightText"),
    ("AccentTextBrush", "#5CC4FF", "#0857AE", "WindowText"),
    ("AccentSubtleBrush", "#12375C", "#DDEBFD", "Window"),
    ("AccentSubtleHoverBrush", "#17436E", "#CFE1FB", "Window"),
    ("AccentBorderBrush", "#2A6FB0", "#9EC2EF", "Highlight"),
    ("RowSelectedBrush", "#113C69", "#DDEBFD", "Window"),
    "estados",
    ("SuccessBrush", "#54E78F", "#14703A", "WindowText"),
    ("SuccessSubtleBrush", "#0B352C", "#E2F4E9", "Window"),
    ("WarningBrush", "#FFC46A", "#8A5200", "WindowText"),
    ("WarningSubtleBrush", "#3B2C19", "#FCF0DC", "Window"),
    ("DangerBrush", "#C63D4F", "#C0392B", "Highlight"),
    ("DangerTextBrush", "#FF8998", "#B3261E", "WindowText"),
    ("DangerBorderBrush", "#8C3F52", "#E5AAA6", "WindowText"),
    ("DangerSubtleBrush", "#3E2534", "#FBE9EC", "Window"),
    ("DangerHoverBrush", "#A93243", "#A22F23", "Highlight"),
    ("InfoSubtleBrush", "#10294A", "#E3EFFD", "Window"),
    ("PurpleBrush", "#B79CFF", "#5B3FC4", "WindowText"),
    ("CyanBrush", "#28B9FF", "#0A66CC", "WindowText"),
    ("GlowBorderBrush", "#2F8FE8", "#4C94E6", "Highlight"),
    "painéis de arte: azul-marinho nas duas skins para os recortes comporem sem moldura",
    ("OnHeroBrush", "#F1F6FF", "#F1F6FF", "WindowText"),
    ("OnHeroMutedBrush", "#B2C2D8", "#C5D4EA", "WindowText"),
    ("OnHeroAccentBrush", "#5CC4FF", "#7CCFFF", "WindowText"),
    ("OnPrimaryBrush", "#FFFFFF", "#FFFFFF", "HighlightText"),
]

# (chave, paradas dark, paradas light, alto contraste, "inicio|fim")
GRADIENTS = [
    ("PageGradientBrush", "#0B1C36 0,#070F1E 0.3,#070F1E 1", "#F8FBFF 0,#EEF3FA 0.3,#EEF3FA 1", "Window", "0.6,0|0.4,1"),
    ("SidebarGradientBrush", "#0C1C34 0,#091426 0.6,#07101F 1", "#E9F0FA 0,#E3EBF7 0.6,#DCE6F5 1", "Window", "0,0|0,1"),
    ("CardGradientBrush", "#102441 0,#0B192E 1", "#FFFFFF 0,#FAFCFF 1", "Window", "0,0|1,1"),
    ("TableHeaderBrush", "#17304F 0,#142849 1", "#EBF2FC 0,#E3ECF9 1", "Window", "0,0|0,1"),
    ("PrimaryGradientBrush", "#0A74E6 0,#0852B8 1", "#0A6BD6 0,#0750AD 1", "Highlight", "0,0|1,1"),
    ("NavSelectedBrush", "#0A74E6 0,#0B4A9E 0.55,#0A2F66 1", "#0A6BD6 0,#0857AE 1", "Highlight", "0,0|1,0.4"),
    ("HeroBrush", "#12315A 0,#0A1B33 0.55,#081428 1", "#14365F 0,#0D2443 0.55,#0A1B33 1", "Window", "0,0|1,1"),
    ("TileBlueBrush", "#11365F 0,#0C1F3A 1", "#E3EFFD 0,#F5F9FF 1", "Window", "0,0|1,1"),
    ("TilePurpleBrush", "#2A2058 0,#141635 1", "#ECE7FB 0,#F8F6FE 1", "Window", "0,0|1,1"),
    ("TileAmberBrush", "#3B2A18 0,#161B29 1", "#FCF0DC 0,#FEF9F0 1", "Window", "0,0|1,1"),
    ("TileCyanBrush", "#0E3A57 0,#0B1F39 1", "#DFF3FE 0,#F4FBFF 1", "Window", "0,0|1,1"),
]

# (tipo, chave, dark, light, alto contraste)
RAW = [
    ("Color", "AccentColor", "#28B9FF", "#0A66CC", "#FFFFFFFF"),
    ("Color", "AccentColorClear", "#0028B9FF", "#000A66CC", "#00000000"),
    ("Color", "ShadowColor", "#000000", "#0F1F36", "#00000000"),
    ("sys:Double", "PopupShadowOpacity", "0.5", "0.16", "0"),
    ("sys:Double", "ArtOpacity", "1", "0.3", "0"),
]

NOTES = {
    "Dark": "WinCore Manager — skin Nebula Dark. Gerado por tools/New-ThemeTokens.py; edite a tabela, não este arquivo.",
    "Light": "WinCore Manager — skin Nebula Light. Gerado por tools/New-ThemeTokens.py; edite a tabela, não este arquivo.",
    "HighContrast": "Cores do esquema de contraste do Windows. Gerado por tools/New-ThemeTokens.py; edite a tabela, não este arquivo.",
}

for index, theme in enumerate(["Dark", "Light", "HighContrast"]):
    contrast = theme == "HighContrast"
    out = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
           '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
           '                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">',
           f"    <!-- {NOTES[theme]} -->"]
    for entry in SOLID:
        if isinstance(entry, str):
            out += ["", f"    <!-- {entry} -->"]
            continue
        key, dark, light, system = entry
        value = (dark, light, sys_color(system))[index]
        out.append(f'    <SolidColorBrush x:Key="{key}" Color="{value}" />')
    out += ["", "    <!-- gradientes prontos: Controls.xaml e as páginas não declaram cor -->"]
    for key, dark, light, system, points in GRADIENTS:
        if contrast:
            out.append(f'    <SolidColorBrush x:Key="{key}" Color="{sys_color(system)}" />')
            continue
        start, end = points.split("|")
        out.append(f'    <LinearGradientBrush x:Key="{key}" StartPoint="{start}" EndPoint="{end}">')
        for stop in (dark, light)[index].split(","):
            color, offset = stop.split()
            out.append(f'        <GradientStop Color="{color}" Offset="{offset}" />')
        out.append("    </LinearGradientBrush>")
    out += ["", "    <!-- cores brutas, sombra de popup e opacidade da arte decorativa -->"]
    for kind, key, dark, light, high in RAW:
        out.append(f'    <{kind} x:Key="{key}">{(dark, light, high)[index]}</{kind}>')
    if not contrast:
        out.append('    <SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}" Color="Transparent" />')
    out.append("</ResourceDictionary>")
    (THEMES / f"Tokens.{theme}.xaml").write_text("\n".join(out) + "\n", encoding="utf-8")
    print(theme, "ok")
