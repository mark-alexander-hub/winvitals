using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinVitals.App.Views;

/// <summary>
/// Small builders for the pieces of interface that are assembled in code.
///
/// The finding list is built rather than templated because each card varies: some have
/// a fix, some a command preview, some evidence, some none of those. Expressing that in
/// XAML triggers costs more than it saves.
/// </summary>
internal static class Ui
{
    public static TextBlock Text(string text, string styleKey = "Body", Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource(styleKey),
        };
        if (margin.HasValue) block.Margin = margin.Value;
        return block;
    }

    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var border = new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Child = child,
        };
        if (margin.HasValue) border.Margin = margin.Value;
        return border;
    }

    /// <summary>A coloured severity chip.</summary>
    public static Border Pill(string text, string brushKey)
    {
        return new Border
        {
            Background = Brush(brushKey + "Soft"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(brushKey),
            },
        };
    }

    public static Border CodeBox(string code)
    {
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CodeBlock"),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = code,
                Style = (Style)Application.Current.FindResource("Mono"),
            },
        };
    }

    /// <summary>A labelled paragraph: small caps label above body text.</summary>
    public static StackPanel Section(string label, string body)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Text(label.ToUpperInvariant(), "Label"));
        panel.Children.Add(Text(body, "Body", new Thickness(0, 3, 0, 0)));
        return panel;
    }

    public static Expander Details(string header)
    {
        return new Expander
        {
            Header = header,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush("Muted"),
            FontSize = 13,
        };
    }

    public static Button Button(string text, string styleKey, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(styleKey),
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// Looks up a palette brush by its plain name. The "Brush" suffix is applied here
    /// so callers use "Warning", not "WarningBrush" — see the note in Theme.Apply for
    /// why the suffix exists at all.
    /// </summary>
    public static SolidColorBrush Brush(string key) =>
        (SolidColorBrush)Application.Current.FindResource(key + "Brush");

    /// <summary>One of the counters across the top of the results page.</summary>
    public static Border Tile(int count, string label, string brushKey)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = count > 0 ? Brush(brushKey) : Brush("Muted"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush("Muted"),
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = Brush("Card"),
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 12, 12),
            Margin = new Thickness(0, 0, 8, 0),
            Child = stack,
        };
    }
}
