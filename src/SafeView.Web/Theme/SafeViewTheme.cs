using MudBlazor;

namespace SafeView.Web.Theme;

/// <summary>
/// Theme SafeView — oba warianty (dark/light) pod jednym <see cref="MudTheme"/>.
/// <see cref="MudBlazor.MudThemeProvider.IsDarkMode"/> decyduje którą paletę wybrać.
///
/// Paleta:
///  • Dark: tła czarne (#000/#0A/#11/#1A), accent żółty #FFD600, tekst biały.
///  • Light: tła jasne (#FAFAFA/#FFFFFF/#F0F0F0), accent żółty #F5B800 (ciemniejszy dla kontrastu na jasnym),
///    tekst ciemny #111111.
/// </summary>
public static class SafeViewTheme
{
    // Dark tokens
    public const string PrimaryYellow = "#FFD600";
    public const string YellowLight = "#FFE640";
    public const string BackgroundBlack = "#000000";
    public const string Surface = "#0A0A0A";
    public const string SurfaceElevated = "#111111";
    public const string SurfaceAlt = "#1A1A1A";
    public const string Border = "#FFFFFF12";
    public const string TextPrimary = "#FFFFFF";
    public const string TextSecondary = "#AAAAAA";
    public const string TextMuted = "#888888";

    // Light tokens
    public const string LightAccent = "#F5B800";        // ciemniejszy żółty — czytelniejszy na jasnym
    public const string LightBackground = "#FAFAFA";
    public const string LightSurface = "#FFFFFF";
    public const string LightSurfaceElevated = "#F5F5F5";
    public const string LightSurfaceAlt = "#F0F0F0";
    public const string LightBorder = "#00000014";
    public const string LightText = "#111111";
    public const string LightTextSecondary = "#555555";
    public const string LightTextMuted = "#888888";

    public static MudTheme Dark { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = PrimaryYellow,
            PrimaryContrastText = BackgroundBlack,
            Secondary = YellowLight,
            Tertiary = "#FFD60033",
            Black = BackgroundBlack,
            White = TextPrimary,
            Background = BackgroundBlack,
            BackgroundGray = Surface,
            Surface = Surface,
            DrawerBackground = SurfaceElevated,
            DrawerText = TextPrimary,
            DrawerIcon = PrimaryYellow,
            AppbarBackground = SurfaceElevated,
            AppbarText = TextPrimary,
            TextPrimary = TextPrimary,
            TextSecondary = TextSecondary,
            TextDisabled = TextMuted,
            ActionDefault = TextSecondary,
            ActionDisabled = "#555555",
            LinesDefault = Border,
            LinesInputs = "#FFFFFF22",
            TableLines = Border,
            TableStriped = "#FFFFFF08",
            TableHover = "#FFD60012",
            Divider = Border,
            DividerLight = "#FFFFFF08",
            Success = "#2EC27E",
            Warning = "#FFB020",
            Error = "#FF5964",
            Info = "#4DA6FF"
        },
        PaletteLight = new PaletteLight
        {
            Primary = LightAccent,
            PrimaryContrastText = "#000000",
            Secondary = "#FFD600",
            Tertiary = "#F5B80033",
            Black = "#000000",
            White = "#FFFFFF",
            Background = LightBackground,
            BackgroundGray = LightSurfaceElevated,
            Surface = LightSurface,
            DrawerBackground = LightSurface,
            DrawerText = LightText,
            DrawerIcon = LightAccent,
            AppbarBackground = LightSurface,
            AppbarText = LightText,
            TextPrimary = LightText,
            TextSecondary = LightTextSecondary,
            TextDisabled = LightTextMuted,
            ActionDefault = LightTextSecondary,
            ActionDisabled = "#BBBBBB",
            LinesDefault = LightBorder,
            LinesInputs = "#00000022",
            TableLines = LightBorder,
            TableStriped = "#00000006",
            TableHover = "#F5B80014",
            Divider = LightBorder,
            DividerLight = "#00000008",
            Success = "#1F9D58",
            Warning = "#D98B00",
            Error = "#D4333F",
            Info = "#1A73E8"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "system-ui", "-apple-system", "sans-serif"],
                FontSize = "0.9rem",
                FontWeight = "400",
                LineHeight = "1.5"
            },
            H1 = new H1Typography { FontFamily = ["Inter"], FontWeight = "800", FontSize = "2.5rem" },
            H2 = new H2Typography { FontFamily = ["Inter"], FontWeight = "700", FontSize = "2rem" },
            H3 = new H3Typography { FontFamily = ["Inter"], FontWeight = "700", FontSize = "1.5rem" },
            H4 = new H4Typography { FontFamily = ["Inter"], FontWeight = "600", FontSize = "1.25rem" },
            H5 = new H5Typography { FontFamily = ["Inter"], FontWeight = "600", FontSize = "1.1rem" },
            H6 = new H6Typography { FontFamily = ["Inter"], FontWeight = "600", FontSize = "1rem" },
            Button = new ButtonTypography { FontFamily = ["Inter"], FontWeight = "600", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = ["JetBrains Mono", "monospace"], FontSize = "0.75rem" },
            Overline = new OverlineTypography { FontFamily = ["JetBrains Mono", "monospace"], FontSize = "0.7rem", LetterSpacing = "0.1em", TextTransform = "uppercase" }
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "64px"
        }
    };
}
