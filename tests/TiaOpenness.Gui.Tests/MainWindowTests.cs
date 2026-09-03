using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// Builds the real main window and pushes it through layout and a render pass.
///
/// This is the test the theme most needs, because the failures it catches are the ones the
/// compiler cannot: a StaticResource that does not exist throws when the XAML is parsed, a
/// resource key that resolves to the wrong type throws inside OnRender, and a template that
/// quietly binds to nothing shows up as a control that is simply not there. All three happened
/// while the theme was being written and none of them were visible before the window opened.
///
/// The window's content is re-hosted in a plain Border for rendering: a Window that has never
/// been shown has no HWND and no visual tree of its own, so it cannot be rendered directly, but
/// everything the user actually sees is under Content and renders fine.
/// </summary>
[Collection(WpfCollection.Name)]
public class MainWindowTests(WpfContext wpf)
{
    private const int Width = 1280;
    private const int Height = 860;

    private sealed class Rendered : IDisposable
    {
        public required MainWindow Window { get; init; }
        public required Border Host { get; init; }
        public required RenderTargetBitmap Bitmap { get; init; }

        public void Dispose() => Window.Close();
    }

    private static Rendered Render()
    {
        var window = new MainWindow();

        var content = (UIElement)window.Content;
        window.Content = null;

        var host = new Border
        {
            Child = content,
            DataContext = window.DataContext,
            Width = Width,
            Height = Height,
        };

        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        return new Rendered { Window = window, Host = host, Bitmap = bitmap };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static Color PixelAt(RenderTargetBitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    [Fact]
    public void Loads_lays_out_and_renders_without_throwing()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            Assert.True(rendered.Host.ActualWidth > 0);
            Assert.Equal(Width, rendered.Bitmap.PixelWidth);
        });
    }

    [Fact]
    public void Renders_in_chinese_as_well_as_english()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            using var rendered = Render();

            var labels = Descendants<TextBlock>(rendered.Host).Select(t => t.Text).ToList();
            Assert.Contains("程序块", labels);
            Assert.Contains("版本控制", labels);
        });
    }

    /// <summary>
    /// The language picker is a pair of RadioButtons bound two-way. Building the window must not
    /// push a value back into the catalogue - if it did, --lang zh would open the app in English.
    /// </summary>
    [Fact]
    public void Building_the_window_does_not_change_the_language()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            using var rendered = Render();

            Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
            Assert.True(Loc.Current.IsChinese);
        });
    }

    /// <summary>The theme painted: the title bar's first pixel row is the title-bar brush.</summary>
    [Fact]
    public void The_title_bar_is_painted_with_the_palette_brush()
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Current.Theme = AppTheme.Light;
                using var rendered = Render();

                var expected = ((SolidColorBrush)Application.Current.FindResource("Mac.TitleBarBackground")).Color;
                var actual = PixelAt(rendered.Bitmap, 300, 6);

                Assert.Equal(expected.R, actual.R);
                Assert.Equal(expected.G, actual.G);
                Assert.Equal(expected.B, actual.B);
            }
            finally
            {
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    /// <summary>
    /// Every button given an icon has to actually show one. The glyph is a Path inside the button
    /// template that collapses when the icon is null; a template that reads the icon wrongly
    /// collapses every glyph and the toolbar silently degrades to text.
    /// </summary>
    [Fact]
    public void Every_button_with_an_icon_shows_its_glyph()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            // The traffic lights also carry an icon, but through their own 7px template.
            var trafficLight = Application.Current.FindResource("Mac.TrafficLight");

            var iconButtons = Descendants<Button>(rendered.Host)
                .Where(b => Ux.GetIcon(b) is not null && b.Style != trafficLight)
                .ToList();

            Assert.True(iconButtons.Count >= 8, "expected the toolbar and browse buttons to carry icons");

            foreach (var button in iconButtons)
            {
                var label = button.Content as string ?? button.ToolTip as string ?? "(icon-only button)";
                var glyph = Descendants<Path>(button).FirstOrDefault(p => p.Name == "Glyph");

                Assert.NotNull(glyph);
                Assert.Equal(Visibility.Visible, glyph.Visibility);
                Assert.NotNull(glyph.Data);
                Assert.NotNull(glyph.Stroke);
                Assert.True(glyph.ActualWidth >= 16, label + ": glyph has no width");
            }
        });
    }

    /// <summary>And, the other way round, a text-only button must not reserve space for one.</summary>
    [Fact]
    public void A_button_without_an_icon_collapses_the_glyph()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            var plain = Descendants<Button>(rendered.Host)
                .Where(b => Ux.GetIcon(b) is null && b.Content is string)
                .ToList();

            Assert.NotEmpty(plain);

            foreach (var button in plain)
            {
                var glyph = Descendants<Path>(button).FirstOrDefault(p => p.Name == "Glyph");
                if (glyph is null) continue; // traffic lights use their own template
                Assert.Equal(Visibility.Collapsed, glyph.Visibility);
            }
        });
    }

    [Fact]
    public void The_traffic_lights_are_present_and_coloured()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            var lights = Descendants<Button>(rendered.Host)
                .Where(b => b.Style == Application.Current.FindResource("Mac.TrafficLight"))
                .ToList();

            Assert.Equal(3, lights.Count);
            Assert.All(lights, light => Assert.IsType<SolidColorBrush>(light.Background));
            Assert.Equal(3, lights.Select(l => ((SolidColorBrush)l.Background).Color).Distinct().Count());
        });
    }

    [Fact]
    public void The_tab_strip_offers_blocks_and_version_control()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            var tabs = Descendants<TabControl>(rendered.Host).Single();

            Assert.Equal(2, tabs.Items.Count);
            Assert.Equal(0, tabs.SelectedIndex);
        });
    }
}
