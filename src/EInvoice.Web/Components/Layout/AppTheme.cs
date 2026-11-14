using MudBlazor;

namespace EInvoice.Web.Components.Layout;

/// <summary>
/// Swiss / minimal finance style: navy and slate, with green, amber and red only for meaning. Text colours meet
/// WCAG 4.5:1 on their background in both modes (the bright green #22C55E is used only on the dark background).
/// System fonts: no web-font requests to third parties (loading Google Fonts sends the visitor's IP address to Google).
/// </summary>
public static class AppTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0F172A",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#334155",
            Success = "#15803D", // 5.0:1 on white
            Warning = "#B45309", // 5.0:1 on white
            Error = "#B91C1C", // 6.5:1 on white
            Info = "#1D4ED8",
            Background = "#F8FAFC",
            Surface = "#FFFFFF",
            AppbarBackground = "#0F172A",
            AppbarText = "#F8FAFC",
            TextPrimary = "#0F172A",
            TextSecondary = "#475569", // 7.6:1 on white
            LinesDefault = "#E2E8F0",
            TableLines = "#E2E8F0",
            Divider = "#E2E8F0",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#93C5FD",
            PrimaryContrastText = "#0F172A",
            Secondary = "#CBD5E1",
            Success = "#22C55E",
            Warning = "#F59E0B",
            Error = "#F87171",
            Info = "#60A5FA",
            Background = "#020617",
            Surface = "#0E1223",
            AppbarBackground = "#0F172A",
            AppbarText = "#F8FAFC",
            TextPrimary = "#F8FAFC",
            TextSecondary = "#94A3B8", // 7.9:1 on the dark surface
            LinesDefault = "#334155",
            TableLines = "#334155",
            Divider = "#334155",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"],
            },
            Button = new ButtonTypography { TextTransform = "none" }, // sentence case, as the rest of the page
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
    };
}
