using System.Windows;
using System.Windows.Media;

namespace TiaOpenness.Gui.Controls;

/// <summary>
/// Attached properties the themed templates read.
///
/// These exist so the whole UI can stay built from stock WPF controls: a toolbar button is a
/// <see cref="System.Windows.Controls.Button"/> that happens to carry an <see cref="IconProperty"/>,
/// not a bespoke control with its own bugs. Subclassing would also have cost every one of these
/// the default styling, keyboard handling and automation peers that come for free.
/// </summary>
public static class Ux
{
    /// <summary>Grey prompt shown inside an empty text field, as macOS does.</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(Ux), new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>Leading glyph for a button. Null leaves the button text-only.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon", typeof(Geometry), typeof(Ux), new PropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    /// <summary>Per-instance corner rounding, for the ends of a segmented control.</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Ux), new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);
}
