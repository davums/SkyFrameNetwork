namespace Ledger.Dashboard;

internal static class DashboardTheme
{
    public static readonly Color Background = Color.FromArgb("#090A0C");
    public static readonly Color Surface = Color.FromArgb("#151619");
    public static readonly Color SurfaceRaised = Color.FromArgb("#202125");
    public static readonly Color Border = Color.FromArgb("#383A40");
    public static readonly Color Accent = Color.FromArgb("#FFFFFF");
    public static readonly Color Text = Color.FromArgb("#F4F4F5");
    public static readonly Color Muted = Color.FromArgb("#A1A1AA");
    public static readonly Color Good = Color.FromArgb("#34D399");
    public static readonly Color Warning = Color.FromArgb("#FBBF24");
    public static readonly Color Danger = Color.FromArgb("#FB7185");

    public static Label Label(string text, double size = 14, bool bold = false, Color? color = null) => new()
    {
        Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        TextColor = color ?? Text, VerticalTextAlignment = TextAlignment.Center
    };
    public static Button Button(string text, EventHandler action, bool primary = false) => new()
    {
        Text = text, TextColor = primary ? Background : Text,
        BackgroundColor = primary ? Accent : SurfaceRaised, CornerRadius = 10,
        Padding = new Thickness(18, 11), FontAttributes = FontAttributes.Bold,
        Command = new Command(() => action.Invoke(null, EventArgs.Empty))
    };
    public static Border Panel(View content, Thickness? padding = null) => new()
    {
        Stroke = Border, StrokeThickness = 1, BackgroundColor = Surface,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
        Padding = padding ?? new Thickness(20), Content = content
    };
    public static Entry Entry(string placeholder, string? text = null) => new()
    {
        Placeholder = placeholder, Text = text, TextColor = Text, PlaceholderColor = Muted,
        BackgroundColor = SurfaceRaised, ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
        HeightRequest = 46, Margin = new Thickness(0, 4)
    };
    public static Label Chip(string text, Color? color = null) => new()
    {
        Text = text, TextColor = color ?? Accent, FontSize = 13,
        BackgroundColor = SurfaceRaised, Padding = new Thickness(10, 6), Margin = new Thickness(3),
        HorizontalTextAlignment = TextAlignment.Center
    };
}
