using MudBlazor;

namespace PmpStudy.Web.Components.Layout;

/// <summary>
/// Thème unique de l'application : neutres zinc, un seul accent sarcelle, Geist.
/// Les mêmes valeurs sont reprises en variables CSS dans wwwroot/app.css (pages de connexion).
/// </summary>
public static class AppTheme
{
    private static readonly string[] Sans = ["Geist", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0f766e",
            PrimaryContrastText = "#ffffff",
            Secondary = "#3f3f46",
            SecondaryContrastText = "#ffffff",
            Tertiary = "#0f766e",
            Info = "#0f766e",
            Success = "#15803d",
            Warning = "#b45309",
            Error = "#b91c1c",
            Dark = "#18181b",
            Background = "#f6f6f7",
            BackgroundGray = "#efeff1",
            Surface = "#ffffff",
            AppbarBackground = "rgba(246,246,247,0.82)",
            AppbarText = "#18181b",
            DrawerBackground = "#f6f6f7",
            DrawerText = "#3f3f46",
            DrawerIcon = "#71717a",
            TextPrimary = "#18181b",
            TextSecondary = "#52525b",
            TextDisabled = "#a1a1aa",
            ActionDefault = "#52525b",
            ActionDisabled = "#a1a1aa",
            ActionDisabledBackground = "#e4e4e7",
            LinesDefault = "#e4e4e7",
            LinesInputs = "#d4d4d8",
            TableLines = "#ececef",
            TableHover = "#f4f4f5",
            Divider = "#e4e4e7",
            DividerLight = "#f0f0f2",
            GrayLight = "#e4e4e7",
            GrayLighter = "#f4f4f5",
            OverlayLight = "rgba(24,24,27,0.35)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#2dd4bf",
            PrimaryContrastText = "#042f2e",
            Secondary = "#d4d4d8",
            SecondaryContrastText = "#18181b",
            Tertiary = "#2dd4bf",
            Info = "#2dd4bf",
            Success = "#4ade80",
            Warning = "#fbbf24",
            Error = "#f87171",
            Dark = "#09090b",
            Background = "#0b0b0d",
            BackgroundGray = "#111114",
            Surface = "#141417",
            AppbarBackground = "rgba(11,11,13,0.82)",
            AppbarText = "#f4f4f5",
            DrawerBackground = "#0b0b0d",
            DrawerText = "#d4d4d8",
            DrawerIcon = "#a1a1aa",
            TextPrimary = "#f4f4f5",
            TextSecondary = "#a1a1aa",
            TextDisabled = "#52525b",
            ActionDefault = "#a1a1aa",
            ActionDisabled = "#52525b",
            ActionDisabledBackground = "#27272a",
            LinesDefault = "#27272a",
            LinesInputs = "#3f3f46",
            TableLines = "#232327",
            TableHover = "#1c1c20",
            Divider = "#27272a",
            DividerLight = "#1f1f23",
            GrayLight = "#27272a",
            GrayLighter = "#1c1c20",
            OverlayDark = "rgba(0,0,0,0.55)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, FontSize = ".9375rem", LineHeight = "1.55", LetterSpacing = "0" },
            H4 = new H4Typography { FontFamily = Sans, FontSize = "1.75rem", FontWeight = "650", LineHeight = "1.2", LetterSpacing = "-0.02em" },
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1.3rem", FontWeight = "620", LineHeight = "1.25", LetterSpacing = "-0.015em" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "1.05rem", FontWeight = "620", LineHeight = "1.3", LetterSpacing = "-0.01em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontWeight = "560" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontWeight = "600", FontSize = ".875rem" },
            Body1 = new Body1Typography { FontFamily = Sans, FontSize = ".9375rem", LineHeight = "1.6" },
            Body2 = new Body2Typography { FontFamily = Sans, FontSize = ".875rem", LineHeight = "1.55" },
            Button = new ButtonTypography { FontFamily = Sans, FontWeight = "560", TextTransform = "none", LetterSpacing = "0", FontSize = ".875rem" },
            Caption = new CaptionTypography { FontFamily = Sans, FontSize = ".78rem", LineHeight = "1.45" },
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = ".72rem", FontWeight = "600", LetterSpacing = ".06em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "248px",
            AppbarHeight = "60px",
        },
    };
}
