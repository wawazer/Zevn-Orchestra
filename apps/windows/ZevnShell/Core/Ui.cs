using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace ZevnShell;

/// <summary>Pembuat elemen UI kecil supaya halaman bisa ditulis ringkas dalam C#.</summary>
internal static class Ui
{
    public static T With<T>(this T e, Action<T> a) { a(e); return e; }

    public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, Brush? fg = null, bool wrap = false, bool mono = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, Foreground = fg ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center };
        if (weight is { } w) t.FontWeight = w;
        if (wrap) t.TextWrapping = TextWrapping.WrapWholeWords; else t.TextTrimming = TextTrimming.CharacterEllipsis;
        if (mono) t.FontFamily = Theme.MonoFont;
        return t;
    }

    public static TextBlock Title(string s, double size = 19) => Text(s, size, FontWeights.SemiBold);
    public static TextBlock Secondary(string s, double size = 12.5, bool wrap = false) => Text(s, size, fg: Theme.Text2, wrap: wrap);
    public static TextBlock Caps(string s) => new()
    {
        Text = s.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 110,
        Foreground = Theme.Text3, VerticalAlignment = VerticalAlignment.Center
    };

    public static FontIcon Icon(string glyph, double size = 14, Brush? fg = null) => new()
    {
        Glyph = glyph, FontSize = size, Foreground = fg ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center
    };

    public static StackPanel Row(double spacing, params UIElement?[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in kids) if (k != null) p.Children.Add(k);
        return p;
    }

    public static StackPanel Col(double spacing, params UIElement?[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var k in kids) if (k != null) p.Children.Add(k);
        return p;
    }

    /// <summary>Grid satu baris dengan definisi kolom, mis. "Auto,*,Auto".</summary>
    public static Grid Columns(string spec, double spacing, params UIElement?[] kids)
    {
        var g = new Grid { ColumnSpacing = spacing };
        foreach (var part in spec.Split(','))
        {
            var p = part.Trim();
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = p == "Auto" ? GridLength.Auto
                    : p.EndsWith('*') ? new GridLength(p.Length == 1 ? 1 : double.Parse(p[..^1]), GridUnitType.Star)
                    : new GridLength(double.Parse(p))
            });
        }
        for (int i = 0; i < kids.Length; i++)
        {
            if (kids[i] is not FrameworkElement k) continue;
            Grid.SetColumn(k, i);
            g.Children.Add(k);
        }
        return g;
    }

    public static Grid Rows(string spec, double spacing, params UIElement?[] kids)
    {
        var g = new Grid { RowSpacing = spacing };
        foreach (var part in spec.Split(','))
        {
            var p = part.Trim();
            g.RowDefinitions.Add(new RowDefinition
            {
                Height = p == "Auto" ? GridLength.Auto
                    : p.EndsWith('*') ? new GridLength(p.Length == 1 ? 1 : double.Parse(p[..^1]), GridUnitType.Star)
                    : new GridLength(double.Parse(p))
            });
        }
        for (int i = 0; i < kids.Length; i++)
        {
            if (kids[i] is not FrameworkElement k) continue;
            Grid.SetRow(k, i);
            g.Children.Add(k);
        }
        return g;
    }

    public static Border Card(UIElement child, double pad = 14, double radius = 10) => new()
    {
        Child = child, Padding = new Thickness(pad), CornerRadius = new CornerRadius(radius),
        Background = Theme.Card, BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1)
    };

    /// <summary>Kartu yang sedikit terangkat saat disorot.</summary>
    public static Border HoverCard(UIElement child, double pad = 12, double radius = 10)
    {
        var b = Card(child, pad, radius);
        b.PointerEntered += (_, _) => { b.Background = Theme.CardHover; b.BorderBrush = Theme.Ink(0.16); };
        b.PointerExited += (_, _) => { b.Background = Theme.Card; b.BorderBrush = Theme.CardStroke; };
        return b;
    }

    public static Border Badge(string text, string hex = Theme.Accent) => new()
    {
        Background = Theme.Brush(hex, 0.13), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(hex), CharacterSpacing = 30 }
    };

    public static Border Pill(string text, Brush? fg = null) => new()
    {
        Background = Theme.Ink(0.06), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10.5, FontFamily = Theme.MonoFont, Foreground = fg ?? Theme.Text3 }
    };

    private static UIElement Label(string text, string? glyph, Brush fg)
    {
        if (glyph == null) return new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeights.SemiBold };
        if (string.IsNullOrEmpty(text)) return Icon(glyph, 13, fg);
        return Row(7, Icon(glyph, 13, fg), new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeights.SemiBold });
    }

    /// <summary>Tombol utama: gradien brand, teks gelap.</summary>
    public static Button Primary(string text, string? glyph, Action click, bool large = false)
    {
        var fg = new SolidColorBrush(Color.FromArgb(220, 6, 18, 16));
        var b = new Button
        {
            Content = Label(text, glyph, fg), Background = Theme.BrandBrush, Foreground = fg, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(large ? 9 : 7), Padding = large ? new Thickness(16, 9, 16, 9) : new Thickness(11, 5, 11, 6),
            FontWeight = FontWeights.SemiBold
        };
        var hover = Theme.BrandBrush;
        hover.Opacity = 0.88;
        b.Resources["ButtonBackgroundPointerOver"] = hover;
        b.Resources["ButtonBackgroundPressed"] = Theme.BrandBrush;
        b.Resources["ButtonForegroundPointerOver"] = fg;
        b.Resources["ButtonForegroundPressed"] = fg;
        b.Resources["ButtonBackgroundDisabled"] = Theme.BrandBrush;
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Secondary(string text, string? glyph, Action click, Brush? fg = null)
    {
        var f = fg ?? Theme.Text;
        var b = new Button
        {
            Content = Label(text, glyph, f), Background = Theme.Ink(0.05), BorderBrush = Theme.Ink(0.08), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 5, 11, 6), Foreground = f
        };
        b.Resources["ButtonBackgroundPointerOver"] = Theme.Ink(0.12);
        b.Resources["ButtonBackgroundPressed"] = Theme.Ink(0.16);
        b.Resources["ButtonForegroundPointerOver"] = f;
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconBtn(string glyph, string tip, Action click, Brush? fg = null, double size = 13)
    {
        var b = new Button
        {
            Content = Icon(glyph, size, fg ?? Theme.Text2), Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0), Padding = new Thickness(6), CornerRadius = new CornerRadius(6), MinWidth = 0, MinHeight = 0
        };
        b.Resources["ButtonBackgroundPointerOver"] = Theme.Ink(0.08);
        b.Resources["ButtonBackgroundPressed"] = Theme.Ink(0.12);
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Tombol datar transparan berisi elemen bebas.</summary>
    public static Button Plain(UIElement content, Action click, Thickness? pad = null)
    {
        var b = new Button
        {
            Content = content, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = pad ?? new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MinWidth = 0, MinHeight = 0
        };
        b.Resources["ButtonBackgroundPointerOver"] = Theme.Ink(0.06);
        b.Resources["ButtonBackgroundPressed"] = Theme.Ink(0.1);
        b.Click += (_, _) => click();
        return b;
    }

    public static TextBox Field(string placeholder, string text = "", bool mono = false, double? width = null)
    {
        var t = new TextBox { PlaceholderText = placeholder, Text = text };
        if (mono) t.FontFamily = Theme.MonoFont;
        if (width is { } w) t.Width = w;
        return t;
    }

    public static NumberBox Number(double value, double min = 0, double max = 65535, double width = 110) => new()
    {
        Value = value, Minimum = min, Maximum = max, Width = width, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsGrouped = false }
    };

    public static AutoSuggestBox Search(string placeholder, string text, Action<string> changed, double width = 220)
    {
        var s = new AutoSuggestBox { PlaceholderText = placeholder, Text = text, Width = width, QueryIcon = new SymbolIcon(Symbol.Find) };
        s.TextChanged += (_, a) => { if (a.Reason == AutoSuggestionBoxTextChangeReason.UserInput) changed(s.Text); };
        return s;
    }

    /// <summary>Header halaman: judul + subjudul di kiri, kontrol di kanan.</summary>
    public static Grid Header(string title, string subtitle, params UIElement?[] trailing)
    {
        var left = Col(2, Title(title), Secondary(subtitle).With(t => t.TextWrapping = TextWrapping.WrapWholeWords));
        var right = Row(7, trailing);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        return Columns("*,Auto", 16, left, right);
    }

    public static UIElement EmptyState(string glyph, string title, string text, UIElement? action = null)
    {
        var icon = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Background = Theme.Ink(0.04), BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 22, Foreground = Theme.Text2 }
        };
        var p = Col(9, icon, Text(title, 15, FontWeights.SemiBold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Secondary(text, 12.5, true).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.MaxWidth = 340; }));
        if (action is FrameworkElement a) { a.HorizontalAlignment = HorizontalAlignment.Center; a.Margin = new Thickness(0, 8, 0, 0); p.Children.Add(a); }
        p.Margin = new Thickness(0, 72, 0, 0);
        p.HorizontalAlignment = HorizontalAlignment.Center;
        Float(icon);
        return p;
    }

    /// <summary>Ikon berkotak gradien (seperti ikon menu sidebar).</summary>
    public static Border TintIcon(string glyph, string hex, double size = 24, double glyphSize = 12, double radius = 7) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Theme.TintGradient(hex),
        Child = new FontIcon { Glyph = glyph, FontSize = glyphSize, Foreground = new SolidColorBrush(Colors.White) }
    };

    public static Border Avatar(string? name, string? email, double size)
    {
        var s = (name ?? email ?? "?").Trim();
        var initial = s.Length > 0 ? s[..1].ToUpperInvariant() : "?";
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = email == null ? Theme.Ink(0.1) : Theme.BrandBrush,
            Child = email == null
                ? new FontIcon { Glyph = G.Person, FontSize = size * 0.45, Foreground = Theme.Text2 }
                : new TextBlock { Text = initial, FontSize = size * 0.42, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    public static ScrollViewer Scroll(UIElement content, double pad = 20) => new()
    {
        Content = new Border { Child = content, Padding = new Thickness(pad) },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled
    };

    public static Border Divider() => new() { Height = 1, Background = Theme.Ink(0.07) };

    /// <summary>Saklar segmen (mis. Password / SSH Key / Agent).</summary>
    public static Grid Segmented(string[] labels, int selected, Action<int> changed)
    {
        var g = new Grid { Background = Theme.Ink(0.05), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), ColumnSpacing = 2 };
        for (int i = 0; i < labels.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var idx = i;
            var on = i == selected;
            var b = new Button
            {
                Content = new TextBlock { Text = labels[i], FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Theme.Text : Theme.Text2 },
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(6),
                Background = on ? (Theme.Dark ? Theme.Ink(0.12) : new SolidColorBrush(Colors.White)) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            b.Resources["ButtonBackgroundPointerOver"] = on ? b.Background : Theme.Ink(0.05);
            b.Click += (_, _) => changed(idx);
            Grid.SetColumn(b, i);
            g.Children.Add(b);
        }
        return g;
    }

    /// <summary>Saklar segmen berisi ikon saja (mis. tampilan grid / daftar).</summary>
    public static Grid SegmentedIcons((string glyph, string tip)[] items, int selected, Action<int> changed)
    {
        var g = new Grid { Background = Theme.Ink(0.05), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), ColumnSpacing = 2 };
        for (int i = 0; i < items.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var idx = i;
            var on = i == selected;
            var b = new Button
            {
                Content = Icon(items[i].glyph, 13, on ? Theme.Text : Theme.Text3),
                Padding = new Thickness(0), Height = 26, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0),
                Background = on ? Theme.Ink(0.12) : new SolidColorBrush(Colors.Transparent), MinWidth = 0, MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center
            };
            b.Resources["ButtonBackgroundPointerOver"] = on ? b.Background : Theme.Ink(0.05);
            ToolTipService.SetToolTip(b, items[i].tip);
            b.Click += (_, _) => changed(idx);
            Grid.SetColumn(b, i);
            g.Children.Add(b);
        }
        return g;
    }

    public static XamlRoot Root => App.Window!.Content.XamlRoot;

    public static async Task<bool> Confirm(string title, string message, string primary = "Hapus", bool destructive = true)
    {
        var d = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = primary, CloseButtonText = "Batal", DefaultButton = ContentDialogButton.Close, XamlRoot = Root,
            RequestedTheme = App.Model.IsDark ? ElementTheme.Dark : ElementTheme.Light
        };
        if (destructive) d.PrimaryButtonStyle = DangerStyle();
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static Style DangerStyle()
    {
        var s = new Style(typeof(Button));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush("#E5484D")));
        s.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)));
        return s;
    }

    public static ContentDialog Dialog(string title, UIElement content, string? primary, string close = "Batal") => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary ?? "", CloseButtonText = close,
        DefaultButton = primary != null ? ContentDialogButton.Primary : ContentDialogButton.Close, XamlRoot = Root,
        RequestedTheme = App.Model.IsDark ? ElementTheme.Dark : ElementTheme.Light
    };

    public static void CopyText(string s)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(s);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    public static async Task<string?> PasteText()
    {
        var c = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!c.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return null;
        try { return await c.GetTextAsync(); } catch { return null; }
    }

    public static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // MARK: Animasi

    /// <summary>Muncul berurutan: fade + naik sedikit.</summary>
    public static void StaggerIn(UIElement e, int index, double baseDelay = 0)
    {
        var v = ElementCompositionPreview.GetElementVisual(e);
        var c = v.Compositor;
        var delay = TimeSpan.FromMilliseconds(Math.Min(index, 14) * 28 + baseDelay * 1000);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(380); fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var rise = c.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 10, 0)); rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = TimeSpan.FromMilliseconds(460); rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        v.StartAnimation("Opacity", fade);
        v.StartAnimation("Translation", rise);
    }

    /// <summary>Melayang naik-turun pelan (ikon empty state).</summary>
    public static void Float(UIElement e)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var v = ElementCompositionPreview.GetElementVisual(e);
        var a = v.Compositor.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, new Vector3(0, -5, 0)); a.InsertKeyFrame(0.5f, new Vector3(0, 5, 0)); a.InsertKeyFrame(1, new Vector3(0, -5, 0));
        a.Duration = TimeSpan.FromSeconds(4); a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Translation", a);
    }

    /// <summary>Kartu terangkat & membesar sedikit saat hover.</summary>
    public static void HoverLift(UIElement e, float lift = -2)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var v = ElementCompositionPreview.GetElementVisual(e);
        void To(float y)
        {
            var a = v.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1, new Vector3(0, y, 0));
            a.Duration = TimeSpan.FromMilliseconds(160);
            v.StartAnimation("Translation", a);
        }
        e.PointerEntered += (_, _) => To(lift);
        e.PointerExited += (_, _) => To(0);
    }

    /// <summary>Denyut (titik status "menghubungkan").</summary>
    public static void Pulse(UIElement e)
    {
        var v = ElementCompositionPreview.GetElementVisual(e);
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 1); a.InsertKeyFrame(0.5f, 0.3f); a.InsertKeyFrame(1, 1);
        a.Duration = TimeSpan.FromMilliseconds(1100); a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }

    public static void Spin(UIElement e)
    {
        if (e is FrameworkElement f) f.SizeChanged += (_, _) => ElementCompositionPreview.GetElementVisual(e).CenterPoint = new Vector3((float)f.ActualWidth / 2, (float)f.ActualHeight / 2, 0);
        var v = ElementCompositionPreview.GetElementVisual(e);
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 0); a.InsertKeyFrame(1, 360, v.Compositor.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromSeconds(1); a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("RotationAngleInDegrees", a);
    }

    public static string Bytes(ulong n)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = n; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{n} B" : $"{v:0.#} {u[i]}";
    }

    public static string Relative(DateTimeOffset d)
    {
        var s = DateTimeOffset.Now - d;
        if (s.TotalSeconds < 60) return "baru saja";
        if (s.TotalMinutes < 60) return $"{(int)s.TotalMinutes} mnt lalu";
        if (s.TotalHours < 24) return $"{(int)s.TotalHours} jam lalu";
        if (s.TotalDays < 30) return $"{(int)s.TotalDays} hari lalu";
        return d.LocalDateTime.ToString("d MMM yyyy", new System.Globalization.CultureInfo("id-ID"));
    }

    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
